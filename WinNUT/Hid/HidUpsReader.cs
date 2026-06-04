using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;
using Microsoft.Extensions.Logging;

namespace WinNUT.Hid;

// Reads UPS state from a USB HID Power Device (Usage Page 0x84 / 0x85).
// HID UPS spec: https://www.usb.org/sites/default/files/pdcv10.pdf
public sealed class HidUpsReader : IUpsReader
{
    // HID usage page encoding: (page << 16) | usageId
    private const uint UsagePagePowerDevice   = 0x0084;
    private const uint UsagePageBatterySystem = 0x0085;

    // Battery System (page 0x85) usages
    private const uint UsageRemainingCapacity = (0x85u << 16) | 0x66;
    private const uint UsageRunTimeToEmpty    = (0x85u << 16) | 0x68;
    private const uint UsageBatteryVoltage    = (0x85u << 16) | 0x30;
    private const uint UsageConfigVoltage     = (0x85u << 16) | 0x40;
    private const uint UsagePresentStatus     = (0x85u << 16) | 0xD0;

    // Power Device (page 0x84) usages
    private const uint UsageInputVoltage      = (0x84u << 16) | 0x30;
    private const uint UsageNominalVoltage    = (0x84u << 16) | 0x40;
    private const uint UsageACPresent         = (0x84u << 16) | 0xD1;
    private const uint UsageCharging          = (0x84u << 16) | 0xD2;
    private const uint UsageDischarging       = (0x84u << 16) | 0xD3;
    private const uint UsageNeedReplacement   = (0x84u << 16) | 0xDB;

    private readonly ILogger<HidUpsReader> _logger;
    private HidDevice? _device;
    private HidStream? _stream;
    private ReportDescriptor? _descriptor;
    private DeviceItem[]? _deviceItems;
    private HidDeviceInputReceiver? _inputReceiver;
    private UpsState _lastState = new();

    public bool IsConnected => _stream != null;

    public HidUpsReader(ILogger<HidUpsReader> logger)
    {
        _logger = logger;
        TryConnect();
    }

    private void TryConnect()
    {
        try
        {
            _device = FindUpsDevice();
            if (_device == null)
            {
                _logger.LogWarning("No HID UPS device found");
                return;
            }

            _logger.LogInformation("Found UPS device: {Name}", _device.GetFriendlyName());

            _stream = _device.Open();
            _stream.ReadTimeout = 200;

            _descriptor = _device.GetReportDescriptor();
            _deviceItems = _descriptor.DeviceItems.ToArray();
            _inputReceiver = _descriptor.CreateHidDeviceInputReceiver();
            _inputReceiver.Start(_stream);

            // Populate static fields (manufacturer, model, serial) from string descriptors
            _lastState.Manufacturer = TryGetString(_device, 1);
            _lastState.Model        = TryGetString(_device, 2);
            _lastState.Serial       = TryGetString(_device, 3);
            _lastState.VendorId     = _device.VendorID;
            _lastState.ProductId    = _device.ProductID;

            // Read nominal voltages from feature reports once at startup
            ReadNominalValues();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to HID UPS device");
            _stream?.Dispose();
            _stream = null;
        }
    }

    public UpsState Read()
    {
        if (_stream == null || _inputReceiver == null || _deviceItems == null)
        {
            TryConnect();
            return _lastState;
        }

        try
        {
            DataValue value;
            while (_inputReceiver.TryRead(_deviceItems, 0, out value))
            {
                ApplyDataValue(value);
            }

            _lastState.LastUpdated = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HID read error — attempting reconnect");
            _stream?.Dispose();
            _stream = null;
            TryConnect();
        }

        return _lastState;
    }

    private void ApplyDataValue(DataValue value)
    {
        foreach (uint usage in value.Usages.Select(u => (uint)u))
        {
            switch (usage)
            {
                case UsageRemainingCapacity:
                    _lastState.BatteryCharge = value.GetPhysicalValue();
                    break;

                case UsageRunTimeToEmpty:
                    _lastState.RuntimeSeconds = (int)value.GetPhysicalValue();
                    break;

                case UsageBatteryVoltage:
                    _lastState.BatteryVoltage = value.GetPhysicalValue();
                    break;

                case UsageInputVoltage:
                    _lastState.InputVoltage = value.GetPhysicalValue();
                    _lastState.OutputVoltage = value.GetPhysicalValue();
                    break;

                case UsageACPresent:
                    UpdateStatus(UpsStatus.OnLine, value.GetLogicalValue() != 0);
                    UpdateStatus(UpsStatus.OnBattery, value.GetLogicalValue() == 0);
                    break;

                case UsageCharging:
                    UpdateStatus(UpsStatus.Charging, value.GetLogicalValue() != 0);
                    break;

                case UsageDischarging:
                    UpdateStatus(UpsStatus.Discharging, value.GetLogicalValue() != 0);
                    break;

                case UsageNeedReplacement:
                    UpdateStatus(UpsStatus.ReplaceBattery, value.GetLogicalValue() != 0);
                    break;
            }
        }

        // Derive LowBattery from charge threshold
        if (_lastState.BatteryCharge <= 10)
            _lastState.Status |= UpsStatus.LowBattery;
        else
            _lastState.Status &= ~UpsStatus.LowBattery;
    }

    private void UpdateStatus(UpsStatus flag, bool set)
    {
        if (set) _lastState.Status |= flag;
        else _lastState.Status &= ~flag;
    }

    private void ReadNominalValues()
    {
        // TODO: Read feature reports for nominal voltage values (battery.voltage.nominal,
        // input.voltage.nominal). Feature reports hold static config; the correct report IDs
        // and value offsets are device-specific and must be mapped after live testing against
        // a physical UPS using a HID report descriptor dump (e.g. hidapitester --list-detail).
    }

    private static HidDevice? FindUpsDevice()
    {
        // UPS devices declare Usage Page 0x84 (Power Device) as their top-level usage
        return DeviceList.Local
            .GetHidDevices()
            .FirstOrDefault(d =>
            {
                try
                {
                    var descriptor = d.GetReportDescriptor();
                    return descriptor.DeviceItems.Any(item =>
                        item.Usages.GetAllValues().Any(u => (u >> 16) == UsagePagePowerDevice ||
                                                            (u >> 16) == UsagePageBatterySystem));
                }
                catch { return false; }
            });
    }

    private static string TryGetString(HidDevice device, int index)
    {
        try
        {
            return index switch
            {
                1 => device.GetManufacturer(),
                2 => device.GetProductName(),
                _ => ""
            };
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        _inputReceiver = null;
        _stream?.Dispose();
    }
}

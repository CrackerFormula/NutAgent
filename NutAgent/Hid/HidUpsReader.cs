using System.Globalization;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Reports.Input;
using Microsoft.Extensions.Logging;

namespace NutAgent.Hid;

// Reads UPS state from a USB HID Power Device (Usage Page 0x84 / 0x85).
// HID UPS spec: https://www.usb.org/sites/default/files/pdcv10.pdf
public sealed class HidUpsReader : IUpsReader
{
    // -------------------------------------------------------------------------
    // HID usage → NUT variable mapping
    // -------------------------------------------------------------------------

    private sealed record NumericUsage(string NutVar, string Format);

    // Numeric usages: reading goes into Variables[NutVar], formatted with Format.
    // Input voltage (0x84/0x30) and output voltage share the same usage — the
    // distinction requires checking the parent collection, which needs live
    // hardware testing. 0x84/0x30 is mapped to input.voltage initially; if the
    // device also reports output.voltage separately it will arrive via 0x84/0x30
    // in a different collection and overwrite this entry. Revisit after testing.
    private static readonly Dictionary<uint, NumericUsage> NumericUsages = new()
    {
        // Battery System (page 0x85)
        [(0x85u << 16) | 0x66] = new("battery.charge",          "F0"),
        [(0x85u << 16) | 0x68] = new("battery.runtime",         "F0"),
        [(0x85u << 16) | 0x30] = new("battery.voltage",         "F2"),
        [(0x85u << 16) | 0x40] = new("battery.voltage.nominal", "F1"),
        [(0x85u << 16) | 0x36] = new("battery.temperature",     "F1"),
        [(0x85u << 16) | 0x67] = new("battery.charge.full",     "F0"),
        [(0x85u << 16) | 0x83] = new("battery.capacity",        "F0"),
        [(0x85u << 16) | 0x8F] = new("battery.cyclecount",      "F0"),

        // Power Device (page 0x84) — numeric
        [(0x84u << 16) | 0x30] = new("input.voltage",           "F1"),
        [(0x84u << 16) | 0x31] = new("input.current",           "F2"),
        [(0x84u << 16) | 0x32] = new("input.frequency",         "F1"),
        [(0x84u << 16) | 0x33] = new("ups.power",               "F0"),
        [(0x84u << 16) | 0x34] = new("ups.realpower",           "F0"),
        [(0x84u << 16) | 0x35] = new("ups.load",                "F0"),
        [(0x84u << 16) | 0x36] = new("ups.temperature",         "F1"),
        [(0x84u << 16) | 0x40] = new("input.voltage.nominal",   "F1"),
        [(0x84u << 16) | 0x42] = new("input.frequency.nominal", "F1"),
        [(0x84u << 16) | 0x53] = new("input.transfer.low",      "F1"),
        [(0x84u << 16) | 0x54] = new("input.transfer.high",     "F1"),
    };

    // Boolean usages: set or clear the corresponding UpsStatus flag.
    // ACPresent (0xD1) is handled separately because it drives two flags.
    // Page 0x85 (Battery System) variants included for devices like Tripp Lite
    // that report status on page 0x85 instead of the standard page 0x84.
    private static readonly Dictionary<uint, UpsStatus> StatusUsages = new()
    {
        [(0x84u << 16) | 0xD2] = UpsStatus.Charging,
        [(0x84u << 16) | 0xD3] = UpsStatus.Discharging,
        [(0x84u << 16) | 0xD4] = UpsStatus.ShutdownImminent,
        [(0x84u << 16) | 0xD8] = UpsStatus.Boosting,
        [(0x84u << 16) | 0xD9] = UpsStatus.Trimming,
        [(0x84u << 16) | 0xDB] = UpsStatus.ReplaceBattery,

        [(0x85u << 16) | 0x44] = UpsStatus.Charging,
        [(0x85u << 16) | 0x45] = UpsStatus.Discharging,
        [(0x85u << 16) | 0x42] = UpsStatus.LowBattery,
        [(0x85u << 16) | 0x47] = UpsStatus.LowBattery,
        [(0x85u << 16) | 0x4B] = UpsStatus.ReplaceBattery,
    };

    // 0x84/0xD1 = ACPresent (HID Power Device spec). 0x84/0x62 = "Volt" — NOT ACPresent, removed.
    private const uint AcPresentUsage = (0x84u << 16) | 0xD1;

    // -------------------------------------------------------------------------

    private readonly ILogger<HidUpsReader> _logger;
    private bool                     _acPresentReceived;
    private int                      _pollCount;
    private HidDevice?               _device;
    private HidStream?               _stream;
    private ReportDescriptor?        _descriptor;
    private DeviceItem[]?            _deviceItems;
    private HidDeviceInputReceiver?  _inputReceiver;
    private byte[]?                  _inputReportBuffer;
    private byte[]?                  _featureReportBuffer;
    private DeviceItemInputParser[]? _parsers;
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

            _descriptor  = _device.GetReportDescriptor();
            _deviceItems = _descriptor.DeviceItems.ToArray();
            _inputReceiver       = _descriptor.CreateHidDeviceInputReceiver();
            _inputReportBuffer   = new byte[_device.GetMaxInputReportLength()];
            _featureReportBuffer = new byte[_device.GetMaxFeatureReportLength()];
            _parsers             = _deviceItems.Select(item => item.CreateDeviceItemInputParser()).ToArray();
            _inputReceiver.Start(_stream);

            // Identity from USB string descriptors
            _lastState.Manufacturer = TryGetString(_device, 1);
            _lastState.Model        = TryGetString(_device, 2);
            _lastState.Serial       = TryGetString(_device, 3);
            _lastState.VendorId     = _device.VendorID;
            _lastState.ProductId    = _device.ProductID;

            // Publish identity into Variables so NUT exposes them immediately
            _lastState.Variables["device.mfr"]     = _lastState.Manufacturer;
            _lastState.Variables["device.model"]   = _lastState.Model;
            _lastState.Variables["device.serial"]  = _lastState.Serial;
            _lastState.Variables["ups.mfr"]        = _lastState.Manufacturer;
            _lastState.Variables["ups.model"]      = _lastState.Model;
            _lastState.Variables["ups.serial"]     = _lastState.Serial;
            _lastState.Variables["ups.vendorid"]   = _lastState.VendorId.ToString("X4").ToLower();
            _lastState.Variables["ups.productid"]  = _lastState.ProductId.ToString("X4").ToLower();

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
        if (_stream == null || _inputReceiver == null || _deviceItems == null || _parsers == null || _inputReportBuffer == null)
        {
            TryConnect();
            return _lastState.Clone();
        }

        try
        {
            Report report;
            while (_inputReceiver.TryRead(_inputReportBuffer, 0, out report))
            {
                for (int i = 0; i < _parsers.Length; i++)
                {
                    if (!_parsers[i].TryParseReport(_inputReportBuffer, 0, report)) continue;
                    for (int j = 0; j < _parsers[i].ValueCount; j++)
                        ApplyDataValue(_parsers[i].GetValue(j));
                }
            }

            // Poll feature reports every read cycle — authoritative status source for devices
            // (e.g. Tripp Lite) that don't send spontaneous input reports on state changes.
            ++_pollCount;
            ReadNominalValues();

            // Keep ups.status in Variables current after every read
            _lastState.Variables["ups.status"] = _lastState.Status.ToNutString();
            _lastState.LastUpdated = DateTime.UtcNow;
            if (_pollCount <= 5)
                _logger.LogInformation("Startup poll {Poll}: status={Status} acPresentReceived={AcPresent} charge={Charge} runtime={Runtime}", _pollCount, _lastState.Status, _acPresentReceived, _lastState.BatteryCharge, _lastState.RuntimeSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HID read error — attempting reconnect");
            _stream?.Dispose();
            _stream = null;
            _parsers = null;
            _inputReportBuffer = null;
            _acPresentReceived = false;
            TryConnect();
        }

        return _lastState.Clone();
    }

    private void ApplyDataValue(DataValue value)
    {
        foreach (uint usage in value.Usages.Select(u => (uint)u))
        {
            if (NumericUsages.TryGetValue(usage, out var numMap))
            {
                var physical  = value.GetPhysicalValue();
                var formatted = physical.ToString(numMap.Format, CultureInfo.InvariantCulture);
                _lastState.Variables[numMap.NutVar] = formatted;

                // Keep typed fields in sync for shutdown logic
                if (numMap.NutVar == "battery.charge")
                    _lastState.BatteryCharge = physical;
                else if (numMap.NutVar == "battery.runtime")
                    _lastState.RuntimeSeconds = (int)physical;
            }
            else if (usage == AcPresentUsage)
            {
                bool firstTime = !_acPresentReceived;
                _acPresentReceived = true;
                bool acPresent = value.GetLogicalValue() != 0;
                if (firstTime)
                    _logger.LogInformation("ACPresent usage 0x{Usage:X8} logical={Logical} acPresent={AcPresent}", usage, value.GetLogicalValue(), acPresent);
                _lastState.Status = acPresent
                    ? (_lastState.Status | UpsStatus.OnLine)    & ~UpsStatus.OnBattery
                    : (_lastState.Status | UpsStatus.OnBattery) & ~UpsStatus.OnLine;
            }
            else if (StatusUsages.TryGetValue(usage, out var flag))
            {
                int logical = value.GetLogicalValue();
                if (logical != 0)
                    _lastState.Status |= flag;
                else
                    _lastState.Status &= ~flag;
                if (_pollCount <= 5)
                    _logger.LogInformation("HID status usage 0x{Usage:X8} flag={Flag} logical={Logical}", usage, flag, logical);
            }
        }

        // Derive OnLine/OnBattery from Charging/Discharging flags when no explicit ACPresent
        // usage is received. For the Tripp Lite (and similar devices), feature reports reliably
        // report Charging=1 on AC and Discharging=1 on battery — so these flags are trustworthy.
        // Charging takes priority over Discharging to handle AC-restore transitions.
        if (!_acPresentReceived)
        {
            if (_lastState.Status.HasFlag(UpsStatus.Charging))
                _lastState.Status = (_lastState.Status | UpsStatus.OnLine) & ~UpsStatus.OnBattery & ~UpsStatus.Discharging;
            else if (_lastState.Status.HasFlag(UpsStatus.Discharging))
                _lastState.Status = (_lastState.Status | UpsStatus.OnBattery) & ~UpsStatus.OnLine;
            else if (!_lastState.Status.HasFlag(UpsStatus.OnBattery))
                _lastState.Status = (_lastState.Status | UpsStatus.OnLine) & ~UpsStatus.OnBattery;
        }

    }

    private void ReadNominalValues()
    {
        // Seed values from feature reports — many UPS devices only send input reports
        // when values change, so battery.charge / battery.runtime arrive only on first
        // change after connect. Reading feature reports on startup gives immediate values.
        if (_stream == null || _deviceItems == null || _featureReportBuffer == null) return;

        foreach (var deviceItem in _deviceItems)
        {
            foreach (var report in deviceItem.FeatureReports)
            {
                Array.Clear(_featureReportBuffer, 0, _featureReportBuffer.Length);
                _featureReportBuffer[0] = report.ReportID;
                try { _stream.GetFeature(_featureReportBuffer); }
                catch { continue; }

                // Feature reports are the authoritative status source for devices (like Tripp Lite)
                // that don't push spontaneous input reports on state changes.
                try { report.Read(_featureReportBuffer, 0, (DataValue value) => ApplyDataValue(value)); }
                catch { }
            }
        }
    }

    private static HidDevice? FindUpsDevice()
    {
        return DeviceList.Local
            .GetHidDevices()
            .FirstOrDefault(d =>
            {
                try
                {
                    var descriptor = d.GetReportDescriptor();
                    return descriptor.DeviceItems.Any(item =>
                        item.Usages.GetAllValues().Any(u =>
                            (u >> 16) == 0x84 || (u >> 16) == 0x85));
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
                3 => device.GetSerialNumber(),
                _ => ""
            };
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        _inputReceiver       = null;
        _parsers             = null;
        _inputReportBuffer   = null;
        _featureReportBuffer = null;
        _stream?.Dispose();
    }
}

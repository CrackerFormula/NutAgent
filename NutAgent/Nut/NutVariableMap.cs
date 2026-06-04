using NutAgent.Hid;

namespace NutAgent.Nut;

// Merges dynamically discovered HID variables with config-derived and static fields
// to produce the full NUT variable set exposed to clients.
public static class NutVariableMap
{
    private const string DriverName    = "NutAgent";
    private const string DriverVersion = "1.0.0";

    public static IReadOnlyDictionary<string, string> Build(UpsState state, int shutdownBatteryThreshold = 20)
    {
        // Start with everything the HID reader discovered
        var vars = new Dictionary<string, string>(state.Variables);

        // Typed fields are always authoritative — override whatever HID may have put in Variables
        vars["ups.status"]      = state.Status.ToNutString();
        vars["battery.charge"]  = state.BatteryCharge.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        vars["battery.runtime"] = state.RuntimeSeconds.ToString();

        // Config-derived thresholds — override whatever HID may have reported
        vars["battery.charge.low"]     = shutdownBatteryThreshold.ToString();
        vars["battery.charge.warning"] = (shutdownBatteryThreshold + 10).ToString();

        // Static fields
        vars["battery.type"]   = vars.GetValueOrDefault("battery.type", "PbAc");
        vars["device.type"]    = "ups";
        vars["driver.name"]    = DriverName;
        vars["driver.version"] = DriverVersion;

        return vars;
    }

    public static string GetType(string varName) => varName switch
    {
        "battery.charge"          or
        "battery.charge.low"      or
        "battery.charge.warning"  or
        "battery.charge.full"     or
        "battery.cyclecount"      or
        "battery.runtime"         or
        "ups.load"                or
        "ups.power"               or
        "ups.realpower"           => "INTEGER",
        _                         => "STRING:256"
    };

    public static string GetDescription(string varName) => varName switch
    {
        "battery.charge"          => "Battery charge (percent of full)",
        "battery.charge.low"      => "Charge level triggering shutdown",
        "battery.charge.warning"  => "Charge level triggering warning",
        "battery.charge.full"     => "Battery full charge capacity",
        "battery.cyclecount"      => "Battery cycle count",
        "battery.runtime"         => "Estimated runtime on battery (seconds)",
        "battery.temperature"     => "Battery temperature (degrees C)",
        "battery.type"            => "Battery chemistry",
        "battery.voltage"         => "Battery voltage",
        "battery.voltage.nominal" => "Nominal battery voltage",
        "battery.capacity"        => "Battery design capacity",
        "device.mfr"              => "Device manufacturer",
        "device.model"            => "Device model",
        "device.serial"           => "Device serial number",
        "device.type"             => "Device type",
        "driver.name"             => "Driver name",
        "driver.version"          => "Driver version",
        "input.current"           => "Input current (A)",
        "input.frequency"         => "Input line frequency (Hz)",
        "input.frequency.nominal" => "Nominal input frequency (Hz)",
        "input.transfer.high"     => "High voltage transfer point (V)",
        "input.transfer.low"      => "Low voltage transfer point (V)",
        "input.voltage"           => "Input voltage (V)",
        "input.voltage.nominal"   => "Nominal input voltage (V)",
        "ups.load"                => "Output load (percent of full)",
        "ups.mfr"                 => "UPS manufacturer",
        "ups.model"               => "UPS model",
        "ups.power"               => "Apparent output power (VA)",
        "ups.productid"           => "USB product ID",
        "ups.realpower"           => "Active output power (W)",
        "ups.serial"              => "UPS serial number",
        "ups.status"              => "UPS status flags",
        "ups.temperature"         => "UPS temperature (degrees C)",
        "ups.vendorid"            => "USB vendor ID",
        _                         => ""
    };
}

using NutAgent.Hid;

namespace NutAgent.Nut;

// Maps UpsState to NUT variable names and values.
// Variable names follow the NUT naming convention:
// https://networkupstools.org/docs/developer-guide.chunked/apas01.html
public static class NutVariableMap
{
    private const string DriverName    = "NutAgent";
    private const string DriverVersion = "1.0.0";

    public static IReadOnlyDictionary<string, string> Build(UpsState state, string upsDescription, int shutdownBatteryThreshold = 20)
    {
        var vars = new Dictionary<string, string>
        {
            ["battery.charge"]            = Fmt(state.BatteryCharge, 0),
            ["battery.charge.low"]        = shutdownBatteryThreshold.ToString(),
            ["battery.charge.warning"]    = (shutdownBatteryThreshold + 10).ToString(),
            ["battery.runtime"]           = state.RuntimeSeconds.ToString(),
            ["battery.type"]              = "PbAc",
            ["battery.voltage"]           = Fmt(state.BatteryVoltage, 2),
            ["battery.voltage.nominal"]   = Fmt(state.BatteryVoltageNominal, 1),

            ["device.mfr"]                = state.Manufacturer,
            ["device.model"]              = state.Model,
            ["device.serial"]             = state.Serial,
            ["device.type"]               = "ups",

            ["driver.name"]               = DriverName,
            ["driver.version"]            = DriverVersion,

            ["input.voltage"]             = Fmt(state.InputVoltage, 1),
            ["input.voltage.nominal"]     = Fmt(state.InputVoltageNominal, 1),

            ["output.voltage"]            = Fmt(state.OutputVoltage, 1),

            ["ups.load"]                  = Fmt(state.Load, 0),
            ["ups.mfr"]                   = state.Manufacturer,
            ["ups.model"]                 = state.Model,
            ["ups.serial"]                = state.Serial,
            ["ups.status"]                = state.Status.ToNutString(),
            ["ups.vendorid"]              = state.VendorId.ToString("X4").ToLower(),
            ["ups.productid"]             = state.ProductId.ToString("X4").ToLower(),
        };

        return vars;
    }

    public static string GetType(string varName) => varName switch
    {
        "battery.charge"         => "INTEGER",
        "battery.charge.low"     => "INTEGER",
        "battery.charge.warning" => "INTEGER",
        "battery.runtime"        => "INTEGER",
        "ups.load"               => "INTEGER",
        _                        => "STRING:256"
    };

    public static string GetDescription(string varName) => varName switch
    {
        "battery.charge"         => "Battery charge (percent of full)",
        "battery.runtime"        => "Battery runtime (seconds)",
        "battery.voltage"        => "Battery voltage",
        "battery.voltage.nominal"=> "Nominal battery voltage",
        "input.voltage"          => "Input voltage",
        "output.voltage"         => "Output voltage",
        "ups.load"               => "Load on UPS (percent of full)",
        "ups.status"             => "UPS status",
        "ups.model"              => "UPS model",
        "ups.mfr"                => "UPS manufacturer",
        _                        => ""
    };

    private static string Fmt(double value, int decimals) =>
        value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
}

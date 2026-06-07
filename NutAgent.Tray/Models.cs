namespace NutAgent.Tray;

public record StatusResponse(
    double  Charge,
    int     RuntimeSeconds,
    int?    RuntimeEst,
    string  Status,
    double? Load,
    bool    IsConnected,
    string  Model,
    string  Manufacturer);

public class TrayConfig
{
    public string Mode                     { get; set; } = "Server";
    public string UpsName                  { get; set; } = "ups";
    public string UpsDescription           { get; set; } = "UPS";
    public string Username                 { get; set; } = "";
    public string Password                 { get; set; } = "";
    public int    Port                     { get; set; } = 3493;
    public string RemoteHost               { get; set; } = "";
    public int    RemotePort               { get; set; } = 3493;
    public string RemoteUpsName            { get; set; } = "ups";
    public string RemoteUsername           { get; set; } = "";
    public string RemotePassword           { get; set; } = "";
    public int    ShutdownBatteryThreshold { get; set; } = 20;
    public int    ShutdownRuntimeMinutes   { get; set; } = 5;
    public int    ShutdownDelaySeconds     { get; set; } = 60;
    public string ShutdownMode             { get; set; } = "Manual";
    public int    SafetyMarginMinutes      { get; set; } = 3;
    public int    PollIntervalSeconds      { get; set; } = 30;
}

public record SetConfigResult(bool Ok, bool RequiresRestart = false);

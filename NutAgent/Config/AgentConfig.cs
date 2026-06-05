namespace NutAgent.Config;

public class AgentConfig
{
    public AgentMode Mode { get; set; } = AgentMode.Server;

    // Server mode
    public string UpsName { get; set; } = "ups";
    public string UpsDescription { get; set; } = "UPS";
    public int Port { get; set; } = 3493;
    public List<NutUser> Users { get; set; } = new();

    // Client mode
    public string RemoteHost { get; set; } = "";
    public int RemotePort { get; set; } = 3493;
    public string RemoteUpsName { get; set; } = "ups";
    public string RemoteUsername { get; set; } = "";
    public string RemotePassword { get; set; } = "";

    // Shutdown policy (both modes)
    public int          ShutdownBatteryThreshold { get; set; } = 20;  // trigger shutdown below this %
    public int          ShutdownRuntimeMinutes   { get; set; } = 5;   // trigger shutdown below this many minutes of runtime (Manual mode)
    public int          ShutdownDelaySeconds     { get; set; } = 60;  // grace period before shutdown
    public ShutdownMode ShutdownMode             { get; set; } = ShutdownMode.Manual;
    public int          SafetyMarginMinutes      { get; set; } = 3;   // Auto mode: shut down when MinutesToEmpty drops below this
    public int          PollIntervalSeconds      { get; set; } = 30;  // client poll interval
}

public enum AgentMode     { Server, Client }
public enum ShutdownMode  { Manual, Auto }

public class NutUser
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool AllowSet { get; set; } = false;
}

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NutAgent.Config;
using NutAgent.Hid;

namespace NutAgent.Ipc;

// Local IPC over a named pipe — used by the tray app to read status and update config.
// Protocol: JSON lines, one request per connection, one response.
// Pipe name: \\.\pipe\nutagent
public sealed class PipeServer
{
    private const string PipeName = "nutagent";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters           = { new JsonStringEnumConverter() },
    };

    private readonly Func<UpsState>      _getState;
    private readonly AgentConfig         _config;
    private readonly Action?             _onNutServerRestart;
    private readonly ILogger<PipeServer> _logger;

    public PipeServer(Func<UpsState> getState, AgentConfig config, Action? onNutServerRestart, ILogger<PipeServer> logger)
    {
        _getState           = getState;
        _config             = config;
        _onNutServerRestart = onNutServerRestart;
        _logger             = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("IPC pipe listening: \\\\.\\pipe\\{Name}", PipeName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Allow any local user to connect — service runs as SYSTEM and would
                // otherwise block user-session clients like the tray app.
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                using var pipe = NamedPipeServerStreamAcl.Create(
                    PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

                await pipe.WaitForConnectionAsync(ct);

                // 5s timeout per connection — prevents a hung client from blocking the loop.
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connCts.CancelAfter(TimeSpan.FromSeconds(5));
                await HandleConnectionAsync(pipe, connCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pipe error — retrying");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        try
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) return;

            var response = ProcessRequest(line);
            await writer.WriteLineAsync(response.AsMemory(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Pipe connection error");
        }
    }

    private string ProcessRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            return type switch
            {
                "status"    => BuildStatus(),
                "getConfig" => BuildGetConfig(),
                "setConfig" => ApplySetConfig(doc.RootElement),
                _           => """{"error":"unknown-type"}"""
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pipe request error: {Json}", json);
            return """{"error":"invalid-request"}""";
        }
    }

    private string BuildStatus()
    {
        var state = _getState();
        var vars  = state.Variables;

        double? load = vars.TryGetValue("ups.load", out var ls) &&
                       double.TryParse(ls, out var lv) ? lv : null;
        int?    runtimeEst = vars.TryGetValue("battery.runtime.est", out var rs) &&
                             int.TryParse(rs, out var rv) ? rv : null;

        return JsonSerializer.Serialize(new
        {
            charge         = state.BatteryCharge,
            runtimeSeconds = state.RuntimeSeconds,
            runtimeEst,
            status         = state.Status.ToNutString(),
            load,
            isConnected    = state.VendorId != 0,
            model          = state.Model,
            manufacturer   = state.Manufacturer,
        }, JsonOpts);
    }

    private string BuildGetConfig() => JsonSerializer.Serialize(_config, JsonOpts);

    private string ApplySetConfig(JsonElement el)
    {
        bool portChanged  = false;
        bool modeChanged  = false;

        // Threshold fields — effective immediately on next poll cycle.
        if (el.TryGetProperty("shutdownBatteryThreshold", out var bt)) _config.ShutdownBatteryThreshold = bt.GetInt32();
        if (el.TryGetProperty("shutdownRuntimeMinutes",   out var rt)) _config.ShutdownRuntimeMinutes   = rt.GetInt32();
        if (el.TryGetProperty("shutdownDelaySeconds",     out var ds)) _config.ShutdownDelaySeconds     = ds.GetInt32();
        if (el.TryGetProperty("safetyMarginMinutes",      out var sm)) _config.SafetyMarginMinutes      = sm.GetInt32();
        if (el.TryGetProperty("shutdownMode", out var sdMode) && sdMode.GetString() is {} sdModeStr &&
            Enum.TryParse<ShutdownMode>(sdModeStr, ignoreCase: true, out var sdModeVal))
            _config.ShutdownMode = sdModeVal;

        // Name / identity fields — NutSession reads these live, effective immediately.
        if (el.TryGetProperty("upsName",        out var un) && un.GetString() is {} u) _config.UpsName        = u;
        if (el.TryGetProperty("upsDescription", out var ud) && ud.GetString() is {} d) _config.UpsDescription = d;

        // Remote fields (client mode) — NutClient reads per-poll, effective on next poll.
        if (el.TryGetProperty("remoteHost",          out var rh) && rh.GetString() is {} h) _config.RemoteHost    = h;
        if (el.TryGetProperty("remotePort",          out var rp))                            _config.RemotePort    = rp.GetInt32();
        if (el.TryGetProperty("remoteUpsName",       out var ru) && ru.GetString() is {} r)  _config.RemoteUpsName = r;
        if (el.TryGetProperty("pollIntervalSeconds", out var pi))                            _config.PollIntervalSeconds = pi.GetInt32();

        // Port change — NutServer rebinds on restart.
        if (el.TryGetProperty("port", out var port) && port.GetInt32() != _config.Port)
        {
            _config.Port = port.GetInt32();
            portChanged  = true;
        }

        // Mode change — requires full service restart (Server↔Client restructures the worker).
        if (el.TryGetProperty("mode", out var modeEl) && modeEl.GetString() is {} modeStr &&
            Enum.TryParse<AgentMode>(modeStr, ignoreCase: true, out var mode) && mode != _config.Mode)
        {
            _config.Mode = mode;
            modeChanged  = true;
        }

        PersistConfig();

        if (portChanged)
            _onNutServerRestart?.Invoke();

        return modeChanged
            ? """{"ok":true,"requiresRestart":true}"""
            : """{"ok":true}""";
    }

    private void PersistConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return;

            var node  = JsonNode.Parse(File.ReadAllText(path));
            var agent = node?["Agent"];
            if (agent == null) return;

            agent["ShutdownBatteryThreshold"] = _config.ShutdownBatteryThreshold;
            agent["ShutdownRuntimeMinutes"]   = _config.ShutdownRuntimeMinutes;
            agent["ShutdownDelaySeconds"]     = _config.ShutdownDelaySeconds;
            agent["ShutdownMode"]             = _config.ShutdownMode.ToString();
            agent["SafetyMarginMinutes"]      = _config.SafetyMarginMinutes;
            agent["Port"]                     = _config.Port;
            agent["UpsName"]                  = _config.UpsName;
            agent["UpsDescription"]           = _config.UpsDescription;
            agent["RemoteHost"]               = _config.RemoteHost;
            agent["RemotePort"]               = _config.RemotePort;
            agent["RemoteUpsName"]            = _config.RemoteUpsName;
            agent["PollIntervalSeconds"]      = _config.PollIntervalSeconds;

            File.WriteAllText(path,
                node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist config to appsettings.json");
        }
    }
}

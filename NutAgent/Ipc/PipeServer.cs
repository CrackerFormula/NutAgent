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
                // Service runs as SYSTEM and would otherwise block user-session clients
                // like the tray app — so allow interactively logged-on users specifically
                // (S-1-5-4), rather than BUILTIN\Users, which also covers network/batch/
                // service logons and is broader than anything that legitimately needs this.
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
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
            // Don't log the raw payload — setConfig requests carry plaintext credentials.
            _logger.LogDebug(ex, "Pipe request error");
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

    private string BuildGetConfig()
    {
        // Flatten the primary local user (Users[0]) to username/password for the simple
        // single-account UI in the tray Settings window — most installs have exactly one.
        var user = _config.Users.FirstOrDefault();

        // Passwords are write-only over IPC — any local interactive user can connect to
        // this pipe, and round-tripping plaintext secrets through getConfig would hand
        // them out on request. The UI only needs to know whether one is already set so
        // it can show a placeholder; it sends a new value only when the user changes it.
        return JsonSerializer.Serialize(new
        {
            mode                     = _config.Mode,
            upsName                  = _config.UpsName,
            upsDescription           = _config.UpsDescription,
            username                 = user?.Username ?? "",
            hasPassword              = !string.IsNullOrEmpty(user?.Password),
            port                     = _config.Port,
            remoteHost               = _config.RemoteHost,
            remotePort               = _config.RemotePort,
            remoteUpsName            = _config.RemoteUpsName,
            remoteUsername           = _config.RemoteUsername,
            hasRemotePassword        = !string.IsNullOrEmpty(_config.RemotePassword),
            shutdownBatteryThreshold = _config.ShutdownBatteryThreshold,
            shutdownRuntimeMinutes   = _config.ShutdownRuntimeMinutes,
            shutdownDelaySeconds     = _config.ShutdownDelaySeconds,
            shutdownMode             = _config.ShutdownMode,
            safetyMarginMinutes      = _config.SafetyMarginMinutes,
            pollIntervalSeconds      = _config.PollIntervalSeconds,
        }, JsonOpts);
    }

    private string ApplySetConfig(JsonElement el)
    {
        bool portChanged  = false;
        bool modeChanged  = false;

        // Threshold fields — effective immediately on next poll cycle. Clamped to sane
        // ranges: an out-of-range value here (e.g. a 100% charge threshold) would make
        // the "on battery" check trip on the very next minor power blip and force an
        // immediate shutdown — bound the inputs so a bad request can't manufacture that.
        if (el.TryGetProperty("shutdownBatteryThreshold", out var bt)) _config.ShutdownBatteryThreshold = Clamp(bt.GetInt32(), 1, 99);
        if (el.TryGetProperty("shutdownRuntimeMinutes",   out var rt)) _config.ShutdownRuntimeMinutes   = Clamp(rt.GetInt32(), 0, 1440);
        if (el.TryGetProperty("shutdownDelaySeconds",     out var ds)) _config.ShutdownDelaySeconds     = Clamp(ds.GetInt32(), 0, 3600);
        if (el.TryGetProperty("safetyMarginMinutes",      out var sm)) _config.SafetyMarginMinutes      = Clamp(sm.GetInt32(), 0, 120);
        if (el.TryGetProperty("shutdownMode", out var sdMode) && sdMode.GetString() is {} sdModeStr &&
            Enum.TryParse<ShutdownMode>(sdModeStr, ignoreCase: true, out var sdModeVal))
            _config.ShutdownMode = sdModeVal;

        // Name / identity fields — NutSession reads these live, effective immediately.
        if (el.TryGetProperty("upsName",        out var un) && un.GetString() is {} u) _config.UpsName        = u;
        if (el.TryGetProperty("upsDescription", out var ud) && ud.GetString() is {} d) _config.UpsDescription = d;

        // Local NUT login (server mode) — update the primary user (Users[0]); create one if
        // the list is empty. Username and password are applied independently: the tray only
        // sends "password" when the user actually typed a new one (see BuildGetConfig — it
        // never echoes the existing plaintext password back, so the UI can't "round-trip"
        // an unchanged value). NutSession reads _config.Users live, effective immediately.
        string? newUsername = el.TryGetProperty("username", out var lu) ? lu.GetString() : null;
        string? newPassword = el.TryGetProperty("password", out var lp) ? lp.GetString() : null;
        if (newUsername != null || newPassword != null)
        {
            var primary = _config.Users.FirstOrDefault();
            if (primary == null)
            {
                primary = new NutUser();
                _config.Users.Add(primary);
            }
            if (newUsername != null) primary.Username = newUsername;
            if (newPassword != null) primary.Password = newPassword;
        }

        // Remote fields (client mode) — NutClient reads per-poll, effective on next poll.
        // RemotePassword follows the same "only sent when changed" rule as the local password.
        if (el.TryGetProperty("remoteHost",          out var rh) && rh.GetString() is {} h)  _config.RemoteHost     = h;
        if (el.TryGetProperty("remotePort",          out var rp))                             _config.RemotePort     = Clamp(rp.GetInt32(), 1, 65535);
        if (el.TryGetProperty("remoteUpsName",       out var ru) && ru.GetString() is {} r)   _config.RemoteUpsName  = r;
        if (el.TryGetProperty("remoteUsername",      out var rUser) && rUser.GetString() is {} rUserName) _config.RemoteUsername = rUserName;
        if (el.TryGetProperty("remotePassword",      out var rPass) && rPass.GetString() is {} rPassword) _config.RemotePassword = rPassword;
        if (el.TryGetProperty("pollIntervalSeconds", out var pi))                             _config.PollIntervalSeconds = Clamp(pi.GetInt32(), 5, 3600);

        // Port change — NutServer rebinds on restart.
        if (el.TryGetProperty("port", out var port) && Clamp(port.GetInt32(), 1, 65535) is var newPort && newPort != _config.Port)
        {
            _config.Port = newPort;
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

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

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
            agent["RemoteUsername"]           = _config.RemoteUsername;
            agent["RemotePassword"]           = _config.RemotePassword;
            agent["PollIntervalSeconds"]      = _config.PollIntervalSeconds;

            var users = new JsonArray();
            foreach (var nutUser in _config.Users)
                users.Add(new JsonObject
                {
                    ["Username"] = nutUser.Username,
                    ["Password"] = nutUser.Password,
                    ["AllowSet"] = nutUser.AllowSet,
                });
            agent["Users"] = users;

            File.WriteAllText(path,
                node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist config to appsettings.json");
        }
    }
}

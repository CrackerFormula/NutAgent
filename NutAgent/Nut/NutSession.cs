using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NutAgent.Config;
using NutAgent.Hid;

namespace NutAgent.Nut;

// Handles a single NUT client connection.
// NUT protocol spec: https://networkupstools.org/docs/developer-guide.chunked/ar01s09.html
internal sealed class NutSession
{
    // A client that never completes a line (or never sends anything) would otherwise
    // hold its connection — and the thread/socket behind it — open forever.
    private const int IdleTimeoutSeconds = 60;
    // NUT commands are short ("LIST VAR <upsname>" etc.) — anything past this is either
    // a misbehaving client or someone testing how much memory a single line can consume.
    private const int MaxLineLength = 512;

    private readonly TcpClient _client;
    private readonly AgentConfig _config;
    private readonly Func<UpsState> _getState;
    private readonly LoginThrottle _loginThrottle;
    private readonly IPAddress? _remoteAddress;
    private readonly ILogger _logger;

    private string? _authenticatedUser;
    private bool _passwordVerified;

    public NutSession(TcpClient client, AgentConfig config, Func<UpsState> getState,
        LoginThrottle loginThrottle, ILogger logger)
    {
        _client        = client;
        _config        = config;
        _getState      = getState;
        _loginThrottle = loginThrottle;
        _logger        = logger;
        _remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await using var stream = _client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        // Reschedules on every line read — fires only when the client goes idle.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                idleCts.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));

                string? line;
                try
                {
                    line = await ReadLineAsync(reader, idleCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug("NUT session idle for {Seconds}s — disconnecting", IdleTimeoutSeconds);
                    break;
                }
                if (line == null) break;

                var response = HandleCommand(line.Trim());
                if (response.Length > 0)
                    await writer.WriteLineAsync(response);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NUT session error");
        }
    }

    // StreamReader.ReadLineAsync has no length cap — a client that never sends '\n'
    // would make it buffer the line in memory indefinitely. Read one char at a time
    // (NUT commands are tiny and infrequent, so the overhead is irrelevant) and bail
    // out once MaxLineLength is exceeded.
    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        var sb  = new StringBuilder();
        var buf = new char[1];

        while (await reader.ReadAsync(buf.AsMemory(0, 1), ct) > 0)
        {
            if (buf[0] == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return sb.ToString();
            }
            if (sb.Length >= MaxLineLength)
                throw new InvalidOperationException($"NUT command exceeded {MaxLineLength} characters");

            sb.Append(buf[0]);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private string HandleCommand(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "ERR INVALID-ARGUMENT";

        return parts[0].ToUpper() switch
        {
            "USERNAME" => HandleUsername(parts),
            "PASSWORD" => HandlePassword(parts),
            "LOGIN"    => HandleLogin(parts),
            "LOGOUT"   => "OK Goodbye",
            "LIST"     => HandleList(parts),
            "GET"      => HandleGet(parts),
            "VER"      => "NutAgent 1.0.0",
            "NETVER"   => "3",
            _          => "ERR UNKNOWN-COMMAND"
        };
    }

    private string HandleUsername(string[] parts)
    {
        if (parts.Length < 2) return "ERR INVALID-ARGUMENT";
        if (IsLockedOut()) return "ERR ACCESS-DENIED";

        var user = _config.Users.FirstOrDefault(u =>
            u.Username.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
        if (user == null)
        {
            RecordFailure();
            return "ERR ACCESS-DENIED";
        }
        _authenticatedUser = parts[1];
        _passwordVerified = false;
        return "OK";
    }

    private string HandlePassword(string[] parts)
    {
        if (parts.Length < 2 || _authenticatedUser == null) return "ERR ACCESS-DENIED";
        if (IsLockedOut()) return "ERR ACCESS-DENIED";

        var user = _config.Users.FirstOrDefault(u =>
            u.Username.Equals(_authenticatedUser, StringComparison.OrdinalIgnoreCase));
        if (user == null || !FixedTimeEquals(user.Password, parts[1]))
        {
            RecordFailure();
            return "ERR ACCESS-DENIED";
        }

        if (_remoteAddress != null) _loginThrottle.RecordSuccess(_remoteAddress);
        _passwordVerified = true;
        return "OK";
    }

    private bool IsLockedOut() => _remoteAddress != null && _loginThrottle.IsLockedOut(_remoteAddress);

    private void RecordFailure()
    {
        if (_remoteAddress != null) _loginThrottle.RecordFailure(_remoteAddress);
    }

    // Guards against timing attacks that could otherwise reveal how many leading
    // characters of a guessed password are correct.
    private static bool FixedTimeEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private string HandleLogin(string[] parts)
    {
        if (parts.Length < 2) return "ERR INVALID-ARGUMENT";
        if (!_passwordVerified) return "ERR ACCESS-DENIED";
        if (!parts[1].Equals(_config.UpsName, StringComparison.OrdinalIgnoreCase))
            return "ERR UNKNOWN-UPS";
        return "OK";
    }

    private string HandleList(string[] parts)
    {
        if (parts.Length < 2) return "ERR INVALID-ARGUMENT";

        return parts[1].ToUpper() switch
        {
            "UPS"  => ListUps(),
            "VAR"  => parts.Length >= 3 ? ListVar(parts[2]) : "ERR INVALID-ARGUMENT",
            "RW"   => parts.Length >= 3 ? $"BEGIN LIST RW {parts[2]}\nEND LIST RW {parts[2]}" : "ERR INVALID-ARGUMENT",
            "CMD"  => parts.Length >= 3 ? $"BEGIN LIST CMD {parts[2]}\nEND LIST CMD {parts[2]}" : "ERR INVALID-ARGUMENT",
            _      => "ERR INVALID-ARGUMENT"
        };
    }

    private string ListUps()
    {
        return $"BEGIN LIST UPS\nUPS {_config.UpsName} \"{_config.UpsDescription}\"\nEND LIST UPS";
    }

    private string ListVar(string upsName)
    {
        if (!upsName.Equals(_config.UpsName, StringComparison.OrdinalIgnoreCase))
            return "ERR UNKNOWN-UPS";

        var vars = NutVariableMap.Build(_getState(), _config.ShutdownBatteryThreshold, _config.UpsNominalWatts, _config.UpsNominalVA);
        var lines = new System.Text.StringBuilder();
        lines.Append($"BEGIN LIST VAR {upsName}\n");
        foreach (var (k, v) in vars)
            lines.Append($"VAR {upsName} {k} \"{v}\"\n");
        lines.Append($"END LIST VAR {upsName}");
        return lines.ToString();
    }

    private string HandleGet(string[] parts)
    {
        if (parts.Length < 2) return "ERR INVALID-ARGUMENT";

        return parts[1].ToUpper() switch
        {
            "VAR"      => parts.Length >= 4 ? GetVar(parts[2], parts[3]) : "ERR INVALID-ARGUMENT",
            "TYPE"     => parts.Length >= 4 ? GetType(parts[2], parts[3]) : "ERR INVALID-ARGUMENT",
            "DESC"     => parts.Length >= 4 ? GetDesc(parts[2], parts[3]) : "ERR INVALID-ARGUMENT",
            "UPSDESC"  => parts.Length >= 3 ? GetUpsDesc(parts[2]) : "ERR INVALID-ARGUMENT",
            "NUMLOGINS"=> parts.Length >= 3 ? "NUMLOGINS 1" : "ERR INVALID-ARGUMENT",
            _          => "ERR INVALID-ARGUMENT"
        };
    }

    private string GetVar(string upsName, string varName)
    {
        if (!upsName.Equals(_config.UpsName, StringComparison.OrdinalIgnoreCase))
            return "ERR UNKNOWN-UPS";

        var vars = NutVariableMap.Build(_getState(), _config.ShutdownBatteryThreshold, _config.UpsNominalWatts, _config.UpsNominalVA);
        return vars.TryGetValue(varName, out var value)
            ? $"VAR {upsName} {varName} \"{value}\""
            : "ERR VAR-NOT-SUPPORTED";
    }

    private string GetType(string upsName, string varName)
    {
        if (!upsName.Equals(_config.UpsName, StringComparison.OrdinalIgnoreCase))
            return "ERR UNKNOWN-UPS";
        return $"TYPE {upsName} {varName} {NutVariableMap.GetType(varName)}";
    }

    private string GetDesc(string upsName, string varName)
    {
        if (!upsName.Equals(_config.UpsName, StringComparison.OrdinalIgnoreCase))
            return "ERR UNKNOWN-UPS";
        return $"DESC {upsName} {varName} \"{NutVariableMap.GetDescription(varName)}\"";
    }

    private string GetUpsDesc(string upsName)
    {
        if (!upsName.Equals(_config.UpsName, StringComparison.OrdinalIgnoreCase))
            return "ERR UNKNOWN-UPS";
        return $"UPSDESC {upsName} \"{_config.UpsDescription}\"";
    }
}

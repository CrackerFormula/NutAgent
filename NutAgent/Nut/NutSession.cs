using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NutAgent.Config;
using NutAgent.Hid;

namespace NutAgent.Nut;

// Handles a single NUT client connection.
// NUT protocol spec: https://networkupstools.org/docs/developer-guide.chunked/ar01s09.html
internal sealed class NutSession
{
    private readonly TcpClient _client;
    private readonly AgentConfig _config;
    private readonly Func<UpsState> _getState;
    private readonly ILogger _logger;

    private string? _authenticatedUser;
    private bool _loggedIn;

    public NutSession(TcpClient client, AgentConfig config, Func<UpsState> getState, ILogger logger)
    {
        _client   = client;
        _config   = config;
        _getState = getState;
        _logger   = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await using var stream = _client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                var response = HandleCommand(line.Trim());
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
        var user = _config.Users.FirstOrDefault(u =>
            u.Username.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
        if (user == null) return "ERR ACCESS-DENIED";
        _authenticatedUser = parts[1];
        return "OK";
    }

    private string HandlePassword(string[] parts)
    {
        if (parts.Length < 2 || _authenticatedUser == null) return "ERR ACCESS-DENIED";
        var user = _config.Users.FirstOrDefault(u =>
            u.Username.Equals(_authenticatedUser, StringComparison.OrdinalIgnoreCase));
        if (user == null || user.Password != parts[1]) return "ERR ACCESS-DENIED";
        return "OK";
    }

    private string HandleLogin(string[] parts)
    {
        if (parts.Length < 2) return "ERR INVALID-ARGUMENT";
        if (_authenticatedUser == null) return "ERR ACCESS-DENIED";
        if (!parts[1].Equals(_config.UpsName, StringComparison.OrdinalIgnoreCase))
            return "ERR UNKNOWN-UPS";
        _loggedIn = true;
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

        var vars = NutVariableMap.Build(_getState(), _config.UpsDescription);
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

        var vars = NutVariableMap.Build(_getState(), _config.UpsDescription);
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

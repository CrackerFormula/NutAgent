using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace NutAgent.Tray.Ipc;

public static class PipeClient
{
    private const string PipeName = "nutagent";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static Task<StatusResponse?> GetStatusAsync(CancellationToken ct = default)
        => SendAsync<StatusResponse>(new { type = "status" }, ct);

    public static Task<TrayConfig?> GetConfigAsync(CancellationToken ct = default)
        => SendAsync<TrayConfig>(new { type = "getConfig" }, ct);

    public static Task<SetConfigResult?> SetConfigAsync(TrayConfig config, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"]                     = "setConfig",
            ["mode"]                     = config.Mode,
            ["upsName"]                  = config.UpsName,
            ["upsDescription"]           = config.UpsDescription,
            ["port"]                     = config.Port,
            ["remoteHost"]               = config.RemoteHost,
            ["remotePort"]               = config.RemotePort,
            ["remoteUpsName"]            = config.RemoteUpsName,
            ["pollIntervalSeconds"]      = config.PollIntervalSeconds,
            ["shutdownBatteryThreshold"] = config.ShutdownBatteryThreshold,
            ["shutdownRuntimeMinutes"]   = config.ShutdownRuntimeMinutes,
            ["shutdownDelaySeconds"]     = config.ShutdownDelaySeconds,
            ["shutdownMode"]             = config.ShutdownMode,
            ["safetyMarginMinutes"]      = config.SafetyMarginMinutes,
        };
        return SendAsync<SetConfigResult>(payload, ct);
    }

    private static async Task<T?> SendAsync<T>(object request, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                                                   PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        try   { await pipe.ConnectAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("NutAgent service pipe not responding"); }

        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using  var reader = new StreamReader(pipe, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOpts).AsMemory(), ct);
        var line = await reader.ReadLineAsync(ct) ?? "{}";
        return JsonSerializer.Deserialize<T>(line, JsonOpts);
    }
}

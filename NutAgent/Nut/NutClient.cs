using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NutAgent.Config;
using NutAgent.Hid;
using NutAgent.Shutdown;

namespace NutAgent.Nut;

// Polls a remote NUT server and triggers a local shutdown when power is lost.
public sealed class NutClient
{
    private readonly AgentConfig _config;
    private readonly ShutdownManager _shutdown;
    private readonly ILogger<NutClient> _logger;

    public NutClient(AgentConfig config, ShutdownManager shutdown, ILogger<NutClient> logger)
    {
        _config   = config;
        _shutdown = shutdown;
        _logger   = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("NUT client monitoring {Host}:{Port} / {Ups}",
            _config.RemoteHost, _config.RemotePort, _config.RemoteUpsName);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Poll failed — will retry in {Interval}s", _config.PollIntervalSeconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds), ct);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await tcp.ConnectAsync(_config.RemoteHost, _config.RemotePort, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Connection to {_config.RemoteHost}:{_config.RemotePort} timed out");
        }

        var stream = tcp.GetStream();
        stream.ReadTimeout  = 10_000;
        stream.WriteTimeout = 10_000;

        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };

        await AuthenticateAsync(writer, reader, ct);

        var status     = await QueryVarAsync(writer, reader, "ups.status",      ct);
        var chargeStr  = await QueryVarAsync(writer, reader, "battery.charge",  ct);
        var runtimeStr = await QueryVarAsync(writer, reader, "battery.runtime", ct);

        await writer.WriteLineAsync("LOGOUT");

        _logger.LogDebug("UPS status={Status} charge={Charge} runtime={Runtime}s", status, chargeStr, runtimeStr);

        var charge  = double.TryParse(chargeStr,  NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 100.0;
        var runtime = int.TryParse(runtimeStr, out var r) ? r : int.MaxValue;
        var isOnBattery  = status.Contains("OB");
        var isLowBattery = status.Contains("LB");

        bool chargeCritical  = charge <= _config.ShutdownBatteryThreshold;
        bool runtimeCritical = runtime > 0 && runtime <= _config.ShutdownRuntimeMinutes * 60;

        if (isLowBattery || (isOnBattery && (chargeCritical || runtimeCritical)))
        {
            _logger.LogWarning(
                "UPS critical (status={Status} charge={Charge}% runtime={Runtime}s) — scheduling shutdown in {Delay}s",
                status, charge, runtime, isLowBattery ? 0 : _config.ShutdownDelaySeconds);
            _shutdown.ScheduleShutdown(isLowBattery ? 0 : _config.ShutdownDelaySeconds);
        }
        else if (!isOnBattery)
        {
            _shutdown.CancelShutdown();
        }
    }

    private async Task AuthenticateAsync(StreamWriter writer, StreamReader reader, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_config.RemoteUsername))
        {
            await writer.WriteLineAsync($"USERNAME {_config.RemoteUsername}");
            var r1 = await reader.ReadLineAsync(ct) ?? "";
            if (r1.StartsWith("ERR")) throw new InvalidOperationException($"Authentication failed: {r1}");

            await writer.WriteLineAsync($"PASSWORD {_config.RemotePassword}");
            var r2 = await reader.ReadLineAsync(ct) ?? "";
            if (r2.StartsWith("ERR")) throw new InvalidOperationException($"Authentication failed: {r2}");
        }

        await writer.WriteLineAsync($"LOGIN {_config.RemoteUpsName}");
        var r3 = await reader.ReadLineAsync(ct) ?? "";
        if (r3.StartsWith("ERR")) throw new InvalidOperationException($"Login failed: {r3}");
    }

    private async Task<string> QueryVarAsync(StreamWriter writer, StreamReader reader, string varName, CancellationToken ct)
    {
        await writer.WriteLineAsync($"GET VAR {_config.RemoteUpsName} {varName}");
        var line = await reader.ReadLineAsync(ct) ?? "";

        // Response: VAR <ups> <var> "<value>"
        var start = line.LastIndexOf('"');
        var end = line.IndexOf('"');
        if (end >= 0 && start > end)
            return line.Substring(end + 1, start - end - 1);

        return "";
    }
}

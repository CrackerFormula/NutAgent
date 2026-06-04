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
        await tcp.ConnectAsync(_config.RemoteHost, _config.RemotePort, ct);

        await using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };

        await AuthenticateAsync(writer, reader, ct);

        var status = await QueryVarAsync(writer, reader, "ups.status", ct);
        var chargeStr = await QueryVarAsync(writer, reader, "battery.charge", ct);

        await writer.WriteLineAsync("LOGOUT");

        _logger.LogDebug("UPS status={Status} charge={Charge}", status, chargeStr);

        var charge = double.TryParse(chargeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 100.0;
        var isOnBattery = status.Contains("OB");
        var isLowBattery = status.Contains("LB");

        if (isLowBattery || (isOnBattery && charge <= _config.ShutdownBatteryThreshold))
        {
            _logger.LogWarning("UPS critical (status={Status} charge={Charge}%) — scheduling shutdown in {Delay}s",
                status, charge, isLowBattery ? 0 : _config.ShutdownDelaySeconds);
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
            await reader.ReadLineAsync(ct); // OK or ERR

            await writer.WriteLineAsync($"PASSWORD {_config.RemotePassword}");
            await reader.ReadLineAsync(ct); // OK or ERR
        }

        await writer.WriteLineAsync($"LOGIN {_config.RemoteUpsName}");
        await reader.ReadLineAsync(ct); // OK or ERR
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

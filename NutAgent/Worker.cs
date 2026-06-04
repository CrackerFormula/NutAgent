using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NutAgent.Config;
using NutAgent.Hid;
using NutAgent.Nut;
using NutAgent.Shutdown;

namespace NutAgent;

public sealed class Worker : BackgroundService
{
    private readonly AgentConfig _config;
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public Worker(IOptions<AgentConfig> config, ILogger<Worker> logger, ILoggerFactory loggerFactory)
    {
        _config       = config.Value;
        _logger       = logger;
        _loggerFactory = loggerFactory;
    }

    protected override Task ExecuteAsync(CancellationToken ct) =>
        _config.Mode == AgentMode.Server ? RunServerAsync(ct) : RunClientAsync(ct);

    private async Task RunServerAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting in server mode — UPS name: {UpsName}", _config.UpsName);

        using var reader = new HidUpsReader(_loggerFactory.CreateLogger<HidUpsReader>());

        // Written by readLoop, read by concurrent NutSession tasks — use Volatile to prevent stale reads.
        UpsState state = new UpsState();
        UpsState GetState() => Volatile.Read(ref state);

        var readLoop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var newState = reader.Read();
                Volatile.Write(ref state, newState);

                if (newState.Status.IsOnBattery())
                    _logger.LogWarning("UPS on battery — charge: {Charge:F0}%  runtime: {Runtime}s",
                        newState.BatteryCharge, newState.RuntimeSeconds);

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }, ct);

        var server = new NutServer(_config, GetState, _loggerFactory.CreateLogger<NutServer>());
        await Task.WhenAll(readLoop, server.RunAsync(ct));
    }

    private async Task RunClientAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting in client mode — server: {Host}:{Port}",
            _config.RemoteHost, _config.RemotePort);

        var shutdown = new ShutdownManager(_loggerFactory.CreateLogger<ShutdownManager>());
        var client   = new NutClient(_config, shutdown, _loggerFactory.CreateLogger<NutClient>());
        await client.RunAsync(ct);
    }
}

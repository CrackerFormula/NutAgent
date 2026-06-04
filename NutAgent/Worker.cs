using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NutAgent.Config;
using NutAgent.Hid;
using NutAgent.Ipc;
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

        using var reader   = new HidUpsReader(_loggerFactory.CreateLogger<HidUpsReader>());
        var       shutdown = new ShutdownManager(_loggerFactory.CreateLogger<ShutdownManager>());
        var       tracker  = new DischargeTracker();

        // Written by readLoop, read by concurrent NutSession tasks — use Volatile to prevent stale reads.
        UpsState state = new UpsState();
        UpsState GetState() => Volatile.Read(ref state);

        var readLoop = Task.Run(async () =>
        {
            bool wasOnBattery = false;
            while (!ct.IsCancellationRequested)
            {
                var newState     = reader.Read();
                bool isOnBattery = newState.Status.IsOnBattery();

                if (!isOnBattery && wasOnBattery)
                    tracker.Reset();
                wasOnBattery = isOnBattery;

                tracker.Record(newState.BatteryCharge, newState.LastUpdated);
                var minutesToEmpty = tracker.MinutesToEmpty;
                if (minutesToEmpty < double.MaxValue)
                    newState.Variables["battery.runtime.est"] = ((int)(minutesToEmpty * 60)).ToString();

                Volatile.Write(ref state, newState);

                if (isOnBattery)
                {
                    _logger.LogWarning("UPS on battery — charge: {Charge:F0}%  runtime: {Runtime}s",
                        newState.BatteryCharge, newState.RuntimeSeconds);

                    bool chargeCritical  = newState.BatteryCharge <= _config.ShutdownBatteryThreshold;
                    bool runtimeCritical = newState.RuntimeSeconds > 0 &&
                                          newState.RuntimeSeconds <= _config.ShutdownRuntimeMinutes * 60;

                    if (newState.Status.IsLowBattery() || chargeCritical || runtimeCritical)
                        shutdown.ScheduleShutdown(newState.Status.IsLowBattery() ? 0 : _config.ShutdownDelaySeconds);
                }
                else
                {
                    shutdown.CancelShutdown();
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }, ct);

        // NutServer restart channel — signalled by PipeServer when the port changes.
        var restartCh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest });

        var nutServerLoop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var server      = new NutServer(_config, GetState, _loggerFactory.CreateLogger<NutServer>());
                var serverTask  = server.RunAsync(serverCts.Token);
                var triggerTask = restartCh.Reader.ReadAsync(ct).AsTask();

                await Task.WhenAny(serverTask, triggerTask);
                serverCts.Cancel();
                try { await serverTask; } catch (OperationCanceledException) { }

                if (triggerTask.IsCompletedSuccessfully && !ct.IsCancellationRequested)
                    _logger.LogInformation("NUT server restarting on port {Port}", _config.Port);
            }
        }, ct);

        var pipe = new PipeServer(GetState, _config,
            onNutServerRestart: () => restartCh.Writer.TryWrite(true),
            _loggerFactory.CreateLogger<PipeServer>());

        await Task.WhenAll(readLoop, nutServerLoop, pipe.RunAsync(ct));
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

using Microsoft.Extensions.Logging;

namespace NutAgent.Shutdown;

public sealed class ShutdownManager
{
    private readonly ILogger<ShutdownManager> _logger;
    private CancellationTokenSource? _shutdownCts;
    private readonly object _lock = new();

    public ShutdownManager(ILogger<ShutdownManager> logger)
    {
        _logger = logger;
    }

    public void ScheduleShutdown(int delaySeconds)
    {
        lock (_lock)
        {
            if (_shutdownCts != null) return; // already scheduled

            _shutdownCts = new CancellationTokenSource();
            var token = _shutdownCts.Token;

            _ = Task.Run(async () =>
            {
                if (delaySeconds > 0)
                {
                    _logger.LogWarning("Shutdown in {Delay}s — restore power to cancel", delaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                }

                if (!token.IsCancellationRequested)
                {
                    _logger.LogWarning("Executing graceful shutdown now");
                    ExecuteShutdown();
                }
            }, CancellationToken.None);
        }
    }

    public void CancelShutdown()
    {
        lock (_lock)
        {
            if (_shutdownCts == null) return;
            _logger.LogInformation("Power restored — shutdown cancelled");
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
            _shutdownCts = null;
        }
    }

    private void ExecuteShutdown()
    {
        try
        {
            // /s = shutdown, /t 0 = immediately, /f = force close apps
            System.Diagnostics.Process.Start("shutdown.exe", "/s /t 0 /f");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute shutdown — run as Administrator or check process permissions");
        }
    }
}

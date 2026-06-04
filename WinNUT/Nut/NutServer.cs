using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WinNUT.Config;
using WinNUT.Hid;

namespace WinNUT.Nut;

public sealed class NutServer : IAsyncDisposable
{
    private readonly AgentConfig _config;
    private readonly Func<UpsState> _getState;
    private readonly ILogger<NutServer> _logger;
    private TcpListener? _listener;

    public NutServer(AgentConfig config, Func<UpsState> getState, ILogger<NutServer> logger)
    {
        _config   = config;
        _getState = getState;
        _logger   = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Any, _config.Port);
        _listener.Start();
        _logger.LogInformation("NUT server listening on port {Port}", _config.Port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint;
        _logger.LogDebug("NUT client connected: {Endpoint}", endpoint);

        using (client)
        {
            var session = new NutSession(client, _config, _getState, _logger);
            await session.RunAsync(ct);
        }

        _logger.LogDebug("NUT client disconnected: {Endpoint}", endpoint);
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Stop();
        await Task.CompletedTask;
    }
}

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NutAgent.Config;
using NutAgent.Hid;

namespace NutAgent.Nut;

public sealed class NutServer
{
    // Caps concurrent NUT sessions so a flood of connections can't exhaust threads/sockets —
    // a real install only ever needs a handful (HA, Unraid, a couple of client-mode PCs).
    private const int MaxConcurrentConnections = 20;

    private readonly AgentConfig _config;
    private readonly Func<UpsState> _getState;
    private readonly ILogger<NutServer> _logger;
    private readonly LoginThrottle _loginThrottle = new();
    private readonly SemaphoreSlim _connectionSlots = new(MaxConcurrentConnections, MaxConcurrentConnections);
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
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _logger.LogInformation("NUT server listening on port {Port}", _config.Port);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);

                if (!await _connectionSlots.WaitAsync(0, ct))
                {
                    _logger.LogDebug("NUT connection limit ({Max}) reached — rejecting {Endpoint}",
                        MaxConcurrentConnections, client.Client.RemoteEndPoint);
                    client.Dispose();
                    continue;
                }

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

        try
        {
            using (client)
            {
                var session = new NutSession(client, _config, _getState, _loginThrottle, _logger);
                await session.RunAsync(ct);
            }
        }
        finally
        {
            _connectionSlots.Release();
        }

        _logger.LogDebug("NUT client disconnected: {Endpoint}", endpoint);
    }
}

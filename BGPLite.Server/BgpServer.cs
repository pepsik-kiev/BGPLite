using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Providers;
using BGPLite.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BGPLite.Server;

public sealed class BgpServer : IHostedService, IDisposable
{
    private readonly AppConfig _config;
    private readonly RouteTable _routeTable;
    private readonly IRouteFilter _routeFilter;
    private readonly BgpMetrics _metrics;
    private readonly ILogger<BgpSession> _sessionLogger;
    private readonly ILogger<BgpServer> _logger;
    private readonly Action<string, uint>? _onPeerIdentified;
    private readonly PeerStore? _peerStore;
    private readonly PrefixService? _prefixService;
    private readonly ConcurrentDictionary<string, BgpSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private Socket? _listener;
    private Task? _acceptTask;
    private PeriodicTimer? _statusTimer;
    private Task? _statusTask;

    public BgpMetrics Metrics => _metrics;
    public RouteTable Routes => _routeTable;

    public BgpServer(
        AppConfig config,
        RouteTable routeTable,
        IRouteFilter routeFilter,
        BgpMetrics metrics,
        ILogger<BgpSession> sessionLogger,
        ILogger<BgpServer> logger,
        Action<string, uint>? onPeerIdentified = null,
        PeerStore? peerStore = null,
        PrefixService? prefixService = null)
    {
        _config = config;
        _routeTable = routeTable;
        _routeFilter = routeFilter;
        _metrics = metrics;
        _sessionLogger = sessionLogger;
        _logger = logger;
        _onPeerIdentified = onPeerIdentified;
        _peerStore = peerStore;
        _prefixService = prefixService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(new IPEndPoint(IPAddress.Any, BgpConstants.BgpPort));
        _listener.Listen(16);

        _logger.LogInformation("BGP server listening on port {Port}", BgpConstants.BgpPort);
        _logger.LogInformation("Local ASN={Asn}, RouterId={RouterId}", _config.Bgp.Asn, _config.Bgp.RouterId);

        _acceptTask = AcceptLoopAsync(_cts.Token);

        _statusTimer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        _statusTask = LogStatusLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("BGP server shutting down");
        _cts.Cancel();

        if (_listener is not null)
        {
            _listener.Close();
            _listener = null;
        }

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();

        if (_acceptTask is not null)
        {
            try { await _acceptTask; } catch { }
        }

        _statusTimer?.Dispose();
        if (_statusTask is not null)
        {
            try { await _statusTask; } catch { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var socket = await _listener!.AcceptAsync(cancellationToken);
                var remoteEndpoint = (IPEndPoint)socket.RemoteEndPoint!;
                var peerAddress = remoteEndpoint.Address.ToString();

                _logger.LogInformation("Incoming connection from {Address}", peerAddress);

                var peerConfig = new PeerConfig { Address = peerAddress };

                var session = new BgpSession(
                    socket, peerConfig, _config.Bgp, _routeTable,
                    _routeFilter, _metrics, _sessionLogger,
                    _onPeerIdentified,
                    _peerStore, _prefixService, _config);

                _sessions[peerAddress] = session;

                _ = RunSessionAsync(peerAddress, session, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connection");
            }
        }
    }

    private async Task RunSessionAsync(string peerAddress, BgpSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.RunAsync(cancellationToken);
        }
        finally
        {
            _sessions.TryRemove(peerAddress, out _);
            session.Dispose();
        }
    }

    private async Task LogStatusLoopAsync(CancellationToken cancellationToken)
    {
        while (await _statusTimer!.WaitForNextTickAsync(cancellationToken))
        {
            var peers = string.Join(", ", _sessions.Keys);
            _logger.LogInformation("Active sessions: {Count} [{Peers}]", _sessions.Count, peers);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _listener?.Dispose();
        foreach (var session in _sessions.Values)
            session.Dispose();
    }
}

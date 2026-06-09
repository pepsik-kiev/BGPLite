using System.Buffers;
using System.Net;
using System.Net.Sockets;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Routing;
using Microsoft.Extensions.Logging;

namespace BGPLite.Server;

public sealed class BgpSession : IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly PeerConfig _peerConfig;
    private readonly BgpConfig _bgpConfig;
    private readonly RouteTable _routeTable;
    private readonly IRouteFilter _routeFilter;
    private readonly BgpMetrics _metrics;
    private readonly ILogger<BgpSession> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string, uint>? _onPeerIdentified;
    private readonly IPeerStore? _peerStore;
    private readonly IPrefixService? _prefixService;
    private readonly AppConfig? _appConfig;

    private BgpFsmState _state = BgpFsmState.Idle;
    private uint _remoteAsn;
    private bool _remoteFourByteAsn;
    private bool _localFourByteAsn = true;
    private ushort _negotiatedHoldTime;
    private List<IpPrefix> _advertisedPrefixes = [];
    private TimeSpan _keepAliveInterval;

    public BgpFsmState State => _state;
    public PeerConfig Peer => _peerConfig;
    public bool IsEstablished => _state == BgpFsmState.Established;

    public async Task RefreshRoutesAsync()
    {
        if (!IsEstablished) return;

        await _sendLock.WaitAsync();
        try
        {
            _logger.LogInformation("Refreshing routes for {Peer}", _peerConfig.Address);
            await WithdrawAllAsync();
            await SendAllRoutesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh routes for {Peer}", _peerConfig.Address);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task WithdrawAllAsync()
    {
        if (_advertisedPrefixes.Count == 0) return;

        const int maxPerUpdate = 100;
        for (var i = 0; i < _advertisedPrefixes.Count; i += maxPerUpdate)
        {
            var batch = _advertisedPrefixes.Skip(i).Take(maxPerUpdate).ToList();
            var update = new BgpUpdateMessage
            {
                WithdrawnRoutes = batch,
                PathAttributes = [],
                Nlri = []
            };
            await SendMessageAsync(update);
            _metrics.UpdateSent();
        }

        _logger.LogInformation("Withdrawn {Count} routes from {Peer}", _advertisedPrefixes.Count, _peerConfig.Address);
        _advertisedPrefixes = [];
    }

    public BgpSession(
        Socket socket,
        PeerConfig peerConfig,
        BgpConfig bgpConfig,
        RouteTable routeTable,
        IRouteFilter routeFilter,
        BgpMetrics metrics,
        ILogger<BgpSession> logger,
        Action<string, uint>? onPeerIdentified = null,
        IPeerStore? peerStore = null,
        IPrefixService? prefixService = null,
        AppConfig? appConfig = null)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _peerConfig = peerConfig;
        _bgpConfig = bgpConfig;
        _routeTable = routeTable;
        _routeFilter = routeFilter;
        _metrics = metrics;
        _logger = logger;
        _onPeerIdentified = onPeerIdentified;
        _peerStore = peerStore;
        _prefixService = prefixService;
        _appConfig = appConfig;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        try
        {
            TransitionTo(BgpFsmState.Connect);
            _metrics.PeerConnected();
            _logger.LogInformation("PeerConnected {Peer}", _peerConfig.Address);

            // Receive OPEN
            var openMessage = await ReceiveMessageAsync(linkedCts.Token);
            if (openMessage is not BgpOpenMessage remoteOpen)
            {
                await SendNotificationAsync(BgpConstants.Error.OpenMessageError, BgpConstants.SubError.Unspecific);
                return;
            }

            _logger.LogInformation("OpenReceived from {Peer} ASN={Asn} Capabilities=[{Caps}]",
                _peerConfig.Address, remoteOpen.Asn,
                string.Join(", ", remoteOpen.Capabilities.Select(c => c.Data.Length > 0
                    ? $"{c.Code}[{Convert.ToHexString(c.Data)}]"
                    : $"{c.Code}")));

            ValidateOpen(remoteOpen);

            TransitionTo(BgpFsmState.OpenSent);

            // Send our OPEN — adapt capabilities to peer
            await SendOpenAsync(remoteOpen);
            _logger.LogInformation("OpenSent to {Peer}", _peerConfig.Address);

            // Send KEEPALIVE (acknowledge OPEN)
            await SendKeepaliveAsync();
            _logger.LogDebug("KeepAliveSent to {Peer} (OPEN confirm)", _peerConfig.Address);

            TransitionTo(BgpFsmState.OpenConfirm);

            // Receive KEEPALIVE
            var response = await ReceiveMessageAsync(linkedCts.Token);
            _logger.LogInformation("Received {Type} from {Peer} in OpenConfirm", response.Type, _peerConfig.Address);

            switch (response)
            {
                case BgpKeepaliveMessage:
                    break;
                case BgpNotificationMessage notif:
                    var dataHex = notif.Data is { Length: > 0 }
                        ? Convert.ToHexString(notif.Data)
                        : "(no data)";
                    _logger.LogWarning(
                        "Peer {Peer} sent NOTIFICATION Error={Error} SubError={SubError} Data={Data}",
                        _peerConfig.Address, notif.ErrorCode, notif.SubErrorCode, dataHex);
                    return;
                default:
                    _logger.LogError("Unexpected message {Type} from {Peer} in OpenConfirm", response.Type, _peerConfig.Address);
                    await SendNotificationAsync(BgpConstants.Error.FiniteStateMachineError, BgpConstants.SubError.Unspecific);
                    return;
            }

            _logger.LogDebug("KeepAliveReceived from {Peer}", _peerConfig.Address);

            TransitionTo(BgpFsmState.Established);
            _metrics.SessionEstablished();
            _logger.LogInformation("SessionEstablished with {Peer} ASN={Asn}", _peerConfig.Address, _remoteAsn);

            // Send initial routes
            await SendAllRoutesAsync();

            // Run main loop: read messages + send keepalives
            await RunEstablishedAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SessionClosed (cancelled) with {Peer}", _peerConfig.Address);
        }
        catch (BgpParseException ex)
        {
            _logger.LogError(ex, "Parse error from {Peer}", _peerConfig.Address);
            await SendNotificationAsync(BgpConstants.Error.MessageHeaderError, BgpConstants.SubError.Unspecific);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session error with {Peer}", _peerConfig.Address);
        }
        finally
        {
            TransitionTo(BgpFsmState.Idle);
            _metrics.SessionClosed();
            _metrics.PeerDisconnected();
            _logger.LogInformation("SessionClosed with {Peer}", _peerConfig.Address);
        }
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private async Task RunEstablishedAsync(CancellationToken cancellationToken)
    {
        using var keepaliveTimer = new PeriodicTimer(_keepAliveInterval);

        var readTask = ReadLoopAsync(cancellationToken);
        var keepaliveTask = KeepAliveLoopAsync(keepaliveTimer, cancellationToken);

        await Task.WhenAny(readTask, keepaliveTask);
        _cts.Cancel();

        try { await readTask; } catch { }
        try { await keepaliveTask; } catch { }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(cancellationToken);

            switch (message)
            {
                case BgpUpdateMessage update:
                    _metrics.UpdateReceived();
                    await HandleUpdateAsync(update);
                    break;
                case BgpKeepaliveMessage:
                    _logger.LogDebug("KeepAliveReceived from {Peer}", _peerConfig.Address);
                    break;
                case BgpNotificationMessage notif:
                    _logger.LogWarning("NotificationReceived from {Peer}: {Error}/{SubError}",
                        _peerConfig.Address, notif.ErrorCode, notif.SubErrorCode);
                    return;
            }
        }
    }

    private async Task KeepAliveLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await SendKeepaliveAsync();
            _logger.LogDebug("KeepAliveSent to {Peer}", _peerConfig.Address);
        }
    }

    private async Task HandleUpdateAsync(BgpUpdateMessage update)
    {
        _logger.LogInformation("UpdateReceived from {Peer}: {Withdrawn} withdrawn, {Nlri} announced",
            _peerConfig.Address, update.WithdrawnRoutes.Count, update.Nlri.Count);

        // Process withdrawals
        foreach (var w in update.WithdrawnRoutes)
        {
            _routeTable.Remove(w.Address, w.Length);
            _logger.LogDebug("Route withdrawn: {Prefix}", w);
        }

        // Process announcements
        if (update.Nlri.Count > 0)
        {
            var origin = BgpOrigin.Incomplete;
            uint nextHop = 0;
            uint[] asPath = [];
            uint[] communities = [];

            foreach (var attr in update.PathAttributes)
            {
                switch (attr.TypeCode)
                {
                    case BgpConstants.Attribute.Origin:
                        origin = AttributeHelper.ReadOrigin(attr);
                        break;
                    case BgpConstants.Attribute.AsPath:
                        asPath = AttributeHelper.ReadAsPath(attr, _remoteFourByteAsn);
                        break;
                    case BgpConstants.Attribute.NextHop:
                        nextHop = AttributeHelper.ReadNextHop(attr);
                        break;
                    case BgpConstants.Attribute.Community:
                        communities = AttributeHelper.ReadCommunities(attr);
                        break;
                }
            }

            foreach (var nlri in update.Nlri)
            {
                var route = new Route
                {
                    Prefix = nlri.Address,
                    PrefixLength = nlri.Length,
                    NextHop = nextHop,
                    AsPath = asPath,
                    Communities = communities
                };

                if (_routeFilter.AcceptIncoming(route, _peerConfig))
                {
                    _routeTable.AddOrUpdate(route);
                    _logger.LogDebug("Route added: {Prefix} via {NextHop}", nlri, BgpConstants.UintToIPAddress(nextHop));
                }
            }
        }

        _metrics.SetRouteCount(_routeTable.Count);
    }

    private async Task SendAllRoutesAsync()
    {
        var nextHop = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());
        var routes = new List<Route>();

        if (_peerStore is not null && _prefixService is not null && _appConfig is not null)
        {
            var peer = _peerStore.GetPeerByIp(_peerConfig.Address);
            if (peer is not null)
            {
                _peerStore.UpdateSessionStatus(_peerConfig.Address, true);

                var subscriptionIds = _peerStore.GetSubscriptions(peer.Id);
                _logger.LogInformation("Peer {Peer} subscriptions: [{Subs}]", _peerConfig.Address, string.Join(", ", subscriptionIds));

                var subscribedLists = _appConfig?.RipeStat?.AsnLists
                    .Where(l => subscriptionIds.Contains(l.Name))
                    .ToList() ?? [];

                // ASN-based lists
                var asns = subscribedLists
                    .Where(l => l.Asns.Count > 0)
                    .SelectMany(l => l.Asns)
                    .ToList();

                _logger.LogInformation("Peer {Peer} resolved {Count} ASNs from subscriptions", _peerConfig.Address, asns.Count);

                if (asns.Count > 0)
                {
                    try
                    {
                        var prefixes = await _prefixService.GetPrefixesForAsns(asns);
                        foreach (var (prefix, length, asn) in prefixes)
                        {
                            routes.Add(new Route
                            {
                                Prefix = prefix,
                                PrefixLength = length,
                                NextHop = nextHop,
                                AsPath = [asn]
                            });
                        }
                        _logger.LogInformation("Fetched {Count} prefixes for {Peer} from ASN subscriptions",
                            routes.Count, _peerConfig.Address);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch prefixes for {Peer}", _peerConfig.Address);
                    }
                }

                // Country-based lists (e.g. RU with no ASNs → use local nets.txt)
                if (subscribedLists.Any(l => l.Asns.Count == 0 && l.Country is not null))
                {
                    try
                    {
                        var ruPrefixes = await _prefixService.GetRuPrefixesAsync();
                        foreach (var (prefix, length, _) in ruPrefixes)
                        {
                            routes.Add(new Route
                            {
                                Prefix = prefix,
                                PrefixLength = length,
                                NextHop = nextHop
                            });
                        }
                        _logger.LogInformation("Fetched {Count} RU prefixes for {Peer}", ruPrefixes.Count, _peerConfig.Address);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", _peerConfig.Address);
                    }
                }

                // Add custom prefixes
                var customPrefixes = _peerStore.GetCustomPrefixes(peer.Id);
                _logger.LogInformation("Peer {Peer} has {SubRoutes} subscription routes + {CustomCount} custom prefixes",
                    _peerConfig.Address, routes.Count, customPrefixes.Count);

                foreach (var cidr in customPrefixes)
                {
                    var slash = cidr.IndexOf('/');
                    var ip = IPAddress.Parse(cidr[..slash]);
                    var length = byte.Parse(cidr[(slash + 1)..]);
                    var prefix = BgpConstants.IPAddressToUint(ip);
                    routes.Add(new Route
                    {
                        Prefix = prefix,
                        PrefixLength = length,
                        NextHop = nextHop
                    });
                }

                _logger.LogInformation("Sending {Count} total routes to {Peer}", routes.Count, _peerConfig.Address);

                if (routes.Count > 0)
                {
                    await SendRoutesAsync(nextHop, routes);
                    return;
                }
            }
            else
            {
                // Unknown peer — auto-register and send default RU list
                _logger.LogInformation("Unknown peer {Ip}, auto-registering with RU defaults", _peerConfig.Address);

                _peerStore.CreatePeer(_peerConfig.Address, _remoteAsn, null);

                var ruAsns = _appConfig.RipeStat?.AsnLists
                    .Where(l => l.Country == "RU")
                    .SelectMany(l => l.Asns)
                    .ToList();

                if (ruAsns is { Count: > 0 })
                {
                    try
                    {
                        var prefixes = await _prefixService.GetPrefixesForAsns(ruAsns);
                        foreach (var (prefix, length, asn) in prefixes)
                        {
                            routes.Add(new Route
                            {
                                Prefix = prefix,
                                PrefixLength = length,
                                NextHop = nextHop,
                                AsPath = [asn]
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to fetch RU prefixes for {Peer}", _peerConfig.Address);
                    }
                }

                if (routes.Count > 0)
                {
                    await SendRoutesAsync(nextHop, routes);
                    return;
                }
            }
        }

        // Final fallback: send from shared route table
        var tableRoutes = _routeTable.GetAll();
        if (tableRoutes.Count == 0) return;

        var filtered = tableRoutes.Where(r => _routeFilter.AcceptOutgoing(r, _peerConfig)).ToList();
        await SendRoutesAsync(nextHop, filtered);
    }

    private async Task SendRoutesAsync(uint nextHop, List<Route> routes)
    {
        const int maxNlriPerUpdate = 100;
        var sent = 0;
        var batch = new List<Route>(maxNlriPerUpdate);

        foreach (var route in routes)
        {
            batch.Add(route);
            if (batch.Count >= maxNlriPerUpdate)
            {
                await SendRouteBatchAsync(nextHop, batch);
                sent += batch.Count;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await SendRouteBatchAsync(nextHop, batch);
            sent += batch.Count;
        }

        _advertisedPrefixes.AddRange(routes.Select(r => new IpPrefix(r.Prefix, r.PrefixLength)));
        _logger.LogInformation("UpdateSent {Count} routes to {Peer}", sent, _peerConfig.Address);
    }

    private async Task SendRouteBatchAsync(uint nextHop, List<Route> routes)
    {
        // Collect all unique communities from batch
        var allCommunities = routes
            .SelectMany(r => r.Communities)
            .Distinct()
            .ToArray();

        var attrs = new List<PathAttribute>
        {
            AttributeHelper.WriteOrigin(BgpOrigin.Igp),
            AttributeHelper.WriteAsPath([_bgpConfig.Asn], _localFourByteAsn),
            AttributeHelper.WriteNextHop(nextHop)
        };

        if (allCommunities.Length > 0)
            attrs.Add(AttributeHelper.WriteCommunities(allCommunities));

        var update = new BgpUpdateMessage
        {
            PathAttributes = attrs,
            Nlri = routes.Select(r => new IpPrefix(r.Prefix, r.PrefixLength)).ToList()
        };

        await SendMessageAsync(update);
        _metrics.UpdateSent();
    }

    private async Task SendUpdateBatchAsync(List<PathAttribute> attrs, List<IpPrefix> nlri)
    {
        var update = new BgpUpdateMessage
        {
            PathAttributes = attrs,
            Nlri = nlri
        };

        await SendMessageAsync(update);
        _metrics.UpdateSent();
    }

    #region Message I/O

    private async Task<BgpMessage> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var headerBuffer = ArrayPool<byte>.Shared.Rent(BgpConstants.MessageHeaderSize);
        try
        {
            await ReadExactAsync(headerBuffer.AsMemory(0, BgpConstants.MessageHeaderSize), cancellationToken);

            var length = BgpMessageReader.GetMessageLength(headerBuffer);
            if (length < 0)
                throw new BgpParseException("Incomplete message header");

            var payloadSize = length - BgpConstants.MessageHeaderSize;
            var messageBuffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                Array.Copy(headerBuffer, messageBuffer, BgpConstants.MessageHeaderSize);

                if (payloadSize > 0)
                    await ReadExactAsync(messageBuffer.AsMemory(BgpConstants.MessageHeaderSize, payloadSize), cancellationToken);

                return BgpMessageReader.ReadMessage(messageBuffer.AsSpan(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(messageBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private async Task SendMessageAsync(BgpMessage message)
    {
        var bufferSize = BgpMessageWriter.GetBufferSize(message);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            var written = BgpMessageWriter.WriteMessage(message, buffer);
            await _stream.WriteAsync(buffer.AsMemory(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
                throw new IOException("Connection closed by peer");
            totalRead += read;
        }
    }

    #endregion

    #region BGP Messages

    private async Task SendOpenAsync(BgpOpenMessage remoteOpen)
    {
        var capabilities = new List<BgpCapabilityInfo>
        {
            BgpCapabilityInfo.FourOctetAsn(_bgpConfig.Asn)
        };

        // Only advertise MP IPv4/Unicast if peer specifically supports IPv4/Unicast
        if (PeerHasMpIpv4Unicast(remoteOpen.Capabilities))
            capabilities.Add(BgpCapabilityInfo.MultiprotocolIpv4Unicast());

        // Only advertise Route Refresh if peer also supports it
        if (remoteOpen.Capabilities.Any(c => c.Code == BgpConstants.Capability.RouteRefresh))
            capabilities.Add(BgpCapabilityInfo.RouteRefresh());

        var asn16 = _bgpConfig.Asn > ushort.MaxValue ? (ushort)23456 : (ushort)_bgpConfig.Asn;
        var routerId = BgpConstants.IPAddressToUint(_bgpConfig.GetRouterIdAddress());

        _logger.LogInformation(
            "Sending OPEN: ASN={Asn} RouterId={RouterId} Capabilities=[{Caps}]",
            asn16, BgpConstants.UintToIPAddress(routerId),
            string.Join(", ", capabilities.Select(c => $"Code={c.Code}")));

        var open = new BgpOpenMessage
        {
            Version = BgpConstants.BgpVersion,
            Asn = asn16,
            HoldTime = (ushort)_bgpConfig.HoldTime,
            RouterId = routerId,
            Capabilities = capabilities
        };

        await SendMessageAsync(open);
    }

    private static bool PeerHasMpIpv4Unicast(List<BgpCapabilityInfo> caps)
    {
        foreach (var cap in caps)
        {
            if (cap.Code != BgpConstants.Capability.Multiprotocol || cap.Data.Length < 4) continue;
            var afi = (ushort)((cap.Data[0] << 8) | cap.Data[1]);
            var safi = cap.Data[3];
            if (afi == BgpConstants.Afi.IPv4 && safi == BgpConstants.Safi.Unicast)
                return true;
        }
        return false;
    }

    private Task SendKeepaliveAsync() => SendMessageAsync(BgpKeepaliveMessage.Instance);

    private async Task SendNotificationAsync(byte errorCode, byte subErrorCode)
    {
        var notification = new BgpNotificationMessage { ErrorCode = errorCode, SubErrorCode = subErrorCode };
        await SendMessageAsync(notification);
        _logger.LogInformation("NotificationSent to {Peer}: {Error}/{SubError}", _peerConfig.Address, errorCode, subErrorCode);
    }

    #endregion

    #region Validation

    private void ValidateOpen(BgpOpenMessage open)
    {
        if (open.Version != BgpConstants.BgpVersion)
            throw new BgpParseException($"Unsupported BGP version: {open.Version}");

        _remoteFourByteAsn = CapabilityHelper.GetRemoteAsn(open).HasValue;
        _remoteAsn = CapabilityHelper.GetEffectiveAsn(open);

        _onPeerIdentified?.Invoke(_peerConfig.Address, _remoteAsn);

        if (_peerConfig.RemoteAsn.HasValue && _remoteAsn != _peerConfig.RemoteAsn.Value)
            throw new BgpParseException($"Unexpected ASN: expected {_peerConfig.RemoteAsn}, got {_remoteAsn}");

        var holdTime = open.HoldTime;
        if (holdTime != 0 && holdTime < 3)
            throw new BgpParseException($"Unacceptable hold time: {holdTime}");

        _negotiatedHoldTime = holdTime;
        _keepAliveInterval = holdTime == 0
            ? TimeSpan.FromSeconds(_bgpConfig.KeepAlive)
            : TimeSpan.FromSeconds(Math.Max(holdTime / 3, 1));
    }

    #endregion

    private void TransitionTo(BgpFsmState newState)
    {
        _logger.LogDebug("FSM: {Old} → {New} for {Peer}", _state, newState, _peerConfig.Address);
        _state = newState;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream.Dispose();
        _socket.Dispose();
        _cts.Dispose();
    }
}

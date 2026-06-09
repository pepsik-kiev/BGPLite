using System.Net;
using System.Text;
using System.Text.Json;
using BGPLite.Api.Entities;
using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BGPLite.Api;

public sealed class ManagementApi : IHostedService, IDisposable
{
    private readonly PeerStore _store;
    private readonly RouteTable _routeTable;
    private readonly AppConfig _config;
    private readonly BgpMetrics _metrics;
    private readonly PrefixService? _prefixService;
    private readonly ILogger<ManagementApi> _logger;
    private HttpListener? _listener;
    private Task? _listenTask;
    private CancellationTokenSource _cts = new();

    public ManagementApi(
        PeerStore store,
        RouteTable routeTable,
        AppConfig config,
        BgpMetrics metrics,
        ILogger<ManagementApi> logger,
        PrefixService? prefixService = null)
    {
        _store = store;
        _routeTable = routeTable;
        _config = config;
        _metrics = metrics;
        _prefixService = prefixService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://+:5000/");
        _listener.Start();

        _logger.LogInformation("Management API listening on http://+:5000/");
        _listenTask = ListenAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener?.Stop();
        if (_listenTask is not null)
        {
            try { await _listenTask; } catch { }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener!.GetContextAsync();
                if (ct.IsCancellationRequested) break;
                _ = HandleAsync(ctx);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Management API error");
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var method = ctx.Request.HttpMethod;

        try
        {
            ApiResponse response;

            if (method == "GET" && path == "/api/my-ip")
            {
                var clientIp = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
                response = ApiResponse.Ok(new { ip = clientIp });
            }
            else if (method == "GET" && path == "/api/asn-lists")
                response = await HandleGetAsnListsAsync();
            else if (method == "GET" && path == "/api/sessions")
                response = HandleGetSessions();
            else if (method == "POST" && path == "/api/peers")
                response = await HandleCreatePeer(ctx);
            else if (method == "GET" && path == "/api/peers")
                response = HandleGetPeers();
            else if (method == "GET" && path == "/api/routes/count")
                response = HandleGetRouteCount();
            else if (method == "GET" && path.StartsWith("/api/peer/") && path.EndsWith("/communities"))
                response = HandleGetPeerCommunities(ExtractPeerIp(path));
            else if (method == "PUT" && path.StartsWith("/api/peer/") && path.EndsWith("/communities"))
                response = await HandleSetPeerCommunities(ExtractPeerIp(path), ctx);
            else if (method == "DELETE" && path.StartsWith("/api/peer/") && path.EndsWith("/communities"))
                response = HandleDeletePeerCommunities(ExtractPeerIp(path));
            else if (method == "PUT" && path.StartsWith("/api/peer/") && path.EndsWith("/description"))
                response = await HandleSetPeerDescription(ExtractPeerIp(path), ctx);
            else
                response = ApiResponse.Error("Not found", 404);

            await WriteResponse(ctx, response);
        }
        catch (Exception ex)
        {
            await WriteResponse(ctx, ApiResponse.Error(ex.Message, 500));
        }
    }

    private async Task<ApiResponse> HandleGetAsnListsAsync()
    {
        var lists = _config.RipeStat?.AsnLists ?? [];
        var result = new List<object>();

        foreach (var l in lists)
        {
            int prefixCount = 0;
            if (_prefixService is not null)
            {
                foreach (var asn in l.Asns)
                {
                    try { prefixCount += await _prefixService.GetPrefixCountAsync(asn); }
                    catch { }
                }
            }

            result.Add(new
            {
                id = l.Id,
                l.Name,
                l.Description,
                l.Country,
                prefixCount,
                type = l.Country is not null ? "country" : "asn"
            });
        }

        return ApiResponse.Ok(result);
    }

    private ApiResponse HandleGetSessions()
    {
        return ApiResponse.Ok(new
        {
            active = _metrics.ActiveSessions
        });
    }

    private static string ExtractPeerIp(string path)
    {
        // /api/peer/{ip}/communities → segments: ["", "api", "peer", "{ip}", ...]
        return path.Split('/')[3];
    }

    private ApiResponse HandleGetPeers()
    {
        var peers = _store.GetAllPeers();
        return ApiResponse.Ok(peers.Select(p => new
        {
            id = p.Id,
            ip = p.Ip,
            asn = p.Asn,
            description = p.Description,
            status = p.Status,
            createdAt = p.CreatedAt,
            lastSessionAt = p.LastSessionAt,
            communities = p.Communities.Select(c => CommunityToString((uint)c.Community)),
            allRoutes = p.Communities.Count == 0
        }));
    }

    private ApiResponse HandleGetPeerCommunities(string peerIp)
    {
        var communities = _store.GetCommunitiesByIp(peerIp);
        return ApiResponse.Ok(new
        {
            ip = peerIp,
            communities = communities.Select(CommunityToString),
            allRoutes = communities.Count == 0
        });
    }

    private async Task<ApiResponse> HandleSetPeerCommunities(string peerIp, HttpListenerContext ctx)
    {
        var peer = _store.GetPeerByIp(peerIp);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<SetCommunitiesRequest>(body);

        if (data?.Communities is null)
            return ApiResponse.Error("Invalid request body", 400);

        var communities = new HashSet<uint>();
        foreach (var c in data.Communities)
            communities.Add(ParseCommunity(c));

        _store.SetCommunities(peer.Id, communities);

        _logger.LogInformation("Updated communities for {Peer}: {Communities}",
            peerIp, string.Join(", ", communities.Select(CommunityToString)));

        return ApiResponse.Ok(new { ip = peerIp, communities = communities.Select(CommunityToString) });
    }

    private async Task<ApiResponse> HandleSetPeerDescription(string peerIp, HttpListenerContext ctx)
    {
        var peer = _store.GetPeerByIp(peerIp);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<SetDescriptionRequest>(body);

        if (data?.Description is null)
            return ApiResponse.Error("Invalid request body", 400);

        _store.SetDescription(peer.Id, data.Description);

        _logger.LogInformation("Updated description for {Peer}: {Desc}", peerIp, data.Description);

        return ApiResponse.Ok(new { ip = peerIp, description = data.Description });
    }

    private ApiResponse HandleDeletePeerCommunities(string peerIp)
    {
        var peer = _store.GetPeerByIp(peerIp);
        if (peer is null)
            return ApiResponse.Error("Peer not found", 404);

        _store.ClearCommunities(peer.Id);

        _logger.LogInformation("Removed community filter for {Peer}", peerIp);
        return ApiResponse.Ok(new { ip = peerIp, allRoutes = true });
    }

    private ApiResponse HandleGetRouteCount()
    {
        var routes = _routeTable.GetAll();
        var byCommunity = routes
            .SelectMany(r => r.Communities.Length == 0
                ? [(community: 0u, route: r)]
                : r.Communities.Select(c => (community: c, route: r)))
            .GroupBy(x => x.community)
            .ToDictionary(g => g.Key == 0 ? "default" : CommunityToString(g.Key), g => g.Count());

        return ApiResponse.Ok(new { total = routes.Count, byCommunity });
    }

    private async Task<ApiResponse> HandleCreatePeer(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<CreatePeerRequest>(body);

        if (data is null)
            return ApiResponse.Error("Invalid request body", 400);

        var asnLists = data.AsnLists ?? [];
        var customPrefixes = new List<(string Prefix, byte Length)>();

        if (data.CustomPrefixes is not null)
        {
            foreach (var cidr in data.CustomPrefixes)
            {
                var slash = cidr.IndexOf('/');
                if (slash < 0)
                    return ApiResponse.Error($"Invalid CIDR: {cidr}", 400);
                var prefix = cidr[..slash];
                var length = byte.Parse(cidr[(slash + 1)..]);
                customPrefixes.Add((prefix, length));
            }
        }

        var id = _store.CreatePeer(data.Ip, data.Asn, data.Description);
        if (asnLists.Count > 0)
            _store.SetSubscriptions(id, asnLists);
        if (customPrefixes.Count > 0)
            _store.SetCustomPrefixes(id, customPrefixes);

        var peer = _store.GetPeerById(id);

        _logger.LogInformation("Created peer {Ip} AS{Asn} ({Id}): {Subs} lists, {Prefixes} custom prefixes",
            data.Ip, data.Asn, id, asnLists.Count, customPrefixes.Count);

        return ApiResponse.Ok(new
        {
            id,
            ip = data.Ip,
            asn = data.Asn,
            description = data.Description,
            status = peer?.Status ?? "inactive",
            createdAt = peer?.CreatedAt,
            asnLists,
            customPrefixes = data.CustomPrefixes ?? []
        });
    }

    private static uint ParseCommunity(string community)
    {
        var colon = community.IndexOf(':');
        var asn = uint.Parse(community[..colon]);
        var value = uint.Parse(community[(colon + 1)..]);
        return (asn << 16) | (value & 0xFFFF);
    }

    private static string CommunityToString(uint community)
    {
        var asn = community >> 16;
        var value = community & 0xFFFF;
        return $"{asn}:{value}";
    }

    private static async Task WriteResponse(HttpListenerContext ctx, ApiResponse response)
    {
        ctx.Response.StatusCode = response.StatusCode;
        ctx.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(response.Body);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _listener?.Close();
    }

    private record SetCommunitiesRequest(List<string> Communities);
    private record SetDescriptionRequest(string Description);
    private record CreatePeerRequest(string Ip, uint Asn, string? Description, List<string>? AsnLists, List<string>? CustomPrefixes);

    private record ApiResponse(object? Body, int StatusCode = 200)
    {
        public static ApiResponse Ok(object data) => new(data);
        public static ApiResponse Error(string message, int code) => new(new { error = message }, code);
    }
}

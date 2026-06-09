using System.Net;
using BGPLite.Api;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Providers;
using BGPLite.Routing;
using BGPLite.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

var baseDir = AppContext.BaseDirectory;
var dataDir = Environment.GetEnvironmentVariable("BGPLITE_DATA") ?? Path.Combine(baseDir, "data");

var configPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(baseDir, "appsettings.yml");
var config = ConfigLoader.Load(configPath);

var routeTable = new RouteTable();
var nextHop = BgpConstants.IPAddressToUint(config.Bgp.GetRouterIdAddress());

// Load default nets.txt (no community)
var netsPath = Path.Combine(baseDir, "nets.txt");
if (File.Exists(netsPath))
{
    var count = LoadPrefixes(netsPath, nextHop, [], routeTable);
    Console.WriteLine($"Loaded {count} prefixes from nets.txt");
}
else
{
    Console.WriteLine($"nets.txt not found at {netsPath}");
}

// Load community files from communities/ directory
var communitiesDir = Path.Combine(baseDir, "communities");
if (Directory.Exists(communitiesDir))
{
    foreach (var file in Directory.GetFiles(communitiesDir, "*.txt"))
    {
        var fileName = Path.GetFileNameWithoutExtension(file);
        var underscore = fileName.IndexOf('_');
        if (underscore < 0) continue;

        var asn = uint.Parse(fileName[..underscore]);
        var value = uint.Parse(fileName[(underscore + 1)..]);
        var community = (asn << 16) | (value & 0xFFFF);
        var communities = new uint[] { community };

        var count = LoadPrefixes(file, nextHop, communities, routeTable);
        Console.WriteLine($"Loaded {count} prefixes with community {asn}:{value} from {fileName}.txt");
    }
}

// SQLite peer store
var dbPath = Path.Combine(dataDir, "bgplite.db");
Console.WriteLine($"Peer database: {dbPath}");

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(config.Bgp);
builder.Services.AddSingleton(routeTable);
builder.Services.AddSingleton(new BgpDbContext(dbPath));
builder.Services.AddSingleton<PeerStore>();
builder.Services.AddSingleton<IRouteFilter>(sp =>
{
    var store = sp.GetRequiredService<PeerStore>();
    return new PeerCommunityFilter(ip => store.GetCommunitiesByIp(ip));
});
builder.Services.AddSingleton(new BgpMetrics());

// RIPE Stat prefix provider
if (config.RipeStat is { AsnLists.Count: > 0 })
{
    builder.Services.AddHttpClient<RipeStatProvider>(c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddSingleton<PrefixService>();
}

builder.Services.AddHostedService(sp =>
{
    var store = sp.GetRequiredService<PeerStore>();
    PrefixService? prefixService = null;
    try { prefixService = sp.GetRequiredService<PrefixService>(); } catch { }

    return new BgpServer(
        sp.GetRequiredService<AppConfig>(),
        sp.GetRequiredService<RouteTable>(),
        sp.GetRequiredService<IRouteFilter>(),
        sp.GetRequiredService<BgpMetrics>(),
        sp.GetRequiredService<ILogger<BgpSession>>(),
        sp.GetRequiredService<ILogger<BgpServer>>(),
        (ip, asn) => store.UpsertPeer(ip, asn),
        store,
        prefixService);
});
builder.Services.AddHostedService<ManagementApi>();

if (config.RipeStat is { AsnLists.Count: > 0 })
{
    foreach (var list in config.RipeStat.AsnLists)
        Console.WriteLine($"  {list.Name}: {list.Description} ({list.Asns.Count} ASNs)");
}

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("BGPLite starting — ASN={Asn}, RouterId={RouterId}", config.Bgp.Asn, config.Bgp.RouterId);
logger.LogInformation("Loaded routes: {RouteCount}", routeTable.Count);

await host.RunAsync();
return;

static int LoadPrefixes(string path, uint nextHop, uint[] communities, RouteTable routeTable)
{
    var count = 0;
    foreach (var line in File.ReadLines(path))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

        var slash = trimmed.IndexOf('/');
        var ip = IPAddress.Parse(trimmed[..slash]);
        var length = byte.Parse(trimmed[(slash + 1)..]);
        var prefix = BgpConstants.IPAddressToUint(ip);

        routeTable.AddOrUpdate(new Route
        {
            Prefix = prefix,
            PrefixLength = length,
            NextHop = nextHop,
            Communities = communities
        });
        count++;
    }
    return count;
}

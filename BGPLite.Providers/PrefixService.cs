using System.Collections.Concurrent;
using BGPLite.Configuration;
using BGPLite.Server;

namespace BGPLite.Providers;

public sealed class PrefixService : IPrefixService
{
    private readonly RipeStatProvider? _ripeStat;
    private readonly IPrefixSourceService _prefixSources;
    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<uint, (IReadOnlyList<(uint Prefix, byte Length)> Data, DateTime CachedAt)> _cache = new();
    private readonly TimeSpan _cacheTtl;

    public PrefixService(AppConfig config, RipeStatProvider? ripeStat, IPrefixSourceService prefixSources, TimeSpan? cacheTtl = null)
    {
        _config = config;
        _ripeStat = ripeStat;
        _prefixSources = prefixSources;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(1);
    }

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn)
    {
        if (_cache.TryGetValue(asn, out var cached) && DateTime.UtcNow - cached.CachedAt < _cacheTtl)
            return cached.Data;

        if (_ripeStat is null) return [];

        var prefixes = await _ripeStat.GetPrefixesAsync(asn);
        _cache[asn] = (prefixes, DateTime.UtcNow);
        return prefixes;
    }

    public async Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns)
    {
        var result = new List<(uint Prefix, byte Length, uint Asn)>();
        foreach (var asn in asns)
        {
            try
            {
                var prefixes = await GetPrefixesAsync(asn);
                foreach (var (prefix, length) in prefixes)
                    result.Add((prefix, length, asn));
            }
            catch
            {
                // skip failed ASN, continue with others
            }
        }
        return result;
    }

    public async Task<int> GetPrefixCountAsync(uint asn)
    {
        var prefixes = await GetPrefixesAsync(asn);
        return prefixes.Count;
    }

    /// <summary>The RU/default prefix set — now backed by the configured default prefix source.</summary>
    public async Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync()
    {
        var prefixes = await _prefixSources.GetDefaultAsync();
        return prefixes.Select(p => (p.Prefix, p.Length, 0u)).ToList();
    }

    public async Task WarmUpAsync()
    {
        var lists = _config.RipeStat?.AsnLists ?? [];

        var allAsns = lists.SelectMany(l => l.Asns).Distinct().ToList();
        foreach (var asn in allAsns)
        {
            try
            {
                await GetPrefixesAsync(asn);
                Console.WriteLine($"  WarmUp: AS{asn} cached");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WarmUp: AS{asn} failed — {ex.Message}");
            }
        }

        // Pre-load all configured prefix sources (file/HTTP/...) into the in-memory cache.
        await _prefixSources.WarmUpAsync();
    }
}

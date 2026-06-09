using System.Collections.Concurrent;

namespace BGPLite.Providers;

public sealed class PrefixService
{
    private readonly RipeStatProvider _ripeStat;
    private readonly ConcurrentDictionary<uint, (IReadOnlyList<(uint Prefix, byte Length)> Data, DateTime CachedAt)> _cache = new();
    private readonly TimeSpan _cacheTtl;

    public PrefixService(RipeStatProvider ripeStat, TimeSpan? cacheTtl = null)
    {
        _ripeStat = ripeStat;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(1);
    }

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn)
    {
        if (_cache.TryGetValue(asn, out var cached) && DateTime.UtcNow - cached.CachedAt < _cacheTtl)
            return cached.Data;

        var prefixes = await _ripeStat.GetPrefixesAsync(asn);
        _cache[asn] = (prefixes, DateTime.UtcNow);
        return prefixes;
    }

    public async Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns)
    {
        var result = new List<(uint Prefix, byte Length, uint Asn)>();
        foreach (var asn in asns)
        {
            var prefixes = await GetPrefixesAsync(asn);
            foreach (var (prefix, length) in prefixes)
                result.Add((prefix, length, asn));
        }
        return result;
    }

    public async Task<int> GetPrefixCountAsync(uint asn)
    {
        var prefixes = await GetPrefixesAsync(asn);
        return prefixes.Count;
    }
}

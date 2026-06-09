using System.Collections.Concurrent;
using System.Net;
using BGPLite.Configuration;
using BGPLite.Protocol;
using BGPLite.Server;

namespace BGPLite.Providers;

public sealed class PrefixService : IPrefixService
{
    private readonly RipeStatProvider? _ripeStat;
    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<uint, (IReadOnlyList<(uint Prefix, byte Length)> Data, DateTime CachedAt)> _cache = new();
    private readonly TimeSpan _cacheTtl;
    private IReadOnlyList<(uint Prefix, byte Length)>? _localPrefixes;

    public PrefixService(AppConfig config, string? netsPath = null, RipeStatProvider? ripeStat = null, TimeSpan? cacheTtl = null)
    {
        _config = config;
        _ripeStat = ripeStat;
        _cacheTtl = cacheTtl ?? TimeSpan.FromHours(1);

        if (netsPath is not null && File.Exists(netsPath))
            _localPrefixes = LoadFromFile(netsPath);
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

    public Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync()
    {
        if (_localPrefixes is not null)
            return Task.FromResult(_localPrefixes.Select(p => (p.Prefix, p.Length, 0u)).ToList());

        // Fallback: если нет локального файла, попробуем RIPE
        if (_ripeStat is null) return Task.FromResult(new List<(uint, byte, uint)>());

        var ruAsns = _config.RipeStat?.AsnLists
            .Where(l => l.Country == "RU")
            .SelectMany(l => l.Asns)
            .ToList();

        if (ruAsns is null or { Count: 0 }) return Task.FromResult(new List<(uint, byte, uint)>());
        return GetPrefixesForAsns(ruAsns);
    }

    private static IReadOnlyList<(uint Prefix, byte Length)> LoadFromFile(string path)
    {
        var result = new List<(uint Prefix, byte Length)>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

            var slash = trimmed.IndexOf('/');
            var ip = IPAddress.Parse(trimmed[..slash]);
            var length = byte.Parse(trimmed[(slash + 1)..]);
            var prefix = BgpConstants.IPAddressToUint(ip);
            result.Add((prefix, length));
        }
        return result;
    }
}

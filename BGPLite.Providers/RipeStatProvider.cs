using System.Net;
using System.Text.Json;
using BGPLite.Protocol;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

public sealed class RipeStatProvider(IHttpClientFactory httpFactory, ILogger<RipeStatProvider> logger)
{
    /// <summary>Named-client key registered with <c>IHttpClientFactory</c>.</summary>
    public const string ClientName = "ripestat";

    public async Task<IReadOnlyList<(uint Prefix, byte PrefixLength)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
    {
        var url = $"https://stat.ripe.net/data/ris-prefixes/data.json?resource=AS{asn}&list_prefixes=true";
        var http = httpFactory.CreateClient(ClientName);
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        var prefixes = doc.RootElement
            .GetProperty("data")
            .GetProperty("prefixes")
            .GetProperty("v4")
            .GetProperty("originating");

        var result = new List<(uint Prefix, byte PrefixLength)>(prefixes.GetArrayLength());

        foreach (var element in prefixes.EnumerateArray())
        {
            var cidr = element.GetString()!;
            var slash = cidr.IndexOf('/');
            var ip = IPAddress.Parse(cidr[..slash]);
            var length = byte.Parse(cidr[(slash + 1)..]);
            var prefix = BgpConstants.IPAddressToUint(ip);
            result.Add((prefix, length));
        }

        logger.LogInformation("AS{Asn}: fetched {Count} prefixes", asn, result.Count);
        return result;
    }
}

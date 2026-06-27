using System.Net;
using System.Text.Json;
using BGPLite.Configuration;
using BGPLite.Protocol;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

public sealed class RipeStatProvider
{
    /// <summary>Named-client key registered with <c>IHttpClientFactory</c>.</summary>
    public const string ClientName = "ripestat";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<RipeStatProvider> _logger;
    private readonly RipeStatConfig _config;

    public RipeStatProvider(IHttpClientFactory httpFactory, ILogger<RipeStatProvider> logger, RipeStatConfig? config = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _config = config ?? new RipeStatConfig();
    }

    public async Task<IReadOnlyList<(uint Prefix, byte PrefixLength)>> GetPrefixesAsync(uint asn, CancellationToken ct = default)
    {
        var url = $"https://stat.ripe.net/data/ris-prefixes/data.json?resource=AS{asn}&list_prefixes=true";

        // The ris-prefixes endpoint is slow for large origin ASes (minutes) and can fail
        // transiently under load, so we retry transient failures a few times before giving up.
        var retries = Math.Max(0, _config.RetryAttempts);

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await FetchOnceAsync(url, asn, ct);
            }
            // Retry only transient failures — and never retry when the caller cancelled us.
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < retries && IsTransient(ex))
            {
                var backoff = TimeSpan.FromSeconds(_config.RetryDelaySeconds * Math.Pow(2, attempt));
                _logger.LogWarning(
                    "AS{Asn}: RIPEstat attempt {Attempt}/{Total} failed ({Reason}); retrying in {Delay:F0}s",
                    asn, attempt + 1, retries + 1, ex.GetType().Name, backoff.TotalSeconds);
                await Task.Delay(backoff, ct);
            }
        }
    }

    private async Task<IReadOnlyList<(uint Prefix, byte PrefixLength)>> FetchOnceAsync(string url, uint asn, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(ClientName);
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

        _logger.LogInformation("AS{Asn}: fetched {Count} prefixes", asn, result.Count);
        return result;
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        // Client timeout: HttpClient fires a TaskCanceledException when its own Timeout elapses.
        // (A caller-initiated cancellation is filtered out by the `!ct.IsCancellationRequested`
        // guard above before we ever get here.)
        OperationCanceledException => true,
        // Transient HTTP failures: rate limiting and server errors. Network-level failures
        // (DNS, connection refused, TLS) surface as HttpRequestException with a null StatusCode.
        HttpRequestException hre => hre.StatusCode is null || IsTransientStatus(hre.StatusCode.Value),
        _ => false,
    };

    private static bool IsTransientStatus(HttpStatusCode code) =>
        (int)code == 429 || (int)code >= 500;
}

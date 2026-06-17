using BGPLite.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>
/// Loads prefixes from a remote CIDR list over HTTP/HTTPS (Kind = <c>"http"</c>). Any direct
/// raw-file URL works — raw.githubusercontent.com, a gist, a pastebin, a self-hosted list, etc.
/// The URL is fetched as-is. Uses <see cref="IHttpClientFactory"/> so the handler pool is recycled
/// by the factory (the provider is stateless and safe to hold as a singleton).
/// </summary>
public sealed class HttpPrefixProvider(IHttpClientFactory httpFactory, ILogger<HttpPrefixProvider> logger)
    : IPrefixSourceProvider
{
    /// <summary>Named-client key registered with <c>IHttpClientFactory</c>.</summary>
    public const string ClientName = "http";

    public string Kind => "http";

    public async Task<IReadOnlyList<(uint Prefix, byte Length)>> LoadAsync(PrefixSourceConfig source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            throw new InvalidOperationException($"Prefix source '{source.Name}': Kind=http requires a Url.");

        var url = source.Url;
        var http = httpFactory.CreateClient(ClientName);
        if (source.Timeout is int seconds && seconds > 0)
            http.Timeout = TimeSpan.FromSeconds(seconds);
        if (source.Headers is { Count: > 0 } headers)
            foreach (var (key, value) in headers)
            {
                // A per-source User-Agent replaces the named-client default instead of appending a second value.
                if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    http.DefaultRequestHeaders.Remove(key);
                if (!http.DefaultRequestHeaders.TryAddWithoutValidation(key, value))
                    logger.LogWarning("Source '{Name}': could not add request header '{Header}'.", source.Name, key);
            }
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct);
        var prefixes = PrefixListParser.Parse(text);
        logger.LogInformation("Source '{Name}' (http): loaded {Count} prefixes from {Url}", source.Name, prefixes.Count, url);
        return prefixes;
    }
}

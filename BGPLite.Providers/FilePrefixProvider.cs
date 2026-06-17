using BGPLite.Configuration;
using Microsoft.Extensions.Logging;

namespace BGPLite.Providers;

/// <summary>Loads prefixes from a local CIDR file (Kind = <c>"file"</c>).</summary>
public sealed class FilePrefixProvider(ILogger<FilePrefixProvider> logger) : IPrefixSourceProvider
{
    public string Kind => "file";

    public Task<IReadOnlyList<(uint Prefix, byte Length)>> LoadAsync(PrefixSourceConfig source, CancellationToken ct = default)
    {
        var path = source.Path;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Prefix source '{source.Name}': Kind=file requires a Path.");

        var fullPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Prefix file not found for source '{source.Name}': {fullPath}", fullPath);

        var prefixes = PrefixListParser.Parse(File.ReadAllText(fullPath));
        logger.LogInformation("Source '{Name}' (file): loaded {Count} prefixes from {Path}", source.Name, prefixes.Count, fullPath);
        return Task.FromResult(prefixes);
    }
}

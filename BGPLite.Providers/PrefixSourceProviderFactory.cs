namespace BGPLite.Providers;

/// <summary>
/// Resolves the correct <see cref="IPrefixSourceProvider"/> for a given
/// <see cref="Configuration.PrefixSourceConfig.Kind"/>. New loading methods are
/// supported by registering an additional <see cref="IPrefixSourceProvider"/>.
/// </summary>
public sealed class PrefixSourceProviderFactory
{
    private readonly Dictionary<string, IPrefixSourceProvider> _byKind;

    public PrefixSourceProviderFactory(IEnumerable<IPrefixSourceProvider> providers)
    {
        _byKind = providers.ToDictionary(p => p.Kind);
    }

    public IPrefixSourceProvider Get(string kind) =>
        _byKind.TryGetValue(kind, out var provider)
            ? provider
            : throw new InvalidOperationException($"Unknown prefix source kind: '{kind}'.");
}

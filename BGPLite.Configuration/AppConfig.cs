using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

public sealed class AppConfig
{
    [YamlMember(Alias = "Bgp")]
    public BgpConfig Bgp { get; init; } = new();

    [YamlMember(Alias = "Peers")]
    public List<PeerConfig> Peers { get; init; } = [];

    [YamlMember(Alias = "ApiPort")]
    public int ApiPort { get; init; } = 5001;

    [YamlMember(Alias = "RipeStat")]
    public RipeStatConfig? RipeStat { get; init; }

    /// <summary>Configurable prefix sources (file, http, ...) loaded at startup via the provider factory.</summary>
    [YamlMember(Alias = "PrefixSources")]
    public List<PrefixSourceConfig> PrefixSources { get; init; } = [];

    /// <summary>Name of the source served as the RU/default set for unconfigured peers.</summary>
    [YamlMember(Alias = "DefaultPrefixSource")]
    public string? DefaultPrefixSource { get; init; }
}

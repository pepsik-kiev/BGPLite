using YamlDotNet.Serialization;

namespace BGPLite.Configuration;

public sealed class RipeStatConfig
{
    [YamlMember(Alias = "AsnLists")]
    public List<AsnList> AsnLists { get; init; } = [];
}

public sealed class AsnList
{
    [YamlMember(Alias = "Name")]
    public string Name { get; init; } = "";

    [YamlMember(Alias = "Description")]
    public string Description { get; init; } = "";

    [YamlMember(Alias = "Asns")]
    public List<uint> Asns { get; init; } = [];

    [YamlMember(Alias = "Country")]
    public string? Country { get; init; }
}

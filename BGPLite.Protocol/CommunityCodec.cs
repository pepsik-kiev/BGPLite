namespace BGPLite.Protocol;

/// <summary>
/// Encodes/decodes a BGP well-known community between its <c>"ASN:VALUE"</c> string form
/// and the packed 32-bit representation (<c>(asn &lt;&lt; 16) | value</c>) used across the codebase.
/// </summary>
public static class CommunityCodec
{
    public static uint Parse(string community)
    {
        var colon = community.IndexOf(':');
        if (colon < 0)
            throw new FormatException($"Invalid community '{community}' (expected 'ASN:VALUE').");

        var asn = uint.Parse(community[..colon]);
        var value = uint.Parse(community[(colon + 1)..]);

        if (asn > 0xFFFF)
            throw new FormatException($"Invalid community '{community}': ASN part must be 0-65535 (got {asn}).");

        return (asn << 16) | (value & 0xFFFF);
    }

    public static string Format(uint community) => $"{community >> 16}:{community & 0xFFFF}";
}

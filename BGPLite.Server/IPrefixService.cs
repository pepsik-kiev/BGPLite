namespace BGPLite.Server;

public interface IPrefixService
{
    Task<IReadOnlyList<(uint Prefix, byte Length)>> GetPrefixesAsync(uint asn);
    Task<List<(uint Prefix, byte Length, uint Asn)>> GetPrefixesForAsns(IEnumerable<uint> asns);
    Task<int> GetPrefixCountAsync(uint asn);
    Task<List<(uint Prefix, byte Length, uint Asn)>> GetRuPrefixesAsync();
    Task<IReadOnlyList<(uint Prefix, byte Length)>> GetSourcePrefixesAsync(string name);
    Task WarmUpAsync();
}

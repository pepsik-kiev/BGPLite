using System.Net;
using System.Net.Sockets;
using BGPLite.Protocol;

namespace BGPLite.Providers;

/// <summary>
/// Parses a CIDR-per-line text blob into packed IPv4 prefixes.
/// Blank lines and lines starting with <c>#</c> are ignored; malformed and non-IPv4
/// lines are skipped silently (the route table only stores IPv4 <c>(uint, byte)</c> keys).
/// </summary>
public static class PrefixListParser
{
    public static IReadOnlyList<(uint Prefix, byte Length)> Parse(string text)
    {
        var result = new List<(uint Prefix, byte Length)>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var slash = line.IndexOf('/');
            if (slash < 0) continue;
            if (!IPAddress.TryParse(line[..slash], out var ip)) continue;
            if (!byte.TryParse(line[(slash + 1)..], out var length)) continue;
            if (ip.AddressFamily != AddressFamily.InterNetwork) continue; // IPv4 only

            result.Add((BgpConstants.IPAddressToUint(ip), length));
        }

        return result;
    }
}

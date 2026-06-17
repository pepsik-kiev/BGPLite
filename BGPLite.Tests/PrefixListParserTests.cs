using System.Net;
using BGPLite.Providers;
using BGPLite.Protocol;

namespace BGPLite.Tests;

public class PrefixListParserTests
{
    private static uint Ip(string s) => BgpConstants.IPAddressToUint(IPAddress.Parse(s));

    [Fact]
    public void ParsesSingleCidr()
    {
        var result = PrefixListParser.Parse("1.2.3.0/24");
        var single = Assert.Single(result);
        Assert.Equal((Ip("1.2.3.0"), (byte)24), single);
    }

    [Fact]
    public void SkipsBlankAndCommentLines()
    {
        var result = PrefixListParser.Parse("# header\n\n1.2.3.0/24\n   \n# tail\n");
        Assert.Single(result);
    }

    [Fact]
    public void SkipsMalformedLineWithoutSlash()
    {
        var result = PrefixListParser.Parse("garbage\n1.2.3.0/24");
        Assert.Single(result);
    }

    [Fact]
    public void SkipsIpv6()
    {
        var result = PrefixListParser.Parse("::1/128\n1.2.3.0/24");
        var single = Assert.Single(result);
        Assert.Equal((Ip("1.2.3.0"), (byte)24), single);
    }

    [Fact]
    public void SkipsBadLength()
    {
        var result = PrefixListParser.Parse("1.2.3.0/abc\n1.2.3.0/24");
        Assert.Single(result);
    }

    [Fact]
    public void EmptyInputReturnsEmpty() => Assert.Empty(PrefixListParser.Parse(""));

    [Fact]
    public void ParsesMultiple()
    {
        var result = PrefixListParser.Parse("10.0.0.0/8\n5.6.0.0/16");
        Assert.Equal(2, result.Count);
    }
}

using BGPLite.Protocol;

namespace BGPLite.Tests;

public class CommunityCodecTests
{
    [Fact]
    public void Parse_PacksAsnAndValue()
    {
        Assert.Equal((65000u << 16) | 100u, CommunityCodec.Parse("65000:100"));
    }

    [Fact]
    public void Format_RoundTrips()
    {
        var packed = CommunityCodec.Parse("65444:1");
        Assert.Equal("65444:1", CommunityCodec.Format(packed));
    }

    [Fact]
    public void Parse_InvalidThrows()
    {
        Assert.Throws<FormatException>(() => CommunityCodec.Parse("nope"));
    }

    [Fact]
    public void Parse_MasksValueTo16Bits()
    {
        // 131071 = 0x1FFFF → low 16 bits 0xFFFF
        Assert.Equal((1u << 16) | 0xFFFFu, CommunityCodec.Parse("1:131071"));
    }

    [Fact]
    public void Parse_FourByteAsn_Throws()
    {
        // A 4-byte ASN would silently wrap to 0 if not validated (131072 << 16 == 0 in uint).
        Assert.Throws<FormatException>(() => CommunityCodec.Parse("131072:100"));
    }
}

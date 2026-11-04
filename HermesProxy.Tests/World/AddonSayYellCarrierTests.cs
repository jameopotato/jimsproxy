using HermesProxy.World;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// Addon say/yell carrier (AddonSayYellCarrier.cs): a 1.14 addon message sent to SAY or YELL
/// is carried between proxied players on a proxy-only channel instead of being forwarded as
/// 1.12 say or yell chat, which made every 1.12 client nearby play the shout animation.
/// </summary>
public class AddonSayYellCarrierTests
{
    [Theory]
    [InlineData(true, 1u, -8949.9f, -132.4f, "NWB", "abc")]
    [InlineData(false, 0u, 1629.3f, -4373.7f, "JPTEST", "x")]
    [InlineData(true, 530u, 0f, 0f, "D4C", "")]
    public void RoundTrip(bool yell, uint mapId, float x, float y, string prefix, string body)
    {
        string text = AddonSayYellCarrier.Encode(yell, mapId, x, y, prefix, body);
        Assert.True(AddonSayYellCarrier.TryDecode(text, out var message));
        Assert.Equal(yell, message.Yell);
        Assert.Equal(mapId, message.MapId);
        Assert.Equal((int)System.MathF.Round(x), message.X);
        Assert.Equal((int)System.MathF.Round(y), message.Y);
        Assert.Equal(prefix, message.Prefix);
        Assert.Equal(body, message.Body);
    }

    [Fact]
    public void BodyKeepsTabsAndHighBytes()
    {
        // A body is whatever the addon sent: tabs and bytes above 0x7F (DEFLATE output read
        // as Latin1) must survive, so only the first three tabs are structure.
        string body = "a\tb\tÿ\u0080c";
        string text = AddonSayYellCarrier.Encode(true, 1, 10, 20, "P", body);
        Assert.True(AddonSayYellCarrier.TryDecode(text, out var message));
        Assert.Equal(body, message.Body);
        Assert.Equal("P", message.Prefix);
    }

    [Fact]
    public void LongestMessageStaysUnderTheServerLengthSeenInTheField()
    {
        // The client caps an addon text at 255 bytes and a prefix at 16. Kronos has accepted
        // 264-byte "prefix\tbody" texts in the field; the header adds about 20 bytes.
        string text = AddonSayYellCarrier.Encode(true, 1, -12345.6f, -12345.6f, new string('P', 16), new string('b', 255));
        Assert.True(text.Length <= 296, $"carrier text is {text.Length} bytes");
        Assert.True(AddonSayYellCarrier.TryDecode(text, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("NWB\tabc")]                 // a plain addon message, not a carrier one
    [InlineData("JPY1\tY,1,2,3")]            // no prefix or body
    [InlineData("JPY1\tY,1,2,3\tNWB")]       // no body separator
    [InlineData("JPY1\tY,1,2,3\t\tbody")]    // empty prefix
    [InlineData("JPY1\tQ,1,2,3\tNWB\tbody")] // unknown type
    [InlineData("JPY1\tY,1,x,3\tNWB\tbody")] // bad number
    [InlineData("JPY1\tY,1,2\tNWB\tbody")]   // short header
    [InlineData("JPY2\tY,1,2,3\tNWB\tbody")] // another version
    public void DecodeRejectsAnythingElse(string? text)
    {
        Assert.False(AddonSayYellCarrier.TryDecode(text, out _));
    }

    [Theory]
    // Say: 25 yards plus a 10-yard margin.
    [InlineData(false, 1u, 0f, 30f, true)]
    [InlineData(false, 1u, 0f, 40f, false)]
    // Yell: 300 yards plus a 20-yard margin, measured in the horizontal plane.
    [InlineData(true, 1u, 300f, 100f, true)]
    [InlineData(true, 1u, 330f, 0f, false)]
    // Another map, even at the same coordinates.
    [InlineData(true, 0u, 0f, 0f, false)]
    public void InRange(bool yell, uint listenerMap, float listenerX, float listenerY, bool expected)
    {
        var message = new AddonSayYellCarrier.Message(yell, 1, 0, 0, "NWB", "x");
        Assert.Equal(expected, AddonSayYellCarrier.InRange(message, listenerMap, true, listenerX, listenerY));
    }

    [Fact]
    public void UnknownListenerPositionOrMapHearsNothing()
    {
        var message = new AddonSayYellCarrier.Message(true, 1, 0, 0, "NWB", "x");
        Assert.False(AddonSayYellCarrier.InRange(message, 1, false, 0, 0));
        Assert.False(AddonSayYellCarrier.InRange(message, null, true, 0, 0));
    }

    [Theory]
    [InlineData(ChatMessageTypeModern.Say, true)]
    [InlineData(ChatMessageTypeModern.Yell, true)]
    [InlineData(ChatMessageTypeModern.Party, false)]
    [InlineData(ChatMessageTypeModern.Raid, false)]
    [InlineData(ChatMessageTypeModern.Guild, false)]
    [InlineData(ChatMessageTypeModern.Officer, false)]
    [InlineData(ChatMessageTypeModern.Whisper, false)]
    public void OnlySayAndYellAreCarried(ChatMessageTypeModern type, bool expected)
    {
        Assert.Equal(expected, AddonSayYellCarrier.IsCarrierType(type));
    }

    [Fact]
    public void ChannelNameMatchesTheServerNormalization()
    {
        Assert.True(AddonSayYellCarrier.IsCarrierChannel(AddonSayYellCarrier.ChannelName));
        Assert.True(AddonSayYellCarrier.IsCarrierChannel(AddonSayYellCarrier.ChannelName.ToLowerInvariant()));
        Assert.True(AddonSayYellCarrier.IsCarrierChannel(AddonSayYellCarrier.ChannelName.ToUpperInvariant()));
        Assert.False(AddonSayYellCarrier.IsCarrierChannel("General - Elwynn Forest"));
        Assert.False(AddonSayYellCarrier.IsCarrierChannel(""));
        Assert.False(AddonSayYellCarrier.IsCarrierChannel(null));
    }
}

using System.Collections.Generic;
using System.Text;
using Framework.IO;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// Guards the addon-comm corruption fix: DEFLATE-compressed addon-channel bodies
// (e.g. Details talent sync) must survive the proxy byte-for-byte. These tests are
// written RED-first — they fail on current code and pass once the fix lands.
public class AddonCommCompressionTests
{
    static AddonCommCompressionTests()
    {
        // ChatPkt resolves an opcode through ModernVersion, whose static ctor needs a
        // real client build (mirrors the other packet-construction tests).
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    // ---- T-A (vector 2): CheckAddonPrefix must preserve the addon body verbatim ----
    // The legacy wire is "prefix\tbody"; only the FIRST tab is the delimiter. A
    // DEFLATE body can contain raw 0x09 (tab) bytes — the WoW addon-channel codec
    // only escapes 0x00 — so any rejoin that splits on every tab corrupts the body.
    [Fact]
    public void CheckAddonPrefix_AddonBodyWithEmbeddedTabs_PreservesBodyVerbatim()
    {
        var registered = new HashSet<string> { "Details" };
        uint language = (uint)Language.Addon;
        string body = "ab\tcd«ef\tgh";          // embedded 0x09 tabs + a high char
        string text = "Details\t" + body;
        string addonPrefix = "";

        bool ok = ChatPkt.CheckAddonPrefix(registered, ref language, ref text, ref addonPrefix);

        Assert.True(ok);
        Assert.Equal("Details", addonPrefix);
        Assert.Equal(body, text);                    // RED today: Join(" ") turns the body's tabs into spaces
    }

    // ---- T-B (vectors 1+3): ChatPkt must frame an addon binary body byte-exact ----
    // 255 bytes → fits the SpanPacketWriter fast path; 600 bytes → forces the
    // ByteBuffer Write() fallback (exceeds the 512-byte MaxChatTextBytes cap). One
    // public path (WritePacketData/GetData) routes to both internal writers by size.
    [Theory]
    [InlineData(255)]
    [InlineData(600)]
    public void ChatPkt_AddonBinaryBody_SurvivesFramingByteExact(int bodyLen)
    {
        byte[] body = new byte[bodyLen];
        for (int i = 0; i < bodyLen; i++)
            body[i] = (byte)((i % 255) + 1);         // 0x01..0xFF, never 0x00

        // The inbound read path should hand ChatPkt a 1:1 byte->char (Latin1) string.
        string chatText = Encoding.Latin1.GetString(body);

        var pkt = new ChatPkt(
            null!, ChatMessageTypeModern.Say, chatText,
            language: (uint)Language.AddonBfA,       // value CheckAddonPrefix assigns to addon msgs
            senderName: "S", receiverName: "T",
            addonPrefix: "Details");

        pkt.WritePacketData();
        byte[] data = pkt.GetData()!;

        (uint chatTextBytes, byte[] payload) = ExtractChatTextField(data);

        Assert.Equal((uint)bodyLen, chatTextBytes);  // RED today: UTF-8 byte count (high bytes doubled)
        Assert.Equal(body, payload);                 // RED today: UTF-8-mangled payload bytes
    }

    // ---- (realistic) ChatPkt: an actual zlib/DEFLATE blob body survives byte-exact ----
    [Fact]
    public void ChatPkt_AddonDeflateBlobBody_SurvivesFramingByteExact()
    {
        // Mirror a real Details broadcast: zlib-compressed data (high-entropy bytes >= 0x80).
        byte[] plain = new byte[200];
        for (int i = 0; i < plain.Length; i++)
            plain[i] = (byte)((i * 37 + 11) % 256);  // deterministic, poorly compressible
        byte[] body = ZLib.Compress(plain);

        string chatText = Encoding.Latin1.GetString(body);
        var pkt = new ChatPkt(
            null!, ChatMessageTypeModern.Say, chatText,
            language: (uint)Language.AddonBfA, senderName: "S", receiverName: "T",
            addonPrefix: "Details");

        pkt.WritePacketData();
        (uint chatTextBytes, byte[] payload) = ExtractChatTextField(pkt.GetData()!);

        Assert.Equal((uint)body.Length, chatTextBytes);
        Assert.Equal(body, payload);
    }

    // ---- T-B' (vector 1, OUTBOUND read): the modern->legacy addon body must survive ----
    // "YOUR addon data as others receive it": ChatAddonMessageParams.Read currently
    // UTF-8-decodes the raw CMSG body, mangling bytes >= 0x80. RED today; green after E-read.
    [Theory]
    [InlineData(200)]
    public void ChatAddonMessageParams_Read_AddonBody_SurvivesByteExact(int bodyLen)
    {
        byte[] body = new byte[bodyLen];
        for (int i = 0; i < bodyLen; i++)
            body[i] = (byte)((i % 255) + 1);         // 0x01..0xFF (8-bit textLen caps at 255)
        string prefix = "Details";

        var wp = new WorldPacket(1u);                // opcode value irrelevant to Read()
        wp.WriteBits(prefix.Length, 5);
        wp.WriteBits(body.Length, 8);
        wp.WriteBit(false);                          // IsLogged
        wp.FlushBits();
        wp.WriteInt32((int)ChatMessageTypeModern.Say);
        wp.WriteString(prefix);
        wp.WriteBytes(body);                         // raw addon body on the wire

        var readPkt = new WorldPacket(1u, wp.GetData());
        var prms = new ChatAddonMessageParams();
        prms.Read(readPkt);

        Assert.Equal(prefix, prms.Prefix);
        Assert.Equal(body, Encoding.Latin1.GetBytes(prms.Text));  // RED today: UTF-8 read mangles high bytes
    }

    // ---- (regression guard) CheckAddonPrefix: a normal single-tab message is unchanged ----
    // Fix C (split on first tab) must produce identical output for the single-tab messages
    // every real addon sends (PallyPower, HealComm, DBM, BigWigs...). Green now AND after.
    [Theory]
    [InlineData("PLPWR\tASSIGN Bob 4 3", "PLPWR", "ASSIGN Bob 4 3")]
    [InlineData("LHC40\tD:2:33076:1500:abc", "LHC40", "D:2:33076:1500:abc")]
    public void CheckAddonPrefix_SingleTabMessage_SplitsIdentically(string wire, string expectedPrefix, string expectedBody)
    {
        var registered = new HashSet<string> { expectedPrefix };
        uint language = (uint)Language.Addon;
        string text = wire;
        string addonPrefix = "";

        bool ok = ChatPkt.CheckAddonPrefix(registered, ref language, ref text, ref addonPrefix);

        Assert.True(ok);
        Assert.Equal(expectedPrefix, addonPrefix);
        Assert.Equal(expectedBody, text);
        Assert.Equal((uint)Language.AddonBfA, language);
    }

    // ---- (regression guard) CheckAddonPrefix: unregistered prefix / no tab still rejected ----
    [Theory]
    [InlineData("Unregistered\tbody")]
    [InlineData("NoTabAtAll")]
    public void CheckAddonPrefix_UnregisteredOrUntabbed_ReturnsFalse(string wire)
    {
        var registered = new HashSet<string> { "Details" };
        uint language = (uint)Language.Addon;
        string text = wire;
        string addonPrefix = "";

        bool ok = ChatPkt.CheckAddonPrefix(registered, ref language, ref text, ref addonPrefix);

        Assert.False(ok);
    }

    // ---- (regression guard) ChatPkt: NON-addon chat stays UTF-8 (the fix must not touch it) ----
    [Fact]
    public void ChatPkt_NonAddonChat_StaysUtf8()
    {
        string msg = "Café déjà vu — naïve";        // multi-byte UTF-8 characters
        var pkt = new ChatPkt(
            null!, ChatMessageTypeModern.Say, msg,
            language: (uint)Language.Common, senderName: "S", receiverName: "T");

        pkt.WritePacketData();
        (uint chatTextBytes, byte[] payload) = ExtractChatTextField(pkt.GetData()!);

        Assert.Equal((uint)Encoding.UTF8.GetByteCount(msg), chatTextBytes);
        Assert.Equal(Encoding.UTF8.GetBytes(msg), payload);
    }

    // ---- T-C (vector 1, IO primitive): Latin1 overloads are byte-exact; UTF-8 default intact ----
    [Fact]
    public void ByteBuffer_Latin1RoundTrip_IsByteIdentical()
    {
        byte[] body = new byte[255];
        for (int i = 0; i < body.Length; i++)
            body[i] = (byte)(i + 1);             // 0x01..0xFF

        using var wb = new ByteBuffer();
        wb.WriteString(Encoding.Latin1.GetString(body), Encoding.Latin1);
        byte[] wire = wb.GetData();
        Assert.Equal(body, wire);                // Latin1 = 1 byte/char, no expansion

        using var rb = new ByteBuffer(wire);
        string read = rb.ReadString((uint)wire.Length, Encoding.Latin1);
        Assert.Equal(body, Encoding.Latin1.GetBytes(read));
    }

    [Fact]
    public void ByteBuffer_Utf8Default_PreservesAccentedText()
    {
        const string text = "Café déjà — naïve";
        using var wb = new ByteBuffer();
        wb.WriteString(text);                     // default still UTF-8
        byte[] wire = wb.GetData();
        Assert.Equal(Encoding.UTF8.GetBytes(text), wire);

        using var rb = new ByteBuffer(wire);
        Assert.Equal(text, rb.ReadString((uint)wire.Length));
    }

    // Mirrors ChatPkt.WriteToSpan()/Write() field order to reach the ChatText body.
    private static (uint chatTextBytes, byte[] payload) ExtractChatTextField(byte[] data)
    {
        var r = new SpanPacketReader(data);
        r.ReadUInt8();                       // SlashCmd
        r.ReadUInt32();                      // _Language
        r.ReadPackedGuid128(out _, out _);   // SenderGUID
        r.ReadPackedGuid128(out _, out _);   // SenderGuildGUID
        r.ReadPackedGuid128(out _, out _);   // SenderAccountGUID
        r.ReadPackedGuid128(out _, out _);   // TargetGUID
        r.ReadUInt32();                      // TargetVirtualAddress
        r.ReadUInt32();                      // SenderVirtualAddress
        r.ReadPackedGuid128(out _, out _);   // PartyGUID
        r.ReadUInt32();                      // AchievementID
        r.ReadFloat();                       // DisplayTime
        uint senderNameBytes = r.ReadBits<uint>(11);
        uint targetNameBytes = r.ReadBits<uint>(11);
        uint prefixBytes = r.ReadBits<uint>(5);
        uint channelBytes = r.ReadBits<uint>(7);
        uint chatTextBytes = r.ReadBits<uint>(12);
        r.ReadBits<uint>(14);                // _ChatFlags
        r.ReadBit();                         // HideChatLog
        r.ReadBit();                         // FakeSenderName
        r.ReadBit();                         // Unused_801.HasValue
        r.ReadBit();                         // ChannelGUID != default
        r.ReadBytes((int)senderNameBytes);
        r.ReadBytes((int)targetNameBytes);
        r.ReadBytes((int)prefixBytes);
        r.ReadBytes((int)channelBytes);
        byte[] payload = r.ReadBytes((int)chatTextBytes).ToArray();
        return (chatTextBytes, payload);
    }
}

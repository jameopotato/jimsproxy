using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// Wire-level coverage for the vanilla SMSG_PET_CAST_FAILED parsing semantics.
// The handler at SpellHandler.cs:HandlePetCastFailed has crashed the proxy on
// Kronos 5 (TrinityCore-based 1.12) because vanilla SMSG_PET_CAST_FAILED bodies
// vary between server flavors:
//   cMaNGOS / vmangos canonical:  uint32 SpellID, uint8 Result        (5 bytes)
//   Kronos 5 (TrinityCore-1.12):  uint32 SpellID only                  (4 bytes)
// The fix uses CanRead() to defensively read Result. These tests verify the
// parsing semantics on both wire shapes against a hand-crafted WorldPacket.
public class PetCastFailedParserTests
{
    static PetCastFailedParserTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private const ushort VanillaOpcodeRaw = 0x138; // SMSG_PET_CAST_FAILED in 1.12

    private static byte[] BuildPacketBuffer(uint spellId, byte? reason)
    {
        // The proxy receive loop hands WorldPacket a buffer where bytes [0..1]
        // are the opcode (the WorldPacket(byte[]) constructor consumes them)
        // and bytes [2..] are the body. Mimic that here.
        int bodyLen = 4 + (reason.HasValue ? 1 : 0);
        var buffer = new byte[2 + bodyLen];
        buffer[0] = (byte)(VanillaOpcodeRaw & 0xFF);
        buffer[1] = (byte)((VanillaOpcodeRaw >> 8) & 0xFF);
        buffer[2] = (byte)(spellId & 0xFF);
        buffer[3] = (byte)((spellId >> 8) & 0xFF);
        buffer[4] = (byte)((spellId >> 16) & 0xFF);
        buffer[5] = (byte)((spellId >> 24) & 0xFF);
        if (reason.HasValue)
            buffer[6] = reason.Value;
        return buffer;
    }

    [Fact]
    public void Kronos5FourByteBody_ReadsSpellIdAndReportsNoReason()
    {
        // Kronos 5 (TrinityCore-1.12) sends 4-byte body (just SpellID). The
        // defensive parser must read SpellID and then see CanRead==false
        // without throwing.
        const uint TestSpellId = 17767u; // Consume Shadows rank 1 (Voidwalker)
        var packet = new WorldPacket(BuildPacketBuffer(TestSpellId, reason: null));

        uint spellId = packet.ReadUInt32();
        bool hasReason = packet.CanRead();

        Assert.Equal(TestSpellId, spellId);
        Assert.False(hasReason);
    }

    [Fact]
    public void CMaNGOSFiveByteBody_ReadsSpellIdAndReason()
    {
        // cMaNGOS / vmangos canonical layout: SpellID followed by uint8 Result.
        const uint TestSpellId = 17767u;
        const byte TestReason = 1; // SpellCastResultVanilla.AlreadyAtFullHealth
        var packet = new WorldPacket(BuildPacketBuffer(TestSpellId, reason: TestReason));

        uint spellId = packet.ReadUInt32();
        bool hasReason = packet.CanRead();
        byte reason = hasReason ? packet.ReadUInt8() : (byte)0;

        Assert.Equal(TestSpellId, spellId);
        Assert.True(hasReason);
        Assert.Equal(TestReason, reason);
        Assert.False(packet.CanRead()); // exactly one reason byte, no trailing data
    }

    [Fact]
    public void Kronos5FourByteBody_DoesNotThrowOnCanReadCheck()
    {
        // The original bug: ReadUInt8() ran past end-of-buffer. The defensive
        // pattern (`hasReason = CanRead(); if (hasReason) ReadUInt8();`) must
        // not throw on a 4-byte body — that's the exact scenario captured in
        // jimsproxy-20260510-115151.jsonl that crashed the proxy.
        var packet = new WorldPacket(BuildPacketBuffer(17767u, reason: null));

        var ex = Record.Exception(() =>
        {
            uint spellId = packet.ReadUInt32();
            if (packet.CanRead())
                packet.ReadUInt8();
        });

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(0, "AffectingCombat")]
    [InlineData(1, "AlreadyAtFullHealth")]
    [InlineData(2, "AlreadyAtFullPower")]
    [InlineData(46, "Moving")]
    [InlineData(67, "NoAmmo")]
    [InlineData(77, "NoPower")]
    public void CMaNGOSFiveByteBody_VariousReasonCodes_DoNotThrow(byte reason, string expectedName)
    {
        // Smoke test that every realistic cMaNGOS reason code parses cleanly.
        // Pre-fix the vanilla handler swallowed every code != 2 silently and
        // crashed on code == 2. With the new defensive read, no code crashes
        // and all are routed.
        var packet = new WorldPacket(BuildPacketBuffer(17767u, reason: reason));

        var ex = Record.Exception(() =>
        {
            uint spellId = packet.ReadUInt32();
            if (packet.CanRead())
            {
                byte r = packet.ReadUInt8();
                Assert.Equal(reason, r);
            }
        });

        Assert.Null(ex);
        Assert.Contains(expectedName, ((SpellCastResultVanilla)reason).ToString());
    }
}

using System;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (loss-of-control synth): a 1.12 server never sends the loss-of-control packet
// family, so via the proxy the 1.14 client's LoC UI (shipped and maintained in classic-era
// FrameXML — lock icon, alert frame, action-button desaturation) is dead for EVERY CC in
// the game. Both opcodes are in the 42597 client's dispatch table (SMSG_ADD_LOSS_OF_CONTROL
// 0x266A, SMSG_LOSS_OF_CONTROL_AURA_UPDATE 0x2669 in our V2_5_3_41750 enum, matching WPP's
// binary-derived V2_5_3_41812 values). These tests lock (a) the vanilla-aura-type → modern
// LocType mapping, (b) the self-only synth gate, and (c) the exact wire layouts via
// write/read round-trips in WowPacketParser's sniff-derived read order (ADD = V8_0_1
// parser; AURA_UPDATE = V7_0_3 parser with the 7.2.5+ AffectedGUID, no 10.1.5+ Duration).
public class LossOfControlSynthTests
{
    public LossOfControlSynthTests()
    {
        // ServerPacket's ctor resolves the opcode through ModernVersion, whose static
        // ctor needs a client build (same bootstrap as AddonCommCompressionTests).
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    static readonly WowGuid128 Self = WowGuid128.Create(HighGuidType703.Player, 28648);
    static readonly WowGuid128 Other = WowGuid128.Create(HighGuidType703.Player, 21654);

    // ---- vanilla SPELL_AURA_* id → modern LossOfControlType ----

    [Theory]
    [InlineData(2u, 1)]   // MOD_POSSESS  -> Possess
    [InlineData(5u, 2)]   // MOD_CONFUSE  -> Confuse
    [InlineData(6u, 3)]   // MOD_CHARM    -> Charm
    [InlineData(7u, 4)]   // MOD_FEAR     -> Fear
    [InlineData(12u, 5)]  // MOD_STUN     -> Stun
    [InlineData(25u, 6)]  // MOD_PACIFY   -> Pacify
    [InlineData(26u, 7)]  // MOD_ROOT     -> Root
    [InlineData(27u, 8)]  // MOD_SILENCE  -> Silence
    [InlineData(60u, 9)]  // MOD_PACIFY_SILENCE -> PacifySilence
    public void LocTypeForAuraType_CcAuras_Map(uint auraType, byte expectedLocType)
    {
        Assert.Equal(expectedLocType, GameData.LocTypeForAuraType(auraType));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]    // MOD_DAMAGE_DONE-adjacent — not CC
    [InlineData(135u)]  // MOD_HEALING_DONE — not CC
    public void LocTypeForAuraType_NonCcAuras_Zero(uint auraType)
    {
        Assert.Equal(0, GameData.LocTypeForAuraType(auraType));
    }

    // ---- synth gate: LOCAL PLAYER only (no observer/multicast sends — that targeting is
    // the unproven part of the Era contract) ----

    [Fact]
    public void SynthGate_SelfWithLeverOn_True()
    {
        Assert.True(WorldClient.ShouldSynthLossOfControlFor(true, Self, Self));
    }

    [Fact]
    public void SynthGate_OtherUnit_False()
    {
        Assert.False(WorldClient.ShouldSynthLossOfControlFor(true, Other, Self));
    }

    [Fact]
    public void SynthGate_LeverOff_False()
    {
        Assert.False(WorldClient.ShouldSynthLossOfControlFor(false, Self, Self));
    }

    // ---- wire layout round-trips (read back in WPP's exact sniff-derived order) ----

    [Fact]
    public void AddLossOfControl_WriteReadRoundTrip_WppV8Order()
    {
        var pkt = new AddLossOfControl
        {
            Victim = Self,
            SpellID = 8122,          // Psychic Scream
            Caster = Other,
            Duration = 8000,
            DurationRemaining = 6500,
            LockoutSchoolMask = 0,
            Mechanic = 5,            // fear
            Type = 4,                // LossOfControlType.Fear
        };
        var written = new WorldPacket();
        pkt.WritePayload(written);

        var read = new WorldPacket(0, written.GetData());
        Assert.Equal(Self, read.ReadPackedGuid128());       // Victim
        Assert.Equal(8122, read.ReadInt32());               // SpellID
        Assert.Equal(Other, read.ReadPackedGuid128());      // Caster
        Assert.Equal(8000u, read.ReadUInt32());             // Duration
        Assert.Equal(6500u, read.ReadUInt32());             // DurationRemaining
        Assert.Equal(0u, read.ReadUInt32());                // LockoutSchoolMask
        Assert.Equal((byte)5, read.ReadUInt8());            // Mechanic
        Assert.Equal((byte)4, read.ReadUInt8());            // Type
        Assert.Throws<IndexOutOfRangeException>(() => read.ReadUInt8()); // fully consumed — no trailing bytes
    }

    [Fact]
    public void LossOfControlAuraUpdate_WriteReadRoundTrip_WppV7Order()
    {
        var pkt = new LossOfControlAuraUpdate { AffectedGUID = Self };
        pkt.LocInfos.Add(new LossOfControlInfo { AuraSlot = 33, EffectIndex = 0, LocType = 4, Mechanic = 5 });
        pkt.LocInfos.Add(new LossOfControlInfo { AuraSlot = 34, EffectIndex = 1, LocType = 5, Mechanic = 12 });
        var written = new WorldPacket();
        pkt.WritePayload(written);

        var read = new WorldPacket(0, written.GetData());
        Assert.Equal(Self, read.ReadPackedGuid128());       // AffectedGUID (7.2.5+)
        Assert.Equal(2, read.ReadInt32());                  // count
        Assert.Equal((byte)33, read.ReadUInt8());           // [0] AuraSlot
        Assert.Equal((byte)0, read.ReadUInt8());            // [0] EffectIndex
        Assert.Equal((byte)4, read.ReadUInt8());            // [0] LocType
        Assert.Equal((byte)5, read.ReadUInt8());            // [0] Mechanic
        Assert.Equal((byte)34, read.ReadUInt8());           // [1] AuraSlot
        Assert.Equal((byte)1, read.ReadUInt8());            // [1] EffectIndex
        Assert.Equal((byte)5, read.ReadUInt8());            // [1] LocType
        Assert.Equal((byte)12, read.ReadUInt8());           // [1] Mechanic
        Assert.Throws<IndexOutOfRangeException>(() => read.ReadUInt8()); // fully consumed
    }
}

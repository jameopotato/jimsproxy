using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (2026-10-06): SMSG_SPELL_EXECUTE_LOG on Kronos writes every per-target guid as a
// plain 8-byte guid (not packed) and nothing at all behind summon-family effects. The bytes in
// the first test are the real packet captured on 2026-10-05 behind a Mining hit on a Rich
// Thorium Vein; reading that node guid as a packed guid is what produced the
// "[HighGuidLegacy] Unknown legacy high-guid 0xAD00" warnings.
public class SpellExecuteLogParseTests
{
    // Player 27032 (0x6998) packed: mask 0x03, two low bytes.
    static readonly byte[] WarriorPacked = { 0x03, 0x98, 0x69 };
    // A second player whose counter needs three bytes, packed in four.
    static readonly byte[] PaladinPacked = { 0x07, 0x01, 0x00, 0x01 };

    static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    static byte[] RawGuid(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        return b;
    }

    static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var p in parts)
            all.AddRange(p);
        return all.ToArray();
    }

    static WorldPacket Legacy(byte[] casterPacked, uint spellId, uint effectType, uint targetCount, params byte[][] payload)
    {
        var bytes = Concat(casterPacked, U32(spellId), U32(1), U32(effectType), U32(targetCount), Concat(payload));
        return new WorldPacket((uint)Opcode.SMSG_SPELL_EXECUTE_LOG, bytes);
    }

    static WorldClient.SpellExecuteLogParse Parse(WorldPacket packet)
        => WorldClient.ParseSpellExecuteLog(packet, GameSessionData.CreateForTesting());

    // The captured Mining packet: effect 33 OPEN_LOCK, one target, the node's guid unpacked.
    // Rich Thorium Vein entry 175404, counter 648339, legacy guid 0xF11002AD2C09E493.
    [Fact]
    public void Mining_OpenLock_ReadsTheNodeGuidUnpacked()
    {
        var packet = Legacy(WarriorPacked, 10248, 33, 1,
            new byte[] { 0x93, 0xE4, 0x09, 0x2C, 0xAD, 0x02, 0x10, 0xF1 });

        var log = Parse(packet);

        Assert.Equal(10248u, log.SpellId);
        Assert.Equal(HighGuidType.Player, log.Caster.GetHighType());
        Assert.Equal(27032ul, log.Caster.GetCounter());
        Assert.Null(log.UnknownEffectType);
        var target = Assert.Single(log.Targets);
        Assert.Equal(33u, target.EffectType);
        Assert.Equal(HighGuidType.GameObject, target.Target.GetHighType());
        Assert.Equal(175404u, target.Target.GetEntry());
        Assert.Equal(648339ul, target.Target.GetCounter());
    }

    // Dragon's Call (13049) is a guardian summon: effect 42, one target, no payload. The old
    // reader took a guid here and ran off the end of the packet 90 times in one evening.
    [Fact]
    public void GuardianSummon_HasNoTargetPayload()
    {
        var packet = Legacy(PaladinPacked, 13049, 42, 1);

        var log = Parse(packet);

        Assert.Null(log.UnknownEffectType);
        var target = Assert.Single(log.Targets);
        Assert.Equal(42u, target.EffectType);
        Assert.True(target.Target.IsEmpty());
    }

    // Redemption (10322) is effect 113 RESURRECT_NEW on 1.12, carrying the resurrected player.
    // The old table labelled 113 as durability damage and read two extra integers past the end.
    [Fact]
    public void ResurrectNew_ReadsThePlayerGuidUnpacked()
    {
        var packet = Legacy(PaladinPacked, 10322, 113, 1, RawGuid(1000));

        var log = Parse(packet);

        Assert.Null(log.UnknownEffectType);
        var target = Assert.Single(log.Targets);
        Assert.Equal(HighGuidType.Player, target.Target.GetHighType());
        Assert.Equal(1000ul, target.Target.GetCounter());
    }

    // Counterspell (2139) is effect 68 INTERRUPT_CAST: the creature guid unpacked, then the
    // interrupted spell id.
    [Fact]
    public void InterruptCast_ReadsCreatureGuidAndSpell()
    {
        ulong creature = (0xF130ul << 48) | (1234ul << 24) | 56ul;
        var packet = Legacy(WarriorPacked, 2139, 68, 1, RawGuid(creature), U32(10181));

        var log = Parse(packet);

        Assert.Null(log.UnknownEffectType);
        var target = Assert.Single(log.Targets);
        Assert.Equal(HighGuidType.Creature, target.Target.GetHighType());
        Assert.Equal(1234u, target.Target.GetEntry());
        Assert.Equal(56ul, target.Target.GetCounter());
        Assert.Equal(10181u, target.Value1);
    }

    // Fishing (7731) logs OPEN_LOCK with zero targets: nothing to read, nothing flagged.
    [Fact]
    public void Fishing_ZeroTargets_ParsesClean()
    {
        var log = Parse(Legacy(WarriorPacked, 7731, 33, 0));

        Assert.Equal(1u, log.EffectCount);
        Assert.Empty(log.Targets);
        Assert.Null(log.UnknownEffectType);
    }

    // An effect the table does not know stops the decode instead of guessing a guid out of
    // whatever bytes follow. No exception, no target.
    [Fact]
    public void UnknownEffect_StopsWithoutGuessing()
    {
        var packet = Legacy(WarriorPacked, 999, 200, 1, RawGuid(0xDEADBEEF));

        var log = Parse(packet);

        Assert.Equal(200u, log.UnknownEffectType);
        Assert.Empty(log.Targets);
    }

    // A known effect whose record is shorter than promised is treated the same way.
    [Fact]
    public void TruncatedRecord_StopsWithoutThrowing()
    {
        var packet = Legacy(WarriorPacked, 10248, 33, 1, new byte[] { 0x93, 0xE4, 0x09 });

        var log = Parse(packet);

        Assert.Equal(33u, log.UnknownEffectType);
        Assert.Empty(log.Targets);
    }

    [Theory]
    [InlineData(33u, 8)]    // OPEN_LOCK
    [InlineData(114u, 8)]   // ATTACK_ME
    [InlineData(68u, 12)]   // INTERRUPT_CAST
    [InlineData(24u, 4)]    // CREATE_ITEM
    [InlineData(42u, 0)]    // SUMMON_GUARDIAN
    [InlineData(88u, 0)]    // SUMMON_TOTEM_SLOT2
    [InlineData(77u, 0)]    // SCRIPT_EFFECT
    [InlineData(200u, -1)]  // not in the table
    public void RecordSizeTable_MatchesKronos(uint effectType, int expected)
    {
        Assert.Equal(expected, WorldClient.SpellExecuteLogRecordSize(effectType));
    }
}

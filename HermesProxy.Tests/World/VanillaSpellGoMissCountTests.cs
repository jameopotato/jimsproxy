using System;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (a resisted or immune cast left open): the three captured Kronos SMSG_SPELL_GO packets whose miss byte
// is 2 with one entry, the phantom byte with no entry, and the plain cases. Each byte string is the packet from the
// byte after the miss count to the end, exactly as logged by spell.parse_failed.
public class VanillaSpellGoMissCountTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    // 2026-10-18, Mind Control (10912) resisted by a player: miss byte 2, then GUID 57, type 2 (Resist), target
    // flags 0x0002 (Unit), packed GUID 57.
    [Fact]
    public void ResistedMindControlOnAPlayer_HasOneMiss()
    {
        Assert.Equal(1, VanillaSpellGoMissCount.Resolve(Hex("39000000000000000202000139"), 2, 0));
    }

    // 2026-10-13, Mind Control (10912) on a creature that is immune: miss byte 2, one entry with type 7 (Immune),
    // then the creature as the target with a 7-byte packed GUID.
    [Fact]
    public void ImmuneMindControlOnACreature_HasOneMiss()
    {
        Assert.Equal(1, VanillaSpellGoMissCount.Resolve(Hex("6706014f280030f1070200df6706014f2830f1"), 2, 0));
    }

    // 2026-08-21, Drain Soul (1120) resisted by a creature: miss byte 2, one entry with type 2 (Resist).
    [Fact]
    public void ResistedDrainSoul_HasOneMiss()
    {
        Assert.Equal(1, VanillaSpellGoMissCount.Resolve(Hex("818300a3140030f1020200db8183a31430f1"), 2, 0));
    }

    // The phantom byte the parser already skipped: byte 1 and no entry, the target section follows at once.
    [Fact]
    public void PhantomByteWithNoEntry_HasNoMiss()
    {
        Assert.Equal(0, VanillaSpellGoMissCount.Resolve(Hex("02000139"), 1, 0));
    }

    // A count that matches its entries is kept as sent.
    [Fact]
    public void CountThatMatchesItsEntries_IsKept()
    {
        Assert.Equal(1, VanillaSpellGoMissCount.Resolve(Hex("39000000000000000202000139"), 1, 0));
        Assert.Equal(2, VanillaSpellGoMissCount.Resolve(Hex("3900000000000000023a00000000000000070200013a"), 2, 0));
    }

    // A reflected miss carries one more byte (the reflect result).
    [Fact]
    public void ReflectedMiss_CountsItsExtraByte()
    {
        Assert.Equal(1, VanillaSpellGoMissCount.Resolve(Hex("39000000000000000b000200013a"), 1, 0));
    }

    // With the Projectile cast flag the ammo block (display id, inventory type) ends the packet.
    [Fact]
    public void ProjectileCast_IncludesTheAmmoBlock()
    {
        Assert.Equal(1, VanillaSpellGoMissCount.Resolve(Hex("39000000000000000102000139" + "0102030405060708"), 2, (uint)CastFlag.Projectile));
    }

    // Nothing that fits: the caller keeps its previous behaviour.
    [Fact]
    public void NoCountFits_ReturnsMinusOne()
    {
        Assert.Equal(-1, VanillaSpellGoMissCount.Resolve(Hex("ffff"), 1, 0));
    }
}

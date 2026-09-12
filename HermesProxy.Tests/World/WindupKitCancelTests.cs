using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// CancelWindupKitOnGo: before forwarding the SPELL_GO that completes a local pressed cast, the proxy
/// sends SMSG_CANCEL_SPELL_VISUAL_KIT for the caster and each kit the spell's visual uses ONLY as the
/// caster-side kit that carries the held wind-up sound (SpellVisualEvent start 1 -> end 2, TargetType 1;
/// the 2026-09-07 PTR run showed the sound-owning effect reports that kit, 99 for the holy heals, and
/// never the 3 -> 13 precast kit). These tests pin the data table the send rests on
/// (CSV/SpellVisualWindupKits1.csv, generated from 1.14.2.42597), the packet writer, and the decision
/// seam. The wire ordering (cancel, then GO) has no socket seam in this project and is verified by code
/// position and the in-process field run.
/// </summary>
public class WindupKitCancelTests
{
    static WindupKitCancelTests()
    {
        GameData.LoadSpellVisualResolved();
        GameData.LoadSpellVisualWindupKits();
    }

    [Theory]
    [InlineData(241697u, 99u)]  // Power Word: Shield 600 -> SpellVisual 784 -> holy wind-up sound kit 99
    [InlineData(238458u, 99u)]  // Lesser Heal 2053 -> 285 -> 99
    [InlineData(239699u, 99u)]  // Renew 6075 -> 280 -> 99
    [InlineData(240765u, 99u)]  // Inner Fire 7128 -> 211 -> 99
    [InlineData(246910u, 99u)]  // Flash of Light 19943 -> 6623 -> 99 (the collaborator's main looper; no 3 -> 13 kit at all)
    [InlineData(241717u, 223u)] // Lightning Shield 945 -> 37 -> 223
    public void ReporterSpells_ResolveToTheirWindupKit(uint spellXSpellVisualId, uint expectedKit)
    {
        var kits = GameData.GetWindupKitsForXSpellVisual(spellXSpellVisualId);
        Assert.Equal(1, kits.Length);
        Assert.Equal(expectedKit, kits[0]);
    }

    [Theory]
    [InlineData(237485u)] // Sunder Armor 7386 -> 406: a 3 -> 13 precast kit (557) only, no held-sound kit; button-only family
    [InlineData(239721u)] // Battle Shout 5242 -> 246: 3 -> 13 only
    [InlineData(0u)]      // no visual
    [InlineData(1u)]      // unknown SpellXSpellVisual
    public void SpellsWithoutAWindupSoundKit_ResolveToNothing(uint spellXSpellVisualId)
    {
        Assert.Equal(0, GameData.GetWindupKitsForXSpellVisual(spellXSpellVisualId).Length);
    }

    [Theory]
    [InlineData(218u)] // used under 1 -> 2 and under 3 -> 13: dual-use, excluded
    [InlineData(61u)]  // same
    [InlineData(270u)] // the 3 -> 13 precast kit; never a held-sound kit, must not be in the table
    [InlineData(557u)] // Sunder's 3 -> 13 kit, same
    public void DualUseAndPrecastKits_NeverAppearInTheTable(uint kit)
    {
        foreach (var kits in GameData.SpellVisualWindupKits.Values)
            Assert.DoesNotContain(kit, kits);
    }

    [Fact]
    public void Table_IsNonTrivial_AndEveryVisualHasAtLeastOneKit()
    {
        Assert.True(GameData.SpellVisualWindupKits.Count > 700, "the generated table should cover most cast visuals (791 at 42597)");
        foreach (var kv in GameData.SpellVisualWindupKits)
            Assert.NotEmpty(kv.Value);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void DecisionSeam_SendsOnlyWhenEnabledAndNotChanneled(bool enabled, bool channeled, bool expected)
    {
        Assert.Equal(expected, WorldClient.ShouldCancelWindupKitsOnGo(enabled, channeled));
    }

    [Fact]
    public void CancelSpellVisualKit_WriteToSpan_MatchesWrite()
    {
        var packet1 = new CancelSpellVisualKit { Source = WowGuid128.Create(HighGuidType703.Player, 37), SpellVisualKitID = 270 };
        packet1.Write();
        packet1.WritePacketData();
        byte[] byteBufferData = packet1.GetData()!;

        var packet2 = new CancelSpellVisualKit { Source = WowGuid128.Create(HighGuidType703.Player, 37), SpellVisualKitID = 270 };
        byte[] spanBuffer = new byte[packet2.MaxSize];
        int written = packet2.WriteToSpan(spanBuffer);

        Assert.True(written > 0);
        Assert.True(written <= packet2.MaxSize);
        Assert.True(byteBufferData.Length == written,
            $"ByteBuffer={System.BitConverter.ToString(byteBufferData)} Span={System.BitConverter.ToString(spanBuffer[..written])}");
        Assert.Equal(byteBufferData, spanBuffer[..written]);
    }
}

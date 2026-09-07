using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// CancelWindupKitOnGo: before forwarding the SPELL_GO that completes a local pressed cast, the proxy
/// sends SMSG_CANCEL_SPELL_VISUAL_KIT for the caster and each kit the spell's visual uses ONLY as a
/// caster-side wind-up (SpellVisualEvent start 3 -> end 13, TargetType 1). These tests pin the data
/// table the send rests on (CSV/SpellVisualWindupKits1.csv, generated from 1.14.2.42597), the packet
/// writer, and the decision seam. The wire ordering (cancel, then GO) has no socket seam in this
/// project and is verified by code position and the in-process field run.
/// </summary>
public class WindupKitCancelTests
{
    static WindupKitCancelTests()
    {
        GameData.LoadSpellVisualResolved();
        GameData.LoadSpellVisualWindupKits();
    }

    [Theory]
    [InlineData(241697u, 270u)] // Power Word: Shield 600 -> SpellVisual 784 -> holy wind-up 270
    [InlineData(238458u, 270u)] // Lesser Heal 2053 -> 285 -> 270
    [InlineData(239699u, 270u)] // Renew 6075 -> 280 -> 270
    [InlineData(240765u, 270u)] // Inner Fire 7128 -> 211 -> 270
    [InlineData(241717u, 224u)] // Lightning Shield 945 -> 37 -> 224
    public void ReporterSpells_ResolveToTheirWindupKit(uint spellXSpellVisualId, uint expectedKit)
    {
        var kits = GameData.GetWindupKitsForXSpellVisual(spellXSpellVisualId);
        Assert.Equal(1, kits.Length);
        Assert.Equal(expectedKit, kits[0]);
    }

    [Theory]
    [InlineData(237799u)] // Shadow Word: Pain 970 -> 71 -> kit 218, which is dual-use and therefore excluded
    [InlineData(0u)]      // no visual
    [InlineData(1u)]      // unknown SpellXSpellVisual
    public void SpellsWithoutAnExclusiveWindupKit_ResolveToNothing(uint spellXSpellVisualId)
    {
        Assert.Equal(0, GameData.GetWindupKitsForXSpellVisual(spellXSpellVisualId).Length);
    }

    [Theory]
    [InlineData(218u)] // shadow wind-up, also used under other event pairs
    [InlineData(726u)] // the most-reused dual-use kit in the 42597 table
    public void DualUseKits_NeverAppearInTheTable(uint kit)
    {
        foreach (var kits in GameData.SpellVisualWindupKits.Values)
            Assert.DoesNotContain(kit, kits);
    }

    [Fact]
    public void Table_IsNonTrivial_AndEveryVisualHasAtLeastOneKit()
    {
        Assert.True(GameData.SpellVisualWindupKits.Count > 1000, "the generated table should cover most cast visuals");
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

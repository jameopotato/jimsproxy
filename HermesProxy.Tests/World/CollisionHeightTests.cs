using System.IO;
using HermesProxy.World;
using HermesProxy.World.Client;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// Collision-visual-parity (#359): the synthesized SMSG_MOVE_SET_COLLISION_HEIGHT must agree
/// with the model the modern client renders — collision ≡ CollisionHeight × ModelScale ×
/// CMS_effective × wire scale, where CMS_effective is the hotfixed CreatureDisplayInfo CMS
/// when the proxy pushes one (tauren K=0.75), else the stock value.
///
/// Expected values are the field-measured/derived truth from the #359 investigation
/// (Tarren Mill doorway bracketed in (3.012, 3.299) by dire-bear-fits vs ♀-humanoid-stuck;
/// old formula sent ♀ tauren 3.29861 while she rendered at 2.47396).
/// </summary>
public class CollisionHeightTests
{
    static CollisionHeightTests()
    {
        // Explicit-path loads: keep these tests independent of LegacyVersion/ModernVersion
        // static-init order (other test classes may have seeded different builds).
        GameData.LoadCreatureDisplayInfo();
        GameData.LoadCreatureModelCollisionHeights(Path.Combine("CSV", "CreatureModelCollisionHeightsModern1.csv"));
        GameData.LoadCreatureDisplayInfoHotfixes(Path.Combine("CSV", "Hotfix", "CreatureDisplayInfo1.csv"));
    }

    [Theory]
    // Female tauren humanoid (display 60): THE #359 bug. Old formula sent 3.29861 (stuck in
    // doorways she visibly cleared); render truth is 2.1111112 × 1.25 × 0.75(hotfix) × 1.25.
    [InlineData(60, 1.25f, 2.47396f)]
    // Male tauren humanoid (display 59): same inflation class (old: 3.01219); render truth
    // 1.6527777 × 1 × 1.0125(hotfix) × 1.35.
    [InlineData(59, 1.35f, 2.25914f)]
    // Dire bear (display 2289, CMS 1.2, wire 1.2): already agreed with the visual — the form
    // that proved the door bracket. Must NOT change.
    [InlineData(2289, 1.2f, 3.0f)]
    // Bear (display 2281, CMS 1.0, wire 1.0): unchanged.
    [InlineData(2281, 1.0f, 2.08333f)]
    // Tauren cat (display 8571, CMS 1.0, wire 1.0, field-observed 2026-07-30): unchanged.
    [InlineData(8571, 1.0f, 2.08333f)]
    // Travel form (display 918, CMS 0.8, wire 0.8): the upstream Max() hitbox floor inflated
    // this to 1.66667; visual truth is 2.0833333 × 0.8 × 0.8.
    [InlineData(918, 0.8f, 1.33333f)]
    // Night-elf cat (display 892, CMS 0.9, wire 0.9): clamp-inflated to 1.875; truth 1.6875.
    [InlineData(892, 0.9f, 1.6875f)]
    // Human male (display 49, all factors 1): the control — byte-identical to the old path.
    [InlineData(49, 1.0f, 2.03127f)]
    // Gnome male (display 1563, CMS 1.15, Kronos wire 1.15 — corpus-verified): the clamp
    // coincided with the truth here (wire == CMS), so the value must not move.
    [InlineData(1563, 1.15f, 1.39597f)]
    // Scale-buffed gnome female (wire 1.4375 = 1.15 × 1.25, seen in field corpus): the old
    // formula dropped a CMS factor for non-baseline wire scales (sent 1.25 × 1.15 × 1.0 =
    // 1.4375 × CH ratio → 1.517-class values); truth is CH 1.0 × 1.15 × 1.4375.
    [InlineData(1564, 1.4375f, 1.65313f)]
    public void ComputeVisualCollisionHeight_MatchesRenderedModel(int displayId, float wireScale, float expected)
    {
        float height = WorldClient.ComputeVisualCollisionHeight(displayId, 0, wireScale);
        Assert.Equal(expected, height, 0.0001f);
    }

    [Fact]
    public void ComputeVisualCollisionHeight_UnknownDisplay_ReturnsZeroForCallSiteFallback()
    {
        // Unknown display → default display info (ModelId 0) → default model (height 0):
        // the call site substitutes PlayerHeight.Normal/Mounted.
        float height = WorldClient.ComputeVisualCollisionHeight(999999, 0, 1.0f);
        Assert.Equal(0f, height);
    }

    [Fact]
    public void HotfixDisplayScales_TaurenRowsLoaded()
    {
        // The parsed hotfix CMS dict is what keeps collision in agreement with the client's
        // hotfixed render — if these rows vanish, ComputeVisualCollisionHeight silently
        // reverts to stock CMS and #359 comes back.
        Assert.Equal(1.0125f, GameData.GetClientEffectiveDisplayScale(59), 0.0001f);
        Assert.Equal(0.75f, GameData.GetClientEffectiveDisplayScale(60), 0.0001f);
        // No hotfix row → stock CMS passes through.
        Assert.Equal(1.2f, GameData.GetClientEffectiveDisplayScale(2289), 0.0001f);
    }
}

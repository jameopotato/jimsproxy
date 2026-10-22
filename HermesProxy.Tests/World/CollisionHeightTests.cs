using HermesProxy.World;
using HermesProxy.World.Client;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// Collision-visual-parity (#359): the synthesized SMSG_MOVE_SET_COLLISION_HEIGHT must agree
/// with the model the modern client draws: CreatureModelData.CollisionHeight × (the scale the
/// proxy sends × the client's own display factor). With the unit-scale translation that drawn
/// size is the 1.12 server's SCALE_X, so the cylinder is the 1.12-sized model in every form.
///
/// #359 history: the Tarren Mill doorway was bracketed in (3.012, 3.299) (a dire bear sent
/// 3.000 fit, a female tauren sent 3.299 stuck). Every height below is under 3.012.
/// </summary>
public class CollisionHeightTests
{
    static CollisionHeightTests() => UnitScaleTestData.EnsureLoaded();

    [Theory]
    // Female tauren (display 60, server 1.25): CH 2.1111112 x 1.25. Was 2.47396 while she drew
    // at the hotfixed 1.17; now she draws at her 1.12 size, 1.25.
    [InlineData(60, 1.25f, 2.63889f)]
    // Male tauren (display 59, server 1.35): CH 1.6527777 x 1.35.
    [InlineData(59, 1.35f, 2.23125f)]
    // Dire bear (display 2289, server 1.2): CH 2.0833333 x 1.2. Was 3.0 at the doubled 1.44.
    [InlineData(2289, 1.2f, 2.5f)]
    // Bear (2281) and tauren cat (8571), CMS 1.0: unchanged.
    [InlineData(2281, 1.0f, 2.08333f)]
    [InlineData(8571, 1.0f, 2.08333f)]
    // Travel form (918, server 0.8): CH 2.0833333 x 0.8. Was 1.33333 at the doubled 0.64.
    [InlineData(918, 0.8f, 1.66667f)]
    // Night elf cat (892, server 0.9): was 1.6875 at the doubled 0.81.
    [InlineData(892, 0.9f, 1.875f)]
    // Human male (49), all factors 1: unchanged.
    [InlineData(49, 1.0f, 2.03127f)]
    // Gnome male (1563, server 1.15): CH 1.0555556 x 1.15. Was 1.39597 at the doubled 1.32.
    [InlineData(1563, 1.15f, 1.21389f)]
    // Scale-buffed gnome female (server 1.4375 = 1.15 x 1.25): CH 1.0 x 1.4375.
    [InlineData(1564, 1.4375f, 1.4375f)]
    // Hallow's End pirate costume (7113, server 1.5): CH 1.388889 x 1.5.
    [InlineData(7113, 1.5f, 2.08333f)]
    public void ComputeVisualCollisionHeight_MatchesDrawnModel(int displayId, float serverScale, float expected)
    {
        float factor = UnitScale.ClientDisplayFactor(new UnitScale.UnitInputs(true, (uint)displayId, 60, 0, false));
        float clientScale = UnitScale.ToClientObjectScale(serverScale, factor);
        float height = WorldClient.ComputeVisualCollisionHeight(displayId, 0, clientScale, factor);
        Assert.Equal(expected, height, 0.0001f);
    }

    [Fact]
    public void ComputeVisualCollisionHeight_UnknownDisplay_ReturnsZeroForCallSiteFallback()
    {
        // Unknown display → no model (height 0): the call site substitutes PlayerHeight.Normal/Mounted.
        float height = WorldClient.ComputeVisualCollisionHeight(999999, 0, 1.0f, 1.0f);
        Assert.Equal(0f, height);
    }

    [Fact]
    public void HotfixDisplayScales_TaurenRowsLoaded()
    {
        // The client draws tauren with the proxy's hotfixed CMS rows, so the factor (and the
        // collision height built from it) must read them, not the stock table.
        Assert.Equal(1.0125f, GameData.GetClientEffectiveDisplayScale(59), 0.0001f);
        Assert.Equal(0.75f, GameData.GetClientEffectiveDisplayScale(60), 0.0001f);
        // No hotfix row → stock CMS passes through.
        Assert.Equal(1.2f, GameData.GetClientEffectiveDisplayScale(2289), 0.0001f);
    }
}

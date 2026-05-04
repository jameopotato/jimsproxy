using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

public class GameObjectFlagsTranslationTests
{
    // Same-named bits should round-trip to the modern enum at the same value.
    [Theory]
    [InlineData(GameObjectFlagsLegacy.InUse,        GameObjectFlagsModern.InUse)]
    [InlineData(GameObjectFlagsLegacy.Locked,       GameObjectFlagsModern.Locked)]
    [InlineData(GameObjectFlagsLegacy.InteractCond, GameObjectFlagsModern.InteractCond)]
    [InlineData(GameObjectFlagsLegacy.Transport,    GameObjectFlagsModern.Transport)]
    [InlineData(GameObjectFlagsLegacy.Nodespawn,    GameObjectFlagsModern.Nodespawn)]
    public void ToModern_NameMatchedFlags_PassThrough(GameObjectFlagsLegacy legacy, GameObjectFlagsModern expected)
    {
        Assert.Equal(expected, legacy.ToModern());
    }

    // Vanilla NoInteract (0x10) is the rune-style "right-click blocker"; modern same-bit value
    // means NotSelectable ("not selectable even in GM mode"). Map explicitly to NotSelectable.
    [Fact]
    public void ToModern_NoInteract_MapsToNotSelectable()
    {
        var modern = GameObjectFlagsLegacy.NoInteract.ToModern();
        Assert.Equal(GameObjectFlagsModern.NotSelectable, modern);
    }

    // Vanilla Triggered (0x40) has no modern equivalent; modern bit 0x40 is AiObstacle, which
    // would mislabel any vanilla GO with TRIGGERED set. Drop it during translation.
    [Fact]
    public void ToModern_Triggered_IsDropped()
    {
        var modern = GameObjectFlagsLegacy.Triggered.ToModern();
        Assert.Equal((GameObjectFlagsModern)0, modern);
        Assert.False(modern.HasFlag(GameObjectFlagsModern.AiObstacle));
    }

    // Combined: rune template flags = 0x10 (NoInteract) + 0x20 (Nodespawn). Modern result must
    // include Nodespawn and NotSelectable, never AiObstacle.
    [Fact]
    public void ToModern_RuneTemplateFlags_TranslatesCorrectly()
    {
        var legacy = GameObjectFlagsLegacy.NoInteract | GameObjectFlagsLegacy.Nodespawn;
        var modern = legacy.ToModern();
        Assert.True(modern.HasFlag(GameObjectFlagsModern.NotSelectable));
        Assert.True(modern.HasFlag(GameObjectFlagsModern.Nodespawn));
        Assert.False(modern.HasFlag(GameObjectFlagsModern.AiObstacle));
    }
}

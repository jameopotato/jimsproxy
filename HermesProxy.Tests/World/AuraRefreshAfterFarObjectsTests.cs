using HermesProxy;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

public class AuraRefreshAfterFarObjectsTests
{
    static AuraRefreshAfterFarObjectsTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private static WowGuid128 Creature(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, 12345, counter);

    [Fact]
    public void NeedsFullAuraRefresh_FreshState_Empty()
    {
        var state = GameSessionData.CreateForTesting();
        Assert.Empty(state.NeedsFullAuraRefresh);
    }

    [Fact]
    public void NeedsFullAuraRefresh_AddThenRemove_TrueOnFirstRemove()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Creature(1);
        state.NeedsFullAuraRefresh.Add(guid);

        Assert.True(state.NeedsFullAuraRefresh.Remove(guid));
        Assert.False(state.NeedsFullAuraRefresh.Remove(guid));
    }

    [Fact]
    public void NeedsFullAuraRefresh_RemoveUnknownGuid_False()
    {
        var state = GameSessionData.CreateForTesting();
        Assert.False(state.NeedsFullAuraRefresh.Remove(Creature(99)));
    }

    [Fact]
    public void NeedsFullAuraRefresh_AddTwice_DedupedToOne()
    {
        var state = GameSessionData.CreateForTesting();
        var guid = Creature(1);
        state.NeedsFullAuraRefresh.Add(guid);
        state.NeedsFullAuraRefresh.Add(guid);

        Assert.Single(state.NeedsFullAuraRefresh);
    }

    [Fact]
    public void NeedsFullAuraRefresh_SeparateGuids_TrackedIndependently()
    {
        var state = GameSessionData.CreateForTesting();
        var a = Creature(1);
        var b = Creature(2);
        state.NeedsFullAuraRefresh.Add(a);

        Assert.True(state.NeedsFullAuraRefresh.Remove(a));
        Assert.False(state.NeedsFullAuraRefresh.Remove(b));
    }
}

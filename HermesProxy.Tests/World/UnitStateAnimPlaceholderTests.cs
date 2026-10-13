using System.Runtime.CompilerServices;
using HermesProxy;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#135, #14): every unit and player create must carry the empty state-animation id
// a native server sends. Without it the client keeps its default (0, a forced Stand) on top of
// the idle it selects, which hides the stealth crouch idle. Game objects already had the value.
public class UnitStateAnimPlaceholderTests
{
    static UnitStateAnimPlaceholderTests()
    {
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private static GlobalSessionData CreateSession()
    {
        var session = (GlobalSessionData)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSessionData));
        session.GameState = WowGuidTestHelper.CreateMockGameSessionData();
        session.GameState.CurrentPlayerGuid = WowGuid128.Create(HighGuidType703.Player, 99);
        return session;
    }

    [Fact]
    public void EmptyStateAnimId_Is1672_On1142()
    {
        Assert.Equal(1672u, ModernVersion.GetEmptyStateAnimId());
    }

    [Fact]
    public void CreatureCreate_SetsEmptyStateAnimId()
    {
        var guid = WowGuid128.Create(HighGuidType703.Creature, 0, 1234, 1);
        var update = new ObjectUpdate(guid, UpdateTypeModern.CreateObject1, CreateSession());

        update.InitializePlaceholders();

        Assert.Equal(ModernVersion.GetEmptyStateAnimId(), update.UnitData.StateAnimID);
    }

    [Fact]
    public void PlayerCreate_SetsEmptyStateAnimId()
    {
        var guid = WowGuid128.Create(HighGuidType703.Player, 1);
        var update = new ObjectUpdate(guid, UpdateTypeModern.CreateObject2, CreateSession());

        update.InitializePlaceholders();

        Assert.Equal(ModernVersion.GetEmptyStateAnimId(), update.UnitData.StateAnimID);
    }

    [Fact]
    public void Create_KeepsAnExplicitStateAnimId()
    {
        var guid = WowGuid128.Create(HighGuidType703.Creature, 0, 1234, 1);
        var update = new ObjectUpdate(guid, UpdateTypeModern.CreateObject1, CreateSession());
        update.UnitData.StateAnimID = 5;

        update.InitializePlaceholders();

        Assert.Equal(5u, update.UnitData.StateAnimID);
    }

    [Fact]
    public void ValuesUpdate_LeavesStateAnimIdUnset()
    {
        var guid = WowGuid128.Create(HighGuidType703.Creature, 0, 1234, 1);
        var update = new ObjectUpdate(guid, UpdateTypeModern.Values, CreateSession());

        update.InitializePlaceholders();

        Assert.Null(update.UnitData.StateAnimID);
    }

    [Fact]
    public void GameObjectCreate_StillGetsEmptyStateAnimId()
    {
        var guid = WowGuid128.Create(HighGuidType703.GameObject, 0, 5678, 1);
        var update = new ObjectUpdate(guid, UpdateTypeModern.CreateObject1, CreateSession());

        update.InitializePlaceholders();

        Assert.Equal(ModernVersion.GetEmptyStateAnimId(), update.GameObjectData.StateAnimID);
    }
}

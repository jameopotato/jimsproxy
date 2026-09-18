using Framework.Constants;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#394 looping cast kit): every packet of a cast's lifecycle must travel the
// SAME client connection. SMSG_SPELL_PREPARE used to omit its ConnectionType, so the
// one-argument ServerPacket ctor gave it ConnectionType.Realm — the default meant for
// character-select traffic — while SMSG_SPELL_START / SMSG_SPELL_GO / SMSG_CAST_FAILED
// declared Instance. Those are two separate TCP connections. TCP orders bytes within a
// connection and guarantees nothing between two, so the client could handle the START
// before the PREPARE's re-key and mint a duplicate cast object that stranded holding the
// wind-up kit: the looping cast sound, the held pose and the stuck action button.
//
// TrinityCore declares all four on CONNECTION_TYPE_INSTANCE, which is why a native realm
// never produces it. These tests pin the whole family so a future packet cannot inherit
// the realm default unnoticed.
public class CastLifecycleConnectionTests
{
    static CastLifecycleConnectionTests()
    {
        // ServerPacket construction resolves opcodes through ModernVersion, whose static
        // ctor needs a real build — the same guard every packet-constructing test uses so
        // the class runs green in isolation.
        if (global::Framework.Settings.ClientBuild == HermesProxy.Enums.ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = HermesProxy.Enums.ClientVersionBuild.V1_14_2_42597;
    }

    [Fact]
    public void SpellPrepare_TravelsTheInstanceConnection()
    {
        Assert.Equal(ConnectionType.Instance, new SpellPrepare().GetConnection());
    }

    [Fact]
    public void EveryCastLifecyclePacket_TravelsTheSameConnection()
    {
        var prepare = new SpellPrepare().GetConnection();

        Assert.Equal(prepare, new SpellStart().GetConnection());
        Assert.Equal(prepare, new SpellGo().GetConnection());
        Assert.Equal(prepare, new CastFailed().GetConnection());
        Assert.Equal(prepare, new SpellFailure().GetConnection());
    }

    // The wind-up kit cancel of #525 is emitted immediately before the SPELL_GO it rides in
    // front of and depends on arriving first, so it must share the GO's connection. Native
    // puts the spell-visual packets on the realm connection; this is a deliberate deviation
    // we depend on, and 1.12 gives us no separate kit stream to key it to instead.
    [Fact]
    public void WindupKitCancel_SharesTheConnectionOfTheGoItPrecedes()
    {
        Assert.Equal(new SpellGo().GetConnection(), new CancelSpellVisualKit().GetConnection());
        Assert.Equal(new SpellGo().GetConnection(), new CancelSpellVisual().GetConnection());
    }
}

using System.Text.Json;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (cast-id breadcrumbs): the cast.wire payload names every local cast id as it leaves for
// the client, and spell.cancel_cast pairs an Esc press with the pending request that owns the id.
public class CastIdBreadcrumbsTests
{
    private static readonly WowGuid128 Player = new(0x1234, 0x0000_0000_0000_0010);
    private static readonly WowGuid128 Other = new(0x9999, 0x0000_0000_0000_0010);

    private static JsonElement Json(object? payload) => JsonSerializer.SerializeToElement(payload);

    [Fact]
    public void Hex_FormatsHighThenLow_FixedWidth()
    {
        Assert.Equal("0x000000000000ABCD0000000000001234", CastIdBreadcrumbs.Hex(new WowGuid128(0x1234, 0xABCD)));
        Assert.Equal("0x00000000000000000000000000000000", CastIdBreadcrumbs.Hex(WowGuid128.Empty));
    }

    [Fact]
    public void Describe_Prepare_CarriesBothIds()
    {
        var prepare = new SpellPrepare { ClientCastID = new WowGuid128(1, 2), ServerCastID = new WowGuid128(3, 4) };
        var json = Json(CastIdBreadcrumbs.Describe(prepare, Player));
        Assert.Equal("prepare", json.GetProperty("packet").GetString());
        Assert.Equal(new WowGuid128(1, 2).ToString(), json.GetProperty("client_cast_id").GetString());
        Assert.Equal(CastIdBreadcrumbs.Hex(new WowGuid128(1, 2)), json.GetProperty("client_cast_id_hex").GetString());
        Assert.Equal(new WowGuid128(3, 4).ToString(), json.GetProperty("cast_id").GetString());
        Assert.Equal(CastIdBreadcrumbs.Hex(new WowGuid128(3, 4)), json.GetProperty("cast_id_hex").GetString());
    }

    [Fact]
    public void Describe_StartAndGo_LocalCasterOnly()
    {
        var start = new SpellStart();
        start.Cast.CasterGUID = Player;
        start.Cast.CasterUnit = Player;
        start.Cast.SpellID = 11689;
        start.Cast.CastID = new WowGuid128(77, 0);
        var json = Json(CastIdBreadcrumbs.Describe(start, Player));
        Assert.Equal("start", json.GetProperty("packet").GetString());
        Assert.Equal(11689, json.GetProperty("spell_id").GetInt32());
        Assert.Equal(new WowGuid128(77, 0).ToString(), json.GetProperty("cast_id").GetString());
        Assert.Equal(CastIdBreadcrumbs.Hex(new WowGuid128(77, 0)), json.GetProperty("cast_id_hex").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("original_cast_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("original_cast_id_hex").ValueKind);

        var go = new SpellGo();
        go.Cast.CasterGUID = Player;
        go.Cast.CasterUnit = Player;
        go.Cast.CastID = new WowGuid128(78, 0);
        go.Cast.OriginalCastID = new WowGuid128(77, 0);
        json = Json(CastIdBreadcrumbs.Describe(go, Player));
        Assert.Equal("go", json.GetProperty("packet").GetString());
        Assert.Equal(new WowGuid128(77, 0).ToString(), json.GetProperty("original_cast_id").GetString());
        Assert.Equal(CastIdBreadcrumbs.Hex(new WowGuid128(77, 0)), json.GetProperty("original_cast_id_hex").GetString());

        var observed = new SpellGo();
        observed.Cast.CasterGUID = Other;
        observed.Cast.CasterUnit = Other;
        Assert.Null(CastIdBreadcrumbs.Describe(observed, Player));
    }

    [Fact]
    public void Describe_Failures_CastFailedAlways_SpellFailureLocalOnly()
    {
        var failed = new CastFailed { CastID = new WowGuid128(5, 6), SpellID = 11661, Reason = 170 };
        var json = Json(CastIdBreadcrumbs.Describe(failed, Player));
        Assert.Equal("cast_failed", json.GetProperty("packet").GetString());
        Assert.Equal(170u, json.GetProperty("reason").GetUInt32());
        Assert.Equal(new WowGuid128(5, 6).ToString(), json.GetProperty("cast_id").GetString());
        Assert.Equal(CastIdBreadcrumbs.Hex(new WowGuid128(5, 6)), json.GetProperty("cast_id_hex").GetString());

        var failure = new SpellFailure { CasterUnit = Player, CastID = new WowGuid128(7, 8), SpellID = 11661, Reason = 28 };
        json = Json(CastIdBreadcrumbs.Describe(failure, Player));
        Assert.Equal("spell_failure", json.GetProperty("packet").GetString());
        Assert.Equal(28u, json.GetProperty("reason").GetUInt32());

        failure.CasterUnit = Other;
        Assert.Null(CastIdBreadcrumbs.Describe(failure, Player));
    }

    [Fact]
    public void Describe_UnrelatedPacket_ReturnsNull()
    {
        Assert.Null(CastIdBreadcrumbs.Describe(new SpellFailedOther { CasterUnit = Player }, Player));
    }

    [Fact]
    public void FindPendingCastByCastId_MatchesClientOrServerId_AcrossSlots()
    {
        var session = GameSessionData.CreateForTesting();
        var normal = new ClientCastRequest { SpellId = 11689, ClientGUID = new WowGuid128(1, 0), ServerGUID = new WowGuid128(2, 0) };
        var pet = new ClientCastRequest { SpellId = 3110, ClientGUID = new WowGuid128(3, 0), ServerGUID = new WowGuid128(4, 0) };
        var held = new ClientCastRequest { SpellId = 11661, ClientGUID = new WowGuid128(5, 0) };
        var heldCastTime = new ClientCastRequest { SpellId = 172, ClientGUID = new WowGuid128(6, 0) };
        var melee = new ClientCastRequest { SpellId = 78, ClientGUID = new WowGuid128(7, 0) };
        var autoRepeat = new ClientCastRequest { SpellId = 75, ClientGUID = new WowGuid128(8, 0) };
        session.PendingNormalCasts.Enqueue(normal);
        session.PendingPetCasts.Enqueue(pet);
        session.ForceHoldCast(held);
        session.HoldCastDuringCastTime(heldCastTime);
        session.CurrentClientNextMeleeCast = melee;
        session.CurrentClientAutoRepeatCast = autoRepeat;

        Assert.Same(normal, session.FindPendingCastByCastId(new WowGuid128(1, 0)));
        Assert.Same(normal, session.FindPendingCastByCastId(new WowGuid128(2, 0)));
        Assert.Same(pet, session.FindPendingCastByCastId(new WowGuid128(4, 0)));
        Assert.Same(held, session.FindPendingCastByCastId(new WowGuid128(5, 0)));
        Assert.Same(heldCastTime, session.FindPendingCastByCastId(new WowGuid128(6, 0)));
        Assert.Same(melee, session.FindPendingCastByCastId(new WowGuid128(7, 0)));
        Assert.Same(autoRepeat, session.FindPendingCastByCastId(new WowGuid128(8, 0)));
        Assert.Null(session.FindPendingCastByCastId(new WowGuid128(42, 0)));
        Assert.Null(session.FindPendingCastByCastId(WowGuid128.Empty));
    }
}

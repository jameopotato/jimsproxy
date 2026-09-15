using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (ranged auto-repeat): the press START is left open for the whole series (never paired
// with a GO, like a native server's), so its forwarded-START FIFO copy has to leave with the slot —
// otherwise the next same-spell terminator or GO would pop a dead series' id. The handler wiring
// needs a live socket; these pin the slot-end rule on the session state.
public class AutoRepeatSlotEndTests
{
    private const uint AutoShot = 75;
    private const uint Shoot = 5019;
    private static readonly WowGuid128 PressStart = new(75 + 0x1234, 0xCA57);
    private static readonly WowGuid128 RetargetStart = new(0x99, 0xCA57);

    private static GameSessionData SessionWithSeries(WowGuid128? pendingRetarget = null)
    {
        var session = GameSessionData.CreateForTesting();
        session.CurrentClientAutoRepeatCast = new ClientCastRequest
        {
            SpellId = AutoShot,
            ServerGUID = PressStart,
            PendingNaturalStartCastId = pendingRetarget,
        };
        session.EnqueueForwardedStartCastId(AutoShot, PressStart);
        if (pendingRetarget is { } retarget)
            session.EnqueueForwardedStartCastId(AutoShot, retarget);
        return session;
    }

    [Fact]
    public void EndingTheSlot_ReleasesThePressStartFifoCopy()
    {
        var session = SessionWithSeries();
        session.EndAutoRepeatSlot();
        Assert.Null(session.CurrentClientAutoRepeatCast);
        Assert.False(session.TryPeekForwardedStartCastId(AutoShot, out _));
    }

    [Fact]
    public void EndingTheSlot_ReleasesAPendingRetargetStartToo()
    {
        var session = SessionWithSeries(RetargetStart);
        session.EndAutoRepeatSlot();
        Assert.Null(session.CurrentClientAutoRepeatCast);
        Assert.False(session.TryPeekForwardedStartCastId(AutoShot, out _));
    }

    [Fact]
    public void EndingTheSlot_LeavesOtherSpellsStartsAlone()
    {
        var session = SessionWithSeries();
        var wandStart = new WowGuid128(0x42, 0xCA57);
        session.EnqueueForwardedStartCastId(Shoot, wandStart);
        session.EndAutoRepeatSlot();
        Assert.True(session.TryPeekForwardedStartCastId(Shoot, out var kept));
        Assert.Equal(wandStart, kept);
    }

    [Fact]
    public void EndingAnEmptySlot_IsANoOp()
    {
        var session = GameSessionData.CreateForTesting();
        session.EnqueueForwardedStartCastId(AutoShot, PressStart);
        session.EndAutoRepeatSlot();
        Assert.Null(session.CurrentClientAutoRepeatCast);
        Assert.True(session.TryPeekForwardedStartCastId(AutoShot, out _));
    }
}

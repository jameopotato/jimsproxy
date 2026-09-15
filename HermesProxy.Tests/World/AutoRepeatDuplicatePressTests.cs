using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (ranged auto-repeat): a duplicate press for the running series is never forwarded, but
// each one leaves a client-minted cast object that only an answer or the series end frees, and the
// client's ring holds 100. The answers are held and sent together right after the next tick GO,
// outside the swing-timer aim window; an over-full hold answers on arrival. The handler wiring needs
// a live socket; these pin the hold rule on the session state.
public class AutoRepeatDuplicatePressTests
{
    private const uint AutoShot = 75;
    private static readonly WowGuid128 PressStart = new(75 + 0x1234, 0xCA57);

    private static GameSessionData SessionWithSeries()
    {
        var session = GameSessionData.CreateForTesting();
        session.CurrentClientAutoRepeatCast = new ClientCastRequest { SpellId = AutoShot, ServerGUID = PressStart };
        return session;
    }

    private static ClientCastRequest Duplicate(ulong counter) =>
        new() { SpellId = AutoShot, ClientGUID = new WowGuid128(counter, 0xBC) };

    [Fact]
    public void Duplicates_AreHeld_AndTakenTogetherAfterTheTickGo_OldestFirst()
    {
        var session = SessionWithSeries();
        Assert.True(session.HoldAutoRepeatDuplicatePress(Duplicate(3)));
        Assert.True(session.HoldAutoRepeatDuplicatePress(Duplicate(4)));
        Assert.True(session.HoldAutoRepeatDuplicatePress(Duplicate(5)));
        var held = session.TakeHeldAutoRepeatDuplicatePresses();
        Assert.NotNull(held);
        Assert.Equal(new ulong[] { 3, 4, 5 }, held.ConvertAll(d => d.ClientGUID.Low));
        Assert.Null(session.TakeHeldAutoRepeatDuplicatePresses());
    }

    [Fact]
    public void AFullHold_AnswersTheNextDuplicateOnArrival_AndKeepsWhatItHolds()
    {
        var session = SessionWithSeries();
        for (ulong i = 0; i < GameSessionData.MaxHeldAutoRepeatDuplicatePresses; i++)
            Assert.True(session.HoldAutoRepeatDuplicatePress(Duplicate(10 + i)));
        Assert.False(session.HoldAutoRepeatDuplicatePress(Duplicate(999)));
        var held = session.TakeHeldAutoRepeatDuplicatePresses();
        Assert.NotNull(held);
        Assert.Equal(GameSessionData.MaxHeldAutoRepeatDuplicatePresses, held.Count);
        Assert.DoesNotContain(held, d => d.ClientGUID.Low == 999);
        Assert.True(session.HoldAutoRepeatDuplicatePress(Duplicate(1000)));
    }

    [Fact]
    public void EndingTheSlot_DropsTheHold_TheClientEndsThoseObjectsItself()
    {
        var session = SessionWithSeries();
        session.HoldAutoRepeatDuplicatePress(Duplicate(3));
        session.EndAutoRepeatSlot();
        Assert.Null(session.TakeHeldAutoRepeatDuplicatePresses());
        session.CurrentClientAutoRepeatCast = new ClientCastRequest { SpellId = AutoShot, ServerGUID = PressStart };
        Assert.Null(session.TakeHeldAutoRepeatDuplicatePresses());
    }

    [Fact]
    public void WithoutASeries_NothingIsHeld()
    {
        var session = GameSessionData.CreateForTesting();
        Assert.False(session.HoldAutoRepeatDuplicatePress(Duplicate(3)));
        Assert.Null(session.TakeHeldAutoRepeatDuplicatePresses());
    }
}

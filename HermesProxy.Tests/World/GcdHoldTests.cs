using System;
using System.Threading;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy issue #43: state-machine tests for the GCD hold-and-fire path in GameSessionData.
public class GcdHoldTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest MakeCast(uint spellId)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            Timestamp = Environment.TickCount,
        };
    }

    [Fact]
    public void IsGcdHoldActive_BeforeBeginGcd_ReturnsFalse()
    {
        var session = NewSession();
        Assert.False(session.IsGcdHoldActive());
    }

    [Fact]
    public void BeginGcd_WithFutureExpiry_ActivatesHold()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.BeginGcd(expireAtTickMs: now + 1500, fireAtTickMs: now + 1500);

        Assert.True(session.IsGcdHoldActive());
    }

    [Fact]
    public void BeginGcd_WithPastExpiry_IsNotActive()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.BeginGcd(expireAtTickMs: now - 1000, fireAtTickMs: now - 1000);

        Assert.False(session.IsGcdHoldActive());
    }

    [Fact]
    public void TryHoldCastDuringGcd_WhenActive_StoresCast()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.BeginGcd(now + 5000, now + 5000);

        var cast = MakeCast(133);
        bool held = session.TryHoldCastDuringGcd(cast, out var displaced);

        Assert.True(held);
        Assert.Null(displaced);
        Assert.Same(cast, session.PeekHeldGcdCast());
    }

    [Fact]
    public void TryHoldCastDuringGcd_WhenGcdAlreadyExpired_ReturnsFalse()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.BeginGcd(now - 1, now - 1);

        var cast = MakeCast(133);
        bool held = session.TryHoldCastDuringGcd(cast, out var displaced);

        Assert.False(held);
        Assert.Null(displaced);
        Assert.Null(session.PeekHeldGcdCast());
    }

    [Fact]
    public void TryHoldCastDuringGcd_OverwritesPreviousCast_ReturnsDisplaced()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.BeginGcd(now + 5000, now + 5000);

        var first = MakeCast(133);
        var second = MakeCast(172);

        session.TryHoldCastDuringGcd(first, out _);
        bool held = session.TryHoldCastDuringGcd(second, out var displaced);

        Assert.True(held);
        Assert.Same(first, displaced);
        Assert.Same(second, session.PeekHeldGcdCast());
    }

    [Fact]
    public void CancelGcdHold_ClearsStateAndDeactivates()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.BeginGcd(now + 5000, now + 5000);
        session.TryHoldCastDuringGcd(MakeCast(133), out _);

        session.CancelGcdHold();

        Assert.False(session.IsGcdHoldActive());
        Assert.Null(session.PeekHeldGcdCast());
    }

    [Fact]
    public void Timer_WithHeldCast_InvokesCallbackWithThatCast()
    {
        var session = NewSession();
        ClientCastRequest? fired = null;
        var signal = new ManualResetEventSlim(false);

        session.OnGcdHeldCastFire = cast =>
        {
            fired = cast;
            signal.Set();
        };

        var held = MakeCast(133);
        long now = Environment.TickCount64;
        session.BeginGcd(expireAtTickMs: now + 50, fireAtTickMs: now + 50);
        session.TryHoldCastDuringGcd(held, out _);

        Assert.True(signal.Wait(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken), "timer did not fire within 2 seconds");
        Assert.Same(held, fired);
        Assert.Null(session.PeekHeldGcdCast()); // slot cleared after fire
    }

    [Fact]
    public void Timer_WithNoHeldCast_DoesNotInvokeCallback()
    {
        var session = NewSession();
        int callCount = 0;
        session.OnGcdHeldCastFire = _ => Interlocked.Increment(ref callCount);

        long now = Environment.TickCount64;
        session.BeginGcd(now + 50, now + 50);
        // Intentionally skip TryHoldCastDuringGcd — nothing in the slot

        Thread.Sleep(150); // give the timer time to fire
        Assert.Equal(0, callCount);
    }

    [Fact]
    public void BeginGcd_CalledTwice_OnlySecondTimerFires()
    {
        var session = NewSession();
        int callCount = 0;
        var signal = new ManualResetEventSlim(false);
        ClientCastRequest? fired = null;
        session.OnGcdHeldCastFire = cast =>
        {
            Interlocked.Increment(ref callCount);
            fired = cast;
            signal.Set();
        };

        long now = Environment.TickCount64;

        // Simulates: cast A completes (BeginGcd), player mashes cast B, cast A's GCD is still
        // running when a NEW SMSG_SPELL_GO arrives and calls BeginGcd again. The second
        // BeginGcd must dispose the first timer so we get exactly one fire.
        session.BeginGcd(now + 2000, now + 2000);
        session.BeginGcd(now + 80, now + 80);
        session.TryHoldCastDuringGcd(MakeCast(200), out _);

        Assert.True(signal.Wait(TimeSpan.FromSeconds(2), Xunit.TestContext.Current.CancellationToken), "timer did not fire within 2 seconds");
        Thread.Sleep(100); // give the orphaned first timer a chance to misfire if it wasn't disposed
        Assert.Equal(1, callCount);
        Assert.Equal(200u, fired?.SpellId);
    }

    // JimsProxy issue #43 post-review hardening tests:

    [Fact]
    public void StaleTimer_QueuedBeforeBeginGcdReplacement_DoesNotClobberNewWindow()
    {
        // Reproduces the stale-Timer race described in the review:
        //   1. Timer A fires (end of GCD A). Its ThreadPool callback is queued but has not
        //      yet acquired _gcdLock.
        //   2. BeginGcd B runs: disposes Timer A (no-op vs queued callback), installs B.
        //   3. Player presses a cast during GCD B: held in _heldGcdCast.
        //   4. Stale Timer A callback finally runs. With the generation guard, it must bail
        //      without clobbering _heldGcdCast / _gcdExpireTimestampMs.
        //
        // We can't directly inject a "queued callback", but we can simulate it by using a
        // very short fire delay for timer A (so the callback is queued under load), then
        // immediately replacing it with BeginGcd B and checking the new window survives.

        var session = NewSession();
        int fireCount = 0;
        session.OnGcdHeldCastFire = _ => Interlocked.Increment(ref fireCount);

        long now = Environment.TickCount64;
        // Fire-at is in the past, forcing Timer A to queue its callback immediately.
        session.BeginGcd(expireAtTickMs: now + 5000, fireAtTickMs: now - 1);
        // Before the queued callback wins, replace with a much longer window.
        session.BeginGcd(expireAtTickMs: now + 5000, fireAtTickMs: now + 5000);

        Thread.Sleep(100); // let the stale callback run

        // New window must still be active; stale callback must not have cleared it.
        Assert.True(session.IsGcdHoldActive());
        Assert.Equal(0, fireCount); // stale timer must NOT fire, new timer hasn't either
    }

    [Fact]
    public void CancelGcdHold_AfterBeginGcd_PreventsPendingTimerFiring()
    {
        // Regression guard for bug_010: when GameState is replaced on logout, CancelGcdHold
        // must bump the generation so a Timer whose callback is in flight will no-op.
        var session = NewSession();
        int fireCount = 0;
        session.OnGcdHeldCastFire = _ => Interlocked.Increment(ref fireCount);

        long now = Environment.TickCount64;
        session.BeginGcd(now + 50, now + 50);
        session.TryHoldCastDuringGcd(MakeCast(42), out _);

        // Cancel before the timer has a chance to complete.
        session.CancelGcdHold();

        Thread.Sleep(150);
        Assert.Equal(0, fireCount);
        Assert.False(session.IsGcdHoldActive());
        Assert.Null(session.PeekHeldGcdCast());
    }

    [Fact]
    public void TryHoldCastDuringGcd_DisplacesPrevious_CallerSeesDisplaced()
    {
        // Bug 4 part 1: displaced cast must be surfaced so CMSG_CAST_SPELL can send a
        // resolution (SendCastRequestFailed). This test just proves the API surfaces
        // the displaced cast; the routing into SendCastRequestFailed is wired in the
        // SpellHandler and is covered by subjective/user testing.
        var session = NewSession();
        long now = Environment.TickCount64;
        session.BeginGcd(now + 5000, now + 5000);

        var first = MakeCast(1);
        var second = MakeCast(2);
        var third = MakeCast(3);

        session.TryHoldCastDuringGcd(first, out var displaced1);
        session.TryHoldCastDuringGcd(second, out var displaced2);
        session.TryHoldCastDuringGcd(third, out var displaced3);

        Assert.Null(displaced1);
        Assert.Same(first, displaced2);
        Assert.Same(second, displaced3);
        Assert.Same(third, session.PeekHeldGcdCast());
    }

    // Second round of review (bug_001, bug_003, bug_004, bug_007):

    [Fact]
    public void CancelGcdHold_ReturnsPreviouslyHeldCast()
    {
        // bug_003: live-session callers (HandleCancelCast) need the dropped cast to
        // send SendCastRequestFailed so the client's cast-tracking map releases the entry.
        var session = NewSession();
        long now = Environment.TickCount64;
        session.BeginGcd(now + 5000, now + 5000);

        var cast = MakeCast(42);
        session.TryHoldCastDuringGcd(cast, out _);

        ClientCastRequest? dropped = session.CancelGcdHold();

        Assert.Same(cast, dropped);
        Assert.Null(session.PeekHeldGcdCast());
    }

    [Fact]
    public void CancelGcdHold_NoHeldCast_ReturnsNull()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.BeginGcd(now + 5000, now + 5000);
        // No TryHoldCastDuringGcd — slot is empty.

        ClientCastRequest? dropped = session.CancelGcdHold();

        Assert.Null(dropped);
    }

    [Fact]
    public void CancelGcdHold_NullsFireCallback_StaleInvokeIsNoOp()
    {
        // bug_001: because OnGcdTimerElapsed invokes OnGcdHeldCastFire AFTER releasing
        // the lock, a stale callback that was already mid-flight when CancelGcdHold ran
        // would otherwise execute against a rotated GameState. CancelGcdHold nulls the
        // delegate to make a stale invoke a no-op.
        var session = NewSession();
        int callCount = 0;
        session.OnGcdHeldCastFire = _ => Interlocked.Increment(ref callCount);

        session.CancelGcdHold();

        Assert.Null(session.OnGcdHeldCastFire);
        Assert.Equal(0, callCount);
    }
}

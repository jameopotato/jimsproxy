using System;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (PR #161 follow-up): tests for DrainExpiredWatchdogCasts. Pure data
// operation — verifies that expired peeks get dequeued without affecting other
// pending casts or the unrelated pet queue.
public class WatchdogEvictionTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest MakeCast(uint spellId, long watchdogDeadlineMs = 0, bool hasStarted = false)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            Timestamp = Environment.TickCount,
            WatchdogDeadlineMs = watchdogDeadlineMs,
            HasStarted = hasStarted,
        };
    }

    [Fact]
    public void EmptyQueues_ReturnsEmptyEvictedLists()
    {
        var session = NewSession();
        session.DrainExpiredWatchdogCasts(Environment.TickCount64,
            out var normal, out var pet);
        Assert.Empty(normal);
        Assert.Empty(pet);
    }

    [Fact]
    public void DeadlineZero_NeverEvicted()
    {
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(1234, watchdogDeadlineMs: 0));
        session.DrainExpiredWatchdogCasts(Environment.TickCount64,
            out var normal, out var _);
        Assert.Empty(normal);
        Assert.Single(session.PendingNormalCasts);
    }

    [Fact]
    public void DeadlineInFuture_NotEvicted()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.PendingNormalCasts.Enqueue(MakeCast(1234, watchdogDeadlineMs: now + 5000));
        session.DrainExpiredWatchdogCasts(now,
            out var normal, out var _);
        Assert.Empty(normal);
        Assert.Single(session.PendingNormalCasts);
    }

    [Fact]
    public void DeadlineInPast_Evicted()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.PendingNormalCasts.Enqueue(MakeCast(1234, watchdogDeadlineMs: now - 1000, hasStarted: true));
        session.DrainExpiredWatchdogCasts(now,
            out var normal, out var _);
        Assert.Single(normal);
        Assert.Equal(1234u, normal[0].SpellId);
        Assert.True(normal[0].HasStarted);
        Assert.Empty(session.PendingNormalCasts);
    }

    [Fact]
    public void MixedQueue_OnlyExpiredEvicted_OthersKept()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.PendingNormalCasts.Enqueue(MakeCast(100, watchdogDeadlineMs: now - 500));   // expired
        session.PendingNormalCasts.Enqueue(MakeCast(200, watchdogDeadlineMs: 0));            // no watchdog
        session.PendingNormalCasts.Enqueue(MakeCast(300, watchdogDeadlineMs: now + 1000));   // future
        session.PendingNormalCasts.Enqueue(MakeCast(400, watchdogDeadlineMs: now - 100));    // expired

        session.DrainExpiredWatchdogCasts(now, out var normal, out var _);

        Assert.Equal(2, normal.Count);
        Assert.Contains(normal, c => c.SpellId == 100);
        Assert.Contains(normal, c => c.SpellId == 400);

        Assert.Equal(2, session.PendingNormalCasts.Count);
    }

    [Fact]
    public void PetQueue_DrainedSeparately()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        session.PendingNormalCasts.Enqueue(MakeCast(100, watchdogDeadlineMs: now - 500));
        session.PendingPetCasts.Enqueue(MakeCast(200, watchdogDeadlineMs: now - 500));

        session.DrainExpiredWatchdogCasts(now, out var normal, out var pet);

        Assert.Single(normal);
        Assert.Single(pet);
        Assert.Equal(100u, normal[0].SpellId);
        Assert.Equal(200u, pet[0].SpellId);
    }

    [Fact]
    public void HasStartedNormalCast_FalseAfterEvictingTheLeakedEntry()
    {
        // The actual symptom Jim flagged — leaked HasStarted=true entry blocks
        // HasStartedNormalCast() from returning false. After eviction it should
        // return false again, unblocking subsequent casts.
        var session = NewSession();
        long now = Environment.TickCount64;
        session.PendingNormalCasts.Enqueue(MakeCast(1234, watchdogDeadlineMs: now - 1000, hasStarted: true));

        Assert.True(session.HasStartedNormalCast());

        session.DrainExpiredWatchdogCasts(now, out var _, out var _);

        Assert.False(session.HasStartedNormalCast());
    }

    [Fact]
    public void DeadlineExactlyNow_NotEvicted()
    {
        // Strict <, not <=. A cast that just hit its deadline this same tick is
        // considered borderline — the natural CAST_FAILED may still arrive in the
        // same packet batch. Wait one more tick before evicting.
        var session = NewSession();
        long now = Environment.TickCount64;
        session.PendingNormalCasts.Enqueue(MakeCast(100, watchdogDeadlineMs: now));

        session.DrainExpiredWatchdogCasts(now, out var normal, out var _);

        Assert.Empty(normal);
        Assert.Single(session.PendingNormalCasts);
    }
}

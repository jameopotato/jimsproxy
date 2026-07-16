using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (cast-queue lock discipline): PendingNormalCasts / PendingPetCasts are mutated by
// compound "dequeue-all, filter, re-enqueue-survivors" helpers. ConcurrentQueue makes each
// individual operation atomic but cannot make a whole drain-rebuild atomic, and the mutators run
// on three unsynchronized contexts: the modern socket's IOCP receive thread (CMSG_CAST_SPELL —
// which calls RunWatchdogEviction on EVERY press), WorldClient's ReceiveLoop task (SPELL_START /
// SPELL_GO / CAST_FAILED), and the ThreadPool GCD hold-release timer.
//
// Before this fix DrainExpiredWatchdogCasts ran its drain-rebuild without PendingCastsLock, and
// the CMSG intake enqueued without it, so a press could interleave with an SMSG-side dequeue and
// scramble FIFO order — the ordering the START<->GO correspondence machinery depends on.
//
// These tests pin: (1) the hot path leaves the queues untouched when nothing is overdue, (2) an
// eviction preserves the order of survivors, and (3) concurrent enqueue/drain conserves every
// entry and preserves FIFO order.
public class PendingCastQueueLockingTests
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
    public void NothingOverdue_LeavesBothQueuesByReferenceAndOrder()
    {
        var session = NewSession();
        var normal = new[] { MakeCast(101), MakeCast(102), MakeCast(103) };
        var pet = new[] { MakeCast(201), MakeCast(202) };
        foreach (var c in normal) session.EnqueuePendingNormalCast(c);
        foreach (var c in pet) session.EnqueuePendingPetCast(c);

        session.DrainExpiredWatchdogCasts(Environment.TickCount64, out var evictedNormal, out var evictedPet);

        Assert.Empty(evictedNormal);
        Assert.Empty(evictedPet);
        // Same instances, same order — the per-press hot path must not churn the queues.
        Assert.Equal(normal, session.PendingNormalCasts.ToArray());
        Assert.Equal(pet, session.PendingPetCasts.ToArray());
    }

    [Fact]
    public void OverdueEviction_PreservesOrderOfSurvivors()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        var keepA = MakeCast(101);
        var overdue = MakeCast(102, watchdogDeadlineMs: now - 1);
        var keepB = MakeCast(103, watchdogDeadlineMs: now + 60_000);

        session.EnqueuePendingNormalCast(keepA);
        session.EnqueuePendingNormalCast(overdue);
        session.EnqueuePendingNormalCast(keepB);

        session.DrainExpiredWatchdogCasts(now, out var evicted, out _);

        Assert.Equal(new[] { overdue }, evicted);
        Assert.Equal(new[] { keepA, keepB }, session.PendingNormalCasts.ToArray());
    }

    // The regression test for the race itself.
    //
    // A timing stress test is the wrong tool here: the window between a drain's dequeue-all and
    // its re-enqueue-all is microseconds, so a racing-threads test passes almost every run even
    // against the unlocked code, and would be a flaky detector that lulls rather than guards.
    //
    // Instead assert the contract directly: every compound mutation of the pending-cast queues
    // must acquire PendingCastsLock. We hold the lock on this thread, invoke each mutator on
    // another, and require it to block — which is only true if it takes the lock. Against the old
    // unlocked DrainExpiredWatchdogCasts / raw Enqueue / pet-queue helpers, each of these fails
    // immediately.
    public static TheoryData<string, Action<GameSessionData>> Mutators() => new()
    {
        { "DrainExpiredWatchdogCasts", s => s.DrainExpiredWatchdogCasts(Environment.TickCount64, out _, out _) },
        { "DrainPendingCastsForDestroyedTarget", s => s.DrainPendingCastsForDestroyedTarget(
            new WowGuid128(1, 2), out _, out _) },
        { "EnqueuePendingNormalCast", s => s.EnqueuePendingNormalCast(MakeCast(1)) },
        { "EnqueuePendingPetCast", s => s.EnqueuePendingPetCast(MakeCast(1)) },
        { "TryDequeuePendingNormalCast", s => s.TryDequeuePendingNormalCast(1, out _) },
        { "TryDequeuePendingPetCast", s => s.TryDequeuePendingPetCast(1, out _) },
        { "ClearNonStartedNormalCasts", s => s.ClearNonStartedNormalCasts() },
        { "ClearNonStartedPetCasts", s => s.ClearNonStartedPetCasts() },
        { "ClearPendingPetCasts", s => s.ClearPendingPetCasts() },
        { "ResetInFlightCastState", s => s.ResetInFlightCastState() },
        { "ClearPendingNormalCasts", s => s.ClearPendingNormalCasts() },
        // 13261 = Malfunction Explosion, a known malfunction substitute — anything else
        // early-outs before the lock (see MalfunctionSubstituteToDevice).
        { "TryEvictForwardedItemUseCast", s => s.TryEvictForwardedItemUseCast(13261, out _) },
        { "TryDequeueItemCast", s => s.TryDequeueItemCast(new WowGuid128(1, 2), out _) },
    };

    [Theory]
    [MemberData(nameof(Mutators))]
    public void EveryQueueMutator_AcquiresPendingCastsLock(string name, Action<GameSessionData> mutate)
    {
        var session = NewSession();
        session.EnqueuePendingNormalCast(MakeCast(101));
        session.EnqueuePendingPetCast(MakeCast(201));

        using var entered = new ManualResetEventSlim(false);
        var worker = new Thread(() => { entered.Set(); mutate(session); });

        Monitor.Enter(session.PendingCastsLock);
        try
        {
            worker.Start();
            entered.Wait(TimeSpan.FromSeconds(5));
            // The mutator must NOT be able to run while we hold the lock.
            Assert.False(worker.Join(TimeSpan.FromMilliseconds(250)),
                $"{name} completed while PendingCastsLock was held — it does not take the lock.");
        }
        finally
        {
            Monitor.Exit(session.PendingCastsLock);
        }

        // Once released it must complete promptly.
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), $"{name} did not complete after the lock was released.");
    }

    // A force-closed STARTED cast must release its forwarded-START CastID, or the dead cast's id
    // stays at the head of the per-spell FIFO and the NEXT same-spell SPELL_GO pops it — stamping
    // the new cast's GO with the dead cast's CastID. The 1.14 client pairs START<->GO by CastID,
    // so the new cast would never close (stuck cast animation + looping cast sound).
    // This pins the invariant that RunWatchdogEviction and the SPELL_START/GO parse-failure drain
    // both rely on.
    [Fact]
    public void ReleasingAForceClosedCastId_LeavesTheNextCastsIdAtTheFifoHead()
    {
        var session = NewSession();
        const uint spellId = 13877; // Blade Flurry
        var deadCastId = new WowGuid128(0x1111111111111111, 0x2222222222222222);
        var liveCastId = new WowGuid128(0x3333333333333333, 0x4444444444444444);

        session.EnqueueForwardedStartCastId(spellId, deadCastId);
        session.EnqueueForwardedStartCastId(spellId, liveCastId);

        // The parse-failure drain / watchdog force-closes the first cast: remove BY VALUE.
        Assert.True(session.RemoveForwardedStartCastId(spellId, deadCastId));

        // The next same-spell GO must recover the LIVE cast's id, not the dead one's.
        Assert.True(session.TryPopForwardedStartCastId(spellId, out var recovered));
        Assert.Equal(liveCastId, recovered);
        Assert.False(session.TryPopForwardedStartCastId(spellId, out _));
    }
}

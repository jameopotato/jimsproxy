using System;
using HermesProxy;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
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

    // ---- Destroy-hook fast path tests ----

    private static WowGuid128 Creature(ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, 12345, counter);

    private static ClientCastRequest MakeCastWithTarget(uint spellId, WowGuid128 targetGuid, bool hasStarted = false)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            Timestamp = Environment.TickCount,
            TargetGuid = targetGuid,
            HasStarted = hasStarted,
        };
    }

    [Fact]
    public void DrainPendingCastsForDestroyedTarget_NoMatch_KeepsAll()
    {
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCastWithTarget(100, Creature(1)));
        session.PendingNormalCasts.Enqueue(MakeCastWithTarget(200, Creature(2)));

        session.DrainPendingCastsForDestroyedTarget(Creature(99),
            out var normal, out var pet);

        Assert.Empty(normal);
        Assert.Empty(pet);
        Assert.Equal(2, session.PendingNormalCasts.Count);
    }

    [Fact]
    public void DrainPendingCastsForDestroyedTarget_Match_EvictsOnly()
    {
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCastWithTarget(100, Creature(1), hasStarted: true));
        session.PendingNormalCasts.Enqueue(MakeCastWithTarget(200, Creature(2)));
        session.PendingNormalCasts.Enqueue(MakeCastWithTarget(300, Creature(1))); // also targets evicted unit

        session.DrainPendingCastsForDestroyedTarget(Creature(1),
            out var normal, out var pet);

        Assert.Equal(2, normal.Count);
        Assert.Contains(normal, c => c.SpellId == 100);
        Assert.Contains(normal, c => c.SpellId == 300);
        Assert.Single(session.PendingNormalCasts);
        Assert.Empty(pet);
    }

    [Fact]
    public void DrainPendingCastsForDestroyedTarget_EmptyTargetGuids_Ignored()
    {
        // AoE / self-cast spells have empty TargetGuid. Destroy hook should
        // not match them by accident even if the destroyed GUID is also empty.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCastWithTarget(100, default));
        session.PendingNormalCasts.Enqueue(MakeCastWithTarget(200, Creature(5)));

        session.DrainPendingCastsForDestroyedTarget(default,
            out var normal, out var _);

        Assert.Empty(normal);
        Assert.Equal(2, session.PendingNormalCasts.Count);
    }

    [Fact]
    public void DrainPendingCastsForDestroyedTarget_PetQueue_DrainedSeparately()
    {
        var session = NewSession();
        var dyingMob = Creature(1);
        session.PendingNormalCasts.Enqueue(MakeCastWithTarget(100, dyingMob));
        session.PendingPetCasts.Enqueue(MakeCastWithTarget(200, dyingMob));

        session.DrainPendingCastsForDestroyedTarget(dyingMob,
            out var normal, out var pet);

        Assert.Single(normal);
        Assert.Single(pet);
        Assert.Equal(100u, normal[0].SpellId);
        Assert.Equal(200u, pet[0].SpellId);
    }

    [Fact]
    public void DrainPendingCastsForDestroyedTarget_HasStartedNormalCast_FalseAfterEviction()
    {
        // The end-to-end symptom: cast-time spell on a target, target dies, server
        // sends only SMSG_SPELL_FAILURE (no trailing CAST_FAILED on Kronos). Without
        // the destroy hook the entry leaks. With the hook, SMSG_DESTROY_OBJECT for
        // the target evicts it immediately and HasStartedNormalCast returns false.
        var session = NewSession();
        var dyingMob = Creature(1);
        session.PendingNormalCasts.Enqueue(MakeCastWithTarget(100, dyingMob, hasStarted: true));

        Assert.True(session.HasStartedNormalCast());

        session.DrainPendingCastsForDestroyedTarget(dyingMob, out var _, out var _);

        Assert.False(session.HasStartedNormalCast());
    }

    // ---- Movement preemption tests ----

    private static ClientCastRequest MakeCastTimeCast(uint spellId, uint castTimeMs, bool hasStarted)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            Timestamp = Environment.TickCount,
            HasStarted = hasStarted,
            StartedCastTimeMs = castTimeMs,
        };
    }

    [Fact]
    public void MarkStartedCastsMovementCancelled_StartedCastTime_Marked()
    {
        var session = NewSession();
        long now = Environment.TickCount64;
        // 2.5s Frostbolt currently casting
        session.PendingNormalCasts.Enqueue(MakeCastTimeCast(116, castTimeMs: 2500, hasStarted: true));

        int marked = session.MarkStartedCastsMovementCancelled(now + 2500);

        Assert.Equal(1, marked);
        var entry = session.PendingNormalCasts.ToArray()[0];
        Assert.True(entry.MovementCancelled);
        Assert.Equal(now + 2500, entry.WatchdogDeadlineMs);
    }

    [Fact]
    public void MarkStartedCastsMovementCancelled_Instant_NotMarked()
    {
        // Instants don't get cancelled by movement in vanilla.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCastTimeCast(1953, castTimeMs: 0, hasStarted: true));

        int marked = session.MarkStartedCastsMovementCancelled(Environment.TickCount64 + 2500);

        Assert.Equal(0, marked);
        Assert.False(session.PendingNormalCasts.ToArray()[0].MovementCancelled);
    }

    [Fact]
    public void MarkStartedCastsMovementCancelled_NotStarted_NotMarked()
    {
        // Cast hasn't started yet (in-flight to server, awaiting SMSG_SPELL_START).
        // Movement at this stage doesn't apply because nothing's actually casting.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCastTimeCast(116, castTimeMs: 2500, hasStarted: false));

        int marked = session.MarkStartedCastsMovementCancelled(Environment.TickCount64 + 2500);

        Assert.Equal(0, marked);
        Assert.False(session.PendingNormalCasts.ToArray()[0].MovementCancelled);
    }

    [Fact]
    public void MarkStartedCastsMovementCancelled_PreservesExistingWatchdog()
    {
        // If a watchdog deadline was already set (e.g., from a prior peek),
        // movement marking shouldn't shorten it. We only fill in the gap.
        var session = NewSession();
        long now = Environment.TickCount64;
        var cast = MakeCastTimeCast(116, castTimeMs: 2500, hasStarted: true);
        cast.WatchdogDeadlineMs = now + 1000; // earlier deadline already armed
        session.PendingNormalCasts.Enqueue(cast);

        session.MarkStartedCastsMovementCancelled(now + 5000);

        var entry = session.PendingNormalCasts.ToArray()[0];
        Assert.True(entry.MovementCancelled);
        Assert.Equal(now + 1000, entry.WatchdogDeadlineMs);
    }

    [Fact]
    public void MarkStartedCastsMovementCancelled_MultipleEntries_OnlyStartedCastTimeMarked()
    {
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCastTimeCast(116, castTimeMs: 2500, hasStarted: true));   // marked
        session.PendingNormalCasts.Enqueue(MakeCastTimeCast(1953, castTimeMs: 0, hasStarted: true));    // instant — skip
        session.PendingNormalCasts.Enqueue(MakeCastTimeCast(133, castTimeMs: 2500, hasStarted: false)); // not started — skip

        int marked = session.MarkStartedCastsMovementCancelled(Environment.TickCount64 + 2500);

        Assert.Equal(1, marked);
        var arr = session.PendingNormalCasts.ToArray();
        Assert.True(arr[0].MovementCancelled);
        Assert.False(arr[1].MovementCancelled);
        Assert.False(arr[2].MovementCancelled);
    }
}

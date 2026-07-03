using System;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (H7 cast-go-mispair fix): SMSG_SPELL_GO must complete the cast that
// SMSG_SPELL_START opened. When an off-GCD instant is double-sent (Blade Flurry sends two
// CMSG_CAST_SPELL), the first press's START marks entry A (started) and the duplicate leaves
// entry B (unstarted) in the queue. TryDequeuePendingNormalCast must resolve the GO to the
// STARTED entry A so the forwarded START and GO carry the same CastID. The old "prefer
// unstarted" rule grabbed B and stamped the GO with a CastID the client never saw at START,
// stranding the client's cast visual → stuck cast animation + looping cast sound. The unstarted
// entry is still preferred when NO started entry exists (an instant whose SMSG_SPELL_START the
// server skipped), so HandleSpellGo can still send its SpellPrepare.
public class CastGoPreferStartedTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest MakeCast(uint spellId, bool hasStarted = false, bool isOffGcd = false)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            Timestamp = Environment.TickCount,
            HasStarted = hasStarted,
            IsOffGcd = isOffGcd,
        };
    }

    [Fact]
    public void TryDequeuePendingNormalCast_StartedAndUnstartedSameSpell_ReturnsStarted()
    {
        // The double-send shape: Blade Flurry (13877) press 1 started, duplicate press unstarted.
        var session = NewSession();
        var started = MakeCast(13877, hasStarted: true, isOffGcd: true);
        var duplicate = MakeCast(13877, hasStarted: false, isOffGcd: true);
        session.PendingNormalCasts.Enqueue(started);
        session.PendingNormalCasts.Enqueue(duplicate);

        bool ok = session.TryDequeuePendingNormalCast(13877, out var cast);

        Assert.True(ok);
        Assert.Same(started, cast);                  // GO resolves to the STARTED entry, not the duplicate
        Assert.Single(session.PendingNormalCasts);   // the duplicate stays for its own CAST_FAILED
        Assert.Same(duplicate, session.PendingNormalCasts.ToArray()[0]);
    }

    [Fact]
    public void TryDequeuePendingNormalCast_OnlyUnstartedSameSpell_ReturnsUnstarted()
    {
        // Skip-START instant: the server emits SMSG_SPELL_GO with no preceding SMSG_SPELL_START,
        // so the only pending entry is unstarted. It must still dequeue (the fallback the fix
        // preserves) so HandleSpellGo can synthesize the SpellPrepare for the client.
        var session = NewSession();
        var instant = MakeCast(11305, hasStarted: false, isOffGcd: false);
        session.PendingNormalCasts.Enqueue(instant);

        bool ok = session.TryDequeuePendingNormalCast(11305, out var cast);

        Assert.True(ok);
        Assert.Same(instant, cast);
        Assert.Empty(session.PendingNormalCasts);
    }

    [Fact]
    public void TryDequeuePendingNormalCast_TwoStartedSameSpell_ReturnsOldestFirst()
    {
        // Two genuine sequential casts of the same spell both in flight (both started): the GO
        // must complete the OLDER one first (FIFO among started), so successive GOs pair with
        // successive STARTs in order.
        var session = NewSession();
        var first = MakeCast(133, hasStarted: true);
        var second = MakeCast(133, hasStarted: true);
        session.PendingNormalCasts.Enqueue(first);
        session.PendingNormalCasts.Enqueue(second);

        bool ok = session.TryDequeuePendingNormalCast(133, out var cast);

        Assert.True(ok);
        Assert.Same(first, cast);                    // oldest started entry
        Assert.Single(session.PendingNormalCasts);
        Assert.Same(second, session.PendingNormalCasts.ToArray()[0]);
    }

    [Fact]
    public void TryDequeuePendingNormalCast_NoMatch_ReturnsFalseAndKeepsQueue()
    {
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(133, hasStarted: true));

        bool ok = session.TryDequeuePendingNormalCast(999, out var cast);

        Assert.False(ok);
        Assert.Null(cast);
        Assert.Single(session.PendingNormalCasts);   // unrelated entry untouched
    }

    [Fact]
    public void TryDequeuePendingNormalCast_DupRejectionPrefersUnstarted_SparesStarted()
    {
        // The OTHER ordering of the double-send: the duplicate's NOT_READY CAST_FAILED arrives
        // BEFORE the first press's SPELL_GO. With preferStarted:false it must consume the
        // unstarted duplicate and SPARE the started entry, so the GO can still pair to it.
        var session = NewSession();
        var started = MakeCast(13877, hasStarted: true, isOffGcd: true);
        var duplicate = MakeCast(13877, hasStarted: false, isOffGcd: true);
        session.PendingNormalCasts.Enqueue(started);
        session.PendingNormalCasts.Enqueue(duplicate);

        bool ok = session.TryDequeuePendingNormalCast(13877, out var cast, preferStarted: false);

        Assert.True(ok);
        Assert.Same(duplicate, cast);                                    // dup-rejection takes the UNSTARTED dup
        Assert.Single(session.PendingNormalCasts);
        Assert.Same(started, session.PendingNormalCasts.ToArray()[0]);   // started spared for its GO
    }

    [Fact]
    public void TryDequeuePendingNormalCast_DupRejectionNoUnstarted_FallsBackToStarted()
    {
        // preferStarted:false with no unstarted entry falls back to the started one (defensive).
        var session = NewSession();
        var started = MakeCast(133, hasStarted: true);
        session.PendingNormalCasts.Enqueue(started);

        bool ok = session.TryDequeuePendingNormalCast(133, out var cast, preferStarted: false);

        Assert.True(ok);
        Assert.Same(started, cast);
        Assert.Empty(session.PendingNormalCasts);
    }

    // --- Off-GCD double-send collapse (Phase 1.5) ---
    // The collapse fires in HandleCastSpell's off-GCD branch when this predicate is true,
    // dropping the duplicate before it becomes a second pending entry. These pin the predicate
    // semantics the collapse relies on.

    [Fact]
    public void HasNonStartedPendingCastForSpell_FirstPressUnstarted_True_CollapsesDup()
    {
        // press2-before-START: the first off-GCD press is still in flight (unstarted) when the
        // double-send duplicate arrives → the collapse fires, so the server casts once.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(13877, hasStarted: false, isOffGcd: true));

        Assert.True(session.HasNonStartedPendingCastForSpell(13877));
    }

    [Fact]
    public void HasNonStartedPendingCastForSpell_FirstPressStarted_False_DequeueBackstopHandlesIt()
    {
        // press2-after-START: the first press already started, so the collapse does NOT fire; the
        // duplicate enqueues and the caller-aware dequeue (preferStarted) is the backstop.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(13877, hasStarted: true, isOffGcd: true));

        Assert.False(session.HasNonStartedPendingCastForSpell(13877));
    }

    [Fact]
    public void HasNonStartedPendingCastForSpell_DifferentSpell_False()
    {
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(133, hasStarted: false));

        Assert.False(session.HasNonStartedPendingCastForSpell(13877));
    }
}

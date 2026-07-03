using System;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#394 GCD-boundary double-forward): HasNonStartedPendingCastForSpell is the
// decision predicate for BOTH layers of the fix — ForwardHeldGcdCast's supersede skip
// ("a fresh same-spell press already forwarded ⇒ don't double-forward the stale held press")
// and HandleCastFailed's transient-stale-drop ("no unstarted dup left ⇒ the transient
// rejection is stale, drop it before it consumes the STARTED cast").
public class HasNonStartedPendingCastTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest MakeCast(uint spellId, bool started = false)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            Timestamp = Environment.TickCount,
            HasStarted = started,
        };
    }

    [Fact]
    public void EmptyQueue_ReturnsFalse()
    {
        var session = NewSession();
        Assert.False(session.HasNonStartedPendingCastForSpell(1454));
    }

    [Fact]
    public void UnstartedSameSpell_ReturnsTrue()
    {
        // The #394 shape: a fresh press forwarded immediately at the GCD boundary sits
        // unstarted in the queue when the late timer callback fires the held press.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(1454));

        Assert.True(session.HasNonStartedPendingCastForSpell(1454));
    }

    [Fact]
    public void StartedSameSpell_ReturnsFalse()
    {
        // The stale-drop shape: on-START sweep already consumed the unstarted dup; only the
        // STARTED cast remains — a transient CAST_FAILED now has nothing legitimate to consume.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(1454, started: true));

        Assert.False(session.HasNonStartedPendingCastForSpell(1454));
    }

    [Fact]
    public void UnstartedDifferentSpell_ReturnsFalse()
    {
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(6222));

        Assert.False(session.HasNonStartedPendingCastForSpell(1454));
    }

    [Fact]
    public void LegacySpellIdMatch_ReturnsTrue()
    {
        // SoM-renumbered items: the legacy emulator replies with the old id.
        var session = NewSession();
        var cast = MakeCast(363880);
        cast.LegacySpellId = 17626;
        session.PendingNormalCasts.Enqueue(cast);

        Assert.True(session.HasNonStartedPendingCastForSpell(17626));
    }
}

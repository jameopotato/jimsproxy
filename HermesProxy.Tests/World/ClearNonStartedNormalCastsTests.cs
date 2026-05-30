using System;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (stuck-Bloodrage fix): ClearNonStartedNormalCasts must not sweep off-GCD
// casts. When a normal cast's SMSG_SPELL_START arrives it sweeps the other non-started
// pending casts to release their button-lit state — but off-GCD casts (Bloodrage,
// Sprint, racials, trinkets) coexist with the GCD cast and the server casts them
// independently. Sweeping them sent a premature CAST_FAILED while the real SPELL_GO
// still arrived unmatched, leaving the action-bar icon stuck-lit until relog. These
// tests pin the exemption: started AND off-GCD casts are kept; only non-started
// normal casts are cleared.
public class ClearNonStartedNormalCastsTests
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
    public void ClearNonStartedNormalCasts_EmptyQueue_ReturnsEmpty()
    {
        var session = NewSession();

        var cleared = session.ClearNonStartedNormalCasts();

        Assert.Empty(cleared);
        Assert.Empty(session.PendingNormalCasts);
    }

    [Fact]
    public void ClearNonStartedNormalCasts_NonStartedNormalCast_IsCleared()
    {
        // Stale queued press (Fireball) with no off-GCD exemption — the case the sweep
        // exists for: release its button-lit state when another cast starts.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(133, hasStarted: false, isOffGcd: false));

        var cleared = session.ClearNonStartedNormalCasts();

        Assert.Single(cleared);
        Assert.Equal(133u, cleared[0].SpellId);
        Assert.Empty(session.PendingNormalCasts);
    }

    [Fact]
    public void ClearNonStartedNormalCasts_StartedCast_IsKept()
    {
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(133, hasStarted: true));

        var cleared = session.ClearNonStartedNormalCasts();

        Assert.Empty(cleared);
        Assert.Single(session.PendingNormalCasts);
    }

    [Fact]
    public void ClearNonStartedNormalCasts_NonStartedOffGcdCast_IsKept()
    {
        // The fix: a pending off-GCD Bloodrage (2687) must survive the sweep so its
        // own SPELL_GO / CAST_FAILED resolves it instead of a premature failure.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(2687, hasStarted: false, isOffGcd: true));

        var cleared = session.ClearNonStartedNormalCasts();

        Assert.Empty(cleared);
        Assert.Single(session.PendingNormalCasts);
        Assert.Equal(2687u, session.PendingNormalCasts.ToArray()[0].SpellId);
    }

    [Fact]
    public void ClearNonStartedNormalCasts_BloodragePendingWhenSunderStarts_BloodrageKept()
    {
        // The exact stuck-lit scenario: Sunder Armor (7386, normal) has just started —
        // which triggers the sweep — while an off-GCD Bloodrage (2687) is still pending
        // and a stale queued Fireball (133) press is also sitting in the queue. Only the
        // stale Fireball should be cleared; the started Sunder and the off-GCD Bloodrage
        // are both kept.
        var session = NewSession();
        session.PendingNormalCasts.Enqueue(MakeCast(7386, hasStarted: true, isOffGcd: false));   // Sunder, started → kept
        session.PendingNormalCasts.Enqueue(MakeCast(2687, hasStarted: false, isOffGcd: true));   // Bloodrage off-GCD → kept (the fix)
        session.PendingNormalCasts.Enqueue(MakeCast(133, hasStarted: false, isOffGcd: false));   // stale Fireball press → cleared

        var cleared = session.ClearNonStartedNormalCasts();

        Assert.Single(cleared);
        Assert.Equal(133u, cleared[0].SpellId);

        Assert.Equal(2, session.PendingNormalCasts.Count);
        Assert.Contains(session.PendingNormalCasts, c => c.SpellId == 7386);
        Assert.Contains(session.PendingNormalCasts, c => c.SpellId == 2687);
    }

    [Fact]
    public void OffGcdCast_SurvivesSweep_ButWatchdogReapsItWhenDeadlineExpires()
    {
        // #344 follow-up backstop: HandleCastSpell arms WatchdogDeadlineMs on off-GCD casts
        // at enqueue. Since they're sweep-exempt and never become HasStarted, if their
        // SPELL_GO is ever lost the sweep keeps them — so the watchdog must reap them, else
        // they linger in PendingNormalCasts forever.
        var session = NewSession();
        var bloodrage = MakeCast(2687, hasStarted: false, isOffGcd: true);
        bloodrage.WatchdogDeadlineMs = Environment.TickCount64 - 1; // armed at enqueue, now expired
        session.PendingNormalCasts.Enqueue(bloodrage);

        // Sweep keeps the off-GCD cast (the #344 exemption)...
        var cleared = session.ClearNonStartedNormalCasts();
        Assert.Empty(cleared);
        Assert.Single(session.PendingNormalCasts);

        // ...but the watchdog reaps it once the deadline passes, so it can't leak.
        session.DrainExpiredWatchdogCasts(Environment.TickCount64, out var normalEvicted, out _);
        Assert.Single(normalEvicted);
        Assert.Equal(2687u, normalEvicted[0].SpellId);
        Assert.Empty(session.PendingNormalCasts);
    }

    [Fact]
    public void OffGcdCast_WithFreshWatchdogDeadline_NotReapedEarly()
    {
        // The armed window must be generous enough not to false-evict a legit off-GCD cast
        // before its SPELL_GO arrives — premature eviction would re-create the stuck-lit bug.
        var session = NewSession();
        var bloodrage = MakeCast(2687, hasStarted: false, isOffGcd: true);
        bloodrage.WatchdogDeadlineMs = Environment.TickCount64 + ClientCastRequest.WatchdogWindowMs;
        session.PendingNormalCasts.Enqueue(bloodrage);

        session.DrainExpiredWatchdogCasts(Environment.TickCount64, out var normalEvicted, out _);

        Assert.Empty(normalEvicted);
        Assert.Single(session.PendingNormalCasts);
    }
}

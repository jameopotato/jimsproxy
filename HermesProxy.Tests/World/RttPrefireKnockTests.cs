using System;
using System.Threading;
using System.Threading.Tasks;
using HermesProxy;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (rtt-prefire): behavior tests for the Knocker resend loop (StartKnockLoop).
// Real-time based — the loop uses Task.Delay exactly like production — so assertions
// use generous waits and only rely on properties the loop guarantees deterministically:
// knock COUNT is bounded by the loop conditions, not by elapsed time.
public class RttPrefireKnockTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static ClientCastRequest MakeCast(uint spellId) => new ClientCastRequest
    {
        SpellId = spellId,
        Timestamp = Environment.TickCount,
    };

    private static void EnqueueNonStarted(GameSessionData session, uint spellId)
    {
        lock (session.PendingCastsLock)
            session.PendingNormalCasts.Enqueue(MakeCast(spellId));
    }

    private static async Task WaitForLoopExit(GameSessionData session, uint spellId)
    {
        // Generous ceiling: 10 knocks × 20ms nominal ≈ 200ms; allow 5× for timer quantum.
        for (int i = 0; i < 100 && session.IsKnockActiveForSpell(spellId); i++)
            await Task.Delay(GameSessionData.KnockIntervalMs);
        Assert.False(session.IsKnockActiveForSpell(spellId));
    }

    [Fact]
    public async Task StartKnockLoop_UnresolvedPress_SendsExactlyKnockCount()
    {
        var session = NewSession();
        EnqueueNonStarted(session, 133);
        int sent = 0;
        session.StartKnockLoop(133, () => Interlocked.Increment(ref sent));

        await WaitForLoopExit(session, 133);

        // Entry never starts/resolves and no newer knock arms, so every knock fires.
        Assert.Equal(GameSessionData.KnockCount, Volatile.Read(ref sent));
    }

    [Fact]
    public async Task StartKnockLoop_EntryResolvedMidLoop_StopsEarly()
    {
        var session = NewSession();
        EnqueueNonStarted(session, 133);
        int sent = 0;
        session.StartKnockLoop(133, () => Interlocked.Increment(ref sent));

        // Resolve the press almost immediately — the GO/CAST_FAILED path's effect on the queue.
        Assert.True(session.TryDequeuePendingNormalCast(133, out _, preferStarted: false));
        await WaitForLoopExit(session, 133);

        // The loop re-checks the non-started set before every send; with the entry gone
        // from (at latest) the second iteration on, it cannot run to exhaustion.
        Assert.True(Volatile.Read(ref sent) < GameSessionData.KnockCount,
            $"expected early stop, sent={sent}");
    }

    [Fact]
    public async Task StartKnockLoop_MarkedStartedMidLoop_StopsEarly()
    {
        var session = NewSession();
        EnqueueNonStarted(session, 133);
        int sent = 0;
        session.StartKnockLoop(133, () => Interlocked.Increment(ref sent));

        // SMSG_SPELL_START's effect: the entry leaves the non-started set without dequeueing.
        Assert.True(session.TryMarkPendingNormalCastStarted(133, out _));
        await WaitForLoopExit(session, 133);

        Assert.True(Volatile.Read(ref sent) < GameSessionData.KnockCount,
            $"expected early stop, sent={sent}");
    }

    [Fact]
    public async Task StartKnockLoop_NewerKnockArms_SupersedesOlderLoop()
    {
        var session = NewSession();
        EnqueueNonStarted(session, 133);
        EnqueueNonStarted(session, 2136);
        int sentOld = 0, sentNew = 0;
        session.StartKnockLoop(133, () => Interlocked.Increment(ref sentOld));
        session.StartKnockLoop(2136, () => Interlocked.Increment(ref sentNew));

        await WaitForLoopExit(session, 133);
        await WaitForLoopExit(session, 2136);

        // The second arm bumped the shared generation: the older loop must abort before
        // exhaustion (normally on its very first generation check — sentOld is almost
        // always 0, but a stalled test thread between the two arms could let one knock
        // through, so assert the strict correctness property: no exhaustion), while the
        // newer loop owns the boundary and runs dry.
        Assert.True(Volatile.Read(ref sentOld) < GameSessionData.KnockCount,
            $"superseded loop must not exhaust, sentOld={sentOld}");
        Assert.Equal(GameSessionData.KnockCount, Volatile.Read(ref sentNew));
    }

    [Fact]
    public void IsKnockActiveForSpell_FalseWithoutLoop_TrueWhileArmed()
    {
        var session = NewSession();
        Assert.False(session.IsKnockActiveForSpell(133));
        EnqueueNonStarted(session, 133);
        session.StartKnockLoop(133, () => { });
        Assert.True(session.IsKnockActiveForSpell(133));
    }
}

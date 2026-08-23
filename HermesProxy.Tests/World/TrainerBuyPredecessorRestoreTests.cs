using HermesProxy.World;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (racial-downrank restore): truth table for the trainer-buy speculative predecessor
// removal (the Kronos IsInWorld-race ban defense) and every path that resolves it. The defense
// removes the predecessor rank from CurrentPlayerKnownSpells at CMSG_TRAINER_BUY_SPELL; the
// cast-block-unknown-spells guard then locally rejects casts of anything outside that set —
// which is exactly what prevents the autoban, so these orderings are ban-critical:
//
//   buy → LEARNED only                → downrankable chain (Shadowguard): server KEPT the lower
//                                       rank — RESTORE it (the 2026-08-14 fix; before it, lower
//                                       ranks read "not learned" until relog).
//   buy → SUPERCEDED → LEARNED        → genuine supersede (Stealth): removal confirmed, learn
//                                       must NOT restore. Restoring here re-arms the autoban.
//   buy → LEARNED → SUPERCEDED        → same chain, swapped arrival: transient restore is fine,
//                                       the supersede's unconditional remove must win.
//   buy → nothing (Twizzy race)       → Kronos removed server-side and told no one: the removal
//                                       must stand so the guard keeps blocking. THE ban case.
//   buy → TRAINER_BUY_FAILED          → server never removed: restore (existing behavior).
public class TrainerBuyPredecessorRestoreTests
{
    // The mock skips field initializers (GetUninitializedObject) — hydrate the two collections
    // these ops touch.
    private static GameSessionData NewState()
    {
        var state = WowGuidTestHelper.CreateMockGameSessionData();
        state.CurrentPlayerKnownSpells = new();
        state.RealSpellToLearnSpell = [];
        return state;
    }

    // Shadowguard (troll priest racial), the reported downrankable chain.
    private const uint ShadowguardR5 = 19311;
    private const uint ShadowguardR6 = 19312;
    // Stealth, the chain Kronos genuinely supersede-removes (the Twizzy autoban, 2026-05-18).
    private const uint StealthR2 = 1785;
    private const uint StealthR3 = 1786;

    private static GameSessionData StateKnowing(params uint[] spells)
    {
        var state = NewState();
        foreach (var s in spells)
            state.CurrentPlayerKnownSpells.Add(s);
        return state;
    }

    private static void AssertPendingCleared(GameSessionData state)
    {
        Assert.Equal(0u, state.PendingTrainerBuySpellId);
        Assert.Equal(0u, state.PendingTrainerBuyRemovedPredecessor);
    }

    [Fact]
    public void DownrankableChain_LearnConfirms_RestoresPredecessor()
    {
        var state = StateKnowing(ShadowguardR5);

        Assert.True(state.ApplyTrainerBuyPredecessorRemoval(ShadowguardR6, ShadowguardR5));
        // The buy→response window: guard must block the predecessor (this is the defense).
        Assert.DoesNotContain(ShadowguardR5, state.CurrentPlayerKnownSpells);

        uint restored = state.ApplyLearnedSpellKnownState(ShadowguardR6);

        Assert.Equal(ShadowguardR5, restored);
        Assert.Contains(ShadowguardR5, state.CurrentPlayerKnownSpells);
        Assert.Contains(ShadowguardR6, state.CurrentPlayerKnownSpells);
        AssertPendingCleared(state);
    }

    [Fact]
    public void SupersedeChain_SupercededThenLearn_PredecessorStaysRemoved()
    {
        var state = StateKnowing(StealthR2);
        state.ApplyTrainerBuyPredecessorRemoval(StealthR3, StealthR2);

        state.ApplySupercededSpellKnownState(StealthR3, StealthR2);
        uint restored = state.ApplyLearnedSpellKnownState(StealthR3);

        // Restoring here would re-arm the exact autoban the defense exists for.
        Assert.Equal(0u, restored);
        Assert.DoesNotContain(StealthR2, state.CurrentPlayerKnownSpells);
        Assert.Contains(StealthR3, state.CurrentPlayerKnownSpells);
        AssertPendingCleared(state);
    }

    [Fact]
    public void SupersedeChain_LearnThenSuperceded_PredecessorEndsRemoved()
    {
        var state = StateKnowing(StealthR2);
        state.ApplyTrainerBuyPredecessorRemoval(StealthR3, StealthR2);

        // Learn lands first: the restore is transient and safe (server hasn't contradicted it yet)…
        uint restored = state.ApplyLearnedSpellKnownState(StealthR3);
        Assert.Equal(StealthR2, restored);
        Assert.Contains(StealthR2, state.CurrentPlayerKnownSpells);

        // …and the supersede's unconditional remove must win the final state.
        state.ApplySupercededSpellKnownState(StealthR3, StealthR2);
        Assert.DoesNotContain(StealthR2, state.CurrentPlayerKnownSpells);
        Assert.Contains(StealthR3, state.CurrentPlayerKnownSpells);
    }

    [Fact]
    public void NoResponse_TwizzyRace_RemovalStandsAndGuardKeepsBlocking()
    {
        // The autoban case: Kronos ran RemoveSpell(prev) but IsInWorld was false, so NO packet
        // ever confirms or denies the buy. The speculative removal must simply stand.
        var state = StateKnowing(StealthR2);
        state.ApplyTrainerBuyPredecessorRemoval(StealthR3, StealthR2);

        Assert.DoesNotContain(StealthR2, state.CurrentPlayerKnownSpells);
        // Pending stays armed so a late response can still resolve it correctly.
        Assert.Equal(StealthR3, state.PendingTrainerBuySpellId);
        Assert.Equal(StealthR2, state.PendingTrainerBuyRemovedPredecessor);
    }

    [Fact]
    public void ExplicitFailed_RealSpellId_Restores()
    {
        var state = StateKnowing(ShadowguardR5);
        state.ApplyTrainerBuyPredecessorRemoval(ShadowguardR6, ShadowguardR5);

        uint restored = state.ApplyTrainerBuyFailedKnownState(ShadowguardR6);

        Assert.Equal(ShadowguardR5, restored);
        Assert.Contains(ShadowguardR5, state.CurrentPlayerKnownSpells);
        AssertPendingCleared(state);
    }

    [Fact]
    public void ExplicitFailed_LearnWrapperId_Restores()
    {
        const uint learnWrapper = 99999;
        var state = StateKnowing(ShadowguardR5);
        state.StoreRealSpell(ShadowguardR6, learnWrapper);
        state.ApplyTrainerBuyPredecessorRemoval(ShadowguardR6, ShadowguardR5);

        uint restored = state.ApplyTrainerBuyFailedKnownState(learnWrapper);

        Assert.Equal(ShadowguardR5, restored);
        Assert.Contains(ShadowguardR5, state.CurrentPlayerKnownSpells);
    }

    [Fact]
    public void ExplicitFailed_NonMatching_ClearsWithoutRestore()
    {
        // Shipped fail-safe: an unrelated FAILED confirms nothing about the pending buy — the
        // removal stands (over-blocking is recoverable via relog; a wrong restore risks a ban).
        var state = StateKnowing(ShadowguardR5);
        state.ApplyTrainerBuyPredecessorRemoval(ShadowguardR6, ShadowguardR5);

        uint restored = state.ApplyTrainerBuyFailedKnownState(12345);

        Assert.Equal(0u, restored);
        Assert.DoesNotContain(ShadowguardR5, state.CurrentPlayerKnownSpells);
        AssertPendingCleared(state);
    }

    [Fact]
    public void UnrelatedLearn_WhilePending_DoesNotRestore_LateMatchStillDoes()
    {
        var state = StateKnowing(ShadowguardR5);
        state.ApplyTrainerBuyPredecessorRemoval(ShadowguardR6, ShadowguardR5);

        // A server-granted spell (quest reward, proc-taught) mid-window must not resolve the buy.
        Assert.Equal(0u, state.ApplyLearnedSpellKnownState(777));
        Assert.DoesNotContain(ShadowguardR5, state.CurrentPlayerKnownSpells);
        Assert.Equal(ShadowguardR6, state.PendingTrainerBuySpellId);

        // The real confirmation still restores.
        Assert.Equal(ShadowguardR5, state.ApplyLearnedSpellKnownState(ShadowguardR6));
        Assert.Contains(ShadowguardR5, state.CurrentPlayerKnownSpells);
    }

    [Fact]
    public void UnrelatedSuperceded_WhilePending_KeepsPendingArmed()
    {
        var state = StateKnowing(ShadowguardR5, 887);
        state.ApplyTrainerBuyPredecessorRemoval(ShadowguardR6, ShadowguardR5);

        state.ApplySupercededSpellKnownState(888, 887);

        Assert.Equal(ShadowguardR6, state.PendingTrainerBuySpellId);
        Assert.Equal(ShadowguardR5, state.ApplyLearnedSpellKnownState(ShadowguardR6));
    }

    [Fact]
    public void PredecessorNotKnownAtBuy_LearnRestoresNothing()
    {
        // e.g. rank 1 was never learned on this character — no phantom rank may appear.
        var state = NewState();

        Assert.False(state.ApplyTrainerBuyPredecessorRemoval(ShadowguardR6, ShadowguardR5));
        uint restored = state.ApplyLearnedSpellKnownState(ShadowguardR6);

        Assert.Equal(0u, restored);
        Assert.DoesNotContain(ShadowguardR5, state.CurrentPlayerKnownSpells);
        Assert.Contains(ShadowguardR6, state.CurrentPlayerKnownSpells);
        AssertPendingCleared(state);
    }

    [Fact]
    public void RebuyOverwritesPending_SecondBuyResolves_FirstStaysRemoved()
    {
        // Single-slot pending state, documented limit: two unresolved buys back-to-back orphan
        // the first removal (fail-safe direction — blocked until relog resyncs, never a ban).
        var state = StateKnowing(ShadowguardR5, StealthR2);
        state.ApplyTrainerBuyPredecessorRemoval(ShadowguardR6, ShadowguardR5);
        state.ApplyTrainerBuyPredecessorRemoval(StealthR3, StealthR2);

        uint restored = state.ApplyLearnedSpellKnownState(StealthR3);

        Assert.Equal(StealthR2, restored);
        Assert.DoesNotContain(ShadowguardR5, state.CurrentPlayerKnownSpells);
        AssertPendingCleared(state);
    }
}

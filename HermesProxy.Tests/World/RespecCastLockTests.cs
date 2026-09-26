using System.Collections.Generic;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (respec cast lock): truth table for the speculative talent-spell lock armed at
// CMSG_CONFIRM_RESPEC_WIPE. The cast-block-unknown-spells guard only learns a spell is gone when
// SMSG_REMOVED_SPELL reaches the proxy; the server wiped it the moment it processed the confirm.
// A press in that window is the Kronos "Spell not in player book" autoban (Shadowform, 2026-09-06),
// so the orderings below are ban-critical:
//
//   confirm → press Shadowform            → LOCKED: rejected locally (THE ban case).
//   confirm → REMOVED(Shadowform) → press → released from the lock; the known-set check now
//                                           blocks it (removal took it out of the mirror).
//   confirm → burst → fence reply         → whatever the server kept is released — safe to cast.
//   confirm → silent rejection → fence    → same: nothing was removed, everything releases.
//   confirm → "no talents" / BUY_FAILED   → explicit rejection releases before the fence.
//   confirm → nothing, ever               → timeout backstop releases (fence lost).
public class RespecCastLockTests
{
    private const uint Shadowform = 15473;        // priest talent, single rank
    private const uint MindBlast = 8092;          // priest trainer spell
    private const uint PermafrostR3 = 12571;      // mage passive talent, rank 3
    private const uint MortalStrikeR1 = 12294;    // warrior talent
    private const uint MortalStrikeR3 = 21553;    // trainer-bought rank of that talent
    private const uint HolyShockR2 = 20929;       // paladin trainer rank of talent 20473
    private const byte Warrior = 1, Paladin = 2, Priest = 5, Mage = 8;

    static RespecCastLockTests()
    {
        GameData.LoadTalentSpellRanks();
        GameData.LoadSpellRankChain();
    }

    // The mock skips field initializers (GetUninitializedObject) — hydrate the mirror the lock reads.
    private static GameSessionData NewState(byte playerClass, params uint[] known)
    {
        var state = WowGuidTestHelper.CreateMockGameSessionData();
        state.CurrentPlayerKnownSpells = new HashSet<uint>(known);
        state.CurrentPlayerClass = playerClass;
        return state;
    }

    private static HashSet<uint> Collect(byte playerClass, params uint[] known)
    {
        var into = new HashSet<uint>();
        GameSessionData.CollectRespecLockSpells(known, playerClass, into);
        return into;
    }

    // === Which spells get locked ===

    [Fact]
    public void Collect_LocksOwnTalent_NotTrainerSpells()
    {
        var locked = Collect(Priest, Shadowform, MindBlast);
        Assert.Equal(new HashSet<uint> { Shadowform }, locked);
    }

    [Fact]
    public void Collect_LocksTrainerRankRootedInTalent_WhenTalentRankItselfIsGone()
    {
        // Learning R2/R3 superseded R1 out of the known set; the server still unlearns R3 on respec.
        var locked = Collect(Warrior, MortalStrikeR3);
        Assert.Equal(new HashSet<uint> { MortalStrikeR3 }, locked);
        Assert.Equal(new HashSet<uint> { HolyShockR2 }, Collect(Paladin, HolyShockR2));
    }

    [Fact]
    public void Collect_LocksPassiveTalentRanks()
    {
        Assert.Equal(new HashSet<uint> { PermafrostR3 }, Collect(Mage, PermafrostR3));
    }

    [Fact]
    public void Collect_SkipsOtherClassTalents_UnlessClassUnknown()
    {
        // A spell that is a talent for another class must not be locked for this one.
        Assert.Empty(Collect(Priest, MortalStrikeR1, MortalStrikeR3));
        // Class not yet observed (0): lock every talent-rooted spell — over-blocking beats a ban.
        Assert.Equal(new HashSet<uint> { MortalStrikeR1, MortalStrikeR3 }, Collect(0, MortalStrikeR1, MortalStrikeR3));
    }

    [Fact]
    public void Collect_EmptyKnownSet_LocksNothing()
    {
        Assert.Empty(Collect(Priest));
    }

    // === Lock lifecycle ===

    [Fact]
    public void Arm_ThenPressBeforeRemoval_IsLocked()
    {
        var state = NewState(Priest, Shadowform, MindBlast);
        Assert.Equal(1, state.ArmRespecCastLock(1000));
        Assert.True(state.IsRespecCastLockArmed);
        Assert.True(state.IsRespecCastLocked(Shadowform, 1500, out int expired));
        Assert.Equal(0, expired);
        Assert.False(state.IsRespecCastLocked(MindBlast, 1500, out _));
    }

    [Fact]
    public void Removal_ReleasesThatSpell_AndDrainsWhenLast()
    {
        var state = NewState(Warrior, MortalStrikeR3, MortalStrikeR1);
        Assert.Equal(2, state.ArmRespecCastLock(0));
        Assert.True(state.ReleaseRespecLockedSpell(MortalStrikeR3, out int remaining));
        Assert.Equal(1, remaining);
        Assert.False(state.IsRespecCastLocked(MortalStrikeR3, 10, out _));
        Assert.True(state.IsRespecCastLocked(MortalStrikeR1, 10, out _));
        Assert.True(state.ReleaseRespecLockedSpell(MortalStrikeR1, out remaining));
        Assert.Equal(0, remaining);
        Assert.False(state.IsRespecCastLockArmed);
    }

    [Fact]
    public void Removal_OfUnlockedSpell_IsIgnored()
    {
        var state = NewState(Priest, Shadowform);
        state.ArmRespecCastLock(0);
        Assert.False(state.ReleaseRespecLockedSpell(MindBlast, out int remaining));
        Assert.Equal(1, remaining);
        Assert.True(state.IsRespecCastLockArmed);
    }

    [Fact]
    public void Fence_ReleasesWhateverTheServerKept()
    {
        var state = NewState(Warrior, MortalStrikeR3, MortalStrikeR1);
        state.ArmRespecCastLock(0);
        state.ReleaseRespecLockedSpell(MortalStrikeR3, out _);
        Assert.Equal(1, state.ClearRespecCastLock());
        Assert.False(state.IsRespecCastLockArmed);
        Assert.False(state.IsRespecCastLocked(MortalStrikeR1, 10, out _));
    }

    [Fact]
    public void Timeout_Backstop_ReleasesAllAndReportsCount()
    {
        var state = NewState(Priest, Shadowform);
        state.ArmRespecCastLock(1000);
        Assert.True(state.IsRespecCastLocked(Shadowform, 1000 + GameSessionData.RespecLockTimeoutMs, out int expired));
        Assert.Equal(0, expired);
        Assert.False(state.IsRespecCastLocked(Shadowform, 1001 + GameSessionData.RespecLockTimeoutMs, out expired));
        Assert.Equal(1, expired);
        Assert.False(state.IsRespecCastLockArmed);
    }

    [Fact]
    public void Rearm_UnionsAndRestartsTimeout()
    {
        var state = NewState(Priest, Shadowform);
        state.ArmRespecCastLock(0);
        state.ReleaseRespecLockedSpell(Shadowform, out _);
        // Second confirm while still known (server kept it, or the mirror is stale): locked again.
        Assert.Equal(1, state.ArmRespecCastLock(50_000));
        Assert.True(state.IsRespecCastLocked(Shadowform, 50_000 + GameSessionData.RespecLockTimeoutMs, out _));
    }

    [Fact]
    public void NeverArmed_IsInert()
    {
        var state = NewState(Priest, Shadowform);
        Assert.False(state.IsRespecCastLockArmed);
        Assert.False(state.IsRespecCastLocked(Shadowform, 0, out int expired));
        Assert.Equal(0, expired);
        Assert.False(state.ReleaseRespecLockedSpell(Shadowform, out int remaining));
        Assert.Equal(0, remaining);
        Assert.Equal(0, state.ClearRespecCastLock());
    }

    [Fact]
    public void Arm_DoesNotTouchTheKnownSetMirror()
    {
        // The lock is a veto layered on the guard; the mirror stays the server's word alone.
        var state = NewState(Priest, Shadowform, MindBlast);
        state.ArmRespecCastLock(0);
        Assert.Contains(Shadowform, state.CurrentPlayerKnownSpells);
        state.ClearRespecCastLock();
        Assert.Contains(Shadowform, state.CurrentPlayerKnownSpells);
    }
    // === Fence ordinal ===

    [Fact]
    public void Fence_StaleClientReplyDoesNotStandDown_OwnReplyDoes()
    {
        var state = NewState(Priest, Shadowform);
        state.NoteMailTimeQuerySent(isRespecFence: false);   // client's own query, still in flight
        state.ArmRespecCastLock(0);
        state.NoteMailTimeQuerySent(isRespecFence: true);    // the fence, queued behind the confirm
        Assert.False(state.NoteMailTimeReplyReachesRespecFence());  // reply to the client's query
        Assert.True(state.IsRespecCastLocked(Shadowform, 10, out _));
        Assert.True(state.NoteMailTimeReplyReachesRespecFence());   // the fence's own reply
    }

    [Fact]
    public void Fence_ReplyWhileNotArmed_IsFalse()
    {
        var state = NewState(Priest, Shadowform);
        state.NoteMailTimeQuerySent(isRespecFence: false);
        Assert.False(state.NoteMailTimeReplyReachesRespecFence());
        // Armed but no fence sent (nothing locked → no fence): a reply never matches.
        state.ArmRespecCastLock(0);
        state.NoteMailTimeQuerySent(isRespecFence: false);
        Assert.False(state.NoteMailTimeReplyReachesRespecFence());
    }

    [Fact]
    public void Fence_RearmMovesTheFenceForward()
    {
        var state = NewState(Priest, Shadowform);
        state.ArmRespecCastLock(0);
        state.NoteMailTimeQuerySent(isRespecFence: true);
        state.ArmRespecCastLock(5);
        state.NoteMailTimeQuerySent(isRespecFence: true);
        Assert.False(state.NoteMailTimeReplyReachesRespecFence());  // first fence: second wipe still pending
        Assert.True(state.NoteMailTimeReplyReachesRespecFence());
    }

    [Fact]
    public void Fence_ClearForgetsTheFence_LateReplyCannotStandDownANewLock()
    {
        var state = NewState(Priest, Shadowform);
        state.ArmRespecCastLock(0);
        state.NoteMailTimeQuerySent(isRespecFence: true);
        state.ClearRespecCastLock();                          // explicit rejection cleared it
        state.ArmRespecCastLock(5);                           // re-armed, new fence not yet sent
        Assert.False(state.NoteMailTimeReplyReachesRespecFence());  // the old fence's reply
        Assert.True(state.IsRespecCastLocked(Shadowform, 10, out _));
    }
}

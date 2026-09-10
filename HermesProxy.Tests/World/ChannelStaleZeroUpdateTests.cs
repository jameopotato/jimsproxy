using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using Xunit;
using Disposition = HermesProxy.GameSessionData.LocalChannelZeroUpdateDisposition;

namespace HermesProxy.Tests.World;

// JimsProxy (fishing recast wedge 2026-09-01): state tests for the held channel
// zero-update guard in GameSessionData. A fishing bobber outlives its channel by ~2s,
// and a mangos-family bobber timeout finishes whatever channel the player has at that
// moment — so a recast inside that window has its NEW channel ended by the server
// ~100ms after it opened while the new bobber lives on. The guard keeps the client's
// channel open so the bobber can be waited out: it arms only when the previous bobber
// is still in the object cache at the new CHANNEL_START, holds the zero-update, and
// drops it only when that bobber's destroy (or SMSG_FISH_NOT_HOOKED) lands in the same
// read pass; a socket drain releases anything still held as genuine.
public class ChannelStaleZeroUpdateTests
{
    static ChannelStaleZeroUpdateTests()
    {
        // ServerPacket construction resolves opcodes through ModernVersion, whose
        // static ctor needs a real build — same guard every packet-constructing
        // test class uses so the class also runs green in isolation.
        if (global::Framework.Settings.ClientBuild == HermesProxy.Enums.ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = HermesProxy.Enums.ClientVersionBuild.V1_14_2_42597;
    }

    private const uint FishingArtisan = 18248;
    private const uint MindFlay = 15407;
    private const uint ChannelDurationMs = 30000;
    private static readonly WowGuid128 OldBobber = new(0x1F00_0000_0000_0001, 0x0000_0000_0000_0001);
    private static readonly WowGuid128 NewBobber = new(0x1F00_0000_0000_0002, 0x0000_0000_0000_0002);
    private static readonly WowGuid128 SomeMob = new(0xF130_0000_0000_0003, 0x0000_0000_0000_0003);

    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static SpellChannelUpdate ZeroUpdate() => new() { TimeRemaining = 0 };

    /// <summary>The server created a bobber for us (UPDATE_OBJECT create block).</summary>
    private static void BobberCreated(GameSessionData session, WowGuid128 guid)
    {
        session.ObjectCacheLegacy[guid] = [];
        session.LocalFishingBobberGuid = guid;
    }

    /// <summary>SMSG_DESTROY_OBJECT for a guid, as HandleDestroyObject drives the guard.</summary>
    private static bool Destroyed(GameSessionData session, WowGuid128 guid)
    {
        session.ObjectCacheLegacy.Remove(guid);
        return session.OnFishingBobberTeardownAnchor(guid);
    }

    /// <summary>The captured wedge's setup: first cast's bobber still alive when the
    /// recast's CHANNEL_START arrives (the new bobber's create trails it).</summary>
    private static GameSessionData RecastWithOldBobberAlive(uint spellId = FishingArtisan)
    {
        var session = NewSession();
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        BobberCreated(session, OldBobber);
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate())); // natural end
        session.OnLocalChannelStart(spellId, ChannelDurationMs);
        BobberCreated(session, NewBobber);
        return session;
    }

    [Fact]
    public void Wedge_ZeroUpdateHeld_DroppedByOldBobberDestroy_ChannelStaysOpen()
    {
        var session = RecastWithOldBobberAlive();

        // Captured batch order: UPDATE_OBJECT(old) → CHANNEL_UPDATE(0) → FISH_NOT_HOOKED → DESTROY(old).
        Assert.Equal(Disposition.Held, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.True(Destroyed(session, OldBobber));
        Assert.Null(session.HeldLocalChannelZeroUpdate);
        // The channel we protected stays open (also keeps the #244 emote guard honest).
        Assert.Equal(FishingArtisan, session.LocalChannelSpellId);
        Assert.Null(session.TakeHeldLocalChannelZeroUpdateAtDrain());
        // Disarmed: the next zero-update (this channel's real end) is genuine.
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.Equal(0u, session.LocalChannelSpellId);
    }

    [Fact]
    public void Wedge_FishNotHookedAlsoAnchorsTheDrop()
    {
        var session = RecastWithOldBobberAlive();
        Assert.Equal(Disposition.Held, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.True(session.OnFishingBobberTeardownAnchor());
        Assert.False(Destroyed(session, OldBobber)); // nothing left to drop
        Assert.Equal(FishingArtisan, session.LocalChannelSpellId);
    }

    [Fact]
    public void OldBobberDestroyBeforeZeroUpdate_SamePass_Drops()
    {
        var session = RecastWithOldBobberAlive();
        Assert.False(Destroyed(session, OldBobber)); // nothing held yet: remembered for this pass
        Assert.Equal(Disposition.Dropped, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.Equal(FishingArtisan, session.LocalChannelSpellId);
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate())); // one-shot
    }

    [Fact]
    public void OldBobberDestroyThenDrain_Disarms()
    {
        var session = RecastWithOldBobberAlive();
        Assert.False(Destroyed(session, OldBobber));
        Assert.Null(session.TakeHeldLocalChannelZeroUpdateAtDrain()); // pass ended, nothing owed any more
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.Equal(0u, session.LocalChannelSpellId);
    }

    [Fact]
    public void HeldWithoutAnchor_ReleasedAtDrain_AsGenuineEnd()
    {
        var session = RecastWithOldBobberAlive();
        var update = ZeroUpdate();
        Assert.Equal(Disposition.Held, session.ClassifyLocalChannelZeroUpdate(update));
        Assert.False(Destroyed(session, SomeMob)); // some other object's destroy is not an anchor
        Assert.Same(update, session.TakeHeldLocalChannelZeroUpdateAtDrain());
        Assert.Equal(0u, session.LocalChannelSpellId);
        Assert.Equal(default, session.StaleZeroUpdateBobberGuid);
    }

    [Fact]
    public void OldBobberAlreadyDestroyed_DoesNotArm()
    {
        var session = NewSession();
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        BobberCreated(session, OldBobber);
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.False(Destroyed(session, OldBobber)); // teardown finished before the recast

        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        Assert.Equal(default, session.StaleZeroUpdateBobberGuid);
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.Equal(0u, session.LocalChannelSpellId);
    }

    [Fact]
    public void FirstCastOfSession_GenuineEarlyInterrupt_Forwards()
    {
        var session = NewSession();
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        BobberCreated(session, OldBobber);

        // No previous bobber ⇒ nothing owed ⇒ a zero-update 100ms in (mob damage) is genuine.
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.Equal(0u, session.LocalChannelSpellId);
    }

    [Fact]
    public void ReplacedWhileStillOpen_ArmsGuard()
    {
        // Recast mid-channel with no zero-update seen in between: the old bobber is alive,
        // its teardown will finish the new channel, and that zero-update is droppable.
        var session = NewSession();
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        BobberCreated(session, OldBobber);
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        Assert.Equal(OldBobber, session.StaleZeroUpdateBobberGuid);
        Assert.Equal(Disposition.Held, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.True(Destroyed(session, OldBobber));
    }

    [Fact]
    public void BreakActionAfterStart_ForwardsImmediately()
    {
        var session = RecastWithOldBobberAlive();

        // Player moved / cast / clicked a GO right after recasting — the zero-update that
        // follows is the genuine result of that action and must reach the client now.
        session.RecordLocalChannelBreakAction();
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.Equal(0u, session.LocalChannelSpellId);
        Assert.Equal(default, session.StaleZeroUpdateBobberGuid);
    }

    [Fact]
    public void BreakActionFromPreviousCast_ClearedByStart_StillHolds()
    {
        var session = NewSession();
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        BobberCreated(session, OldBobber);
        session.RecordLocalChannelBreakAction(); // e.g. clicked the old bobber early
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));

        // The new CHANNEL_START resets the break flag — an earlier cast's action must
        // not disarm the guard for the channel that follows it.
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        Assert.Equal(Disposition.Held, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
    }

    [Fact]
    public void NonFishingChannel_NeverArms()
    {
        // A genuine early interrupt of a combat channel (damage pushback at <1.5s into
        // Mind Flay) must reach the client, or the cast bar wedges the other way.
        var session = RecastWithOldBobberAlive(MindFlay);
        Assert.Equal(default, session.StaleZeroUpdateBobberGuid);
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
    }

    [Fact]
    public void NewStart_SupersedesAnythingHeld()
    {
        var session = RecastWithOldBobberAlive();
        Assert.Equal(Disposition.Held, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        Assert.Null(session.HeldLocalChannelZeroUpdate);
        Assert.Null(session.TakeHeldLocalChannelZeroUpdateAtDrain());
    }

    [Fact]
    public void DestroyOfNewestBobber_ForgetsIt()
    {
        var session = NewSession();
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        BobberCreated(session, OldBobber);
        Assert.False(Destroyed(session, OldBobber));
        Assert.Equal(default, session.LocalFishingBobberGuid);
    }

    [Fact]
    public void AfterDrop_ClientChannelIsOrphaned_EndedByNewBobberDestroy()
    {
        var session = RecastWithOldBobberAlive();
        Assert.Equal(Disposition.Held, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.True(Destroyed(session, OldBobber));

        // The server has no channel any more; the client's is ours to end, and only the
        // new bobber's destroy (catch looted, fish escaped, timed out) ends it.
        Assert.Equal(NewBobber, session.OrphanedClientChannelBobberGuid);
        Assert.False(session.TakeOrphanedClientChannelEnd(SomeMob));
        Assert.Equal(FishingArtisan, session.LocalChannelSpellId);
        Assert.True(session.TakeOrphanedClientChannelEnd(NewBobber));
        Assert.Equal(0u, session.LocalChannelSpellId);
        Assert.False(session.TakeOrphanedClientChannelEnd(NewBobber)); // one-shot
    }

    [Fact]
    public void AfterDropViaEarlyAnchor_ClientChannelIsOrphaned()
    {
        var session = RecastWithOldBobberAlive();
        Assert.False(Destroyed(session, OldBobber));
        Assert.Equal(Disposition.Dropped, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.Equal(NewBobber, session.OrphanedClientChannelBobberGuid);
    }

    [Fact]
    public void OrphanedChannel_ClearedByNewStartOrGenuineEnd()
    {
        var session = RecastWithOldBobberAlive();
        Assert.Equal(Disposition.Held, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.True(Destroyed(session, OldBobber));

        // A recast while orphaned: the new START owns the client channel again.
        session.OnLocalChannelStart(FishingArtisan, ChannelDurationMs);
        Assert.Equal(default, session.OrphanedClientChannelBobberGuid);
        Assert.Equal(NewBobber, session.StaleZeroUpdateBobberGuid); // and the orphan bobber, still alive, is the next teardown owed
    }

    [Fact]
    public void ReleasedAtDrain_NothingOrphaned()
    {
        var session = RecastWithOldBobberAlive();
        Assert.Equal(Disposition.Held, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
        Assert.NotNull(session.TakeHeldLocalChannelZeroUpdateAtDrain());
        Assert.Equal(default, session.OrphanedClientChannelBobberGuid);
        Assert.False(session.TakeOrphanedClientChannelEnd(NewBobber));
    }

    [Fact]
    public void ZeroDurationStart_ClosesChannelWindow()
    {
        // Pre-existing #244 behavior: a CHANNEL_START with no duration means no channel.
        var session = NewSession();
        session.OnLocalChannelStart(FishingArtisan, 0);
        Assert.Equal(0u, session.LocalChannelSpellId);
        Assert.Equal(Disposition.Forward, session.ClassifyLocalChannelZeroUpdate(ZeroUpdate()));
    }

    [Theory]
    [InlineData(7620u, true)]   // Fishing (Apprentice)
    [InlineData(7731u, true)]   // Fishing (Journeyman)
    [InlineData(7732u, true)]   // Fishing (Expert)
    [InlineData(18248u, true)]  // Fishing (Artisan)
    [InlineData(33095u, true)]  // Fishing (Master) — TBC 2.4.3 backends are accepted
    [InlineData(15407u, false)] // Mind Flay
    [InlineData(0u, false)]     // not channeling
    public void IsFishingChannelSpell_CoversAllRanks(uint spellId, bool expected)
    {
        Assert.Equal(expected, GameData.IsFishingChannelSpell(spellId));
    }
}

using HermesProxy.Auth;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Threading;
using Framework.Logging;
using Framework.Realm;
using HermesProxy.World.Server.Packets;
using ArenaTeamInspectData = HermesProxy.World.Server.Packets.ArenaTeamInspectData;
using System;

namespace HermesProxy;

public sealed record PendingPetScale(
    WowGuid128 Guid,
    uint Entry,
    uint DisplayId,
    float RawScale,
    float Cms,
    bool IsWarlockPet);

public class PlayerCache
{
    public string? Name;
    public Race RaceId = Race.None;
    public Class ClassId = Class.None;
    public Gender SexId = Gender.None;
    public byte Level = 0;
}

public sealed class OwnCharacterInfo : PlayerCache
{
    public WowGuid128 AccountId;
    public WowGuid128 CharacterGuid;
    public Realm Realm = null!;
    public ulong LastLoginUnixSec;
}

public sealed class TradeSession
{
    public static uint GlobalTradeIdCounter; // Fallback for pre 2.0.0 servers
    public uint TradeId;

    public WowGuid128 Partner;
    public WowGuid128 PartnerAccount;

    public uint ClientStateIndex = 1; // incremented for every update on our side
    public uint ServerStateIndex = 1; // incremented by any trade action
}

// JimsProxy (zep-stuck-low-latency-race 2026-05-17): which deferred-synth path
// armed the PendingDeferredTransportSynth flag. NewWorld defers + conditionally
// skips when destination has player on transport (natural NEW_WORLD load already
// unlocks the client). Login always fires the synth because there's no loading
// screen at login to unlock the modern client's turn-input gate.
public enum DeferredTransportSynthMode
{
    None = 0,
    NewWorld = 1,
    Login = 2,
}

public sealed class GameSessionData
{
    public bool HasWsgHordeFlagCarrier;
    public bool HasWsgAllyFlagCarrier;

    // JimsProxy (pvp-log-data-throttle 2026-05-17): tick of the most recent
    // CMSG_PVP_LOG_DATA we forwarded to the legacy server. Used by
    // BattlegroundHandler.HandlePvPLogData to drop requests inside the throttle
    // window (10s). Kronos / vanilla-emu servers treat sustained
    // CMSG_PVP_LOG_DATA above ~10/min as a spam-bot signal and silently queue
    // a kick — addons like BattlegroundEnemies (2s ticker) and enemyFrames
    // (every-frame OnUpdate) can blow past that threshold per-addon. The
    // proxy throttle is the universal defense: covers any current or future
    // misbehaving PvP-scoreboard addon with zero per-addon work. 0 means we
    // haven't forwarded one yet this session — first request always passes.
    public long LastForwardedPvpLogDataTickMs;
    public bool JimsPlusSideband;
    public bool ChannelDisplayList;
    public bool ShowPlayedTime;
    public bool IsInFarSight;
    // JimsProxy (taxi-flight-robustness): IsInTaxiFlight is read on the dismount Task's
    // ThreadPool thread and written on the packet-handler thread. Always access via
    // Volatile.Read/Write so weak-memory-model reorderings can't deliver stale state to
    // the dismount logic (e.g. seeing true after handler set it false).
    public bool IsInTaxiFlight;
    public bool IsWaitingForTaxiStart;
    // JimsProxy (rp-walk-control-transition): tracks whether the most recent
    // SMSG_CONTROL_UPDATE for the player carried HasControl=true. Used by the
    // RP-walk speed-reset fix to gate firing on a real false→true transition
    // (CC release) and skip the no-op true→true case (login, /reload). Without
    // this gate the login flow's HasControl=true blanket-fires the speed reset
    // and clobbers any active speed buff (mount, sprint, aspect of the cheetah)
    // by hardcoding 7.0f. Default true: a player who has never been CC'd is
    // always in control, so the natural login state is "has control already."
    public bool LastObservedHasControl = true;
    // JimsProxy (speed-stuck-after-fear-while-mounted): cached for reassert; see memory.
    public float LastKnownPlayerRunSpeed = 7.0f;
    // JimsProxy (speed-stuck-after-bg-end-while-mounted): deferred reassert flag; see memory.
    public bool PendingPostTeleportRunSpeedReassert;
    // JimsProxy (taxi-flight-robustness): when set, signals a pending taxi-dismount Task
    // scheduled to fire at TaxiDismountFiresAtTickMs. The CTS is cancelled+disposed on
    // (a) clean session disconnect, (b) early landing CMSG, (c) a fresh taxi spline
    // arriving before the prior dismount fired (multi-segment chained flights). Without
    // cancellation the Task captured a now-dead session and either NRE'd inside
    // SendPacketToClient or fired control-grant packets at a session that had moved on.
    public CancellationTokenSource? TaxiDismountCts;
    public long TaxiDismountFiresAtTickMs;
    // JimsProxy (dance-stuck-on-movement 2026-05-07): the modern Classic 1.14 client treats
    // certain ONESHOT emote IDs (notably EMOTE_ONESHOT_DANCE = 10) as looping animations
    // client-side and only stops them on a new SMSG_EMOTE arriving. Vanilla 1.12 servers
    // (Kronos / Twinstar) don't broadcast a stop emote when the player moves, so the dance
    // loops indefinitely. Track the last looping emote so HandlePlayerMove can synthesize
    // a stop SMSG_EMOTE on the first movement-start packet.
    public uint LastLoopingEmoteId; // 0 means no active loop
    public long LastLoopingEmoteTickMs;
    public string? TaxiAttemptId;
    public bool IsWaitingForNewWorld;
    public bool IsWaitingForWorldPortAck;
    // JimsProxy (zep-stuck-no-move 2026-05-14): set to a sentinel MoveCounter when
    // HandleNewWorld emits a synthesized SMSG_MOVE_TELEPORT to clear the modern
    // client's stale MOVEMENTFLAG_ONTRANSPORT after a cross-continent transport
    // (zep/boat) zone change. The legacy server never sent that teleport, so the
    // matching CMSG_MOVE_TELEPORT_ACK must be dropped before reaching the legacy
    // server (per project_kronos_three_delayed_kick_sources, a spurious teleport-ack
    // can feed malformed-packet kick counters on Kronos). 0 means none pending.
    public uint PendingSyntheticTransportClearAckCounter;

    // JimsProxy (zep-relog-diag 2026-05-15): tracks the player's last-observed
    // TransportGuid across UpdateObject reads so the diagnostic in
    // UpdateHandler.ReadMovementUpdateBlock fires only on state transitions.
    public WowGuid128? DiagLastObservedPlayerTransportGuid;

    // JimsProxy (zep-stuck-low-latency-race 2026-05-17): deferred-synth mode.
    // HandleNewWorld / HandleLoginVerifyWorld set this and defer the transport-
    // clear MoveTeleport synth until the player's first post-NEW_WORLD (or
    // post-login) UpdateObject is processed in UpdateHandler. Two reasons to
    // defer:
    //   1. NewWorld path — at low latency (~35ms) the inline synth fired
    //      BEFORE the destination map's COMPRESSED_UPDATE_OBJECT arrived; the
    //      server's subsequent natural player-update re-attached
    //      MOVEMENTFLAG_ONTRANSPORT (player landed on destination zep tower /
    //      boat deck), and the rapid clear→re-attach wedged the modern client.
    //      Deferred path skips the synth entirely when the destination's
    //      player UpdateObject confirms the player is on a destination
    //      transport (no synth needed — the natural NEW_WORLD flow already
    //      unlocks the client's movement state on map load).
    //   2. Login path — when the client logs in already on a transport, the
    //      modern client gates turn-input (CMSG_MOVE_SET_FACING / START_TURN_*)
    //      and never sends them until something resets its movement state.
    //      There's no loading screen at login, so the natural NEW_WORLD reset
    //      doesn't fire. Synthesizing the transport-clear MoveTeleport gives
    //      the gate the kick it needs to release. Login path ALWAYS synths
    //      regardless of transport state.
    // Cleared by the deferred path in UpdateHandler.ReadMovementUpdateBlock
    // once it fires or skips.
    public DeferredTransportSynthMode PendingDeferredTransportSynth;

    public bool IsFirstEnterWorld;
    public bool IsConnectedToInstance;
    public Queue<ServerPacket> PendingUninstancedPackets = new(); // Here packets are queued while IsConnectedToInstance = false;
    public readonly Lock PendingUninstancedPacketsLock = new();
    public bool IsInWorld;
    public uint? CurrentMapId;
    public uint CurrentZoneId;
    public uint CurrentTaxiNode;
    public List<byte> UsableTaxiNodes = [];
    public uint PendingTransferMapId;
    public uint LastEnteredAreaTrigger;
    public uint LastDispellSpellId;
    public Dictionary<WowGuid128, uint[]> CachedPlayerEnchants = new();
    // JimsProxy: tracks the active player's equipped item entry IDs by slot 0..18
    // (head, neck, shoulders, body, chest, waist, legs, feet, wrists, hands, finger1,
    // finger2, trinket1, trinket2, back, mainhand, offhand, ranged, tabard). Updated
    // incrementally as VisibleItems entries arrive from the legacy server. Read by the
    // synthesized-spell-stats path to walk equip-effect triggered spells and surface
    // vanilla item bonuses (especially +healing) on the modern character sheet, since
    // vanilla 1.12 has no PLAYER_FIELD_MOD_HEALING_DONE_POS field at the protocol level.
    public int[] CurrentEquippedItemIds = new int[19];
    // JimsProxy: tracks the active player's currently-applied aura spell ids by slot. Includes
    // raid/party buffs (Greater Blessing of Wisdom, Mark of the Wild, etc.) and class set
    // bonuses (e.g. T2 Priest 8-piece +healing aura). Walked alongside equipped items by the
    // synthesized-spell-stats path so set-bonus and consumable +healing/+damage flows surface
    // on the modern client's character sheet, not just per-piece equip effects. Slot index
    // matches the legacy UNIT_FIELD_AURA layout. Vanilla 1.12 has 56 slots (16 visible + 40
    // passive); we size at 256 to cover later expansions safely.
    public uint[] CurrentPlayerAuraSpellIds = new uint[256];
    // JimsProxy: minimum context the synthesized-spell-crit path needs about the active
    // player. Class+level pick the per-class crit constants (vanilla cmangos formula
    // chance = base + INT / (rate0 + rate1*level)), Intellect drives the linear term.
    // All four are read from the player's UNIT_FIELD_BYTES_0 / UNIT_FIELD_LEVEL /
    // UNIT_FIELD_STAT3 in UpdateHandler. Default 0/0/0 is treated as "not yet known"
    // and skips the synthesis.
    public byte CurrentPlayerClass;
    public byte CurrentPlayerLevel;
    public int CurrentPlayerIntellect;
    // JimsProxy: spells in the active player's spellbook (SMSG_SEND_KNOWN_SPELLS).
    // Used by the synthesized-spell-crit path to pick up talent passives that get
    // CastSpell()'d on self by the legacy server but don't appear in the visible
    // aura array (vanilla server buries some passives below the visible-aura cutoff).
    // Walked alongside active auras when summing crit aura contributions.
    public System.Collections.Generic.HashSet<uint> CurrentPlayerKnownSpells = new();
    //MIRASU - Talent rank passive spell ids the proxy has synthesized into the modern
    //MIRASU   client's known-spells set on top of CurrentPlayerKnownSpells. Vanilla's
    //MIRASU   Player::LearnTalent path only keeps the highest active rank in the spell
    //MIRASU   list (lower ranks get RemoveSpell'd), so IsPlayerSpell(rank1Id) returns
    //MIRASU   false on the 1.14 client for every multi-rank talent the player has spent
    //MIRASU   points in — breaking talent-keyed lookups in LibClassicDurations and
    //MIRASU   similar addons. SpellHandler injects predecessor ranks from GameData's
    //MIRASU   TalentRankPredecessors table and tracks the synthesized set here so the
    //MIRASU   reconcile step can withdraw them on respec.
    public System.Collections.Generic.HashSet<uint> SynthesizedTalentRanks = new();
    // JimsProxy (Kronos IsInWorld race defense): tracks the last CMSG_TRAINER_BUY_SPELL
    // we forwarded so we can restore the speculatively-removed predecessor if the buy
    // fails. Kronos's RemoveSpell(prev) runs unconditionally on trainer-buy, but the
    // SMSG_SUPERCEDED_SPELL send is gated on m_session->IsInWorld() — when that gate
    // is briefly false, the predecessor disappears server-side without notification,
    // and the next CMSG_CAST_SPELL for it triggers an anticheat autoban
    // (see project_kronos_trainer_buy_predecessor_removal_bug). We mirror the removal
    // proxy-side on send so the existing cast-block-unknown-spells guard at
    // World/Server/PacketHandlers/SpellHandler.cs:159 catches the post-buy cast attempt
    // and converts it into a SMSG_CAST_FAILED (you don't have this spell) instead of
    // a real ban. On explicit SMSG_TRAINER_BUY_FAILED we restore the predecessor.
    public uint PendingTrainerBuySpellId;
    public uint PendingTrainerBuyRemovedPredecessor;
    // JimsProxy: real spell ids that the most recent SMSG_TRAINER_LIST marked
    // with TrainerSpellState=Known. Authoritative server view of "this spell
    // is effectively learned by you" — covers both direct ownership (HasSpell)
    // and supersede-chain ownership (HasSpell on any higher rank). Without this
    // we can't intercept stale-click buys for spells that were just removed
    // from CurrentPlayerKnownSpells by a SMSG_SUPERCEDED_SPELLS for the new
    // rank: e.g., learn Apprentice Riding, then Journeyman, then click stale
    // Apprentice in the UI — Apprentice is gone from KnownSpells but the
    // server still treats it as effectively-known and rejects the buy with
    // FAILED. Repopulated on every new SMSG_TRAINER_LIST so this set always
    // reflects the latest server view.
    public System.Collections.Generic.HashSet<uint> LastTrainerListKnownSpells = new();
    // JimsProxy: in-flight trainer-buy tracking. Set on every forwarded
    // CMSG_TRAINER_BUY_SPELL, cleared on the response (LEARNED, SUPERCEDED, FAILED,
    // or a fresh trainer-list refresh). Used to drop rapid same-spell double-clicks
    // where the second CMSG races with the first's response — both reach the
    // server, the first succeeds, the second gets FAILED. Distinct from
    // PendingTrainerBuy* (which only tracks buys that triggered speculative
    // predecessor removal for the ban defense).
    public uint InFlightTrainerBuySpellId;
    public long InFlightTrainerBuyTickMs;
    // JimsProxy: per-unit HP cache used to compute overhealing on legacy servers
    // that don't include OverHeal in SMSG_SPELL_HEAL_LOG (1.12 vanilla). Authoritative
    // source is UNIT_FIELD_HEALTH / UNIT_FIELD_MAXHEALTH from SMSG_UPDATE_OBJECT;
    // we also bump current HP forward on heal events to stay accurate between pushes.
    public ConcurrentDictionary<WowGuid128, (int Hp, int MaxHp)> UnitHealthCache = new();
    // JimsProxy: per-unit resting (un-hasted) ranged attack time. Vanilla bakes ranged
    // haste straight into UNIT_FIELD_RANGEDATTACKTIME, but the modern client treats its
    // RangedAttackRoundBaseTime as the *base* and time-scales the bow draw/release
    // animation by ModRangedHaste. We track the slowest RANGEDATTACKTIME seen per unit
    // as the base so we can synthesize ModRangedHaste = resting / current. Written from
    // the WorldClient handler thread; ConcurrentDictionary keeps it torn-state safe.
    public ConcurrentDictionary<WowGuid128, uint> RestingRangedAttackTime = new();
    // JimsProxy (#320): defer mid-swing UNIT_FIELD_BASEATTACKTIME field changes for the
    // local player until the next SMSG_ATTACKER_STATE_UPDATE. Vanilla server's
    // m_attackTimer is frozen at swing-start cadence (vmangos Unit.cpp ResetAttackTimer
    // fires only on swing-out, NOT when a ModMeleeHaste aura applies/removes mid-swing),
    // so the in-flight swing finishes at the OLD speed while the BASEATTACKTIME field
    // jumps to the new speed immediately. WeaponSwingTimer / Quartz / ClassicSwingTimer
    // all rescale the remaining swing-bar by (new/old) on UnitAttackSpeed change, landing
    // the bar at 0 BEFORE the actual swing fires — the "bar ends early then snaps" /
    // "0 hang" symptom. Slot 0=MH, 1=OH; ranged has its own RestingRangedAttackTime path
    // (PR #287) that operates on a different mechanism (animation engine, not addon API).
    public uint[] LastSentBaseAttackTime = new uint[2];
    public uint[] PendingBaseAttackTime = new uint[2];
    public bool[] HasPendingBaseAttackTime = new bool[2];
    public long[] LastAttackerStateUpdateMs = new long[2];
    // JimsProxy: pet creature family cache. SMSG_PET_SPELLS_MESSAGE on pre-3.1
    // servers doesn't carry the family on the wire — we derive it from the
    // creature template via GetItemId(petGuid). For quest-tame pets the
    // GUID→entry mapping can drop out of cache between updates, so a follow-up
    // SMSG_PET_SPELLS_MESSAGE comes through with creature_family=0. The modern
    // client's PetPaperDollFrame_SetStats then calls strupper on a nil family
    // name and errors. This cache stickies the first successful family lookup
    // so we always send a valid family, cleared only on explicit pet dismiss.
    public ConcurrentDictionary<WowGuid128, ushort> CachedPetCreatureFamily = new();
    public Dictionary<uint, WowGuid128> CachedPetNumbers = new();
    // Tracks quest ids the proxy has issued its own CMSG_QUERY_QUEST_INFO for.
    public HashSet<uint> ProxyIssuedQuestInfoQueries = new();
    // JimsProxy: client-originated CMSG_QUERY_QUEST_INFO gating. Questie's filter-toggle
    // SmoothReset iterates ~10k quest IDs and fires GetQuestTagInfo on each, generating
    // thousands of CMSG_QUERY_QUEST_INFO in a single burst. Without dedupe, Kronos's
    // anti-flood drops the connection. InFlight tracks queries already forwarded so
    // duplicates within the round-trip window are dropped (one modern SMSG response
    // satisfies all client-side waiters for the same quest). Negative cache tracks
    // quest IDs the legacy server returned masked-entry on — typically TBC/Wrath
    // quests in Questie's DB that the 1.12 server has never heard of.
    // ConcurrentDictionary (not HashSet) — written from WorldServer handler thread (Add),
    // WorldClient handler thread (Remove/Add), and the drainer Task thread (Remove).
    // Plain HashSet under concurrent Add/Remove tears its bucket array.
    public ConcurrentDictionary<uint, byte> InFlightClientQuestInfoQueries = new();
    public ConcurrentDictionary<uint, byte> NegativeQuestInfoCache = new();
    // JimsProxy: token-bucket rate limiter for CMSG_QUERY_QUEST_INFO. Questie's
    // cold-start scan bursts ~1500 UNIQUE quest IDs in ~700ms (so in-flight dedupe
    // does nothing on the first pass); this lands at >2000/sec, well past Kronos's
    // ~80-100/sec WorldPacketLimit, and the connection gets dropped. The bucket
    // smooths the burst to a sustained 10/sec with a 20-token initial allowance.
    // PendingQuestQueryQueue holds IDs awaiting a token; QuestQueryDrainerRunning
    // is a 0/1 CAS flag so at most one async drainer pumps the queue per session.
    public System.Collections.Concurrent.ConcurrentQueue<uint> PendingQuestQueryQueue = new();
    public double QuestQueryTokens = 20.0;
    public long QuestQueryLastRefillMs = 0;
    public int QuestQueryDrainerRunning = 0;
    public readonly object QuestQueryBucketLock = new();
    // JimsProxy (pet-scale-resolve-race): Pet GUIDs whose first SCALE_X arrived
    // before SMSG_QUERY_CREATURE_RESPONSE landed.
    public Dictionary<WowGuid128, PendingPetScale> PetScaleResolvePending = new();
    public HashSet<uint> PetScaleProxyQueriedEntries = new();
    // JimsProxy: per-(caster, spell) timestamp of the last forwarded SMSG_SPELL_FAILED_OTHER,
    // used to dedupe Kronos's auto-cast retry storm. Pet auto-cast can fire 5+/sec when the
    // target is out of range or being killed, each emitting a fresh SPELL_FAILED_OTHER. The
    // proxy was forwarding all of them as 3-packet bundles (SpellFailure + SpellFailedOther
    // + CancelSpellVisual), and 10+ CancelSpellVisuals in rapid succession chained into a
    // stuck cast sound on the 1.14.2 client (reported as "imp Firebolt sound stuck").
    // Forwarding the first one is sufficient — the client cancels the cast bar / visual and
    // subsequent same-cast failures add no new state.
    public Dictionary<(WowGuid128 Caster, uint SpellId), long> RecentlyForwardedSpellFailedOther = new();

    // JimsProxy (out-of-range-ghost): guids just destroyed / out-of-ranged and not yet re-created. Vanilla broadcasts a moving unit's trailing MSG_MOVE_* at map-level distance AFTER the per-object-visibility destroy; relaying that stray movement re-ghosts the unit "running in place" on the modern client until re-approach. Movement for these guids is dropped until a CreateObject clears the mark.
    private readonly ConcurrentDictionary<WowGuid128, long> _recentlyDestroyedObjects = new();
    private const long RecentlyDestroyedTtlMs = 10000;

    public void MarkObjectRecentlyDestroyed(WowGuid128 guid)
    {
        if (guid.IsEmpty())
            return;
        _recentlyDestroyedObjects[guid] = Environment.TickCount64;
        // Opportunistic sweep so a long session of spawns/despawns can't grow this unbounded.
        if (_recentlyDestroyedObjects.Count > 4096)
        {
            long cutoff = Environment.TickCount64 - RecentlyDestroyedTtlMs;
            foreach (var kvp in _recentlyDestroyedObjects)
                if (kvp.Value < cutoff)
                    _recentlyDestroyedObjects.TryRemove(kvp.Key, out _);
        }
    }

    public void ClearRecentlyDestroyedObject(WowGuid128 guid) => _recentlyDestroyedObjects.TryRemove(guid, out _);

    public bool WasObjectRecentlyDestroyed(WowGuid128 guid, out long agoMs)
    {
        agoMs = 0;
        if (_recentlyDestroyedObjects.TryGetValue(guid, out long when))
        {
            agoMs = Environment.TickCount64 - when;
            if (agoMs < RecentlyDestroyedTtlMs)
                return true;
            _recentlyDestroyedObjects.TryRemove(guid, out _);
        }
        return false;
    }

    public string LeftChannelName = "";
    public bool IsPassingOnLoot;
    public int GroupUpdateCounter;
    public uint GroupReadyCheckResponses;
    public World.Server.Packets.PartyUpdate?[] CurrentGroups = new World.Server.Packets.PartyUpdate?[2];
    public bool WeWantToLeaveGroup; // Only send kick message when we dont initiated the group-leave
    public List<OwnCharacterInfo> OwnCharacters = [];
    public WowGuid128 CurrentPlayerGuid;
    public long CurrentPlayerCreateTime;
    public OwnCharacterInfo? CurrentPlayerInfo;
    public CurrentPlayerStorage CurrentPlayerStorage = null!;
    public uint CurrentGuildCreateTime;
    public uint CurrentGuildNumAccounts;
    public WowGuid128 CurrentInteractedWithNPC;
    public WowGuid128 CurrentInteractedWithGO;
    // JimsProxy: Auctionator-style Full Scan synthesis state. CMSG_AUCTION_REPLICATE_ITEMS
    // has no legacy 1.12 equivalent; the proxy walks pages internally via
    // CMSG_AUCTION_LIST_ITEMS at the canonical 6s cooldown and assembles rows
    // into one SMSG_AUCTION_REPLICATE_RESPONSE. Default Browse-UI Search uses
    // CMSG_AUCTION_LIST_ITEMS (different opcode), so every REPLICATE_ITEMS is
    // by definition a full-AH scan request — no side-channel needed to
    // differentiate. Lock guards every read/write: accumulator is appended
    // from the WorldClient SMSG thread and read from the WorldSocket CMSG thread.
    public bool AuctionReplicateInProgress;
    public int AuctionReplicatePage;
    public WowGuid128 AuctionReplicateAuctioneer = WowGuid128.Empty;
    public uint AuctionReplicateChangeNumberGlobal;
    public uint AuctionReplicateChangeNumberCursor;
    public uint AuctionReplicateChangeNumberTombstone;
    public List<World.Server.Packets.AuctionItem> AuctionReplicateAccumulator = new();
    public readonly Lock AuctionReplicateLock = new();
    public DateTime AuctionReplicateStartTime;
    // JimsProxy (issue #305-ah): walk owner-items pages and combine into one SMSG; see memory.
    public bool AuctionOwnerWalkInProgress;
    public WowGuid128 AuctionOwnerWalkAuctioneer = WowGuid128.Empty;
    public List<World.Server.Packets.AuctionItem> AuctionOwnerWalkAccumulator = new();
    public readonly Lock AuctionOwnerWalkLock = new();
    public long AuctionOwnerWalkLastFinalizedTickMs;
    public uint LastWhoRequestId;
    public WowGuid128 CurrentPetGuid;
    public WowGuid128 CurrentSelection;
    public WowGuid64 CurrentAttackTarget;        // active CMSG_ATTACK_SWING victim, cleared on ATTACK_STOP/CANCEL_COMBAT
    public bool WaitingForAttackStart;           // true between CMSG_ATTACK_SWING and SMSG_ATTACK_START
    public bool DeferredAttackStop;              // CMSG_ATTACK_STOP received while waiting for SMSG_ATTACK_START
    public uint[] CurrentArenaTeamIds = new uint[3];
    public ConcurrentQueue<ClientCastRequest> PendingNormalCasts = new();  // regular spell casts (queue for proper FIFO handling)
    public ClientCastRequest? CurrentClientNextMeleeCast; // next melee spells (Raptor Strike, Heroic Strike, etc.)
    public ClientCastRequest? CurrentClientAutoRepeatCast; // auto repeat spells (Auto Shot, Shoot, etc.)
    public ConcurrentQueue<ClientCastRequest> PendingPetCasts = new();  // pet spell casts (queue for proper FIFO handling)
    // JimsProxy (issue #43): serializes the drain-filter-rebuild helpers below with the
    // ThreadPool-thread Enqueue in WorldSocket.ForwardHeldGcdCast. Without this, the timer
    // thread can enqueue a held cast mid-drain, causing the drain to observe the new item
    // out-of-order and possibly return it as a FIFO match for an unrelated SMSG_SPELL_GO.
    // Pre-existing Enqueues from the network-thread CMSG handlers stay lock-free (same-thread
    // semantics as before this PR); only the new cross-thread path takes the lock.
    internal readonly object PendingCastsLock = new();

    // JimsProxy (#313): the spell-queue hold-window width is configurable via
    // Framework.Settings.SpellQueueWindowMs (400 retail-accurate / 1000 / 1300 smoothest;
    // default 1300). The hold gates (IsInGcdQueueWindow / HasStartedCastInQueueWindow) read it
    // directly: a press in the last SpellQueueWindowMs of an active GCD or cast bar is held and
    // fired at expiry; earlier presses are forwarded for the server to arbitrate.

    // JimsProxy (issue #43): GCD hold-and-fire state. While the player is on a GCD (tracked
    // from SMSG_SPELL_GO), new CMSG_CAST_SPELL presses are held in _heldGcdCast instead of
    // flooding the server. At GCD expiry a Timer fires the most-recent held cast via the
    // OnGcdHeldCastFire callback set by WorldSocket.
    private readonly object _gcdLock = new();
    private long _gcdExpireTimestampMs;                 // 0 = no GCD active; Environment.TickCount64 baseline
    private ClientCastRequest? _heldGcdCast;            // most recently-pressed cast while GCD active (overwritten on new press)
    private Timer? _gcdExpiryTimer;
    private uint _gcdGeneration;                        // incremented each BeginGcd; callback compares against its captured generation to detect stale fires
    private bool _gcdTimerHasFired;                     // true after OnGcdTimerElapsed runs; prevents orphaned holds
    private uint _lastFiredSpellId;                     // spell ID forwarded by the timer; used to drop same-spell late presses
    public Action<ClientCastRequest>? OnGcdHeldCastFire; // set by WorldSocket at attach time; invoked on a ThreadPool thread at GCD expiry

    // JimsProxy: cast-time spell queue. While a cast-time spell is in progress
    // (HasStartedNormalCast), presses are held here instead of dropped. Fired
    // on SPELL_GO when the cast completes. Most-recent-press-wins, one slot.
    private ClientCastRequest? _heldCastTimeCast;

    public ClientCastRequest? HoldCastDuringCastTime(ClientCastRequest cast)
    {
        lock (_gcdLock)
        {
            var displaced = _heldCastTimeCast;
            _heldCastTimeCast = cast;
            return displaced;
        }
    }

    public ClientCastRequest? TakeHeldCastTimeCast()
    {
        lock (_gcdLock)
        {
            var cast = _heldCastTimeCast;
            _heldCastTimeCast = null;
            return cast;
        }
    }

    // JimsProxy (silent-hold GCD sweep 2026-05-07): returns the dropped cast so callers
    // can ack-fail it to the modern client. Without the ack the button stays lit forever
    // when a cast bar is interrupted (ESC, kick, target death, OOM mid-cast).
    public ClientCastRequest? ClearHeldCastTimeCast()
    {
        lock (_gcdLock)
        {
            var dropped = _heldCastTimeCast;
            _heldCastTimeCast = null;
            return dropped;
        }
    }

    public bool HasNonStartedPendingCastForSpell(uint spellId)
    {
        foreach (var item in PendingNormalCasts)
        {
            if (!item.HasStarted &&
                (item.SpellId == spellId || (item.LegacySpellId != 0 && item.LegacySpellId == spellId)))
                return true;
        }
        return false;
    }

    // JimsProxy: proxy→server RTT measurement for adaptive GCD fire offset.
    private readonly object _rttLock = new();
    private long _lastPingSendTickMs;
    private uint _lastPingSerial;
    private double _smoothedRttMs;
    private int _rttSampleCount;

    //MIRASU - Tracks the unique CastID assigned to in-flight non-player casts so SPELL_GO and
    //MIRASU   SPELL_FAILED_OTHER can reference the same cast the SPELL_START introduced.
    //MIRASU   Without this, mob casts reuse a deterministic CastID (spellId+casterCounter) on
    //MIRASU   every cycle and the modern client treats consecutive casts as the same in-flight
    //MIRASU   cast -- visuals/sounds drift and target-frame cast bars don't dismiss on kick.
    public ConcurrentDictionary<(WowGuid128 caster, uint spellId), WowGuid128> OtherCasterActiveCastIds = new();
    //MIRASU - monotonic sequence used to make non-player CastIDs unique per cast.
    public int OtherCastSequenceCounter;
    public int PlayerChildCastSequence;
    // JimsProxy: tracks unique CastIDs minted at SMSG_SPELL_START for pet casts that
    // don't match PendingPetCasts (i.e. pet AUTO-CASTs, not player-initiated presses).
    // Without this, every auto-cast of a given spell from a given pet shares the same
    // deterministic CastID (spellId + casterCounter); the 1.14.2 client treats multiple
    // SPELL_STARTs with the same CastID as updates to one in-flight cast → audio
    // pipeline overlaps and the cast bar gets stuck (reported as "imp Firebolt sound
    // stuck"). At SPELL_GO we recall the stored CastID so the START/GO pair shares a
    // single unique ID. Player-pressed pet casts hit PendingPetCasts and get
    // overridden with ServerGUID downstream → the auto-cast map entry is a harmless
    // orphan in that case.
    public ConcurrentDictionary<(WowGuid128 caster, uint spellId), WowGuid128> PetAutoCastActiveCastIds = new();
    // JimsProxy (cast-go-castid-recovery): client-facing CastID forwarded for the LOCAL
    // PLAYER's SMSG_SPELL_START, keyed by spellId. HandleSpellGo recalls it when no
    // PendingNormalCast / melee / auto-repeat entry matches at SPELL_GO, so START and GO
    // ship the SAME CastID. The 1.14 client pairs START↔GO by CastID; a mismatch leaves
    // the cast un-terminated → stuck casting animation + looping cast sound. Covers
    // server-initiated player casts with no CMSG (GO loot subspells e.g. Whipper Root
    // "Create Whipper Root Tubers" 15343, weapon/trinket procs) and casts whose pending
    // entry was consumed by an interleaved duplicate CAST_FAILED before the GO (Blade
    // Flurry, re-clicked gathers). Fallback ONLY — never consulted when a real pending
    // cast is dequeued at GO, so normal casts are wire-identical. Cleared on world
    // transfer alongside the pet/other-caster cast-id maps.
    public ConcurrentDictionary<uint, WowGuid128> PlayerForwardedCastIds = new();
    // JimsProxy (T1 identity-pinned cast correspondence): per-spell FIFO of the CastIDs
    // forwarded to the modern client at the LOCAL PLAYER's SMSG_SPELL_START. Supersedes the
    // single-slot PlayerForwardedCastIds (above) when Settings.IdentityPinnedCastIdsActive.
    // A single slot is overwritten when two same-spell casts are in flight at once (the
    // immediate-forward / Low-Latency path), so a later GO recovers the wrong CastID; the
    // FIFO preserves START order so each terminating event consumes the matching forwarded
    // CastID and START↔GO/FAILED pair deterministically, independent of which queue entry
    // the dequeue heuristic picks. Bounded per spell (oldest dropped past the cap) and
    // cleared on reconnect so a missed pop can neither leak nor stale-head a future cast.
    // Lock-guarded plain Dictionary/List (not Concurrent*) so enqueue+bound and the
    // remove-by-value the watchdog needs are atomic; contention is negligible (cast events).
    private readonly Dictionary<uint, List<WowGuid128>> _playerForwardedStartCastIds = new();
    private readonly object _playerForwardedStartCastIdsLock = new();
    private const int MaxForwardedStartCastIdsPerSpell = 8;
    // Tracks last-seen UNIT_CHANNEL_SPELL per unit so we can synthesize
    // SMSG_SPELL_CHANNEL_START/UPDATE for observers (vanilla only sends
    // MSG_CHANNEL_START to the caster, not to nearby players).
    public ConcurrentDictionary<WowGuid128, int> UnitChannelSpells = new();
    // #383: maps a channeling unit -> (GameObject that triggered the channel, spellId).
    // Kronos sends ritual participants UNIT_CHANNEL_SPELL but no channel object, so the
    // 1.14 client has the channel spell yet no portal to face and never strikes the pose.
    // We recall the portal from the unit's SPELL_GO cast-source to restore the object.
    public ConcurrentDictionary<WowGuid128, (WowGuid128 SourceObject, int SpellId)> ChannelSourceObjectByUnit = new();
    public WowGuid64 LastLootTargetGuid;
    //MIRASU - ConcurrentDictionary because abandon-clear runs on the modern-server thread
    //MIRASU   (CMSG_QUEST_LOG_REMOVE_QUEST handler in Server/QuestHandler.cs) while item-credit
    //MIRASU   reads/writes and COMPLETE/FAILED clears run on the WorldClient thread. Plain
    //MIRASU   Dictionary risks torn-state corruption on cross-thread enumeration.
    public ConcurrentDictionary<(uint QuestID, sbyte StorageIndex), uint> QuestItemObjectiveProgress = new();
    //MIRASU - PendingQuestItemCredits is a List (no concurrent equivalent supports predicate-remove).
    //MIRASU   Guard with PendingQuestItemCreditsLock for cross-thread safety. Cleared on
    //MIRASU   COMPLETE/FAILED/abandon for the affected quest's item objectives so a stale buffered
    //MIRASU   credit can't be replayed against a re-accept (or a different quest sharing the item).
    public List<(uint ItemId, uint Count)> PendingQuestItemCredits = new();
    public readonly object PendingQuestItemCreditsLock = new();
    //MIRASU - SMSG_ITEM_PUSH_RESULT for quest items that arrive in the same pickup burst as a
    //MIRASU   buffered SMSG_QUEST_UPDATE_ADD_ITEM (template not cached). The objective lookup
    //MIRASU   would fail at HandleItemPushResult time, so the inventory packet is held here and
    //MIRASU   replayed after the buffered credit is replayed (template cached, dict populated).
    //MIRASU   Held packet is the fully-built ItemPushResult; replay just recomputes
    //MIRASU   QuantityInInventory and sends. Lock-guarded -- HandleItemPushResult and
    //MIRASU   ReplayPendingQuestItemCredits run on the WorldClient thread, but the lock pattern
    //MIRASU   matches PendingQuestItemCredits and survives any future cross-thread caller.
    internal List<HermesProxy.World.Server.Packets.ItemPushResult> PendingItemPushResults = new();
    public readonly object PendingItemPushResultsLock = new();
    public uint CurrentLootCoins; //MIRASU - remembers coin amount from SMSG_LOOT_RESPONSE so proxy can synthesize SMSG_LOOT_MONEY_NOTIFY when client picks up gold (Kronos/TC-1.12 doesn't emit it)
    public List<WowGuid128>? MasterLootCandidates;
    public WowGuid64 LastMasterLootSentTarget;
    public List<int> ActionButtons = [];
    public Dictionary<WowGuid128, Dictionary<byte, int>> UnitAuraDurationUpdateTime = [];
    public Dictionary<WowGuid128, Dictionary<byte, int>> UnitAuraDurationLeft = [];
    public Dictionary<WowGuid128, Dictionary<byte, int>> UnitAuraDurationFull = [];
    public Dictionary<WowGuid128, Dictionary<byte, WowGuid128>> UnitAuraCaster = [];

    // MIRASU (stack-aura-decrement): last AuraDataInfo emitted for each (unit, slot).
    // Used to detect AURAAPPLICATIONS-quad-only updates where a single slot's app
    // byte changed (Lightning Shield charge consumed, Sunder Armor stack added,
    // Devouring Plague tick stacked) and re-emit just that slot without spuriously
    // refreshing the other three slots packed into the same uint32.
    public Dictionary<WowGuid128, Dictionary<byte, AuraDataInfo>> UnitAuraLastEmitted = [];

    // JimsProxy (Rupture-DoT-Lingering-Icon): combo-point cache + finisher-cast snapshot.
    // Vanilla servers don't send aura duration for enemy debuffs, and CP-scaling finishers
    // (Rupture, Kidney Shot) compute their duration server-side as (base + perCp × CP).
    // We cache CP from SMSG_UPDATE_COMBO_POINTS and snapshot it on the outgoing
    // CMSG_CAST_SPELL — at that moment the server hasn't consumed CP yet, so the cached
    // value is the real CP that will be applied. Aura-apply paths consult the snapshot to
    // synthesize the correct duration locally before the legacy server clears the CP.
    public byte CurrentComboPoints;
    public WowGuid128 CurrentComboTarget;
    private (uint SpellId, WowGuid128 Target, byte ComboPoints, int Tick)? _pendingFinisherCast;
    public Dictionary<WowGuid128, PlayerCache> CachedPlayers = [];
    public HashSet<WowGuid128> IgnoredPlayers = [];
    public Dictionary<WowGuid128, uint> PlayerGuildIds = [];
    public readonly Lock ObjectCacheLock = new();
    public Dictionary<WowGuid128, Dictionary<int, UpdateField>> ObjectCacheLegacy = [];
    public Dictionary<WowGuid128, UpdateFieldsArray> ObjectCacheModern = [];
    public Dictionary<WowGuid128, ObjectType> OriginalObjectTypes = [];
    public Dictionary<WowGuid128, uint[]> ItemGems = [];
    public Dictionary<uint, Class> CreatureClasses = [];
    public Dictionary<string, int> ChannelIds = [];
    public Dictionary<int, string> ChannelNamesById = [];
    public Dictionary<uint, uint> ItemBuyCount = [];
    public Dictionary<uint, uint> RealSpellToLearnSpell = [];
    public Dictionary<uint, ArenaTeamData> ArenaTeams = [];
    public World.Server.Packets.MailListResult? PendingMailListPacket;
    public HashSet<uint> RequestedItemTextIds = [];
    public Dictionary<uint, string> ItemTexts = [];
    public Dictionary<uint, uint> BattleFieldQueueTypes = [];
    public Dictionary<uint, long> BattleFieldQueueTimes = [];
    public Dictionary<uint, uint> DailyQuestsDone = [];
    public HashSet<WowGuid128> FlagCarrierGuids = [];
    public Dictionary<WowGuid64, ushort> ObjectSpawnCount = [];
    public HashSet<WowGuid64> DespawnedGameObjects = [];
    public HashSet<WowGuid128> HunterPetGuids = [];
    public Dictionary<WowGuid128, ArenaTeamInspectData[]> PlayerArenaTeams = [];
    public HashSet<string> AddonPrefixes = [];
    public Dictionary<byte, Dictionary<byte, int>> FlatSpellMods = [];
    public Dictionary<byte, Dictionary<byte, int>> PctSpellMods = [];
    public Dictionary<WowGuid128, Dictionary<uint, WowGuid128>> LastAuraCasterOnTarget = [];
    // JimsProxy (aura-refresh-after-far-objects): GUIDs the legacy server
    // just told us to remove via FarObjects. The modern client drops the
    // unit's aura state on OutOfRangeGuids, so the next Values update for
    // any of these units must arrive as AuraUpdate(UpdateAll=true) to
    // reseed the buff/debuff bar from scratch — otherwise the next delta
    // is merged against state the client no longer has, and the previous
    // buff bar lingers stale until reload.
    public HashSet<WowGuid128> NeedsFullAuraRefresh = [];
    // JimsProxy (synth-spell-start-for-autoshot): timestamp of the most recent
    // natural SMSG_SPELL_START forwarded for the local player's ranged auto
    // attack (Auto Shot 75 / Shoot 5019). The 1.12 server only emits SPELL_START
    // at toggle/retarget — every subsequent auto-repeat tick arrives as a bare
    // SPELL_GO. Modern Classic 1.14 servers emit SPELL_START per tick, so any
    // CAST_START-driven swing-timer addon (e.g. Kaedin's swing timer) only
    // fires once per series via the proxy. HandleSpellGo synthesizes a
    // SPELL_START before the GO when no natural one was forwarded recently
    // (window: AutoShotSynthSpellStartGapMs).
    public Dictionary<uint, long> LastNaturalAutoShotSpellStartMs = [];
    public TradeSession? CurrentTrade = null;
    public HashSet<uint> RequestedItemHotfixes = [];
    public HashSet<uint> RequestedItemSparseHotfixes = [];

    // Mobs we've seen send Flying spline or FixedZ movement flags. Vanilla servers
    // don't populate UNIT_FIELD_HOVERHEIGHT consistently (Twinstar e.g. leaves it at 0),
    // so we need a server-agnostic hover signal. Once a guid lands here, all subsequent
    // packets for it get the hover override regardless of HOVERHEIGHT.
    public HashSet<WowGuid128> KnownHoveringMobs = [];

    // MIRASU (swim-mob basketball-bounce 2026-05-23): mobs we've observed with
    // MovementFlag.Swimming set (Rotgrip in Maraudon, naga in Desolace, etc.).
    // Modern 1.14 client expects UNIT_BYTES_1.AnimTier = Swim and spline flags
    // without SmoothGroundPath for these — vanilla doesn't carry AnimTier so we
    // synthesize it. Same shape as KnownHoveringMobs above.
    public HashSet<WowGuid128> KnownSwimmingMobs = [];

    // JimsProxy (Tallstrider-Fix): per-GUID last-known facing orientation, populated from
    // any MovementInfo we observe (spawn, heartbeat, ObjectUpdate movement block). Used by
    // MovementHandler.HandleMonsterMove to compare the creature's current facing against
    // the spline's first-segment direction — if the angle change is large, we treat the
    // move as a state-transition (aggro/turn-to-target) and skip SplineFlagModern.Steering
    // so the modern client snaps to the new heading instead of slowly rotating the body
    // through the path. Small angle changes get Steering for smooth patrol corners.
    public Dictionary<WowGuid128, float> LastKnownOrientation = new();

    private GameSessionData()
    {

    }

    public static GameSessionData CreateNewGameSessionData(GlobalSessionData globalSession, GameSessionData? previous = null)
    {
        var self = new GameSessionData();
        self.CurrentPlayerStorage = new CurrentPlayerStorage(globalSession);

        // Realm-scoped caches survive a /camp on a native 1.12 client (NameCache, ignore
        // list, guild membership of other players are all per-realm files, not per-character).
        // Wiping them on logout caused SMSG_INSPECT_RESULT to silently bail at the
        // CachedPlayers lookup whenever the modern client's own persistent name cache
        // satisfied the unit-frame name without issuing a fresh CMSG_QUERY_PLAYER_NAME —
        // the proxy then had no entry to fill the inspect Name/Class/Race/Sex fields.
        if (previous != null)
        {
            self.CachedPlayers = previous.CachedPlayers;
            self.PlayerGuildIds = previous.PlayerGuildIds;
            self.IgnoredPlayers = previous.IgnoredPlayers;
        }
        return self;
    }

    /// <summary>
    /// Test-only factory — skips CurrentPlayerStorage initialization so tests that only need
    /// the GCD hold state machine (issue #43) can construct a bare GameSessionData without
    /// standing up a full GlobalSessionData graph.
    /// </summary>
    internal static GameSessionData CreateForTesting()
    {
        return new GameSessionData();
    }
    
    public uint GetCurrentGroupSize()
    {
        var group = GetCurrentGroup();
        if (group == null)
            return 0;

        // Don't count self.
        return (uint)(group.PlayerList.Count > 1 ? group.PlayerList.Count - 1 : 0);
    }
    public WowGuid128 GetCurrentGroupLeader()
    {
        var group = GetCurrentGroup();
        if (group == null)
            return WowGuid128.Empty;

        return group.LeaderGUID;
    }
    public LootMethod GetCurrentLootMethod()
    {
        var group = GetCurrentGroup();
        if (group == null)
            return LootMethod.FreeForAll;

        return group.LootSettings.Method;
    }
    public WowGuid128 GetCurrentGroupGuid()
    {
        var group = GetCurrentGroup();
        if (group == null)
            return WowGuid128.Empty;

        return group.PartyGUID;
    }
    public World.Server.Packets.PartyUpdate? GetCurrentGroup()
    {
        return CurrentGroups[GetCurrentPartyIndex()];
    }
    public sbyte GetCurrentPartyIndex()
    {
        return (sbyte)(IsInBattleground() ? 1 : 0);
    }
    public byte GetItemSpellSlot(WowGuid128 guid, uint spellId)
    {
        int OBJECT_FIELD_ENTRY = LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_ENTRY);
        if (OBJECT_FIELD_ENTRY < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates == null)
            return 0;

        uint itemId = updates[OBJECT_FIELD_ENTRY].UInt32Value;
        return GameData.GetItemEffectSlot(itemId, spellId);
    }
    /// <summary>
    /// If the modern client sent a spell id that the legacy server doesn't know for this item
    /// (e.g. SoM 1.14.1+ renumbered Diamond Flask 17626 → 363880), resolve the legacy spell id
    /// from the item's cached ItemEffects (slot 0 = on-use trinket/potion entry).
    /// Returns 0 when no remap is needed (modern id == legacy id) or when item data isn't cached yet.
    /// </summary>
    public uint GetLegacyItemSpellId(WowGuid128 itemGuid, uint modernSpellId)
    {
        uint itemId = GetItemId(itemGuid);
        if (itemId == 0)
            return 0;

        var slotMap = GameData.GetItemEffectSlotMap(itemId);
        if (slotMap == null)
            return 0;

        // Modern spell id is already known to the legacy server — no remap needed.
        if (slotMap.ContainsKey(modernSpellId))
            return 0;

        // On-use items keep their effect at slot 0; return that legacy spell id.
        foreach (var kvp in slotMap)
        {
            if (kvp.Value == 0)
            {
                // Also remember the legacy → modern direction so subsequent aura updates
                // (which carry the legacy spell id) can be translated back to the modern id
                // the client recognizes — otherwise the buff icon never appears next to the minimap.
                // We learn it here from the client's actual CMSG_USE_ITEM rather than relying on
                // ItemEffect CSV data, which can be stale for SoM-renumbered items.
                GameData.LegacyToModernSpellId[kvp.Key] = modernSpellId;
                return kvp.Key;
            }
        }
        return 0;
    }
    public uint GetItemId(WowGuid128 guid)
    {
        int OBJECT_FIELD_ENTRY = LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_ENTRY);
        if (OBJECT_FIELD_ENTRY < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates == null)
            return 0;

        // JimsProxy (mc-player-pet-bar 2026-05-07): players don't have OBJECT_FIELD_ENTRY in
        // their cached field set, so a raw indexer access throws KeyNotFoundException ('3')
        // when called for an MC'd player target. Several callers (notably the SMSG_PET_SPELLS
        // handler in PetHandler.cs) catch broadly and silently drop their entire output —
        // empty pet bar in BG MC. Treat missing field as "no entry" and return 0.
        if (!updates.TryGetValue(OBJECT_FIELD_ENTRY, out var entryField))
            return 0;
        return entryField.UInt32Value;
    }
    public void SetFlatSpellMod(byte spellMod, byte spellMask, int amount)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(FlatSpellMods, spellMod, out _);
        dict ??= [];
        dict[spellMask] = amount;
    }
    public void SetPctSpellMod(byte spellMod, byte spellMask, int amount)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(PctSpellMods, spellMod, out _);
        dict ??= [];
        dict[spellMask] = amount;
    }
    public ArenaTeamInspectData GetArenaTeamDataForPlayer(WowGuid128 guid, byte slot)
    {
        if (PlayerArenaTeams.TryGetValue(guid, out var teams) && teams[slot] != null)
            return teams[slot];

        return new ArenaTeamInspectData();
    }
    public void StoreArenaTeamDataForPlayer(WowGuid128 guid, byte slot, ArenaTeamInspectData team)
    {
        ref var teams = ref CollectionsMarshal.GetValueRefOrAddDefault(PlayerArenaTeams, guid, out _);
        teams ??= new ArenaTeamInspectData[ArenaTeamConst.MaxArenaSlot];
        teams[slot] = team;
    }
    public WowGuid64 GetInventorySlotItem(int slot)
    {
        int PLAYER_FIELD_INV_SLOT_HEAD = LegacyVersion.GetUpdateField(PlayerField.PLAYER_FIELD_INV_SLOT_HEAD);
        if (PLAYER_FIELD_INV_SLOT_HEAD >= 0)
        {
            var updates = GetCachedObjectFieldsLegacy(CurrentPlayerGuid);
            if (updates != null)
                return updates.GetGuidValue(PLAYER_FIELD_INV_SLOT_HEAD + slot * 2).To64();
        }
        return WowGuid64.Empty;
    }
    public WowGuid64 GetInventorySlotItem(byte containerSlot, byte slot)
    {
        // Main backpack: read directly from player inventory fields
        if (containerSlot == ItemConst.NullSlot)
            return GetInventorySlotItem(slot);

        // Extra bag: read from the bag container's slot fields
        var bagGuid64 = GetInventorySlotItem(containerSlot);
        if (bagGuid64 == WowGuid64.Empty)
            return WowGuid64.Empty;

        int containerSlotField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_SLOT_1);
        if (containerSlotField < 0)
            return WowGuid64.Empty;

        var bagGuid128 = bagGuid64.To128(this);
        var bagFields = GetCachedObjectFieldsLegacy(bagGuid128);
        if (bagFields == null)
            return WowGuid64.Empty;

        return bagFields.GetGuidValue(containerSlotField + slot * 2);
    }

    // JimsProxy: the items in the last inventory move, remembered so we can repair an
    // InventoryChangeFailure the server sends with empty item GUIDs (it does this for
    // invalid-slot rejections — e.g. the modern client's phantom keyring slots 13-32 that
    // Kronos lacks). Without the GUIDs the client can't unlock the source item, so it
    // stays stuck until relog. Set in the item-move handlers, consumed in the failure handler.
    public WowGuid128 LastMoveItem0;
    public WowGuid128 LastMoveItem1;
    public int LastMoveItemsTickMs;

    public uint GetItemStackCount(WowGuid128 itemGuid)
    {
        uint count = GetLegacyFieldValueUInt32(itemGuid, ItemField.ITEM_FIELD_STACK_COUNT);
        return count > 0 ? count : 1;
    }
    //MIRASU - count all instances of itemEntry across equipped + backpack + bag contents.
    //MIRASU   Mirrors vmangos Player::GetItemCount(item, /*inBankAlso=*/false). Used as a
    //MIRASU   fallback for quest item-objective progress when the proxy hasn't yet seen a
    //MIRASU   SMSG_QUEST_UPDATE_ADD_ITEM credit (typical case: player relogs mid-quest with
    //MIRASU   the item already in inventory; vmangos writes item counters to slot+GOcount
    //MIRASU   in PLAYER_QUEST_LOG, which is unreadable by the modern client because that
    //MIRASU   "extra" log slot belongs to a different quest in vanilla's allocation scheme,
    //MIRASU   so the modern client renders item objectives at 0/N until we synthesize the
    //MIRASU   count ourselves).
    public uint CountItemsByEntry(uint itemEntry)
    {
        if (itemEntry == 0)
            return 0;

        uint total = 0;
        int objectFieldEntry = LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_ENTRY);
        if (objectFieldEntry < 0)
            return 0;

        //MIRASU - equipped slots 0..18 + backpack 23..38. Skip the bag slots themselves
        //MIRASU   (19..22) — we iterate their contents in the separate loop below.
        for (int slot = 0; slot < World.Enums.Vanilla.InventorySlots.ItemEnd; slot++)
        {
            if (slot >= World.Enums.Vanilla.InventorySlots.BagStart && slot < World.Enums.Vanilla.InventorySlots.BagEnd)
                continue;

            var itemGuid64 = GetInventorySlotItem(slot);
            if (itemGuid64 == WowGuid64.Empty)
                continue;

            var itemGuid128 = itemGuid64.To128(this);
            var itemFields = GetCachedObjectFieldsLegacy(itemGuid128);
            if (itemFields == null)
                continue;

            if (itemFields.TryGetValue(objectFieldEntry, out var entryVal) && entryVal.UInt32Value == itemEntry)
                total += GetItemStackCount(itemGuid128);
        }

        int containerSlotField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_SLOT_1);
        int numSlotsField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_NUM_SLOTS);
        if (containerSlotField < 0 || numSlotsField < 0)
            return total;

        for (int bagIdx = World.Enums.Vanilla.InventorySlots.BagStart; bagIdx < World.Enums.Vanilla.InventorySlots.BagEnd; bagIdx++)
        {
            var bagGuid64 = GetInventorySlotItem(bagIdx);
            if (bagGuid64 == WowGuid64.Empty)
                continue;

            var bagGuid128 = bagGuid64.To128(this);
            var bagFields = GetCachedObjectFieldsLegacy(bagGuid128);
            if (bagFields == null)
                continue;
            if (!bagFields.TryGetValue(numSlotsField, out var numSlotsValue))
                continue;
            int numSlots = (int)numSlotsValue.UInt32Value;

            for (int s = 0; s < numSlots; s++)
            {
                var slotGuid64 = bagFields.GetGuidValue(containerSlotField + s * 2);
                if (slotGuid64 == WowGuid64.Empty)
                    continue;

                var slotGuid128 = slotGuid64.To128(this);
                var slotFields = GetCachedObjectFieldsLegacy(slotGuid128);
                if (slotFields == null)
                    continue;

                if (slotFields.TryGetValue(objectFieldEntry, out var entryVal) && entryVal.UInt32Value == itemEntry)
                    total += GetItemStackCount(slotGuid128);
            }
        }

        return total;
    }
    public (byte containerSlot, byte slot)? FindItemInInventory(WowGuid64 itemGuid64)
    {
        // Search main backpack
        for (int i = World.Enums.Vanilla.InventorySlots.ItemStart; i < World.Enums.Vanilla.InventorySlots.ItemEnd; i++)
        {
            if (GetInventorySlotItem(i) == itemGuid64)
                return (ItemConst.NullSlot, (byte)i);
        }

        // Search extra bag containers
        int containerSlotField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_SLOT_1);
        int numSlotsField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_NUM_SLOTS);
        if (containerSlotField < 0 || numSlotsField < 0)
            return null;

        for (int bagIdx = World.Enums.Vanilla.InventorySlots.BagStart; bagIdx < World.Enums.Vanilla.InventorySlots.BagEnd; bagIdx++)
        {
            var bagGuid64 = GetInventorySlotItem(bagIdx);
            if (bagGuid64 == WowGuid64.Empty)
                continue;

            var bagGuid128 = bagGuid64.To128(this);
            var bagFields = GetCachedObjectFieldsLegacy(bagGuid128);
            if (bagFields == null)
                continue;

            if (!bagFields.TryGetValue(numSlotsField, out var numSlotsValue))
                continue;
            int numSlots = (int)numSlotsValue.UInt32Value;

            for (int slot = 0; slot < numSlots; slot++)
            {
                var slotGuid = bagFields.GetGuidValue(containerSlotField + slot * 2);
                if (slotGuid == itemGuid64)
                    return ((byte)bagIdx, (byte)slot);
            }
        }

        return null;
    }
    public (byte containerSlot, byte slot)? FindEmptyInventorySlot()
    {
        // Search main backpack first
        for (int i = World.Enums.Vanilla.InventorySlots.ItemStart; i < World.Enums.Vanilla.InventorySlots.ItemEnd; i++)
        {
            if (GetInventorySlotItem(i) == WowGuid64.Empty)
                return (ItemConst.NullSlot, (byte)i);
        }

        // Search extra bag containers
        int containerSlotField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_SLOT_1);
        int numSlotsField = LegacyVersion.GetUpdateField(ContainerField.CONTAINER_FIELD_NUM_SLOTS);
        if (containerSlotField < 0 || numSlotsField < 0)
            return null;

        for (int bagIdx = World.Enums.Vanilla.InventorySlots.BagStart; bagIdx < World.Enums.Vanilla.InventorySlots.BagEnd; bagIdx++)
        {
            var bagGuid64 = GetInventorySlotItem(bagIdx);
            if (bagGuid64 == WowGuid64.Empty)
                continue;

            var bagGuid128 = bagGuid64.To128(this);
            var bagFields = GetCachedObjectFieldsLegacy(bagGuid128);
            if (bagFields == null)
                continue;

            if (!bagFields.TryGetValue(numSlotsField, out var numSlotsValue))
                continue;
            int numSlots = (int)numSlotsValue.UInt32Value;

            for (int slot = 0; slot < numSlots; slot++)
            {
                var slotGuid = bagFields.GetGuidValue(containerSlotField + slot * 2);
                if (slotGuid == WowGuid64.Empty)
                    return ((byte)bagIdx, (byte)slot);
            }
        }

        return null;
    }
    public ushort GetObjectSpawnCounter(WowGuid64 guid)
    {
        if (ObjectSpawnCount.TryGetValue(guid, out ushort count))
            return count;
        return 0;
    }
    public void IncrementObjectSpawnCounter(WowGuid64 guid)
    {
        ref ushort count = ref CollectionsMarshal.GetValueRefOrAddDefault(ObjectSpawnCount, guid, out bool existed);
        if (existed)
            count++;
        // else: default(ushort) = 0, matching the original "Add(guid, 0)" behavior.
    }
    public void SetDailyQuestSlot(uint slot, uint questId)
    {
        if (questId != 0)
            DailyQuestsDone[slot] = questId;
        else
            DailyQuestsDone.Remove(slot);
    }
    public bool IsAlliancePlayer(WowGuid128 guid)
    {
        PlayerCache? cache;
        if (CachedPlayers.TryGetValue(guid, out cache))
            return GameData.IsAllianceRace(cache.RaceId);
        return false;
    }
    public bool IsInBattleground()
    {
        if (CurrentMapId == null)
            return false;

        uint bgId = GameData.GetBattlegroundIdFromMapId((uint)CurrentMapId);
        if (bgId == 0)
        {
            return false;
        }

        // Only if we are properly queued for the BG.
        foreach (var queue in BattleFieldQueueTypes)
        {
            if (LegacyVersion.RemovedInVersion(Enums.ClientVersionBuild.V2_0_1_6180))
            {
                if (queue.Value == CurrentMapId)
                    return true;
            }
            else
            {
                if (queue.Value == bgId)
                    return true;
            }
        }

        return false;
    }
    public long GetBattleFieldQueueTime(uint queueSlot)
    {
        if (BattleFieldQueueTimes.TryGetValue(queueSlot, out var time))
            return time;

        time = Time.UnixTime;
        BattleFieldQueueTimes.Add(queueSlot, time);
        return time;
    }
    public void StoreBattleFieldQueueType(uint queueSlot, uint mapOrBgId)
    {
        BattleFieldQueueTypes[queueSlot] = mapOrBgId;
    }
    public uint GetBattleFieldQueueType(uint queueSlot)
    {
        return BattleFieldQueueTypes.TryGetValue(queueSlot, out var value) ? value : 0u;
    }
    public void StoreAuraDurationLeft(WowGuid128 guid, byte slot, int duration, int currentTime)
    {
        ref var leftDict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraDurationLeft, guid, out _);
        leftDict ??= [];
        leftDict[slot] = duration;

        ref var timeDict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraDurationUpdateTime, guid, out _);
        timeDict ??= [];
        timeDict[slot] = currentTime;
    }
    public void StoreAuraDurationFull(WowGuid128 guid, byte slot, int duration)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraDurationFull, guid, out _);
        dict ??= [];
        dict[slot] = duration;
    }
    public void ClearAuraDuration(WowGuid128 guid, byte slot)
    {
        if (UnitAuraDurationUpdateTime.TryGetValue(guid, out var timeDict))
            timeDict.Remove(slot);

        if (UnitAuraDurationLeft.TryGetValue(guid, out var leftDict))
            leftDict.Remove(slot);

        if (UnitAuraDurationFull.TryGetValue(guid, out var fullDict))
            fullDict.Remove(slot);
    }
    public void GetAuraDuration(WowGuid128 guid, byte slot, out int left, out int full)
    {
        left = -1;
        if (UnitAuraDurationLeft.TryGetValue(guid, out var leftDict) &&
            leftDict.TryGetValue(slot, out var leftVal))
            left = leftVal;

        full = left;
        if (UnitAuraDurationFull.TryGetValue(guid, out var fullDict) &&
            fullDict.TryGetValue(slot, out var fullVal))
            full = fullVal;

        if (left > 0 &&
            UnitAuraDurationUpdateTime.TryGetValue(guid, out var timeDict) &&
            timeDict.TryGetValue(slot, out var time))
            left -= Environment.TickCount - time;
    }
    public void StoreAuraCaster(WowGuid128 target, byte slot, WowGuid128 caster)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraCaster, target, out _);
        dict ??= [];
        dict[slot] = caster;
    }
    public void ClearAuraCaster(WowGuid128 guid, byte slot)
    {
        if (UnitAuraCaster.TryGetValue(guid, out var dict))
            dict.Remove(slot);
    }
    // JimsProxy (target-buffs-stuck-after-render-roundtrip): drop all
    // per-target aura state when a unit is destroyed (left render
    // distance, despawned, or died). Without this, the modern client
    // surfaces stale buffs the next time the target re-enters render.
    // Returns the number of (slot,duration) entries evicted from
    // UnitAuraDurationLeft so the caller can record diagnostics.
    public int EvictUnitAuraState(WowGuid128 guid)
    {
        int evicted = UnitAuraDurationLeft.TryGetValue(guid, out var leftDict) ? leftDict.Count : 0;
        UnitAuraDurationUpdateTime.Remove(guid);
        UnitAuraDurationLeft.Remove(guid);
        UnitAuraDurationFull.Remove(guid);
        UnitAuraCaster.Remove(guid);
        UnitAuraLastEmitted.Remove(guid);
        return evicted;
    }
    public void StoreLastEmittedAura(WowGuid128 guid, byte slot, AuraDataInfo data)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraLastEmitted, guid, out _);
        dict ??= [];
        dict[slot] = data;
    }
    public AuraDataInfo? GetLastEmittedAura(WowGuid128 guid, byte slot)
    {
        if (UnitAuraLastEmitted.TryGetValue(guid, out var dict) &&
            dict.TryGetValue(slot, out var data))
            return data;
        return null;
    }
    public void ClearLastEmittedAura(WowGuid128 guid, byte slot)
    {
        if (UnitAuraLastEmitted.TryGetValue(guid, out var dict))
            dict.Remove(slot);
    }
    public WowGuid128 GetAuraCaster(WowGuid128 target, byte slot)
    {
        if (UnitAuraCaster.TryGetValue(target, out var dict) &&
            dict.TryGetValue(slot, out var caster))
            return caster;

        return default;
    }
    public WowGuid128 GetAuraCaster(WowGuid128 target, byte slot, uint spellId)
    {
        WowGuid128 caster = GetAuraCaster(target, slot);
        if (caster == default)
        {
            caster = GetLastAuraCasterOnTarget(target, spellId);
            if (caster != default)
                StoreAuraCaster(target, slot, caster);
        }

        return caster;
    }
    public void StoreLastAuraCasterOnTarget(WowGuid128 target, uint spellId, WowGuid128 caster)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(LastAuraCasterOnTarget, target, out _);
        dict ??= [];
        dict[spellId] = caster;
    }

    // JimsProxy (Rupture-DoT-Lingering-Icon): record the CP-scaling finisher we just sent
    // to the server. Called from CMSG_CAST_SPELL handling, before the server has consumed
    // the player's combo points. The aura-apply paths (SendAuraRefreshUpdate and the
    // UpdateHandler aura-discovery loop) consult this snapshot to compute the real
    // server-side duration for enemy debuffs that don't get SMSG_UPDATE_AURA_DURATION.
    public void StorePendingFinisherCast(uint spellId, WowGuid128 target, byte comboPoints)
    {
        _pendingFinisherCast = (spellId, target, comboPoints, Environment.TickCount);
    }

    /// <summary>
    /// JimsProxy: returns the CP-scaled aura duration in milliseconds if the matching
    /// finisher cast was observed within ~3 s. The TTL is generous because aura discovery
    /// can lag behind SMSG_SPELL_GO by a packet or two on busy emulators. Returns null
    /// when no matching snapshot exists (proxy started mid-fight, off-screen mob debuff,
    /// non-CP-scaling spell, etc.) so the caller can fall back to the CSV.
    /// </summary>
    public int? TryGetPendingFinisherDurationMs(uint spellId, WowGuid128 target)
    {
        if (_pendingFinisherCast is not { } pending)
            return null;
        if (pending.SpellId != spellId || pending.Target != target)
            return null;
        if (Environment.TickCount - pending.Tick > 3000)
            return null;
        return GameData.TryGetComboPointDuration(spellId, pending.ComboPoints);
    }
    public WowGuid128 GetLastAuraCasterOnTarget(WowGuid128 target, uint spellId)
    {
        if (LastAuraCasterOnTarget.TryGetValue(target, out var dict) &&
            dict.TryGetValue(spellId, out var caster))
        {
            dict.Remove(spellId);
            return caster;
        }

        return default;
    }

    // Spell Cast Queue Helper Methods

    /// <summary>
    /// Try to find and dequeue a pending cast by SpellId. <paramref name="preferStarted"/>
    /// selects which entry wins when multiple same-spell entries are queued — the off-GCD
    /// double-send shape in Low-Latency mode, where the CMSG-side dup guard is bypassed and two
    /// entries coexist (e.g. Blade Flurry: the first press's START marked entry A (started); the
    /// duplicate left entry B (unstarted)):
    /// <list type="bullet">
    /// <item>SMSG_SPELL_GO and <i>real</i> failures pass <c>true</c> — they complete/fail the cast
    /// SMSG_SPELL_START opened (the STARTED entry, whose CastID the client's cast visual is
    /// showing), so START and GO pair. Falls back to the oldest unstarted entry for the skip-START
    /// instant path (server emits GO with no preceding START).</item>
    /// <item>A duplicate's NOT_READY / SpellInProgress rejection passes <c>false</c> — it must fail
    /// the UNSTARTED dup and LEAVE the started entry for its in-flight GO. Falls back to a started
    /// entry only if no unstarted one exists.</item>
    /// </list>
    /// Routing the two callers to opposite preferences is what makes the double-send pair
    /// correctly in BOTH packet orderings (GO-before-fail and fail-before-GO). See H7.
    /// </summary>
    public bool TryDequeuePendingNormalCast(uint spellId, out ClientCastRequest? cast, bool preferStarted = true)
    {
        var pending = new List<ClientCastRequest>();
        cast = null;
        ClientCastRequest? fallback = null;
        int fallbackIndex = -1;
        bool sawOppositeMatch = false;   // a same-spell entry in the non-preferred state was also queued

        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out var current))
            {
                bool matches = CastMatchesSpellId(current, spellId);
                bool preferred = matches && current.HasStarted == preferStarted;
                if (matches && !preferred)
                    sawOppositeMatch = true;

                if (cast == null && preferred)
                {
                    cast = current;                       // first preferred match (FIFO order)
                }
                else if (cast == null && matches && fallback == null)
                {
                    fallback = current;                   // remember the first non-preferred match
                    fallbackIndex = pending.Count;
                    pending.Add(current);
                }
                else
                {
                    pending.Add(current);
                }
            }

            // No preferred match — fall back to the first non-preferred same-spell entry.
            if (cast == null && fallback != null)
            {
                cast = fallback;
                pending.RemoveAt(fallbackIndex);
            }

            foreach (var item in pending)
            {
                PendingNormalCasts.Enqueue(item);
            }
        }

        // DIAGNOSTIC (H7 stuck-cast investigation): fires only when the dequeue resolved to its
        // PREFERRED entry while a same-spell entry in the OPPOSITE state was also queued — the
        // exact double-send ambiguity H7 describes, now resolved correctly. preferStarted=true
        // (GO) → the started entry was kept paired; preferStarted=false (dup-failure) → the
        // started entry was spared for its GO. Presence in the tester's JSONL confirms the
        // condition was occurring. Remove when the investigation closes.
        if (cast != null && sawOppositeMatch && cast.HasStarted == preferStarted && Framework.Settings.DebugOutput)
        {
            Log.Event(preferStarted ? "cast.go.prefer_started" : "cast.fail.spared_started", new
            {
                spell_id = spellId,
                cast_id = cast.ServerGUID.ToString(),
            });
        }

        return cast != null;
    }

    /// <summary>
    /// Match a pending cast against an incoming server spellId, accepting either
    /// the modern (client-sent) SpellId or the LegacySpellId we resolved at item-use time.
    /// Needed for SoM 1.14.1+ items where Blizzard renumbered the on-use spell id
    /// (e.g. Diamond Flask 17626 → 363880); the legacy emulator still replies with the old id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CastMatchesSpellId(ClientCastRequest cast, uint spellId)
    {
        return cast.SpellId == spellId || (cast.LegacySpellId != 0 && cast.LegacySpellId == spellId);
    }

    /// <summary>
    /// Try to find a pending cast by SpellId and mark it as started (for SPELL_START).
    /// </summary>
    public bool TryMarkPendingNormalCastStarted(uint spellId, out ClientCastRequest? cast)
    {
        cast = null;

        foreach (var item in PendingNormalCasts)
        {
            if (CastMatchesSpellId(item, spellId) && !item.HasStarted)
            {
                item.HasStarted = true;
                item.StartedAtTickMs = Environment.TickCount64;
                cast = item;
                return true;
            }
        }

        return false;
    }

    // JimsProxy (T1 identity-pinned cast correspondence) — per-spell forwarded-START CastID
    // FIFO helpers. Active only when Settings.IdentityPinnedCastIdsActive; OFF path never
    // calls these. See _playerForwardedStartCastIds.

    /// <summary>
    /// Record the CastID forwarded to the client at the local player's SMSG_SPELL_START.
    /// Oldest-first; bounded so a missed pop can't grow without bound.
    /// </summary>
    public void EnqueueForwardedStartCastId(uint spellId, WowGuid128 castId)
    {
        lock (_playerForwardedStartCastIdsLock)
        {
            if (!_playerForwardedStartCastIds.TryGetValue(spellId, out var list))
            {
                list = new List<WowGuid128>(2);
                _playerForwardedStartCastIds[spellId] = list;
            }
            list.Add(castId);
            while (list.Count > MaxForwardedStartCastIdsPerSpell)
                list.RemoveAt(0);
        }
    }

    /// <summary>
    /// Pop the oldest forwarded-START CastID for a spell — a terminating event (SPELL_GO or a
    /// real CAST_FAILED) consuming the cast it opened.
    /// </summary>
    public bool TryPopForwardedStartCastId(uint spellId, out WowGuid128 castId)
    {
        lock (_playerForwardedStartCastIdsLock)
        {
            if (_playerForwardedStartCastIds.TryGetValue(spellId, out var list) && list.Count > 0)
            {
                castId = list[0];
                list.RemoveAt(0);
                if (list.Count == 0)
                    _playerForwardedStartCastIds.Remove(spellId);
                return true;
            }
        }
        castId = default;
        return false;
    }

    /// <summary>
    /// Peek the oldest forwarded-START CastID without consuming it. SMSG_SPELL_FAILURE only
    /// peeks the pending cast (the trailing SMSG_CAST_FAILED / GO / watchdog is what pops),
    /// so it stamps from the FIFO front without removing it.
    /// </summary>
    public bool TryPeekForwardedStartCastId(uint spellId, out WowGuid128 castId)
    {
        lock (_playerForwardedStartCastIdsLock)
        {
            if (_playerForwardedStartCastIds.TryGetValue(spellId, out var list) && list.Count > 0)
            {
                castId = list[0];
                return true;
            }
        }
        castId = default;
        return false;
    }

    /// <summary>
    /// Remove a specific forwarded-START CastID by value. The watchdog evicts a known cast
    /// that may not be the oldest, so removing by value (not popping the front) keeps the FIFO
    /// consistent — a later same-spell cast's GO can't then pop this evicted cast's stale CastID.
    /// </summary>
    public bool RemoveForwardedStartCastId(uint spellId, WowGuid128 castId)
    {
        lock (_playerForwardedStartCastIdsLock)
        {
            if (_playerForwardedStartCastIds.TryGetValue(spellId, out var list) && list.Remove(castId))
            {
                if (list.Count == 0)
                    _playerForwardedStartCastIds.Remove(spellId);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Drop all forwarded-START CastIDs (reconnect / world-transfer state reset).
    /// </summary>
    public void ClearForwardedStartCastIds()
    {
        lock (_playerForwardedStartCastIdsLock)
        {
            _playerForwardedStartCastIds.Clear();
        }
    }

    /// <summary>
    /// Clear all pending normal casts (used on timeout or disconnect).
    /// </summary>
    public void ClearPendingNormalCasts()
    {
        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// Check if there's a normal cast that has already started (is in progress).
    /// Used to reject new casts without forwarding to server.
    /// </summary>
    public bool HasStartedNormalCast()
    {
        foreach (var item in PendingNormalCasts)
        {
            if (item.HasStarted)
                return true;
        }
        return false;
    }

    /// <summary>
    /// JimsProxy (issue #334): returns true if a started normal cast targets the
    /// given GameObject. Used to drop chain CMSG_CAST_SPELL on the same GO without
    /// holding-and-releasing it 1ms after SPELL_GO. Releasing a held same-GO cast
    /// preempts the legacy server's loot-creating script subspell (cast FROM the
    /// player at SPELL_GO + ~440ms — e.g. spell 15343 "Create Whipper Root
    /// Tubers"), and the loot is silently lost.
    /// Returns false if the argument is empty or not a GameObject GUID.
    /// </summary>
    public bool HasStartedCastOnGameObject(WowGuid128 gameObjectGuid)
    {
        if (gameObjectGuid.IsEmpty())
            return false;
        if (gameObjectGuid.GetHighType() != HighGuidType.GameObject)
            return false;
        foreach (var item in PendingNormalCasts)
        {
            if (item.HasStarted && item.TargetGuid == gameObjectGuid)
                return true;
        }
        return false;
    }

    /// <summary>
    /// JimsProxy: narrow variant of HasStartedNormalCast — returns true only when an
    /// in-progress cast is within the last SpellQueueWindowMs of its cast bar. Mirrors
    /// the 1.14 client's SpellQueueWindow=400 semantics: presses arriving in this window
    /// get queued and fire on cast completion; earlier presses are forwarded to the server
    /// and receive the server's actual response (SpellInProgress / NOT_READY etc.).
    /// Used by the HandleCastSpell cast-time hold gate. The wider HasStartedNormalCast()
    /// remains for callers that genuinely need "any started cast" (e.g. item-use duplicate
    /// guards).
    /// </summary>
    public bool HasStartedCastInQueueWindow()
    {
        long now = Environment.TickCount64;
        foreach (var item in PendingNormalCasts)
        {
            if (!item.HasStarted || item.StartedAtTickMs == 0 || item.StartedCastTimeMs == 0)
                continue;
            long castEnd = item.StartedAtTickMs + item.StartedCastTimeMs;
            long remaining = castEnd - now;
            if (remaining > 0 && remaining <= Framework.Settings.SpellQueueWindowMs)
                return true;
        }
        return false;
    }

    /// <summary>
    /// JimsProxy (PR #161 follow-up): walks PendingNormalCasts and PendingPetCasts,
    /// dequeues any entry whose WatchdogDeadlineMs has expired, and returns the
    /// evicted entries via the out parameters. Caller (GlobalSessionData
    /// .RunWatchdogEviction) emits the synthetic packets via InstanceSocket.
    /// Pure data operation — no socket dependency, easy to unit-test.
    /// </summary>
    /// <summary>
    /// JimsProxy (PR #161 follow-up — movement preemption): walks
    /// PendingNormalCasts and marks every HasStarted=true cast-time spell
    /// (StartedCastTimeMs>0) as MovementCancelled. Trailing SMSG_SPELL_FAILURE
    /// / SMSG_CAST_FAILED for these casts are suppressed (modern client
    /// already cancelled its own cast bar via client-side movement prediction).
    /// Also arms the watchdog so even if the legacy server never sends the
    /// trailing failure, the leak heals at the next cast event. Returns the
    /// number of casts marked, for diagnostics. Instants and not-yet-started
    /// casts are ignored (movement doesn't cancel them in vanilla).
    /// </summary>
    public int MarkStartedCastsMovementCancelled(long watchdogDeadlineMs)
    {
        int marked = 0;
        long nowTick = Environment.TickCount64;
        // DIAGNOSTIC (stuck-spell investigation): hoist toggle read; remove with diagnostics
        bool debugEvents = Framework.Settings.DebugOutput;
        foreach (var cast in PendingNormalCasts)
        {
            if (cast.HasStarted && cast.StartedCastTimeMs > 0
                && !GameData.IsChanneledSpell(cast.SpellId))
            {
                cast.MovementCancelled = true;
                cast.MarkedAtTickMs = nowTick;
                if (cast.WatchdogDeadlineMs == 0)
                    cast.WatchdogDeadlineMs = watchdogDeadlineMs;
                marked++;
                // DIAGNOSTIC (stuck-spell investigation): remove when closed
                if (debugEvents)
                    Log.Event("cast.movement_marked", new
                    {
                        spell_id = cast.SpellId,
                        started_cast_time_ms = cast.StartedCastTimeMs,
                        client_cast_id = cast.ClientGUID.ToString(),
                    });
            }
        }
        return marked;
    }

    /// <summary>
    /// JimsProxy (PR #161 follow-up — destroy-hook fast path): walks pending
    /// queues and dequeues any !HasStarted cast whose TargetGuid matches the
    /// destroyed unit. Returns evicted entries for the caller to emit synthetic
    /// CastFailed packets with a more accurate reason (BadTargets) than the
    /// watchdog's DontReport, since we know exactly why the cast can't proceed:
    /// the target was destroyed.
    ///
    /// Started casts (HasStarted=true) are intentionally LEFT in the queue.
    /// The legacy server owns them — it sends a real SMSG_SPELL_GO or
    /// SMSG_SPELL_FAILURE whose CastID/reason the proxy routes back to the
    /// client. Evicting a started cast here races that resolution and produces
    /// the wrong outcome:
    ///
    ///   - Channels (mining, herbalism, Drain Soul): a queued second tick gets
    ///     started by the server, then evicted when the depleted node despawns,
    ///     leaving a phantom channel — the node "did nothing" until the player
    ///     moves away and back. Synthetic SMSG_CAST_FAILED cannot tear down a
    ///     channel bar; only SMSG_SPELL_FAILURE can.
    ///   - Cast-time spells (Frostbolt etc.) mid-cast on a dying mob: the
    ///     server will send SMSG_SPELL_FAILURE → SMSG_CAST_FAILED. Evicting
    ///     here races those packets and corrupts queue alignment for later
    ///     same-spell presses (the burst-flood scenario seen in May 2026 logs).
    ///
    /// Trade: BadTargets feedback for combat-on-dying-mob is now driven by the
    /// server response (~RTT-bound) instead of the instant client-side synthesis.
    /// Worth it to eliminate the preemptive-removal-of-started-cast class of bugs.
    /// </summary>
    public void DrainPendingCastsForDestroyedTarget(WowGuid128 destroyedGuid,
        out List<ClientCastRequest> normalEvicted,
        out List<ClientCastRequest> petEvicted)
    {
        normalEvicted = new List<ClientCastRequest>();
        petEvicted = new List<ClientCastRequest>();
        if (destroyedGuid.IsEmpty())
            return;

        // DIAGNOSTIC (stuck-spell investigation): hoist toggle read; remove with diagnostics
        bool debugEvents = Framework.Settings.DebugOutput;

        var keepNormal = new List<ClientCastRequest>();
        while (PendingNormalCasts.TryDequeue(out var cast))
        {
            if (!cast.HasStarted && !cast.TargetGuid.IsEmpty() && cast.TargetGuid == destroyedGuid)
            {
                normalEvicted.Add(cast);
                // DIAGNOSTIC (stuck-spell investigation): remove when closed
                if (debugEvents)
                    Log.Event("cast.destroy_eviction", new
                    {
                        queue = "normal",
                        spell_id = cast.SpellId,
                        had_started = cast.HasStarted,
                        is_channeled = GameData.IsChanneledSpell(cast.SpellId),
                        destroyed_target_low = destroyedGuid.GetCounter(),
                        client_cast_id = cast.ClientGUID.ToString(),
                    });
            }
            else
                keepNormal.Add(cast);
        }
        foreach (var c in keepNormal)
            PendingNormalCasts.Enqueue(c);

        var keepPet = new List<ClientCastRequest>();
        while (PendingPetCasts.TryDequeue(out var cast))
        {
            if (!cast.HasStarted && !cast.TargetGuid.IsEmpty() && cast.TargetGuid == destroyedGuid)
            {
                petEvicted.Add(cast);
                // DIAGNOSTIC (stuck-spell investigation): remove when closed
                if (debugEvents)
                    Log.Event("cast.destroy_eviction", new
                    {
                        queue = "pet",
                        spell_id = cast.SpellId,
                        had_started = cast.HasStarted,
                        is_channeled = GameData.IsChanneledSpell(cast.SpellId),
                        destroyed_target_low = destroyedGuid.GetCounter(),
                        client_cast_id = cast.ClientGUID.ToString(),
                    });
            }
            else
                keepPet.Add(cast);
        }
        foreach (var c in keepPet)
            PendingPetCasts.Enqueue(c);
    }

    public void DrainExpiredWatchdogCasts(long nowMs,
        out List<ClientCastRequest> normalEvicted,
        out List<ClientCastRequest> petEvicted)
    {
        normalEvicted = new List<ClientCastRequest>();
        petEvicted = new List<ClientCastRequest>();

        var keepNormal = new List<ClientCastRequest>();
        while (PendingNormalCasts.TryDequeue(out var cast))
        {
            if (cast.WatchdogDeadlineMs > 0 && cast.WatchdogDeadlineMs < nowMs)
                normalEvicted.Add(cast);
            else
                keepNormal.Add(cast);
        }
        foreach (var c in keepNormal)
            PendingNormalCasts.Enqueue(c);

        var keepPet = new List<ClientCastRequest>();
        while (PendingPetCasts.TryDequeue(out var cast))
        {
            if (cast.WatchdogDeadlineMs > 0 && cast.WatchdogDeadlineMs < nowMs)
                petEvicted.Add(cast);
            else
                keepPet.Add(cast);
        }
        foreach (var c in keepPet)
            PendingPetCasts.Enqueue(c);
    }

    /// <summary>
    /// JimsProxy (Mount-Button-Stuck-Lit): returns true if any pending normal cast — started OR
    /// merely in flight to the legacy server — matches the given SpellId (or its LegacySpellId
    /// for SoM-renumbered USE_ITEMs).
    /// </summary>
    public bool HasInFlightNormalCastForSpell(uint spellId)
    {
        foreach (var item in PendingNormalCasts)
        {
            if (CastMatchesSpellId(item, spellId))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if any pending normal cast has been forwarded to the legacy server but
    /// hasn't received SMSG_SPELL_START yet. Covers the post-GCD-expiry window where
    /// IsGcdHoldActive() returns false but the server hasn't confirmed the forwarded cast.
    /// </summary>
    public bool HasForwardedPendingCast()
    {
        foreach (var item in PendingNormalCasts)
        {
            if (!item.HasStarted)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Store a cast in the held slot unconditionally (even if GCD has expired). Used by the
    /// HasForwardedPendingCast guard to hold casts during the post-GCD window while waiting
    /// for the server to respond. Returns any displaced cast.
    /// </summary>
    public ClientCastRequest? ForceHoldCast(ClientCastRequest cast)
    {
        lock (_gcdLock)
        {
            var displaced = _heldGcdCast;
            _heldGcdCast = cast;
            return displaced;
        }
    }

    /// <summary>
    /// Take the held cast if the GCD has expired and no forwarded cast is pending. Used by
    /// failure handlers to fire the held cast immediately when the server rejects a cast.
    /// Returns null if GCD is still active (timer will handle it) or if no cast is held.
    /// </summary>
    public ClientCastRequest? TakeHeldCastIfReady()
    {
        lock (_gcdLock)
        {
            if (_heldGcdCast == null)
                return null;
            if (_gcdExpireTimestampMs > Environment.TickCount64)
                return null;
            var cast = _heldGcdCast;
            _heldGcdCast = null;
            return cast;
        }
    }

    // ── RTT measurement and adaptive GCD offset ───────────────────────

    public void RecordPingSent(uint serial)
    {
        lock (_rttLock)
        {
            _lastPingSerial = serial;
            _lastPingSendTickMs = Environment.TickCount64;
        }
    }

    public void RecordPongReceived(uint serial)
    {
        lock (_rttLock)
        {
            if (serial != _lastPingSerial || _lastPingSendTickMs == 0) return;
            long rttMs = Environment.TickCount64 - _lastPingSendTickMs;
            _lastPingSendTickMs = 0;
            if (rttMs > 300)
            {
                Log.Event("rtt.sample.rejected", new { serial, raw_ms = rttMs, reason = "outlier_above_300ms" });
                return;
            }
            const double alpha = 0.2;
            _smoothedRttMs = _rttSampleCount == 0 ? rttMs : (_smoothedRttMs * (1 - alpha) + rttMs * alpha);
            _rttSampleCount++;
            Log.Event("rtt.sample", new { serial, raw_ms = rttMs, smoothed_ms = Math.Round(_smoothedRttMs, 1), samples = _rttSampleCount });
        }
    }

    public int GetAdaptiveFireOffsetMs()
    {
        lock (_rttLock)
        {
            if (_rttSampleCount < 3)
                return Framework.Settings.SpellCastEarlyFireOffsetMs;
            return (int)Math.Clamp(Math.Round(_smoothedRttMs - 10), 0, 100);
        }
    }

    public void ResetRttSmoothing()
    {
        lock (_rttLock)
        {
            _smoothedRttMs = 0;
            _rttSampleCount = 0;
            _lastPingSendTickMs = 0;
            _lastPingSerial = 0;
            Log.Event("rtt.smoothing.reset", new { });
        }
    }

    public double GetSmoothedRttMs()
    {
        lock (_rttLock)
        {
            return Math.Round(_smoothedRttMs, 1);
        }
    }

    /// <summary>
    /// Clear only pending normal casts that haven't started yet.
    /// Keeps started casts so SPELL_GO can dequeue them later.
    /// Also keeps off-GCD casts (Bloodrage, Sprint, Rapid Fire, racials): they coexist
    /// with a normal GCD cast and the server processes them independently, so a normal
    /// cast's SMSG_SPELL_START must not fail them — they resolve via their own
    /// SPELL_GO / CAST_FAILED. See ClientCastRequest.IsOffGcd for the stuck-lit rationale.
    /// Returns the cleared casts so they can be failed.
    /// </summary>
    public List<ClientCastRequest> ClearNonStartedNormalCasts()
    {
        var cleared = new List<ClientCastRequest>();
        var keep = new List<ClientCastRequest>();

        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out var current))
            {
                if (current.HasStarted || current.IsOffGcd)
                    keep.Add(current);
                else
                    cleared.Add(current);
            }

            // Re-enqueue started casts
            foreach (var item in keep)
            {
                PendingNormalCasts.Enqueue(item);
            }
        }

        return cleared;
    }

    /// <summary>
    /// Engineering-malfunction substitute spells mapped to the device whose forwarded item-use cast
    /// they preempt. When a device malfunctions the server replaces the device's cast with one of
    /// these substitutes and sends ITS CAST_FAILED (status != 2, discarded), so the device's item-use
    /// cast never gets a SPELL_START or its own failure and sits forwarded-unstarted forever —
    /// HasForwardedPendingCast() then jams every later press. Seeded with the only substitution
    /// confirmed in packet logs: Malfunction Explosion (13261) -> Goblin Mortar (13237). Goblin Rocket
    /// Boots (8892) and Sapper Charge (13241) are intentionally absent — they pair SPELL_START/GO
    /// normally and never orphan. Expand as new substitute -> device pairs are confirmed.
    /// </summary>
    private static readonly FrozenDictionary<uint, uint> MalfunctionSubstituteToDevice =
        new Dictionary<uint, uint>
        {
            [13261] = 13237, // Malfunction Explosion -> Goblin Mortar
        }.ToFrozenDictionary();

    /// <summary>
    /// Evict the forwarded-but-unstarted item-use cast that a server-side malfunction substitute
    /// preempted. Fires only when <paramref name="triggerSpellId"/> is a known malfunction substitute
    /// and only evicts that substitute's specific device cast (see <see cref="MalfunctionSubstituteToDevice"/>),
    /// so an unrelated status-0 CAST_FAILED can't evict a healthy in-flight item and the right victim
    /// is always picked. Clears the orphan that would otherwise jam HasForwardedPendingCast(); returns
    /// the evicted request so the caller can release its button state.
    /// </summary>
    public bool TryEvictForwardedItemUseCast(uint triggerSpellId, out ClientCastRequest? evicted)
    {
        evicted = null;

        if (!MalfunctionSubstituteToDevice.TryGetValue(triggerSpellId, out var deviceSpellId))
            return false;

        var keep = new List<ClientCastRequest>();
        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out var current))
            {
                if (evicted == null && !current.HasStarted && !current.ItemGUID.IsEmpty()
                    && current.SpellId == deviceSpellId)
                    evicted = current;
                else
                    keep.Add(current);
            }
            foreach (var item in keep)
                PendingNormalCasts.Enqueue(item);
        }
        return evicted != null;
    }

    /// <summary>
    /// Try to find and dequeue a pending pet cast by SpellId.
    /// </summary>
    public bool TryDequeuePendingPetCast(uint spellId, out ClientCastRequest? cast)
    {
        var pending = new List<ClientCastRequest>();
        cast = null;

        while (PendingPetCasts.TryDequeue(out var current))
        {
            if (cast == null && CastMatchesSpellId(current, spellId))
            {
                cast = current;
            }
            else
            {
                pending.Add(current);
            }
        }

        foreach (var item in pending)
        {
            PendingPetCasts.Enqueue(item);
        }

        return cast != null;
    }

    /// <summary>
    /// Try to find a pending pet cast by SpellId and mark it as started.
    /// </summary>
    public bool TryMarkPendingPetCastStarted(uint spellId, out ClientCastRequest? cast)
    {
        cast = null;

        foreach (var item in PendingPetCasts)
        {
            if (CastMatchesSpellId(item, spellId) && !item.HasStarted)
            {
                item.HasStarted = true;
                item.StartedAtTickMs = Environment.TickCount64;
                cast = item;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clear all pending pet casts.
    /// </summary>
    public void ClearPendingPetCasts()
    {
        while (PendingPetCasts.TryDequeue(out _)) { }
    }

    /// <summary>
    /// JimsProxy reconnect-state-cleanup: drop all in-flight cast bookkeeping
    /// after an unplanned disconnect+reconnect. The legacy server forgets
    /// everything we had in flight when it cuts the socket; if we don't drop
    /// our local mirror, the next press of any spell that was pending at DC
    /// time gets silently rejected by HasNonStartedPendingCastForSpell —
    /// user-visible symptom is "spell stuck, spamming key does nothing, no
    /// error message" (e.g. rogue's R-key Sinister Strike not firing). Same
    /// story for OtherCasterActiveCastIds (mob/other-player CastIDs minted
    /// pre-DC won't match anything the new server-side state knows about).
    /// Returns the count of entries cleared so the reconnect log can show
    /// whether the gap was actually significant.
    /// </summary>
    public (int normalCasts, int petCasts, int otherCasterIds) ResetInFlightCastState()
    {
        int normalCount;
        lock (PendingCastsLock)
        {
            normalCount = PendingNormalCasts.Count;
            while (PendingNormalCasts.TryDequeue(out _)) { }
        }
        int petCount = PendingPetCasts.Count;
        while (PendingPetCasts.TryDequeue(out _)) { }
        int otherCount = OtherCasterActiveCastIds.Count;
        OtherCasterActiveCastIds.Clear();
        PetAutoCastActiveCastIds.Clear();
        PlayerForwardedCastIds.Clear();
        ClearForwardedStartCastIds();
        // Single-slot trackers for melee + auto-repeat (Auto Shot, Shoot Wand)
        // — same lifecycle as PendingNormalCasts; if a tracker was set when
        // the DC fired, it never gets cleared by the SPELL_GO/CAST_FAILED
        // path that normally nulls it.
        CurrentClientNextMeleeCast = null;
        CurrentClientAutoRepeatCast = null;
        return (normalCount, petCount, otherCount);
    }

    /// <summary>
    /// Check if there's a pet cast that has already started (is in progress).
    /// Used to reject new casts without forwarding to server.
    /// </summary>
    public bool HasStartedPetCast()
    {
        foreach (var item in PendingPetCasts)
        {
            if (item.HasStarted)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Clear only pending pet casts that haven't started yet.
    /// Keeps started casts so SPELL_GO can dequeue them later.
    /// Returns the cleared casts so they can be failed.
    /// </summary>
    public List<ClientCastRequest> ClearNonStartedPetCasts()
    {
        var cleared = new List<ClientCastRequest>();
        var keep = new List<ClientCastRequest>();

        while (PendingPetCasts.TryDequeue(out var current))
        {
            if (current.HasStarted)
                keep.Add(current);
            else
                cleared.Add(current);
        }

        // Re-enqueue started casts
        foreach (var item in keep)
        {
            PendingPetCasts.Enqueue(item);
        }

        return cleared;
    }

    // JimsProxy (issue #43): GCD hold-and-fire helpers.

    /// <summary>
    /// Returns true if a GCD hold window is currently active (i.e. a subsequent cast should be held
    /// rather than forwarded). Uses Environment.TickCount64 as the timebase.
    /// </summary>
    public bool IsGcdHoldActive()
    {
        lock (_gcdLock)
        {
            return _gcdExpireTimestampMs > Environment.TickCount64;
        }
    }

    // JimsProxy (#379 form-exit): deadline (TickCount64) of the form-exit defer window, opened
    // when the local player cancels a shapeshift form aura (HandleCancelAura). The next local
    // SMSG_SPELL_START consumes it and is deferred so the form-removal UPDATE_OBJECT and the
    // model swap render before the cast visual starts. 0 = no window. Written on the WorldServer
    // dispatch thread, consumed on the WorldClient receive thread — accessed via Interlocked.
    private long _formExitWindowUntilMs;

    /// <summary>
    /// JimsProxy (#379 form-exit): arm the form-exit defer window for <paramref name="windowMs"/>.
    /// Sized to cover CANCEL_AURA→SPELL_START (one server round-trip + emit spacing), not tuned
    /// to the model swap — that's Settings.FormExitStartDeferMs.
    /// </summary>
    public void OpenFormExitWindow(long windowMs)
    {
        Interlocked.Exchange(ref _formExitWindowUntilMs, Environment.TickCount64 + windowMs);
    }

    /// <summary>
    /// JimsProxy (#379 form-exit): one-shot consume of the form-exit window. True if the window
    /// is still open (and closes it); false when expired or never opened.
    /// </summary>
    public bool TryConsumeFormExitWindow()
    {
        long until = Interlocked.Exchange(ref _formExitWindowUntilMs, 0);
        return until != 0 && Environment.TickCount64 <= until;
    }

    /// <summary>
    /// JimsProxy: narrow variant of IsGcdHoldActive — returns true only when the GCD has
    /// at most SpellQueueWindowMs remaining. Mirrors the 1.14 client's SpellQueueWindow
    /// semantics for the GCD case (instants pressed in the last 400 ms of the previous cast's
    /// GCD get queued and fire on GCD expiry; earlier presses are forwarded and receive the
    /// server's NOT_READY). Used by the HandleCastSpell GCD hold gate. The wider
    /// IsGcdHoldActive() remains for callers that need "is any GCD active at all"
    /// (e.g. the held-cast-on-failure release path in Client/SpellHandler.cs).
    /// </summary>
    public bool IsInGcdQueueWindow()
    {
        lock (_gcdLock)
        {
            long remaining = _gcdExpireTimestampMs - Environment.TickCount64;
            return remaining > 0 && remaining <= Framework.Settings.SpellQueueWindowMs;
        }
    }

    /// <summary>
    /// JimsProxy: ms remaining in the current GCD hold window, or 0 if no GCD is active.
    /// Used by diagnostics to show how deep into the GCD a held/displaced press landed —
    /// helps distinguish "user mashed early in GCD" from "user mashed in the natural
    /// retail spell-queue window (last ~400ms)" without changing behavior.
    /// </summary>
    public long GetGcdRemainingMs()
    {
        lock (_gcdLock)
        {
            long remaining = _gcdExpireTimestampMs - Environment.TickCount64;
            return remaining > 0 ? remaining : 0;
        }
    }

    /// <summary>
    /// Store <paramref name="cast"/> as the pending held cast for the current GCD window.
    /// Returns true if the GCD is still active (cast was stored). Returns false if the GCD
    /// already expired in the meantime (caller should forward immediately via the normal path).
    /// If a previously-held cast existed, it's returned via <paramref name="displaced"/> so
    /// the caller can decide how to handle it (today: silently drop).
    /// </summary>
    public bool TryHoldCastDuringGcd(ClientCastRequest cast, out ClientCastRequest? displaced)
    {
        displaced = null;
        lock (_gcdLock)
        {
            if (_gcdExpireTimestampMs <= Environment.TickCount64)
                return false;
            if (_gcdTimerHasFired)
                return false; // Timer already fired — no one to release this cast. Forward immediately.
            displaced = _heldGcdCast;
            _heldGcdCast = cast;
            return true;
        }
    }

    /// <summary>
    /// Returns true if the GCD timer already fired and the spell it forwarded matches
    /// the given spell ID. Used to silently drop same-spell late presses that would
    /// just get NOT_READY from the server.
    /// </summary>
    public bool ShouldDropLateSameSpell(uint spellId)
    {
        lock (_gcdLock)
        {
            return _gcdTimerHasFired &&
                   _lastFiredSpellId == spellId &&
                   _gcdExpireTimestampMs > Environment.TickCount64;
        }
    }

    /// <summary>
    /// Start (or restart) a GCD hold window. The timer fires at <paramref name="fireAtTickMs"/>,
    /// at which point any pending held cast is handed to OnGcdHeldCastFire on a ThreadPool thread.
    /// expireAtTickMs and fireAtTickMs are Environment.TickCount64 timestamps.
    /// </summary>
    public void BeginGcd(long expireAtTickMs, long fireAtTickMs)
    {
        lock (_gcdLock)
        {
            _gcdExpiryTimer?.Dispose();
            _gcdExpireTimestampMs = expireAtTickMs;
            _gcdTimerHasFired = false;
            unchecked { _gcdGeneration++; }
            uint myGeneration = _gcdGeneration;
            long delayMs = Math.Max(0, fireAtTickMs - Environment.TickCount64);
            // Timer.Dispose() does NOT wait for an already-queued callback, so a stale
            // callback from a prior GCD window can race against a freshly-installed timer.
            // We capture the generation counter into the callback's state arg and bail in
            // OnGcdTimerElapsed if the generation no longer matches _gcdGeneration.
            _gcdExpiryTimer = new Timer(OnGcdTimerElapsed, state: myGeneration, delayMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Cancel the active GCD hold window and drop any held cast. Used on session teardown
    /// or when the client cancels a cast while we're holding one for them. Returns the
    /// previously-held cast (if any) so callers on a live session can route it through
    /// SendCastRequestFailed to resolve the client's ClientGUID/ServerGUID tracking.
    /// OnDisconnect and HandleLogoutComplete ignore the return value since the session is
    /// going away.
    /// </summary>
    public ClientCastRequest? CancelGcdHold()
    {
        lock (_gcdLock)
        {
            ClientCastRequest? dropped = _heldGcdCast;
            _gcdExpiryTimer?.Dispose();
            _gcdExpiryTimer = null;
            _gcdExpireTimestampMs = 0;
            _gcdTimerHasFired = false;
            _heldGcdCast = null;
            // Bump generation so any already-queued callback from the cancelled timer sees a
            // stale generation and bails. Prevents post-cancel firing on session teardown.
            unchecked { _gcdGeneration++; }
            // Also null the fire delegate so a stale Invoke that escaped the lock (see the
            // TOCTOU window in OnGcdTimerElapsed between the generation check and the
            // post-lock Invoke) turns into a no-op instead of operating on a rotated GameState.
            // HandleCastSpell re-registers the delegate on the next cast via its null check.
            OnGcdHeldCastFire = null;
            return dropped;
        }
    }

    /// <summary>
    /// JimsProxy (taxi-flight-robustness): cancel any pending taxi-dismount Task and clear
    /// the in-flight bookkeeping. Idempotent — safe to call when no flight is active.
    /// Called on (a) clean session disconnect, (b) early-landing CMSG, (c) a fresh taxi
    /// spline arriving for the same player (multi-segment chained flights re-issue rather
    /// than queuing). Without cancellation the captured Task fires SendPacketToClient
    /// against a session that may have already been torn down or replaced.
    /// </summary>
    public void CancelTaxiDismount(string reason)
    {
        var cts = Interlocked.Exchange(ref TaxiDismountCts, null);
        var attemptId = TaxiAttemptId;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* CTS may already be disposed */ }
            cts.Dispose();
            Framework.Logging.Log.Event("taxi.flight.dismount_cancelled", new
            {
                attempt_id = attemptId,
                reason = reason,
            });
        }
        TaxiDismountFiresAtTickMs = 0;
        TaxiAttemptId = null;
    }

    /// <summary>
    /// Test-only: peek at the currently-held cast without consuming it. Returns null when none held.
    /// </summary>
    internal ClientCastRequest? PeekHeldGcdCast()
    {
        lock (_gcdLock)
        {
            return _heldGcdCast;
        }
    }

    private void OnGcdTimerElapsed(object? state)
    {
        ClientCastRequest? toFire;
        lock (_gcdLock)
        {
            // Reject stale fires: a queued callback from a prior BeginGcd can run after the
            // current generation has moved on. Clobbering _heldGcdCast / _gcdExpireTimestampMs
            // here would zero out state belonging to the new GCD window, silently disabling
            // the hold for the rest of it.
            if (state is not uint myGeneration || myGeneration != _gcdGeneration)
                return;

            toFire = _heldGcdCast;
            _heldGcdCast = null;
            _gcdTimerHasFired = true;
            _lastFiredSpellId = toFire?.SpellId ?? 0;
            // Keep _gcdExpireTimestampMs alive — but presses after timer fires should NOT be
            // held (no timer to release them). TryHoldCastDuringGcd checks _gcdTimerHasFired
            // and returns false so the caller forwards immediately instead of orphaning.
            // Don't null _gcdExpiryTimer here: a concurrent BeginGcd could have already replaced it.
        }
        if (toFire != null)
            OnGcdHeldCastFire?.Invoke(toFire);
    }

    /// <summary>
    /// Try to find and dequeue a pending cast by ItemGUID (for item use failures).
    /// Only matches casts that haven't started yet.
    /// </summary>
    public bool TryDequeueItemCast(WowGuid128 itemGuid, out ClientCastRequest? cast)
    {
        var pending = new List<ClientCastRequest>();
        cast = null;

        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out var current))
            {
                if (cast == null && !current.HasStarted && current.ItemGUID == itemGuid)
                {
                    cast = current;
                }
                else
                {
                    pending.Add(current);
                }
            }

            // Re-enqueue non-matching casts
            foreach (var item in pending)
            {
                PendingNormalCasts.Enqueue(item);
            }
        }

        return cast != null;
    }

    public void StorePlayerGuildId(WowGuid128 guid, uint guildId)
    {
        PlayerGuildIds[guid] = guildId;
    }
    public uint GetPlayerGuildId(WowGuid128 guid)
    {
        return PlayerGuildIds.TryGetValue(guid, out var value) ? value : 0u;
    }
    public uint[]? GetGemsForItem(WowGuid128 guid)
    {
        return ItemGems.TryGetValue(guid, out var gems) ? gems : null;
    }
    public void SaveGemsForItem(WowGuid128 guid, uint?[] gems)
    {
        ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(ItemGems, guid, out _);
        existing ??= new uint[ItemConst.MaxGemSockets];

        for (int i = 0; i < ItemConst.MaxGemSockets; i++)
        {
            if (gems[i] != null)
                existing[i] = (uint)gems[i]!;
        }
    }
    public WowGuid128 GetPetGuidByNumber(uint petNumber)
    {
        if (CachedPetNumbers.TryGetValue(petNumber, out var cached))
            return cached;

        lock (ObjectCacheLock)
        {
            foreach (var itr in ObjectCacheModern)
            {
                if (itr.Key.GetHighType() == HighGuidType.Pet &&
                    itr.Key.GetEntry() == petNumber)
                {
                    CachedPetNumbers[petNumber] = itr.Key;
                    return itr.Key;
                }
            }
            return default;
        }
    }
    public void StoreOriginalObjectType(WowGuid128 guid, ObjectType type)
    {
        OriginalObjectTypes[guid] = type;
    }
    public ObjectType GetOriginalObjectType(WowGuid128 guid)
    {
        return OriginalObjectTypes.TryGetValue(guid, out var type) ? type : guid.GetObjectType();
    }
    public void StoreRealSpell(uint realSpellId, uint learnSpellId)
    {
        RealSpellToLearnSpell[realSpellId] = learnSpellId;
    }
    public uint GetLearnSpellFromRealSpell(uint spellId)
    {
        return RealSpellToLearnSpell.TryGetValue(spellId, out var learnSpell) ? learnSpell : spellId;
    }
    public void StoreCreatureClass(uint entry, Class classId)
    {
        CreatureClasses[entry] = classId;
    }
    public void SetItemBuyCount(uint itemId, uint buyCount)
    {
        ItemBuyCount[itemId] = buyCount;
    }
    public uint GetItemBuyCount(uint itemId)
    {
        return ItemBuyCount.TryGetValue(itemId, out var count) ? count : 1u;
    }
    public void SetChannelId(string name, int id)
    {
        // If the name was previously mapped to a different id, evict the stale
        // reverse entry so ChannelNamesById can't accumulate dead ids.
        if (ChannelIds.TryGetValue(name, out var oldId) && oldId != id)
            ChannelNamesById.Remove(oldId);

        ChannelIds[name] = id;
        ChannelNamesById[id] = name;
    }
    public string GetChannelName(int id)
    {
        return ChannelNamesById.TryGetValue(id, out var name) ? name : "";
    }

    public string GetPlayerName(WowGuid128 guid)
    {
        if (CachedPlayers.TryGetValue(guid, out var cache) && cache.Name != null)
            return cache.Name;
        return "";
    }

    public WowGuid128 GetPlayerGuidByName(string name)
    {
        name = name.Trim().Replace("\0", "");
        foreach (var player in CachedPlayers)
        {
            if (player.Value.Name == name && !WowGuid128.IsUnknownPlayerGuid(player.Key))
                return player.Key;
        }
        return default;
    }

    public void UpdatePlayerCache(WowGuid128 guid, PlayerCache data)
    {
        if (data.Name != null)
            data.Name = data.Name.Trim().Replace("\0", "");

        if (CachedPlayers.TryGetValue(guid, out var existing))
        {
            if (!string.IsNullOrEmpty(data.Name))
                existing.Name = data.Name;
            if (data.RaceId != Race.None)
                existing.RaceId = data.RaceId;
            if (data.ClassId != Class.None)
                existing.ClassId = data.ClassId;
            if (data.SexId != Gender.None)
                existing.SexId = data.SexId;
            if (data.Level != 0)
                existing.Level = data.Level;
        }
        else
            CachedPlayers.Add(guid, data);
    }

    public Class GetUnitClass(WowGuid128 guid)
    {
        if (CachedPlayers.TryGetValue(guid, out var cache))
            return cache.ClassId;

        if (CreatureClasses.TryGetValue(guid.GetEntry(), out var classId))
            return classId;

        return Class.Warrior;
    }

    public int GetLegacyFieldValueInt32<T>(WowGuid128 guid, T field) where T : Enum
    {
        int fieldIndex = LegacyVersion.GetUpdateField(field);
        if (fieldIndex < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates != null && updates.TryGetValue(fieldIndex, out var value))
            return value.Int32Value;

        return 0;
    }

    public uint GetLegacyFieldValueUInt32<T>(WowGuid128 guid, T field) where T : Enum
    {
        int fieldIndex = LegacyVersion.GetUpdateField(field);
        if (fieldIndex < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates != null && updates.TryGetValue(fieldIndex, out var value))
            return value.UInt32Value;

        return 0;
    }

    public float GetLegacyFieldValueFloat<T>(WowGuid128 guid, T field) where T : Enum
    {
        int fieldIndex = LegacyVersion.GetUpdateField(field);
        if (fieldIndex < 0)
            return 0;

        var updates = GetCachedObjectFieldsLegacy(guid);
        if (updates != null && updates.TryGetValue(fieldIndex, out var value))
            return value.FloatValue;

        return 0;
    }

    // JimsProxy (issue #305): return player's skill rank for skillLine, 0 if absent; see memory.
    public ushort GetPlayerSkillRank(uint skillLine)
    {
        if (skillLine == 0) return 0;
        int baseField = LegacyVersion.GetUpdateField(PlayerField.PLAYER_SKILL_INFO_1_1);
        if (baseField < 0) return 0;
        var fields = GetCachedObjectFieldsLegacy(CurrentPlayerGuid);
        if (fields == null) return 0;
        for (int i = 0; i < 128; i++)
        {
            int idIdx = baseField + i * 3;
            if (!fields.TryGetValue(idIdx, out var idField)) continue;
            ushort id = (ushort)(idField.UInt32Value & 0xFFFF);
            if (id != skillLine) continue;
            if (!fields.TryGetValue(idIdx + 1, out var valField)) return 0;
            ushort rank = (ushort)(valField.UInt32Value & 0xFFFF);
            ushort perm = 0;
            if (fields.TryGetValue(idIdx + 2, out var bonusField))
                perm = (ushort)((bonusField.UInt32Value >> 16) & 0xFFFF);
            return (ushort)(rank + perm);
        }
        return 0;
    }

    // JimsProxy (issue #305): true if any skill slot is populated; guards pre-UpdateObject race; see memory.
    public bool HasPopulatedSkillBlock()
    {
        int baseField = LegacyVersion.GetUpdateField(PlayerField.PLAYER_SKILL_INFO_1_1);
        if (baseField < 0) return false;
        var fields = GetCachedObjectFieldsLegacy(CurrentPlayerGuid);
        if (fields == null) return false;
        for (int i = 0; i < 128; i++)
        {
            int idIdx = baseField + i * 3;
            if (fields.TryGetValue(idIdx, out var idField) && (idField.UInt32Value & 0xFFFF) != 0)
                return true;
        }
        return false;
    }

    public Dictionary<int, UpdateField>? GetCachedObjectFieldsLegacy(WowGuid128 guid)
    {
        lock (ObjectCacheLock)
        {
            ObjectCacheLegacy.TryGetValue(guid, out var dict);
            return dict;
        }
    }

    public UpdateFieldsArray? GetCachedObjectFieldsModern(WowGuid128 guid)
    {
        lock (ObjectCacheLock)
        {
            ObjectCacheModern.TryGetValue(guid, out var array);
            return array;
        }
    }
}

public class ClientCastRequest
{
    public bool HasStarted;
    public uint SpellId;
    public uint LegacySpellId; // 0 = same as SpellId; non-zero when modern client used a renumbered spell (e.g. SoM 1.14.1+ items)
    public uint SpellXSpellVisualId;
    public long Timestamp;
    public WowGuid128 ClientGUID;
    public WowGuid128 ServerGUID;
    public WowGuid128 ItemGUID;

    // JimsProxy (issue #43): when a cast is HELD during a GCD hold window, we keep the
    // fully-built CMSG_CAST_SPELL packet here so the timer callback can forward it
    // verbatim at GCD expiry. Null for casts that were forwarded immediately (normal path).
    public WorldPacket? HeldPacketForReplay;

    // JimsProxy: TickCount64 timestamp when this cast was placed into the GCD hold slot.
    // Diagnostic only — used by spell.held_fire to compute hold duration. 0 if never held.
    public long HeldAtTickMs;

    public bool HasSentPrepare;

    // JimsProxy: cast time (ms) reported by SMSG_SPELL_START. 0 means instant.
    // Distinguishes truly cast-time spells (Frostbolt, Polymorph) from instants that
    // *also* emit SMSG_SPELL_START on Kronos 1.12 (Arcane Explosion, Counterspell, etc.).
    // The GCD hold gate in HandleSpellGo uses this instead of HasStarted so Kronos-flavored
    // instants still trigger BeginGcd. See JimsProxy issue #43 follow-up.
    public uint StartedCastTimeMs;

    // JimsProxy (stuck-Bloodrage fix): true for off-GCD spells (Sprint, Evasion,
    // Bloodrage, Rapid Fire, racials). Off-GCD casts coexist with a normal GCD cast,
    // so when a normal cast's SMSG_SPELL_START arrives, ClearNonStartedNormalCasts
    // must NOT sweep these out of the queue. The server casts them independently and
    // their own SPELL_GO / CAST_FAILED resolves the button. Without this exemption the
    // off-GCD cast gets a premature CAST_FAILED while its real SPELL_GO still arrives
    // unmatched, leaving the action-bar highlight stuck-lit until relog.
    // NOTE: only spell casts (HandleCastSpell) set this; item-use casts take a
    // separate path and are NOT tagged off-GCD here — tracked in jimsproxy issue #345.
    public bool IsOffGcd;

    // JimsProxy: TickCount64 timestamp when SMSG_SPELL_START arrived for this cast.
    // Set in TryMarkPendingNormalCastStarted / TryMarkPendingPetCastStarted. Used together
    // with StartedCastTimeMs by HasStartedCastInQueueWindow to gate the cast-time hold
    // to the last SpellQueueWindowMs of the cast bar (1.14 SpellQueueWindow semantics). 0 means
    // SPELL_START has not yet arrived (entry is still !HasStarted).
    public long StartedAtTickMs;

    // JimsProxy (PR #161 follow-up): when HandleSpellFailure peeks this entry
    // (instead of dequeuing) so the trailing SMSG_CAST_FAILED can deliver the
    // real reason, set this to TickCount64 + 2500ms. If HandleCastFailed
    // doesn't dequeue within the window (Kronos can drop the trailing CAST_FAILED
    // on cast-time + target-dies), EvictExpiredWatchdogCasts force-dequeues
    // and emits a synthetic SpellPrepare + CastFailed(DontReport) to clear
    // button-lit state instead of leaking a HasStarted=true entry that
    // permanently blocks HasStartedNormalCast(). 0 = no watchdog active.
    public long WatchdogDeadlineMs;

    // JimsProxy (PR #161 follow-up — destroy-hook fast path): captured from
    // CastSpell.Cast.Target.Unit at enqueue time. When HandleDestroyObject
    // sees this GUID, the proxy can immediately evict any pending casts that
    // were aimed at it (target died / despawned) and emit a synthetic
    // CastFailed(BadTargets) — much faster than waiting up to 2.5s for the
    // watchdog. Empty/default GUID = self-cast or no unit target (e.g. AoE
    // ground-target spells), which the destroy hook ignores.
    public WowGuid128 TargetGuid;

    // JimsProxy (PR #161 follow-up — movement preemption): set true when the
    // proxy detects a CMSG_MOVE_START_* opcode while this cast-time spell is
    // in progress (HasStarted=true && StartedCastTimeMs>0). Vanilla cancels
    // any cast-time spell on movement, and the modern 1.14 client predicts
    // this client-side — so when this flag is set the trailing
    // SMSG_SPELL_FAILURE suppresses its broadcast (no misleading "in combat"
    // popup) and the trailing SMSG_CAST_FAILED forwards as DontReport so the
    // client gets its CMSG_CANCEL_CAST ack without a popup.
    public bool MovementCancelled;

    // DIAGNOSTIC (stuck-spell investigation): TickCount64 when MovementCancelled
    // was set. Used by cast.movement_resolved debug events to measure how long
    // between proxy-side mark and actual server resolution (CAST_FAILED /
    // SPELL_FAILURE / watchdog). 0 = never marked. Remove with diagnostics.
    public long MarkedAtTickMs;
}
public class ArenaTeamData
{
    public string Name = null!;
    public uint TeamSize;
    public uint WeekPlayed;
    public uint WeekWins;
    public uint SeasonPlayed;
    public uint SeasonWins;
    public uint Rating;
    public uint Rank;
    public uint BackgroundColor;
    public uint EmblemStyle;
    public uint EmblemColor;
    public uint BorderStyle;
    public uint BorderColor;
}
public class GlobalSessionData
{
    public BNetServer.Networking.AccountInfo AccountInfo = null!;
    public BNetServer.Networking.GameAccountInfo GameAccountInfo = null!;
    public string Username = null!;
    public string LoginTicket = null!;
    public byte[] SessionKey = null!;
    public string Locale = null!;
    public string OS = null!;
    public uint Build;
    public GameSessionData GameState;

    //MIRASU - GameState gets recreated on SMSG_LOGOUT_COMPLETE (CharacterHandler.HandleLogoutComplete)
    //MIRASU   which wipes QuestItemObjectiveProgress. To preserve quest item running totals across a
    //MIRASU   logout-to-charselect-relog flow, we snapshot the dict here keyed by character guid before
    //MIRASU   the reset, and restore lazily on first item pickup post-relog if the new CurrentPlayerGuid
    //MIRASU   matches. Char-switch (Char A → Char B) is handled naturally by the per-character key.
    //MIRASU - ConcurrentDictionary (outer + inner) because the saved snapshot is mutated from
    //MIRASU   both the modern-server thread (abandon-clear in Server/QuestHandler.cs) and the
    //MIRASU   WorldClient thread (snapshot/restore + COMPLETE-clear in Client/QuestHandler.cs).
    public ConcurrentDictionary<WowGuid128, ConcurrentDictionary<(uint QuestID, sbyte StorageIndex), uint>> SavedQuestItemProgressByCharacter = new();
    //MIRASU - track restore by GameSessionData *instance* (reference equality), not by playerGuid.
    //MIRASU   Logging out and back in to the SAME character produces a fresh GameSessionData with the
    //MIRASU   same CurrentPlayerGuid; a guid-based guard would skip the restore on relog and the
    //MIRASU   running totals would be lost. New GameState reference => restore runs once.
    //MIRASU   volatile gives us a memory barrier on read/write so a future caller off the WorldClient
    //MIRASU   thread can't see a stale reference.
    private volatile GameSessionData? _lastRestoredForGameState;

    private Timer? _questProgressDiskTimer;
    private volatile bool _questProgressDiskDirty;
    private const int QuestProgressDiskDebounceMs = 5_000;

    public RealmId RealmId;
    public RealmManager RealmManager = new();
    public Realm? Realm => RealmManager.GetRealm(RealmId);

    public AccountMetaDataManager AccountMetaDataMgr = null!;
    public AccountDataManager AccountDataMgr = null!;

    public WorldSocket RealmSocket = null!;
    public WorldSocket InstanceSocket = null!;
    public volatile WorldSocket? LingeringInstanceSocket;
    public AuthClient AuthClient = null!;
    public WorldClient? WorldClient;
    // JimsProxy: set true on SMSG_LOGOUT_COMPLETE so the next CMSG_PLAYER_LOGIN
    // tears down and recreates WorldClient. Twinstar accepts a second
    // CMSG_PLAYER_LOGIN on the same world TCP (the LOGIN_VERIFY_WORLD comes
    // back fine) but then closes the connection a few seconds into the new
    // character's session — leaving session.WorldClient null mid-game. We
    // can't drop the WorldClient at LOGOUT_COMPLETE itself because char-select
    // (CMSG_ENUM_CHARACTERS / CMSG_QUERY_PLAYER_NAME / etc.) is forwarded over
    // the same WorldClient and needs it alive until the user picks a char.
    // Cleared after the recreate succeeds.
    public volatile bool WorldClientNeedsRecreateOnNextLogin;
    public volatile bool IsInCharacterSelect;
    public SniffFile ModernSniff = null!;

    public Dictionary<string, WowGuid128> GuildsByName = [];
    public Dictionary<uint, List<string>> GuildRanks = [];

    // JimsProxy threat translation: per-session threat calculator. Vanilla 1.12
    // doesn't broadcast threat; this engine observes combat events and synthesizes
    // SMSG_THREAT_UPDATE so the modern client's native threat APIs populate.
    public ThreatTracker ThreatTracker = null!;

    // JimsProxy HealComm bridge: cross-version heal-prediction and resurrection
    // addon-comm translation between LibHealComm-4.0 (modern) and HealComm-1.0
    // (vanilla 1.12), so mixed-population raids see each other's predictions.
    public HealCommBridge HealCommBridge = null!;

    public GlobalSessionData()
    {
        GameState = GameSessionData.CreateNewGameSessionData(this);
        AuthClient = new AuthClient(this);
        ThreatTracker = new ThreatTracker(this);
        HealCommBridge = new HealCommBridge(this);
    }

    /// <summary>
    /// JimsProxy (PR #161 follow-up — destroy-hook fast path): when
    /// SMSG_DESTROY_OBJECT arrives for a unit, evict any not-yet-started
    /// pending casts aimed at it. Reason=BadTargets because we know exactly
    /// why the cast can't proceed (target was destroyed) and the modern
    /// client renders the correct popup ("Invalid target"). Faster than the
    /// 2.5s watchdog — fires within ~RTT of the destroy packet. Started casts
    /// are left for the server's real SPELL_GO/SPELL_FAILURE (see
    /// DrainPendingCastsForDestroyedTarget).
    /// </summary>
    public void EvictPendingCastsForDestroyedTarget(WowGuid128 destroyedGuid)
    {
        if (InstanceSocket == null) return;
        if (destroyedGuid.IsEmpty()) return;

        GameState.DrainPendingCastsForDestroyedTarget(destroyedGuid,
            out var normalEvicted,
            out var petEvicted);

        foreach (var cast in normalEvicted)
        {
            Log.Event("cast.destroy_evicted", new
            {
                queue = "normal",
                spell_id = cast.SpellId,
                client_cast_id = cast.ClientGUID.ToString(),
                target_low = cast.TargetGuid.GetCounter(),
                had_started = cast.HasStarted,
            });
            if (!cast.HasStarted)
            {
                SpellPrepare prepare = new();
                prepare.ClientCastID = cast.ClientGUID;
                prepare.ServerCastID = cast.ServerGUID;
                InstanceSocket.SendPacket(prepare);
            }
            CastFailed failed = new();
            failed.SpellID = cast.SpellId;
            failed.SpellXSpellVisualID = cast.SpellXSpellVisualId;
            failed.Reason = (byte)SpellCastResultClassic.BadTargets;
            failed.CastID = cast.ServerGUID;
            InstanceSocket.SendPacket(failed);
        }

        foreach (var cast in petEvicted)
        {
            Log.Event("cast.destroy_evicted", new
            {
                queue = "pet",
                spell_id = cast.SpellId,
                client_cast_id = cast.ClientGUID.ToString(),
                target_low = cast.TargetGuid.GetCounter(),
                had_started = cast.HasStarted,
            });
            PetCastFailed failed = new();
            failed.SpellID = cast.SpellId;
            failed.Reason = (uint)SpellCastResultClassic.BadTargets;
            failed.CastID = cast.ServerGUID;
            InstanceSocket.SendPacket(failed);
        }
    }

    /// <summary>
    /// JimsProxy (PR #161 follow-up): drain any expired watchdog peeks from the
    /// pending-cast queues and emit synthetic SpellPrepare + CastFailed(DontReport)
    /// (or PetCastFailed) for each so the modern client's button-lit / cast-bar
    /// state clears. Called from the top of every spell-event handler so a leak
    /// from "no trailing CAST_FAILED arrived" is at most one cast event old.
    /// Reason=DontReport because the trailing CAST_FAILED that would have carried
    /// the real reason never arrived — we don't know which to show.
    /// </summary>
    public void RunWatchdogEviction()
    {
        if (InstanceSocket == null)
            return;

        long nowMs = Environment.TickCount64;
        GameState.DrainExpiredWatchdogCasts(nowMs,
            out var normalEvicted,
            out var petEvicted);

        foreach (var cast in normalEvicted)
        {
            Log.Event("cast.watchdog_evicted", new
            {
                queue = "normal",
                spell_id = cast.SpellId,
                client_cast_id = cast.ClientGUID.ToString(),
                had_started = cast.HasStarted,
                ms_overdue = nowMs - cast.WatchdogDeadlineMs,
            });
            // T1 (identity-pinned): the started cast is being force-closed without a server
            // terminating event — release its forwarded-START CastID so a later same-spell cast
            // can't pop this evicted cast's stale CastID. Remove by value (it may not be the FIFO
            // head). The synthetic CastFailed below already carries cast.ServerGUID == that CastID.
            if (Framework.Settings.IdentityPinnedCastIdsActive && cast.HasStarted)
                GameState.RemoveForwardedStartCastId(cast.SpellId, cast.ServerGUID);
            if (!cast.HasStarted)
            {
                SpellPrepare prepare = new();
                prepare.ClientCastID = cast.ClientGUID;
                prepare.ServerCastID = cast.ServerGUID;
                InstanceSocket.SendPacket(prepare);
            }
            CastFailed failed = new();
            failed.SpellID = cast.SpellId;
            failed.SpellXSpellVisualID = cast.SpellXSpellVisualId;
            failed.Reason = (byte)SpellCastResultClassic.DontReport;
            failed.CastID = cast.ServerGUID;
            InstanceSocket.SendPacket(failed);

            // JimsProxy (transient-no-dismiss-started): under LowLatencyMode, HandleSpellFailure
            // deferred a started cast's caster-side visual-cancel to its trailing CAST_FAILED. If
            // that CAST_FAILED was dropped (e.g. Kronos target-dies-mid-cast) the cast lands here
            // instead — so cancel the casting-pose / channel visual too, not just dismiss the bar,
            // or the pose can linger until the next cast. Idempotent on the client.
            if (Framework.Settings.LowLatencyMode && cast.HasStarted)
            {
                uint resolvedVisual = GameData.GetSpellVisualIdFromXSpellVisual(cast.SpellXSpellVisualId);
                if (resolvedVisual != 0)
                {
                    CancelSpellVisual cancelVisual = new();
                    cancelVisual.Source = GameState.CurrentPlayerGuid;
                    cancelVisual.SpellVisualID = (int)resolvedVisual;
                    InstanceSocket.SendPacket(cancelVisual);
                }
            }
        }

        foreach (var cast in petEvicted)
        {
            Log.Event("cast.watchdog_evicted", new
            {
                queue = "pet",
                spell_id = cast.SpellId,
                client_cast_id = cast.ClientGUID.ToString(),
                had_started = cast.HasStarted,
                ms_overdue = nowMs - cast.WatchdogDeadlineMs,
            });
            PetCastFailed failed = new();
            failed.SpellID = cast.SpellId;
            failed.Reason = (uint)SpellCastResultClassic.DontReport;
            failed.CastID = cast.ServerGUID;
            InstanceSocket.SendPacket(failed);
        }
    }

    public void StoreGuildRankNames(uint guildId, List<string> ranks)
    {
        GuildRanks[guildId] = ranks;
    }
    public uint GetGuildRankIdByName(uint guildId, string name)
    {
        if (GuildRanks.TryGetValue(guildId, out var ranks))
        {
            for (int i = 0; i < ranks.Count; i++)
            {
                if (ranks[i] == name)
                    return (uint)i;
            }
        }
        return 0;
    }
    public string GetGuildRankNameById(uint guildId, byte rankId)
    {
        if (GuildRanks.TryGetValue(guildId, out var ranks))
            return ranks[rankId];

        return $"Rank {rankId}";
    }
    public void StoreGuildGuidAndName(WowGuid128 guid, string name)
    {
        GuildsByName[name] = guid;
    }
    public WowGuid128 GetGuildGuid(string name)
    {
        if (GuildsByName.TryGetValue(name, out var guid))
            return guid;

        guid = WowGuid128.Create(HighGuidType703.Guild, (ulong)(GuildsByName.Count + 1));
        GuildsByName.Add(name, guid);
        return guid;
    }

    public WowGuid128 GetGameAccountGuidForPlayer(WowGuid128 playerGuid)
    {
        if (GameState.OwnCharacters.Any(own => own.CharacterGuid == playerGuid))
            return WowGuid128.Create(HighGuidType703.WowAccount, GameAccountInfo.Id);
        else
            return WowGuid128.Create(HighGuidType703.WowAccount, playerGuid.GetCounter());
    }

    public WowGuid128 GetBnetAccountGuidForPlayer(WowGuid128 playerGuid)
    {
        if (GameState.OwnCharacters.Any(own => own.CharacterGuid == playerGuid))
            return WowGuid128.Create(HighGuidType703.BNetAccount, AccountInfo.Id);
        else
            return WowGuid128.Create(HighGuidType703.BNetAccount, playerGuid.GetCounter());
    }

    //MIRASU - capture the current player's QuestItemObjectiveProgress before GameState is wiped,
    //MIRASU   so we can restore it on re-login (logout-to-charselect-relog flow). Called from
    //MIRASU   HandleLogoutComplete BEFORE the GameState reassignment, while CurrentPlayerGuid is
    //MIRASU   still pointed at the outgoing character. Idempotent on default/empty guid (no-op).
    //MIRASU   In-memory snapshot is always immediate. Disk persistence is debounced (5s) so
    //MIRASU   rapid quest item pickups collapse into a single write. Logout/disconnect paths
    //MIRASU   call FlushQuestItemProgressToDisk() for an immediate flush.
    public void SnapshotQuestItemProgressForRestore()
    {
        var guid = GameState.CurrentPlayerGuid;
        if (guid == default)
            return;

        var live = GameState.QuestItemObjectiveProgress;
        //MIRASU - copy into a fresh ConcurrentDictionary so subsequent abandon-clears on the saved
        //MIRASU   inner dict are thread-safe. The seed copy from `live` is itself a CDict snapshot
        //MIRASU   (weakly-consistent enumeration, safe under concurrent writes).
        var snapshot = new ConcurrentDictionary<(uint QuestID, sbyte StorageIndex), uint>(live);
        SavedQuestItemProgressByCharacter[guid] = snapshot;

        _questProgressDiskDirty = true;
        _questProgressDiskTimer?.Change(QuestProgressDiskDebounceMs, Timeout.Infinite);
        if (_questProgressDiskTimer == null)
            _questProgressDiskTimer = new Timer(_ => DebouncedPersistQuestItemProgress(), null, QuestProgressDiskDebounceMs, Timeout.Infinite);

        Framework.Logging.Log.Event("quest.progress.snapshot", new
        {
            player_guid_low = guid.Low,
            player_guid_high = guid.High,
            entries = live.Count,
            persisted = false,
            debounced = true,
        });
    }

    private void DebouncedPersistQuestItemProgress()
    {
        if (!_questProgressDiskDirty)
            return;
        _questProgressDiskDirty = false;

        var guid = GameState?.CurrentPlayerGuid ?? default;
        if (guid == default)
            return;

        if (SavedQuestItemProgressByCharacter.TryGetValue(guid, out var snapshot))
            TryPersistQuestItemProgressToDisk(guid, snapshot);
    }

    public void FlushQuestItemProgressToDisk()
    {
        _questProgressDiskTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        if (!_questProgressDiskDirty)
            return;
        _questProgressDiskDirty = false;

        var guid = GameState?.CurrentPlayerGuid ?? default;
        if (guid == default)
            return;

        if (SavedQuestItemProgressByCharacter.TryGetValue(guid, out var snapshot))
        {
            bool persisted = TryPersistQuestItemProgressToDisk(guid, snapshot);
            Framework.Logging.Log.Event("quest.progress.flush", new
            {
                player_guid_low = guid.Low,
                player_guid_high = guid.High,
                entries = snapshot.Count,
                persisted,
            });
        }
    }

    //MIRASU - public entry point for the QuestHandler clear paths (COMPLETE/FAILED/abandon) so the
    //MIRASU   on-disk file stays consistent with the in-memory saved snapshot. Without this, a
    //MIRASU   crash between an abandon and the next graceful logout would leave stale entries on
    //MIRASU   disk that get restored on the next session and credited against a re-accept.
    //MIRASU   Uses the debounced disk path — abandon/COMPLETE aren't as latency-sensitive as
    //MIRASU   disconnect, and the next graceful logout or disconnect flushes immediately anyway.
    public void PersistQuestItemProgressForCurrentPlayer()
    {
        var guid = GameState.CurrentPlayerGuid;
        if (guid == default)
            return;
        if (!SavedQuestItemProgressByCharacter.TryGetValue(guid, out var saved))
            saved = new ConcurrentDictionary<(uint QuestID, sbyte StorageIndex), uint>();
        SavedQuestItemProgressByCharacter[guid] = saved;
        _questProgressDiskDirty = true;
        _questProgressDiskTimer?.Change(QuestProgressDiskDebounceMs, Timeout.Infinite);
        if (_questProgressDiskTimer == null)
            _questProgressDiskTimer = new Timer(_ => DebouncedPersistQuestItemProgress(), null, QuestProgressDiskDebounceMs, Timeout.Infinite);
    }

    //MIRASU - resolves realm + character name from OwnCharacters, then writes via AccountMetaDataMgr.
    //MIRASU   Returns false (with a log line) if any prerequisite is missing rather than throwing,
    //MIRASU   so a transient init-order issue doesn't tear down logout/disconnect cleanup.
    private bool TryPersistQuestItemProgressToDisk(WowGuid128 guid, ConcurrentDictionary<(uint QuestID, sbyte StorageIndex), uint> entries)
    {
        if (AccountMetaDataMgr == null)
            return false;
        var charInfo = GameState.OwnCharacters.FirstOrDefault(c => c.CharacterGuid == guid);
        if (charInfo == null || string.IsNullOrEmpty(charInfo.Name) || charInfo.Realm == null || string.IsNullOrEmpty(charInfo.Realm.Name))
            return false;

        try
        {
            AccountMetaDataMgr.SaveQuestItemProgress(charInfo.Realm.Name, charInfo.Name, entries);
            return true;
        }
        catch (Exception ex)
        {
            Framework.Logging.Log.Print(LogType.Error, $"Failed to persist quest item progress for '{charInfo.Name}@{charInfo.Realm.Name}': {ex.Message}");
            return false;
        }
    }

    //MIRASU - lazy-load disk-persisted progress into SavedQuestItemProgressByCharacter on the first
    //MIRASU   restore-attempt of a session. Returns true if anything was loaded. Stale entries for
    //MIRASU   quests no longer in the player's quest log get pruned by a subsequent abandon/COMPLETE
    //MIRASU   path -- here we just trust the disk file (it was last written by a previous logout or
    //MIRASU   a quest-clear, so it should already be consistent absent a crash mid-session).
    private bool TryLoadQuestItemProgressFromDisk(WowGuid128 guid)
    {
        if (AccountMetaDataMgr == null)
            return false;
        var charInfo = GameState.OwnCharacters.FirstOrDefault(c => c.CharacterGuid == guid);
        if (charInfo == null || string.IsNullOrEmpty(charInfo.Name) || charInfo.Realm == null || string.IsNullOrEmpty(charInfo.Realm.Name))
            return false;

        var loaded = AccountMetaDataMgr.LoadQuestItemProgress(charInfo.Realm.Name, charInfo.Name);
        if (loaded == null || loaded.Count == 0)
            return false;

        var inner = new ConcurrentDictionary<(uint QuestID, sbyte StorageIndex), uint>(loaded);
        SavedQuestItemProgressByCharacter[guid] = inner;

        Framework.Logging.Log.Event("quest.progress.disk.loaded", new
        {
            player_guid_low = guid.Low,
            player_guid_high = guid.High,
            char_name = charInfo.Name,
            realm = charInfo.Realm.Name,
            entries = loaded.Count,
        });
        return true;
    }

    //MIRASU - restore saved QuestItemObjectiveProgress entries for the current player on first call
    //MIRASU   per (player, GameState) combination. Called from ProcessQuestItemCredit at the top of
    //MIRASU   the live-pickup path so the very first item credit post-relog sees the running total
    //MIRASU   from before the logout. After restore the live dict is authoritative -- subsequent
    //MIRASU   abandon/COMPLETE clears (which already touch live + saved) keep both in sync.
    public void EnsureQuestItemProgressRestored()
    {
        var guid = GameState.CurrentPlayerGuid;
        //MIRASU - reference-equality on the GameSessionData instance is intentional: a relog
        //MIRASU   produces a fresh GameState reference even when the playerGuid is identical,
        //MIRASU   which is how we detect "first item credit of a new session" reliably.
        if (guid == default || ReferenceEquals(_lastRestoredForGameState, GameState))
            return;

        //MIRASU - verify disk-load preconditions BEFORE latching _lastRestoredForGameState. On a
        //MIRASU   cold proxy start, AccountMetaDataMgr/OwnCharacters/Realm can lag behind
        //MIRASU   CurrentPlayerGuid: the first item credit may arrive while charInfo is still
        //MIRASU   incomplete. If we latched the gate first and the disk load silently failed,
        //MIRASU   every subsequent credit in this GameState would skip restore and the toast
        //MIRASU   would render against stored=0 forever (post-restart "1/N" first-toast bug).
        //MIRASU   Returning without latching lets the next credit retry once preconditions are met.
        if (AccountMetaDataMgr == null)
        {
            Framework.Logging.Log.Event("quest.progress.restore.deferred", new
            {
                player_guid_low = guid.Low,
                player_guid_high = guid.High,
                reason = "account_meta_data_mgr_null",
            });
            return;
        }
        var charInfo = GameState.OwnCharacters.FirstOrDefault(c => c.CharacterGuid == guid);
        if (charInfo == null || string.IsNullOrEmpty(charInfo.Name) || charInfo.Realm == null || string.IsNullOrEmpty(charInfo.Realm.Name))
        {
            Framework.Logging.Log.Event("quest.progress.restore.deferred", new
            {
                player_guid_low = guid.Low,
                player_guid_high = guid.High,
                reason = charInfo == null ? "char_not_in_own_characters" : "char_info_incomplete",
                own_characters = GameState.OwnCharacters.Count,
                char_name_empty = charInfo == null || string.IsNullOrEmpty(charInfo.Name),
                realm_null = charInfo == null || charInfo.Realm == null,
                realm_name_empty = charInfo?.Realm == null || string.IsNullOrEmpty(charInfo.Realm.Name),
            });
            return;
        }

        _lastRestoredForGameState = GameState;
        //MIRASU - if no in-memory snapshot exists for this player yet (cold proxy start, or first
        //MIRASU   session for this character), try the on-disk file. Disk persistence is what makes
        //MIRASU   the toast survive a full proxy restart -- the in-memory dict is empty after restart.
        if (!SavedQuestItemProgressByCharacter.ContainsKey(guid))
            TryLoadQuestItemProgressFromDisk(guid);

        if (!SavedQuestItemProgressByCharacter.TryGetValue(guid, out var saved) || saved.Count == 0)
        {
            Framework.Logging.Log.Event("quest.progress.restore.empty", new
            {
                player_guid_low = guid.Low,
                player_guid_high = guid.High,
                saved_characters = SavedQuestItemProgressByCharacter.Count,
            });
            return;
        }

        var live = GameState.QuestItemObjectiveProgress;
        int restored = 0;
        foreach (var kvp in saved)
        {
            //MIRASU - don't clobber an entry the new GameState already saw (e.g. an SMSG_QUEST_UPDATE_ADD_ITEM
            //MIRASU   that arrived before this restore call would have populated it -- shouldn't happen because
            //MIRASU   restore runs at the top of ProcessQuestItemCredit, but defend anyway). TryAdd is the
            //MIRASU   atomic equivalent on ConcurrentDictionary.
            if (live.TryAdd(kvp.Key, kvp.Value))
                restored++;
        }

        Framework.Logging.Log.Event("quest.progress.restored", new
        {
            player_guid_low = guid.Low,
            player_guid_high = guid.High,
            saved_entries = saved.Count,
            restored_entries = restored,
        });
    }

    // JimsProxy (unplanned-dc-auto-reconnect): handle a UNPLANNED legacy-side
    // disconnect (server-initiated TCP RST or socket exception, NOT a realm swap)
    // by attempting one cached-session-key reconnect. If reconnect succeeds within
    // Settings.UnplannedReconnectTimeoutMs, the modern client never knows the gap
    // happened. If it fails or times out, close the modern InstanceSocket cleanly
    // so the user sees "Disconnected" within a second instead of being stuck in
    // a ghost world for tens of seconds (the prior suppress-only behavior).
    //
    // The reconnect uses the same realmd session key the original WorldClient
    // captured at connect time — Kronos/cmangos may or may not honor it depending
    // on their session policy. If they reject it, the auth handshake fails and
    // we fall through to clean DC.
    //
    // Heavy Log.Event coverage at every step so a JSONL bundle from a tester
    // shows exactly where the reconnect path succeeded or failed (no repro needed
    // to investigate). Bundle the reconnect attempt as a self-contained sequence
    // tagged with a unique attempt_id so multiple attempts in a session can be
    // correlated.
    // Per-session guard: ensures only one reconnect attempt runs at a time even if
    // HandleDisconnect and the ReceiveLoop catch fire simultaneously from the same TCP RST.
    // 0 = idle, 1 = attempt in flight. Compare-and-swapped at entry; reset in finally.
    private int _reconnectInProgress;

    // Intentional-logout/disconnect flag. Set when the player initiates logout
    // (CMSG_LOGOUT_REQUEST forwarded) or the modern client sends CMSG_LOG_DISCONNECT.
    // Cleared on SMSG_LOGOUT_CANCEL_ACK and CMSG_PLAYER_LOGIN (fresh session).
    // Cross-thread: RealmSocket thread writes (CMSG_LOG_DISCONNECT),
    // WorldClient ReceiveLoop thread reads (HandleDisconnect).
    // Accessed via Volatile.Write/Read for memory ordering.
    private int _logoutOrDisconnectIntentional;

    public void SetLogoutIntentional()
    {
        Volatile.Write(ref _logoutOrDisconnectIntentional, 1);
    }

    public void ClearLogoutIntentional()
    {
        Volatile.Write(ref _logoutOrDisconnectIntentional, 0);
    }

    public bool IsLogoutIntentional()
    {
        return Volatile.Read(ref _logoutOrDisconnectIntentional) != 0;
    }

    public void TryUnplannedReconnectAndPropagate(
        World.Client.WorldClient deadClient,
        string? originalExceptionType = null,
        string? originalExceptionMessage = null,
        int? originalSocketErrorCode = null)
    {
        var attemptId = Guid.NewGuid().ToString("N").Substring(0, 8);

        // Race guard FIRST — HandleDisconnect and ReceiveLoop's catch can both fire from
        // the same TCP RST on different threads. Only one wins the CAS; the loser exits
        // immediately without re-emitting `detected`, propagating, or queueing a Task.
        if (Interlocked.CompareExchange(ref _reconnectInProgress, 1, 0) != 0)
        {
            Framework.Logging.Log.Event("session.unplanned_reconnect.skipped_in_progress", new { attempt_id = attemptId });
            return;
        }

        bool taskQueued = false;
        try
        {
            var realm = RealmManager.GetRealm(RealmId);
            var playerGuid = GameState?.CurrentPlayerGuid ?? default;

            Framework.Logging.Log.Event("session.unplanned_dc.detected", new
            {
                attempt_id = attemptId,
                realm_name = realm?.Name,
                player_guid = playerGuid.ToString(),
                has_authclient = AuthClient != null,
                has_instance_socket = InstanceSocket != null,
                reconnect_enabled = Framework.Settings.EnableUnplannedReconnect,
                reconnect_timeout_ms = Framework.Settings.UnplannedReconnectTimeoutMs,
                // JimsProxy: forward the underlying disconnect cause so a JSONL bundle shows
                // *why* the legacy server cut us off, not just *that* it did. Helps spot
                // patterns (mid-session Warden kicks, repeated RSTs after specific opcodes).
                original_exception_type = originalExceptionType,
                original_exception_message = originalExceptionMessage,
                original_socket_error_code = originalSocketErrorCode,
            });

            if (!Framework.Settings.EnableUnplannedReconnect)
            {
                Framework.Logging.Log.Event("session.unplanned_reconnect.skipped_disabled", new { attempt_id = attemptId });
                PropagateUnplannedDcToModern(attemptId, "reconnect_disabled");
                return;
            }

            if (realm == null || playerGuid == default || AuthClient == null)
            {
                Framework.Logging.Log.Event("session.unplanned_reconnect.skipped_state", new
                {
                    attempt_id = attemptId,
                    has_realm = realm != null,
                    has_player_guid = playerGuid != default,
                    has_authclient = AuthClient != null,
                });
                PropagateUnplannedDcToModern(attemptId, "missing_state");
                return;
            }

            // Run the reconnect off the receive-loop thread so we don't block the catch.
            // Ownership of _reconnectInProgress transfers to the Task — its finally resets it.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                World.Client.WorldClient? newClient = null;
                bool reconnectSucceeded = false;
                try
                {
                    Framework.Logging.Log.Event("session.unplanned_reconnect.start", new
                    {
                        attempt_id = attemptId,
                        realm_name = realm!.Name,
                        realm_address = realm.ExternalAddress,
                        realm_port = (int)realm.Port,
                    });

                    newClient = new World.Client.WorldClient();

                    // Bound the connect+auth handshake by the configured timeout. ConnectToWorldServer
                    // blocks until _isSuccessful is set; if the legacy server's listener is dead, the
                    // OS-level TCP timeout (~21s on Windows) would otherwise stall us.
                    var connectTask = System.Threading.Tasks.Task.Run(() => newClient.ConnectToWorldServer(realm, this));
                    bool completed = connectTask.Wait(Framework.Settings.UnplannedReconnectTimeoutMs);
                    if (!completed)
                    {
                        Framework.Logging.Log.Event("session.unplanned_reconnect.timeout", new
                        {
                            attempt_id = attemptId,
                            elapsed_ms = sw.ElapsedMilliseconds,
                            timeout_ms = Framework.Settings.UnplannedReconnectTimeoutMs,
                        });
                        PropagateUnplannedDcToModern(attemptId, "timeout");
                        return;
                    }
                    bool authed = connectTask.Result;
                    Framework.Logging.Log.Event("session.unplanned_reconnect.connect_completed", new
                    {
                        attempt_id = attemptId,
                        elapsed_ms = sw.ElapsedMilliseconds,
                        authed = authed,
                    });
                    if (!authed)
                    {
                        PropagateUnplannedDcToModern(attemptId, "auth_failed");
                        return;
                    }

                    // JimsProxy reconnect-state-cleanup: drop in-flight cast bookkeeping that
                    // the legacy server forgot when it cut the socket. Without this, the next
                    // press of any spell that was pending at DC time gets silently rejected
                    // (HasNonStartedPendingCastForSpell), and the user sees "stuck spell, no
                    // error". Done BEFORE registering the new WorldClient so that any CMSG
                    // arriving in the tiny race window between WorldClient assignment and
                    // PLAYER_LOGIN-completion sees a clean slate. See ResetInFlightCastState
                    // doc for the full rationale.
                    var clearedCounts = GameState!.ResetInFlightCastState();
                    Framework.Logging.Log.Event("session.unplanned_reconnect.state_cleared", new
                    {
                        attempt_id = attemptId,
                        normal_casts_cleared = clearedCounts.normalCasts,
                        pet_casts_cleared = clearedCounts.petCasts,
                        other_caster_ids_cleared = clearedCounts.otherCasterIds,
                    });

                    // CRITICAL: register the new client with the session BEFORE sending CMSG_PLAYER_LOGIN.
                    // Modern→legacy CMSGs route via session.WorldClient (set null when the dead client
                    // was unregistered in WorldClient.HandleDisconnect/ReceiveLoop). Without this,
                    // any movement/cast/chat the player attempts after spawn-back is silently dropped,
                    // AND a subsequent unplanned DC won't recover (the new client wouldn't pass the
                    // `wasActiveWorldClient` check). Doing it before the login send also covers any
                    // CMSGs the modern client emits between login-sent and the server's spawn burst.
                    WorldClient = newClient;

                    // Re-issue CMSG_PLAYER_LOGIN with the cached character GUID so the legacy
                    // server places the character back in the world. The modern client's
                    // InstanceSocket stays open across this — the legacy server's ensuing
                    // SMSG_LOGIN_VERIFY_WORLD + spawn burst will be forwarded to the modern
                    // client, which may visibly flash a loading screen or briefly desync.
                    // That's acceptable vs the alternative (37s frozen world).
                    var loginPacket = new World.WorldPacket(World.Enums.Opcode.CMSG_PLAYER_LOGIN);
                    loginPacket.WriteGuid(playerGuid.To64());
                    newClient.SendPacketToServer(loginPacket);

                    Framework.Logging.Log.Event("session.unplanned_reconnect.player_login_sent", new
                    {
                        attempt_id = attemptId,
                        elapsed_ms = sw.ElapsedMilliseconds,
                        player_guid = playerGuid.ToString(),
                    });

                    Framework.Logging.Log.Event("session.unplanned_reconnect.success", new
                    {
                        attempt_id = attemptId,
                        elapsed_ms = sw.ElapsedMilliseconds,
                    });
                    reconnectSucceeded = true;
                }
                catch (Exception ex)
                {
                    Framework.Logging.Log.Event("session.unplanned_reconnect.exception", new
                    {
                        attempt_id = attemptId,
                        elapsed_ms = sw.ElapsedMilliseconds,
                        exception_type = ex.GetType().Name,
                        exception_message = ex.Message,
                    });
                    PropagateUnplannedDcToModern(attemptId, "exception");
                }
                finally
                {
                    // Release the in-progress flag so a future unplanned DC on this session
                    // can attempt another reconnect.
                    Volatile.Write(ref _reconnectInProgress, 0);

                    // Close the orphaned newClient's socket on any failure path so it doesn't
                    // linger in CLOSE_WAIT. On success the new client is the live one — leave it.
                    if (!reconnectSucceeded && newClient != null)
                    {
                        try
                        {
                            newClient.Disconnect();
                        }
                        catch (Exception ex)
                        {
                            Framework.Logging.Log.Event("session.unplanned_reconnect.cleanup_error", new
                            {
                                attempt_id = attemptId,
                                exception_type = ex.GetType().Name,
                                exception_message = ex.Message,
                            });
                        }
                    }
                }
            });
            taskQueued = true;
        }
        finally
        {
            // If we returned without queueing the Task (disabled / missing state / threw),
            // the Task's own finally never runs — release the flag here so future DCs aren't blocked.
            if (!taskQueued)
                Volatile.Write(ref _reconnectInProgress, 0);
        }
    }

    // Close the modern client's InstanceSocket so the user sees "Disconnected"
    // immediately rather than being stuck in a ghost world. Idempotent — safe to
    // call even if InstanceSocket has already been torn down.
    public void PropagateUnplannedDcToModern(string attemptId, string reason)
    {
        var instanceSock = InstanceSocket;
        var realmSock = RealmSocket;
        Framework.Logging.Log.Event("session.unplanned_dc.propagated", new
        {
            attempt_id = attemptId,
            reason = reason,
            had_instance_socket = instanceSock != null,
            had_realm_socket = realmSock != null,
        });

        if (instanceSock != null)
        {
            try { instanceSock.CloseSocket(); }
            catch (Exception ex)
            {
                Framework.Logging.Log.Event("session.unplanned_dc.close_error", new
                {
                    attempt_id = attemptId,
                    socket = "instance",
                    exception_type = ex.GetType().Name,
                    exception_message = ex.Message,
                });
            }
        }

        // RealmSocket handles CMSG_PING — without closing it the modern client
        // stays "connected" answering pings indefinitely after a legacy DC.
        if (realmSock != null)
        {
            try { realmSock.CloseSocket(); }
            catch (Exception ex)
            {
                Framework.Logging.Log.Event("session.unplanned_dc.close_error", new
                {
                    attempt_id = attemptId,
                    socket = "realm",
                    exception_type = ex.GetType().Name,
                    exception_message = ex.Message,
                });
            }
        }
    }

    public void OnDisconnect()
    {
        // JimsProxy: structured session.disconnect — emitted once per cleanup with snapshot
        Framework.Logging.Log.Event("session.disconnect", new
        {
            had_auth_client = AuthClient != null,
            had_world_client = WorldClient != null,
            had_realm_socket = RealmSocket != null,
            had_instance_socket = InstanceSocket != null,
            had_modern_sniff = ModernSniff != null,
            account_login = AccountInfo?.Login,
        });

        // JimsProxy (issue #43): cancel any held GCD cast and dispose its timer so it can't
        // fire after InstanceSocket has been torn down.
        GameState?.CancelGcdHold();

        // JimsProxy (taxi-flight-robustness): same reasoning for the taxi-dismount Task —
        // a pending flight Task captures session/InstanceSocket references and would NRE
        // (or worse, send packets to a recreated session post-reconnect) if it fired after
        // teardown.
        GameState?.CancelTaxiDismount("session_disconnect");

        //MIRASU - capture quest item running totals before GameState is recreated so an unexpected
        //MIRASU   network disconnect followed by reconnect-to-same-character preserves the toast
        //MIRASU   total, mirroring the graceful logout-to-charselect path. Without this, a Wi-Fi
        //MIRASU   blip resets the quest toast to "1/N" on the next pickup post-reconnect.
        if (GameState != null)
        {
            SnapshotQuestItemProgressForRestore();
            FlushQuestItemProgressToDisk();
        }

        if (ModernSniff != null)
        {
            ModernSniff.CloseFile();
            ModernSniff = null!;
        }
        if (AuthClient != null)
        {
            AuthClient.Disconnect();
            AuthClient = null!;
        }
        if (WorldClient != null)
        {
            WorldClient.Disconnect();
            WorldClient = null;
        }
        if (RealmSocket != null)
        {
            RealmSocket.CloseSocket();
            RealmSocket = null!;
        }
        if (InstanceSocket != null)
        {
            InstanceSocket.CloseSocket();
            InstanceSocket = null!;
        }

        GameState = GameSessionData.CreateNewGameSessionData(this, GameState);
        // Threat lists are tied to the previous character's mob/unit GUIDs;
        // wipe so the new login starts clean.
        ThreatTracker.Reset();
    }

    public void SendHermesTextMessage(string message, bool isError = false)
    {
        var socket = InstanceSocket;
        if (socket == null)
        {
            return;
        }

        var wholeMessage = new StringBuilder();
        wholeMessage.Append("|cFF111111[|r|cFF33DD22HermesProxy|r|cFF111111]|r ");
        if (isError)
            wholeMessage.Append("|cFFFF0000");
        wholeMessage.Append(message);

        var chatPkt = new ChatPkt(this, ChatMessageTypeModern.System, wholeMessage.ToString());
        socket.SendPacket(chatPkt);
    }
}

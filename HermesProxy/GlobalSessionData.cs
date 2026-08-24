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

// JimsProxy (empty-victim wedge): what an SMSG_ATTACK_STOP naming the local player did to the
// auto-attack handshake state. Returned by GameSessionData.ApplyLocalPlayerAttackStop so the
// socket layer only has to decide whether to forward a CMSG_ATTACK_STOP.
public enum PlayerAttackStopOutcome
{
    /// A CMSG_ATTACK_STOP was deferred behind an in-flight swing handshake — caller forwards it now.
    FlushDeferredStop = 0,
    /// Settled auto-attack stopped by the server (Gouge, Blind, Vanish, ...) — target cleared.
    ClearSettledTarget = 1,
    /// The server rejected our swing (matching victim, or an empty victim) — handshake cleared.
    ClearRejectedHandshake = 2,
    /// Stop for the OLD target mid target-switch — the newer swing's target is preserved.
    PreserveTargetSwitch = 3,
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
    // JimsProxy (Performance Mode / handshake expiry): Environment.TickCount64 of the last "JP"/"1"
    // affirmation from the addon (0 = never). The JimsPlus addon heartbeats the handshake every
    // ~30s; if we stop hearing it (addon disabled or crashed) the sideband expires, so we don't
    // keep streaming raw JP_ chat that the now-absent client-side filter can no longer suppress.
    public long JimsPlusSidebandAffirmedMs;
    public bool ChannelDisplayList;
    public bool ShowPlayedTime;
    public bool IsInFarSight;
    // JimsProxy (stuck-logout-stun): state for the artificial-logout-stun login fix; all four
    // reset at CMSG_PLAYER_LOGIN so every world login runs exactly one fresh detection. See
    // WorldClient.DetectStuckLogoutStunAtSelfCreate (UpdateHandler.cs) for the mechanism doc.
    public bool StuckStunLoginCheckDone;        // first-self-create gate: one detection per login
    public bool StuckStunDetectedThisLogin;     // enables raw CAST_RESULT logging + the server-clear breadcrumb
    public bool StuckStunCancelArmed;           // detection → end-of-UPDATE_OBJECT synth handoff
    public bool AwaitingSynthLogoutCancelAck;   // swallow the next SMSG_LOGOUT_CANCEL_ACK (client never asked)
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
    // JimsProxy (feared-while-sitting, issue #479): the local player's stand state
    // (UNIT_FIELD_BYTES_1 byte 0) as last written by the legacy server, read from the
    // legacy field cache. Gate input for the synthesized stand-up on incoming fear.
    public uint GetLocalPlayerStandState() =>
        GetLegacyFieldValueUInt32(CurrentPlayerGuid, UnitField.UNIT_FIELD_BYTES_1) & 0xFF;
    // JimsProxy (feared-while-sitting, issue #479): rising-edge tracker for the CC-onset
    // fallback — the Fleeing|Confused bits of the local player's UNIT_FIELD_FLAGS as of
    // the last FLAGS write. Reset at CMSG_PLAYER_LOGIN.
    public uint LastLocalFearConfuseFlags;
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
    // JimsProxy (#244 emote channel guard): the local player's active channel
    // window, tracked from MSG_CHANNEL_START / MSG_CHANNEL_UPDATE (vanilla
    // sends both only to the caster). Text-emote forwards are dropped while it
    // is open — vanilla's HandleTextEmoteOpcode interrupts channels and strips
    // auras flagged ANIM_CANCELS for every anim-bearing text emote (vmangos
    // ChatHandler.cpp), which is how /clap killed bandages. End-time bounded by
    // the start's own duration so a missed 0-update can never wedge the guard
    // permanently.
    public uint LocalChannelSpellId; // 0 means not channeling
    public long LocalChannelEndTickMs;

    /// <summary>True while the local player's channel window (tracked from
    /// MSG_CHANNEL_START/UPDATE) is open, with a small grace margin. Used to
    /// drop c2s text-emote forwards — mangos-family emote handlers interrupt
    /// channels and strip auras flagged ANIM_CANCELS (#244).</summary>
    public bool IsLocalChannelWindowOpen()
    {
        return LocalChannelSpellId != 0 &&
               Environment.TickCount64 < LocalChannelEndTickMs + 2000;
    }
    public string? TaxiAttemptId;
    public bool IsWaitingForNewWorld;
    public bool IsWaitingForWorldPortAck;
    // JimsProxy (worldentry stage-0 tripwire 2026-08-02): telemetry anchors for the
    // world-transfer loading-screen window (SMSG_TRANSFER_PENDING → SMSG_NEW_WORLD →
    // CMSG_WORLD_PORT_RESPONSE). Everything forwarded to the modern client inside
    // this window is suspected of being silently discarded for movers the client has
    // not created yet (documented precedent: the stuck-logout-stun create-baked root,
    // UpdateHandler.cs). The tripwire in WorldSocket.SendPacket records the window's
    // contents; these anchors give every line a phase-relative timestamp and a
    // per-transfer correlation id. Anchors are maintained unconditionally (two long
    // writes per transfer); all Log.Event emission is DebugOutput-gated. See
    // WORLD-ENTRY-CONTRACT-INVESTIGATION.md.
    public int WorldEntryWindowSeq;            // increments at each SMSG_TRANSFER_PENDING
    public long WorldEntryTransferPendingTick; // TickCount64 at transfer-pending; 0 = no window open
    public long WorldEntryNewWorldTick;        // TickCount64 at NEW_WORLD forward; 0 = not reached
    public int WorldEntryWindowForwardCount;   // packets sent to the modern client in-window (DebugOutput only)
    // JimsProxy (worldentry root-ceremony breadcrumb 2026-08-03): per-arrival
    // ROOT/UNROOT ceremony leg + ack counting (always-on unclosed breadcrumb).
    // See World/Client/WorldEntryCeremony.cs for the model and evidence.
    public readonly WorldEntryCeremonyTracker WorldEntryCeremony = new();
    // JimsProxy (carried-root cure 2026-08-03): the proxy's model of whether the
    // MODERN CLIENT currently believes it is rooted — set when a self root
    // (either family) is forwarded, cleared when any self unroot is forwarded
    // (live-verified: the client accepts either family as clearing). A /reload
    // clears the client without our knowledge; the resulting stale-true only ever
    // costs one harmless no-op synth unroot at the next arrival (fail-safe
    // direction).
    public bool ClientBelievesRooted;
    public bool WorldEntryPendingCarriedRootCheck;   // set at NEW_WORLD; consumed at the player's first destination update
    public bool WorldEntryCarriedRootCureArmed;      // dispatcher → end-of-UPDATE_OBJECT synth handoff (stuck-stun pattern)
    public bool WorldEntryCureAfterTeleportAck;      // same-map teleport variant: armed at the self MoveTeleport, fired at its CMSG_MOVE_TELEPORT_ACK
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
    // JimsProxy (camp login-eviction merge): hold-and-merge state for instanced-map
    // logins — see World/Client/LoginEvictionHold.cs. Lives on GameSessionData so a
    // hold can never survive a relogin (fresh instance per login), and is NOT
    // carried over by CarryOverRealmScopedCaches by design.
    public readonly LoginEvictionHold LoginEvictionHold = new();
    // JimsProxy (camp stun lock, step 2): pre-create self-op hold — see
    // World/Client/PreCreateOpHold.cs. Same lifetime rules as LoginEvictionHold.
    public readonly PreCreateOpHold PreCreateOpHold = new();
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
    // Pet names learned from party member stats, keyed by pet number — lets us answer
    // pet name queries for out-of-range party pets the legacy server returns empty for.
    public Dictionary<uint, string> PartyPetNames = new();
    // Last known pet stats / member level per party member guid. The legacy server only
    // re-sends fields when they change, but the modern client expires party data it
    // hasn't seen refreshed — these snapshots get re-attached to forwarded updates.
    public Dictionary<WowGuid128, World.Server.Packets.PartyMemberPetStats> PartyPetStats = new();
    public Dictionary<WowGuid128, ushort> PartyMemberLevels = new();
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

    // JimsProxy (out-of-range-ghost, #415): guids just destroyed / out-of-ranged and not yet
    // re-created. Vanilla broadcasts a moving unit's trailing MSG_MOVE_* / monster-moves at
    // map-level distance AFTER the per-object-visibility destroy; relaying that stray movement
    // re-ghosts the unit "running in place" on the modern client until re-approach. Movement for
    // these guids is dropped until a CreateObject clears the mark — suppression must NOT expire
    // on a timer: movement for a destroyed-and-not-recreated guid is never legitimate (the modern
    // client cannot render a unit before its create). The original 10s TTL leaked exactly there —
    // units dwelling in the destroyed-but-still-broadcasting annulus past 10s (lateral
    // boundary-skimming, patrol routes, trailing pets: 289 at-edge encounters across 67 field
    // sessions) had their first post-TTL packet relayed and re-ghosted. The age constant below is
    // pure dict hygiene for units that never return, far above any trailing-broadcast horizon —
    // it is not a relay permission.
    private readonly ConcurrentDictionary<WowGuid128, long> _recentlyDestroyedObjects = new();
    private const long RecentlyDestroyedSweepAgeMs = 600_000;
    private const int RecentlyDestroyedSweepThreshold = 4096;

    // JimsProxy (Performance Mode / handshake expiry): the sideband is live only while the addon
    // keeps re-affirming "1" (it heartbeats every ~30s). A disabled/crashed addon can't send "0",
    // so we time the handshake out — otherwise the proxy would keep emitting raw JP_ chat that the
    // now-absent addon filter no longer suppresses (the reported "JP_CS:Player-..." spam).
    public const long JimsPlusSidebandExpiryMs = 100_000;
    public bool IsJimsPlusSidebandActive()
        => JimsPlusSideband
           && (Environment.TickCount64 - JimsPlusSidebandAffirmedMs) <= JimsPlusSidebandExpiryMs;

    public void MarkObjectRecentlyDestroyed(WowGuid128 guid)
    {
        if (guid.IsEmpty())
            return;
        _recentlyDestroyedObjects[guid] = Environment.TickCount64;
        // Opportunistic hygiene sweep so a long session of spawns/despawns can't grow this
        // unbounded. Only long-stale entries go; recent marks are never evicted for size —
        // evicting a live mark would reopen the leak in exactly the crowded scenes that grow it.
        if (_recentlyDestroyedObjects.Count > RecentlyDestroyedSweepThreshold)
        {
            long cutoff = Environment.TickCount64 - RecentlyDestroyedSweepAgeMs;
            foreach (var kvp in _recentlyDestroyedObjects)
                if (kvp.Value < cutoff)
                    _recentlyDestroyedObjects.TryRemove(kvp.Key, out _);
        }
    }

    public void ClearRecentlyDestroyedObject(WowGuid128 guid) => _recentlyDestroyedObjects.TryRemove(guid, out _);

    // Test seams (RecentlyDestroyedSuppressionTests): inject a mark with a chosen timestamp so
    // age-dependent behavior is testable without wall-clock waits, and expose the entry count
    // for the hygiene-sweep assertions.
    internal void MarkObjectRecentlyDestroyedAtTick(WowGuid128 guid, long tickMs)
    {
        if (guid.IsEmpty())
            return;
        _recentlyDestroyedObjects[guid] = tickMs;
    }

    internal int RecentlyDestroyedCountForTest => _recentlyDestroyedObjects.Count;

    public bool WasObjectRecentlyDestroyed(WowGuid128 guid, out long agoMs)
    {
        agoMs = 0;
        if (_recentlyDestroyedObjects.TryGetValue(guid, out long when))
        {
            // Suppress for as long as the mark stands — only a CreateObject (re-approach)
            // clears it. agoMs feeds the drop diagnostics; values beyond the old 10s TTL are
            // the packets that used to leak and re-ghost (#415).
            agoMs = Environment.TickCount64 - when;
            return true;
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
    public long LastAttackSwingSentTick;         // TEMP-DIAG (#464 follow-up, REMOVE with the breadcrumb): TickCount64 when we last forwarded CMSG_ATTACK_SWING — feeds ms_since_swing
    public WowGuid128 PendingPreemptAttackStopVictim; // #450: preempt stop armed by SMSG_PARTY_KILL_LOG, flushed after the trailing killing-blow ASU or at socket drain
    public uint[] CurrentArenaTeamIds = new uint[3];
    public ConcurrentQueue<ClientCastRequest> PendingNormalCasts = new();  // regular spell casts (queue for proper FIFO handling)
    public ClientCastRequest? CurrentClientNextMeleeCast; // next melee spells (Raptor Strike, Heroic Strike, etc.)
    public ClientCastRequest? CurrentClientAutoRepeatCast; // auto repeat spells (Auto Shot, Shoot, etc.)
    public ConcurrentQueue<ClientCastRequest> PendingPetCasts = new();  // pet spell casts (queue for proper FIFO handling)
    // JimsProxy (issue #43): serializes EVERY mutation of PendingNormalCasts / PendingPetCasts.
    //
    // Both queues are drained and rebuilt by compound "dequeue-all, filter, re-enqueue-survivors"
    // helpers. ConcurrentQueue makes each individual Enqueue/TryDequeue atomic, but it cannot make
    // a whole drain-rebuild atomic: a concurrent Enqueue lands in the middle of one, and two
    // concurrent drain-rebuilds split the queue between their private survivor lists. Either way
    // FIFO order — which START/GO/CAST_FAILED matching depends on — is not preserved.
    //
    // These mutations genuinely run in parallel. CMSG handlers (HandleCastSpell, and the
    // RunWatchdogEviction it calls on every press) execute on the modern socket's IOCP receive
    // thread; SMSG handlers (SPELL_START/GO/CAST_FAILED) execute on WorldClient's ReceiveLoop
    // task; the GCD hold-release timer fires on a ThreadPool thread. Nothing serializes them.
    //
    // So: take this lock around any mutation, and prefer the EnqueuePending*Cast helpers below.
    // Read walks (HasStartedNormalCast, TryMarkPendingNormalCastStarted, ...) take the lock
    // too: a ConcurrentQueue enumeration IS a consistent snapshot, but a snapshot taken while
    // another thread holds this lock mid drain-rebuild (every entry dequeued into a private
    // survivor list, not yet re-enqueued) sees an EMPTY queue and false-misses a live entry —
    // a SPELL_START landing in that window skips its match, START and GO then ship different
    // CastIDs, and the client never closes the cast. The queue holds a handful of entries at
    // most; the lock cost is noise. (This supersedes the earlier "read walks may stay
    // lock-free" rule — that blessing was unsound during drain-rebuild windows.)
    internal readonly object PendingCastsLock = new();

    /// <summary>
    /// Enqueue a normal cast under <see cref="PendingCastsLock"/>. Always use this rather than
    /// touching <see cref="PendingNormalCasts"/> directly — a lock-free Enqueue racing a
    /// drain-rebuild reorders the FIFO the cast-correspondence machinery relies on.
    /// </summary>
    public void EnqueuePendingNormalCast(ClientCastRequest cast)
    {
        lock (PendingCastsLock)
            PendingNormalCasts.Enqueue(cast);
    }

    /// <summary>
    /// Enqueue a pet cast under <see cref="PendingCastsLock"/>. See
    /// <see cref="EnqueuePendingNormalCast"/>.
    /// </summary>
    public void EnqueuePendingPetCast(ClientCastRequest cast)
    {
        lock (PendingCastsLock)
            PendingPetCasts.Enqueue(cast);
    }

    // JimsProxy (#313): the spell-queue hold-window width is configurable via
    // Framework.Settings.SpellQueueWindowMs (default 400 retail-accurate / 1000 / 1300
    // smoothest). The hold gates (IsInGcdQueueWindow / HasStartedCastInQueueWindow) read it
    // directly: a press in the last SpellQueueWindowMs of an active GCD or cast bar is held and
    // fired at expiry; earlier presses are forwarded for the server to arbitrate. Exception:
    // under RTT Pre-Fire Timer the GCD gate ignores it and uses the fixed
    // Settings.RttPrefireTimerWindowMs (the launcher greys the queue controls under LL, so
    // their stored value must not govern Timer).

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
    public Action<ClientCastRequest>? OnAutoRepeatRetry; // set by WorldSocket; refires Shoot/Auto Shot after retryable legacy failures

    // JimsProxy (rtt-prefire, Knocker): SugarProxy-parity resend loop. The caller has already
    // enqueued and forwarded the press once (LL forward-everything unchanged); this re-sends the
    // SAME wire packet every 20ms, up to 10 knocks (Sugar's exact constants: time.Sleep(20ms),
    // N=10 flat), so the first knock after the server-side GCD expires lands ≤~20ms late with
    // zero GCD prediction — the server is the timing authority. Resends are wire-only: the FIFO
    // entry is NOT re-enqueued (Sugar re-Adds into a per-spell map, which is idempotent; our
    // FIFO is not). The loop self-terminates when the press starts/resolves (it leaves the
    // non-started set — our equivalent of Sugar's per-entry cancel flag set at START/GO/FAILED),
    // when ANY newer knock arms (global generation counter — Sugar's session-atomic semantics),
    // or when knocks run out. Early-knock NOT_READY / SpellInProgress bounces are swallowed by
    // HandleCastFailed's knock-chaff guard while IsKnockActiveForSpell is true; the final bounce
    // (arriving after the guard drops) resolves the press through the normal transient path.
    public const int KnockIntervalMs = 20;
    public const int KnockCount = 10;
    private long _knockGeneration;
    public readonly ConcurrentDictionary<uint, long> ActiveKnocks = new();

    public bool IsKnockActiveForSpell(uint spellId) => ActiveKnocks.ContainsKey(spellId);

    // onAbandoned: invoked (once, on the loop's ThreadPool task) when the loop exits with the
    // press still unstarted and enqueued — i.e. superseded by a newer press, or knocks exhausted
    // while every bounce was swallowed as chaff. Nothing else reliably pairs that entry: the
    // chaff guard ate its failures while the loop was live, and the final in-flight bounce can
    // land inside the guard-teardown window and be swallowed too. The caller resolves the entry
    // deterministically ON THIS EVENT (dequeue + DontReport ack) — never on a clock — otherwise
    // it blocks the spell via the LL duplicate guard until the 2.5s watchdog (alpha review,
    // knock-supersede leak).
    public void StartKnockLoop(uint spellId, Action resendToServer, Action<uint>? onAbandoned = null)
    {
        long myGen = Interlocked.Increment(ref _knockGeneration);
        ActiveKnocks[spellId] = myGen;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            int sent = 0;
            bool entryLive = true;
            try
            {
                for (int i = 0; i < KnockCount; i++)
                {
                    await System.Threading.Tasks.Task.Delay(KnockIntervalMs);
                    if (!HasNonStartedPendingCastForSpell(spellId))
                    {
                        entryLive = false;
                        break; // started or resolved — the cast landed (or terminally failed)
                    }
                    if (Interlocked.Read(ref _knockGeneration) != myGen)
                        break; // a newer press armed its own loop — it owns the GCD boundary now
                    resendToServer();
                    sent++;
                }
            }
            catch
            {
                // Session teardown mid-loop (socket gone) — the knock is moot; never surface an
                // unhandled exception on the ThreadPool (would crash the process).
            }
            finally
            {
                // Drop the chaff guard FIRST: from here on arriving bounces resolve the entry
                // through the normal transient paths; the abandonment re-check below fires only
                // if the entry is still there after that ordering.
                ActiveKnocks.TryRemove(new KeyValuePair<uint, long>(spellId, myGen));
                if (entryLive && HasNonStartedPendingCastForSpell(spellId))
                {
                    try { onAbandoned?.Invoke(spellId); }
                    catch
                    {
                        // Same teardown rationale as above — resolving on a dying session is moot.
                    }
                }
                if (Framework.Settings.DebugOutput)
                    Log.Event("cast.knock_loop_done", new { spell_id = spellId, knocks_sent = sent });
            }
        });
    }

    // JimsProxy (observed-bow retract): an observed (non-local) unit auto-repeating — Auto Shot 75 / wand Shoot 5019 — latches the 1.14 client's intrinsic ranged-aim the moment we forward its SPELL_START, and vanilla never broadcasts a stop for other units, so once the shooter quits nothing lowers the weapon (your own char is fine — SMSG_SPELL_FAILURE retracts the local aim). Hybrid retract: DETERMINISTIC stop edges — the target dies (PARTY_KILL_LOG / health->0), or the shooter itself dies, moves, or retargets — lower the bow instantly, AND a quiescence timer is the catch-all for the case no edge covers: the shooter stops with its target still alive and stands still (out of ammo, /stopattack, LoS, target dummy). Each latched shooter tracks its current target (for the death/retarget edges) and its last-shot time (for the sweep). The sweep fires SMSG_CANCEL_AUTO_REPEAT carrying THAT unit's GUID once it goes quiet past ObservedAutoRepeatQuietMs (the modern packet is per-GUID; the legacy handler only ever cancels the local player); the threshold sits above the slowest bow cadence + jitter so a steady shooter never flickers, and a premature retract self-heals — the next shot's START re-raises the aim.
    private readonly object _observedAutoRepeatLock = new();
    private readonly Dictionary<WowGuid128, ObservedShooterState> _observedShooterTargets = new();
    private Timer? _observedAutoRepeatSweepTimer;
    public Action<WowGuid128>? OnObservedAutoRepeatExpire; // set by WorldClient; invoked on a ThreadPool thread per quiesced shooter (also gates the timer — null in tests, so no real timer is armed)
    private const long ObservedAutoRepeatQuietMs = 4000;   // no shot for this long => series ended, lower the weapon (above slowest ~3.0s bow cadence + jitter so a steady shooter never flickers)
    private const long ObservedAutoRepeatSweepTickMs = 500;

    // JimsProxy (observed-bow retract): per-shooter latch state — the unit it's shooting (death/retarget edges match on it) and its last-shot time (the quiescence sweep retracts once this goes stale).
    private readonly struct ObservedShooterState
    {
        public readonly WowGuid128 Target;
        public readonly long LastShotMs;
        public ObservedShooterState(WowGuid128 target, long lastShotMs) { Target = target; LastShotMs = lastShotMs; }
    }

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
        lock (PendingCastsLock)
        {
            foreach (var item in PendingNormalCasts)
            {
                if (!item.HasStarted &&
                    (item.SpellId == spellId || (item.LegacySpellId != 0 && item.LegacySpellId == spellId)))
                    return true;
            }
            return false;
        }
    }

    // JimsProxy (fifo-terminator-symmetry + dup-failure frame hold): per-spell STARTED twin
    // of the check above — "is a same-spell cast currently between its forwarded SPELL_START
    // and its terminal event?" For the frame hold that in-flight window is the hold predicate:
    // a dup press's CAST_FAILED delivered during it can share a client frame with the cast's
    // SPELL_GO, and the client's kit-cancel sweep runs by (unit, visualID) — not CastID — so
    // the correctly-CastID'd dup failure can still tear the live cast's visual kit in the same
    // frame its GO is closing it (the #394 looping-sound collision, 2026-08-14 Stonetavern JSONL).
    public bool HasStartedPendingCastForSpell(uint spellId)
    {
        lock (PendingCastsLock)
        {
            foreach (var item in PendingNormalCasts)
            {
                if (item.HasStarted &&
                    (item.SpellId == spellId || (item.LegacySpellId != 0 && item.LegacySpellId == spellId)))
                    return true;
            }
            return false;
        }
    }

    // JimsProxy (dup-failure frame hold): a dup press's failure delivery, held while its
    // same-spell started cast is still in flight. Either a list of fully-built client packets
    // (SpellPrepare + CastFailed, the unsuppressed path) or the pending request to ack via
    // SendCastRequestFailed(DontReport) (the SuppressSpellCastErrors path). Released after the
    // started cast's terminal event forwards — SugarProxy's AddFailedPacket/GetFailedPacket
    // shape (hold by data dependency, never a clock), with a strictly wider release set: the
    // stale sweep ties held entries to the pending-cast lifecycle, so a silently-evicted cast
    // can't strand its dup's button-release past the next cast event.
    public sealed class HeldDupFailure
    {
        public uint SpellId;
        public List<ServerPacket> Packets = new();
        public ClientCastRequest? SuppressAck;
        public uint ReasonId;
        public long HeldAtMs;
    }

    // Lock order: _heldDupFailuresLock may be taken BEFORE PendingCastsLock (the stale sweep
    // checks anchors under it) — never call the held-dup methods while holding PendingCastsLock.
    private readonly Dictionary<uint, List<HeldDupFailure>> _heldDupFailures = new();
    private readonly object _heldDupFailuresLock = new();

    public void HoldDupFailure(HeldDupFailure held)
    {
        lock (_heldDupFailuresLock)
        {
            if (!_heldDupFailures.TryGetValue(held.SpellId, out var list))
            {
                list = new List<HeldDupFailure>();
                _heldDupFailures[held.SpellId] = list;
            }
            list.Add(held);
        }
    }

    public int HeldDupFailureCount
    {
        get { lock (_heldDupFailuresLock) { int n = 0; foreach (var l in _heldDupFailures.Values) n += l.Count; return n; } }
    }

    /// <summary>
    /// Remove and return every held dup failure for this spell, in hold (FIFO) order —
    /// null when none (keeps the per-GO hot path allocation-free). Called right after the
    /// started cast's terminal event (SPELL_GO or its real CAST_FAILED) forwards, so the
    /// release lands in the flush AFTER the terminal — Sugar's replay position, empirically
    /// safe in its field record.
    /// </summary>
    public List<HeldDupFailure>? TakeHeldDupFailures(uint spellId)
    {
        lock (_heldDupFailuresLock)
        {
            if (_heldDupFailures.Count != 0 && _heldDupFailures.Remove(spellId, out var list))
                return list;
            return null;
        }
    }

    /// <summary>
    /// Remove and return held dup failures whose anchor died: no started same-spell cast
    /// remains pending (evicted by the watchdog, parse-failure drain, destroy eviction, or a
    /// world transfer clear). The caller must still DELIVER these — a never-released dup
    /// failure strands the client's action button lit (the press is never answered).
    /// Self-healing: run on every local cast event, like RunWatchdogEviction.
    /// </summary>
    public List<HeldDupFailure>? TakeStaleHeldDupFailures()
    {
        List<HeldDupFailure>? stale = null;
        lock (_heldDupFailuresLock)
        {
            if (_heldDupFailures.Count == 0)
                return null;
            List<uint>? deadKeys = null;
            foreach (var key in _heldDupFailures.Keys)
            {
                if (!HasStartedPendingCastForSpell(key))
                    (deadKeys ??= new List<uint>()).Add(key);
            }
            if (deadKeys != null)
            {
                foreach (var key in deadKeys)
                {
                    if (_heldDupFailures.Remove(key, out var list))
                        (stale ??= new List<HeldDupFailure>()).AddRange(list);
                }
            }
        }
        return stale;
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
    // JimsProxy (#484 observed-castid-pairing): was a single slot per (caster, spell) — a
    // rapid same-spell recast overwrote the predecessor's CastID, so the predecessor's late
    // cancel broadcast (Kronos delivers it 0-554ms AFTER the successor's SPELL_START) popped
    // the SUCCESSOR's ID: the terminator built for the old cast was stamped with the new
    // cast's identity and killed the new bar at 0ms (field: 9 instances, heal-snipe /
    // chain-cast spam). Now a short FIFO per key, mirroring _playerForwardedStartCastIds.
    // The server runs at most ONE live cast per unit, so the list is [superseded
    // predecessor?, live cast]: a terminator pairs with the OLDEST (a predecessor's echo
    // always precedes any event of the successor's outcome), a GO pairs with the NEWEST
    // (only the live cast can complete). The predecessor's echo window provably closes at
    // the successor's GO (the echo lags the superseding START by less than one cast time),
    // so GO purges everything older — an entry cannot outlive one cast cycle and a stale
    // zombie can never eat a later cast's terminator.
    private readonly Dictionary<(WowGuid128 caster, uint spellId), List<WowGuid128>> _observedLiveCastIds = new();
    // JimsProxy (#485 killed-then-fired recovery): last CastID consumed by a terminator per
    // key, kept until the next same-key START or GO. Kronos broadcasts SPELL_FAILED_OTHER
    // for casts it then COMPLETES (non-terminal failures: 536/16.7k observed-player casts in
    // the 12-day corpus) — the terminator pops the tracked entry, so the following GO would
    // mint a fresh CastID the client never saw start. Recovering the terminated ID instead
    // lets START/terminator/GO tell one coherent story and makes the killed-then-fired
    // signature sweepable from always-on events (terminator castIdCounter == GO
    // castIdCounter).
    private readonly Dictionary<(WowGuid128 caster, uint spellId), WowGuid128> _observedTerminatedCastIds = new();
    private readonly object _observedCastIdsLock = new();

    /// <summary>
    /// Record the CastID minted at an observed (non-local, non-pet) caster's SPELL_START.
    /// Keeps at most the direct predecessor alongside the new live cast: anything older has
    /// had a full cast cycle for its echo to arrive and is dropped (see field notes above).
    /// A new START also invalidates any stashed terminated-ID recovery for the key.
    /// </summary>
    public void EnqueueObservedStartCastId(WowGuid128 caster, uint spellId, WowGuid128 castId)
    {
        var key = (caster, spellId);
        lock (_observedCastIdsLock)
        {
            _observedTerminatedCastIds.Remove(key);
            if (!_observedLiveCastIds.TryGetValue(key, out var list))
            {
                list = new List<WowGuid128>(2);
                _observedLiveCastIds[key] = list;
            }
            // Keep only the cast that was live until now (the direct predecessor).
            while (list.Count > 1)
                list.RemoveAt(0);
            list.Add(castId);
        }
    }

    /// <summary>
    /// Pair an observed caster's SPELL_GO with the NEWEST tracked CastID — the server runs
    /// one live cast per unit, so only the newest can complete; anything older is a
    /// superseded predecessor whose echo window this GO closes (purged here). Also clears
    /// the terminated-ID stash: a completed successor means any stashed predecessor ID is
    /// stale.
    /// </summary>
    public bool TryPairObservedGoCastId(WowGuid128 caster, uint spellId, out WowGuid128 castId)
    {
        var key = (caster, spellId);
        lock (_observedCastIdsLock)
        {
            if (_observedLiveCastIds.TryGetValue(key, out var list) && list.Count > 0)
            {
                castId = list[^1];
                _observedLiveCastIds.Remove(key);
                _observedTerminatedCastIds.Remove(key);
                return true;
            }
        }
        castId = default;
        return false;
    }

    /// <summary>
    /// Pair an observed caster's terminator (SPELL_FAILED_OTHER / SPELL_FAILURE) with the
    /// OLDEST tracked CastID: when a superseded predecessor is still tracked, its late echo
    /// is the first terminator to arrive, so the echo consumes the predecessor and the live
    /// cast keeps its identity. pairedLiveCast reports whether the consumed entry WAS the
    /// live (newest) cast — callers gate the client-visible interrupt synthesis on it so a
    /// predecessor's echo can no longer dismiss the on-screen bar (#484). The consumed ID is
    /// stashed for killed-then-fired GO recovery (#485).
    /// </summary>
    public bool TryPairObservedTerminatorCastId(WowGuid128 caster, uint spellId, out WowGuid128 castId, out bool pairedLiveCast)
    {
        var key = (caster, spellId);
        lock (_observedCastIdsLock)
        {
            if (_observedLiveCastIds.TryGetValue(key, out var list) && list.Count > 0)
            {
                castId = list[0];
                pairedLiveCast = list.Count == 1;
                list.RemoveAt(0);
                if (list.Count == 0)
                    _observedLiveCastIds.Remove(key);
                _observedTerminatedCastIds[key] = castId;
                return true;
            }
        }
        castId = default;
        pairedLiveCast = false;
        return false;
    }

    /// <summary>
    /// Recover the CastID a terminator consumed when the cast then completes anyway
    /// (killed-then-fired, #485): SPELL_GO with no live tracked entry re-uses the terminated
    /// cast's ID instead of minting one the client never saw start. Single-shot; invalidated
    /// by any same-key START or GO.
    /// </summary>
    public bool TryRecoverTerminatedObservedCastId(WowGuid128 caster, uint spellId, out WowGuid128 castId)
    {
        var key = (caster, spellId);
        lock (_observedCastIdsLock)
        {
            if (_observedTerminatedCastIds.TryGetValue(key, out castId))
            {
                _observedTerminatedCastIds.Remove(key);
                return true;
            }
        }
        castId = default;
        return false;
    }

    /// <summary>Whether an observed cast instance is tracked live for (caster, spell) — the dedup's live-terminator bypass (#471).</summary>
    public bool HasLiveObservedCast(WowGuid128 caster, uint spellId)
    {
        lock (_observedCastIdsLock)
        {
            return _observedLiveCastIds.ContainsKey((caster, spellId));
        }
    }

    private int ClearObservedCastIds()
    {
        lock (_observedCastIdsLock)
        {
            int count = _observedLiveCastIds.Count;
            _observedLiveCastIds.Clear();
            _observedTerminatedCastIds.Clear();
            return count;
        }
    }
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

    // JimsProxy (observed-pose strand, 2026-08-14): decide whether an incoming
    // SMSG_SPELL_FAILED_OTHER may be dropped by the retry-storm dedup. A failure whose
    // (caster, spell) has a live tracked cast instance (_observedLiveCastIds /
    // PetAutoCastActiveCastIds) is that instance's terminator — a skipped cancel
    // strands the cast-hold kit on the 1.14.2 client (observed player frozen in the
    // skinning "crafting hands" pose until despawn; same for mob casting poses).
    // The storm the dedup was built for (repeat failures with NO intervening
    // SPELL_START) still dedups: the first routed failure removes the tracked entry,
    // so storm repeats find no live instance.
    // Returns true = skip this failure. msSinceLastForwarded: -1 when outside the
    // window / first failure; >= 0 when within the window (callers log the
    // live-cast bypass case). Callers record forwarded failures in
    // RecentlyForwardedSpellFailedOther.
    public bool ShouldDedupSpellFailedOther(WowGuid128 caster, uint spellId, long nowMs, long dedupWindowMs, out long msSinceLastForwarded)
    {
        msSinceLastForwarded = -1;
        var key = (caster, spellId);
        if (!RecentlyForwardedSpellFailedOther.TryGetValue(key, out var lastMs) || nowMs - lastMs >= dedupWindowMs)
            return false;
        msSinceLastForwarded = nowMs - lastMs;
        if (HasLiveObservedCast(caster, spellId) || PetAutoCastActiveCastIds.ContainsKey(key))
            return false; // live cast instance: this failure is its terminator, never a duplicate
        return true;
    }

    // JimsProxy (cast-go-castid-recovery): per-spell FIFO of the client-facing CastIDs
    // forwarded to the modern client at the LOCAL PLAYER's SMSG_SPELL_START, keyed by spellId.
    // HandleSpellGo recalls the oldest when no PendingNormalCast / melee / auto-repeat entry
    // matches at SPELL_GO, so START and GO ship the SAME CastID. The 1.14 client pairs START↔GO
    // by CastID; a mismatch leaves the cast un-terminated → stuck casting animation + looping
    // cast sound. Covers server-initiated player casts with no CMSG (GO loot subspells e.g.
    // Whipper Root "Create Whipper Root Tubers" 15343, weapon/trinket procs) and casts whose
    // pending entry was consumed by an interleaved duplicate CAST_FAILED before the GO (Blade
    // Flurry, re-clicked gathers). A single spellId->CastID slot would be overwritten when two
    // same-spell casts are in flight at once (the immediate-forward / Low-Latency path), so a
    // later GO recovers the wrong CastID; the FIFO preserves START order so each terminating
    // event consumes the matching forwarded CastID and START↔GO/FAILED pair deterministically,
    // independent of which queue entry the dequeue heuristic picks. Fallback ONLY — never
    // consulted when a real pending cast is dequeued at GO, so normal casts are wire-identical.
    // Bounded per spell (oldest dropped past the cap) and cleared on reconnect / world transfer
    // so a missed pop can neither leak nor stale-head a future cast. Lock-guarded plain
    // Dictionary/List (not Concurrent*) so enqueue+bound and the remove-by-value the watchdog
    // needs are atomic; contention is negligible (cast events).
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
    // JimsProxy (res-sickness-swap-race): TickCount of the most recent server duration
    // PUSH per (unit, slot) — written ONLY by the SMSG_UPDATE_AURA_DURATION /
    // SMSG_SET_EXTRA_AURA_INFO handlers, never by emit-path stores (finisher snapshot,
    // expiry restore). Distinguishes "a server-authoritative duration for this slot just
    // raced ahead of its field update" from "stale duration left by the slot's previous
    // occupant", which the swap-wipe guard in the UpdateHandler aura loop cannot tell
    // apart by spell ID alone.
    public Dictionary<WowGuid128, Dictionary<byte, int>> UnitAuraDurationPushTime = [];
    // JimsProxy (temp-enchant-0s-after-relogin): remaining-time pushes from
    // SMSG_ITEM_ENCHANT_TIME_UPDATE, keyed item guid → (legacy enchantment slot →
    // seconds + receipt tick). At login vanilla cores send the push BEFORE the item's
    // create block (the only carrier of remaining time — the create's duration field is
    // zero), and the modern client discards updates for guids it has not constructed.
    // The push is stashed here and consumed into the item's create block when it is
    // translated. Not carried across sessions: each login gets fresh pushes.
    public Dictionary<WowGuid128, Dictionary<uint, (uint Seconds, int Tick)>> PendingItemEnchantDurations = [];
    public Dictionary<WowGuid128, Dictionary<byte, WowGuid128>> UnitAuraCaster = [];
    // Wall-clock aura expiry per (unit, spell). Unlike the per-slot caches above this
    // survives unit destroys AND relogs (carried over in CreateNewGameSessionData), so a
    // buff re-seen on a group member resumes its real remaining time instead of
    // restarting at full duration (PallyPower blessing timers after relog/stealth).
    public Dictionary<(WowGuid128, int), long> UnitAuraExpiryTick = [];

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

    // JimsProxy (Kronos Chronoboon): items whose tooltip is dynamic server-side — Kronos rewrites
    // the stored-world-buff list into the item's Description on each store/restore. The 1.14 client
    // caches item templates by id and won't re-render a cached one from any push, so instead we
    // alias the item to a throwaway entry id bumped on each change (the client re-fetches a never-
    // seen id clean). DynamicItemRefreshPending parks (real entry -> item GUID) between the
    // HandleUseItem re-query and its SMSG_ITEM_QUERY_SINGLE_RESPONSE. The guid->alias map itself lives in
    // GameData.ItemEntryAlias (STATIC, so it survives a relogin — this per-session dict used to hold it,
    // which wiped the alias on relog and reverted the item to its stale base-25007 cache).
    public Dictionary<uint, WowGuid128> DynamicItemRefreshPending = [];

    // JimsProxy (Kronos Chronoboon): alias Item + ItemEffect hotfix packets pre-built at mint on the WC
    // thread, drained + sent once by HandleDbQueryBulk (WS thread) in the client's ItemSparse query
    // window. Pre-building keeps HandleDbQueryBulk from MUTATING the shared record stores off the WS
    // thread (it would race the loot path's writes on the WC thread). ConcurrentDictionary: written WC,
    // read/removed WS.
    public ConcurrentDictionary<uint, List<ServerPacket>> AliasPendingPackets = new();

    // JimsProxy (Kronos Chronoboon): a Chronoboon use whose tooltip refresh must wait for its long
    // on-use cast to COMPLETE — the store only changes the item when the cast finishes, and
    // recreating the item mid-cast cancels it. Keyed by the on-use (legacy) spell id; the matching
    // SMSG_SPELL_GO (player caster) fires the deferred re-query. Value = (real entry, item GUID).
    // ConcurrentDictionary: written on the WS thread (HandleUseItem), read+removed on the WC thread
    // (HandleSpellGo). Normally separated by the ~10s cast, but a rapid re-use could overlap.
    public ConcurrentDictionary<uint, (uint Entry, WowGuid128 Guid)> ChronoboonCastAwaitingGo = new();

    // JimsProxy (Kronos Chronoboon): on-use spell ids of Chronoboon items the player has used this
    // session. The server's SMSG_COOLDOWN_EVENT for these is dropped (HandleCooldownEvent) so the
    // cooldown doesn't briefly paint on the OLD item before the destroy+recreate; it's re-asserted on
    // the recreated item instead.
    // ConcurrentDictionary (value unused): written WS (HandleUseItem), read WC (HandleCooldownEvent).
    public ConcurrentDictionary<uint, byte> ChronoboonOnUseSpells = new();

    // JimsProxy (Kronos Chronoboon): on-use cooldown END time (Environment.TickCount64 ms) keyed by the
    // on-use spell id. Captured from SMSG_SEND_KNOWN_SPELLS at login (the legacy server sends every active
    // cooldown there, banked items included) and refreshed on each use-path re-assert. Lets HandleShowBank
    // repaint the sweep on a BANKED Chronoboon: the client learns the spell cooldown at login but never
    // binds it to a bank item that only becomes visible when the bank opens. WC-thread only.
    public Dictionary<uint, long> ChronoboonOnUseCooldownEndMs = new();

    // JimsProxy (Kronos Chronoboon): item GUIDs presented as aliased Chronoboons this session, so
    // HandleShowBank can repaint their cooldown without scanning the global (all-players) alias map. WC only.
    public HashSet<WowGuid128> ChronoboonItemGuids = new();

    // JimsProxy (Kronos Chronoboon): boon GUIDs whose login-time tooltip refresh already ran this login,
    // so re-creates of the same item (same real entry) can't re-trigger an infinite refresh loop. WC only.
    public HashSet<WowGuid128> ChronoboonLoginRefreshFired = new();

    // JimsProxy (Kronos Chronoboon): the per-player boon template Kronos PUSHES unsolicited during login
    // (a solicited entry query answers with the BASE template — 2026-07-30 log — so this push is the only
    // correct login-time source). Latest push wins; per-login. WC only.
    // Known edge (latest-wins): a solicited BASE-template reply landing while ChronoboonLoginBoonGuids
    // are parked would re-mint from the base (empty tooltip). Nothing solicits the real entry in the
    // normal login flow (the client only ever sees alias ids) and a wrong mint self-heals on the next
    // use/relog — accepted.
    public ItemTemplate? ChronoboonLoginPushTemplate = null;

    // JimsProxy (Kronos Chronoboon): boon GUIDs created before the login push arrived, awaiting refresh
    // when it does (ordering fallback — observed order is push first, creates after). WC only.
    public HashSet<WowGuid128> ChronoboonLoginBoonGuids = new();

    // JimsProxy (Kronos Chronoboon): SEND_KNOWN_SPELLS item cooldowns keyed by ITEM entry — store and
    // restore are different spells, so a spell-keyed lookup misses when the boon's current on-use isn't
    // the spell that's cooling down. WC only.
    public Dictionary<uint, (uint SpellId, long EndMs)> LoginItemCooldownByItemEntry = new();

    // JimsProxy (Kronos Chronoboon): boon GUID whose remaining-cooldown repaint must go out AFTER the
    // current update packet (carrying its aliased create) is forwarded. WC only.
    public WowGuid128? ChronoboonRepaintPendingGuid = null;

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

    // MIRASU (mc-rune-dousing): observed life state of the 7 MC rune bosses (entry -> alive). Kronos sends
    // identical GO data for a rune before and after its boss dies (flags raw 48, circle GO present, state 1),
    // so the only usable boss-dead signal is the boss NPC's own health. Never-seen bosses are absent here and
    // treated as dead (rune targetable) — matching what a 1.12 client could do. WC-thread only.
    public Dictionary<uint, bool> McBossAlive = new();

    // MIRASU (mc-rune-dousing): last-seen guid + legacy GAMEOBJECT_FLAGS per MC rune entry, so a boss life-state
    // flip can resynthesize the rune's targetability without waiting for the server to resend rune fields. WC only.
    public Dictionary<uint, (WowGuid128 Guid, uint LegacyFlags)> McRunesSeen = new();

    // MIRASU (mc-rune-dousing): last-seen guid per MC flame-circle entry (178187-178193). Kronos respawns the
    // circle around already-doused runes on reload; the proxy destroys those client-side (paired rune has InUse). WC only.
    public Dictionary<uint, WowGuid128> McCirclesSeen = new();

// JimsProxy (#382 PetInCombat charm strip): players currently charmed by ANOTHER PLAYER,
    // tracked from ALL perspectives (no self/charmer exclusion — the victim's own client
    // drops too, and the charm+0x800 hybrid is wrong from every viewpoint). Maintained in
    // the CHARMEDBY translation; cleared when CHARMEDBY empties or on a create block without
    // CHARMEDBY (out-of-range charm end). While a guid is in here, UNIT_FIELD_FLAGS's
    // UNIT_FLAG_PET_IN_COMBAT is stripped from its forwarded flags (Charm382StripPetInCombat).
    public HashSet<WowGuid128> PlayerCharmedByPlayer = [];

    // JimsProxy (#382): last RAW vanilla UNIT_FIELD_FLAGS seen per PLAYER guid — always the
    // server's value, never the stripped presentation. Feeds the charm-edge flags re-sync:
    // a pet-class player can enter a charm already carrying a legitimate pet-owner 0x800
    // while the charm apply block carries no flags write at all, so the re-sync needs the
    // pre-charm value to strip from (and to restore from at charm end).
    public Dictionary<WowGuid128, uint> LastKnownPlayerUnitFlags = new();

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
            CarryOverRealmScopedCaches(previous, self);
        return self;
    }

    private static void CarryOverRealmScopedCaches(GameSessionData previous, GameSessionData self)
    {
        self.CachedPlayers = previous.CachedPlayers;
        self.PlayerGuildIds = previous.PlayerGuildIds;
        self.IgnoredPlayers = previous.IgnoredPlayers;
        // Buff expiries are wall-clock facts about other units — a /camp doesn't
        // change when the rogue's blessing runs out.
        self.UnitAuraExpiryTick = previous.UnitAuraExpiryTick;
    }

    /// <summary>
    /// Test-only factory — skips CurrentPlayerStorage initialization so tests that only need
    /// the GCD hold state machine (issue #43) can construct a bare GameSessionData without
    /// standing up a full GlobalSessionData graph.
    /// </summary>
    internal static GameSessionData CreateForTesting(GameSessionData? previous = null)
    {
        var self = new GameSessionData();
        if (previous != null)
            CarryOverRealmScopedCaches(previous, self);
        return self;
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
    // JimsProxy (res-sickness-swap-race): vanilla cores send SMSG_UPDATE_AURA_DURATION
    // immediately at aura apply, while the field update installing the aura is batched
    // to the end of the server tick. On a direct slot swap (Ghost → Resurrection
    // Sickness at a spirit-healer res: same tick, no empty pass) the new occupant's
    // duration therefore lands a few ms BEFORE the swap. Recording the push time lets
    // the swap-wipe guard keep that fresh value instead of discarding it as the previous
    // occupant's leftover.
    public const int AuraDurationPushFreshnessMs = 1000;
    public void StoreAuraDurationPushTime(WowGuid128 guid, byte slot, int currentTime)
    {
        ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(UnitAuraDurationPushTime, guid, out _);
        dict ??= [];
        dict[slot] = currentTime;
    }
    public bool HasFreshAuraDurationPush(WowGuid128 guid, byte slot, int currentTime)
    {
        if (UnitAuraDurationPushTime.TryGetValue(guid, out var dict) &&
            dict.TryGetValue(slot, out var pushedAt))
        {
            int age = unchecked(currentTime - pushedAt);
            return age >= 0 && age <= AuraDurationPushFreshnessMs;
        }
        return false;
    }
    public void ClearAuraDuration(WowGuid128 guid, byte slot)
    {
        if (UnitAuraDurationUpdateTime.TryGetValue(guid, out var timeDict))
            timeDict.Remove(slot);

        if (UnitAuraDurationLeft.TryGetValue(guid, out var leftDict))
            leftDict.Remove(slot);

        if (UnitAuraDurationFull.TryGetValue(guid, out var fullDict))
            fullDict.Remove(slot);

        if (UnitAuraDurationPushTime.TryGetValue(guid, out var pushDict))
            pushDict.Remove(slot);
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
    public void StorePendingItemEnchantDuration(WowGuid128 itemGuid, uint legacySlot, uint durationSeconds, int nowTick)
    {
        if (durationSeconds == 0)
            return;

        ref var slots = ref CollectionsMarshal.GetValueRefOrAddDefault(PendingItemEnchantDurations, itemGuid, out _);
        slots ??= [];
        slots[legacySlot] = (durationSeconds, nowTick);
    }
    public List<(uint LegacySlot, uint DurationMs)>? ConsumePendingItemEnchantDurations(WowGuid128 itemGuid, int nowTick)
    {
        if (!PendingItemEnchantDurations.Remove(itemGuid, out var slots) || slots.Count == 0)
            return null;

        List<(uint LegacySlot, uint DurationMs)> result = new(slots.Count);
        foreach (var (slot, push) in slots)
        {
            // TickCount skew safety — a receipt tick "in the future" decays nothing.
            int elapsed = unchecked(nowTick - push.Tick);
            if (elapsed < 0)
                elapsed = 0;

            long remainingMs = (long)push.Seconds * 1000 - elapsed;
            if (remainingMs > 0)
                result.Add((slot, (uint)remainingMs));
        }
        return result.Count > 0 ? result : null;
    }
    // Inject only where the create shows a live enchant whose duration field the
    // server left empty (vanilla's login shape); a server-provided duration wins.
    public static bool ShouldInjectEnchantDuration(HermesProxy.World.Objects.ItemEnchantment? enchantment)
    {
        return enchantment is { ID: > 0 } && (enchantment.Duration == null || enchantment.Duration == 0);
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
        UnitAuraDurationPushTime.Remove(guid);
        UnitAuraCaster.Remove(guid);
        UnitAuraLastEmitted.Remove(guid);
        // UnitAuraExpiryTick deliberately survives — it restores remaining buff time
        // when the unit re-enters view.
        return evicted;
    }

    public void RecordAuraExpiry(WowGuid128 guid, int spellId, int remainingMs)
    {
        if (remainingMs <= 0)
            return;
        if (UnitAuraExpiryTick.Count > 4096)
        {
            long now = Environment.TickCount64;
            List<(WowGuid128, int)> expired = [];
            foreach (var kvp in UnitAuraExpiryTick)
                if (kvp.Value <= now)
                    expired.Add(kvp.Key);
            foreach (var key in expired)
                UnitAuraExpiryTick.Remove(key);
        }
        UnitAuraExpiryTick[(guid, spellId)] = Environment.TickCount64 + remainingMs;
    }

    public int? TryGetAuraRemainingMs(WowGuid128 guid, int spellId)
    {
        if (!UnitAuraExpiryTick.TryGetValue((guid, spellId), out var expiry))
            return null;
        long remaining = expiry - Environment.TickCount64;
        if (remaining <= 0)
        {
            UnitAuraExpiryTick.Remove((guid, spellId));
            return null;
        }
        return (int)Math.Min(remaining, int.MaxValue);
    }

    public void ClearAuraExpiry(WowGuid128 guid, int spellId)
    {
        UnitAuraExpiryTick.Remove((guid, spellId));
    }
    // A single values update can move an aura to a lower free slot (set at B, clear at
    // old A, B < A). The clear must not delete the expiry the set just recorded.
    public bool IsSpellEmittedInAnotherSlot(WowGuid128 guid, byte exceptSlot, uint spellId)
    {
        if (!UnitAuraLastEmitted.TryGetValue(guid, out var dict))
            return false;
        foreach (var kvp in dict)
            if (kvp.Key != exceptSlot && kvp.Value.SpellID == spellId)
                return true;
        return false;
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

        lock (PendingCastsLock)
        {
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
    }

    // JimsProxy (cast-go-castid-recovery) — per-spell forwarded-START CastID FIFO helpers.
    // The unconditional recovery mechanism for local-player START↔GO/CAST_FAILED pairing.
    // See _playerForwardedStartCastIds.

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
        lock (PendingCastsLock)
        {
            foreach (var item in PendingNormalCasts)
            {
                if (item.HasStarted)
                    return true;
            }
            return false;
        }
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
        lock (PendingCastsLock)
        {
            foreach (var item in PendingNormalCasts)
            {
                if (item.HasStarted && item.TargetGuid == gameObjectGuid)
                    return true;
            }
            return false;
        }
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
        lock (PendingCastsLock)
        {
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
    }

    /// <summary>
    /// JimsProxy (ghost-swing fix): the local player's auto-attack target just died, proven
    /// terminally by SMSG_PARTY_KILL_LOG. If we were in a SETTLED auto-attack on it — the stop
    /// victim matches CurrentAttackTarget, no swing-start handshake is in flight
    /// (WaitingForAttackStart) and no stop is deferred (DeferredAttackStop) — clear the target
    /// and return true so the caller can push the modern client an immediate SMSG_ATTACK_STOP
    /// instead of waiting ~1 RTT for the legacy server to echo our CMSG_ATTACK_STOP. A dead unit
    /// can never be auto-attacked, so the server can never contradict the early stop. Returns
    /// false for the handshake / target-switch states, which are owned by the SMSG_ATTACK_STOP
    /// handler's PR #321 logic — the two fixes stay on disjoint state. Pure data operation —
    /// no socket dependency, easy to unit-test.
    /// </summary>
    public bool TryClearSettledAttackTargetOnDeath(WowGuid64 deadVictim)
    {
        if (CurrentAttackTarget == default || deadVictim != CurrentAttackTarget)
            return false;
        if (WaitingForAttackStart || DeferredAttackStop)
            return false;

        CurrentAttackTarget = default;
        return true;
    }

    /// <summary>
    /// JimsProxy (empty-victim wedge): the modern client sent CMSG_ATTACK_SWING. Returns false when
    /// the de-dupe guard should swallow it — we already believe we are auto-attacking that exact
    /// victim, so forwarding again would be redundant. Otherwise records the new handshake and
    /// returns true so the caller forwards the swing to the legacy server.
    /// Pure data operation — no socket dependency, easy to unit-test.
    /// </summary>
    public bool TryBeginLocalPlayerAttackSwing(WowGuid64 victim)
    {
        if (CurrentAttackTarget == victim)
            return false;

        // A pending stop (STOP→SWING target switch) is cancelled by the new swing — the legacy
        // server handles the switch inside CMSG_ATTACK_SWING without an explicit stop.
        DeferredAttackStop = false;
        CurrentAttackTarget = victim;
        WaitingForAttackStart = true;
        return true;
    }

    /// <summary>
    /// JimsProxy (empty-victim wedge): apply an SMSG_ATTACK_STOP that names the LOCAL PLAYER as
    /// attacker to the auto-attack handshake state, and tell the caller whether it must forward a
    /// CMSG_ATTACK_STOP to the legacy server. Pure data operation — no socket dependency.
    /// </summary>
    public PlayerAttackStopOutcome ApplyLocalPlayerAttackStop(WowGuid64 stopVictim)
    {
        if (DeferredAttackStop)
        {
            DeferredAttackStop = false;
            CurrentAttackTarget = default;
            return PlayerAttackStopOutcome.FlushDeferredStop;
        }

        if (!WaitingForAttackStart)
        {
            // Server-initiated stop without our SWING: Gouge / Cheap Shot / Blind /
            // Feign Death / stealth / Vanish. Must clear CurrentAttackTarget here or
            // the next CMSG_ATTACK_SWING gets eaten by the de-dupe guard.
            CurrentAttackTarget = default;
            return PlayerAttackStopOutcome.ClearSettledTarget;
        }

        // Server rejected our SWING with ATTACK_STOP (no prior ATTACK_START): target died or
        // became invalid between our SWING and server processing.
        //
        // An EMPTY victim counts as a rejection too. A stop naming NO victim cannot be a stale
        // stop for the OLD target of a switch — it is "you have no attack target at all". Kronos
        // sends exactly this form when it refuses the engage, e.g. Charge at a mob that is
        // evading/leashing back from another fight: the Charge lands, the server answers
        // SMSG_ATTACKSTOP(player, 0) instead of SMSG_ATTACKSTART, and the client immediately
        // re-sends CMSG_ATTACK_SWING. Before this branch covered the empty case that retry fell
        // through, CurrentAttackTarget stayed pinned to the mob forever, and the de-dupe guard in
        // TryBeginLocalPlayerAttackSwing ate every subsequent swing — auto-attack never recovered
        // for the rest of the session (wire-confirmed 2026-08-12: zero player swings across the
        // whole fight, plus a second occurrence 386s earlier in the same capture that only
        // recovered because a second Charge produced a real ATTACK_START).
        //
        // Over-clearing is the safe direction: the worst case is one redundant CMSG_ATTACK_SWING
        // forwarded, which is precisely what the client asked for.
        if (stopVictim == CurrentAttackTarget || stopVictim == WowGuid64.Empty)
        {
            WaitingForAttackStart = false;
            CurrentAttackTarget = default;
            return PlayerAttackStopOutcome.ClearRejectedHandshake;
        }

        // WaitingForAttackStart is true but the stop names a different, non-empty victim —
        // target-switch sequence, the new SWING already set CurrentAttackTarget. Keep it.
        return PlayerAttackStopOutcome.PreserveTargetSwitch;
    }

    /// <summary>
    /// JimsProxy (#450 — killing-blow ordering): arm the preemptive SMSG_ATTACK_STOP instead of
    /// emitting it inline from the SMSG_PARTY_KILL_LOG handler. Kronos sends the melee killing
    /// blow's SMSG_ATTACKER_STATE_UPDATE *after* PARTY_KILL_LOG in the same burst (6 of 12 kills
    /// in the #450 capture); an inline stop lands between the kill and the hit, and the modern
    /// client then re-plays the hit as a fresh swing on the corpse — floating text + swing sound
    /// seconds late, after the loot window is already open. The armed stop is flushed by
    /// WorldClient right after the trailing ASU for this victim is forwarded, or at socket drain
    /// when no ASU trails (spell killing blows). Returns the previously armed victim so a
    /// multi-kill burst can flush the older stop immediately — default when none was armed.
    /// Pure data operation — no socket dependency, easy to unit-test.
    /// </summary>
    public WowGuid128 ArmPreemptAttackStop(WowGuid128 victim)
    {
        var prior = PendingPreemptAttackStopVictim;
        PendingPreemptAttackStopVictim = victim;
        return prior;
    }

    /// <summary>
    /// JimsProxy (#450): pairing trigger for the armed preempt stop. Clears and returns true when
    /// (attacker, victim) is the local player's hit on the armed victim. Two call sites: the
    /// SMSG_ATTACKER_STATE_UPDATE handler (emit the stop right after forwarding the killing blow)
    /// and the SMSG_ATTACK_STOP handler (the server's own player stop was just forwarded — cancel
    /// the armed one so the drain flush can't send a duplicate). Pure data operation.
    /// </summary>
    public bool TryConsumePreemptAttackStop(WowGuid128 attacker, WowGuid128 victim)
    {
        if (PendingPreemptAttackStopVictim == default)
            return false;
        if (attacker != CurrentPlayerGuid || victim != PendingPreemptAttackStopVictim)
            return false;

        PendingPreemptAttackStopVictim = default;
        return true;
    }

    /// <summary>
    /// JimsProxy (#450): drain trigger — returns the armed victim (default if none) and clears it.
    /// Called by WorldClient's receive loop once the legacy socket has no more buffered packets,
    /// i.e. no killing-blow ASU trailed the kill log in this burst. Pure data operation.
    /// </summary>
    public WowGuid128 TakePreemptAttackStopForFlush()
    {
        var victim = PendingPreemptAttackStopVictim;
        PendingPreemptAttackStopVictim = default;
        return victim;
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
    ///
    /// JimsProxy (strafe cancel-gap): when <paramref name="newlyMarked"/> is
    /// provided, casts whose MovementCancelled flag TRANSITIONED false→true in
    /// this call are appended — re-marks are excluded, so the strafe cancel
    /// synth fires at most once per cast and never for a cast an earlier
    /// movement key (with its own client-sent cancel) already marked.
    /// </summary>
    public int MarkStartedCastsMovementCancelled(long watchdogDeadlineMs,
        List<ClientCastRequest>? newlyMarked = null)
    {
        int marked = 0;
        long nowTick = Environment.TickCount64;
        // DIAGNOSTIC (stuck-spell investigation): hoist toggle read; remove with diagnostics
        bool debugEvents = Framework.Settings.DebugOutput;
        lock (PendingCastsLock)
        {
            foreach (var cast in PendingNormalCasts)
            {
                if (cast.HasStarted && cast.StartedCastTimeMs > 0
                    && !GameData.IsChanneledSpell(cast.SpellId))
                {
                    if (!cast.MovementCancelled)
                        newlyMarked?.Add(cast);
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

        // The drain-rebuild is a compound mutation: hold the lock across it. Logging happens
        // afterwards so no file I/O runs inside the critical section.
        lock (PendingCastsLock)
        {
            var keepNormal = new List<ClientCastRequest>();
            while (PendingNormalCasts.TryDequeue(out var cast))
            {
                if (!cast.HasStarted && !cast.TargetGuid.IsEmpty() && cast.TargetGuid == destroyedGuid)
                    normalEvicted.Add(cast);
                else
                    keepNormal.Add(cast);
            }
            foreach (var c in keepNormal)
                PendingNormalCasts.Enqueue(c);

            var keepPet = new List<ClientCastRequest>();
            while (PendingPetCasts.TryDequeue(out var cast))
            {
                if (!cast.HasStarted && !cast.TargetGuid.IsEmpty() && cast.TargetGuid == destroyedGuid)
                    petEvicted.Add(cast);
                else
                    keepPet.Add(cast);
            }
            foreach (var c in keepPet)
                PendingPetCasts.Enqueue(c);
        }

        // DIAGNOSTIC (stuck-spell investigation): remove when closed
        if (debugEvents)
        {
            foreach (var cast in normalEvicted)
                LogDestroyEviction("normal", cast, destroyedGuid);
            foreach (var cast in petEvicted)
                LogDestroyEviction("pet", cast, destroyedGuid);
        }
    }

    // DIAGNOSTIC (stuck-spell investigation): remove when closed
    private static void LogDestroyEviction(string queue, ClientCastRequest cast, WowGuid128 destroyedGuid)
    {
        Log.Event("cast.destroy_eviction", new
        {
            queue,
            spell_id = cast.SpellId,
            had_started = cast.HasStarted,
            is_channeled = GameData.IsChanneledSpell(cast.SpellId),
            destroyed_target_low = destroyedGuid.GetCounter(),
            client_cast_id = cast.ClientGUID.ToString(),
        });
    }

    /// <summary>
    /// Evict pending casts whose watchdog deadline has passed. Called from the top of every spell
    /// event handler AND from CMSG_CAST_SPELL — i.e. on every single client cast press — so the
    /// common case (nothing overdue) must not touch the queues at all: a drain-rebuild there would
    /// churn the FIFO on the hot path and, worse, race the SMSG-thread dequeue.
    /// </summary>
    public void DrainExpiredWatchdogCasts(long nowMs,
        out List<ClientCastRequest> normalEvicted,
        out List<ClientCastRequest> petEvicted)
    {
        normalEvicted = new List<ClientCastRequest>();
        petEvicted = new List<ClientCastRequest>();

        lock (PendingCastsLock)
        {
            if (!HasOverdueWatchdogCast(PendingNormalCasts, nowMs) &&
                !HasOverdueWatchdogCast(PendingPetCasts, nowMs))
                return;   // hot path: nothing to evict, leave both queues untouched

            var keepNormal = new List<ClientCastRequest>();
            while (PendingNormalCasts.TryDequeue(out var cast))
            {
                if (IsWatchdogOverdue(cast, nowMs))
                    normalEvicted.Add(cast);
                else
                    keepNormal.Add(cast);
            }
            foreach (var c in keepNormal)
                PendingNormalCasts.Enqueue(c);

            var keepPet = new List<ClientCastRequest>();
            while (PendingPetCasts.TryDequeue(out var cast))
            {
                if (IsWatchdogOverdue(cast, nowMs))
                    petEvicted.Add(cast);
                else
                    keepPet.Add(cast);
            }
            foreach (var c in keepPet)
                PendingPetCasts.Enqueue(c);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWatchdogOverdue(ClientCastRequest cast, long nowMs)
        => cast.WatchdogDeadlineMs > 0 && cast.WatchdogDeadlineMs < nowMs;

    private static bool HasOverdueWatchdogCast(ConcurrentQueue<ClientCastRequest> queue, long nowMs)
    {
        foreach (var cast in queue)
        {
            if (IsWatchdogOverdue(cast, nowMs))
                return true;
        }
        return false;
    }

    /// <summary>
    /// JimsProxy (Mount-Button-Stuck-Lit): returns true if any pending normal cast — started OR
    /// merely in flight to the legacy server — matches the given SpellId (or its LegacySpellId
    /// for SoM-renumbered USE_ITEMs).
    /// </summary>
    public bool HasInFlightNormalCastForSpell(uint spellId)
    {
        lock (PendingCastsLock)
        {
            foreach (var item in PendingNormalCasts)
            {
                if (CastMatchesSpellId(item, spellId))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Returns true if any pending normal cast has been forwarded to the legacy server but
    /// hasn't received SMSG_SPELL_START yet. Covers the post-GCD-expiry window where
    /// IsGcdHoldActive() returns false but the server hasn't confirmed the forwarded cast.
    /// </summary>
    public bool HasForwardedPendingCast()
    {
        lock (PendingCastsLock)
        {
            foreach (var item in PendingNormalCasts)
            {
                if (!item.HasStarted)
                    return true;
            }
            return false;
        }
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

    /// <summary>
    /// JimsProxy (GCD held_pending race): release a press parked in _heldGcdCast by the
    /// HasForwardedPendingCast guard (ForceHoldCast) when the forwarded cast that blocked it
    /// turns out to be a CAST-TIME spell, learned at its SPELL_START. Cast-time spells arm no
    /// GCD-expiry timer (BeginGcd is GO-side, instants only) and their next SPELL_GO is at
    /// completion — so the parked press would otherwise ride the entire cast and fire a full
    /// cast-time late. Returns the parked cast for the caller to forward (the server answers
    /// SpellInProgress while the cast occupies the caster). Returns null for an instant START
    /// (startedCastTimeMs == 0 — left to the GO/BeginGcd path), while a GCD timer is still
    /// pending (it will release the hold), while another forwarded cast is still unstarted
    /// (keep waiting for it), or when nothing is parked.
    /// </summary>
    public ClientCastRequest? TakeForcedHoldOrphanedByCastTimeStart(uint startedCastTimeMs)
    {
        if (startedCastTimeMs == 0)
            return null;
        if (HasForwardedPendingCast())
            return null;
        lock (_gcdLock)
        {
            if (_heldGcdCast == null)
                return null;
            if (_gcdExpireTimestampMs > Environment.TickCount64)
                return null; // GCD timer still pending — it will release the hold
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

        lock (PendingCastsLock)
        {
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
        lock (PendingCastsLock)
        {
            while (PendingPetCasts.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// JimsProxy reconnect-state-cleanup: drop all in-flight cast bookkeeping
    /// after an unplanned disconnect+reconnect. The legacy server forgets
    /// everything we had in flight when it cuts the socket; if we don't drop
    /// our local mirror, the next press of any spell that was pending at DC
    /// time gets silently rejected by HasNonStartedPendingCastForSpell —
    /// user-visible symptom is "spell stuck, spamming key does nothing, no
    /// error message" (e.g. rogue's R-key Sinister Strike not firing). Same
    /// story for _observedLiveCastIds (mob/other-player CastIDs minted
    /// pre-DC won't match anything the new server-side state knows about).
    /// Returns the count of entries cleared so the reconnect log can show
    /// whether the gap was actually significant.
    /// </summary>
    public (int normalCasts, int petCasts, int otherCasterIds) ResetInFlightCastState()
    {
        int normalCount;
        int petCount;
        lock (PendingCastsLock)
        {
            normalCount = PendingNormalCasts.Count;
            while (PendingNormalCasts.TryDequeue(out _)) { }
            petCount = PendingPetCasts.Count;
            while (PendingPetCasts.TryDequeue(out _)) { }
        }
        int otherCount = ClearObservedCastIds();
        PetAutoCastActiveCastIds.Clear();
        ClearForwardedStartCastIds();
        // JimsProxy (dup-failure frame hold): drop held dup failures with the session state —
        // their anchors were just cleared, and the client resets its own cast/button state
        // across a reconnect or load screen. Delivering them into the new session would ship
        // stale-CastID packets and pollute the held_ms field-gate metric.
        lock (_heldDupFailuresLock)
        {
            _heldDupFailures.Clear();
        }
        // Single-slot trackers for melee + auto-repeat (Auto Shot, Shoot Wand)
        // — same lifecycle as PendingNormalCasts; if a tracker was set when
        // the DC fired, it never gets cleared by the SPELL_GO/CAST_FAILED
        // path that normally nulls it.
        CurrentClientNextMeleeCast = null;
        CurrentClientAutoRepeatCast = null;
        OnAutoRepeatRetry = null;
        ClearAllObservedAutoRepeat();
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

        lock (PendingCastsLock)
        {
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
    /// JimsProxy: narrow variant of IsGcdHoldActive — returns true only when the GCD is inside
    /// its hold-admission tail. Mirrors the 1.14 client's SpellQueueWindow semantics for the
    /// GCD case (instants pressed in the tail of the previous cast's GCD get queued and fire
    /// on GCD expiry; earlier presses are forwarded and receive the server's NOT_READY).
    /// Queue mode reads the configurable Framework.Settings.SpellQueueWindowMs; under RTT
    /// Pre-Fire Timer the width is the fixed Settings.RttPrefireTimerWindowMs (400 ms) so the
    /// launcher's greyed-out spell-queue dropdown can't silently govern Timer holds.
    /// Used by the HandleCastSpell GCD hold gate. The wider
    /// IsGcdHoldActive() remains for callers that need "is any GCD active at all"
    /// (e.g. the held-cast-on-failure release path in Client/SpellHandler.cs).
    /// </summary>
    public bool IsInGcdQueueWindow()
    {
        lock (_gcdLock)
        {
            long remaining = _gcdExpireTimestampMs - Environment.TickCount64;
            long window = Framework.Settings.RttPrefireTimerActive
                ? Framework.Settings.RttPrefireTimerWindowMs
                : Framework.Settings.SpellQueueWindowMs;
            return remaining > 0 && remaining <= window;
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

    // JimsProxy (observed-bow retract): mark an observed shooter latched (we forwarded its auto-repeat aim), cache the unit it's shooting (refreshed each shot so a mid-series retarget updates the death-match key) and stamp the shot time, then lazily arm the quiescence sweep. The timer is armed only when OnObservedAutoRepeatExpire is bound (production WorldClient) — null in tests, so the suite never spins a real timer; the deterministic edges run regardless.
    public void NoteObservedAutoRepeatActivity(WowGuid128 shooter, WowGuid128 target)
    {
        lock (_observedAutoRepeatLock)
        {
            _observedShooterTargets[shooter] = new ObservedShooterState(target, Environment.TickCount64);
            if (OnObservedAutoRepeatExpire != null)
                _observedAutoRepeatSweepTimer ??= new Timer(OnObservedAutoRepeatSweepTick, null, ObservedAutoRepeatSweepTickMs, ObservedAutoRepeatSweepTickMs);
        }
    }

    // Test seam: latch a shooter at an injected timestamp without arming the real background timer (which runs on Environment.TickCount64 and would race injected-clock sweeps).
    internal void NoteObservedAutoRepeatActivityForTest(WowGuid128 shooter, WowGuid128 target, long nowMs)
    {
        lock (_observedAutoRepeatLock)
            _observedShooterTargets[shooter] = new ObservedShooterState(target, nowMs);
    }

    // JimsProxy (observed-bow retract): a deterministic stop edge for one shooter (it moved or died, or we're tearing down) — drop the latch and report whether it WAS latched so the caller sends exactly one SMSG_CANCEL_AUTO_REPEAT.
    public bool TryEndObservedAutoRepeat(WowGuid128 shooter)
    {
        lock (_observedAutoRepeatLock)
            return _observedShooterTargets.Remove(shooter);
    }

    // JimsProxy (observed-bow retract): the shooter's UNIT_FIELD_TARGET changed — if it's latched and now aimed elsewhere (or cleared), the prior series ended, so drop the latch and report it for retract. A still-firing retarget self-heals on the next shot's START.
    public bool TryEndObservedAutoRepeatOnTargetChange(WowGuid128 shooter, WowGuid128 newTarget)
    {
        lock (_observedAutoRepeatLock)
        {
            if (_observedShooterTargets.TryGetValue(shooter, out var cached) && cached.Target != newTarget)
            {
                _observedShooterTargets.Remove(shooter);
                return true;
            }
            return false;
        }
    }

    // JimsProxy (observed-bow retract): a unit died (PARTY_KILL_LOG victim or health->0) — collect and drop every observed shooter aimed at it so the caller retracts each. Terminal-proof: a corpse can't be shot, so the retract can never be contradicted.
    public List<WowGuid128> EndObservedAutoRepeatForVictim(WowGuid128 victim)
    {
        var hit = new List<WowGuid128>();
        lock (_observedAutoRepeatLock)
        {
            foreach (var kvp in _observedShooterTargets)
                if (kvp.Value.Target == victim)
                    hit.Add(kvp.Key);
            foreach (var shooter in hit)
                _observedShooterTargets.Remove(shooter);
        }
        return hit;
    }

    private void OnObservedAutoRepeatSweepTick(object? state)
    {
        // Invoke the cancel callback outside the lock (mirrors OnGcdTimerElapsed) so the off-thread SendPacketToClient never runs under _observedAutoRepeatLock.
        foreach (var shooter in SweepObservedAutoRepeat(Environment.TickCount64))
        {
            // A sweep landing during session teardown must never surface an unhandled exception on the ThreadPool (would crash the process); the aim is moot once disconnected.
            try { OnObservedAutoRepeatExpire?.Invoke(shooter); }
            catch { }
        }
    }

    // JimsProxy (observed-bow retract): the quiescence catch-all — remove and return shooters quiet past ObservedAutoRepeatQuietMs (no edge covered their stop: out of ammo, /stopattack, LoS, target dummy), and dispose the sweep timer once the table empties so it self-disarms. Internal + injectable clock for deterministic tests.
    internal List<WowGuid128> SweepObservedAutoRepeat(long nowMs)
    {
        var expired = new List<WowGuid128>();
        lock (_observedAutoRepeatLock)
        {
            foreach (var kvp in _observedShooterTargets)
                if (nowMs - kvp.Value.LastShotMs >= ObservedAutoRepeatQuietMs)
                    expired.Add(kvp.Key);
            foreach (var shooter in expired)
                _observedShooterTargets.Remove(shooter);
            if (_observedShooterTargets.Count == 0)
            {
                _observedAutoRepeatSweepTimer?.Dispose();
                _observedAutoRepeatSweepTimer = null;
            }
        }
        return expired;
    }

    // JimsProxy (observed-bow retract): drop all observed auto-repeat tracking on reconnect, disarm the sweep, and unbind the cancel callback so a post-reconnect shot re-binds it to the live WorldClient. Called from ResetInFlightCastState alongside the other auto-repeat trackers.
    public void ClearAllObservedAutoRepeat()
    {
        lock (_observedAutoRepeatLock)
        {
            _observedShooterTargets.Clear();
            _observedAutoRepeatSweepTimer?.Dispose();
            _observedAutoRepeatSweepTimer = null;
            OnObservedAutoRepeatExpire = null;
        }
    }

    /// <summary>
    /// Try to find and dequeue a pending cast by ItemGUID (for item use failures).
    /// Only matches casts that haven't started yet. An empty GUID matches nothing —
    /// anonymous rejections pair via <see cref="TryDequeueOldestUnstartedItemCast"/>.
    /// </summary>
    public bool TryDequeueItemCast(WowGuid128 itemGuid, out ClientCastRequest? cast)
    {
        var pending = new List<ClientCastRequest>();
        cast = null;

        // JimsProxy (#442 review, Issue A): an empty failure GUID must match NOTHING. Normal
        // CMSG_CAST_SPELL entries leave ItemGUID at default (WowGuid128 is a struct), so the
        // == below would pair `empty == empty` with the oldest unstarted SPELL entry — a
        // spurious visible CastFailed for a healthy cast, and the real item orphan surviving
        // behind the handler's else-if (the #442 lockout, in exactly the raid shape).
        if (itemGuid.IsEmpty())
            return false;

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

    /// <summary>
    /// JimsProxy (#442): dequeue the OLDEST forwarded-but-unstarted item-use cast, regardless of
    /// item GUID. Fallback for SMSG_INVENTORY_CHANGE_FAILURE rejections that Kronos sends with
    /// EMPTY item GUIDs (observed live 2026-07-29: a duplicate CMSG_USE_ITEM straddling its
    /// predecessor's SPELL_GO was rejected with result 23/ItemNotFound and both GUIDs zero), which
    /// TryDequeueItemCast can't match. Without this the rejected entry is unreapable — unstarted
    /// (jams HasForwardedPendingCast → every on-GCD press silently parked until relog/map change),
    /// IsOffGcd (spared by ClearNonStartedNormalCasts), never SPELL_FAILURE-peeked (no watchdog),
    /// and a permanent HasInFlightNormalCastForSpell match (that item unusable).
    ///
    /// FIFO pairing: the rejection is the server's answer to SOME outstanding use; when the GUID
    /// doesn't identify which, the oldest unresolved one is the correct pair in the single-item
    /// case (the overwhelmingly common one — the CMSG-side dup guard keeps same-spell entries
    /// unique). Known edge with TWO different items in flight: an unrelated empty-GUID inventory
    /// failure inside the one-RTT window can evict the wrong (healthy) entry; its later GO then
    /// finds no queue match and forwards unmatched — cosmetic, self-limiting, and strictly better
    /// than the permanent lockout. See issue #442.
    /// </summary>
    public bool TryDequeueOldestUnstartedItemCast(out ClientCastRequest? cast)
    {
        var pending = new List<ClientCastRequest>();
        cast = null;

        lock (PendingCastsLock)
        {
            while (PendingNormalCasts.TryDequeue(out var current))
            {
                if (cast == null && !current.HasStarted && !current.ItemGUID.IsEmpty())
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

    /// <summary>Ban defense (Kronos IsInWorld race): speculatively removes the predecessor rank
    /// at CMSG_TRAINER_BUY_SPELL, mirroring Kronos's server-side RemoveSpell(prev) that can run
    /// without any notification packet. Returns whether the predecessor was actually in the set.
    /// Pure data operation — no socket dependency, easy to unit-test.</summary>
    public bool ApplyTrainerBuyPredecessorRemoval(uint realSpellId, uint predecessor)
    {
        bool removed = CurrentPlayerKnownSpells.Remove(predecessor);
        PendingTrainerBuySpellId = realSpellId;
        PendingTrainerBuyRemovedPredecessor = removed ? predecessor : 0u;
        return removed;
    }

    /// <summary>SMSG_LEARNED_SPELL bookkeeping: records the learn; a confirmed learn for the
    /// pending trainer buy restores the speculatively-removed predecessor — no SUPERCEDED_SPELLS
    /// means the server KEPT the lower rank (downrankable chain, e.g. Shadowguard). Returns the
    /// restored predecessor id, or 0. A real supersede chain still ends removed in either arrival
    /// order: SUPERCEDED-first clears the pending state so nothing restores; LEARNED-first
    /// restores transiently and SUPERCEDED's unconditional remove wins. The no-response Kronos
    /// race (the autoban this defense exists for) confirms nothing, so its removal stands.</summary>
    public uint ApplyLearnedSpellKnownState(uint spellId)
    {
        CurrentPlayerKnownSpells.Add(spellId);
        if (PendingTrainerBuySpellId != spellId)
            return 0u;
        uint restored = PendingTrainerBuyRemovedPredecessor;
        if (restored != 0)
            CurrentPlayerKnownSpells.Add(restored);
        PendingTrainerBuySpellId = 0u;
        PendingTrainerBuyRemovedPredecessor = 0u;
        return restored;
    }

    /// <summary>SMSG_SUPERCEDED_SPELLS bookkeeping: the server really removed the old rank, so
    /// the proxy's view drops it and any matching pending speculative removal is confirmed —
    /// cleared WITHOUT restoring.</summary>
    public void ApplySupercededSpellKnownState(uint newSpellId, uint supercededId)
    {
        CurrentPlayerKnownSpells.Remove(supercededId);
        CurrentPlayerKnownSpells.Add(newSpellId);
        if (PendingTrainerBuySpellId == newSpellId || PendingTrainerBuySpellId == supercededId)
        {
            PendingTrainerBuySpellId = 0u;
            PendingTrainerBuyRemovedPredecessor = 0u;
        }
    }

    /// <summary>SMSG_TRAINER_BUY_FAILED bookkeeping: an explicit rejection means the server never
    /// removed the predecessor — restore it when the failure names the pending buy (real id or
    /// learn-wrapper id). Returns the restored predecessor id, or 0. Preserves the shipped
    /// fail-safe: a non-matching failure clears the pending state WITHOUT restoring (the removal
    /// stands, the cast guard keeps blocking — over-blocking is recoverable, a ban is not).</summary>
    public uint ApplyTrainerBuyFailedKnownState(uint failedSpellId)
    {
        if (PendingTrainerBuySpellId == 0 || PendingTrainerBuyRemovedPredecessor == 0)
            return 0u;
        uint restored = 0u;
        if (failedSpellId == PendingTrainerBuySpellId ||
            failedSpellId == GetLearnSpellFromRealSpell(PendingTrainerBuySpellId))
        {
            restored = PendingTrainerBuyRemovedPredecessor;
            CurrentPlayerKnownSpells.Add(restored);
        }
        PendingTrainerBuySpellId = 0u;
        PendingTrainerBuyRemovedPredecessor = 0u;
        return restored;
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

    // JimsProxy (held-aware GCD anchoring): true once this press was released from the GCD
    // hold slot by the release timer (ForwardHeldGcdCast) — i.e. the proxy RE-TIMED it.
    // The synthetic GCD-anchor packets (#124 cooldown synth, bb4bb18 GO.CastTime stamp) exist
    // to correct drift the client accrues across re-timed casts; their LL gates key on this
    // flag rather than on mode alone, so a hold path re-admitted under LL (RttPrefire=Timer)
    // keeps its anchors while never-held LL casts stay packet-trimmed.
    public bool WasHeld;

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

    // JimsProxy (strafe cancel-gap presentation parity): set true (alongside
    // MovementCancelled) ONLY for casts the strafe branch synthesized a
    // CMSG_CANCEL_CAST for — i.e. the ones the 1.14 client did NOT cancel itself.
    // The client renders the red "Interrupted" locally when IT initiates the
    // cancel (forward/back/jump), but it never sends nor predicts a cancel on
    // strafe, so a strafe-synth-cancelled cast's trailing SMSG_SPELL_FAILURE must
    // be FORWARDED with the interrupt reason (not suppressed like the
    // client-predicted case) to reproduce that "Interrupted" render. Only ever set
    // when Settings.StrafeCancelPreempt is on (the synth's sole trigger), so it is
    // structurally inert when the kill switch is off.
    public bool StrafeSynthCancelled;

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

    // JimsProxy KTM originator: broadcasts our engine's threat on the 1.12 KLHTM
    // channel for KLHThreatMeter raiders when the local client has no KTM addon
    // of its own (the addon case is handled by KtmThreatBridge.RewriteOutbound).
    public KtmThreatOriginator KtmThreatOriginator = null!;

    public GlobalSessionData()
    {
        GameState = GameSessionData.CreateNewGameSessionData(this);
        AuthClient = new AuthClient(this);
        ThreatTracker = new ThreatTracker(this);
        HealCommBridge = new HealCommBridge(this);
        KtmThreatOriginator = new KtmThreatOriginator(this);
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
            if (cast.HasStarted)
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
        KtmThreatOriginator.Reset();
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

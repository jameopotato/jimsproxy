using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HermesProxy.World;

// JimsProxy HealComm bridge: cross-version heal-prediction and resurrection
// addon-comm translation.
//
// Vanilla 1.12 clients run HealComm-1.0 over the addon channel ("HealComm" prefix);
// modern 1.14 clients run LibHealComm-4.0 ("LHC40" prefix). On a mixed-population
// server (1.12 native players AND 1.14-via-proxy players in the same group/raid),
// neither side sees the other's heal predictions or resurrection casts because the
// wire formats are incompatible.
//
// This bridge runs Option A — in-place translation, single wire format. We rewrite
// outbound LHC40 messages from our modern client into HC-1.0 before they hit the
// legacy server, and rewrite inbound HC-1.0 messages from 1.12 natives into LHC40
// before they reach our modern client. Effective wire bandwidth is unchanged from
// HealComm-1.0-only baseline; lossy LHC40-rich data (per-tick HoT timing, channel
// bars, bombed Regrowth) downgrades to HC-1.0 minimum even between modern peers.
// Future v2 (peer detection + dual-emit) can lift that limit.
//
// Resurrection direction A (we cast a rez, 1.12 natives see it on their Luna frames)
// is handled by synthesizing an HC-1.0 outbound from the local player's spell-cast
// observation in HandleSpellStartOrGo. Direction B (1.12 native rezzes us, our
// modern client's native indicator fires) needs no addon translation — modern
// Luna's resurrect indicator reads the native UnitHasIncomingResurrection API,
// which is driven entirely client-side from SMSG_SPELL_START with a resurrect-
// effect spell. That works for free if the proxy preserves the target GUID,
// independent of this bridge.
//
// Threading: methods are called from chat / spell packet handlers on the
// WorldClient or WorldSocket threads. The pending-cast cache uses
// ConcurrentDictionary so cross-thread reads from inbound translation
// don't race with spell-cast bookkeeping.
public sealed class HealCommBridge
{
    public const string LhcPrefix = "LHC40";
    public const string HcPrefix = "HealComm";

    // Vanilla HoTs all tick on a 3-second interval. Receiver computes
    // duration = totalTicks * tickInterval / 1000 (or inverse on inbound).
    private const int VanillaHotTickIntervalMs = 3000;

    // Spell-ID rank-1 anchors. LibHealComm-4.0 keys by spellID; HealComm-1.0
    // doesn't carry one. When we synthesize LHC40 from inbound HC-1.0 we
    // need a representative spellID so the modern lib renders an icon.
    // Rank-1 is fine — the prediction visual is the same across ranks.
    private const uint SpellRenew = 139;
    private const uint SpellRejuvenation = 774;
    private const uint SpellRegrowth = 8936;
    private const uint SpellPrayerOfHealing = 596;
    // Priest, named plain "Heal": stands in for a heal that cannot be identified. JimsPlus is
    // told separately that the spell is not known.
    private const uint SpellHeal = 2054;
    private const uint SpellFlashHeal = 2061;         // Priest
    private const uint SpellHealingTouch = 5185;      // Druid
    private const uint SpellHolyLight = 635;          // Paladin
    private const uint SpellFlashOfLight = 19750;     // Paladin
    private const uint SpellHealingWave = 331;        // Shaman

    // Direct heals by spell id (every rank). A proxied healer's side appends the spell it is
    // casting to its HC-1.0 announcement (see TranslateDirectHealOutbound); an appended id is
    // used only if it is one of these, so a crafted message cannot put an arbitrary cast on a
    // player.
    private static readonly HashSet<uint> DirectHealSpellIds = new()
    {
        2050, 2052, 2053,                                                  // Lesser Heal
        2054, 2055, 6063, 6064,                                            // Heal
        2060, 10963, 10964, 10965, 25314,                                  // Greater Heal
        2061, 9472, 9473, 9474, 10915, 10916, 10917,                       // Flash Heal
        635, 639, 647, 1026, 1042, 3472, 10328, 10329, 25292,              // Holy Light
        19750, 19939, 19940, 19941, 19942, 19943,                          // Flash of Light
        331, 332, 547, 913, 939, 959, 8005, 10395, 10396, 25357,           // Healing Wave
        8004, 8008, 8010, 10466, 10467, 10468,                             // Lesser Healing Wave
        1064, 10622, 10623,                                                // Chain Heal
        5185, 5186, 5187, 5188, 5189, 6778, 8903, 9758, 9888, 9889, 25297, // Healing Touch
        8936, 8938, 8939, 8940, 8941, 9750, 9856, 9857, 9858,              // Regrowth
    };

    // Resurrection spell IDs we synthesize outbound HC-1.0 for. Druid Rebirth
    // and Warlock Soulstone are intentionally absent — both are instant casts
    // (only emit SMSG_SPELL_GO, never SPELL_START), so HC-1.0's
    // start/stop model doesn't apply, and the modern native indicator
    // doesn't fire for them either (matches retail behavior).
    private static readonly HashSet<uint> ResurrectionSpellIds = new()
    {
        2006, 2010, 10880, 10881, 20770,           // Priest Resurrection (all ranks + R5)
        7328, 10322, 10324, 20772, 20773, 25435,   // Paladin Redemption
        2008, 20609, 20610, 20776, 20777,          // Shaman Ancestral Spirit
    };

    // HC-1.0 multi-target group heal — vanilla only has Prayer of Healing.
    // Used to pick GrpHeal vs Heal on outbound translation.
    private static readonly HashSet<uint> GroupHealSpellIds = new()
    {
        596, 996, 10960, 10961, 25316,              // Prayer of Healing all ranks
    };

    // HoT spell IDs → HC-1.0 wire keyword. LibHealComm-4.0 sends H: messages
    // with the spellID; we map back to the HC-1.0 string the 1.12 receiver
    // expects ("Renew" / "Reju" / "Regr").
    private static readonly Dictionary<uint, string> HotKeywordById = new()
    {
        // Renew (Priest)
        { 139, "Renew" }, { 6074, "Renew" }, { 6075, "Renew" }, { 6076, "Renew" },
        { 6077, "Renew" }, { 6078, "Renew" }, { 10928, "Renew" }, { 10929, "Renew" },
        { 10930, "Renew" }, { 25315, "Renew" },
        // Rejuvenation (Druid)
        { 774, "Reju" }, { 1058, "Reju" }, { 1430, "Reju" }, { 2090, "Reju" },
        { 2091, "Reju" }, { 3627, "Reju" }, { 8910, "Reju" }, { 9839, "Reju" },
        { 9840, "Reju" }, { 9841, "Reju" }, { 25299, "Reju" },
        // Regrowth (Druid) — HoT portion; the bomb is instant
        { 8936, "Regr" }, { 8938, "Regr" }, { 8939, "Regr" }, { 8940, "Regr" },
        { 8941, "Regr" }, { 9750, "Regr" }, { 9856, "Regr" }, { 9857, "Regr" },
        { 9858, "Regr" },
    };

    // HC-1.0 keyword → representative LHC40 spellID (rank 1) for inbound
    // synthesis. Modern lib uses spellID for icon and HoT-stack tracking.
    private static readonly Dictionary<string, uint> HotIdByKeyword = new()
    {
        { "Renew", SpellRenew },
        { "Reju", SpellRejuvenation },
        { "Regr", SpellRegrowth },
    };

    private readonly GlobalSessionData _session;

    // Per-caster pending cast metadata. Used on inbound HC-1.0 → LHC40 to
    // resolve a representative spellID for the per-class direct heal
    // (HC-1.0 omits it from Heal/...) and to compute relative timestamps
    // for delay messages, plus to remember the spellID across Healstop.
    private sealed class PendingCast
    {
        public uint SpellId;
        public bool IsGroup;
        public DateTime StartUtc;
        public DateTime EndUtc;
    }

    private readonly ConcurrentDictionary<WowGuid128, PendingCast> _pendingByCaster = new();

    // Per-remote-caster active synthesized cast. We synthesize SMSG_SPELL_START
    // for inbound HC-1.0 heals so the modern client's native UnitCastingInfo /
    // UnitGetIncomingHeals fire (instead of trying to make LibHealComm-4.0
    // consume rewritten addon comms — that path fails because vanilla servers
    // gate-broadcast SPELL_START so remote raid healers' casts never reach the
    // client, leaving LHC4 with no UnitCastingInfo to anchor endTime to).
    // Tracked per-caster (not per-spell) because HC-1.0 Healstop carries no
    // spell ID — at most one synth cast per caster at a time, matching how
    // vanilla HealComm-1.0 itself models a caster's pending cast.
    //
    // JimsProxy (announced heals), read from the 1.14.2 client: the incoming-heal entry is
    // recorded only when the caster's unit object exists on the client, and so is the combat
    // log line. A synthesized cast for a healer with no unit there (the usual out-of-range
    // case) is therefore dropped whole; that healer's heal reaches only LibHealComm-4.0 (the
    // LHC40 message) and JimsPlus (the JP_HC sideband). The synthesized cast does its job for
    // a healer who is in view but whose cast start the server did not send.
    private sealed class SynthCast
    {
        public uint SpellId;
        // The synthesized cast's id; for an adopted cast, the id of the server's own cast.
        public WowGuid128 CastId;
        public uint SpellVisualId;
        public DateTime ExpectedEndUtc;
        // False when the client already shows this heal from the server's own SMSG_SPELL_START
        // (see MatchRealCast): no cast was synthesized, so there is none to delay or dismiss.
        // The entry is still tracked so the heal's stop and delay reach LibHealComm-4.0.
        public bool SentSpellStart;
        // The synthesized cast was also described to JimsPlus over the sideband (JP_HC), so its
        // delay and end go there too.
        public bool SentSideband;
    }

    private readonly ConcurrentDictionary<WowGuid128, SynthCast> _synthCastsByCaster = new();

    // JimsProxy (a heal's cast bar turning into Greater Heal): the last cast-time cast the server
    // itself started for another player, i.e. a cast the client is already showing. A healer's
    // addon announces a heal only after its own client sees the cast start, so for a healer in
    // range the server's SMSG_SPELL_START reaches us first and the announcement a round trip
    // later. Synthesizing a start for that announcement put a second cast on the healer, under a
    // spell guessed from the class (Greater Heal for a priest or an unknown class): the client
    // logged a second SPELL_CAST_START and every cast bar switched to the guess mid-cast.
    private readonly struct RealCast
    {
        public readonly uint SpellId;
        // The id the proxy gave this cast when it forwarded the start. The server's later
        // packets about the cast are paired to it (GameSessionData.TryPairObserved*), which is
        // how its end is told apart from the end of another cast of the same spell.
        public readonly WowGuid128 CastId;
        // While the cast runs, when it is expected to end; once Ended, when it did.
        public readonly long EndMs;
        // The server has since sent this cast's SMSG_SPELL_GO or failure: the client saw it
        // start and end, so a late announcement of it has nothing left to describe.
        public readonly bool Ended;
        // As forwarded in the cast start, for sending that start again (AttachNativeAmount).
        public readonly uint SpellVisualId;
        public readonly uint CastFlags;
        public readonly uint CastFlagsEx;

        public RealCast(uint spellId, WowGuid128 castId, long endMs, uint spellVisualId, uint castFlags, uint castFlagsEx, bool ended = false)
        {
            SpellId = spellId;
            CastId = castId;
            EndMs = endMs;
            SpellVisualId = spellVisualId;
            CastFlags = castFlags;
            CastFlagsEx = castFlagsEx;
            Ended = ended;
        }

        public RealCast AsEnded(long endedMs) => new(SpellId, CastId, endedMs, SpellVisualId, CastFlags, CastFlagsEx, ended: true);
    }

    // JimsProxy (announced heals): an incoming-heal entry this bridge put on the client for a
    // cast the SERVER started (see AttachNativeAmount). The client removes it when it processes
    // that cast's SMSG_SPELL_GO or failure and at no other time: not when the cast's time is up,
    // not on the caster's next cast, not when the caster leaves view. Left alone in those cases
    // it would stay on the target's frame until the target itself left view, so every entry is
    // kept here until the server's end packet for the cast has been forwarded or the bridge has
    // ended it itself.
    private sealed class NativeAmount
    {
        public WowGuid128 Caster;
        public WowGuid128 CastId;
        public uint SpellId;
        public uint SpellVisualId;
        public DateTime DeadlineUtc;
    }

    private readonly ConcurrentDictionary<WowGuid128, NativeAmount> _nativeAmounts = new();

    // How long past a cast's expected end the bridge waits for the server's end packet before
    // ending the client's entry itself. Generous on purpose: the failure it sends also ends the
    // caster's cast on the client, and an observer is not told about pushback, so a cast can
    // legitimately run past the end the bridge expects.
    internal TimeSpan NativeAmountCleanupGrace = TimeSpan.FromSeconds(2);

    // Test seam for Settings.HealCommNativeAmountInRange (a static, shared by every test class).
    internal bool? NativeAmountInRangeForTest;

    private enum RealCastMatch
    {
        // The client was shown no such cast: a healer out of range, which the synthesis is for.
        None,
        // The client is showing the cast the announcement describes.
        Adopted,
        // The client showed that cast and it has already ended.
        Ended,
    }

    private readonly Dictionary<WowGuid128, RealCast> _realCastsByCaster = new();
    private readonly List<WowGuid128> _realCastSweepScratch = new();
    private readonly object _realCastsLock = new();

    // How long after a real cast's expected end an announcement still counts as that cast's.
    // The announcement trails the cast by the caster's round trip, so on a short heal from a
    // laggy caster it can land after the cast completed. Erring this way costs one native
    // prediction if the healer left range in that second; the other way shows a cast that
    // never happened.
    private const long RealCastAnnounceGraceMs = 1000;

    // Expired entries are swept when the table reaches this size (one entry per player seen
    // casting, so it would otherwise grow for the whole session in a city or battleground).
    private const int RealCastSweepThreshold = 64;

    internal Func<long> ClockMsForTest = static () => Environment.TickCount64;

    // Tolerance window for distinguishing HC-1.0 Healstop-as-natural-completion
    // (no dismiss needed; cast bar drains to zero on its own) from
    // Healstop-as-interrupt (must send SpellFailedOther to dismiss bar early).
    // 250ms covers RTT + scheduler jitter; tighter risks false-natural for real
    // interrupts very near the end of the cast.
    private static readonly TimeSpan NaturalCompletionTolerance = TimeSpan.FromMilliseconds(250);

    public HealCommBridge(GlobalSessionData session)
    {
        _session = session;
    }

    // ---- Outbound: LHC40 (modern client) → HC-1.0 (vanilla wire) ----------

    // Returns true if the addon message was translated and prefix/text
    // have been rewritten in place. Caller continues normal flow with the
    // new prefix/text and 1.12 natives in the raid see the prediction
    // on their HealComm-1.0 / Luna unit frames.
    //
    // Returns false if the message wasn't an LHC40 prediction we recognize
    // (channel bars, bombs, HoT-stack events) — in that case the caller
    // forwards the original LHC40 untranslated, which 1.12 natives will
    // simply ignore (different prefix).
    public bool TryTranslateOutboundAddon(ref string prefix, ref string text)
    {
        if (prefix != LhcPrefix)
            return false;

        // LHC40 fields: commType, extraArg, spellID, arg1, arg2, arg3, arg4, arg5, arg6
        var parts = text.Split(':');
        if (parts.Length < 2)
            return false;

        string commType = parts[0];
        string? translated = commType switch
        {
            "D" => TranslateDirectHealOutbound(parts),
            "S" => TranslateStopOutbound(parts),
            "F" => TranslateDelayOutbound(parts),
            "H" => TranslateHotOutbound(parts),
            _ => null,
        };

        if (translated == null)
            return false;

        prefix = HcPrefix;
        text = translated;
        return true;
    }

    private string? TranslateDirectHealOutbound(string[] parts)
    {
        // LHC40 D:{castSec}:{spellID}:{amount}:{guidsCSV}  (extraArg is castTime in seconds)
        // HC-1.0 Heal/{name}/{amount}/{castMs}/  (HC-1.0 receiver does (casttime/1000)+GetTime())
        // Unit conversion required: HC-1.0 wire is MILLISECONDS, LHC40 wire is SECONDS.
        if (parts.Length < 5) return null;

        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float castSec))
            return null;
        if (!uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint spellId))
            return null;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
            return null;

        var targetNames = ResolveCompressedGuidsToNames(parts[4]);
        if (targetNames.Count == 0)
            return null;

        int castMs = (int)Math.Round(castSec * 1000f);

        // Group heal (Prayer of Healing) → GrpHeal/{amount}/{castMs}/{n1}/{n2}/.../
        // HC-1.0 group format ends with a trailing slash after the last name.
        if (GroupHealSpellIds.Contains(spellId) || targetNames.Count > 1)
        {
            var sb = new StringBuilder();
            sb.Append("GrpHeal/").Append(amount).Append('/').Append(castMs).Append('/');
            foreach (var name in targetNames)
                sb.Append(name).Append('/');
            return sb.ToString();
        }

        return FormatDirectHeal(targetNames[0], amount, castMs, spellId, Framework.Settings.HealCommAnnounceSpellId);
    }

    // Single-target direct heal → Heal/{targetName}/{amount}/{castMs}/
    //
    // JimsProxy (announced heals): with `withSpell` (Settings.HealCommAnnounceSpellId, off by
    // default) the spell being cast rides along as a fifth field. Our own side is the one place
    // the spell of this heal is known for certain, and HC-1.0 has no field for it, so a proxied
    // receiver out of range of this healer otherwise has to work it out. The 1.12 receivers read
    // so far take fields two to four by position and ignore the rest (HealComm-1.0
    // startHeal(result[2], result[3], result[4]); pfUI msgobj[2..4]); one that does not would
    // lose this healer's heals, which is why it is opt-in. The group form cannot carry it: its
    // trailing fields are target names.
    internal static string FormatDirectHeal(string targetName, int amount, int castMs, uint spellId, bool withSpell)
    {
        if (withSpell && DirectHealSpellIds.Contains(spellId))
            return $"Heal/{targetName}/{amount}/{castMs}/{spellId}/";
        return $"Heal/{targetName}/{amount}/{castMs}/";
    }

    private static string? TranslateStopOutbound(string[] parts)
    {
        // S::{spellID}:{interrupted}:{guidsCSV?}
        // Only signal stop on interrupted=1; success (=0) needs no HC-1.0 message
        // because HC-1.0's natural ScheduleEvent timeout removes the prediction.
        if (parts.Length < 4) return null;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int interrupted))
            return null;
        if (interrupted == 0)
            return null;

        // Determine group vs single from optional 5th field (target list).
        // If present and contains comma → multi-target → GrpHealstop.
        if (parts.Length >= 5 && !string.IsNullOrEmpty(parts[4]) && parts[4].Contains(','))
            return "GrpHealstop";
        return "Healstop";
    }

    private static string? TranslateDelayOutbound(string[] parts)
    {
        // F::{spellID}:{startTimeRel}:{endTimeRel}
        // HC-1.0 wants milliseconds of *additional* delay since the prior
        // expected end. LHC40's startRel/endRel are seconds relative to
        // GetTime() at send. We approximate the delay as endRel-startRel
        // diff against the lib's prior known castDuration; without that
        // history, a sensible fallback is the absolute difference scaled
        // to ms. Receiver only uses this to extend its visual cast bar.
        if (parts.Length < 5) return null;
        if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float startRel))
            return null;
        if (!float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float endRel))
            return null;

        // Approximate: any forward shift of endRel is the new total duration;
        // 500ms is a defensible default delay slice when we can't infer it.
        int delayMs = (int)Math.Max(0, (endRel - startRel) * 1000f - 1500f);
        if (delayMs <= 0) delayMs = 500;
        return $"Healdelay/{delayMs}/";
    }

    private string? TranslateHotOutbound(string[] parts)
    {
        // LHC4 wire: H:{totalTicks}:{spellID}:{amount}::{tickIntervalSec}:{guidsCSV}
        // tickInterval is SECONDS, not milliseconds (verified in
        // LibHealComm-4.0.lua:1922 — `duration = totalTicks * tickInterval`
        // and `endTime = GetTime() + duration` where GetTime() returns seconds).
        // Earlier code divided by 1000 thinking ms, producing duration=0 for
        // every HoT (5 * 3 / 1000 = 0) → emitted "Renew/{name}/0/" which
        // 1.12 HC-1.0 receivers ignore.
        if (parts.Length < 7) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int totalTicks))
            return null;
        if (!uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint spellId))
            return null;
        // Float-tolerant parse: every vanilla 1.12 HoT has integer-second tick
        // intervals (Renew=3, Regrowth=3, Rejuvenation=3, Tranquility=2) and
        // Lua tostring on 3 and 3.0 both produce "3" so today's wire is always
        // integer. Future LHC4 versions could emit "1.5" for a fractional-tick
        // HoT; float parse keeps us correct without breaking the current path.
        if (!float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float tickIntervalSec))
            return null;

        if (!HotKeywordById.TryGetValue(spellId, out string? keyword))
            return null;

        var targetNames = ResolveCompressedGuidsToNames(parts[6]);
        if (targetNames.Count == 0)
            return null;

        int durationSec = (int)Math.Round(totalTicks * tickIntervalSec);
        // Single HoT message per target — HC-1.0 has no batched form.
        var sb = new StringBuilder();
        foreach (var name in targetNames)
            sb.Append(keyword).Append('/').Append(name).Append('/').Append(durationSec).Append('/');
        return sb.ToString();
    }

    // ---- Inbound: HC-1.0 (vanilla wire) → SMSG_SPELL_START synth -----------

    // Hooked before CheckAddonPrefix in HandleServerChatMessageVanilla/WotLK.
    // When prefix=="HealComm", parses the HC-1.0 body and synthesizes
    // SMSG_SPELL_START / SMSG_SPELL_FAILED_OTHER / SMSG_SPELL_DELAYED packets
    // to the modern client. Modern client's native UnitCastingInfo and
    // UnitGetIncomingHeals fire from those packets, driving any heal-prediction
    // UI (Blizzard default, Luna native, VuhDo, etc.) without addon involvement.
    // A heal the client already shows from the server's own SMSG_SPELL_START
    // (a healer in range) gets no synthesized cast: see MatchRealCast.
    //
    // Always sets prefix="_HC_DROP" / text="" so the original HC-1.0 message
    // never reaches the modern client — it's served by the synthesized packets
    // we already sent. Returns true (handled) for every HealComm-prefixed
    // message so CheckAddonPrefix filters it out cleanly downstream.
    public bool TryTranslateInboundAddon(WowGuid128 senderGuid, string senderName, ref string prefix, ref string text)
    {
        if (prefix != HcPrefix)
            return false;

        // Vanilla SMSG_MESSAGECHAT carries null-terminated CStrings. The
        // textLength includes the trailing NUL so the body arrives with
        // \0 appended. For messages with a trailing slash (Heal/.../, etc.)
        // the NUL lands in an empty trailing token and is harmless, but
        // simple messages like "Healstop" / "GrpHealstop" keep the NUL on
        // the head token and break the switch match. Strip globally.
        text = text.Replace("\0", "");

        var parts = text.Split('/');
        if (parts.Length < 1)
        {
            DropInbound(ref prefix, ref text);
            return true;
        }

        string head = parts[0];

        // Filter echo of our own outbound HC-1.0 broadcast (legacy server
        // reflects RAID/PARTY addon messages back to the sender). Without
        // this we'd synth a phantom SpellStart on top of the legacy
        // server's real SMSG_SPELL_START for the local player's cast.
        if (senderGuid == _session.GameState.CurrentPlayerGuid)
        {
            DropInbound(ref prefix, ref text);
            return true;
        }

        // Each handler returns a translated LHC40 body if applicable, or null.
        // Side effect: each handler also triggers SMSG_SPELL_START synthesis so
        // native UnitGetIncomingHeals fires regardless of whether LHC4-based
        // addons are present.
        //
        // Dual-emit rationale: native heal-prediction (driven by synth) covers
        // Blizzard default UI / ElvUI / any addon reading UnitGetIncomingHeals.
        // LHC40 wire (driven by this LHC40 translation) covers Luna, VuhDo,
        // and other LHC4-based addons that fire visuals from
        // HealComm_HealStarted callbacks instead of the native API.
        string? lhc40Body = head switch
        {
            "Heal"         => HandleInboundDirectHeal(senderGuid, parts),
            // "HealStop" is pfUI's spelling (libpredict sends it on SPELLCAST_FAILED and
            // SPELLCAST_INTERRUPTED). No 1.12 receiver matches it, pfUI's own included, so a
            // pfUI healer's cancelled heal used to stay until its timer. It also arrives stray,
            // after the heal is over, which finds nothing to stop: of 27 logged, 21 came with
            // no heal in progress and 6 during a cast the server reported failed.
            "Healstop" or "HealStop" => HandleInboundStop(senderGuid, isGroup: false),
            "Healdelay"    => HandleInboundDelay(senderGuid, parts, isGroup: false),
            "GrpHeal"      => HandleInboundGroupHeal(senderGuid, parts),
            "GrpHealstop"  => HandleInboundStop(senderGuid, isGroup: true),
            "GrpHealdelay" => HandleInboundDelay(senderGuid, parts, isGroup: true),
            // HoTs (Renew/Reju/Regr) need SMSG_AURA_UPDATE synthesis to drive
            // modern UnitBuff/UnitAura — out of scope for v1, drop quietly.
            "Renew" or "Reju" or "Regr" => null,
            // Resurrection direction B uses native UnitHasIncomingResurrection
            // driven by the legacy server's real SMSG_SPELL_START with a
            // resurrect-effect spell. Drop the addon msg so it doesn't sit
            // on the channel as noise the modern client can't parse.
            "Resurrection" => null,
            _ => null,
        };

        if (lhc40Body != null)
        {
            prefix = LhcPrefix;
            text = lhc40Body;
            return true;
        }

        DropInbound(ref prefix, ref text);
        return true;
    }

    private static void DropInbound(ref string prefix, ref string text)
    {
        // Swap to a safe-but-unknown prefix so CheckAddonPrefix filters the
        // message out cleanly without invoking modern client-side parsers.
        prefix = "_HC_DROP";
        text = "";
    }

    private string? HandleInboundDirectHeal(WowGuid128 senderGuid, string[] parts)
    {
        // HC-1.0 wire: Heal/{targetName}/{amount}/{castTimeMs}/
        // HC-1.0 castTime is MILLISECONDS (verified by HealComm-1.0 receiver:
        // ctime = (casttime/1000) + GetTime()). LHC40 D extraArg is SECONDS.
        if (parts.Length < 4) return null;
        string targetName = parts[1];
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
            return null;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int castTimeMs))
            return null;

        var targetGuid = _session.GameState.GetPlayerGuidByName(targetName);
        if (targetGuid.IsEmpty()) return null;

        var match = MatchRealCast(senderGuid, castTimeMs, out var real);
        uint spellId = real.SpellId;
        switch (match)
        {
            case RealCastMatch.Ended:
                // The client saw this cast start and end before its announcement arrived.
                return null;
            case RealCastMatch.Adopted:
                AttachNativeAmount(senderGuid, real, targetGuid, (uint)Math.Max(0, amount));
                break;
            case RealCastMatch.None:
                // A healer out of range. The spell is the one its own side named, else the
                // one its class and cast time leave no doubt about, else unknown.
                bool certain = true;
                if (!TryReadAnnouncedSpell(parts, out spellId))
                {
                    spellId = IdentifyDirectHeal(GetSenderClass(senderGuid), castTimeMs);
                    if (spellId == 0)
                    {
                        certain = false;
                        spellId = SpellHeal;
                    }
                }
                SynthesizeRemoteHealCast(senderGuid, targetGuid, spellId, castTimeMs, (uint)Math.Max(0, amount), certain, targetName);
                break;
        }

        // LHC40 D:{castSec}:{spellID}:{amount}:{guidsCSV}
        float castSec = castTimeMs / 1000f;
        return $"D:{castSec.ToString("0.###", CultureInfo.InvariantCulture)}:{spellId}:{amount}:{CompressGuid(targetGuid)}";
    }

    private string? HandleInboundGroupHeal(WowGuid128 senderGuid, string[] parts)
    {
        // HC-1.0 wire: GrpHeal/{amount}/{castTimeMs}/{name1}/{name2}/...
        if (parts.Length < 4) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
            return null;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int castTimeMs))
            return null;

        var compressedTargets = new List<string>();
        WowGuid128 firstTarget = WowGuid128.Empty;
        for (int i = 3; i < parts.Length; i++)
        {
            if (string.IsNullOrEmpty(parts[i])) continue;
            var g = _session.GameState.GetPlayerGuidByName(parts[i]);
            if (g.IsEmpty()) continue;
            if (firstTarget.IsEmpty()) firstTarget = g;
            compressedTargets.Add(CompressGuid(g));
        }
        if (firstTarget.IsEmpty()) return null;

        // SMSG_SPELL_START Target.Unit is single — modern Prayer of Healing
        // AoE cast bar renders on caster regardless of target field. Per-
        // target prediction picks the first resolvable target as the anchor.
        var match = MatchRealCast(senderGuid, castTimeMs, out var real);
        uint spellId = real.SpellId;
        switch (match)
        {
            case RealCastMatch.Ended:
                return null;
            case RealCastMatch.Adopted:
                AttachNativeAmount(senderGuid, real, firstTarget, (uint)Math.Max(0, amount));
                break;
            case RealCastMatch.None:
                // The group form is only ever Prayer of Healing (HealComm-1.0 sends it for
                // nothing else, pfUI never sends it), so the spell is known; the rank is not.
                spellId = SpellPrayerOfHealing;
                SynthesizeRemoteHealCast(senderGuid, firstTarget, spellId, castTimeMs, (uint)Math.Max(0, amount), certain: true, targetName: "");
                break;
        }

        // LHC40 D: with comma-joined target list — the lib accepts multi-target.
        float castSec = castTimeMs / 1000f;
        return $"D:{castSec.ToString("0.###", CultureInfo.InvariantCulture)}:{spellId}:{amount}:{string.Join(',', compressedTargets)}";
    }

    private string? HandleInboundStop(WowGuid128 senderGuid, bool isGroup)
    {
        // HC-1.0 Healstop carries no fields. We classify natural-completion vs
        // interrupt by comparing current time against the synth's expected end:
        // before end (with tolerance for RTT/jitter) → interrupt; after → natural.
        bool wasInterrupt;
        if (_synthCastsByCaster.TryGetValue(senderGuid, out var synth))
        {
            wasInterrupt = (DateTime.UtcNow + NaturalCompletionTolerance) < synth.ExpectedEndUtc;
        }
        else
        {
            // No active synth — Healstop arrived before any tracked Heal (rare,
            // possibly out-of-order packets). Default to interrupt-visual.
            wasInterrupt = true;
        }

        // DismissSynth emits BOTH the native SpellFailedOther AND a synthesized
        // CHAT_MSG_ADDON LHC40 S: stop, so Luna's LHC4-callback-driven bar AND
        // the native UnitGetIncomingHeals both clear from this single call.
        // Returning null tells the inbound flow to drop the original HC-1.0
        // message — we've already routed both signal paths to the client.
        DismissSynth(senderGuid, wasInterrupt ? "interrupted" : "natural_completion");
        return null;
    }

    private string? HandleInboundDelay(WowGuid128 senderGuid, string[] parts, bool isGroup)
    {
        // HC-1.0 wire: Healdelay/{delayMs}/  (delay is in milliseconds)
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int delayMs))
            return null;

        if (!_synthCastsByCaster.TryGetValue(senderGuid, out var synth))
            return null;

        SynthesizeRemoteHealDelay(senderGuid, delayMs);

        // LHC40 F::{spellID}:{startRel}:{endRel} — start/end relative to GetTime() in seconds.
        DateTime now = DateTime.UtcNow;
        float startRelSec = -(float)((synth.ExpectedEndUtc.AddMilliseconds(-delayMs) - now).TotalSeconds);
        float endRelSec = (float)((synth.ExpectedEndUtc - now).TotalSeconds);
        return $"F::{synth.SpellId}:{startRelSec.ToString("0.###", CultureInfo.InvariantCulture)}:{endRelSec.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    // ---- SMSG synthesis to modern client ----------------------------------

    // The heal a caster's addon just announced is one the client already shows: the server's own
    // SMSG_SPELL_START for it was forwarded before the announcement arrived (see _realCastsByCaster).
    // Nothing is synthesized. The heal is tracked under the spell actually being cast, so its
    // LHC40 start carries the real spell and its stop and delay are still translated; the native
    // cast is started, delayed and ended by the server's own packets (see OnRealSpellEndFromOther
    // for the end). An announcement that arrives after that cast has already ended describes
    // nothing the client still shows, and is dropped: the caller neither synthesizes nor
    // forwards it.
    private RealCastMatch MatchRealCast(WowGuid128 senderGuid, int castTimeMs, out RealCast real)
    {
        long nowMs = ClockMsForTest();
        lock (_realCastsLock)
        {
            if (!_realCastsByCaster.TryGetValue(senderGuid, out real) ||
                nowMs > real.EndMs + RealCastAnnounceGraceMs)
            {
                real = default;
                return RealCastMatch.None;
            }
            if (real.Ended)
                return RealCastMatch.Ended;
        }
        uint spellId = real.SpellId;

        // A repeated announcement of the same cast (HC-1.0 sends on RAID and BATTLEGROUND)
        // replaces its own entry, as a repeated synthesis did.
        DismissSynth(senderGuid, "superseded");

        var tracked = new SynthCast
        {
            SpellId = spellId,
            CastId = real.CastId,
            SentSpellStart = false,
            ExpectedEndUtc = DateTime.UtcNow.AddMilliseconds(castTimeMs),
        };
        _synthCastsByCaster[senderGuid] = tracked;
        ScheduleNaturalCleanup(senderGuid, tracked);

        // Per announced heal: DebugOutput only.
        if (Framework.Settings.DebugOutput)
            Log.Event("healcomm.real_cast_adopted", new
            {
                sender_low = senderGuid.GetCounter(),
                spell_id = spellId,
                cast_time = castTimeMs,
            });
        return RealCastMatch.Adopted;
    }

    // The spell a proxied healer's side appended to its announcement (the fifth field of
    // "Heal/<target>/<amount>/<castMs>/<spellId>/"): the caster's own proxy saw the cast, so
    // this is the spell, rank included. Anything that is not a direct heal is ignored.
    private static bool TryReadAnnouncedSpell(string[] parts, out uint spellId)
    {
        spellId = 0;
        return parts.Length > 4 &&
               uint.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out spellId) &&
               DirectHealSpellIds.Contains(spellId);
    }

    // JimsProxy (announced heals): the spell a single-target heal must be, given the healer's
    // class and the announced cast time, or 0 when more than one spell fits. The announced cast
    // time is the server's own (equal in all 10,451 logged heals where both were seen). Every
    // entry is a class and cast time at which one spell made up at least 99% of 36,890 logged
    // heal casts; the rank is not known, so the first rank stands for the spell.
    //   Paladin 1500 Flash of Light 15,877 of 15,877; 2500 Holy Light 2,159 of 2,159.
    //   Priest  1500 Flash Heal 6,094 of 6,143 (the rest Lesser Heal); 1400 Flash Heal 1,314 of 1,314.
    //   Druid   2500 Healing Touch 1,385 of 1,385; 3000 2,258 of 2,260; 3500 45 of 45.
    //   Shaman  2000 Healing Wave 38 of 38; 3000 150 of 150.
    // Left out as ambiguous: priest 2500 (Heal 65%, Greater Heal 27%, Lesser Heal 8%) and 3000
    // (Heal 93%), druid 2000 (Healing Touch 52%, Regrowth 48%) and 1500, shaman 1500 (Lesser
    // Healing Wave 93%) and 2500 (Healing Wave 70%, Chain Heal 30%). The announced amount does
    // not separate them: the ranges overlap.
    internal static uint IdentifyDirectHeal(Class senderClass, int castTimeMs)
    {
        return (senderClass, castTimeMs) switch
        {
            (Class.Paladin, 1500) => SpellFlashOfLight,
            (Class.Paladin, 2500) => SpellHolyLight,
            (Class.Priest, 1400 or 1500) => SpellFlashHeal,
            (Class.Druid, 2500 or 3000 or 3500) => SpellHealingTouch,
            (Class.Shaman, 2000 or 3000) => SpellHealingWave,
            _ => 0,
        };
    }

    // JimsProxy (announced heals), opt-in (Settings.HealCommNativeAmountInRange): put the
    // announced amount on a cast the server started, so the client's own incoming-heal number
    // (UnitGetIncomingHeals) has it for a healer in range.
    //
    // The client takes that number only from a cast-start packet, and the server's own start
    // went out before the healer's addon announced the amount. Read from the 1.14.2 client:
    // a second SMSG_SPELL_START with the same cast id runs the whole start again on the existing
    // cast and records the prediction on it. For another unit's cast that costs one thing, a
    // second "begins to cast" combat log line: the client keeps no cast bar and fires no cast
    // event for other units on this setup, and the running visual is neither restarted nor
    // stopped as long as the visual id is the one already sent. So the packet repeats the real
    // start (same cast id, spell and visual) with the time left as its cast time, the announced
    // target, and the heal-prediction block. JimsPlus is told first (JP_HC:R), so its own bar
    // does not start over on the repeated log line.
    //
    // The server's own SMSG_SPELL_GO or failure for the cast then removes the entry on the
    // client, the proxy forwards those already, and nothing else does: see NativeAmount for
    // what the bridge has to do about the rest.
    private void AttachNativeAmount(WowGuid128 casterGuid, RealCast real, WowGuid128 targetGuid, uint amount)
    {
        if (!(NativeAmountInRangeForTest ?? Framework.Settings.HealCommNativeAmountInRange))
            return;
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;

        long remainingMs = Math.Max(1, real.EndMs - ClockMsForTest());

        if (_session.GameState.IsJimsPlusSidebandActive())
            SendSideband($"JP_HC:R:{casterGuid.ToClientGuidString()}");

        var repeat = new SpellStart();
        repeat.Cast.CasterGUID = casterGuid;
        repeat.Cast.CasterUnit = casterGuid;
        repeat.Cast.CastID = real.CastId;
        repeat.Cast.SpellID = (int)real.SpellId;
        repeat.Cast.SpellXSpellVisualID = real.SpellVisualId;
        repeat.Cast.CastFlags = real.CastFlags | (uint)CastFlag.HealPrediction;
        repeat.Cast.CastFlagsEx = real.CastFlagsEx;
        repeat.Cast.CastTime = (uint)remainingMs;
        repeat.Cast.Target.Flags = SpellCastTargetFlags.Unit;
        repeat.Cast.Target.Unit = targetGuid;
        repeat.Cast.Predict.Points = amount;
        repeat.Cast.Predict.Type = 0;
        repeat.Cast.Predict.BeaconGUID = WowGuid128.Empty;
        worldClient.SendPacketToClient(repeat);

        // One record per cast: a repeated announcement of the same cast (another target, the
        // same message on a second channel) adds to the same cast on the client.
        var entry = new NativeAmount
        {
            Caster = casterGuid,
            CastId = real.CastId,
            SpellId = real.SpellId,
            SpellVisualId = real.SpellVisualId,
            DeadlineUtc = DateTime.UtcNow.AddMilliseconds(remainingMs) + NativeAmountCleanupGrace,
        };
        _nativeAmounts[real.CastId] = entry;
        ScheduleNativeAmountCleanup(entry);
    }

    // The server's end packet for the cast never came (or was paired to another cast): end the
    // client's entry once the cast is well past its expected end.
    private void ScheduleNativeAmountCleanup(NativeAmount entry)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    TimeSpan wait = entry.DeadlineUtc - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait).ConfigureAwait(false);
                    if (!_nativeAmounts.TryGetValue(entry.CastId, out var current) || !ReferenceEquals(current, entry))
                        return;
                    if (entry.DeadlineUtc > DateTime.UtcNow)
                        continue; // pushed back while waiting
                    EndNativeAmount(entry);
                    return;
                }
            }
            catch { }
        });
    }

    // Ends the client's incoming-heal entry for a cast the server started, when the server's
    // own end packet will not do it. The client acts on this only while the caster's unit exists.
    private void EndNativeAmount(NativeAmount entry)
    {
        if (_nativeAmounts.TryRemove(new KeyValuePair<WowGuid128, NativeAmount>(entry.CastId, entry)))
            SendCastEnd(entry.Caster, entry.CastId, entry.SpellId, entry.SpellVisualId);
    }

    // SMSG_SPELL_FAILED_OTHER for a cast the bridge put on the client or added a prediction to.
    // For another unit the client fires no event and plays nothing for it; it ends that cast's
    // visual and removes its incoming-heal entries, provided the reason is not zero (which
    // reason is otherwise immaterial for another unit) and the caster's unit exists.
    private void SendCastEnd(WowGuid128 casterGuid, WowGuid128 castId, uint spellId, uint spellVisualId)
    {
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;

        var failed = new SpellFailedOther();
        failed.CasterUnit = casterGuid;
        failed.CastID = castId;
        failed.SpellID = spellId;
        failed.SpellXSpellVisualID = spellVisualId;
        failed.Reason = 27;
        worldClient.SendPacketToClient(failed);
    }

    // JimsProxy (announced heals): the caster's unit is about to leave the client (destroyed or
    // out of range). Called before that reaches the client, because a failure can remove an
    // incoming-heal entry only while the caster's unit exists, and nothing removes it after.
    public void OnUnitLeavingView(WowGuid128 guid)
    {
        if (!guid.IsPlayer())
            return;

        if (!_nativeAmounts.IsEmpty)
        {
            foreach (var entry in _nativeAmounts.Values)
            {
                if (entry.Caster == guid)
                    EndNativeAmount(entry);
            }
        }

        // A synthesized cast keeps being tracked: the heal goes on, and LibHealComm-4.0 and
        // JimsPlus need no unit. Only the client's own entry has to go.
        if (_synthCastsByCaster.TryGetValue(guid, out var synth) && synth.SentSpellStart)
            SendCastEnd(guid, synth.CastId, synth.SpellId, synth.SpellVisualId);
    }

    private Class GetSenderClass(WowGuid128 senderGuid)
    {
        return _session.GameState.CachedPlayers.TryGetValue(senderGuid, out var cached) ? cached.ClassId : Class.None;
    }

    // `certain`: the spell is the healer's own word or follows from class and cast time; false
    // for the Unknown Heal stand-in. `targetName` is empty for a group heal.
    private void SynthesizeRemoteHealCast(WowGuid128 senderGuid, WowGuid128 targetGuid, uint spellId, int castTimeMs, uint predictedAmount, bool certain, string targetName)
    {
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;

        var gameState = _session.GameState;
        if (gameState.CurrentMapId == null) return;

        // Dismiss any prior synth for this caster — covers rapid re-cast
        // (cancel + re-queue) where HC-1.0 doesn't always emit a stop.
        DismissSynth(senderGuid, "superseded");

        uint sequence = (uint)Interlocked.Increment(ref gameState.OtherCastSequenceCounter);
        ulong uniqueLow = ((ulong)sequence << 32) | (uint)(spellId + senderGuid.GetCounter());
        var castId = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal,
            (uint)gameState.CurrentMapId, spellId, uniqueLow);
        uint visualId = GameData.GetSpellVisual(spellId);

        var spell = new SpellStart();
        spell.Cast.CasterGUID = senderGuid;
        spell.Cast.CasterUnit = senderGuid;
        spell.Cast.CastID = castId;
        spell.Cast.SpellID = (int)spellId;
        spell.Cast.SpellXSpellVisualID = visualId;
        // CastFlag.HealPrediction (0x40000000) tells the modern client to read
        // the SpellHealPrediction block. Without it, UnitGetIncomingHeals
        // returns 0 even when Predict.Points is populated, so the heal-
        // prediction bar on the target frame stays empty.
        spell.Cast.CastFlags = (uint)CastFlag.HealPrediction;
        spell.Cast.CastFlagsEx = 0;
        spell.Cast.CastTime = (uint)castTimeMs;
        spell.Cast.Target.Flags = SpellCastTargetFlags.Unit;
        spell.Cast.Target.Unit = targetGuid;
        // Heal-prediction payload. Type=0 is direct heal (vs HoT/channel/bomb).
        // BeaconGUID stays empty — Beacon of Light retransmission is paladin-
        // local mechanic, not relevant for cross-version proxied predictions.
        spell.Cast.Predict.Points = predictedAmount;
        spell.Cast.Predict.Type = 0;
        spell.Cast.Predict.BeaconGUID = WowGuid128.Empty;
        worldClient.SendPacketToClient(spell);

        // JimsProxy (announced heals): what the combat log line for this cast cannot say. The
        // JimsPlus bar takes its cast from this instead: the exact cast time, who is being
        // healed, and whether the spell is known ("Flash of Light → Tank") or not ("Unknown
        // Heal → Tank"). Sent after the start, like JP_CS; the addon accepts either order.
        bool sideband = gameState.IsJimsPlusSidebandActive();
        if (sideband)
            SendSideband($"JP_HC:S:{senderGuid.ToClientGuidString()}:{spellId}:{castTimeMs}:{(certain ? 1 : 0)}:{targetName}");

        var synth = new SynthCast
        {
            SpellId = spellId,
            CastId = castId,
            SpellVisualId = visualId,
            ExpectedEndUtc = DateTime.UtcNow.AddMilliseconds(castTimeMs),
            SentSpellStart = true,
            SentSideband = sideband,
        };
        _synthCastsByCaster[senderGuid] = synth;

        // Some 1.12 HealComm-1.0 forks only emit Healstop on interrupt, not on
        // success. Without a terminal signal, the modern client's heal-prediction
        // tracker holds the pending cast forever — Luna's bar fills and never
        // drains. Schedule a fallback cleanup at expected-end + tolerance: if no
        // Healstop arrives by then, fire a natural-completion dismiss ourselves.
        // If a Healstop or supersede DOES arrive first, the cleanup becomes a
        // no-op (the tracked entry is no longer this one — we check before dismissing).
        ScheduleNaturalCleanup(senderGuid, synth);
    }

    private void SendSideband(string line)
    {
        _session.WorldClient?.SendPacketToClient(new ChatPkt(_session, ChatMessageTypeModern.System, line));
    }

    private void ScheduleNaturalCleanup(WowGuid128 senderGuid, SynthCast scheduledFor)
    {
        // Wake slightly after expected end so the visual cast bar drains to
        // zero before we send the dismiss. NaturalCompletionTolerance buys
        // headroom for RTT/scheduler jitter.
        //
        // The expected end moves when the cast is pushed back (Healdelay), so it is read again
        // on waking. The timer used to be set once from the announced cast time: a pushed-back
        // heal lost its prediction that much early (logged: gone 1.2 s before the heal landed).
        _ = Task.Run(async () =>
        {
            // Swallow: fire-and-forget timer; DismissSynth can throw if the
            // world connection dropped between the lookup and SendPacketToClient.
            // Without the catch, the exception becomes an unobserved task
            // exception on the timer thread.
            try
            {
                while (true)
                {
                    TimeSpan wait = scheduledFor.ExpectedEndUtc + NaturalCompletionTolerance - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait).ConfigureAwait(false);
                    // By instance, not CastId: an adopted real cast has no CastId of its own.
                    if (!_synthCastsByCaster.TryGetValue(senderGuid, out var synth) || !ReferenceEquals(synth, scheduledFor))
                        return;
                    if (scheduledFor.ExpectedEndUtc + NaturalCompletionTolerance > DateTime.UtcNow)
                        continue; // pushed back while waiting
                    DismissSynth(senderGuid, "natural_completion_timeout");
                    return;
                }
            }
            catch { }
        });
    }

    private void SynthesizeRemoteHealDelay(WowGuid128 senderGuid, int delayMs)
    {
        if (!_synthCastsByCaster.TryGetValue(senderGuid, out var synth))
            return;
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;

        // An adopted real cast is delayed on the client by the server's own SMSG_SPELL_DELAYED.
        if (synth.SentSpellStart)
        {
            var delayed = new SpellDelayed();
            delayed.CasterGUID = senderGuid;
            delayed.Delay = delayMs;
            worldClient.SendPacketToClient(delayed);
        }
        if (synth.SentSideband)
            SendSideband($"JP_HC:D:{senderGuid.ToClientGuidString()}:{delayMs}");

        synth.ExpectedEndUtc = synth.ExpectedEndUtc.AddMilliseconds(delayMs);
        if (!synth.SentSpellStart && _nativeAmounts.TryGetValue(synth.CastId, out var native))
            native.DeadlineUtc = native.DeadlineUtc.AddMilliseconds(delayMs);
    }

    private void DismissSynth(WowGuid128 senderGuid, string reasonTag)
    {
        if (!_synthCastsByCaster.TryRemove(senderGuid, out var synth)) return;
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;

        // Native dismiss: SpellFailedOther removes the cast's incoming-heal entry
        // (UnitGetIncomingHeals) on the client. Only for a cast we synthesized: an
        // adopted real cast ends on the client through the server's own SPELL_GO or
        // failure, and so does an amount attached to it (see NativeAmount).
        if (synth.SentSpellStart)
            SendCastEnd(senderGuid, synth.CastId, synth.SpellId, synth.SpellVisualId);

        // The JimsPlus bar for a synthesized cast ends the same way. 1: stopped early by the
        // healer. 2: the server's own cast start for this healer follows, which replaces the
        // bar. 0: ran its course. A replaced announcement says nothing: its successor follows.
        if (synth.SentSideband && reasonTag != "superseded")
        {
            int how = reasonTag == "interrupted" ? 1 : reasonTag == "real_spell_start_arrived" ? 2 : 0;
            SendSideband($"JP_HC:X:{senderGuid.ToClientGuidString()}:{how}");
        }

        // LHC4-callback dismiss: SpellFailedOther doesn't reach LibHealComm-4.0
        // (it watches CHAT_MSG_ADDON, not native cast packets). Luna and other
        // LHC4-callback-driven addons render their heal-prediction bar from
        // HealComm_HealStarted / HealComm_HealStopped callbacks — without an
        // LHC40 S: addon msg, the bar stays visible forever even after we've
        // cleared the native pending heal. Synthesize an inbound CHAT_MSG_ADDON
        // here so LHC4 fires HealComm_HealStopped → Luna's element updates.
        // Interrupt-visual flag: only "interrupted" (real Healstop mid-cast) and
        // "real_cast_failed" (the server's failure for the cast) get the X mark;
        // superseded / natural_completion_timeout are clean completions and
        // shouldn't show interrupt UI.
        bool isInterruptVisual = reasonTag is "interrupted" or "real_cast_failed";
        EmitLhc40StopToClient(senderGuid, synth.SpellId, isInterruptVisual);
    }

    // Synthesize an inbound CHAT_MSG_ADDON to the modern client. Used to feed
    // LibHealComm-4.0 stop signals on dismiss paths that aren't driven by a
    // real HC-1.0 Healstop arrival (auto-cleanup timer, supersede, real
    // SPELL_START dedupe).
    //
    // CRITICAL: the modern client wire encoding for addon messages uses
    // Language.AddonBfA (183), NOT Language.Addon (uint.MaxValue) which is the
    // legacy/internal sentinel. ChatPkt.CheckAddonPrefix performs this
    // transform on the inbound translation path; we must do it manually here
    // because we're constructing ChatPkt directly. Wrong language value makes
    // the modern client render the addon body as visible chat text AND drops
    // it from LHC4's CHAT_MSG_ADDON handler — both visible-leak and
    // not-clearing-Luna-bar are the same bug.
    private void EmitLhc40StopToClient(WowGuid128 senderGuid, uint spellId, bool isInterrupted)
    {
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;

        string body = $"S::{spellId}:{(isInterrupted ? 1 : 0)}:";
        var chatType = GroupAddonChatType();

        // ChatPkt resolves senderName from senderGuid internally (line 385 in
        // ChatPackets.cs), so we can pass empty senderName.
        var chat = new ChatPkt(_session, chatType, body, (uint)Language.AddonBfA,
            senderGuid, "", default, "", "", ChatFlags.None, LhcPrefix);
        worldClient.SendPacketToClient(chat);
    }

    // Called from HandleSpellStart when the legacy server's REAL SMSG_SPELL_START
    // arrives for a remote caster. If we have an active synth for this caster,
    // dismiss it so the modern client doesn't render two overlapping cast bars
    // (synth + real). Don't gate on spellId equality — synth carries the first
    // rank of the identified spell (e.g. 635 Holy Light Rank 1) or the Unknown
    // Heal stand-in, while the real cast carries the actual rank, so a strict
    // match would mostly fail. Vanilla emits at most one heal cast at a time per
    // caster anyway, so dismissing on any tracked-caster real-cast arrival is safe.
    //
    // That is the rare order (the healer came into range mid-cast). The usual one
    // is the reverse — real start first, announcement after — so a player's
    // cast-time cast is also remembered here for MatchRealCast.
    public void OnRealSpellStartFromOther(WowGuid128 casterGuid, uint spellId, uint castTimeMs, WowGuid128 castId,
        uint spellVisualId = 0, uint castFlags = 0, uint castFlagsEx = 0)
    {
        if (_synthCastsByCaster.ContainsKey(casterGuid))
            DismissSynth(casterGuid, "real_spell_start_arrived");

        // One cast at a time: an amount still attached to an earlier cast of this caster is
        // left over (that cast's end was not reported, or its failure has yet to arrive as an
        // echo), and the client does not remove it for a new start.
        if (!_nativeAmounts.IsEmpty)
        {
            foreach (var entry in _nativeAmounts.Values)
            {
                if (entry.Caster == casterGuid && entry.CastId != castId)
                    EndNativeAmount(entry);
            }
        }

        // Instants are never announced as a heal, and only players run HealComm.
        if (castTimeMs == 0 || !casterGuid.IsPlayer())
            return;

        long nowMs = ClockMsForTest();
        lock (_realCastsLock)
        {
            if (_realCastsByCaster.Count >= RealCastSweepThreshold)
            {
                foreach (var entry in _realCastsByCaster)
                {
                    if (nowMs > entry.Value.EndMs + RealCastAnnounceGraceMs)
                        _realCastSweepScratch.Add(entry.Key);
                }
                foreach (var expired in _realCastSweepScratch)
                    _realCastsByCaster.Remove(expired);
                _realCastSweepScratch.Clear();
            }
            _realCastsByCaster[casterGuid] = new RealCast(spellId, castId, nowMs + castTimeMs, spellVisualId, castFlags, castFlagsEx);
        }
    }

    // JimsProxy (announced heals): the server's own SMSG_SPELL_GO (`completed`) or failure for
    // another player's cast, with the cast id the proxy paired that packet to. For a healer in
    // range this is the end of the heal, and it is what the proxy goes by: the heal's
    // prediction is taken down here instead of waiting for the healer's addon to say so. The
    // addon's word arrives a round trip later, in a spelling the bridge may not know, or never
    // (HealComm-1.0 sends no stop for a completed heal), and until then LibHealComm-4.0 kept
    // showing a heal that had landed or been cancelled.
    //
    // The cast id matters. A 1.12 failure names only the caster and the spell, and the server
    // sends the same packets for two other things besides "this cast was interrupted":
    //  - the late echo of an earlier cast of the same spell. The proxy pairs that to the earlier
    //    cast, so its id is not the tracked heal's and nothing ends. (46 of 10,660 logged heal
    //    casts, 44 of them one healer's, about 80 ms into the next cast. Going by caster and
    //    spell alone would have taken those predictions down for the whole cast.)
    //  - a second cast request refused while the first is still casting. That one does pair to
    //    the live cast and cannot be told from an interrupt when it arrives: 26 of the 1,271
    //    failures paired to a live heal were followed by the heal firing. 23 came within 270 ms
    //    of the cast's end (the next heal pressed early) and cost that much prediction; 3 came
    //    in the first 250 ms of the cast and cost the rest of it. Not believing a failure that
    //    early or that late would misread more casts than it saves: 42 real cancels fall in
    //    the first 250 ms and 208 in the last 300.
    public void OnRealSpellEndFromOther(WowGuid128 casterGuid, uint spellId, WowGuid128 castId, bool completed)
    {
        if (!casterGuid.IsPlayer())
            return;

        long nowMs = ClockMsForTest();
        lock (_realCastsLock)
        {
            // Remembered as ended, so an announcement still on its way is not taken for a
            // cast the client is showing (nor, with nothing remembered, for a healer out of
            // range, which would synthesize a cast that is already over).
            if (_realCastsByCaster.TryGetValue(casterGuid, out var real) && !real.Ended && real.CastId == castId)
                _realCastsByCaster[casterGuid] = real.AsEnded(nowMs);
        }

        // The packet being forwarded removes the client's incoming-heal entry for this cast.
        _nativeAmounts.TryRemove(castId, out _);

        if (!_synthCastsByCaster.TryGetValue(casterGuid, out var tracked))
            return;
        if (!tracked.SentSpellStart)
        {
            // An adopted cast: it ends when the server's cast it stands for does.
            if (tracked.CastId == castId)
                DismissSynth(casterGuid, completed ? "real_cast_completed" : "real_cast_failed");
        }
        // A synthesized cast, and the healer has come into range since it started: the server
        // sent no start for that heal, so there is no cast id to go by. A heal of theirs
        // completing is that heal. A failure is left to the healer's own stop: it cannot be
        // told from the echo of an earlier cast.
        else if (completed && (DirectHealSpellIds.Contains(spellId) || GroupHealSpellIds.Contains(spellId)))
        {
            DismissSynth(casterGuid, "real_cast_completed");
        }
    }

    internal int RealCastCountForTest
    {
        get { lock (_realCastsLock) return _realCastsByCaster.Count; }
    }

    // ---- Resurrection direction A (we cast a rez, 1.12 natives see it) -----

    public bool IsResurrectionSpell(uint spellId) => ResurrectionSpellIds.Contains(spellId);

    // Builds the HC-1.0 outbound payload for "local player started a
    // resurrection cast on {target}". Caller is responsible for sending
    // it through the legacy chat send path on the appropriate channel
    // (RAID if in raid, PARTY if in party, drop if solo). Returns null
    // if we couldn't resolve the target name (rez on non-player corpse?
    // shouldn't happen but guard anyway).
    public string? BuildResurrectionStartPayload(uint spellId, WowGuid128 targetGuid)
    {
        string targetName = _session.GameState.GetPlayerName(targetGuid);
        if (string.IsNullOrEmpty(targetName))
            return null;

        return $"Resurrection/{targetName}/start/";
    }

    public string BuildResurrectionStopPayload(uint spellId)
    {
        return "Resurrection/stop/";
    }

    // Hook called from HandleSpellStart when the SMSG_SPELL_START caster is
    // the local player. Fires HC-1.0 "Resurrection/{name}/start/" on the
    // RAID/PARTY chat channel so 1.12-native HealComm-1.0 listeners light
    // up their incoming-rez indicator on the corpse's unit frame. Modern
    // peers ignore the "HealComm" prefix (they read "LHC40"), so this is
    // additive — no impact on modern↔modern flows.
    public void OnLocalPlayerSpellStart(uint spellId, WowGuid128 targetGuid)
    {
        if (!IsResurrectionSpell(spellId)) return;
        if (targetGuid.IsEmpty()) return;

        string? payload = BuildResurrectionStartPayload(spellId, targetGuid);
        if (payload == null) return;

        EmitToServer(HcPrefix, payload);
        // Track so a subsequent failure emits stop too. Reusing the same
        // pending-cast cache keeps the lifecycle bookkeeping in one place.
        var localGuid = _session.GameState.CurrentPlayerGuid;
        if (!localGuid.IsEmpty())
        {
            _pendingByCaster[localGuid] = new PendingCast
            {
                SpellId = spellId,
                IsGroup = false,
                StartUtc = DateTime.UtcNow,
                EndUtc = DateTime.UtcNow.AddSeconds(10),
            };
        }
    }

    // Hook called from spell-failure / cast-failed paths when the failing
    // caster is the local player. Emits HC-1.0 stop only if the failing
    // spell matches our tracked rez. Earlier code did unconditional
    // TryRemove which silently consumed the rez tracker on any non-rez
    // spell fail (extremely common — denied Holy Light, OOM Heal, etc.) —
    // so the real rez interrupt never emitted a stop, leaving 1.12 Luna's
    // incoming-rez indicator stuck.
    public void OnLocalPlayerSpellStop(uint spellId)
    {
        var localGuid = _session.GameState.CurrentPlayerGuid;
        if (localGuid.IsEmpty()) return;
        if (!_pendingByCaster.TryGetValue(localGuid, out var pending)) return;
        if (pending.SpellId != spellId) return;
        if (!IsResurrectionSpell(pending.SpellId)) return;

        // Atomic: remove only if the entry is still the same one we peeked.
        if (!_pendingByCaster.TryRemove(new KeyValuePair<WowGuid128, PendingCast>(localGuid, pending)))
            return;

        string payload = BuildResurrectionStopPayload(pending.SpellId);
        EmitToServer(HcPrefix, payload);
    }

    // Hook called from SMSG_SPELL_GO when the casting unit is the local
    // player. Marks the rez as naturally completed — clear the pending
    // tracker WITHOUT emitting HC-1.0 stop. Luna and other 1.12-native
    // HC-1.0 receivers clear their incoming-rez indicator automatically
    // when the target's "Resurrection Request" buff applies via the normal
    // SPELL_AURA_APPLIED path, so no stop is needed for natural completion;
    // emitting one would clear the indicator a frame before the buff lands
    // and cause a visible flicker on the corpse.
    //
    // The bridge's PendingCast.EndUtc is set on rez start (10s) but never
    // read — explicit cleanup here means we don't have to add a TTL sweep,
    // and stale entries can't accumulate across multiple successful rezzes
    // in the same session.
    public void OnLocalPlayerSpellCompleted(uint spellId)
    {
        var localGuid = _session.GameState.CurrentPlayerGuid;
        if (localGuid.IsEmpty()) return;
        if (!_pendingByCaster.TryGetValue(localGuid, out var pending)) return;
        if (pending.SpellId != spellId) return;
        if (!IsResurrectionSpell(pending.SpellId)) return;

        _pendingByCaster.TryRemove(new KeyValuePair<WowGuid128, PendingCast>(localGuid, pending));
    }

    // Send a synthesized addon message to the legacy server on the
    // appropriate chat channel for the current group context. Legacy
    // server then broadcasts it to all party/raid members. Silently
    // skips when there's no group (solo cast → no peers to notify).
    private void EmitToServer(string prefix, string body)
    {
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;

        string text = prefix + '\t' + body;
        uint addonLanguage = (uint)Language.Addon;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            // WotLK/TBC chat type: prefer Raid; if not in a raid, Party.
            // No reliable solo guard — server will reject "no group" with
            // a chat error we ignore.
            ChatMessageTypeWotLK chatType = IsInRaid() ? ChatMessageTypeWotLK.Raid : ChatMessageTypeWotLK.Party;
            worldClient.SendMessageChatWotLK(chatType, addonLanguage, text, "", "");
        }
        else
        {
            ChatMessageTypeVanilla chatType = IsInRaid() ? ChatMessageTypeVanilla.Raid : ChatMessageTypeVanilla.Party;
            worldClient.SendMessageChatVanilla(chatType, addonLanguage, text, "", "");
        }
    }

    // Detect raid vs party from cached group state. CurrentGroups is a
    // length-2 array: [0] is the home group, [1] is the battleground group.
    // Raid context is encoded in the home group's PartyFlags (Raid | FakeRaid),
    // NOT by which array slot is populated. Earlier code checked slot 1 which
    // only fires inside BGs and never for an actual 5-40 player raid — net
    // effect was the bridge sent emit on Party channel during raids (server
    // rejects "no party") and broke heal-prediction inside the headline use
    // case.
    // The chat type of an addon message the bridge makes for the client. LibHealComm-4.0 drops
    // a message whose channel is not the one it is sending on itself, and the 1.14.2 client
    // names the channel from the chat type: party and raid by their own types, "INSTANCE_CHAT"
    // only for the battleground types (0x3E, 0x3F). In a battleground the library is on
    // INSTANCE_CHAT, so a stop sent as party or raid chat was dropped there, and a direct heal
    // it has recorded never expires on its own.
    private ChatMessageTypeModern GroupAddonChatType()
    {
        return GroupAddonChatType(_session.GameState.IsInBattleground(), IsInRaid());
    }

    internal static ChatMessageTypeModern GroupAddonChatType(bool inBattleground, bool inRaid)
    {
        if (inBattleground)
            return ChatMessageTypeModern.Battleground;
        return inRaid ? ChatMessageTypeModern.Raid : ChatMessageTypeModern.Party;
    }

    private bool IsInRaid()
    {
        var groups = _session.GameState.CurrentGroups;
        if (groups == null || groups.Length == 0) return false;
        var home = groups[0];
        if (home == null) return false;
        return (home.PartyFlags & GroupFlags.MaskBgRaid) != 0;
    }

    // ---- Helpers -----------------------------------------------------------

    // Resolves a comma-separated list of LHC40-compressed GUIDs into player
    // names via the proxy's roster cache. Skips non-player and unresolvable
    // entries. Empty list signals a translation we can't complete.
    private List<string> ResolveCompressedGuidsToNames(string compressedCsv)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(compressedCsv))
            return result;

        foreach (var token in compressedCsv.Split(','))
        {
            if (string.IsNullOrEmpty(token))
                continue;
            // Pet GUIDs ("p-...") would need a different format; HC-1.0
            // doesn't model pet heals well anyway. Skip them in v1.
            if (token.StartsWith("p-"))
                continue;
            var guid = DecompressPlayerGuid(token);
            if (guid.IsEmpty())
                continue;
            string name = _session.GameState.GetPlayerName(guid);
            if (!string.IsNullOrEmpty(name))
                result.Add(name);
        }
        return result;
    }

    // LHC40 strips "Player-" prefix and keeps "{realmId}-{counter:X8}".
    // Decompose to find a player WowGuid128 in our roster matching that
    // counter. Realm ID is informational here — within a single session
    // we only see one realm, so the counter is sufficient.
    private WowGuid128 DecompressPlayerGuid(string compressed)
    {
        int dash = compressed.LastIndexOf('-');
        if (dash <= 0 || dash >= compressed.Length - 1)
            return WowGuid128.Empty;
        string counterHex = compressed.Substring(dash + 1);
        if (!ulong.TryParse(counterHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong counter))
            return WowGuid128.Empty;

        // Walk the cached players for a matching low GUID. Cheap because
        // raid/party rosters are at most ~40 entries.
        foreach (var entry in _session.GameState.CachedPlayers)
        {
            if (entry.Key.IsPlayer() && entry.Key.GetCounter() == counter)
                return entry.Key;
        }
        return WowGuid128.Empty;
    }

    // Compress a player WowGuid128 into LHC40 wire form: "{realmId}-{counter:X8}".
    private static string CompressGuid(WowGuid128 guid)
    {
        return $"{guid.GetRealmId()}-{guid.GetCounter():X8}";
    }
}

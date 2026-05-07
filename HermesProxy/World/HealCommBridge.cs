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
    private const uint SpellGreaterHeal = 2060;       // Priest direct
    private const uint SpellHealingTouch = 5185;      // Druid direct
    private const uint SpellHolyLight = 635;          // Paladin direct
    private const uint SpellHealingWave = 331;        // Shaman direct

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
    private sealed class SynthCast
    {
        public uint SpellId;
        public WowGuid128 CastId;
        public uint SpellVisualId;
        public DateTime ExpectedEndUtc;
    }

    private readonly ConcurrentDictionary<WowGuid128, SynthCast> _synthCastsByCaster = new();

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
        {
            Log.Event("healcomm.outbound.skipped", new
            {
                reason = "unsupported_lhc40_type",
                comm_type = commType,
            });
            return false;
        }

        Log.Event("healcomm.outbound.translated", new
        {
            comm_type = commType,
            from_prefix = prefix,
            to_prefix = HcPrefix,
            payload = translated,
        });

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

        // Single-target direct heal → Heal/{targetName}/{amount}/{castMs}/
        return $"Heal/{targetNames[0]}/{amount}/{castMs}/";
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
        if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tickIntervalSec))
            return null;

        if (!HotKeywordById.TryGetValue(spellId, out string? keyword))
            return null;

        var targetNames = ResolveCompressedGuidsToNames(parts[6]);
        if (targetNames.Count == 0)
            return null;

        int durationSec = totalTicks * tickIntervalSec;
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
            Log.Event("healcomm.inbound.skipped", new
            {
                reason = "echo_self",
                head,
                payload = text,
            });
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
            "Healstop"     => HandleInboundStop(senderGuid, isGroup: false),
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
            Log.Event("healcomm.inbound.translated", new
            {
                head,
                sender = senderName,
                from_prefix = prefix,
                to_prefix = LhcPrefix,
                payload = lhc40Body,
            });
            prefix = LhcPrefix;
            text = lhc40Body;
            return true;
        }

        // Distinguish "head was known and handled via synth side-effects"
        // (Healstop / Healdelay routed through DismissSynth / SpellDelayed
        // packets) from genuinely unsupported types (Renew/Reju/Regr/Resurrection
        // dropped by design, or unknown heads).
        bool isKnownSynthHandled = head is "Healstop" or "GrpHealstop" or "Healdelay" or "GrpHealdelay";
        Log.Event("healcomm.inbound.skipped", new
        {
            head,
            sender = senderName,
            payload = text,
            reason = isKnownSynthHandled ? "synth_handled_no_forward" : "unrecognized_or_unsupported",
        });
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

        uint spellId = GuessDirectHealSpellId(senderGuid);
        SynthesizeRemoteHealCast(senderGuid, targetGuid, spellId, castTimeMs, (uint)Math.Max(0, amount));

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
        SynthesizeRemoteHealCast(senderGuid, firstTarget, SpellPrayerOfHealing, castTimeMs, (uint)Math.Max(0, amount));

        // LHC40 D: with comma-joined target list — the lib accepts multi-target.
        float castSec = castTimeMs / 1000f;
        return $"D:{castSec.ToString("0.###", CultureInfo.InvariantCulture)}:{SpellPrayerOfHealing}:{amount}:{string.Join(',', compressedTargets)}";
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

    private void SynthesizeRemoteHealCast(WowGuid128 senderGuid, WowGuid128 targetGuid, uint spellId, int castTimeMs, uint predictedAmount)
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

        _synthCastsByCaster[senderGuid] = new SynthCast
        {
            SpellId = spellId,
            CastId = castId,
            SpellVisualId = visualId,
            ExpectedEndUtc = DateTime.UtcNow.AddMilliseconds(castTimeMs),
        };

        // Some 1.12 HealComm-1.0 forks only emit Healstop on interrupt, not on
        // success. Without a terminal signal, the modern client's heal-prediction
        // tracker holds the pending cast forever — Luna's bar fills and never
        // drains. Schedule a fallback cleanup at expected-end + tolerance: if no
        // Healstop arrives by then, fire a natural-completion dismiss ourselves.
        // If a Healstop or supersede DOES arrive first, the cleanup becomes a
        // no-op (the synth's CastId won't match — we check before dismissing).
        ScheduleNaturalCleanup(senderGuid, castId, castTimeMs);

        Log.Event("healcomm.synth.spell_start", new
        {
            caster = senderGuid.ToString(),
            target = targetGuid.ToString(),
            spell_id = spellId,
            cast_time_ms = castTimeMs,
            predicted_amount = predictedAmount,
            cast_id_counter = castId.GetCounter(),
        });
    }

    private void ScheduleNaturalCleanup(WowGuid128 senderGuid, WowGuid128 castId, int castTimeMs)
    {
        // Wake slightly after expected end so the visual cast bar drains to
        // zero before we send the dismiss. NaturalCompletionTolerance buys
        // headroom for RTT/scheduler jitter.
        int delayMs = castTimeMs + (int)NaturalCompletionTolerance.TotalMilliseconds;
        Task.Delay(delayMs).ContinueWith(_ =>
        {
            try
            {
                if (_synthCastsByCaster.TryGetValue(senderGuid, out var synth) && synth.CastId == castId)
                    DismissSynth(senderGuid, "natural_completion_timeout");
            }
            catch (Exception ex)
            {
                Log.Event("healcomm.synth.cleanup_error", new
                {
                    caster = senderGuid.ToString(),
                    error = ex.Message,
                });
            }
        }, TaskScheduler.Default);
    }

    private void SynthesizeRemoteHealDelay(WowGuid128 senderGuid, int delayMs)
    {
        if (!_synthCastsByCaster.TryGetValue(senderGuid, out var synth))
            return;
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;

        var delayed = new SpellDelayed();
        delayed.CasterGUID = senderGuid;
        delayed.Delay = delayMs;
        worldClient.SendPacketToClient(delayed);

        synth.ExpectedEndUtc = synth.ExpectedEndUtc.AddMilliseconds(delayMs);

        Log.Event("healcomm.synth.delayed", new
        {
            caster = senderGuid.ToString(),
            delay_ms = delayMs,
            spell_id = synth.SpellId,
        });
    }

    private void DismissSynth(WowGuid128 senderGuid, string reasonTag)
    {
        if (!_synthCastsByCaster.TryRemove(senderGuid, out var synth)) return;
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;

        // Native dismiss: SpellFailedOther clears UnitGetIncomingHeals and the
        // cast bar on observers' frames. Reason 27 is INTERRUPTED — generic
        // enough that the client just clears state without surfacing a
        // player-facing error (errors only render for local-player SpellFailure).
        var failed = new SpellFailedOther();
        failed.CasterUnit = senderGuid;
        failed.CastID = synth.CastId;
        failed.SpellID = synth.SpellId;
        failed.SpellXSpellVisualID = synth.SpellVisualId;
        failed.Reason = 27;
        worldClient.SendPacketToClient(failed);

        // LHC4-callback dismiss: SpellFailedOther doesn't reach LibHealComm-4.0
        // (it watches CHAT_MSG_ADDON, not native cast packets). Luna and other
        // LHC4-callback-driven addons render their heal-prediction bar from
        // HealComm_HealStarted / HealComm_HealStopped callbacks — without an
        // LHC40 S: addon msg, the bar stays visible forever even after we've
        // cleared the native pending heal. Synthesize an inbound CHAT_MSG_ADDON
        // here so LHC4 fires HealComm_HealStopped → Luna's element updates.
        // Interrupt-visual flag: only "interrupted" (real Healstop mid-cast)
        // gets the X mark; superseded / natural_completion_timeout are clean
        // completions and shouldn't show interrupt UI.
        bool isInterruptVisual = reasonTag == "interrupted";
        EmitLhc40StopToClient(senderGuid, synth.SpellId, isInterruptVisual);

        Log.Event("healcomm.synth.dismissed", new
        {
            caster = senderGuid.ToString(),
            spell_id = synth.SpellId,
            cast_id_counter = synth.CastId.GetCounter(),
            reason = reasonTag,
            lhc40_interrupt_flag = isInterruptVisual,
        });
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
        var chatType = IsInRaid() ? ChatMessageTypeModern.Raid : ChatMessageTypeModern.Party;

        // ChatPkt resolves senderName from senderGuid internally (line 385 in
        // ChatPackets.cs), so we can pass empty senderName.
        var chat = new ChatPkt(_session, chatType, body, (uint)Language.AddonBfA,
            senderGuid, "", default, "", "", ChatFlags.None, LhcPrefix);
        worldClient.SendPacketToClient(chat);
    }

    // Called from HandleSpellStart when the legacy server's REAL SMSG_SPELL_START
    // arrives for a remote caster. If we have an active synth for this caster +
    // spell, dismiss the synth first so the modern client doesn't render two
    // overlapping cast bars (the synthesized one + the real one).
    public void OnRealSpellStartFromOther(WowGuid128 casterGuid, uint spellId)
    {
        if (_synthCastsByCaster.TryGetValue(casterGuid, out var synth) && synth.SpellId == spellId)
            DismissSynth(casterGuid, "real_spell_start_arrived");
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
        {
            Log.Event("healcomm.rez.start.no_target_name", new
            {
                spell_id = spellId,
                target_guid = targetGuid.ToString(),
            });
            return null;
        }

        Log.Event("healcomm.rez.start.synthesized", new
        {
            spell_id = spellId,
            target_name = targetName,
        });
        return $"Resurrection/{targetName}/start/";
    }

    public string BuildResurrectionStopPayload(uint spellId)
    {
        Log.Event("healcomm.rez.stop.synthesized", new
        {
            spell_id = spellId,
        });
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
    // caster is the local player. Emits HC-1.0 stop only if we previously
    // tracked a resurrection start for this player (avoids spamming stop
    // for non-rez failures).
    public void OnLocalPlayerSpellStop(uint spellId)
    {
        var localGuid = _session.GameState.CurrentPlayerGuid;
        if (localGuid.IsEmpty()) return;
        if (!_pendingByCaster.TryRemove(localGuid, out var pending)) return;
        if (!IsResurrectionSpell(pending.SpellId)) return;

        string payload = BuildResurrectionStopPayload(pending.SpellId);
        EmitToServer(HcPrefix, payload);
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

        Log.Event("healcomm.emit", new
        {
            prefix,
            body,
            in_raid = IsInRaid(),
        });
    }

    // Detect raid vs party from cached group state. CurrentGroups[0] is
    // the home group; raid sub-groups (indices 1+) signal raid context.
    private bool IsInRaid()
    {
        var groups = _session.GameState.CurrentGroups;
        if (groups == null) return false;
        for (int i = 1; i < groups.Length; i++)
        {
            var g = groups[i];
            if (g != null && g.PlayerList?.Count > 0)
                return true;
        }
        return false;
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

    // Pick a representative direct-heal spellID by sender's class so the
    // modern lib renders an icon. HC-1.0 doesn't carry spellID in Heal/...
    // and the actual rank is irrelevant for prediction display.
    private uint GuessDirectHealSpellId(WowGuid128 senderGuid)
    {
        if (_session.GameState.CachedPlayers.TryGetValue(senderGuid, out var cached))
        {
            return cached.ClassId switch
            {
                Class.Priest => SpellGreaterHeal,
                Class.Druid => SpellHealingTouch,
                Class.Paladin => SpellHolyLight,
                Class.Shaman => SpellHealingWave,
                _ => SpellGreaterHeal,
            };
        }
        return SpellGreaterHeal;
    }
}

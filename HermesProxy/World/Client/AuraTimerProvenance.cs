using System;
using System.Collections.Generic;
using HermesProxy.World.Objects;

namespace HermesProxy.World.Client;

/// <summary>
/// How the proxy learned WHEN an aura on another unit started. The 1.12 server never tells
/// the proxy how much time another unit's buff has left, so every remaining time the proxy
/// sends is either known from an event it saw or a guess. Issue #513: the proxy used to send
/// the spell's full duration for a buff it first saw already running, then recorded that
/// guess and served it again as if it were known.
/// </summary>
public enum AuraStartClass : byte
{
    /// <summary>Nothing seen: no value is sent and nothing is recorded.</summary>
    Unknown = 0,
    /// <summary>The local player's own aura: the server sent the remaining time.</summary>
    Self,
    /// <summary>A same-spell SMSG_SPELL_GO hit the unit (fresh cast or recast) while in view.</summary>
    ObservedGo,
    /// <summary>A new spell appeared in a slot of a unit already in view, or moved slot.</summary>
    ObservedSlot,
    /// <summary>A spell newly listed in an out-of-view party member's stats delta.</summary>
    StatsFirstSeen,
    /// <summary>The same spell relisted alone in a later out-of-view stats delta.</summary>
    StatsRelist,
    /// <summary>Served from the expiry map (any class above) on a cold slot.</summary>
    Restored,
}

/// <summary>Whether the FULL duration of an aura is known for the caster that applied it.</summary>
public enum AuraDurationSource : byte
{
    /// <summary>No duration table entry.</summary>
    None = 0,
    /// <summary>The spell id fixes the full duration (AuraDurations CSV).</summary>
    Fixed,
    /// <summary>The local player cast it: talent table or combo-point snapshot applies.</summary>
    Local,
    /// <summary>The duration depends on the caster or target (talent-modified spell cast by
    /// someone else, a combo-point finisher cast by someone else): not knowable here.</summary>
    Variable,
}

/// <summary>
/// One expiry-map entry: when the aura ends, how the proxy knows, and who applied it when that
/// was seen (default = unknown). The caster matters beyond the timer: the stock raid frame lists
/// only the buffs the viewing player cast (FrameXML CompactUnitFrame_UtilShouldDisplayBuff), so a
/// member's re-create after a relog must carry the caster back or the viewer's own buffs vanish
/// from the raid frame.
/// </summary>
public readonly record struct AuraExpiryRecord(
    long ExpiryTick,
    AuraStartClass StartClass,
    AuraDurationSource Source,
    int FullMs,
    int BoundMs,
    WowGuid128 Caster = default);

/// <summary>
/// JimsProxy (#513): the rules that decide whether an aura timer goes on the wire. Pure
/// functions, no session access; unit-tested in HermesProxy.Tests/World/AuraTimerProvenanceTests.
///
/// Wire contract: a KNOWN timer carries the Duration flag with both Duration and Remaining
/// on every emit (creates, deltas, stack and charge re-emits); an UNKNOWN timer carries no
/// flag and no values. Never values without the flag and never the flag without values.
/// </summary>
internal static class AuraTimerProvenance
{
    /// <summary>A stats-seen start is stamped at packet arrival; the server applied it up to
    /// about one second earlier (its party-stats send grid), so the error is one-sided.</summary>
    internal const int StatsStartBoundMs = 1000;

    /// <summary>An approximate start is served only when its bound is at most this share of
    /// the full duration (1 s against 50 s or more on a 1 s grid).</summary>
    internal const double ApproximateBoundShare = 0.02;

    /// <summary>Rule 1 (expired but present): an entry with less than this left while the spell
    /// is still in the slot is treated as unknown and dropped. The only defence against a
    /// same-caster refresh the proxy did not see.</summary>
    internal const int ExpiredFloorMs = 2000;

    /// <summary>Rule 2: a partial that restates every spell of the member's last full state
    /// within this window after that full state is a bulk resend, not a relist.</summary>
    internal const int BulkWindowMs = 3000;

    /// <summary>Rule 3: which duration source applies to an aura from this caster.</summary>
    internal static AuraDurationSource DurationSourceFor(
        bool hasFixedDuration, bool localPlayerCast, bool talentModifiedSpell, bool comboPointFinisher)
    {
        if (localPlayerCast && (talentModifiedSpell || comboPointFinisher))
            return AuraDurationSource.Local;
        if (talentModifiedSpell || comboPointFinisher)
            return AuraDurationSource.Variable;
        return hasFixedDuration ? AuraDurationSource.Fixed : AuraDurationSource.None;
    }

    internal static bool IsApproximateClass(AuraStartClass startClass)
        => startClass == AuraStartClass.StatsFirstSeen || startClass == AuraStartClass.StatsRelist;

    /// <summary>A start class and a duration source together make a value KNOWN.</summary>
    internal static bool IsKnown(AuraStartClass startClass, AuraDurationSource source, int fullMs, int boundMs)
    {
        if (startClass == AuraStartClass.Unknown)
            return false;
        if (source != AuraDurationSource.Fixed && source != AuraDurationSource.Local)
            return false;
        if (fullMs <= 0)
            return false;
        if (boundMs > 0 && boundMs > fullMs * ApproximateBoundShare)
            return false;
        return true;
    }

    /// <summary>
    /// Rule 1 applied to an expiry-map entry at an emit: the remaining time to serve, or null
    /// when the entry has run out (or nearly) and must be dropped.
    /// </summary>
    internal static int? RemainingToServe(in AuraExpiryRecord record, long nowTick)
    {
        long remaining = record.ExpiryTick - nowTick;
        if (remaining < ExpiredFloorMs)
            return null;
        return (int)Math.Min(remaining, int.MaxValue);
    }

    /// <summary>
    /// Rule 2: a partial party state whose aura entries are a bulk listing records nothing.
    /// <paramref name="restated"/> = spells in the delta that the member's table already held
    /// in that slot; <paramref name="lastFullSpells"/> = the spell set of the member's last full
    /// state, with <paramref name="msSinceLastFull"/> the time since it (null = no full seen).
    /// </summary>
    internal static bool IsBulkListing(
        bool hasStatusFlag,
        int restatedCount,
        IReadOnlyCollection<uint> restatedSpells,
        IReadOnlySet<uint>? lastFullSpells,
        long? msSinceLastFull)
    {
        if (hasStatusFlag)
            return true;
        if (restatedCount >= 2)
            return true;
        if (lastFullSpells != null && msSinceLastFull is long age && age >= 0 && age <= BulkWindowMs
            && lastFullSpells.Count > 0 && restatedSpells.Count == lastFullSpells.Count)
        {
            foreach (var id in restatedSpells)
                if (!lastFullSpells.Contains(id))
                    return false;
            return true;
        }
        return false;
    }

    /// <summary>A start to record from an out-of-view party stats delta.</summary>
    internal readonly record struct PartyAuraStamp(int Slot, uint ModernSpellId, AuraStartClass StartClass, AuraDurationSource Source, int FullMs);

    /// <summary>A slot whose previous spell is gone (cleared or swapped) in a stats delta.</summary>
    internal readonly record struct PartyAuraClearance(int Slot, uint LegacySpellId, bool SpellStillElsewhere);

    internal sealed class PartyDeltaClassification
    {
        public bool Bulk;
        public int RestatedCount;
        public readonly List<PartyAuraStamp> Stamps = new();
        public readonly List<PartyAuraClearance> Cleared = new();
        /// <summary>Fresh applications whose duration is not knowable: any older record of
        /// that spell on the member must be dropped so it cannot be served for the new one.</summary>
        public readonly List<uint> UnknownFreshModernIds = new();
    }

    /// <summary>
    /// The stats start classes (rules 2, 3 and 4) for one out-of-view party stats delta, decided
    /// against the member's slot table as it was BEFORE the delta. Pure: the callbacks supply
    /// the spell data.
    /// </summary>
    internal static PartyDeltaClassification ClassifyPartyDelta(
        uint[] tableBefore,
        IReadOnlyList<PartyAuraSlotTable.SlotChange> changes,
        bool hasStatusFlag,
        IReadOnlySet<uint>? lastFullSpells,
        long? msSinceLastFull,
        Func<uint, uint> toModernSpellId,
        Func<uint, int> fixedDurationMs,
        Func<uint, bool> isTalentModifiedSpell,
        Func<uint, bool> isComboPointFinisher)
    {
        var result = new PartyDeltaClassification();
        var restated = new List<uint>();
        foreach (var change in changes)
        {
            if (change.Slot < 0 || change.Slot >= tableBefore.Length)
                continue;
            uint before = tableBefore[change.Slot];
            if (before != 0 && before != change.LegacySpellId)
            {
                bool elsewhere = false;
                for (int s = 0; s < tableBefore.Length; s++)
                {
                    if (s != change.Slot && tableBefore[s] == before)
                    {
                        elsewhere = true;
                        break;
                    }
                }
                result.Cleared.Add(new PartyAuraClearance(change.Slot, before, elsewhere));
            }
            if (change.LegacySpellId != 0 && before == change.LegacySpellId)
            {
                result.RestatedCount++;
                restated.Add(toModernSpellId(before));
            }
        }

        result.Bulk = IsBulkListing(hasStatusFlag, result.RestatedCount, restated, lastFullSpells, msSinceLastFull);
        if (result.Bulk)
            return result;

        foreach (var change in changes)
        {
            if (change.LegacySpellId == 0 || change.Slot < 0 || change.Slot >= tableBefore.Length)
                continue;
            bool firstSeen = tableBefore[change.Slot] != change.LegacySpellId;
            uint modernId = toModernSpellId(change.LegacySpellId);
            var startClass = firstSeen ? AuraStartClass.StatsFirstSeen : AuraStartClass.StatsRelist;
            int fullMs = fixedDurationMs(modernId);
            var source = DurationSourceFor(fullMs > 0, false, isTalentModifiedSpell(modernId), isComboPointFinisher(modernId));
            if (!IsKnown(startClass, source, fullMs, StatsStartBoundMs))
            {
                if (firstSeen)
                    result.UnknownFreshModernIds.Add(modernId);
                continue;
            }
            result.Stamps.Add(new PartyAuraStamp(change.Slot, modernId, startClass, source, fullMs));
        }
        return result;
    }
}

using System;
using System.Collections.Generic;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (#513 follow-up, out-of-view party buff lists): the 1.12 server reports an
/// out-of-range party member's auras as slot-indexed deltas (SMSG_PARTY_MEMBER_STATS: a 32-bit
/// positive mask and a 16-bit negative mask, one spell id per set bit, 0 for a cleared slot; the
/// mask bit index is the aura slot index, negative bit j = slot 32 + j), and the full list only on
/// request (SMSG_PARTY_MEMBER_STATS_FULL). The 1.14 party-state aura block has no slot index and no
/// removal marker, and the client REPLACES the member's stored list with every aura-bearing state.
/// Forwarding a delta as a standalone list therefore shrank the member's buff list on screen to the
/// last change until the next full state, and a SpellId 0 entry stops an addon's buff loop early.
///
/// This table keeps the 48 slots per member (and per party pet) so the proxy can emit the complete
/// non-zero list on every aura-bearing state, exactly as a native server does. Pure functions, no
/// session access, unit-tested in <c>HermesProxy.Tests/World/PartyAuraSlotTableTests</c>.
/// </summary>
internal static class PartyAuraSlotTable
{
    internal const int PositiveSlots = 32;
    internal const int NegativeSlots = 16;
    /// <summary>48: the vanilla UNIT_FIELD_AURA size and the two party-stats masks together.</summary>
    internal const int SlotCount = PositiveSlots + NegativeSlots;

    /// <summary>One parsed slot change: the slot index and the legacy spell id (0 = cleared).</summary>
    internal readonly record struct SlotChange(int Slot, uint LegacySpellId);

    internal static uint[] NewTable() => new uint[SlotCount];

    /// <summary>A partial state: patch only the slots named by the mask bits.</summary>
    internal static void ApplyDelta(uint[] table, IEnumerable<SlotChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.Slot < 0 || change.Slot >= SlotCount)
                continue;
            table[change.Slot] = change.LegacySpellId;
        }
    }

    /// <summary>A full state lists every occupied slot: clear everything, then apply.</summary>
    internal static void RebuildFromFull(uint[] table, IEnumerable<SlotChange> changes)
    {
        Array.Clear(table, 0, table.Length);
        ApplyDelta(table, changes);
    }

    /// <summary>
    /// Seed the table from a unit's last in-view UNIT_FIELD_AURA fields (the legacy field cache),
    /// so the first delta after the member leaves view patches a complete list instead of an empty one.
    /// </summary>
    internal static void PrimeFromUnitFields(uint[] table, Dictionary<int, UpdateField> legacyFields, int auraFieldBase)
    {
        for (int i = 0; i < SlotCount; ++i)
            table[i] = legacyFields.TryGetValue(auraFieldBase + i, out var field) ? field.UInt32Value : 0u;
    }

    internal static bool IsEmpty(uint[] table)
    {
        foreach (var id in table)
            if (id != 0)
                return false;
        return true;
    }

    /// <summary>
    /// The complete non-zero list in slot order, as the 1.14 party-state aura block: positive slots
    /// carry the Positive flag, negative slots the Negative flag, ActiveFlags 1 (the shape the
    /// proxy already sent per entry). Never a SpellId 0 entry.
    /// </summary>
    internal static List<PartyMemberAuraStates> BuildAuraList(uint[] table, Func<uint, uint> toModernSpellId)
    {
        var list = new List<PartyMemberAuraStates>();
        for (int i = 0; i < SlotCount; ++i)
        {
            uint legacyId = table[i];
            if (legacyId == 0)
                continue;
            list.Add(new PartyMemberAuraStates
            {
                SpellId = toModernSpellId(legacyId),
                ActiveFlags = 1,
                AuraFlags = (ushort)(i < PositiveSlots ? AuraFlagsModern.Positive : AuraFlagsModern.Negative),
            });
        }
        return list;
    }

    internal static List<PartyMemberAuraStates> BuildAuraList(uint[] table)
        => BuildAuraList(table, GameData.GetModernSpellId);
}

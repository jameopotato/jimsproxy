using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace HermesProxy.World;

// LibThreatClassic2's set-bonus threat modifiers. Eight vanilla 1.12 sets
// apply gear-dependent threat adjustments — multipliers on specific spells,
// class-wide damage reductions, or heal threat scaling. Pure proxy-side
// reads (UNIT_FIELD_INV_SLOT_HEAD via the cached object fields). No
// sideband, no addon required.
//
// Implementation: per-event lookup. Iterates the player's 18 equipped slots
// (head..feet + relic + tabard + rings + trinkets) and counts how many of
// each known set's items are present. Returns the highest-tier bonus that
// the equipped count satisfies. ~18 dictionary lookups per threat event;
// acceptable at our event rate. Add caching if profiling shows hot.
internal static class ThreatSetBonuses
{
    public const int SET_WARRIOR_MIGHT = 1;
    public const int SET_MAGE_NETHERWIND = 2;
    public const int SET_MAGE_ARCANIST = 3;
    public const int SET_WARLOCK_NEMESIS = 4;
    public const int SET_WARLOCK_PLAGUEHEART = 5;
    public const int SET_PRIEST_VESTMENTS_OF_FAITH = 6;
    public const int SET_ROGUE_BONESCYTHE = 7;
    public const int SET_ROGUE_BLOODFANG = 8;

    // Item entry ID → set ID. Source: LibThreatClassic2 ClassModules/Classic
    // per-class set tables (verified 2026-05-17).
    private static readonly FrozenDictionary<uint, int> ItemToSet =
        BuildItemToSetMap().ToFrozenDictionary();

    private static Dictionary<uint, int> BuildItemToSetMap()
    {
        var map = new Dictionary<uint, int>();

        // Warrior — Battlegear of Might (T1, 8 pieces). Sunder Armor x1.15.
        foreach (var id in new uint[] { 16866, 16867, 16868, 16869, 16860, 16861, 16862, 16863 })
            map[id] = SET_WARRIOR_MIGHT;

        // Mage — Netherwind Regalia (T2, 8 pieces). Per-spell flat threat
        // reductions on Scorch/Fireball/Frostbolt/Arcane Missiles at 3-set.
        foreach (var id in new uint[] { 16914, 16917, 16916, 16818, 16915, 16818, 16819, 16920 })
            map[id] = SET_MAGE_NETHERWIND;

        // Mage — Arcanist Regalia (T1, 8 pieces). x0.85 to fire/frost/arcane
        // damage threat at 8-set.
        foreach (var id in new uint[] { 16795, 16796, 16797, 16798, 16799, 16800, 16801, 16802 })
            map[id] = SET_MAGE_ARCANIST;

        // Warlock — Nemesis Raiment (T2, 8 pieces). x0.8 to Searing Pain +
        // Destruction school spells at 8-set.
        foreach (var id in new uint[] { 16927, 16928, 16929, 16930, 16931, 16932, 16933, 16934 })
            map[id] = SET_WARLOCK_NEMESIS;

        // Warlock — Plagueheart Raiment (T3, 8 pieces). x0.75 spell threat
        // at 8-set (additional crit penalty deferred — needs crit-event hook).
        foreach (var id in new uint[] { 22504, 22505, 22506, 22507, 22508, 22509, 22510, 22511, 23063 })
            map[id] = SET_WARLOCK_PLAGUEHEART;

        // Priest — Vestments of Faith (T3, 8 pieces). x0.9 to heal threat at
        // 8-set.
        foreach (var id in new uint[] { 22512, 22513, 22514, 22515, 22516, 22517, 22518, 22519, 23064 })
            map[id] = SET_PRIEST_VESTMENTS_OF_FAITH;

        // Rogue — Bonescythe Armor (T3, 8 pieces). x0.92 to Sinister Strike,
        // Backstab, Hemorrhage, Eviscerate at 8-set.
        foreach (var id in new uint[] { 22476, 22477, 22478, 22479, 22480, 22481, 22482, 22483, 23060 })
            map[id] = SET_ROGUE_BONESCYTHE;

        // Rogue — Bloodfang Armor (T2, 8 pieces). Feint x1.25 at 5-set.
        foreach (var id in new uint[] { 16832, 16905, 16906, 16907, 16908, 16909, 16910, 16911 })
            map[id] = SET_ROGUE_BLOODFANG;

        return map;
    }

    // Count equipped pieces per set the player has on right now. Iterates
    // slots 0..18 (head..feet + tabard + rings + trinkets) reading the
    // cached OBJECT_FIELD_ENTRY for each item GUID. Skips empty slots and
    // items whose entries aren't in any tracked set.
    public static Dictionary<int, int> CountEquippedSetPieces(GameSessionData state)
    {
        var counts = new Dictionary<int, int>();
        int entryIdx = LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_ENTRY);
        if (entryIdx < 0) return counts;

        // Equipment slots only (0..18, head..tabard). BagStart=19 begins the
        // bag slots themselves, which carry containers not equippable gear.
        for (int slot = 0; slot < Enums.Vanilla.InventorySlots.BagStart; slot++)
        {
            var itemGuid64 = state.GetInventorySlotItem(slot);
            if (itemGuid64 == WowGuid64.Empty) continue;

            var itemGuid128 = itemGuid64.To128(state);
            var itemFields = state.GetCachedObjectFieldsLegacy(itemGuid128);
            if (itemFields == null) continue;

            if (!itemFields.TryGetValue(entryIdx, out var entryField)) continue;
            uint itemEntry = entryField.UInt32Value;
            if (itemEntry == 0) continue;

            if (ItemToSet.TryGetValue(itemEntry, out int setId))
                counts[setId] = counts.GetValueOrDefault(setId) + 1;
        }
        return counts;
    }

    // Per-spell damage multipliers conditional on set bonuses. Combined with
    // the per-class general multiplier from GetClassWideDamageMultiplier.
    private static readonly HashSet<int> NemesisDestructionSpells = new()
    {
        // Shadow Bolt R1..10
        686, 695, 705, 1088, 1106, 7641, 11659, 11660, 11661, 25307,
        // Searing Pain R1..6 (already in DamageMultipliers but Nemesis stacks)
        5676, 17919, 17920, 17921, 17922, 17923,
        // Conflagrate R1..4
        17962, 18931, 18932, 18933,
        // Immolate R1..8
        348, 707, 1094, 2941, 11665, 11667, 11668, 25309,
        // Rain of Fire R1..4
        5740, 6219, 11677, 11678,
        // Hellfire R1..4 (damage tick)
        1949, 11683, 11684, 11685,
        // Soul Fire R1..2
        6353, 17924,
    };

    private static readonly HashSet<int> BonescytheSpells = new()
    {
        // Sinister Strike R1..9
        1752, 1757, 1758, 1759, 1760, 8621, 11293, 11294, 26862,
        // Backstab R1..10
        53, 2589, 2590, 2591, 8721, 11279, 11280, 11281, 25300, 26863,
        // Hemorrhage R1..4
        16511, 17347, 17348, 26864,
        // Eviscerate R1..9
        2098, 6760, 6761, 6762, 8623, 8624, 11299, 11300, 31016,
    };

    // Returns the gear-derived damage multiplier for this spell (1.0 = no
    // adjustment). Applied alongside the base ThreatModules.DamageMultipliers
    // — caller multiplies the two together.
    public static double GetGearDamageMultiplier(GameSessionData state, Class playerClass, int spellId)
    {
        var counts = CountEquippedSetPieces(state);
        double mult = 1.0;

        switch (playerClass)
        {
            case Class.Mage:
                // Arcanist 8-set: x0.85 fire/frost/arcane (all mage damage).
                if (counts.GetValueOrDefault(SET_MAGE_ARCANIST) >= 8)
                    mult *= 0.85;
                break;

            case Class.Warlock:
                if (counts.GetValueOrDefault(SET_WARLOCK_NEMESIS) >= 8 &&
                    NemesisDestructionSpells.Contains(spellId))
                    mult *= 0.8;
                // Plagueheart 8-set: x0.75 to ALL warlock damage spells.
                if (counts.GetValueOrDefault(SET_WARLOCK_PLAGUEHEART) >= 8)
                    mult *= 0.75;
                break;

            case Class.Rogue:
                if (counts.GetValueOrDefault(SET_ROGUE_BONESCYTHE) >= 8 &&
                    BonescytheSpells.Contains(spellId))
                    mult *= 0.92;
                break;
        }

        return mult;
    }

    // Heal-side multiplier (Vestments of Faith 8-set on Priest: x0.9 to
    // every heal threat event).
    public static double GetGearHealMultiplier(GameSessionData state, Class playerClass)
    {
        if (playerClass != Class.Priest) return 1.0;
        var counts = CountEquippedSetPieces(state);
        return counts.GetValueOrDefault(SET_PRIEST_VESTMENTS_OF_FAITH) >= 8 ? 0.9 : 1.0;
    }

    // Per-spell flat threat ADD (not multiplier) for specific set effects.
    // Mage Netherwind 3-set: -100 flat on Scorch/Fireball/Frostbolt, -20 on
    // Arcane Missiles tick. Applied additively to the per-event threat.
    public static double GetGearDamageFlatAdjust(GameSessionData state, Class playerClass, int spellId)
    {
        if (playerClass != Class.Mage) return 0.0;
        var counts = CountEquippedSetPieces(state);
        if (counts.GetValueOrDefault(SET_MAGE_NETHERWIND) < 3) return 0.0;

        // Scorch R1..9
        if (spellId == 2948 || spellId == 8444 || spellId == 8445 || spellId == 8446 ||
            spellId == 10205 || spellId == 10206 || spellId == 10207 || spellId == 27073 ||
            spellId == 27074)
            return -100.0;
        // Fireball R1..13
        if (spellId == 133 || spellId == 143 || spellId == 145 || spellId == 3140 ||
            spellId == 8400 || spellId == 8401 || spellId == 8402 || spellId == 10148 ||
            spellId == 10149 || spellId == 10150 || spellId == 10151 || spellId == 25306 ||
            spellId == 38692)
            return -100.0;
        // Frostbolt R1..11
        if (spellId == 116 || spellId == 205 || spellId == 837 || spellId == 7322 ||
            spellId == 8406 || spellId == 8407 || spellId == 8408 || spellId == 10179 ||
            spellId == 10180 || spellId == 10181 || spellId == 25304)
            return -100.0;
        // Arcane Missiles (tick) R1..7
        if (spellId == 5143 || spellId == 5144 || spellId == 5145 || spellId == 8416 ||
            spellId == 8417 || spellId == 10211 || spellId == 10212 || spellId == 25345)
            return -20.0;

        return 0.0;
    }

    // Spell-specific MULTIPLIER set bonuses (Bloodfang Feint, Might Sunder).
    // These wrap flat-threat spells where the value lives in ThreatModules'
    // per-rank table — caller multiplies the flat amount by this scalar.
    public static double GetGearSpellMultiplier(GameSessionData state, Class playerClass, int spellId)
    {
        var counts = CountEquippedSetPieces(state);

        // Warrior Might 8-set: Sunder Armor x1.15
        if (playerClass == Class.Warrior && counts.GetValueOrDefault(SET_WARRIOR_MIGHT) >= 8 &&
            IsSunderArmor(spellId))
            return 1.15;

        // Rogue Bloodfang 5-set: Feint x1.25
        if (playerClass == Class.Rogue && counts.GetValueOrDefault(SET_ROGUE_BLOODFANG) >= 5 &&
            IsFeint(spellId))
            return 1.25;

        return 1.0;
    }

    private static bool IsSunderArmor(int spellId) =>
        spellId == 7386 || spellId == 7405 || spellId == 8380 ||
        spellId == 11596 || spellId == 11597;

    private static bool IsFeint(int spellId) =>
        spellId == 1966 || spellId == 6768 || spellId == 8637 ||
        spellId == 11303 || spellId == 25302;
}

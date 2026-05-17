using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace HermesProxy.World;

// LibThreatClassic2's per-boss NPC modules — abilities that wipe or modify
// player threat on the casting boss. Direct port of NPCModules/*/*.lua. All
// vanilla 1.12 raid + outdoor bosses with documented threat-affecting spells.
//
// Two trigger shapes:
//
//   CastHandlers   — SPELL_CAST_SUCCESS on the boss. Used by abilities that
//                    fire a "wipe / modify" effect on the SPELL TARGET (player
//                    or full raid) without a damage component. E.g. Ragnaros
//                    Wrath, Shazzrah Gate, Hakkar Aspect of Arlokk.
//
//   DamageHandlers — SPELL_DAMAGE on the boss. Used by knock-back style
//                    abilities that modify the damaged player's threat
//                    proportionally to a hit-confirmation. E.g. Broodlord
//                    Knock Away, Wing Buffet, Sand Blast.
//
// Some bosses chain-trigger off chat events (Onyxia phase 3 yell, Nefarian
// phase 2 yell). Not ported — proxy doesn't have a clean chat-text observer
// and the wipe is also predictable on HP threshold by addons doing their
// own bookkeeping. Translation tables in LTC2 NPCModules are skipped.
internal static class ThreatNPCModules
{
    public enum NPCThreatAction
    {
        WipeRaidOnMob,       // Boss's whole threat list cleared (raid reset).
        WipeSource,          // Specific player's threat on boss set to 0.
        HalveSource,         // Specific player's threat on boss × 0.5.
        QuarterReduceSource, // Specific player's threat on boss × 0.75 (Onyxia Knock Away).
    }

    // (creatureEntry, spellId) → action. Frozen for fast lookup; called on
    // every player-targeted SMSG_SPELL_GO that has an NPC source — needs to be
    // O(1).
    private static readonly FrozenDictionary<(uint, int), NPCThreatAction> CastHandlers =
        BuildCastHandlers().ToFrozenDictionary();

    private static readonly FrozenDictionary<(uint, int), NPCThreatAction> DamageHandlers =
        BuildDamageHandlers().ToFrozenDictionary();

    private static Dictionary<(uint, int), NPCThreatAction> BuildCastHandlers()
    {
        var map = new Dictionary<(uint, int), NPCThreatAction>();

        // Molten Core — Ragnaros 11502 / Wrath 20566
        map[(11502, 20566)] = NPCThreatAction.WipeRaidOnMob;

        // Molten Core — Shazzrah 12264 / Gate 23138
        map[(12264, 23138)] = NPCThreatAction.WipeRaidOnMob;

        // Naxxramas — Kel'Thuzad 15990 / Chains of Kel'Thuzad 28410
        map[(15990, 28410)] = NPCThreatAction.WipeRaidOnMob;

        // Zul'Gurub — Hakkar 14834 / Aspect of Arlokk 24690
        map[(14834, 24690)] = NPCThreatAction.WipeSource;

        // Onyxia 10184 / Fireball 18392 (wipe one player)
        map[(10184, 18392)] = NPCThreatAction.WipeSource;
        // Onyxia 10184 / Knock Away 19633 (0.75× one player)
        map[(10184, 19633)] = NPCThreatAction.QuarterReduceSource;

        return map;
    }

    private static Dictionary<(uint, int), NPCThreatAction> BuildDamageHandlers()
    {
        var map = new Dictionary<(uint, int), NPCThreatAction>();

        // AQ40 — Ouro 15517 / Sand Blast 26102 (wipe one player)
        map[(15517, 26102)] = NPCThreatAction.WipeSource;

        // BWL — Broodlord Lashlayer 12017 / Knock Away 18670 (0.5×)
        map[(12017, 18670)] = NPCThreatAction.HalveSource;

        // BWL drakes — Ebonroc / Firemaw / Flamegor / Wing Buffet 23339
        map[(14601, 23339)] = NPCThreatAction.HalveSource;  // Ebonroc
        map[(11983, 23339)] = NPCThreatAction.HalveSource;  // Firemaw
        map[(11981, 23339)] = NPCThreatAction.HalveSource;  // Flamegor

        // Molten Core — Molten Giant 11658 / Knock Away 18945 (0.5×)
        map[(11658, 18945)] = NPCThreatAction.HalveSource;

        return map;
    }

    // Naxxramas Noth uses BUFF_GAIN (29211 Blink), not cast or damage. Handled
    // separately via the aura-applied observer if we wire one in the future.
    public const int NOTH_BLINK_AURA = 29211;
    public const uint NOTH_NPC_ID = 15954;

    // Look up the cached creature_template entry for a unit GUID via
    // OBJECT_FIELD_ENTRY. Returns 0 if the unit isn't cached or isn't an NPC.
    public static uint GetCreatureEntry(GameSessionData state, WowGuid128 guid)
    {
        if (guid.IsEmpty()) return 0;
        var fields = state.GetCachedObjectFieldsLegacy(guid);
        if (fields == null) return 0;
        int entryIdx = LegacyVersion.GetUpdateField(ObjectField.OBJECT_FIELD_ENTRY);
        if (entryIdx < 0) return 0;
        if (!fields.TryGetValue(entryIdx, out var entryField)) return 0;
        return entryField.UInt32Value;
    }

    // Dispatch a SPELL_CAST_SUCCESS event from an NPC. Returns true if the
    // (npcEntry, spellId) pair matched a registered handler. Caller resolves
    // npcEntry from npcGuid via GetCreatureEntry before calling.
    public static bool TryHandleNPCCast(ThreatTracker tracker, WowGuid128 npcGuid, uint npcEntry, int spellId, WowGuid128 targetGuid)
    {
        if (!CastHandlers.TryGetValue((npcEntry, spellId), out var action)) return false;
        ApplyAction(tracker, npcGuid, npcEntry, spellId, targetGuid, action, "cast_success");
        return true;
    }

    // Dispatch a SPELL_DAMAGE event from an NPC. Returns true if matched.
    public static bool TryHandleNPCDamage(ThreatTracker tracker, WowGuid128 npcGuid, uint npcEntry, int spellId, WowGuid128 victimGuid)
    {
        if (!DamageHandlers.TryGetValue((npcEntry, spellId), out var action)) return false;
        ApplyAction(tracker, npcGuid, npcEntry, spellId, victimGuid, action, "spell_damage");
        return true;
    }

    private static void ApplyAction(ThreatTracker tracker, WowGuid128 npcGuid, uint npcEntry, int spellId, WowGuid128 victimGuid, NPCThreatAction action, string trigger)
    {
        switch (action)
        {
            case NPCThreatAction.WipeRaidOnMob:
                tracker.WipeRaidThreatOnMob(npcGuid);
                break;
            case NPCThreatAction.WipeSource:
                tracker.MultiplyTargetThreat(npcGuid, victimGuid, 0.0);
                break;
            case NPCThreatAction.HalveSource:
                tracker.MultiplyTargetThreat(npcGuid, victimGuid, 0.5);
                break;
            case NPCThreatAction.QuarterReduceSource:
                tracker.MultiplyTargetThreat(npcGuid, victimGuid, 0.75);
                break;
        }

        Framework.Logging.Log.Event("threat.npc_module_fired", new
        {
            npc_low = npcGuid.GetCounter(),
            npc_entry = npcEntry,
            spell_id = spellId,
            victim_low = victimGuid.GetCounter(),
            action = action.ToString(),
            trigger,
        });
    }
}

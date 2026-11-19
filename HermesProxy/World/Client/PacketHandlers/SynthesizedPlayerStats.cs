using Framework;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Modern Classic Era 1.14 interprets UiSpellHitModifier as a *rating* and applies the
    // Classic-Era rating-to-% conversion at L60: 7.0 spell-hit rating per 1% (empirically
    // verified — sending field=40 displayed 5.7%, 40/5.7 ≈ 7.0). Pre-scale so the displayed
    // value matches what we synthesize. UiHitModifier (melee/ranged) does NOT have this
    // conversion and stays a flat %.
    private const float SPELL_HIT_RATING_PER_PCT_L60 = 7.0f;

    // JimsProxy: the character-sheet stats vanilla 1.12 has no field for (Spell Healing, per-school
    // Spell Crit, melee/ranged Hit, Spell Hit), computed from the active player's equipped items,
    // visible auras, spellbook and item set bonuses. Called from the player field parse for every
    // update of the active player, and from ResendSynthesizedPlayerStatsForItemTemplate.
    //
    // fillDamageGaps: per-school +damage is normally pushed by the legacy server, but Kronos drops
    // fields whose values are zero from the update mask, so a healing-focused class gets null
    // entries; the parse path fills those from the synthesis and never overwrites a server value.
    // The re-emit path has no server values to compare against and leaves +damage alone.
    void SynthesizeVanillaPlayerStats(ActivePlayerData data, bool fillDamageGaps)
    {
        var gs = GetSession().GameState;
        var setBonusSpellIds = GameData.GetActiveItemSetBonusSpells(gs.CurrentEquippedItemIds, gs.GetPlayerSkillRank);

        var (healingDone, damageDone) = GameData.ComputeEquipmentSpellStats(
            gs.CurrentEquippedItemIds, gs.CurrentPlayerAuraSpellIds, setBonusSpellIds);
        if (healingDone.HasValue)
            data.ModHealingDonePos = healingDone;
        if (fillDamageGaps)
        {
            for (int i = 0; i < 7; i++)
            {
                if (data.ModDamageDonePos[i] == null && damageDone[i].HasValue)
                    data.ModDamageDonePos[i] = damageDone[i];
            }
        }

        // Vanilla 1.12 has no PLAYER_SPELL_CRIT_PERCENTAGE1 field. Base + INT/rate + aura
        // contributions per school, overriding the modern client's per-school crit array
        // (nothing the legacy server pushes can land in it, so overwriting is safe).
        float[] critBySchool = GameData.ComputeSpellCritChance(
            gs.CurrentPlayerClass, gs.CurrentPlayerLevel, gs.CurrentPlayerIntellect,
            gs.CurrentEquippedItemIds, gs.CurrentPlayerAuraSpellIds, gs.CurrentPlayerKnownSpells,
            setBonusSpellIds);
        for (int i = 0; i < 7; i++)
            data.SpellCritPercentage[i] = critBySchool[i];

        // Vanilla 1.12 has no UiHitModifier-equivalent field — Kronos can't push a value the
        // modern client recognises — so the Ranged/Melee "Hit Chance" row reads 0% until we
        // compute it from aura type 54 contributions on equipment, active auras, talent
        // passives and set bonuses.
        data.UiHitModifier = GameData.ComputeMeleeRangedHitModifier(
            gs.CurrentEquippedItemIds, gs.CurrentPlayerAuraSpellIds, gs.CurrentPlayerKnownSpells,
            setBonusSpellIds);

        float spellHitMod = GameData.ComputeSpellHitModifier(
            gs.CurrentEquippedItemIds, gs.CurrentPlayerAuraSpellIds, gs.CurrentPlayerKnownSpells,
            setBonusSpellIds);
        data.UiSpellHitModifier = spellHitMod * SPELL_HIT_RATING_PER_PCT_L60;
    }

    // JimsProxy: re-emit the synthesized stats once an equipped item's template is known.
    // GameData.ItemTemplates is filled only by SMSG_ITEM_QUERY_SINGLE_RESPONSE, and the proxy
    // queries the templates it is missing after it has parsed the SMSG_UPDATE_OBJECT that
    // showed it the items. So on the first login of a proxy process the player's create block
    // was synthesized with no equipped template cached: hit, spell hit, spell crit and
    // +healing went out as if the gear had no equip auras, and they stayed that way until
    // the next server-side player values update, which for an idle level 60 was the next
    // equip (a warrior with +1% hit bracers saw 0% until swapping shoulders). A values
    // update carrying just these fields corrects the sheet as each equipped template lands.
    void ResendSynthesizedPlayerStatsForItemTemplate(uint itemEntry)
    {
        if (LegacyVersion.ExpansionVersion != 1)
            return;

        var gs = GetSession().GameState;
        if (gs.CurrentPlayerGuid.IsEmpty() || gs.IsWaitingForNewWorld)
            return;
        if (Array.IndexOf(gs.CurrentEquippedItemIds, (int)itemEntry) < 0)
            return;

        UpdateObject updateObject = new UpdateObject(gs);
        ObjectUpdate update = new ObjectUpdate(gs.CurrentPlayerGuid, UpdateTypeModern.Values, GetSession());
        SynthesizeVanillaPlayerStats(update.ActivePlayerData, fillDamageGaps: false);
        updateObject.ObjectUpdates.Add(update);
        SendPacketToClient(updateObject);

        if (Settings.DebugOutput)
        {
            Framework.Logging.Log.Event("stats.synth.resent_after_item_template", new
            {
                item = itemEntry,
                hit = update.ActivePlayerData.UiHitModifier,
                spell_hit_rating = update.ActivePlayerData.UiSpellHitModifier,
                healing = update.ActivePlayerData.ModHealingDonePos,
            });
        }
    }
}

using Framework.Logging;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;

namespace HermesProxy.World;

// JimsProxy threat translation: client-side threat calculation engine.
//
// Vanilla 1.12 servers don't broadcast threat over the wire; threat tables live
// in server-side internal state. The modern 1.14 client expects SMSG_THREAT_UPDATE
// (and friends) so its native APIs (UnitDetailedThreatSituation, UnitThreatSituation,
// UNIT_THREAT_LIST_UPDATE event) populate. Without those packets the modern client
// shows zero threat and downstream addons (Details TinyThreat, TidyPlates_ThreatPlates,
// default UI target frame) get nothing to display.
//
// This class observes combat events forwarded from the legacy server, computes
// threat per LibThreatClassic2 rules (port-in-progress, see ClassModules/), and
// emits modern SMSG threat opcodes to the client.
//
// Phase 2 scope: track damage threat for the local player and their pet. Other
// group members' threat is not yet shown (Phase 6 group sync). Class-specific
// abilities (Distracting Shot, Feign Death, Sunder Armor flat threat, defensive
// stance multipliers, etc.) ship in Phase 3+.
//
// Threading: methods are called from the WorldClient thread (which dispatches
// SMSG packet handlers). Emission goes back via WorldClient.SendPacketToClient
// so it stays on the same thread — no cross-thread state mutation.
public sealed class ThreatTracker
{
    // Per-mob threat list. Outer key = threatened entity (mob). Inner dict
    // maps threater (player/pet) -> raw threat value. We pack × 100 only at
    // emit time so internal math can stay floating point.
    private readonly Dictionary<WowGuid128, Dictionary<WowGuid128, double>> _threatLists = new();

    // Mobs whose threat list has been mutated since the last emit. Flushed
    // in EmitDirty(). Lets a single damage event that touches multiple mobs
    // (e.g. Multi-Shot, Volley) batch into one round of SMSG emission.
    private readonly HashSet<WowGuid128> _dirty = new();

    // Last-emitted highest threater per mob — so we only emit
    // SMSG_HIGHEST_THREAT_UPDATE when the top actually changes.
    private readonly Dictionary<WowGuid128, WowGuid128> _lastHighest = new();

    // Vanilla melee aggro hysteresis: a challenger must exceed the current
    // tank's threat by 10% before aggro flips. Without this, two threaters
    // doing similar damage rapidly swap the "highest" position on every
    // damage event and the modern client paints Luna red on every party
    // member simultaneously. 1.10 = melee threshold; ranged is 1.30 but
    // we can't tell from a damage event whether the challenger is in melee
    // range so we use the more permissive value (matches the local-player
    // case which is by far the dominant one).
    private const double AggroFlipMargin = 1.10;

    private readonly GlobalSessionData _session;

    // Passive threat multiplier cache, keyed by threater GUID. Recomputed
    // on every threat operation (cheap) but the threat.passive_modifier
    // event only emits on change, so testers can spot stance / form
    // transitions without spamming logs. Phase 6.0 widened from a single
    // pair (local player only) to a per-threater dict so groupmates'
    // stance / form changes are also tracked.
    private readonly Dictionary<WowGuid128, (uint stanceForm, double modifier)> _passiveCache = new();

    public ThreatTracker(GlobalSessionData session)
    {
        _session = session;
    }

    // Adds threat with the local player's stance / form / class passive
    // modifier applied. Use for ability-driven flat threat (Sunder, Distracting
    // Shot, etc.) and damage threat. Set / multiply / set-to-top operations
    // bypass this — those encode their own absolute values.
    //
    // Pet attacks always pass through unmodified (modifier = 1.0). Non-local
    // threaters never reach here in the first place.
    public void AddModifiedThreat(WowGuid128 mob, WowGuid128 threater, double rawAmount)
    {
        double modifier = GetPassiveModifier(threater);
        AddThreat(mob, threater, rawAmount * modifier);
    }

    // Add (or subtract, if amount is negative) raw threat from a threater
    // against a mob. Marks the mob dirty so the next EmitDirty pushes the
    // updated threat list to the client.
    public void AddThreat(WowGuid128 mob, WowGuid128 threater, double amount)
    {
        if (mob == default || threater == default || amount == 0)
            return;

        if (!_threatLists.TryGetValue(mob, out var list))
        {
            list = new Dictionary<WowGuid128, double>();
            _threatLists[mob] = list;
        }

        list.TryGetValue(threater, out double existing);
        double updated = existing + amount;
        // Vanilla clamps threat at zero — negative threat is invisible to the
        // server's pull aggro check. LibThreatClassic2 mirrors this.
        if (updated < 0) updated = 0;
        list[threater] = updated;

        _dirty.Add(mob);
    }

    // Set a threater's threat on a mob to an absolute value. Used by Growl
    // (which sets pet threat to current top + 1) in Phase 3.
    public void SetThreat(WowGuid128 mob, WowGuid128 threater, double amount)
    {
        if (mob == default || threater == default)
            return;

        if (!_threatLists.TryGetValue(mob, out var list))
        {
            list = new Dictionary<WowGuid128, double>();
            _threatLists[mob] = list;
        }
        if (amount < 0) amount = 0;
        list[threater] = amount;
        _dirty.Add(mob);
    }

    // Multiply a single threater's threat by a factor across ALL their mobs.
    // Hunter Feign Death uses this (factor = 0). Returns to the caller via
    // dirty-mark; emit happens on next flush.
    public void MultiplyThreat(WowGuid128 threater, double factor)
    {
        if (threater == default) return;
        foreach (var (mob, list) in _threatLists)
        {
            if (list.TryGetValue(threater, out double current))
            {
                double updated = current * factor;
                if (updated < 0) updated = 0;
                list[threater] = updated;
                _dirty.Add(mob);
            }
        }
    }

    // Bring threater up to the current top of mob's list (taunt semantics).
    // Used by Warrior Taunt, Mocking Blow, Druid Growl. If the threater is
    // already at or above the top, no-op. Marks dirty on success so the next
    // EmitDirty pushes both the changed value and the new HIGHEST_THREAT_UPDATE.
    public void SetToTop(WowGuid128 mob, WowGuid128 threater)
    {
        if (mob == default || threater == default) return;
        if (!_threatLists.TryGetValue(mob, out var list))
        {
            list = new Dictionary<WowGuid128, double>();
            _threatLists[mob] = list;
        }

        double topThreat = 0;
        foreach (var v in list.Values)
        {
            if (v > topThreat) topThreat = v;
        }

        list.TryGetValue(threater, out double myThreat);
        if (myThreat >= topThreat) return;

        list[threater] = topThreat;
        _dirty.Add(mob);
    }

    // Add (or subtract) flat threat across every mob the threater is currently
    // tracked on. Used for buffs / utility casts that bump aggro on every mob
    // already in combat with the player (Cleanse, Remove Lesser Curse, etc.).
    // No-op for mobs the threater isn't already on — vanilla doesn't generate
    // threat against unrelated mobs from these casts.
    public void AddThreatToAllMobs(WowGuid128 threater, double rawAmount)
    {
        if (threater == default || rawAmount == 0) return;

        double modifier = GetPassiveModifier(threater);
        double scaledAmount = rawAmount * modifier;

        foreach (var (mob, list) in _threatLists)
        {
            if (!list.TryGetValue(threater, out double existing))
                continue;
            double updated = existing + scaledAmount;
            if (updated < 0) updated = 0;
            list[threater] = updated;
            _dirty.Add(mob);
        }
    }

    // Mob died, ran far away, evaded, etc. Remove its threat list entirely
    // and emit SMSG_THREAT_CLEAR so the modern client knows to drop it from
    // the threat APIs.
    public void ClearMob(WowGuid128 mob)
    {
        if (mob == default) return;
        if (!_threatLists.Remove(mob))
            return;
        _lastHighest.Remove(mob);
        _dirty.Remove(mob);

        var pkt = new ThreatClearPkt { UnitGUID = mob };
        SendToClient(pkt);

        Log.Event("threat.mob_cleared", new
        {
            mob_guid = mob.ToString(),
        });
    }

    // A single threater (e.g. a player who left the group, died) drops off a
    // mob's threat list. Vanilla also fires this on Vanish, etc. — Phase 3+.
    public void RemoveThreater(WowGuid128 mob, WowGuid128 threater)
    {
        if (mob == default || threater == default) return;
        if (!_threatLists.TryGetValue(mob, out var list)) return;
        if (!list.Remove(threater)) return;

        var pkt = new ThreatRemovePkt
        {
            UnitGUID = mob,
            AboutGUID = threater,
        };
        SendToClient(pkt);

        // The mob's remaining list may still need an update (since the top
        // may have changed). Mark dirty so EmitDirty refreshes it.
        if (list.Count > 0)
        {
            _dirty.Add(mob);
        }
        else
        {
            _threatLists.Remove(mob);
            _lastHighest.Remove(mob);
        }
    }

    // Flush all pending dirty mobs to the client. Call after a batch of damage
    // events — typically once per WorldClient packet dispatch is fine since
    // most encounters fire one combat event at a time.
    public void EmitDirty()
    {
        if (_dirty.Count == 0) return;

        // Snapshot then clear before emitting so any re-entrancy from the
        // emit path doesn't see the same dirty mobs twice.
        var toEmit = new List<WowGuid128>(_dirty);
        _dirty.Clear();

        foreach (var mob in toEmit)
        {
            if (!_threatLists.TryGetValue(mob, out var list) || list.Count == 0)
                continue;

            // Find the raw top threater (max value, no hysteresis).
            WowGuid128 rawTop = default;
            double rawTopValue = -1;
            foreach (var (threater, value) in list)
            {
                if (value > rawTopValue)
                {
                    rawTopValue = value;
                    rawTop = threater;
                }
            }

            // Apply vanilla aggro hysteresis. Vanilla 1.12 requires a melee
            // challenger to exceed the current tank's threat by 10% (130% for
            // ranged) before aggro flips. Numerically passing the top by 1
            // threat point is NOT enough — the server holds aggro on the
            // current tank until the threshold is crossed.
            //
            // Why this matters for addons: the modern client computes
            // UnitThreatSituation status against HighestThreatGUID. If we
            // re-flip the top on every close-tie swing (two players doing
            // similar damage, tank + DPS within 10%, etc.), each threater
            // briefly becomes "the highest" and the modern client reports
            // status 2-3 for both, painting Luna red on every party member
            // simultaneously. Holding the current top until the 110%
            // threshold is crossed restores vanilla's "one tank, one aggro"
            // feel — DPS sit at status 0/1 (hidden / yellow) while the actual
            // tank sits at status 3 (red).
            _lastHighest.TryGetValue(mob, out var prevHighest);
            WowGuid128 newHighest;
            double highestValue;
            bool aggroFlipHeld = false;
            if (prevHighest == default || !list.TryGetValue(prevHighest, out var prevValue))
            {
                // No prior top, or prior top has dropped off the list (left
                // party / died / despawned). No hysteresis to apply — pick
                // the raw top.
                newHighest = rawTop;
                highestValue = rawTopValue;
            }
            else if (rawTop == prevHighest)
            {
                // Current top still on top — no flip to consider.
                newHighest = prevHighest;
                highestValue = prevValue;
            }
            else if (rawTopValue >= prevValue * AggroFlipMargin)
            {
                // Challenger crossed the 110% threshold — flip.
                newHighest = rawTop;
                highestValue = rawTopValue;
            }
            else
            {
                // Challenger numerically ahead but inside the hysteresis
                // band — vanilla server would still target prev. Hold.
                newHighest = prevHighest;
                highestValue = prevValue;
                aggroFlipHeld = true;
            }

            var update = new ThreatUpdatePkt { UnitGUID = mob };
            foreach (var (threater, value) in list)
            {
                update.ThreatList.Add(new ThreatInfo
                {
                    ThreaterGUID = threater,
                    Threat = ToWireThreat(value),
                });
            }
            SendToClient(update);

            bool highestChanged = prevHighest != newHighest;

            if (aggroFlipHeld)
            {
                Log.Event("threat.aggro_flip_held", new
                {
                    mob_low = mob.GetCounter(),
                    held_threater_low = prevHighest.GetCounter(),
                    held_value = (long)highestValue,
                    challenger_low = rawTop.GetCounter(),
                    challenger_value = (long)rawTopValue,
                    margin_required = AggroFlipMargin,
                });
            }

            Log.Event("threat.emit_update", new
            {
                mob_low = mob.GetCounter(),
                threater_count = list.Count,
                highest_low = newHighest.GetCounter(),
                highest_value = (long)highestValue,
                highest_is_local = newHighest == _session.GameState.CurrentPlayerGuid,
                highest_changed = highestChanged,
                threaters = ThreaterSnapshot(list),
            });

            // Emit HIGHEST only when the top changes — saves churn but keeps
            // tank-aggro indicators (red border, nameplate color) snappy.
            if (highestChanged)
            {
                _lastHighest[mob] = newHighest;
                var highest = new HighestThreatUpdatePkt
                {
                    UnitGUID = mob,
                    HighestThreatGUID = newHighest,
                };
                foreach (var (threater, value) in list)
                {
                    highest.ThreatList.Add(new ThreatInfo
                    {
                        ThreaterGUID = threater,
                        Threat = ToWireThreat(value),
                    });
                }
                SendToClient(highest);

                Log.Event("threat.highest_changed", new
                {
                    mob_low = mob.GetCounter(),
                    prev_highest_low = prevHighest.GetCounter(),
                    new_highest_low = newHighest.GetCounter(),
                    new_highest_is_local = newHighest == _session.GameState.CurrentPlayerGuid,
                    new_highest_value = (long)highestValue,
                });
            }
        }
    }

    // Compact one-line representation of a mob's threater list for the JSONL
    // bundle. Keeps the snapshot small — counter + value pairs — so it's
    // readable in a diagnostic without flooding context.
    private static string ThreaterSnapshot(Dictionary<WowGuid128, double> list)
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var (threater, value) in list)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(threater.GetCounter()).Append('=').Append((long)value);
        }
        return sb.ToString();
    }

    // Wipe everything — used on session disconnect / character switch. Doesn't
    // emit packets since the client connection is going away anyway.
    public void Reset()
    {
        _threatLists.Clear();
        _lastHighest.Clear();
        _dirty.Clear();
    }

    // Called from SMSG_CANCEL_COMBAT — the legacy server told the local player
    // they've left combat. The 1.12 server does NOT broadcast leave-combat
    // for other party members and doesn't emit a "drop the threat list" packet
    // for mobs that evade / run away / lose interest. Without an explicit
    // clear, the modern client retains the stale threat list and Luna /
    // ThreatPlates keep their red indicators lit on every nameplate the
    // player fought during the session.
    //
    // Aggressive but matches vanilla feel: if WE'RE not in combat, nothing
    // should display threat anywhere. If a groupmate is still fighting, their
    // own subsequent damage events will re-populate the relevant mob's list.
    public void OnLocalPlayerLeftCombat()
    {
        if (_threatLists.Count == 0) return;

        var mobsCleared = _threatLists.Count;
        var mobsToClear = new List<WowGuid128>(_threatLists.Keys);
        foreach (var mob in mobsToClear)
            ClearMob(mob);

        Log.Event("threat.local_player_left_combat", new
        {
            mobs_cleared = mobsCleared,
        });
    }

    // Called from the SMSG_DESTROY_OBJECT handler. Two cases to clean up:
    //   1) The destroyed unit was a mob we tracked → ClearMob (emit SMSG_THREAT_CLEAR).
    //   2) The destroyed unit was a threater (player who left, pet despawned) →
    //      RemoveThreater across every mob that had them on its list.
    public void OnUnitDestroyed(WowGuid128 guid)
    {
        if (guid == default) return;

        // Case 1: this guid was a mob in our tracked set.
        if (_threatLists.ContainsKey(guid))
        {
            ClearMob(guid);
        }

        // Case 2: this guid was a threater on some other mob's list. Scan all
        // remaining lists. Vanilla group sizes keep this O(party) — cheap.
        List<WowGuid128>? mobsToCleanup = null;
        foreach (var (mob, list) in _threatLists)
        {
            if (list.ContainsKey(guid))
            {
                mobsToCleanup ??= new List<WowGuid128>();
                mobsToCleanup.Add(mob);
            }
        }
        if (mobsToCleanup != null)
        {
            foreach (var mob in mobsToCleanup)
                RemoveThreater(mob, guid);
            EmitDirty();
        }
    }

    // Called by the SMSG combat-log observers in WorldClient handlers. Checks
    // if the attacker is one of "my threaters" (the local player or their pet)
    // and adds damage threat against the victim mob if so. Damage threat in
    // vanilla is 1.0 × raw damage with no school multiplier (modifiers from
    // class abilities like Defensive Stance ×1.45 ship in Phase 3+).
    //
    // Auto-flushes the dirty set so a single combat-log packet results in at
    // most one SMSG_THREAT_UPDATE on the wire. Callers don't need to remember
    // to call EmitDirty.
    // Convenience overload for melee swings (no spell id) and other call
    // sites that don't have spell context yet. Treats it as plain damage.
    public void OnDamage(WowGuid128 attacker, WowGuid128 victim, double rawDamage)
        => OnDamage(attacker, victim, 0, rawDamage);

    public void OnDamage(WowGuid128 attacker, WowGuid128 victim, int spellId, double rawDamage)
    {
        if (rawDamage <= 0) return;
        if (!IsRelevantThreater(attacker))
        {
            // Tester bundles will show this when a damage event fires but
            // we filtered the attacker out of the group / pet / self set —
            // e.g. a stranger's pet hitting our shared mob, or a groupmate
            // who hasn't been seen in CurrentGroups yet (login race).
            Log.Event("threat.drop_irrelevant_attacker", new
            {
                attacker_low = attacker.GetCounter(),
                attacker_high = attacker.GetHighType().ToString(),
                victim_low = victim.GetCounter(),
                spell_id = spellId,
                damage = (long)rawDamage,
            });
            return;
        }
        if (victim == default) return;

        SnapshotTalentStateIfChanged();
        double abilityMultiplier = ThreatModules.GetDamageMultiplier(spellId);
        double passiveModifier = GetPassiveModifier(attacker);
        double talentMultiplier = GetSpellTalentMultiplier(attacker, spellId);

        // Set-bonus gear adjustments (Mage Arcanist x0.85, Warlock Nemesis x0.8
        // on Destruction, Warlock Plagueheart x0.75, Rogue Bonescythe x0.92,
        // Mage Netherwind -100/-20 flat). Player-only; pets / guardians don't
        // carry sets. Layered on top of ability/passive/talent multipliers.
        double gearMultiplier = 1.0;
        double gearFlat = 0.0;
        if (attacker == _session.GameState.CurrentPlayerGuid)
        {
            var playerClass = (Class)_session.GameState.CurrentPlayerClass;
            gearMultiplier = ThreatSetBonuses.GetGearDamageMultiplier(_session.GameState, playerClass, spellId);
            gearFlat = ThreatSetBonuses.GetGearDamageFlatAdjust(_session.GameState, playerClass, spellId);
        }

        double scaledThreat = (rawDamage * abilityMultiplier * gearMultiplier + gearFlat) * passiveModifier * talentMultiplier;
        if (scaledThreat < 0) scaledThreat = 0;
        AddThreat(victim, attacker, scaledThreat);

        if (_threatLists.TryGetValue(victim, out var list) &&
            list.TryGetValue(attacker, out double newTotal))
        {
            Log.Event("threat.damage_added", new
            {
                attacker_low = attacker.GetCounter(),
                attacker_is_player = attacker == _session.GameState.CurrentPlayerGuid,
                victim_low = victim.GetCounter(),
                spell_id = spellId,
                damage = (long)rawDamage,
                ability_mult = abilityMultiplier,
                passive_mod = passiveModifier,
                talent_mult = talentMultiplier,
                threat_added = (long)scaledThreat,
                new_total = (long)newTotal,
                threater_count = list.Count,
            });
        }

        EmitDirty();
    }

    // Phase 7 — heal threat. Vanilla heal threat is 0.5 × effective heal,
    // distributed evenly across every mob currently in combat with the heal
    // target (i.e., every mob whose threater list contains healTarget).
    //
    // Caveat: we only know about mobs WE have engaged. If a pure healer
    // never deals damage, the mobs they're "supporting" never enter our
    // threat list, so heal threat won't fire for them. Group-sync threat
    // (a future phase) is the proper fix; until then, the limitation is
    // identical to the rest of the system — heal threat works for tanks,
    // off-healers who DPS, and self-heals while in combat.
    public void OnHeal(WowGuid128 healer, WowGuid128 healTarget, int spellId, double effectiveHeal)
    {
        if (effectiveHeal <= 0) return;
        if (!IsRelevantThreater(healer)) return;
        if (healTarget == default) return;

        // Collect mobs whose threater list contains the heal target.
        List<WowGuid128>? mobsThreateningTarget = null;
        foreach (var (mob, list) in _threatLists)
        {
            if (list.ContainsKey(healTarget))
            {
                mobsThreateningTarget ??= new List<WowGuid128>();
                mobsThreateningTarget.Add(mob);
            }
        }

        if (mobsThreateningTarget == null || mobsThreateningTarget.Count == 0)
        {
            // Healed someone we don't know is in combat — drop. Avoids
            // putting threat on every mob we've ever fought just because we
            // healed a friendly out of nowhere.
            return;
        }

        SnapshotTalentStateIfChanged();
        double passiveModifier = GetPassiveModifier(healer);
        // School-gated talent on heals: paladin Imp Righteous Fury boosts holy
        // heal threat when RF aura is active. Other classes' heals are no-op.
        double talentMultiplier = GetSpellTalentMultiplier(healer, spellId);

        // Set-bonus heal-threat scalar (Priest Vestments of Faith 8-set: x0.9).
        double gearHealMultiplier = 1.0;
        if (healer == _session.GameState.CurrentPlayerGuid)
        {
            var playerClass = (Class)_session.GameState.CurrentPlayerClass;
            gearHealMultiplier = ThreatSetBonuses.GetGearHealMultiplier(_session.GameState, playerClass);
        }

        double totalThreat = effectiveHeal * 0.5 * passiveModifier * talentMultiplier * gearHealMultiplier;
        double threatPerMob = totalThreat / mobsThreateningTarget.Count;

        foreach (var mob in mobsThreateningTarget)
            AddThreat(mob, healer, threatPerMob);

        Log.Event("threat.heal_added", new
        {
            healer_low = healer.GetCounter(),
            heal_target_low = healTarget.GetCounter(),
            spell_id = spellId,
            effective_heal = (long)effectiveHeal,
            passive_mod = passiveModifier,
            talent_mult = talentMultiplier,
            mobs_split = mobsThreateningTarget.Count,
            threat_per_mob = (long)threatPerMob,
            total_threat = (long)totalThreat,
        });

        EmitDirty();
    }

    // LTC2 ExemptGains: spells that should NOT generate energize threat.
    //   34299 — Improved Leader of the Pack (heal-side, no threat)
    //   33778 — Lifebloom final bloom (overheal-like effect)
    private static bool IsEnergizeExempt(int spellId) =>
        spellId == 34299 || spellId == 33778;

    // Energize-event threat. LibThreatClassic2 fires on SPELL_ENERGIZE and
    // SPELL_PERIODIC_ENERGIZE: mana gain × 0.5, all other power types × 5.
    // Threat goes to the CASTER and is added to EVERY mob the caster is on
    // (replicated, not split — matches LTC2's per-mob iteration in
    // ThreatClassModuleCore.lua line 588). Server pre-caps the amount to
    // actual gain (zero-gain energizes don't fire), so no client-side cap
    // math needed proxy-side.
    public void OnEnergize(WowGuid128 caster, WowGuid128 recipient, int spellId, PowerType powerType, double amount)
    {
        if (amount <= 0) return;
        if (!IsRelevantThreater(caster)) return;
        if (IsEnergizeExempt(spellId)) return;

        double multiplier = powerType == PowerType.Mana ? 0.5 : 5.0;
        double rawThreat = amount * multiplier;

        // AddThreatToAllMobs applies the passive modifier internally and
        // skips mobs the caster isn't already on (no aggro pull from
        // off-combat energize — matches lib semantics).
        AddThreatToAllMobs(caster, rawThreat);

        Framework.Logging.Log.Event("threat.energize", new
        {
            caster_low = caster.GetCounter(),
            recipient_low = recipient.GetCounter(),
            spell_id = spellId,
            power_type = powerType.ToString(),
            amount = (long)amount,
            multiplier,
            raw_threat = (long)rawThreat,
        });

        EmitDirty();
    }

    // Called from SMSG_SPELL_GO observer for any spell cast that may modify
    // threat (Distracting Shot, Disengage, Feign Death, Growl, Cower, ...).
    // Routes to ThreatModules' per-spell-id handler. Auto-flushes the dirty
    // set so the threat-update SMSG goes out alongside the SpellGo packet.
    public void OnSpellCast(WowGuid128 caster, int spellId, IList<WowGuid128> hitTargets)
    {
        if (caster == default) return;

        if (!ThreatModules.TryHandle(this, _session, spellId, caster, hitTargets))
            return;

        EmitDirty();
    }

    // True if the given GUID is the local player, a unit they own (pet,
    // totem, guardian), any party / raid member, or a pet owned by any
    // party / raid member. Pet ownership is read from UNIT_FIELD_SUMMONEDBY
    // on the unit's cached fields, which the legacy server populates and the
    // proxy mirrors. We re-check on every event rather than caching because
    // pets get swapped (Hunter Call Pet, Warlock summon swap) and the cost
    // of one dictionary lookup per damage event is negligible.
    //
    // JimsProxy Phase 6.0 (group-aware threat): widened from local-player +
    // local-pet to the full party / raid so addons (Details TinyThreat,
    // TidyPlates_ThreatPlates) see threat for everyone in your group, not
    // just yourself. Vanilla 1.12 servers broadcast combat-log packets
    // (SMSG_ATTACKER_STATE_UPDATE, SMSG_SPELL_NON_MELEE_DAMAGE_LOG) to
    // every client in range, so each proxy can compute group-wide threat
    // unilaterally — no peer cooperation needed for in-range groupmates.
    private bool IsRelevantThreater(WowGuid128 guid)
    {
        if (guid == default) return false;
        var playerGuid = _session.GameState.CurrentPlayerGuid;
        if (guid == playerGuid) return true;
        if (IsAnyPartyMember(guid)) return true;

        var fields = _session.GameState.GetCachedObjectFieldsLegacy(guid);
        if (fields == null) return false;

        int summonedByIdx = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_SUMMONEDBY);
        if (summonedByIdx < 0) return false;

        WowGuid64 summonedBy64 = fields.GetGuidValue(summonedByIdx);
        if (summonedBy64 == WowGuid64.Empty) return false;

        WowGuid128 summonedBy128 = summonedBy64.To128(_session.GameState);
        return summonedBy128 == playerGuid || IsAnyPartyMember(summonedBy128);
    }

    // Iterates both home + battleground party slots so we recognise
    // everyone in the user's current group regardless of context.
    private bool IsAnyPartyMember(WowGuid128 guid)
    {
        if (guid == default) return false;
        var groups = _session.GameState.CurrentGroups;
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            if (group == null) continue;
            foreach (var member in group.PlayerList)
            {
                if (member.GUID == guid)
                    return true;
            }
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Phase 6 — passive threat multipliers from class + stance / form + talents.
    //
    // Mirrors LibThreatClassic2's per-class `passiveThreatModifiers` model. The
    // proxy reads the player's known-spells set (now reliable thanks to the
    // talent-rank injection fix: CurrentPlayerKnownSpells holds the highest
    // rank the server has granted, SynthesizedTalentRanks holds proxy-injected
    // predecessor ranks). Highest rank of each talent is resolved by walking
    // the rank IDs from low to high and remembering the highest match.
    //
    // Baselines (apply unconditionally):
    //   Warrior:
    //     Defensive Stance (71)   → 1.30  ×  (1 + 0.03 × Defiance rank)
    //     Berserker Stance (2458) → 0.80
    //     Battle Stance (2457) /
    //       no stance             → 0.80  (matches the lib's quirk; lib treats
    //                                      non-Defensive warriors as 0.8, see
    //                                      ClassModules/Classic/Warrior.lua)
    //   Druid:
    //     Bear / Dire Bear Form
    //       (5487 / 9634)         → 1.30  ×  (1 + 0.03 × Feral Instinct rank)
    //     Cat (768) /
    //       Travel (783) /
    //       Aquatic (1066)        → 0.71
    //     Caster                  → 1.00
    //   Rogue:                    → 0.71  (always; the lib's 0.71 IS the rogue
    //                                      passive — vanilla rogue Subtlety
    //                                      tab has no flat-threat talent on
    //                                      top of this)
    //   Priest                    → 1.00  ×  (1 - 0.04 × Silent Resolve rank)
    //   All other classes:        → 1.00
    //
    // Talent rank IDs sourced from CSV/TalentSpellRanks.csv (built from the
    // 1.14.2 Talent.dbc) — same data the talent-rank injection fix relies on.
    // Per-rank values pulled from LibThreatClassic2's ClassModules/Classic/
    // {Warrior,Druid,Priest}.lua so what we compute matches what existing
    // threat addons compute server-side from GetTalentInfo.

    // Stance / form spell IDs we watch on the player's aura table. HashSet so
    // the per-event scan is O(slots) with O(1) per-slot membership check.
    private static readonly HashSet<uint> StanceFormAuras = new()
    {
        71,    // Warrior — Defensive Stance
        2457,  // Warrior — Battle Stance
        2458,  // Warrior — Berserker Stance
        5487,  // Druid — Bear Form
        9634,  // Druid — Dire Bear Form
        768,   // Druid — Cat Form
        783,   // Druid — Travel Form
        1066,  // Druid — Aquatic Form
    };

    // Talent rank spell ID arrays — ordered rank 1 → rank N. Matched against
    // CurrentPlayerKnownSpells ∪ SynthesizedTalentRanks via GetTalentRank.
    // Source: 1.14.2 Talent.dbc cross-referenced against SpellName.dbc.
    //
    // Flat-passive layer (apply on GetPassiveModifier):
    //   Defiance        (Warrior Protection, talent_id 144)
    //   Feral Instinct  (Druid Feral Combat, talent_id 799)
    //   Silent Resolve  (Priest Discipline,  talent_id 352)
    //
    // School-gated layer (apply on GetSpellTalentMultiplier):
    //   Shadow Affinity        (Priest Shadow,         talent_id 461)
    //   Druid Subtlety         (Druid Restoration,     talent_id 841)
    //   Improved Righteous Fury (Paladin Holy,         talent_id 1501)
    //                                                   gated on RF aura 25780 active
    private static readonly uint[] DefianceRanks            = { 12303, 12788, 12789, 12791, 12792 };
    private static readonly uint[] FeralInstinctRanks       = { 16947, 16948, 16949, 16950, 16951 };
    private static readonly uint[] SilentResolveRanks       = { 14523, 14784, 14785, 14786, 14787 };
    private static readonly uint[] ShadowAffinityRanks      = { 15272, 15318, 15320 };
    private static readonly uint[] DruidSubtletyRanks       = { 17118, 17119, 17120, 17121, 17122 };
    private static readonly uint[] ImpRighteousFuryRanks    = { 20468, 20469, 20470 };
    private static readonly uint[] ImpPwsRanks              = { 14748, 14768, 14769 };

    private const uint RighteousFuryAura = 25780;

    // School-affected spell-ID sets, transcribed from LibThreatClassic2's
    // ClassModules/Classic/{Priest,Druid,Paladin}.lua. Each set is the canonical
    // list of vanilla spells the corresponding talent's multiplier applies to.
    //
    // Curated lists rather than school lookup because the proxy doesn't carry
    // SpellMisc.SchoolMask in a queryable form; LTC2 also curates because
    // school-based gating alone would miss heal/damage variants that share a
    // spell family but differ in school (e.g. paladin Hammer of Wrath is Holy
    // damage; Hammer of Justice is no-school CC — both Hammer-named).

    private static readonly HashSet<uint> ShadowAffinitySpells = new()
    {
        // Shadow Word: Pain (R1..8)
        589, 594, 970, 992, 2767, 10892, 10893, 10894,
        // Mind Blast (R1..9)
        8092, 8102, 8103, 8104, 8105, 8106, 10945, 10946, 10947,
        // Mind Flay (R1..6)
        15407, 17311, 17312, 17313, 17314, 18807,
        // Devouring Plague (R1..5)
        2944, 19276, 19277, 19278, 19279,
        // Vampiric Embrace (passive damage→heal — counted as shadow threat)
        15286,
    };

    // Spells the Righteous Fury (+ Imp RF) multiplier applies to. Holy damage
    // and Holy heals — paladin's RF aura boosts threat for both. List taken
    // from LTC2 Paladin.lua's HolyHealIDs + holyShieldIDs + paladin damage
    // catalogue. Heal-side application is gated to RF being active (no RF =
    // baseline 1.0); damage-side is gated likewise.
    private static readonly HashSet<uint> PaladinRighteousFurySpells = new()
    {
        // Consecration (R1..6)
        26573, 20116, 20922, 20923, 20924, 27983,
        // Holy Shield damage proc (R1..3) — also has its own 1.2x abilityMul
        20925, 20927, 20928,
        // Holy Shock damage (R1..3)
        25912, 25911, 25902,
        // Holy Shock heal (R1..3)
        25903, 25913, 25914,
        // Hammer of Wrath (R1..3)
        24239, 24274, 24275,
        // Holy Light (R1..9)
        635, 639, 647, 1026, 1042, 3472, 10328, 10329, 25292,
        // Flash of Light (R1..6)
        19750, 19939, 19940, 19941, 19942, 19943,
        // Lay on Hands (R1..3)
        633, 2800, 10310,
        // Judgement: server emits damage via "umbrella" IDs and per-seal IDs.
        // 23590 is the most common engine event; 20184/85/86/87/88, 20271, 20286,
        // 20425 cover Justice/Light/Wisdom/Righteousness/Crusader/Command variants.
        23590, 23591, 20271, 20184, 20185, 20186, 20187, 20188, 20286, 20425,
        // Seal of Righteousness damage proc (R1..8) — fires on every melee swing
        20154, 20287, 20288, 20289, 20290, 20291, 20292, 20293, 21084, 25713,
        // Holy Wrath (R1..2)
        2812, 10318,
        // Exorcism (R1..6)
        879, 5614, 5615, 10312, 10313, 10314,
    };

    // Returns the highest rank (1..N) of a talent the player has, or 0 if untaken.
    // Walks the rank ids in ascending order; the last index that matches the
    // player's known-spells set is the talent's current rank. Reads both the
    // real server-tracked CurrentPlayerKnownSpells and the proxy-injected
    // SynthesizedTalentRanks — without the latter, only the highest rank would
    // be visible (vanilla LearnTalent RemoveSpell's predecessors).
    private int GetTalentRank(uint[] rankIds)
    {
        var known = _session.GameState.CurrentPlayerKnownSpells;
        var synth = _session.GameState.SynthesizedTalentRanks;
        int highest = 0;
        for (int i = 0; i < rankIds.Length; i++)
        {
            uint sid = rankIds[i];
            if (known.Contains(sid) || synth.Contains(sid))
                highest = i + 1;
        }
        return highest;
    }

    // Cached snapshot of detected talent ranks so the diagnostic event below
    // only fires when the rank set actually changes (login, learn, respec).
    // Initialized to all -1 sentinels so the first call after construction
    // always emits a baseline snapshot for tester observability.
    private (int defiance, int feralInstinct, int silentResolve,
             int shadowAffinity, int druidSubtlety, int impRighteousFury,
             int impPws) _lastTalentSnapshot = (-1, -1, -1, -1, -1, -1, -1);

    // Emits a `threat.talent_snapshot` JSONL event whenever the detected
    // talent-rank set differs from the last observation. Lets a solo tester
    // run the proxy, log in on any character, attack any mob once, and grep
    // the bundle for the snapshot line to confirm the talent injection +
    // detection pipeline is working — even for characters that are too low
    // level to have any of these talents (all ranks would log as 0).
    private void SnapshotTalentStateIfChanged()
    {
        var playerClass = (Class)_session.GameState.CurrentPlayerClass;
        int defiance       = playerClass == Class.Warrior ? GetTalentRank(DefianceRanks)         : 0;
        int feralInstinct  = playerClass == Class.Druid   ? GetTalentRank(FeralInstinctRanks)    : 0;
        int silentResolve  = playerClass == Class.Priest  ? GetTalentRank(SilentResolveRanks)    : 0;
        int shadowAffinity = playerClass == Class.Priest  ? GetTalentRank(ShadowAffinityRanks)   : 0;
        int druidSubtlety  = playerClass == Class.Druid   ? GetTalentRank(DruidSubtletyRanks)    : 0;
        int irf            = playerClass == Class.Paladin ? GetTalentRank(ImpRighteousFuryRanks) : 0;
        int impPws         = playerClass == Class.Priest  ? GetTalentRank(ImpPwsRanks)           : 0;
        var current = (defiance, feralInstinct, silentResolve, shadowAffinity, druidSubtlety, irf, impPws);
        if (current == _lastTalentSnapshot)
            return;
        _lastTalentSnapshot = current;
        Log.Event("threat.talent_snapshot", new
        {
            player_class = (byte)playerClass,
            defiance_rank = defiance,
            feral_instinct_rank = feralInstinct,
            silent_resolve_rank = silentResolve,
            shadow_affinity_rank = shadowAffinity,
            druid_subtlety_rank = druidSubtlety,
            imp_righteous_fury_rank = irf,
            imp_pws_rank = impPws,
            real_known_count = _session.GameState.CurrentPlayerKnownSpells.Count,
            synthesized_count = _session.GameState.SynthesizedTalentRanks.Count,
        });
    }

    // Returns the Imp PW:S multiplier (1.0 + 0.05 × rank). Called from the
    // PW:S cast handler in ThreatModules. Public so the cast handler can
    // pre-multiply the table amount before adding threat.
    public double GetImpPwsMultiplier()
    {
        if (_session.GameState.CurrentPlayerClass != (byte)Class.Priest)
            return 1.0;
        int rank = GetTalentRank(ImpPwsRanks);
        return rank > 0 ? 1.0 + (0.05 * rank) : 1.0;
    }

    // PW:S cast threat: fixed per-rank amount × Imp PWS × passive (Silent
    // Resolve included via GetPassiveModifier) × distribution across all
    // mobs in combat with the shield recipient. Same shape as OnHeal but
    // uses the table amount instead of effective-heal × 0.5.
    public void OnPowerWordShield(WowGuid128 caster, WowGuid128 shieldTarget, int spellId, double baseAmount)
    {
        if (baseAmount <= 0) return;
        if (caster != _session.GameState.CurrentPlayerGuid) return;
        if (shieldTarget == default) return;

        List<WowGuid128>? mobsInCombat = null;
        foreach (var (mob, list) in _threatLists)
        {
            if (list.ContainsKey(shieldTarget))
            {
                mobsInCombat ??= new List<WowGuid128>();
                mobsInCombat.Add(mob);
            }
        }
        if (mobsInCombat == null || mobsInCombat.Count == 0)
            return;

        SnapshotTalentStateIfChanged();
        double passive = GetPassiveModifier(caster);
        double impPwsMult = GetImpPwsMultiplier();
        double totalThreat = baseAmount * impPwsMult * passive;
        double threatPerMob = totalThreat / mobsInCombat.Count;

        foreach (var mob in mobsInCombat)
            AddThreat(mob, caster, threatPerMob);

        Log.Event("threat.spell.power_word_shield", new
        {
            spell_id = spellId,
            shield_target_low = shieldTarget.GetCounter(),
            base_amount = baseAmount,
            imp_pws_mult = impPwsMult,
            passive_mod = passive,
            mobs_split = mobsInCombat.Count,
            threat_per_mob = (long)threatPerMob,
            total_threat = (long)totalThreat,
        });

        EmitDirty();
    }

    // Returns the passive multiplier to apply to threater's flat-add threat.
    // Phase 6.1 generalised to any group threater: class via party-list
    // lookup (or local-player class for self), stance / form via the
    // threater's own UNIT_FIELD_AURA cache. Pets / charms / unknown class
    // fall through to 1.0 since vanilla pets carry no stance / form /
    // class modifier.
    //
    // Side effect: emits threat.passive_modifier on every value change so
    // testers can correlate stance switches with threat shifts in the JSONL.
    private double GetPassiveModifier(WowGuid128 threater)
    {
        var threaterClass = GetThreaterClass(threater);
        uint formAura = ScanStanceFormAura(threater);
        double modifier = ComputePassiveModifier(threaterClass, formAura);

        // Talent layer only applies to the local player — the proxy can read its
        // own known-spells set but has no view into group members' talents.
        // Without this gate, group members would carry the local player's talent
        // multiplier (wrong), since the GetTalentMultiplier helper reads our
        // session's CurrentPlayerKnownSpells / SynthesizedTalentRanks.
        if (threater == _session.GameState.CurrentPlayerGuid)
            modifier *= GetTalentMultiplier(threaterClass, formAura);

        _passiveCache.TryGetValue(threater, out var cached);
        if (cached.stanceForm != formAura || cached.modifier != modifier)
        {
            Log.Event("threat.passive_modifier", new
            {
                threater_low = threater.GetCounter(),
                is_local_player = threater == _session.GameState.CurrentPlayerGuid,
                player_class = (byte)threaterClass,
                stance_form_spell = formAura,
                modifier,
                previous_modifier = cached.modifier,
                previous_stance_form_spell = cached.stanceForm,
            });
            _passiveCache[threater] = (formAura, modifier);
        }

        return modifier;
    }

    // Per-spell talent multiplier for school-gated talents. Reads the local
    // player's known-spells set to detect ranks; non-local-player attackers
    // always get 1.0 since we can't see their talents. The aura-gated case
    // (Paladin RF active) is checked here too.
    //
    // Applies on top of GetPassiveModifier — so a Paladin tank with 3/3 IRF
    // and RF active gets the flat passive (1.0 currently — paladins have no
    // flat-passive talent in the LTC2 model) × IRF multiplier of 1.9 for any
    // PaladinRighteousFurySpells.Contains(spellId) damage/heal event.
    public double GetSpellTalentMultiplier(WowGuid128 attacker, int spellId)
    {
        if (spellId <= 0) return 1.0;
        if (attacker != _session.GameState.CurrentPlayerGuid) return 1.0;
        uint sid = (uint)spellId;
        var playerClass = (Class)_session.GameState.CurrentPlayerClass;
        switch (playerClass)
        {
            case Class.Priest:
                if (ShadowAffinitySpells.Contains(sid))
                {
                    int rank = GetTalentRank(ShadowAffinityRanks);
                    // LTC2 explicit array: [0.92, 0.84, 0.75] — rank 3 is NOT
                    // 1-3*0.08=0.76 (the lib hard-codes the 0.75 for a slightly
                    // steeper drop at 3/3).
                    return rank switch { 1 => 0.92, 2 => 0.84, 3 => 0.75, _ => 1.0 };
                }
                return 1.0;
            case Class.Druid:
                // Druid Subtlety moved to GetTalentMultiplier (universal passive,
                // applies to heals too — was previously gated to Balance damage
                // spells only, missing the resto-druid heal-threat reduction).
                return 1.0;
            case Class.Paladin:
                if (PaladinRighteousFurySpells.Contains(sid) && IsRighteousFuryActive())
                {
                    int rank = GetTalentRank(ImpRighteousFuryRanks);
                    // LTC2 Paladin.lua: righteousFuryMod = 1 + 0.6 * (1 + irfRanks[rank+1])
                    // irfRanks = {0, 0.16, 0.33, 0.5}.
                    double irfBonus = rank switch { 1 => 0.16, 2 => 0.33, 3 => 0.50, _ => 0.0 };
                    return 1.0 + 0.6 * (1.0 + irfBonus);
                }
                return 1.0;
            default:
                return 1.0;
        }
    }

    // Scans the player's aura list for Righteous Fury (25780). Used to gate
    // Improved Righteous Fury multiplication in GetSpellTalentMultiplier.
    // Same scan shape as ScanStanceFormAura but with a single target spell id.
    private bool IsRighteousFuryActive()
    {
        var fields = _session.GameState.GetCachedObjectFieldsLegacy(_session.GameState.CurrentPlayerGuid);
        if (fields == null) return false;
        int unitFieldAura = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_AURA);
        if (unitFieldAura < 0) return false;
        int slots = LegacyVersion.GetAuraSlotsCount();
        for (int i = 0; i < slots; i++)
        {
            if (!fields.TryGetValue(unitFieldAura + i, out var field))
                continue;
            if (field.UInt32Value == RighteousFuryAura)
                return true;
        }
        return false;
    }

    // Computes the talent-layered multiplier to apply on top of the class+stance
    // baseline. Returns 1.0 for any class without known threat-talents (Hunter,
    // Mage, Shaman, Warlock, Paladin pre-Holy-spec — Paladin Imp Righteous Fury
    // is school-gated and lives in a separate code path).
    //
    // Each modifier follows the per-rank formula from LibThreatClassic2's
    // matching ClassModule. Talents that gate on a specific stance/form return
    // 1.0 outside the gating condition so respecs and form switches take effect
    // on the next GetPassiveModifier call without flushing any state.
    private double GetTalentMultiplier(Class playerClass, uint formAura)
    {
        switch (playerClass)
        {
            case Class.Warrior:
                // Defiance: +0.03 / rank, Defensive Stance only.
                if (formAura == 71)
                {
                    int rank = GetTalentRank(DefianceRanks);
                    if (rank > 0)
                        return 1.0 + (0.03 * rank);
                }
                return 1.0;

            case Class.Druid:
            {
                double mult = 1.0;
                // Feral Instinct: +0.03 / rank, Bear / Dire Bear only.
                if (formAura == 5487 || formAura == 9634)
                {
                    int fiRank = GetTalentRank(FeralInstinctRanks);
                    if (fiRank > 0) mult *= 1.0 + (0.03 * fiRank);
                }
                // Subtlety (Restoration tier 1): −0.04 / rank, applies to all
                // spells (damage AND heals). Universal per LTC2 Druid.lua —
                // not school-gated, despite the name's overlap with rogue Subtlety.
                int subRank = GetTalentRank(DruidSubtletyRanks);
                if (subRank > 0) mult *= 1.0 - (0.04 * subRank);
                return mult;
            }

            case Class.Priest:
                // Silent Resolve: −0.04 / rank, applies to all damage and heals.
                int srRank = GetTalentRank(SilentResolveRanks);
                if (srRank > 0)
                    return 1.0 - (0.04 * srRank);
                return 1.0;

            default:
                return 1.0;
        }
    }

    private Class GetThreaterClass(WowGuid128 guid)
    {
        if (guid == _session.GameState.CurrentPlayerGuid)
            return (Class)_session.GameState.CurrentPlayerClass;

        var groups = _session.GameState.CurrentGroups;
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            if (group == null) continue;
            foreach (var member in group.PlayerList)
            {
                if (member.GUID == guid)
                    return member.ClassId;
            }
        }
        return Class.None;
    }

    private uint ScanStanceFormAura(WowGuid128 guid)
    {
        var fields = _session.GameState.GetCachedObjectFieldsLegacy(guid);
        if (fields == null) return 0;

        int unitFieldAura = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_AURA);
        if (unitFieldAura < 0) return 0;

        int slots = LegacyVersion.GetAuraSlotsCount();
        for (int i = 0; i < slots; i++)
        {
            if (!fields.TryGetValue(unitFieldAura + i, out var field))
                continue;
            uint spellId = field.UInt32Value;
            if (spellId != 0 && StanceFormAuras.Contains(spellId))
                return spellId;
        }
        return 0;
    }

    private static double ComputePassiveModifier(Class playerClass, uint stanceFormAura)
    {
        switch (playerClass)
        {
            case Class.Rogue:
                return 0.71;

            case Class.Warrior:
                return stanceFormAura switch
                {
                    71   => 1.30, // Defensive Stance
                    2458 => 0.80, // Berserker Stance
                    2457 => 0.80, // Battle Stance — matches lib's quirk
                    _    => 0.80, // no stance — matches lib's else branch
                };

            case Class.Druid:
                return stanceFormAura switch
                {
                    5487 => 1.30, // Bear Form
                    9634 => 1.30, // Dire Bear Form
                    768  => 0.71, // Cat Form
                    783  => 0.71, // Travel Form
                    1066 => 0.71, // Aquatic Form
                    _    => 1.00, // caster form
                };

            default:
                return 1.0;
        }
    }

    private static long ToWireThreat(double rawThreat)
    {
        // Modern protocol packs threat × 100 and writes int64 (8 bytes — see
        // CombatPackets.cs comment). Classic Era addons divide by 100 on read
        // (verified in Details_TinyThreat.lua). Clamp to long range; practical
        // values are well within 32 bits but the wire field is 64.
        double scaled = rawThreat * 100.0;
        if (scaled <= 0) return 0;
        if (scaled >= long.MaxValue) return long.MaxValue;
        return (long)scaled;
    }

    private void SendToClient(ServerPacket packet)
    {
        var worldClient = _session.WorldClient;
        if (worldClient == null) return;
        worldClient.SendPacketToClient(packet);
    }
}

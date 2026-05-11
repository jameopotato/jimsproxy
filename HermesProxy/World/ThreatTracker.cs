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

        double abilityMultiplier = ThreatModules.GetDamageMultiplier(spellId);
        double passiveModifier = GetPassiveModifier(attacker);
        double scaledThreat = rawDamage * abilityMultiplier * passiveModifier;
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

        double passiveModifier = GetPassiveModifier(healer);
        double totalThreat = effectiveHeal * 0.5 * passiveModifier;
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
            mobs_split = mobsThreateningTarget.Count,
            threat_per_mob = (long)threatPerMob,
            total_threat = (long)totalThreat,
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
    // Phase 5 — passive threat multipliers from class + stance / form.
    //
    // Vanilla LibThreatClassic2 reads GetShapeshiftForm() and class talents to
    // compute a `passiveThreatModifiers` value applied to every flat-add threat
    // operation. We can't introspect talents from the proxy side (they live in
    // the client), so we ship the no-talent baselines:
    //
    //   Warrior:
    //     Defensive Stance (71)   → 1.30  (no Defiance)
    //     Berserker Stance (2458) → 0.80
    //     Battle Stance (2457) /
    //       no stance             → 0.80  (matches the lib's quirk; lib treats
    //                                      non-Defensive warriors as 0.8, see
    //                                      ClassModules/Classic/Warrior.lua)
    //
    //   Druid:
    //     Bear / Dire Bear Form
    //       (5487 / 9634)         → 1.30  (no Feral Instinct)
    //     Cat (768) /
    //       Travel (783) /
    //       Aquatic (1066)        → 0.71
    //     Caster                  → 1.00
    //
    //   Rogue:                    → 0.71  (always; passive at ClassEnable)
    //   All other classes:        → 1.00
    //
    // Future Phase 6+ will layer talent reads (Defiance, Feral Instinct,
    // Subtlety, Shadow Affinity, Improved PWS) on top — those need the proxy
    // to start mirroring talent state from CMSG_LEARN_TALENT and
    // SMSG_INITIALIZE_FACTIONS-era talent data. Until then, these baselines
    // run ~3-9% under the actual server-side numbers for talented characters.

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

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

    private readonly GlobalSessionData _session;

    // Passive threat multiplier cache. Recomputed on every threat operation
    // (cheap) but the threat.passive_modifier event only emits on change,
    // so testers can spot stance / form transitions without spamming logs.
    private double _cachedPassiveModifier = 1.0;
    private uint _cachedStanceFormAuraId = 0;

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

            // Find the top threater. Ties broken arbitrarily — vanilla itself
            // has tie-breaking quirks but they don't matter for display.
            WowGuid128 newHighest = default;
            double highestValue = -1;
            foreach (var (threater, value) in list)
            {
                if (value > highestValue)
                {
                    highestValue = value;
                    newHighest = threater;
                }
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

            // Emit HIGHEST only when the top changes — saves churn but keeps
            // tank-aggro indicators (red border, nameplate color) snappy.
            _lastHighest.TryGetValue(mob, out var prevHighest);
            if (prevHighest != newHighest)
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
            }
        }
    }

    // Wipe everything — used on session disconnect / character switch. Doesn't
    // emit packets since the client connection is going away anyway.
    public void Reset()
    {
        _threatLists.Clear();
        _lastHighest.Clear();
        _dirty.Clear();
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
        if (!IsLocalThreater(attacker)) return;
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
        if (!IsLocalThreater(healer)) return;
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

    // True if the given GUID is the local player or a unit they currently own
    // (pet, totem, guardian). Pet ownership is read from UNIT_FIELD_SUMMONEDBY
    // on the unit's cached fields, which the legacy server populates and the
    // proxy mirrors. We re-check on every event rather than caching because
    // pets get swapped (Hunter Call Pet, Warlock summon swap) and the cost
    // of one dictionary lookup per damage event is negligible.
    private bool IsLocalThreater(WowGuid128 guid)
    {
        if (guid == default) return false;
        var playerGuid = _session.GameState.CurrentPlayerGuid;
        if (guid == playerGuid) return true;

        var fields = _session.GameState.GetCachedObjectFieldsLegacy(guid);
        if (fields == null) return false;

        int summonedByIdx = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_SUMMONEDBY);
        if (summonedByIdx < 0) return false;

        WowGuid64 summonedBy64 = fields.GetGuidValue(summonedByIdx);
        if (summonedBy64 == WowGuid64.Empty) return false;

        WowGuid128 summonedBy128 = summonedBy64.To128(_session.GameState);
        return summonedBy128 == playerGuid;
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
    // Pet (and other local threaters that aren't the player) always return 1.0
    // since vanilla pets carry no stance / form / class modifier.
    //
    // Side effect: emits threat.passive_modifier on every value change so
    // testers can correlate stance switches with threat shifts in the JSONL.
    private double GetPassiveModifier(WowGuid128 threater)
    {
        if (threater != _session.GameState.CurrentPlayerGuid)
            return 1.0;

        uint formAura = ScanPlayerStanceFormAura();
        var playerClass = (Class)_session.GameState.CurrentPlayerClass;
        double modifier = ComputePassiveModifier(playerClass, formAura);

        if (formAura != _cachedStanceFormAuraId || modifier != _cachedPassiveModifier)
        {
            Log.Event("threat.passive_modifier", new
            {
                player_class = (byte)playerClass,
                stance_form_spell = formAura,
                modifier,
                previous_modifier = _cachedPassiveModifier,
                previous_stance_form_spell = _cachedStanceFormAuraId,
            });
            _cachedStanceFormAuraId = formAura;
            _cachedPassiveModifier = modifier;
        }

        return modifier;
    }

    private uint ScanPlayerStanceFormAura()
    {
        var fields = _session.GameState.GetCachedObjectFieldsLegacy(_session.GameState.CurrentPlayerGuid);
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

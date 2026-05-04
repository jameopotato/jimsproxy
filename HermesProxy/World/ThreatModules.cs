using Framework.Logging;
using HermesProxy.World.Objects;
using System.Collections.Generic;

namespace HermesProxy.World;

// Per-spell threat behavior for the local player and pet, ported from
// LibThreatClassic2 (KTMClassic addon). Phases roll out incrementally:
//   Phase 3: Hunter, Pet
//   Phase 4: Warrior, Druid, Mage, Rogue, Paladin (cast-success flat threat,
//            taunt-style set-to-top, zero-multiplier abilities, simple
//            add-to-all-mobs casts)
//
// Out of scope here (deferred):
//   - AbilityHandlers (per-damage spell-school multipliers, e.g. Maul x1.75,
//     Earth Shock x2, Searing Pain x2). Needs the OnDamage path to know which
//     spell school did the damage.
//   - MobDebuffHandlers (Demoralizing Shout, Demoralizing Roar, Faerie Fire
//     debuff stick). Needs SMSG_AURA_UPDATE observer.
//   - Stance / form / Righteous Fury passive multipliers and class talents
//     (Defiance, Feral Instinct, Subtlety, Shadow Affinity, Improved PWS).
//   - Multi-target effects that read party/raid composition (Greater Blessings).
//
// Caster gating: every handler re-checks that the caster is the local player
// (or pet, where applicable) before mutating threat. The OnDamage path already
// does the same check; we duplicate it here because spell hits propagate via
// SMSG_SPELL_GO for any caster in range, not just the local player.
internal static class ThreatModules
{
    private delegate void ThreatHandler(
        ThreatTracker tracker,
        GlobalSessionData session,
        int spellId,
        WowGuid128 caster,
        IList<WowGuid128> hitTargets);

    // Built lazily via the static constructor so field initializers for the
    // per-spell amount dictionaries (declared further down in the file) have
    // already run by the time BuildHandlers reads them. Inline-initializing
    // this field would run BuildHandlers FIRST (lexical order), which would
    // NRE on the still-null amount dictionaries.
    private static readonly Dictionary<int, ThreatHandler> Handlers;

    static ThreatModules()
    {
        Handlers = BuildHandlers();
    }

    public static bool TryHandle(
        ThreatTracker tracker,
        GlobalSessionData session,
        int spellId,
        WowGuid128 caster,
        IList<WowGuid128> hitTargets)
    {
        if (!Handlers.TryGetValue(spellId, out var handler))
            return false;

        handler(tracker, session, spellId, caster, hitTargets);
        return true;
    }

    // Phase 6 — per-spell damage threat multiplier. Looked up on every
    // OnDamage event with a non-zero spell id. Values mirror
    // LibThreatClassic2's AbilityHandlers entries; spells not in the table
    // (and melee swings, which arrive with spellId=0) get the implicit 1.0×.
    //
    // Set-bonus modifiers (Mage Netherwind, Warlock Plagueheart/Nemesis,
    // Rogue Bonescythe) and talent-based school multipliers are deferred —
    // those need item-set / talent introspection from the proxy side.
    private static readonly Dictionary<int, double> DamageMultipliers = new()
    {
        // Druid Maul (R1..7) — bear-form spike attack, double-threat per the lib
        [6807] = 1.75, [6808] = 1.75, [6809] = 1.75,
        [8972] = 1.75, [9745] = 1.75, [9880] = 1.75, [9881] = 1.75,

        // Druid Swipe (R1..5)
        [779]  = 1.75, [780]  = 1.75, [769]  = 1.75,
        [9754] = 1.75, [9908] = 1.75,

        // Warlock Searing Pain (R1..6) — high-threat dps spell by design
        [5676] = 2.0, [17919] = 2.0, [17920] = 2.0,
        [17921] = 2.0, [17922] = 2.0, [17923] = 2.0,

        // Priest Mind Blast (R1..9)
        [8092] = 2.0, [8102] = 2.0, [8103] = 2.0,
        [8104] = 2.0, [8105] = 2.0, [8106] = 2.0,
        [10945] = 2.0, [10946] = 2.0, [10947] = 2.0,

        // Priest Holy Nova damage component (R1..6) — generates no threat
        [15237] = 0.0, [15430] = 0.0, [15431] = 0.0,
        [27799] = 0.0, [27800] = 0.0, [27801] = 0.0,

        // Priest Shadowguard reflect (R1..6) — generates no threat
        [18137] = 0.0, [19308] = 0.0, [19309] = 0.0,
        [19310] = 0.0, [19311] = 0.0, [19312] = 0.0,

        // Shaman Earth Shock (R1..7) — designed as the shaman tank-equivalent
        // taunt, lib applies 2x to give it real teeth on a threat meter
        [8042] = 2.0, [8044] = 2.0, [8045] = 2.0,
        [8046] = 2.0, [10412] = 2.0, [10413] = 2.0, [10414] = 2.0,

        // Paladin Holy Shield reflect damage (R1..3)
        [20925] = 1.2, [20927] = 1.2, [20928] = 1.2,
    };

    public static double GetDamageMultiplier(int spellId)
    {
        if (spellId <= 0) return 1.0;
        return DamageMultipliers.TryGetValue(spellId, out double mult) ? mult : 1.0;
    }

    // -----------------------------------------------------------------------
    // Per-spell threat tables (rank → amount). Numbers come straight from
    // LibThreatClassic2 ClassModules/Classic/*.lua.
    // -----------------------------------------------------------------------

    // Hunter
    private static readonly Dictionary<int, double> DistractingShotAmount = new()
    {
        [20736] = 120, [14274] = 200, [15629] = 300,
        [15630] = 400, [15631] = 500, [15632] = 600,
    };
    private static readonly Dictionary<int, double> DisengageAmount = new()
    {
        [781] = -140, [14272] = -280, [14273] = -405,
    };

    // Pet
    private static readonly Dictionary<int, double> GrowlAmount = new()
    {
        [2649] = 50, [14916] = 65, [14917] = 110,
        [14918] = 170, [14919] = 240, [14920] = 320, [14921] = 415,
    };
    private static readonly Dictionary<int, double> CowerPetAmount = new()
    {
        [1742] = -30, [1753] = -55, [1754] = -85,
        [1755] = -125, [1756] = -175, [16697] = -225,
    };

    // Warrior — sunderFactor = 261/58, with R5 hardcoded to 261.
    private static readonly Dictionary<int, double> SunderArmorAmount = new()
    {
        [7386]  = 261.0 / 58.0 * 10,
        [7405]  = 261.0 / 58.0 * 22,
        [8380]  = 261.0 / 58.0 * 34,
        [11596] = 261.0 / 58.0 * 46,
        [11597] = 261,
    };
    private static readonly Dictionary<int, double> HeroicStrikeAmount = new()
    {
        [78] = 20, [284] = 39, [285] = 59, [1608] = 78,
        [11564] = 98, [11565] = 118, [11566] = 137,
        [11567] = 145, [25286] = 175,
    };
    private static readonly Dictionary<int, double> CleaveAmount = new()
    {
        [845] = 10, [7369] = 40, [11608] = 60, [11609] = 70, [20569] = 100,
    };
    // Revenge — revengeFactor = 355/60. Vanilla hardcodes the top two ranks.
    private static readonly Dictionary<int, double> RevengeAmount = new()
    {
        [6572]  = 355.0 / 60.0 * 14,
        [6574]  = 355.0 / 60.0 * 24,
        [7379]  = 355.0 / 60.0 * 34,
        [11600] = 355.0 / 60.0 * 44,
        [11601] = 315,
        [25288] = 355,
    };
    // Hamstring — hamstringFactor = 145/54.
    private static readonly Dictionary<int, double> HamstringAmount = new()
    {
        [1715] = 145.0 / 54.0 * 8,
        [7372] = 145.0 / 54.0 * 32,
        [7373] = 145,
    };
    // Shield Slam — shieldSlamFactor = 250/60.
    private static readonly Dictionary<int, double> ShieldSlamAmount = new()
    {
        [23922] = 250.0 / 60.0 * 40,
        [23923] = 250.0 / 60.0 * 48,
        [23924] = 250.0 / 60.0 * 54,
        [23925] = 250,
    };
    // Shield Bash — shieldBashFactor = 187/52.
    private static readonly Dictionary<int, double> ShieldBashAmount = new()
    {
        [72]   = 187.0 / 52.0 * 12,
        [1671] = 187.0 / 52.0 * 32,
        [1672] = 187,
    };
    // Thunder Clap — thunderClapFactor = 130/58.
    private static readonly Dictionary<int, double> ThunderClapAmount = new()
    {
        [6343]  = 130.0 / 58.0 * 6,
        [8198]  = 130.0 / 58.0 * 18,
        [8204]  = 130.0 / 58.0 * 28,
        [8205]  = 130.0 / 58.0 * 38,
        [11580] = 130.0 / 58.0 * 48,
        [11581] = 130,
    };
    private static readonly Dictionary<int, double> DisarmAmount = new()
    {
        [676] = 104,
    };

    // Druid
    private static readonly Dictionary<int, double> FaerieFireAmount = new()
    {
        // caster faerie fire — factor 108/54
        [770]   = 108.0 / 54.0 * 18,
        [778]   = 108.0 / 54.0 * 30,
        [9749]  = 108.0 / 54.0 * 42,
        [9907]  = 108,
        // feral
        [16857] = 108.0 / 54.0 * 18,
        [17390] = 108.0 / 54.0 * 30,
        [17391] = 108.0 / 54.0 * 42,
        [17392] = 108,
    };
    private static readonly Dictionary<int, double> CowerDruidAmount = new()
    {
        [8998] = -240, [9000] = -390, [9892] = -600,
    };

    // Rogue — Feint scales NEGATIVE.
    private static readonly Dictionary<int, double> FeintAmount = new()
    {
        [1966] = -150, [6768] = -240, [8637] = -390,
        [11303] = -600, [25302] = -800,
    };

    // Warrior Demoralizing Shout — demoShoutFactor = 43/54.
    private static readonly Dictionary<int, double> DemoralizingShoutAmount = new()
    {
        [1160]  = 43.0 / 54.0 * 14,
        [6190]  = 43.0 / 54.0 * 24,
        [11554] = 43.0 / 54.0 * 34,
        [11555] = 43.0 / 54.0 * 44,
        [11556] = 43,
    };

    // Druid Demoralizing Roar — flat per rank.
    private static readonly Dictionary<int, double> DemoralizingRoarAmount = new()
    {
        [99]   = 9,
        [1735] = 15,
        [9490] = 20,
        [9747] = 30,
        [9898] = 39,
    };

    // Warrior Battle Shout — adds threat to all mobs in combat with caster
    // when the buff goes out (per-rank flat).
    private static readonly Dictionary<int, double> BattleShoutAmount = new()
    {
        [6673]  = 1,
        [5242]  = 12,
        [6192]  = 22,
        [11549] = 32,
        [11550] = 42,
        [11551] = 52,
        [25289] = 60,
    };

    // Paladin Lesser Blessings — small flat threat to every mob in combat
    // each time the buff is cast (per-rank, per-blessing-type from the lib).
    // Greater Blessings deferred — those scale by raid class headcount,
    // which needs raid roster mirroring we don't have yet.
    private static readonly Dictionary<int, double> LesserBlessingAmount = new()
    {
        // Kings
        [20217] = 20,
        // Light
        [19977] = 40, [19978] = 50, [19979] = 60,
        // Might
        [19740] = 4, [19834] = 12, [19835] = 22, [19836] = 32,
        [19837] = 42, [19838] = 52, [25291] = 60,
        // Sanctuary
        [20911] = 30, [20912] = 40, [20913] = 50, [20914] = 60,
        // Salvation
        [1038]  = 26,
        // Wisdom
        [19742] = 14, [19850] = 24, [19852] = 34, [19853] = 44,
        [19854] = 54, [25290] = 60,
        // Freedom
        [1044]  = 18,
        // Protection
        [1022]  = 10, [5599]  = 24, [10278] = 38,
        // Sacrifice
        [6940]  = 46, [20729] = 54,
    };

    // -----------------------------------------------------------------------
    // Generic helpers — parametrize the common shapes so each ability boils
    // down to one entry in the registry.
    // -----------------------------------------------------------------------

    private static ThreatHandler PlayerSingleTargetFlat(string eventTag, Dictionary<int, double> amounts)
        => (tracker, session, spellId, caster, hitTargets) =>
        {
            if (caster != session.GameState.CurrentPlayerGuid) return;
            if (hitTargets.Count == 0) return;
            if (!amounts.TryGetValue(spellId, out double amount)) return;

            var target = hitTargets[0];
            tracker.AddModifiedThreat(target, caster, amount);

            Log.Event("threat.spell." + eventTag, new
            {
                spell_id = spellId,
                target_low = target.GetCounter(),
                amount,
            });
        };

    // For abilities that apply the same flat threat to every mob they hit
    // (Demoralizing Shout, Demoralizing Roar, etc.). Lib treats these as
    // MobDebuffHandlers — fires once per target when the debuff lands. Our
    // approximation is to iterate the SMSG_SPELL_GO HitTargets list, which
    // the legacy server populates only with mobs that didn't resist.
    private static ThreatHandler PlayerMultiTargetFlat(string eventTag, Dictionary<int, double> amounts)
        => (tracker, session, spellId, caster, hitTargets) =>
        {
            if (caster != session.GameState.CurrentPlayerGuid) return;
            if (hitTargets.Count == 0) return;
            if (!amounts.TryGetValue(spellId, out double amount)) return;

            foreach (var target in hitTargets)
                tracker.AddModifiedThreat(target, caster, amount);

            Log.Event("threat.spell." + eventTag, new
            {
                spell_id = spellId,
                target_count = hitTargets.Count,
                amount,
            });
        };

    private static ThreatHandler PetSingleTargetFlat(string eventTag, Dictionary<int, double> amounts)
        => (tracker, session, spellId, caster, hitTargets) =>
        {
            if (caster != session.GameState.CurrentPetGuid) return;
            if (hitTargets.Count == 0) return;
            if (!amounts.TryGetValue(spellId, out double amount)) return;

            var target = hitTargets[0];
            tracker.AddModifiedThreat(target, caster, amount);

            Log.Event("threat.spell." + eventTag, new
            {
                spell_id = spellId,
                target_low = target.GetCounter(),
                amount,
            });
        };

    private static ThreatHandler PlayerSetToTop(string eventTag)
        => (tracker, session, spellId, caster, hitTargets) =>
        {
            if (caster != session.GameState.CurrentPlayerGuid) return;
            if (hitTargets.Count == 0) return;

            var target = hitTargets[0];
            tracker.SetToTop(target, caster);

            Log.Event("threat.spell." + eventTag, new
            {
                spell_id = spellId,
                target_low = target.GetCounter(),
            });
        };

    private static ThreatHandler PlayerZeroAllMobs(string eventTag)
        => (tracker, session, spellId, caster, hitTargets) =>
        {
            if (caster != session.GameState.CurrentPlayerGuid) return;

            tracker.MultiplyThreat(caster, 0.0);

            Log.Event("threat.spell." + eventTag, new
            {
                spell_id = spellId,
                caster_low = caster.GetCounter(),
            });
        };

    private static ThreatHandler PlayerAddToAllMobs(string eventTag, double amount)
        => (tracker, session, spellId, caster, hitTargets) =>
        {
            if (caster != session.GameState.CurrentPlayerGuid) return;

            tracker.AddThreatToAllMobs(caster, amount);

            Log.Event("threat.spell." + eventTag, new
            {
                spell_id = spellId,
                caster_low = caster.GetCounter(),
                amount,
            });
        };

    // Per-rank variant for shouts and blessings — adds the rank's flat amount
    // to every mob the caster currently has on a threat list.
    private static ThreatHandler PlayerAddToAllMobsByRank(string eventTag, Dictionary<int, double> amounts)
        => (tracker, session, spellId, caster, hitTargets) =>
        {
            if (caster != session.GameState.CurrentPlayerGuid) return;
            if (!amounts.TryGetValue(spellId, out double amount)) return;

            tracker.AddThreatToAllMobs(caster, amount);

            Log.Event("threat.spell." + eventTag, new
            {
                spell_id = spellId,
                caster_low = caster.GetCounter(),
                amount,
            });
        };

    // -----------------------------------------------------------------------
    // Registry — every spell ID we care about lands here. Adding a new ability
    // is one entry plus (optionally) a per-rank amount table above.
    // -----------------------------------------------------------------------

    private static Dictionary<int, ThreatHandler> BuildHandlers()
    {
        var map = new Dictionary<int, ThreatHandler>();

        // Hunter
        var hunterDistractingShot = PlayerSingleTargetFlat("distracting_shot", DistractingShotAmount);
        foreach (var id in DistractingShotAmount.Keys) map[id] = hunterDistractingShot;

        var hunterDisengage = PlayerSingleTargetFlat("disengage", DisengageAmount);
        foreach (var id in DisengageAmount.Keys) map[id] = hunterDisengage;

        map[5384] = PlayerZeroAllMobs("feign_death");

        // Pet
        var petGrowl = PetSingleTargetFlat("growl", GrowlAmount);
        foreach (var id in GrowlAmount.Keys) map[id] = petGrowl;

        var petCower = PetSingleTargetFlat("cower_pet", CowerPetAmount);
        foreach (var id in CowerPetAmount.Keys) map[id] = petCower;

        // Warrior
        var sunder = PlayerSingleTargetFlat("sunder_armor", SunderArmorAmount);
        foreach (var id in SunderArmorAmount.Keys) map[id] = sunder;

        var heroicStrike = PlayerSingleTargetFlat("heroic_strike", HeroicStrikeAmount);
        foreach (var id in HeroicStrikeAmount.Keys) map[id] = heroicStrike;

        var cleave = PlayerSingleTargetFlat("cleave", CleaveAmount);
        foreach (var id in CleaveAmount.Keys) map[id] = cleave;

        var revenge = PlayerSingleTargetFlat("revenge", RevengeAmount);
        foreach (var id in RevengeAmount.Keys) map[id] = revenge;

        var hamstring = PlayerSingleTargetFlat("hamstring", HamstringAmount);
        foreach (var id in HamstringAmount.Keys) map[id] = hamstring;

        var shieldSlam = PlayerSingleTargetFlat("shield_slam", ShieldSlamAmount);
        foreach (var id in ShieldSlamAmount.Keys) map[id] = shieldSlam;

        var shieldBash = PlayerSingleTargetFlat("shield_bash", ShieldBashAmount);
        foreach (var id in ShieldBashAmount.Keys) map[id] = shieldBash;

        var thunderClap = PlayerSingleTargetFlat("thunder_clap", ThunderClapAmount);
        foreach (var id in ThunderClapAmount.Keys) map[id] = thunderClap;

        var disarm = PlayerSingleTargetFlat("disarm", DisarmAmount);
        foreach (var id in DisarmAmount.Keys) map[id] = disarm;

        // Warrior — Taunt (set-to-top). Vanilla addon also gates on "is taunt
        // immune?", but we apply unconditionally — the legacy server already
        // resolved immunity before sending SMSG_SPELL_GO, so the cast having
        // landed is itself the success signal.
        map[355] = PlayerSetToTop("taunt");

        // Warrior — Mocking Blow ranks 1..5, also taunt-style.
        var mockingBlow = PlayerSetToTop("mocking_blow");
        foreach (var id in new[] { 694, 7400, 7402, 20559, 20560 }) map[id] = mockingBlow;

        // Druid
        var faerieFire = PlayerSingleTargetFlat("faerie_fire", FaerieFireAmount);
        foreach (var id in FaerieFireAmount.Keys) map[id] = faerieFire;

        var cowerDruid = PlayerSingleTargetFlat("cower_druid", CowerDruidAmount);
        foreach (var id in CowerDruidAmount.Keys) map[id] = cowerDruid;

        // Druid Growl is a player taunt (bear form).
        map[6795] = PlayerSetToTop("growl_druid");

        // Mage
        map[2139] = PlayerSingleTargetFlat("counterspell", new Dictionary<int, double> { [2139] = 300 });
        map[475]  = PlayerAddToAllMobs("remove_lesser_curse", 14);

        // Rogue
        var feint = PlayerSingleTargetFlat("feint", FeintAmount);
        foreach (var id in FeintAmount.Keys) map[id] = feint;

        var vanish = PlayerZeroAllMobs("vanish");
        foreach (var id in new[] { 1856, 1857 }) map[id] = vanish;

        // Paladin
        map[4987] = PlayerAddToAllMobs("cleanse", 40);

        // Warrior Demoralizing Shout — multi-target debuff threat.
        var demoShout = PlayerMultiTargetFlat("demoralizing_shout", DemoralizingShoutAmount);
        foreach (var id in DemoralizingShoutAmount.Keys) map[id] = demoShout;

        // Druid Demoralizing Roar — multi-target debuff threat.
        var demoRoar = PlayerMultiTargetFlat("demoralizing_roar", DemoralizingRoarAmount);
        foreach (var id in DemoralizingRoarAmount.Keys) map[id] = demoRoar;

        // Warrior Battle Shout — flat add to every mob in combat with caster.
        var battleShout = PlayerAddToAllMobsByRank("battle_shout", BattleShoutAmount);
        foreach (var id in BattleShoutAmount.Keys) map[id] = battleShout;

        // Paladin Lesser Blessings — flat add to every mob in combat.
        var lesserBlessing = PlayerAddToAllMobsByRank("lesser_blessing", LesserBlessingAmount);
        foreach (var id in LesserBlessingAmount.Keys) map[id] = lesserBlessing;

        return map;
    }
}

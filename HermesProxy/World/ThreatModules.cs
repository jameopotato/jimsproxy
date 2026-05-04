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

        return map;
    }
}

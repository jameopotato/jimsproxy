using Framework.Logging;

using nietras.SeparatedValues;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HermesProxy.World;

// JimsProxy (unit-scale): one translation for the size of every unit.
//
// A 1.12 server puts a unit's whole display size into OBJECT_FIELD_SCALE_X: the display's
// CreatureModelScale times its model's ModelScale, times whatever the server adds on top
// (aura scale mods, creature template scale, hunter pet growth). Blizzard's own 1.8 server
// did it this way (in a 2005 Blackwing Lair capture every creature's SCALE_X equals its
// display's CreatureModelScale, up to 5.0), and Kronos does it for creatures, pets and
// players alike: gnomes 1.15, female tauren 1.25 (= ModelScale 1.25), cat form 0.9, the
// Hallow's End goblin pirate costume 1.5. The 1.12 client draws that value as it is.
//
// The modern client sizes a unit as ObjectData.Scale times its own display factor, built
// from its DB2 data for the unit's current DisplayID:
//
//   s = CreatureModelScale (a player uses CreatureDisplayInfo.PlayerOverrideScale instead
//       when that is non-zero; no 1.14.2 row sets it)
//     x the CreatureModelScale of the race model named by the display's
//       CreatureDisplayInfoExtra row (humanoid NPC displays; 1.0 without one)
//     x UnitData.DisplayScale (the proxy always sends 1.0)
//     x CreatureModelData.ModelScale
//   then, for a creature whose template names a CreatureFamily:
//     s = the family's level scale, if that is larger or the unit is a pet (PetNumber != 0)
//     and a pet's s is multiplied by its model's TamedPetBaseScale.
//
// Forwarding SCALE_X unchanged therefore counts the display size twice (the goblin costume
// drew at 1.5 x 1.5; every gnome at 1.15 x 1.15). Sending SCALE_X / s hands the client the
// 1.12 size exactly, and it is what a modern server sends for the same unit (1.0 for an
// ordinary one; for a hunter pet the family level scale the server already sent cancels).
//
// Everything that sizes a unit goes through here: the values-update scale, its re-sends
// when the display, level or pet number changes, the re-send when a creature's template
// (its family) arrives, and the Scale in the synthesized collision-height packet, which
// lands in the same client slot as ObjectData.Scale (the last one to arrive wins).
//
// Data: CSV/UnitScale*<exp>.csv (generated from the client's DB2 by
// scripts/gen-unit-scale-csv.py), CSV/CreatureFamily.csv, and the CreatureDisplayInfo /
// CreatureDisplayInfoExtra rows the proxy pushes as hotfixes (those replace the client's).
public static class UnitScale
{
    public readonly record struct DisplayRow(uint ModelId, float CreatureModelScale, uint ExtendedDisplayInfoId, float PlayerOverrideScale);
    public readonly record struct DisplayRace(int RaceId, int Sex);
    public readonly record struct ModelRow(float ModelScale, float TamedPetBaseScale);

    // What the client knows about the unit when it sizes it. FamilyId is the CreatureFamily
    // of the creature's template (0 = none or not known yet); players have no template.
    public readonly record struct UnitInputs(bool IsPlayer, uint DisplayId, int Level, int FamilyId, bool IsPet);

    // The factor and how it was built, for the diagnostic event and the tests.
    public readonly record struct Factor(
        float Value,
        float DisplayScale,
        float RaceModelScale,
        float ModelScale,
        float FamilyScale,
        bool FamilyApplied,
        bool KnownDisplay);

    // Below this the client treats PlayerOverrideScale as unset.
    const float PlayerOverrideEpsilon = 2.4e-7f;

    static FrozenDictionary<uint, DisplayRow> s_displays = FrozenDictionary<uint, DisplayRow>.Empty;
    static FrozenDictionary<uint, DisplayRace> s_displayRaces = FrozenDictionary<uint, DisplayRace>.Empty;
    static FrozenDictionary<(int Race, int Sex), uint> s_raceModelDisplays = FrozenDictionary<(int, int), uint>.Empty;
    static FrozenDictionary<uint, ModelRow> s_models = FrozenDictionary<uint, ModelRow>.Empty;
    static FrozenDictionary<uint, DisplayRow> s_displayHotfixes = FrozenDictionary<uint, DisplayRow>.Empty;
    static FrozenDictionary<uint, DisplayRace> s_displayRaceHotfixes = FrozenDictionary<uint, DisplayRace>.Empty;

    // The tables exist for the client build (1.14.x today). Without them the proxy has no
    // way to know the client's factor and forwards SCALE_X unchanged.
    public static bool HasClientData => s_displays.Count > 0;

    // Translate only from a 1.12 server: later servers already send a modern scale.
    public static bool TranslationEnabled => LegacyVersion.ExpansionVersion == 1 && HasClientData;

    public static void LoadClientData() => LoadClientData("CSV", ModernVersion.ExpansionVersion);

    public static void LoadClientData(string directory, int expansion)
    {
        string displaysPath = Path.Combine(directory, $"UnitScaleDisplays{expansion}.csv");
        if (!File.Exists(displaysPath))
        {
            // Expected for a client build with no tables (the scale then passes through).
            Log.Print(LogType.Debug, $"No unit scale tables for client expansion {expansion} ({displaysPath}); unit scale passes through.");
            return;
        }

        var displays = new Dictionary<uint, DisplayRow>(10600);
        using (var reader = Sep.Reader(o => o with { HasHeader = true }).FromFile(displaysPath))
        {
            foreach (var row in reader)
                displays[ParseUInt(row[0].Span)] = new DisplayRow(ParseUInt(row[1].Span), ParseFloat(row[2].Span), ParseUInt(row[3].Span), ParseFloat(row[4].Span));
        }

        var races = new Dictionary<uint, DisplayRace>(7000);
        using (var reader = Sep.Reader(o => o with { HasHeader = true }).FromFile(Path.Combine(directory, $"UnitScaleDisplayRaces{expansion}.csv")))
        {
            foreach (var row in reader)
                races[ParseUInt(row[0].Span)] = new DisplayRace((int)ParseUInt(row[1].Span), (int)ParseUInt(row[2].Span));
        }

        var raceModels = new Dictionary<(int, int), uint>(32);
        using (var reader = Sep.Reader(o => o with { HasHeader = true }).FromFile(Path.Combine(directory, $"UnitScaleRaceModels{expansion}.csv")))
        {
            foreach (var row in reader)
                raceModels[((int)ParseUInt(row[0].Span), (int)ParseUInt(row[1].Span))] = ParseUInt(row[2].Span);
        }

        var models = new Dictionary<uint, ModelRow>(512);
        using (var reader = Sep.Reader(o => o with { HasHeader = true }).FromFile(Path.Combine(directory, $"UnitScaleModels{expansion}.csv")))
        {
            foreach (var row in reader)
                models[ParseUInt(row[0].Span)] = new ModelRow(ParseFloat(row[1].Span), ParseFloat(row[2].Span));
        }

        s_displayRaces = races.ToFrozenDictionary();
        s_raceModelDisplays = raceModels.ToFrozenDictionary();
        s_models = models.ToFrozenDictionary();
        s_displays = displays.ToFrozenDictionary();
    }

    // The CreatureDisplayInfo / CreatureDisplayInfoExtra rows the proxy pushes as hotfixes
    // replace the client's own rows, so the factor must read them (GameData's hotfix loaders).
    internal static void SetDisplayHotfixes(Dictionary<uint, DisplayRow> rows) => s_displayHotfixes = rows.ToFrozenDictionary();
    internal static void SetDisplayRaceHotfixes(Dictionary<uint, DisplayRace> rows) => s_displayRaceHotfixes = rows.ToFrozenDictionary();

    public static float ClientDisplayFactor(in UnitInputs unit) => ComputeFactor(unit).Value;

    public static Factor ComputeFactor(in UnitInputs unit)
    {
        if (!TryGetDisplay(unit.DisplayId, out var display))
            return new Factor(1.0f, 1.0f, 1.0f, 1.0f, 0f, false, false);

        float displayScale = unit.IsPlayer && MathF.Abs(display.PlayerOverrideScale) >= PlayerOverrideEpsilon
            ? display.PlayerOverrideScale
            : display.CreatureModelScale;
        float raceModelScale = RaceModelScale(display.ExtendedDisplayInfoId);
        var model = GetModel(display.ModelId);

        float s = displayScale * raceModelScale * model.ModelScale;
        if (s <= 0f)
            s = 1.0f;

        float familyScale = 0f;
        bool familyApplied = false;
        if (!unit.IsPlayer && unit.FamilyId > 0 && GameData.CreatureFamilies.TryGetValue(unit.FamilyId, out var family))
        {
            familyScale = FamilyLevelScale(family, unit.Level);
            if (familyScale > s || unit.IsPet)
            {
                s = familyScale;
                familyApplied = true;
            }
            // Inside the family block on purpose: the client applies TamedPetBaseScale only to a
            // pet whose template has a family. (1.14.2: 1.0 on every model but 13195.)
            if (unit.IsPet && model.TamedPetBaseScale > 0f)
                s *= model.TamedPetBaseScale;
        }

        return new Factor(s, displayScale, raceModelScale, model.ModelScale, familyScale, familyApplied, true);
    }

    // A display built from a race's character model (a CreatureDisplayInfoExtra row): a
    // humanoid NPC, which never has a CreatureFamily, so its factor needs no template.
    public static bool IsCharacterModelDisplay(uint displayId)
        => TryGetDisplay(displayId, out var display) && display.ExtendedDisplayInfoId != 0;

    // The model the client draws for a display (0 when it has no row for it).
    public static uint ClientModelId(uint displayId) => TryGetDisplay(displayId, out var display) ? display.ModelId : 0;

    // The size the server meant, handed to a client that multiplies in its own factor.
    // A zero factor (a family with no scale) or a zero scale is left alone.
    public static float ToClientObjectScale(float serverScale, float factor)
        => serverScale > 0f && factor > 0f ? serverScale / factor : serverScale;

    // CreatureFamily scale by level: MinScale at MinScaleLevel and below, MaxScale at
    // MaxScaleLevel and above, linear in between.
    public static float FamilyLevelScale(GameData.CreatureFamilyData family, int level)
    {
        int span = family.MaxScaleLevel - family.MinScaleLevel;
        if (span == 0)
            return family.MinScale;
        int steps = Math.Min(Math.Max(level - family.MinScaleLevel, 0), span);
        return family.MinScale + (family.MaxScale - family.MinScale) * steps / span;
    }

    static bool TryGetDisplay(uint displayId, out DisplayRow display)
    {
        if (s_displayHotfixes.TryGetValue(displayId, out display))
            return true;
        if (s_displays.TryGetValue(displayId, out display))
            return true;

        // Not in the client's tables (no tables for this client build, or a display the
        // client lacks): fall back to the proxy's general display list so the collision
        // height keeps working; a display the client lacks is not drawn anyway.
        var legacy = GameData.GetDisplayInfo(displayId);
        if (legacy.ModelId == 0)
            return false;
        display = new DisplayRow(legacy.ModelId, GameData.GetClientEffectiveDisplayScale(displayId), 0, 0f);
        return true;
    }

    static ModelRow GetModel(uint modelId)
    {
        if (s_models.TryGetValue(modelId, out var model))
            return model;
        return new ModelRow(GameData.GetModelData(modelId).ModelScale, 1.0f);
    }

    static float RaceModelScale(uint extendedDisplayInfoId)
    {
        if (extendedDisplayInfoId == 0)
            return 1.0f;
        if (!s_displayRaceHotfixes.TryGetValue(extendedDisplayInfoId, out var race)
            && !s_displayRaces.TryGetValue(extendedDisplayInfoId, out race))
            return 1.0f;
        if (!s_raceModelDisplays.TryGetValue((race.RaceId, race.Sex), out uint raceDisplayId))
            return 1.0f;
        if (s_displayHotfixes.TryGetValue(raceDisplayId, out var hotfixRow))
            return hotfixRow.CreatureModelScale;
        if (s_displays.TryGetValue(raceDisplayId, out var raceRow))
            return raceRow.CreatureModelScale;
        return 1.0f;
    }

    static uint ParseUInt(ReadOnlySpan<char> text) => uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    static float ParseFloat(ReadOnlySpan<char> text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// One load of the data the unit scale factor reads. The hotfix loaders add to a
// process-wide table, so every test class that needs them goes through here.
internal static class UnitScaleTestData
{
    private static readonly object s_lock = new();
    private static bool s_loaded;

    public static void EnsureLoaded()
    {
        lock (s_lock)
        {
            if (s_loaded)
                return;
            GameData.LoadCreatureDisplayInfo();
            GameData.LoadCreatureModelCollisionHeights(Path.Combine("CSV", "CreatureModelCollisionHeightsModern1.csv"));
            GameData.LoadCreatureFamilies();
            GameData.LoadCreatureDisplayInfoHotfixes(Path.Combine("CSV", "Hotfix", "CreatureDisplayInfo1.csv"));
            GameData.LoadCreatureDisplayInfoExtraHotfixes(Path.Combine("CSV", "Hotfix", "CreatureDisplayInfoExtra1.csv"));
            UnitScale.LoadClientData("CSV", 1);
            s_loaded = true;
        }
    }
}

/// <summary>
/// Unit scale (UnitScale.cs): the proxy sends the 1.12 server's SCALE_X divided by the
/// display factor the modern client multiplies in itself, so the client draws the 1.12 size.
/// Server scales are from Kronos field logs (503 sessions); factors from the client's
/// 1.14.2 DB2 data plus the proxy's own CreatureDisplayInfo hotfix rows.
/// </summary>
public class UnitScaleTests
{
    static UnitScaleTests() => UnitScaleTestData.EnsureLoaded();

    private const float Eps = 0.0001f;

    private static UnitScale.UnitInputs Player(uint displayId) => new(true, displayId, 60, 0, false);
    private static UnitScale.UnitInputs Creature(uint displayId, int level = 60, int familyId = 0, bool isPet = false) => new(false, displayId, level, familyId, isPet);

    [Fact]
    public void ClientDataLoaded()
    {
        Assert.True(UnitScale.HasClientData);
        Assert.True(UnitScale.TranslationEnabled); // the suite runs as a 1.12.1 server
    }

    [Theory]
    // Player race models: CreatureModelScale x ModelScale (player displays have no race row).
    [InlineData(49u, 1.0f)]      // human male
    [InlineData(53u, 1.0f)]      // dwarf male
    [InlineData(1563u, 1.15f)]   // gnome male
    [InlineData(1564u, 1.15f)]   // gnome female
    [InlineData(59u, 1.0125f)]   // tauren male: the proxy's hotfix row (CMS 1.0125) is what the client has
    [InlineData(60u, 0.9375f)]   // tauren female: hotfix CMS 0.75 x ModelScale 1.25
    // Forms and transforms: the display the client draws.
    [InlineData(892u, 0.9f)]     // night elf cat form
    [InlineData(918u, 0.8f)]     // travel form
    [InlineData(2289u, 1.2f)]    // tauren bear form
    [InlineData(7113u, 1.5f)]    // Hallow's End pirate costume (goblin, race row -> goblin model at CMS 1.0)
    [InlineData(7114u, 1.3f)]    // pirate costume, female
    [InlineData(8808u, 0.3f)]    // bat display (bat costume)
    public void PlayerFactor(uint displayId, float expected)
    {
        Assert.Equal(expected, UnitScale.ClientDisplayFactor(Player(displayId)), Eps);
    }

    [Theory]
    // Every player case from the field logs: server scale -> scale sent -> drawn size.
    // The drawn size (sent x factor) must be the server's scale, the 1.12 size.
    [InlineData(7113u, 1.5f, 1.0f)]     // the reported case: drew 2.25 when 1.5 was forwarded
    [InlineData(7114u, 1.3f, 1.0f)]
    [InlineData(8808u, 0.3f, 1.0f)]     // drew 0.09 when forwarded
    [InlineData(1563u, 1.15f, 1.0f)]    // every gnome drew 1.32 when forwarded
    [InlineData(1564u, 1.495f, 1.3f)]   // gnome with a +30% scale aura keeps the aura
    [InlineData(1563u, 0.575f, 0.5f)]   // gnome with a -50% scale aura
    [InlineData(892u, 0.9f, 1.0f)]      // cat drew 0.81
    [InlineData(918u, 0.8f, 1.0f)]      // travel drew 0.64
    [InlineData(2289u, 1.2f, 1.0f)]     // tauren bear drew 1.44
    [InlineData(59u, 1.35f, 1.3333334f)]
    [InlineData(60u, 1.25f, 1.3333334f)] // female tauren drew 1.17 (hotfix x ModelScale)
    [InlineData(53u, 0.7f, 0.7f)]       // shrunk dwarf: CMS 1, unchanged
    [InlineData(49u, 1.0f, 1.0f)]
    public void PlayerScaleTranslatesToThe112Size(uint displayId, float serverScale, float expectedSent)
    {
        float factor = UnitScale.ClientDisplayFactor(Player(displayId));
        float sent = UnitScale.ToClientObjectScale(serverScale, factor);
        Assert.Equal(expectedSent, sent, Eps);
        Assert.Equal(serverScale, sent * factor, Eps);
    }

    [Theory]
    // Humanoid NPC displays carry a CreatureDisplayInfoExtra row; the factor takes the race
    // model's CreatureModelScale on top of the display's own.
    [InlineData(1832u, 1.15f)]   // gnome NPC: CMS 1.0 x gnome model 1.15
    [InlineData(1624u, 1.0125f)] // tauren male NPC: CMS 1.0 x tauren male model (hotfix 1.0125)
    [InlineData(1322u, 1.125f)]  // tauren female NPC: CMS 1.2 x tauren female model (hotfix 0.75) x ModelScale 1.25
    [InlineData(6882u, 1.0f)]    // goblin NPC
    [InlineData(1276u, 1.0f)]    // human NPC
    public void HumanoidNpcFactorIncludesRaceModel(uint displayId, float expected)
    {
        Assert.Equal(expected, UnitScale.ClientDisplayFactor(Creature(displayId)), Eps);
    }

    [Fact]
    public void FamilyBeastTakesTheLargerOfDisplayAndFamilyScale()
    {
        // Greater Duskbat (display 4734, CMS 0.15), bat family 24 at level 20: the family
        // scale 0.4 + 0.3 x 19/59 is larger, so the client uses it.
        var bat = Creature(4734, level: 20, familyId: 24);
        float factor = UnitScale.ClientDisplayFactor(bat);
        Assert.Equal(0.4f + 0.3f * 19f / 59f, factor, Eps);
        Assert.Equal(0.15f, UnitScale.ToClientObjectScale(0.15f, factor) * factor, Eps);

        // Dire wolf display 9370 (CMS 1.5), wolf family at level 55: the display is larger.
        Assert.Equal(1.5f, UnitScale.ClientDisplayFactor(Creature(9370, level: 55, familyId: 1)), Eps);

        // The same creature with no family known: display only.
        Assert.Equal(0.15f, UnitScale.ClientDisplayFactor(Creature(4734, level: 20)), Eps);
    }

    [Theory]
    // Pets: the family level scale replaces the display terms. Kronos sends a hunter pet
    // the family level scale itself, and summoned pets their display scale, so ordinary
    // pets get about 1.0 (what a modern server sends).
    [InlineData(9370u, 1, 55, 0.975f, 1.0f)]   // dire wolf pet, level 55 (field: 0.975)
    [InlineData(1132u, 16, 60, 0.8f, 1.0f)]    // voidwalker (field: 0.8; drew 0.64 before)
    [InlineData(4449u, 23, 60, 0.5f, 1.0f)]    // imp (field: 0.5)
    [InlineData(850u, 15, 60, 0.7f, 1.0f)]     // felhunter (field: 0.7)
    [InlineData(4162u, 17, 60, 1.0f, 1.0f)]    // succubus
    public void PetFactorIsTheFamilyLevelScale(uint displayId, int familyId, int level, float serverScale, float expectedSent)
    {
        float factor = UnitScale.ClientDisplayFactor(Creature(displayId, level, familyId, isPet: true));
        Assert.Equal(expectedSent, UnitScale.ToClientObjectScale(serverScale, factor), 0.003f);
    }

    [Theory]
    // Summons with no family keep the display rule: server scale == CMS -> 1.0.
    [InlineData(6909u, 3.0f)]    // Mechanical Battle Chicken (drew 9x when forwarded)
    [InlineData(10992u, 0.25f)]  // Mini Diablo
    public void FamilylessSummonUsesTheDisplay(uint displayId, float serverScale)
    {
        float factor = UnitScale.ClientDisplayFactor(Creature(displayId));
        Assert.Equal(1.0f, UnitScale.ToClientObjectScale(serverScale, factor), Eps);
    }

    [Fact]
    public void EdgeCases()
    {
        // A family whose scales are all zero (Remote Control, 28) on a pet: factor 0, and
        // the server's scale is left alone rather than divided by zero.
        float zero = UnitScale.ClientDisplayFactor(Creature(1132, 60, 28, isPet: true));
        Assert.Equal(0f, zero);
        Assert.Equal(0.8f, UnitScale.ToClientObjectScale(0.8f, zero));

        // A display nobody knows: factor 1, scale unchanged.
        var unknown = UnitScale.ComputeFactor(Creature(99_999_999));
        Assert.False(unknown.KnownDisplay);
        Assert.Equal(1.0f, unknown.Value);

        // A zero server scale stays zero.
        Assert.Equal(0f, UnitScale.ToClientObjectScale(0f, 1.5f));

        // Family level scale: clamped at both ends, MinScale when the levels are equal.
        var wolf = GameData.CreatureFamilies[1];
        Assert.Equal(wolf.MinScale, UnitScale.FamilyLevelScale(wolf, 0), Eps);
        Assert.Equal(wolf.MaxScale, UnitScale.FamilyLevelScale(wolf, 70), Eps);
        var imp = GameData.CreatureFamilies[23];
        Assert.Equal(0.5f, UnitScale.FamilyLevelScale(imp, 30), Eps);
    }

    // The handler-level path: the factor inputs read from the proxy's copy of the unit's
    // 1.12 fields, the way UpdateHandler and the collision-height packet use them.
    [Fact]
    public void ComputeClientUnitScale_FromLegacyFields()
    {
        var costume = Fields((Idx(ObjectField.OBJECT_FIELD_SCALE_X), new UpdateField(1.5f)),
                             (Idx(UnitField.UNIT_FIELD_DISPLAYID), new UpdateField(7113)),
                             (Idx(UnitField.UNIT_FIELD_LEVEL), new UpdateField(25)));
        float sent = WorldClient.ComputeClientUnitScale(ObjectType.ActivePlayer, costume, out float factor);
        Assert.Equal(1.0f, sent, Eps);
        Assert.Equal(1.5f, factor, Eps);

        // A creature whose template the proxy has not seen: no family yet, flagged unknown.
        var unseen = Fields((Idx(ObjectField.OBJECT_FIELD_ENTRY), new UpdateField(9_999_777)),
                            (Idx(ObjectField.OBJECT_FIELD_SCALE_X), new UpdateField(0.15f)),
                            (Idx(UnitField.UNIT_FIELD_DISPLAYID), new UpdateField(4734)),
                            (Idx(UnitField.UNIT_FIELD_LEVEL), new UpdateField(20)));
        var inputs = WorldClient.ResolveUnitScaleInputs(ObjectType.Unit, unseen, out uint entry, out bool templateKnown);
        Assert.Equal(9_999_777u, entry);
        Assert.False(templateKnown);
        Assert.Equal(0, inputs.FamilyId);

        // An unseen humanoid NPC (a character-model display) needs no template: no family.
        var gnomeNpc = Fields((Idx(ObjectField.OBJECT_FIELD_ENTRY), new UpdateField(9_999_778)),
                              (Idx(ObjectField.OBJECT_FIELD_SCALE_X), new UpdateField(1.15f)),
                              (Idx(UnitField.UNIT_FIELD_DISPLAYID), new UpdateField(1832)),
                              (Idx(UnitField.UNIT_FIELD_LEVEL), new UpdateField(30)));
        WorldClient.ResolveUnitScaleInputs(ObjectType.Unit, gnomeNpc, out _, out bool humanoidKnown);
        Assert.True(humanoidKnown);
        Assert.Equal(1.0f, WorldClient.ComputeClientUnitScale(ObjectType.Unit, gnomeNpc, out float gnomeFactor), Eps);
        Assert.Equal(1.15f, gnomeFactor, Eps);
    }

    private static int Idx<T>(T field) where T : Enum
    {
        int index = LegacyVersion.GetUpdateField(field);
        Assert.True(index >= 0, $"{field} has no 1.12 index");
        return index;
    }

    private static Dictionary<int, UpdateField> Fields(params (int Index, UpdateField Value)[] values)
    {
        var fields = new Dictionary<int, UpdateField>();
        foreach (var (index, value) in values)
            fields[index] = value;
        return fields;
    }
}

/// <summary>
/// Audit guard for the unit-scale fix: every place the proxy writes a size the modern
/// client draws a unit at must go through UnitScale. Scale was patched case by case for
/// months (per-NPC lists, per-family constants, a race hotfix) because writes were spread
/// over many paths. If this test fails, route the new write through
/// WorldClient.TranslateUnitScale / ComputeClientUnitScale (UnitScaleHandler.cs) instead of
/// adding to the list.
/// </summary>
public class UnitScaleWriteAuditTests
{
    // file -> how many writes it may hold.
    private static readonly Dictionary<string, int> AllowedObjectScaleWrites = new()
    {
        // The 1.12 value for every object; a unit's is replaced by TranslateUnitScale below it.
        ["UpdateHandler.cs"] = 1,
        // TranslateUnitScale and ResendUnitScalesForEntry.
        ["UnitScaleHandler.cs"] = 2,
    };

    private static readonly Dictionary<string, int> AllowedDisplayScaleWrites = new()
    {
        // The 1.0 defaults on every create (the factor assumes DisplayScale 1.0).
        ["UpdatePackets.cs"] = 2,
    };

    private static readonly Dictionary<string, int> AllowedCollisionHeightPackets = new()
    {
        // Its Scale comes from ComputeClientUnitScale.
        ["UpdateHandler.cs"] = 1,
    };

    [Fact]
    public void EveryUnitScaleWriteGoesThroughUnitScale()
    {
        string? root = FindSourceRoot();
        if (root == null)
            Assert.Skip("Source tree not found next to the test binaries.");

        var files = Directory.EnumerateFiles(Path.Combine(root!, "HermesProxy"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var objectScale = new Regex(@"\bObjectData\.Scale\s*=(?!=)");
        var displayScale = new Regex(@"\b(?:DisplayScale|NativeXDisplayScale)\s*=(?!=)");
        var collisionPacket = new Regex(@"\bnew\s+MoveSetCollisionHeight\b|\bMoveSetCollisionHeight\s+\w+\s*=\s*new\b");

        Check(files, objectScale, AllowedObjectScaleWrites, "ObjectData.Scale");
        Check(files, displayScale, AllowedDisplayScaleWrites, "UnitData.DisplayScale / NativeXDisplayScale");
        Check(files, collisionPacket, AllowedCollisionHeightPackets, "MoveSetCollisionHeight");
    }

    private static void Check(List<string> files, Regex pattern, Dictionary<string, int> allowed, string what)
    {
        var found = new Dictionary<string, int>();
        foreach (var file in files)
        {
            int count = File.ReadLines(file).Count(line => !line.TrimStart().StartsWith("//") && pattern.IsMatch(line));
            if (count > 0)
                found[Path.GetFileName(file)] = found.GetValueOrDefault(Path.GetFileName(file)) + count;
        }

        foreach (var (file, count) in found)
        {
            Assert.True(allowed.TryGetValue(file, out int max) && count <= max,
                $"{what} is written in {file} ({count}x). Unit sizes must go through UnitScale (UnitScaleHandler.cs); see UnitScaleWriteAuditTests.");
        }
        foreach (var (file, count) in allowed)
            Assert.True(found.GetValueOrDefault(file) == count, $"{what}: expected {count} write(s) in {file}, found {found.GetValueOrDefault(file)}. Update the audit list with the change.");
    }

    private static string? FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HermesProxy.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}

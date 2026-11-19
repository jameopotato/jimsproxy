using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HermesProxy.World;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests;

// Item set bonuses on the synthesized character-sheet stats. The 1.12 server applies a set
// bonus as a passive aura with no visible slot (all 238 set spells in the 1.12.1 client's
// Spell.dbc carry SPELL_ATTR_PASSIVE) and no item template lists it, so the synthesis derives
// it from ItemSet data. Devilsaur Armor (set 143: 15062 + 15063) 2-piece is spell 15465,
// aura 54, +2% hit after the loader's +1.
public class ItemSetBonusTests
{
    const int DevilsaurLeggings = 15062;
    const int DevilsaurGauntlets = 15063;
    const uint DevilsaurSet = 143;
    const uint DevilsaurTwoPiece = 15465;

    const int BloodvineVest = 19682;
    const int BloodvineLeggings = 19683;
    const int BloodvineBoots = 19684;
    const uint BloodvineSet = 421;
    const uint BloodvineThreePiece = 18382;
    const uint Tailoring = 197;

    const int ItemWithoutTemplate = 999999;

    static readonly uint[] NoAuras = new uint[56];

    static void Arrange()
    {
        GameData.SpellAuraEffects = new Dictionary<uint, GameData.SpellAuraEffect[]>
        {
            [DevilsaurTwoPiece] = new[] { new GameData.SpellAuraEffect(0, 54, 2, 0) },
            [BloodvineThreePiece] = new[] { new GameData.SpellAuraEffect(0, 57, 2, 0) },
        }.ToFrozenDictionary();
        GameData.ItemSetBonuses = new Dictionary<uint, GameData.ItemSetBonus[]>
        {
            [DevilsaurSet] = new[] { new GameData.ItemSetBonus(DevilsaurTwoPiece, 2, 0, 0) },
            [BloodvineSet] = new[] { new GameData.ItemSetBonus(BloodvineThreePiece, 3, Tailoring, 300) },
        }.ToFrozenDictionary();
        GameData.StoreItemTemplate(DevilsaurLeggings, new ItemTemplate { ItemSet = DevilsaurSet });
        GameData.StoreItemTemplate(DevilsaurGauntlets, new ItemTemplate { ItemSet = DevilsaurSet });
        GameData.StoreItemTemplate(BloodvineVest, new ItemTemplate { ItemSet = BloodvineSet });
        GameData.StoreItemTemplate(BloodvineLeggings, new ItemTemplate { ItemSet = BloodvineSet });
        GameData.StoreItemTemplate(BloodvineBoots, new ItemTemplate { ItemSet = BloodvineSet });
        GameData.ItemTemplates.Remove(ItemWithoutTemplate);
    }

    static int[] Equipped(params int[] items)
    {
        var slots = new int[19];
        for (int i = 0; i < items.Length; i++)
            slots[i] = items[i];
        return slots;
    }

    [Fact]
    public void TwoDevilsaurPieces_GrantTheSetHit()
    {
        Arrange();
        var equipped = Equipped(DevilsaurLeggings, DevilsaurGauntlets);

        var sets = GameData.GetActiveItemSetBonusSpells(equipped, null);
        Assert.Equal(new uint[] { DevilsaurTwoPiece }, sets);

        float hit = GameData.ComputeMeleeRangedHitModifier(equipped, NoAuras, null!, sets);
        Assert.Equal(2f, hit);
    }

    [Fact]
    public void OneDevilsaurPiece_GrantsNoSetHit()
    {
        Arrange();
        var equipped = Equipped(DevilsaurLeggings);

        var sets = GameData.GetActiveItemSetBonusSpells(equipped, null);
        Assert.Empty(sets);
        Assert.Equal(0f, GameData.ComputeMeleeRangedHitModifier(equipped, NoAuras, null!, sets));
    }

    [Fact]
    public void SetSpellAlreadyInAnAuraSlot_IsCountedOnce()
    {
        Arrange();
        var equipped = Equipped(DevilsaurLeggings, DevilsaurGauntlets);
        var auras = new uint[56];
        auras[3] = DevilsaurTwoPiece;

        var sets = GameData.GetActiveItemSetBonusSpells(equipped, null);
        float hit = GameData.ComputeMeleeRangedHitModifier(equipped, auras, null!, sets);
        Assert.Equal(2f, hit);
    }

    [Fact]
    public void SkillGatedSet_NeedsTheSkillRank()
    {
        Arrange();
        var equipped = Equipped(BloodvineVest, BloodvineLeggings, BloodvineBoots);

        Assert.Empty(GameData.GetActiveItemSetBonusSpells(equipped, null));
        Assert.Empty(GameData.GetActiveItemSetBonusSpells(equipped, _ => 299));

        var sets = GameData.GetActiveItemSetBonusSpells(equipped, skill => skill == Tailoring ? (ushort)300 : (ushort)0);
        Assert.Equal(new uint[] { BloodvineThreePiece }, sets);

        // Mage: base 0.2 + INT/59.5 with INT 0, plus the set's aura 57 on every school.
        float[] crit = GameData.ComputeSpellCritChance(8, 60, 0, equipped, NoAuras, new HashSet<uint>(), sets);
        for (int school = 0; school < 7; school++)
            Assert.Equal(2.2f, crit[school], 3);
    }

    [Fact]
    public void PieceWhoseTemplateIsNotCached_DoesNotCountTowardTheSet()
    {
        Arrange();
        var equipped = Equipped(DevilsaurLeggings, ItemWithoutTemplate);

        Assert.Empty(GameData.GetActiveItemSetBonusSpells(equipped, null));
    }

    [Fact]
    public void ShippedCsv_HasTheDevilsaurTwoPieceRow()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "CSV", "ItemSetSpells1.csv");
        Assert.SkipUnless(File.Exists(path), "CSV/ItemSetSpells1.csv is not in the test output directory");

        var rows = File.ReadAllLines(path).Skip(1).Select(l => l.Split(',')).ToList();
        Assert.Equal(471, rows.Count);
        Assert.Contains(rows, r => r[0] == "143" && r[1] == "15465" && r[2] == "2" && r[3] == "0" && r[4] == "0");
        Assert.Contains(rows, r => r[0] == "421" && r[3] == "197" && r[4] == "300");
    }
}

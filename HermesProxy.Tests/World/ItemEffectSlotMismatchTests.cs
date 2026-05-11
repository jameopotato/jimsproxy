using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Framework.IO;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Objects;
using Xunit;

namespace HermesProxy.Tests.World;

// Healthstone slot-mismatch preservation: vmangos/cmangos place item-use effects at
// slot 0; modern CSV reference data has them at slot 1 (TBC era moved them) with the
// shared SpellCategoryID + CategoryCoolDownMSec. Without preservation the proxy strips
// the CSV record and adds a bare slot-0 record, so the modern client renders no "Use:"
// tooltip line and no heal animation. Tests cover the full cycle: first query
// relocates and emits a hotfix; second query (relog) must not strip the preserved
// fields back to vanilla server's stripped zeros.
[Collection("GameDataStaticState")]
public class ItemEffectSlotMismatchTests
{
    static ItemEffectSlotMismatchTests()
    {
        // ModernVersion's static constructor needs a real build before any test
        // exercises packet construction (HotFixMessage looks up opcodes).
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    private const uint TestItemEntry = 9_999_001;
    private const int TestSpellId = 9_999_101;
    private const int TestRecordId = 9_999_201;

    private static ItemTemplate MakeVanillaServerItem()
    {
        var item = new ItemTemplate
        {
            Entry = TestItemEntry,
        };
        // Vanilla server places the on-use spell at slot 0 with no category info.
        item.TriggeredSpellIds[0] = TestSpellId;
        item.TriggeredSpellTypes[0] = 0; // USE
        item.TriggeredSpellCharges[0] = -1;
        item.TriggeredSpellCooldowns[0] = 0;
        item.TriggeredSpellCategories[0] = 0;
        item.TriggeredSpellCategoryCooldowns[0] = 0;
        return item;
    }

    private static GameData.ItemEffect SeedCsvEffectAtSlot1()
    {
        var csvEffect = new GameData.ItemEffect
        {
            Id = TestRecordId,
            LegacySlotIndex = 1,
            TriggerType = 0,
            Charges = -1,
            CoolDownMSec = 0,
            CategoryCoolDownMSec = 120000,
            SpellCategoryID = 1153,
            SpellID = TestSpellId,
            ChrSpecializationID = 0,
            ParentItemID = (int)TestItemEntry,
        };
        GameData.ItemEffectStore[(uint)csvEffect.Id] = csvEffect;
        return csvEffect;
    }

    private static FrozenDictionary<uint, GameData.ItemSpellsData> InjectSpellsData()
    {
        var prev = GameData.ItemSpellsDataStore;
        var dict = new Dictionary<uint, GameData.ItemSpellsData>(prev);
        dict[(uint)TestSpellId] = new GameData.ItemSpellsData
        {
            Id = TestSpellId,
            Category = 30,
            RecoveryTime = 0,
            CategoryRecoveryTime = 0,
        };
        GameData.ItemSpellsDataStore = dict.ToFrozenDictionary();
        return prev;
    }

    private static void Cleanup(FrozenDictionary<uint, GameData.ItemSpellsData> prevSpellsData)
    {
        GameData.ItemEffectStore.Remove((uint)TestRecordId);
        GameData.PreservedItemEffectIds.Remove(TestRecordId);
        GameData.ItemSpellsDataStore = prevSpellsData;
    }

    [Fact]
    public void FirstQuery_RelocatesCsvRecordToServerSlotAndPreservesCategory()
    {
        var csvEffect = SeedCsvEffectAtSlot1();
        var prevSpellsData = InjectSpellsData();
        try
        {
            var item = MakeVanillaServerItem();

            var hotfix = GameData.GenerateItemEffectUpdateIfNeeded(item, slot: 0);

            Assert.NotNull(hotfix);
            Assert.Equal(0, csvEffect.LegacySlotIndex);
            Assert.Equal(TestSpellId, csvEffect.SpellID);
            Assert.Equal((ushort)1153, csvEffect.SpellCategoryID);
            Assert.Equal(120000, csvEffect.CategoryCoolDownMSec);
            Assert.Equal(0, csvEffect.CoolDownMSec);
            Assert.Equal(-1, csvEffect.Charges);
            Assert.Contains(TestRecordId, GameData.PreservedItemEffectIds);
        }
        finally
        {
            Cleanup(prevSpellsData);
        }
    }

    [Fact]
    public void OriginalCsvSlot_AfterRelocation_NoLongerHasRecord()
    {
        SeedCsvEffectAtSlot1();
        var prevSpellsData = InjectSpellsData();
        try
        {
            var item = MakeVanillaServerItem();

            // Slot 0 query relocates...
            GameData.GenerateItemEffectUpdateIfNeeded(item, slot: 0);
            // ...slot 1 query must now find nothing and emit no hotfix (server has no spell at slot 1).
            var slot1Reply = GameData.GenerateItemEffectUpdateIfNeeded(item, slot: 1);

            Assert.Null(slot1Reply);
        }
        finally
        {
            Cleanup(prevSpellsData);
        }
    }

    [Fact]
    public void SecondQuerySameSlot_DoesNotStripPreservedCategoryAndCooldown()
    {
        // Regression guard: the existing wrongCategory/wrongCatCooldown comparison
        // would interpret server-zero as disagreement with CSV's 1153 / 120000 and
        // strip the values via UpdateItemEffectRecord. The PreservedItemEffectIds
        // marker must short-circuit subsequent queries.
        var csvEffect = SeedCsvEffectAtSlot1();
        var prevSpellsData = InjectSpellsData();
        try
        {
            var item = MakeVanillaServerItem();

            GameData.GenerateItemEffectUpdateIfNeeded(item, slot: 0);
            var second = GameData.GenerateItemEffectUpdateIfNeeded(item, slot: 0);

            Assert.Null(second);
            Assert.Equal((ushort)1153, csvEffect.SpellCategoryID);
            Assert.Equal(120000, csvEffect.CategoryCoolDownMSec);
            Assert.Equal(TestSpellId, csvEffect.SpellID);
            Assert.Equal((byte)0, csvEffect.LegacySlotIndex);
        }
        finally
        {
            Cleanup(prevSpellsData);
        }
    }

    [Fact]
    public void NoCsvRecord_FallsThroughToAddNew()
    {
        // Regression guard: items where CSV has no matching SpellID at any slot must
        // still go through the original AddItemEffectRecord path. The relocation
        // logic only triggers when a same-spell record exists at a different slot.
        var prevSpellsData = InjectSpellsData();
        try
        {
            var item = MakeVanillaServerItem();
            var beforeIds = new HashSet<uint>(GameData.ItemEffectStore.Keys);

            var hotfix = GameData.GenerateItemEffectUpdateIfNeeded(item, slot: 0);

            Assert.NotNull(hotfix);
            // A new record should have been added (not relocated, since no match existed).
            var added = new HashSet<uint>(GameData.ItemEffectStore.Keys);
            added.ExceptWith(beforeIds);
            Assert.Single(added);
            var newRecord = GameData.ItemEffectStore[added.First()];
            Assert.Equal((int)TestItemEntry, newRecord.ParentItemID);
            Assert.Equal((byte)0, newRecord.LegacySlotIndex);
            Assert.Equal(TestSpellId, newRecord.SpellID);
            // Newly-added records do not get preserved (no CSV value to preserve).
            Assert.DoesNotContain(newRecord.Id, GameData.PreservedItemEffectIds);

            // Cleanup the freshly-added record.
            foreach (var id in added)
                GameData.ItemEffectStore.Remove(id);
        }
        finally
        {
            Cleanup(prevSpellsData);
        }
    }

    [Fact]
    public void RelocatedRecord_HotfixPayloadCarriesPreservedFields()
    {
        // Wire-level guarantee: the hotfix sent to the modern client must carry
        // LegacySlotIndex=0 (relocated) and SpellCategoryID=1153 / CategoryCoolDownMSec=120000
        // (preserved from CSV) — those are exactly the fields the modern client needs to
        // render the "Use:" tooltip line and the shared 120s healthstone cooldown.
        var csvEffect = SeedCsvEffectAtSlot1();
        var prevSpellsData = InjectSpellsData();
        try
        {
            var item = MakeVanillaServerItem();
            GameData.GenerateItemEffectUpdateIfNeeded(item, slot: 0);

            var buffer = new ByteBuffer();
            GameData.WriteItemEffectHotfix(csvEffect, buffer);
            var bytes = buffer.GetData();
            var reader = new ByteBuffer(bytes);

            // Wire layout (see WriteItemEffectHotfix): u8 LegacySlotIndex, i8 TriggerType,
            // i16 Charges, i32 CoolDownMSec, i32 CategoryCoolDownMSec, u16 SpellCategoryID,
            // i32 SpellID, u16 ChrSpecializationID, i32 ParentItemID.
            Assert.Equal((byte)0, reader.ReadUInt8());
            Assert.Equal((sbyte)0, reader.ReadInt8());
            Assert.Equal((short)-1, reader.ReadInt16());
            Assert.Equal(0, reader.ReadInt32());
            Assert.Equal(120000, reader.ReadInt32());
            Assert.Equal((ushort)1153, reader.ReadUInt16());
            Assert.Equal(TestSpellId, reader.ReadInt32());
            Assert.Equal((ushort)0, reader.ReadUInt16());
            Assert.Equal((int)TestItemEntry, reader.ReadInt32());
        }
        finally
        {
            Cleanup(prevSpellsData);
        }
    }

    [Fact]
    public void PreservedMarker_DroppedWhenServerChangesSpellID()
    {
        // Defensive guard: if the legacy server ever switches the bound SpellID at the
        // same slot across queries, the relocated record gets mutated through the
        // existing comparison-block path. The preservation marker must be cleared so
        // future queries don't lock in whatever stripped state the comparison produced.
        var csvEffect = SeedCsvEffectAtSlot1();
        var prevSpellsData = InjectSpellsData();
        try
        {
            var item = MakeVanillaServerItem();
            GameData.GenerateItemEffectUpdateIfNeeded(item, slot: 0);
            Assert.Contains(TestRecordId, GameData.PreservedItemEffectIds);

            // Server now reports a different SpellID at slot 0 for the same item.
            const int OtherSpellId = 9_999_777;
            item.TriggeredSpellIds[0] = OtherSpellId;

            GameData.GenerateItemEffectUpdateIfNeeded(item, slot: 0);

            Assert.DoesNotContain(TestRecordId, GameData.PreservedItemEffectIds);
            Assert.Equal(OtherSpellId, csvEffect.SpellID);
        }
        finally
        {
            Cleanup(prevSpellsData);
        }
    }
}

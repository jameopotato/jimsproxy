using System.Collections.Generic;
using Xunit;

namespace HermesProxy.Tests.World;

// Static simulator for vmangos's PLAYER_QUEST_LOG_X_2 bit-packing and the proxy's
// modern-client StorageIndex assignment. Pins the algorithm in CI and demonstrates
// the two bug classes that motivated commits 6d5e64b (StorageIndex=absolute slot)
// and 9b88b91 (item-objective overlay):
//
//   1. SLOT-GAP bug — vmangos credits counters at the ABSOLUTE slot index in
//      ReqCreatureOrGOId[0..3] (Player.cpp::SendQuestUpdateAddCreatureOrGo). If
//      that array has a zero gap, the old proxy code's compressed StorageIndex
//      mis-aligns the modern client's ObjectiveProgress[StorageIndex] lookup by
//      one slot per gap. Repro: quest 905 "The Angry Scytheclaws", quest 877
//      "The Stagnant Oasis".
//
//   2. ITEM-SIDECAR bug — vmangos writes item-objective counters to a DIFFERENT
//      log slot (slot + GOcount, Player.cpp::SendQuestUpdateAddItem). The modern
//      client can't read that, so the item objective renders at 0/N even after
//      the player has collected enough to be server-completable. Repro: quest
//      358 "Graverobbers", quest 891 "The Guns of Northwatch".
//
// Each [Theory] case mirrors a real or hypothetical Twinstar quest_template
// layout. If the simulator says "BEFORE" produces a 0/N render at a given
// objective AND the tester reports that exact symptom, the diagnosis is
// conclusive. Run the new ones via `dotnet test --filter QuestObjectiveLayout`
// when a future quest report comes in.
public class QuestObjectiveLayoutTests
{
    // vmangos Player.h::SetQuestSlotCounter: val |= ((uint32)count << (counter * 6))
    // 4 x 6-bit counters in bits 0..23, 8-bit state in bits 24..31.
    public static uint PackQuestLogX2(int[] counters, byte state = 0)
    {
        uint v = 0;
        for (int i = 0; i < counters.Length && i < 4; i++)
            v |= ((uint)counters[i] & 0x3Fu) << (i * 6);
        v |= ((uint)state) << 24;
        return v;
    }

    public static (int[] Counters, byte State) UnpackQuestLogX2(uint v)
    {
        var counters = new int[4];
        for (int i = 0; i < 4; i++)
            counters[i] = (int)((v >> (i * 6)) & 0x3Fu);
        return (counters, (byte)((v >> 24) & 0xFFu));
    }

    public record struct Objective(string Kind, int WireSlot, int StorageIndex, int TargetId);

    // Mirrors QueryHandler.HandleQueryQuestInfoResponse storage-index assignment.
    // applyFix = false reproduces the pre-6d5e64b compressed objectiveCounter++.
    // applyFix = true reproduces the post-6d5e64b absolute-slot assignment for GO/Monster.
    public static List<Objective> BuildModernTemplate(int[] reqCreatureOrGoId, int[] reqItemId, bool applyFix)
    {
        var result = new List<Objective>();
        int objectiveCounter = 0;
        for (int i = 0; i < 4 && i < reqCreatureOrGoId.Length; i++)
        {
            if (reqCreatureOrGoId[i] == 0)
                continue;
            int storageIndex;
            if (applyFix)
            {
                storageIndex = i;
                if (objectiveCounter < i + 1)
                    objectiveCounter = i + 1;
            }
            else
            {
                storageIndex = objectiveCounter++;
            }
            result.Add(new Objective("GO", i, storageIndex, reqCreatureOrGoId[i]));
        }
        for (int i = 0; i < reqItemId.Length; i++)
        {
            if (reqItemId[i] == 0)
                continue;
            result.Add(new Objective("Item", i, objectiveCounter++, reqItemId[i]));
        }
        return result;
    }

    // Mirrors ReadQuestLogEntry's modern-side rendering: ObjectiveProgress[StorageIndex] is
    // read from the unpacked bit-pack counters, with the item-overlay layered on top when
    // overlay is non-empty (post-9b88b91 behavior). overlay key = StorageIndex.
    public static int RenderObjective(Objective obj, int[] counters, IReadOnlyDictionary<int, int>? itemOverlay)
    {
        if (obj.Kind == "Item" && itemOverlay != null && itemOverlay.TryGetValue(obj.StorageIndex, out int ov))
            return ov;
        if (obj.StorageIndex >= 0 && obj.StorageIndex < counters.Length)
            return counters[obj.StorageIndex];
        return 0;
    }

    // Helper: compute counters[] the way vmangos's SetQuestSlotCounter writes them when
    // CastedCreatureOrGO/KilledMonsterCredit fire. Item-sidecar counters are NOT placed
    // in this quest's log slot — they go to slot+GOcount, which the modern client can't
    // resolve back to this quest, so we leave them out (that's the BEFORE state).
    public static int[] CreditCounters(int[] reqCreatureOrGoId, IReadOnlyDictionary<int, int> kills)
    {
        var counters = new int[4];
        for (int i = 0; i < reqCreatureOrGoId.Length && i < 4; i++)
        {
            if (reqCreatureOrGoId[i] != 0 && kills.TryGetValue(reqCreatureOrGoId[i], out int n))
                counters[i] = n;
        }
        return counters;
    }

    // === Quest 905 "The Angry Scytheclaws" — slot-0 gap, the original 6d5e64b repro ===
    // Tester observation: Blue Raptor Nest stuck at 0/1 while quest is server-completable.

    [Fact]
    public void Quest905_SlotZeroGap_BeforeFix_BlueShows0Of1()
    {
        var go = new[] { 0, -6907, -6908, -6906 }; // Blue, Yellow, Red at slots 1/2/3
        var counters = CreditCounters(go, new Dictionary<int, int> { [-6907] = 1, [-6908] = 1, [-6906] = 1 });
        Assert.Equal(new[] { 0, 1, 1, 1 }, counters);

        var template = BuildModernTemplate(go, new[] { 0, 0, 0, 0 }, applyFix: false);
        var blue = template.Find(o => o.TargetId == -6907);
        Assert.Equal(0, blue.StorageIndex); // compressed → 0, points at the empty counter[0]
        Assert.Equal(0, RenderObjective(blue, counters, null)); // VISIBLE BUG
    }

    [Fact]
    public void Quest905_SlotZeroGap_AfterFix_AllRender1()
    {
        var go = new[] { 0, -6907, -6908, -6906 };
        var counters = CreditCounters(go, new Dictionary<int, int> { [-6907] = 1, [-6908] = 1, [-6906] = 1 });

        var template = BuildModernTemplate(go, new[] { 0, 0, 0, 0 }, applyFix: true);
        foreach (var obj in template)
            Assert.Equal(1, RenderObjective(obj, counters, null));
    }

    // === Quest 358 "Graverobbers" — contiguous GO + item sidecar ===
    // Tester observation: kills fine, Embalming Ichor stuck at 0/8 (quest still turn-in-able).

    [Fact]
    public void Quest358_ItemSidecar_BeforeFix_IchorShows0()
    {
        var go = new[] { 1941, 1675, 0, 0 }; // Graverobber, Mongrel
        var items = new[] { 2834, 0, 0, 0 }; // Ichor
        var counters = CreditCounters(go, new Dictionary<int, int> { [1941] = 8, [1675] = 5 });
        Assert.Equal(new[] { 8, 5, 0, 0 }, counters);

        var template = BuildModernTemplate(go, items, applyFix: false);
        var ichor = template.Find(o => o.Kind == "Item" && o.TargetId == 2834);
        Assert.Equal(2, ichor.StorageIndex); // compressed slot after the 2 GOs
        Assert.Equal(0, RenderObjective(ichor, counters, null)); // VISIBLE BUG
    }

    [Fact]
    public void Quest358_ItemSidecar_AfterFix_IchorShows8FromOverlay()
    {
        var go = new[] { 1941, 1675, 0, 0 };
        var items = new[] { 2834, 0, 0, 0 };
        var counters = CreditCounters(go, new Dictionary<int, int> { [1941] = 8, [1675] = 5 });

        var template = BuildModernTemplate(go, items, applyFix: true);
        var ichor = template.Find(o => o.Kind == "Item" && o.TargetId == 2834);
        var overlay = new Dictionary<int, int> { [ichor.StorageIndex] = 8 }; // proxy walks inventory: 8 Ichor
        Assert.Equal(8, RenderObjective(ichor, counters, overlay));
    }

    // === Quest 877 "The Stagnant Oasis" — single GO, slot-3 gap explains the report ===
    // Tester observation: "Test the Dried Seeds" stuck at 0/1.

    [Fact]
    public void Quest877_SlotThreeGap_BeforeFix_GoShows0Of1()
    {
        var go = new[] { 0, 0, 0, 3737 }; // Dried Seeds GO at slot 3
        var counters = CreditCounters(go, new Dictionary<int, int> { [3737] = 1 });
        Assert.Equal(new[] { 0, 0, 0, 1 }, counters);

        var template = BuildModernTemplate(go, new[] { 0, 0, 0, 0 }, applyFix: false);
        var seeds = template.Find(o => o.TargetId == 3737);
        Assert.Equal(0, seeds.StorageIndex); // compressed
        Assert.Equal(0, RenderObjective(seeds, counters, null)); // VISIBLE BUG
    }

    [Fact]
    public void Quest877_SlotThreeGap_AfterFix_GoShows1()
    {
        var go = new[] { 0, 0, 0, 3737 };
        var counters = CreditCounters(go, new Dictionary<int, int> { [3737] = 1 });

        var template = BuildModernTemplate(go, new[] { 0, 0, 0, 0 }, applyFix: true);
        var seeds = template.Find(o => o.TargetId == 3737);
        Assert.Equal(3, seeds.StorageIndex);
        Assert.Equal(1, RenderObjective(seeds, counters, null));
    }

    [Fact]
    public void Quest877_NoGap_BothBehaviorsRenderCorrectly()
    {
        // Sanity: if Twinstar's data didn't have a gap, neither fix would change behavior.
        var go = new[] { 3737, 0, 0, 0 };
        var counters = CreditCounters(go, new Dictionary<int, int> { [3737] = 1 });

        var pre = BuildModernTemplate(go, new[] { 0, 0, 0, 0 }, applyFix: false);
        var post = BuildModernTemplate(go, new[] { 0, 0, 0, 0 }, applyFix: true);
        Assert.Equal(1, RenderObjective(pre[0], counters, null));
        Assert.Equal(1, RenderObjective(post[0], counters, null));
    }

    // === Quest 891 "The Guns of Northwatch" — 3 named kills + item sidecar ===
    // Tester observation: medals stuck at 0/10. Same shape as 358.

    [Fact]
    public void Quest891_Contiguous_BeforeFix_MedalsShow0()
    {
        var go = new[] { 3393, 3455, 3454, 0 }; // Fairmount, Whessan, Smythe
        var items = new[] { 5078, 0, 0, 0 }; // Theramore Medal x10
        var counters = CreditCounters(go, new Dictionary<int, int> { [3393] = 1, [3455] = 1, [3454] = 1 });
        Assert.Equal(new[] { 1, 1, 1, 0 }, counters);

        var template = BuildModernTemplate(go, items, applyFix: false);
        var medal = template.Find(o => o.Kind == "Item");
        Assert.Equal(3, medal.StorageIndex);
        Assert.Equal(0, RenderObjective(medal, counters, null)); // VISIBLE BUG
    }

    [Fact]
    public void Quest891_Contiguous_AfterFix_MedalsShow10FromOverlay()
    {
        var go = new[] { 3393, 3455, 3454, 0 };
        var items = new[] { 5078, 0, 0, 0 };
        var counters = CreditCounters(go, new Dictionary<int, int> { [3393] = 1, [3455] = 1, [3454] = 1 });

        var template = BuildModernTemplate(go, items, applyFix: true);
        var medal = template.Find(o => o.Kind == "Item");
        var overlay = new Dictionary<int, int> { [medal.StorageIndex] = 10 };
        Assert.Equal(10, RenderObjective(medal, counters, overlay));
        // GO objectives still render correctly under absolute-slot assignment.
        foreach (var obj in template)
            if (obj.Kind == "GO")
                Assert.Equal(1, RenderObjective(obj, counters, null));
    }

    // === Bit-pack round-trip — proves vmangos's 6-bit layout matches the proxy unpack ===

    [Theory]
    [InlineData(new[] { 0, 0, 0, 0 }, 0, 0x00000000u)]
    [InlineData(new[] { 1, 1, 1, 0 }, 0, 0x00001041u)]   // Quest 891 contiguous after all 3 kills
    [InlineData(new[] { 0, 1, 1, 1 }, 0, 0x00041040u)]   // Quest 905 slot-0 gap, all credited
    [InlineData(new[] { 8, 5, 0, 0 }, 0, 0x00000148u)]   // Quest 358 kill counts only
    [InlineData(new[] { 0, 0, 0, 1 }, 0, 0x00040000u)]   // Quest 877 slot-3 gap
    [InlineData(new[] { 63, 63, 63, 63 }, 1, 0x01FFFFFFu)] // max counter values + state byte
    public void PackUnpack_RoundTripsCorrectly(int[] counters, byte state, uint expectedPacked)
    {
        uint packed = PackQuestLogX2(counters, state);
        Assert.Equal(expectedPacked, packed);

        var (unpackedCounters, unpackedState) = UnpackQuestLogX2(packed);
        Assert.Equal(counters, unpackedCounters);
        Assert.Equal(state, unpackedState);
    }
}

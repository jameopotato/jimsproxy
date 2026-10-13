using System.Collections.Generic;
using System.Linq;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (#513 follow-up): the out-of-view party aura slot table. The 1.12 server sends
/// slot-indexed deltas; the 1.14 client replaces a member's list with every aura-bearing state,
/// so the proxy must always emit the complete non-zero list. See <see cref="PartyAuraSlotTable"/>.
/// </summary>
public class PartyAuraSlotTableTests
{
    private const uint Fortitude = 1243;
    private const uint MarkOfTheWild = 1126;
    private const uint ArcaneIntellect = 10938;
    private const uint WeakenedSoul = 6788;

    private static uint Identity(uint id) => id;

    private static uint[] TableWith(params (int slot, uint id)[] entries)
    {
        var table = PartyAuraSlotTable.NewTable();
        foreach (var (slot, id) in entries)
            table[slot] = id;
        return table;
    }

    private static List<uint> Ids(List<PartyMemberAuraStates> list) => list.Select(a => a.SpellId).ToList();

    // A delta that adds one buff must produce the complete list, not a one-entry list.
    [Fact]
    public void DeltaAdd_EmitsTheCompleteList()
    {
        var table = TableWith((0, Fortitude), (1, MarkOfTheWild));

        PartyAuraSlotTable.ApplyDelta(table, new[] { new PartyAuraSlotTable.SlotChange(2, ArcaneIntellect) });

        Assert.Equal(new List<uint> { Fortitude, MarkOfTheWild, ArcaneIntellect }, Ids(PartyAuraSlotTable.BuildAuraList(table, Identity)));
    }

    // A delta that clears one slot (spell id 0) must produce the list without that buff and no 0 entry.
    [Fact]
    public void DeltaRemove_EmitsTheListWithoutIt()
    {
        var table = TableWith((0, Fortitude), (1, MarkOfTheWild));

        PartyAuraSlotTable.ApplyDelta(table, new[] { new PartyAuraSlotTable.SlotChange(1, 0) });

        Assert.Equal(new List<uint> { Fortitude }, Ids(PartyAuraSlotTable.BuildAuraList(table, Identity)));
        Assert.Equal(0u, table[1]);
    }

    // A full state rebuilds from scratch: stale slots go, and a 0 entry in the full list is never emitted.
    [Fact]
    public void Full_RebuildsTheTableAndFiltersZeroEntries()
    {
        var table = TableWith((5, 999));

        PartyAuraSlotTable.RebuildFromFull(table, new[]
        {
            new PartyAuraSlotTable.SlotChange(0, Fortitude),
            new PartyAuraSlotTable.SlotChange(3, 0),
        });

        Assert.Equal(new List<uint> { Fortitude }, Ids(PartyAuraSlotTable.BuildAuraList(table, Identity)));
        Assert.Equal(0u, table[5]);
    }

    // A delta that only clears slots must never emit a SpellId 0 entry (it stops a buff loop early).
    [Fact]
    public void ZeroOnlyDelta_NeverEmitsSpellIdZero()
    {
        var table = TableWith((0, Fortitude));

        PartyAuraSlotTable.ApplyDelta(table, new[] { new PartyAuraSlotTable.SlotChange(0, 0) });
        var list = PartyAuraSlotTable.BuildAuraList(table, Identity);

        Assert.Empty(list);
        Assert.True(PartyAuraSlotTable.IsEmpty(table));
    }

    // Aura flag set with empty masks (seen on Kronos): nothing changes, the list is kept, not wiped.
    [Fact]
    public void AuraFlagsWithEmptyMasks_KeepTheList()
    {
        var table = TableWith((0, Fortitude), (2, ArcaneIntellect));

        PartyAuraSlotTable.ApplyDelta(table, new PartyAuraSlotTable.SlotChange[0]);

        Assert.Equal(new List<uint> { Fortitude, ArcaneIntellect }, Ids(PartyAuraSlotTable.BuildAuraList(table, Identity)));
    }

    // Negative-mask bit j is slot 32 + j and carries the Negative flag; positive slots carry Positive.
    [Fact]
    public void NegativeSlots_CarryTheNegativeFlag_InSlotOrder()
    {
        var table = PartyAuraSlotTable.NewTable();

        PartyAuraSlotTable.ApplyDelta(table, new[]
        {
            new PartyAuraSlotTable.SlotChange(PartyAuraSlotTable.PositiveSlots + 2, WeakenedSoul),
            new PartyAuraSlotTable.SlotChange(0, Fortitude),
        });
        var list = PartyAuraSlotTable.BuildAuraList(table, Identity);

        Assert.Equal(new List<uint> { Fortitude, WeakenedSoul }, Ids(list));
        Assert.Equal((ushort)AuraFlagsModern.Positive, list[0].AuraFlags);
        Assert.Equal((ushort)AuraFlagsModern.Negative, list[1].AuraFlags);
        Assert.All(list, a => Assert.Equal(1u, a.ActiveFlags));
    }

    // Out-of-range slot indices are ignored rather than thrown.
    [Fact]
    public void OutOfRangeSlot_IsIgnored()
    {
        var table = TableWith((0, Fortitude));

        PartyAuraSlotTable.ApplyDelta(table, new[] { new PartyAuraSlotTable.SlotChange(PartyAuraSlotTable.SlotCount, 42) });

        Assert.Equal(new List<uint> { Fortitude }, Ids(PartyAuraSlotTable.BuildAuraList(table, Identity)));
    }

    // Priming from a unit's last in-view UNIT_FIELD_AURA fields copies all 48 slots and clears the rest.
    [Fact]
    public void PrimeFromUnitFields_CopiesAllSlots()
    {
        const int auraBase = 47;
        var fields = new Dictionary<int, UpdateField>
        {
            [auraBase + 0] = new UpdateField(Fortitude),
            [auraBase + 33] = new UpdateField(WeakenedSoul),
        };
        var table = TableWith((7, 999));

        PartyAuraSlotTable.PrimeFromUnitFields(table, fields, auraBase);

        Assert.Equal(Fortitude, table[0]);
        Assert.Equal(WeakenedSoul, table[33]);
        Assert.Equal(0u, table[7]);
        Assert.Equal(new List<uint> { Fortitude, WeakenedSoul }, Ids(PartyAuraSlotTable.BuildAuraList(table, Identity)));
    }

    // The emitted ids go through the legacy-to-modern spell id remap (the party path skipped it before).
    [Fact]
    public void BuildAuraList_RemapsToModernSpellIds()
    {
        var table = TableWith((0, 24427));

        var list = PartyAuraSlotTable.BuildAuraList(table, id => id == 24427 ? 363880u : id);

        Assert.Equal(new List<uint> { 363880 }, Ids(list));
    }

    // Membership: a guid in either group slot counts (slot 1 is the battleground raid).
    [Fact]
    public void IsGroupMemberGuid_CountsEitherGroupSlot()
    {
        var state = GameSessionData.CreateForTesting();
        var member0 = WowGuid128.Create(HighGuidType703.Player, 1001);
        var member1 = WowGuid128.Create(HighGuidType703.Player, 1002);
        var stranger = WowGuid128.Create(HighGuidType703.Player, 1003);
        state.CurrentGroups[0] = new PartyUpdate { PlayerList = { new PartyPlayerInfo { GUID = member0 } } };
        state.CurrentGroups[1] = new PartyUpdate { PlayerList = { new PartyPlayerInfo { GUID = member1 } } };

        Assert.True(state.IsGroupMemberGuid(member0));
        Assert.True(state.IsGroupMemberGuid(member1));
        Assert.False(state.IsGroupMemberGuid(stranger));
    }
}

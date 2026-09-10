using System.Collections;
using System.Collections.Generic;
using HermesProxy.World.Client;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (map exploration reset 2026-09-01): two legacy uint32 explored-zone fields pack
// into one modern ulong element, and the modern builder always writes the WHOLE element. A
// mid-session discovery dirties exactly ONE legacy field, so the untouched half must be
// composed from the cumulative updates dict (the session's merged field cache) — composing
// it from the outgoing update zeroed 32 zones' worth of exploration on every new discovery.
public class ExploredZonesTranslationTests
{
    private const int BaseField = 200; // arbitrary legacy field index; the helper takes it as a parameter
    private const int FieldCount = 64; // vanilla: 64 uint32 fields -> 32 modern ulong elements

    private static BitArray EmptyMask() => new BitArray(BaseField + FieldCount);

    private static ulong?[] NewModernArray() => new ulong?[240];

    [Fact]
    public void PartialUpdate_EvenField_PreservesCachedOddHalf()
    {
        // The reported bug: discovering a new area sends ONE legacy field; the cached
        // paired field (from the login create) must survive into the composed ulong.
        var mask = EmptyMask();
        mask[BaseField + 0] = true;
        var updates = new Dictionary<int, UpdateField>
        {
            [BaseField + 0] = new UpdateField(0x00000001u), // newly discovered bit
            [BaseField + 1] = new UpdateField(0xDEADBEEFu), // cached since create
        };
        var modern = NewModernArray();

        WorldClient.TranslateExploredZones(BaseField, FieldCount, mask, updates, modern);

        Assert.Equal(0xDEADBEEF00000001UL, modern[0]);
    }

    [Fact]
    public void PartialUpdate_OddField_PreservesCachedEvenHalf()
    {
        var mask = EmptyMask();
        mask[BaseField + 1] = true;
        var updates = new Dictionary<int, UpdateField>
        {
            [BaseField + 0] = new UpdateField(0xCAFEF00Du), // cached since create
            [BaseField + 1] = new UpdateField(0x80000000u), // newly discovered bit
        };
        var modern = NewModernArray();

        WorldClient.TranslateExploredZones(BaseField, FieldCount, mask, updates, modern);

        Assert.Equal(0x80000000CAFEF00DUL, modern[0]);
    }

    [Fact]
    public void FullPairUpdate_ComposesBothHalves()
    {
        // Create-block shape: both fields of a pair arrive masked together.
        var mask = EmptyMask();
        mask[BaseField + 2] = true;
        mask[BaseField + 3] = true;
        var updates = new Dictionary<int, UpdateField>
        {
            [BaseField + 2] = new UpdateField(0x11111111u),
            [BaseField + 3] = new UpdateField(0x22222222u),
        };
        var modern = NewModernArray();

        WorldClient.TranslateExploredZones(BaseField, FieldCount, mask, updates, modern);

        Assert.Equal(0x2222222211111111UL, modern[1]);
    }

    [Fact]
    public void UnmaskedPairs_StayNull_SoBuilderDoesNotSendThem()
    {
        // Elements the server didn't touch must remain null — a non-null element makes the
        // modern builder write it, which would spam full explored-zone state every update.
        var mask = EmptyMask();
        mask[BaseField + 0] = true;
        var updates = new Dictionary<int, UpdateField>
        {
            [BaseField + 0] = new UpdateField(1u),
            [BaseField + 4] = new UpdateField(0xFFFFFFFFu), // cached but not masked this packet
        };
        var modern = NewModernArray();

        WorldClient.TranslateExploredZones(BaseField, FieldCount, mask, updates, modern);

        Assert.NotNull(modern[0]);
        for (int i = 1; i < modern.Length; i++)
            Assert.Null(modern[i]);
    }

    [Fact]
    public void MissingPairedField_ComposesAsUnexplored()
    {
        // A field never seen (zero at create, so never sent) is genuinely unexplored.
        var mask = EmptyMask();
        mask[BaseField + 6] = true;
        var updates = new Dictionary<int, UpdateField>
        {
            [BaseField + 6] = new UpdateField(0x00000008u),
        };
        var modern = NewModernArray();

        WorldClient.TranslateExploredZones(BaseField, FieldCount, mask, updates, modern);

        Assert.Equal(0x0000000000000008UL, modern[3]);
    }

    [Fact]
    public void MultiplePairsInOneUpdate_AllComposedIndependently()
    {
        var mask = EmptyMask();
        mask[BaseField + 0] = true;  // partial, even
        mask[BaseField + 9] = true;  // partial, odd
        var updates = new Dictionary<int, UpdateField>
        {
            [BaseField + 0] = new UpdateField(0x000000FFu),
            [BaseField + 1] = new UpdateField(0xAAAAAAAAu), // cached
            [BaseField + 8] = new UpdateField(0x55555555u), // cached
            [BaseField + 9] = new UpdateField(0xFF000000u),
        };
        var modern = NewModernArray();

        WorldClient.TranslateExploredZones(BaseField, FieldCount, mask, updates, modern);

        Assert.Equal(0xAAAAAAAA000000FFUL, modern[0]);
        Assert.Equal(0xFF00000055555555UL, modern[4]);
    }
}

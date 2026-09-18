using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (Linux client crash at world entry): SMSG_FEATURE_SYSTEM_STATUS carries an optional
// RaceClassExpansionLevels list behind a has-bit. The proxy never fills the list, so the bit must be
// clear. The nullable sweep had defaulted the field to an empty list, which set the bit and wrote a
// zero count; the 1.14.2 client under Linux faulted on that at world entry (6 of 6 runs, 0 of 4 with
// the bit clear) while the Windows client tolerated it. These tests pin the absent shape by payload
// size, which is independent of the bit layout of the packet's version-dependent flag block.
public class FeatureSystemStatusTests
{
    static FeatureSystemStatusTests()
    {
        // ServerPacket construction resolves opcodes through ModernVersion, whose static ctor needs
        // a real build; the same guard every packet-constructing test uses.
        if (global::Framework.Settings.ClientBuild == HermesProxy.Enums.ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = HermesProxy.Enums.ClientVersionBuild.V1_14_2_42597;
    }

    private static byte[] Bytes(FeatureSystemStatus packet)
    {
        // WritePacketData runs Write() itself for a non-span packet; calling Write() first would
        // append the payload twice.
        packet.WritePacketData();
        return packet.GetData()!;
    }

    [Fact]
    public void RaceClassExpansionLevels_DefaultsToAbsent()
    {
        Assert.Null(new FeatureSystemStatus().RaceClassExpansionLevels);
    }

    [Fact]
    public void DefaultPacket_WritesNoRaceClassExpansionLevelsCount()
    {
        var absent = Bytes(new FeatureSystemStatus());
        var empty = Bytes(new FeatureSystemStatus { RaceClassExpansionLevels = new() });
        var three = Bytes(new FeatureSystemStatus { RaceClassExpansionLevels = new() { 1, 2, 3 } });

        // The has-bit occupies the same flushed bit block either way; an empty list adds only its
        // 4-byte count, and three entries add the count plus three bytes.
        Assert.Equal(absent.Length + 4, empty.Length);
        Assert.Equal(absent.Length + 4 + 3, three.Length);
    }
}

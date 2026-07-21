using Framework.IO;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using Xunit;

namespace HermesProxy.Tests.World;

// Covers the write side of the MSG_MOVE_TIME_SKIPPED (s2c) -> SMSG_MOVE_SKIP_TIME
// translation: that MoveSkipTime serializes as [packed mover guid128][uint32
// timeSkipped] and that its two write paths (ByteBuffer Write() and span
// WriteToSpan()) agree byte-for-byte. The handler-side gating (self-guid /
// recently-destroyed drops) mirrors the already-tested WasObjectRecentlyDestroyed
// gate and needs a full session, so it is left to integration/A-B testing.
public class MoveSkipTimeTests
{
    static MoveSkipTimeTests()
    {
        // SMSG_MOVE_SKIP_TIME resolves to 0x2E18 for the shipped 1.14.2 build
        // (opcodes-defining build V2_5_3_41750); ServerPacket's ctor asserts the
        // opcode is non-zero, so ClientBuild must be set before construction.
        if (global::Framework.Settings.ClientBuild == ClientVersionBuild.Zero)
            global::Framework.Settings.ClientBuild = ClientVersionBuild.V1_14_2_42597;
    }

    // Exposes the ByteBuffer (Write()) output, which is otherwise unreachable via
    // the public API because WritePacketData() prefers the span path for any
    // ISpanWritable packet. Derived access to the protected _worldPacket is legal.
    private sealed class ProbeMoveSkipTime : MoveSkipTime
    {
        public byte[] WriteViaByteBuffer()
        {
            Write();
            return _worldPacket.GetData()!;
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(12345u)]
    [InlineData(uint.MaxValue)]
    public void WriteToSpan_MatchesByteBufferWrite(uint timeSkipped)
    {
        var mover = WowGuid128.Create(HighGuidType703.Player, 22202);

        var probe = new ProbeMoveSkipTime { MoverGUID = mover, TimeSkipped = timeSkipped };
        byte[] byteBufferBytes = probe.WriteViaByteBuffer();

        var packet = new MoveSkipTime { MoverGUID = mover, TimeSkipped = timeSkipped };
        byte[] spanBuffer = new byte[packet.MaxSize];
        int written = packet.WriteToSpan(spanBuffer);

        Assert.True(written > 0);
        Assert.Equal(byteBufferBytes.Length, written);
        Assert.Equal(byteBufferBytes, spanBuffer[..written]);
    }

    [Fact]
    public void WriteToSpan_EmitsPackedGuidThenTimeSkipped()
    {
        var mover = WowGuid128.Create(HighGuidType703.Player, 22202);
        uint skip = 0xDEADBEEF;

        // Independent reference in the documented wire order (guid, then u32).
        using var reference = new WorldPacket();
        reference.WritePackedGuid128(mover);
        reference.WriteUInt32(skip);
        byte[] expected = reference.GetData()!;

        var packet = new MoveSkipTime { MoverGUID = mover, TimeSkipped = skip };
        byte[] spanBuffer = new byte[packet.MaxSize];
        int written = packet.WriteToSpan(spanBuffer);

        Assert.Equal(expected, spanBuffer[..written]);
    }

    [Fact]
    public void WriteToSpan_StaysWithinMaxSize()
    {
        var mover = WowGuid128.Create(HighGuidType703.Creature, 0, 1234, 1);
        var packet = new MoveSkipTime { MoverGUID = mover, TimeSkipped = uint.MaxValue };

        byte[] spanBuffer = new byte[packet.MaxSize];
        int written = packet.WriteToSpan(spanBuffer);

        Assert.True(written > 0);
        Assert.True(written <= packet.MaxSize);
    }
}

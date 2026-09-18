using System;
using System.Buffers.Binary;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#508): a bag-full mail take must hand the 1.14 client back the attachment id it
// asked for. The 1.12 SMSG_SEND_MAIL_RESULT error path is
//   [mailId][MAIL_ITEM_TAKEN][MAIL_ERR_EQUIP_ERROR][equipError]
// with no item guid/count (vmangos/cmangos SendMailResult), so nothing on the wire names the
// slot that failed. Forwarding AttachID=0 leaves the client's pending take-command unresolved:
// free a bag slot, take again, nothing happens, until the world session ends.
public class MailTakeItemAttachIdTests
{
    const uint MailId = 77;
    const uint ItemTaken = (uint)MailActionType.AttachmentExpired; // misnamed enum value 2 == MAIL_ITEM_TAKEN
    const uint MoneyTaken = (uint)MailActionType.MoneyTaken;
    const uint Ok = (uint)MailErrorType.Ok;
    const uint EquipError = (uint)MailErrorType.Equip;
    const uint InternalError = (uint)MailErrorType.InternalError;
    const uint VanillaInvFull = (uint)InventoryResultVanilla.InvFull;

    static WorldPacket LegacyResult(params uint[] fields)
    {
        var bytes = new byte[fields.Length * 4];
        for (int i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), fields[i]);
        return new WorldPacket((uint)Opcode.SMSG_MAIL_COMMAND_RESULT, bytes);
    }

    static GameSessionData SessionWithRecordedTake(uint mailId, uint attachId)
    {
        var session = GameSessionData.CreateForTesting();
        session.PendingMailTakeAttachId[mailId] = attachId;
        return session;
    }

    // The #508 wire: take rejected for a full inventory. The client asked for a slot and must get that
    // slot back, not 0. Slot 3 is recorded here so the test cannot pass through the vanilla slot-1
    // fallback below the echo (the test build pins a 1.12 server, where the fallback also yields 1).
    [Fact]
    public void BagFullTake_EchoesRequestedAttachmentSlot()
    {
        var session = SessionWithRecordedTake(MailId, 3);

        var result = WorldClient.ParseMailCommandResult(
            LegacyResult(MailId, ItemTaken, EquipError, VanillaInvFull), session);

        Assert.Equal(MailId, result.MailID);
        Assert.Equal(MailActionType.AttachmentExpired, result.Command);
        Assert.Equal(MailErrorType.Equip, result.ErrorCode);
        Assert.Equal(InventoryResult.InvFull, result.BagResult);
        Assert.Equal(3u, result.AttachID);
    }

    // No recorded take (e.g. the result outlived a reconnect): a vanilla mail still has exactly
    // one attachment, so slot 1 is the only correct answer on the error path.
    [Fact]
    public void BagFullTake_NoRecordedTake_FallsBackToSlotOneOnVanilla()
    {
        var session = GameSessionData.CreateForTesting();

        var result = WorldClient.ParseMailCommandResult(
            LegacyResult(MailId, ItemTaken, EquipError, VanillaInvFull), session);

        Assert.Equal(1u, result.AttachID);
    }

    // Success path is untouched: [itemGuidLow][count] are read and the vanilla placeholder applies.
    [Fact]
    public void SuccessfulTake_KeepsExistingPlaceholderAndQuantity()
    {
        var session = SessionWithRecordedTake(MailId, 1);

        var result = WorldClient.ParseMailCommandResult(
            LegacyResult(MailId, ItemTaken, Ok, /*itemGuidLow*/ 123456, /*count*/ 5), session);

        Assert.Equal(MailErrorType.Ok, result.ErrorCode);
        Assert.Equal(1u, result.AttachID);
        Assert.Equal(5u, result.QtyInInventory);
        Assert.Equal(default, result.BagResult);
    }

    // The recorded take is consumed by its result, success or failure, so the map can't grow.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ItemTakenResult_ConsumesRecordedTake(bool failed)
    {
        var session = SessionWithRecordedTake(MailId, 1);
        var packet = failed
            ? LegacyResult(MailId, ItemTaken, EquipError, VanillaInvFull)
            : LegacyResult(MailId, ItemTaken, Ok, 123456, 1);

        WorldClient.ParseMailCommandResult(packet, session);

        Assert.False(session.PendingMailTakeAttachId.ContainsKey(MailId));
    }

    // Scope guard: only item-taken results carry an attachment id. A failed money take must not
    // be decorated with one, and must not disturb a recorded item take on the same mail.
    [Fact]
    public void MoneyTakenError_LeavesAttachIdAndRecordAlone()
    {
        var session = SessionWithRecordedTake(MailId, 1);

        var result = WorldClient.ParseMailCommandResult(
            LegacyResult(MailId, MoneyTaken, InternalError), session);

        Assert.Equal(MailActionType.MoneyTaken, result.Command);
        Assert.Equal(0u, result.AttachID);
        Assert.True(session.PendingMailTakeAttachId.ContainsKey(MailId));
    }
}

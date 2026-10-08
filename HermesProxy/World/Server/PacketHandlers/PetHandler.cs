using Framework.Constants;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    [PacketHandler(Opcode.CMSG_PET_ACTION)]
    void HandlePetAction(PetAction act)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_ACTION);
        packet.WriteGuid(act.PetGUID.To64());
        packet.WriteUInt32(act.Action);
        packet.WriteGuid(act.TargetGUID.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_PET_STOP_ATTACK)]
    void HandlePetStopAttack(PetStopAttack stop)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_STOP_ATTACK);
        packet.WriteGuid(stop.PetGUID.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_PET_SET_ACTION)]
    void HandlePetSetAction(PetSetAction action)
    {
        var gs = GetSession().GameState;

        // JimsProxy pet-bar lock (opt-in): when the player has the lock on, refuse a write that
        // would drop a spell onto a command or stance slot. The 1.12 server would silently
        // ignore it anyway, but the client moves the button optimistically, so the bar ends up
        // with a spell where Attack or a stance belongs and no way to restore it. Drop the write
        // and snap the client's bar back to the one we last forwarded. With the lock off, every
        // write passes through exactly as before. Ability-slot writes always pass through.
        if (gs.PetControlLockEnabled && !PetActionBar.IsClientWriteAllowed(action.Index, action.Action))
        {
            Log.Event("pet.bar.control_write_blocked", new
            {
                pet_guid = action.PetGUID.ToString(),
                index = action.Index,
                action = action.Action.ToString("X8"),
            });
            ResendPetBarToClient(action.PetGUID);
            return;
        }

        WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_SET_ACTION);
        packet.WriteGuid(action.PetGUID.To64());
        packet.WriteUInt32(action.Index);
        packet.WriteUInt32(action.Action);

        // Keep the cached bar in step with the ability slot the client just rearranged, so
        // a later reset or control-write bounce re-sends the right abilities.
        lock (gs.PetBarLock)
        {
            var snap = gs.LastPetSpells;
            if (snap != null && snap.PetGUID == action.PetGUID && action.Index < (uint)snap.ActionButtons.Length)
                snap.ActionButtons[action.Index] = action.Action;
        }

        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_PET_RENAME)]
    void HandlePetRename(PetRename pet)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_RENAME);
        packet.WriteGuid(pet.RenameData.PetGUID.To64());
        packet.WriteCString(pet.RenameData.NewName);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            packet.WriteBool(pet.RenameData.HasDeclinedNames);
            if (pet.RenameData.HasDeclinedNames)
            {
                for (int i = 0; i < PlayerConst.MaxDeclinedNameCases; i++)
                    packet.WriteCString(pet.RenameData.DeclinedNames.name[i]);
            }
        }
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_REQUEST_STABLED_PETS)]
    void HandleRequestStabledPets(RequestStabledPets stable)
    {
        WorldPacket packet = new WorldPacket(Opcode.MSG_LIST_STABLED_PETS);
        packet.WriteGuid(stable.StableMaster.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_BUY_STABLE_SLOT)]
    void HandleBuyStableSlot(BuyStableSlot stable)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_BUY_STABLE_SLOT);
        packet.WriteGuid(stable.StableMaster.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_PET_ABANDON)]
    void HandlePetAbandon(PetAbandon pet)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_ABANDON);
        packet.WriteGuid(pet.PetGUID.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_STABLE_PET)]
    void HandleStablePet(StablePet pet)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_STABLE_PET);
        packet.WriteGuid(pet.StableMaster.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_UNSTABLE_PET)]
    void HandleUnstablePet(UnstablePet pet)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_UNSTABLE_PET);
        packet.WriteGuid(pet.StableMaster.To64());
        packet.WriteUInt32(pet.PetNumber);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_STABLE_SWAP_PET)]
    void HandleStableSwapPet(StableSwapPet pet)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_STABLE_SWAP_PET);
        packet.WriteGuid(pet.StableMaster.To64());
        packet.WriteUInt32(pet.PetNumber);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_PET_SPELL_AUTOCAST)]
    void HandlePetSpellAutocast(PetSpellAutocast autocast)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_SPELL_AUTOCAST);
        packet.WriteGuid(autocast.PetGUID.To64());
        packet.WriteUInt32(autocast.SpellID);
        packet.WriteUInt8((byte)(autocast.AutocastEnabled ? 1 : 0));
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_PET_CANCEL_AURA)]
    void HandlePetCancelAura(PetCancelAura cancel)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_CANCEL_AURA);
        packet.WriteGuid(cancel.PetGUID.To64());
        packet.WriteUInt32(cancel.SpellID);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_REQUEST_PET_INFO)]
    void HandleRequestPetInfo(PetInfoRequest r)
    {
        // CMSG_REQUEST_PET_INFO
        WorldPacket packet = new WorldPacket(Opcode.CMSG_REQUEST_PET_INFO);
        SendPacketToServer(packet);

    }

    // JimsProxy pet-bar lock: re-send the last bar we forwarded, control slots re-enforced, so a
    // misplaced button bounces back visibly instead of lingering until the next summon. Used
    // after a refused control-write and by the one-shot reset. Returns whether a bar was sent
    // (false if we have no bar for this pet yet).
    private bool ResendPetBarToClient(WowGuid128 petGuid)
    {
        var gs = GetSession().GameState;
        PetSpells? resend = null;
        lock (gs.PetBarLock)
        {
            var snap = gs.LastPetSpells;
            if (snap != null && snap.PetGUID == petGuid)
            {
                PetActionBar.EnforceControlSlots(snap.ActionButtons);
                resend = snap.CloneWithoutCooldowns();
                if (resend.TimeLimit != 0)
                {
                    // Temporary pets count down from the original send; keep the client's timer honest.
                    long elapsed = Environment.TickCount64 - gs.LastPetSpellsTick;
                    resend.TimeLimit = (uint)Math.Max(0, resend.TimeLimit - elapsed);
                }
            }
        }

        if (resend == null)
            return false;
        GetSession().WorldClient?.SendPacketToClient(resend);
        return true;
    }

    // JimsProxy pet-bar lock: turn the opt-in command/stance lock on or off for this session
    // (JimsPlus "P\tLOCK\t<0|1>"). Turning it on immediately rebuilds the current pet's bar so
    // the player sees it fixed without resummoning.
    internal void SetPetControlLock(bool enabled)
    {
        var gs = GetSession().GameState;
        gs.PetControlLockEnabled = enabled;
        Log.Event("pet.bar.lock_set", new { enabled });
        if (enabled)
            ResendPetBarToClient(gs.CurrentPetGuid);
    }

    // JimsProxy pet-bar lock: one-shot repaint of the current pet's bar (JimsPlus "P\tRESET").
    // Works whether or not the lock is on; without the lock it reverts on the next summon.
    internal void ResetPetBarNow()
    {
        var gs = GetSession().GameState;
        bool sent = !gs.CurrentPetGuid.IsEmpty() && ResendPetBarToClient(gs.CurrentPetGuid);
        Log.Event("pet.bar.reset_requested", new { pet_guid = gs.CurrentPetGuid.ToString(), sent });
        if (sent)
            GetSession().SendHermesTextMessage("Pet bar reset: Attack, Follow and Stay are back on the left and the stances on the right.");
        else
            GetSession().SendHermesTextMessage("No pet out to reset. Summon or call your pet first.", isError: true);
    }
}

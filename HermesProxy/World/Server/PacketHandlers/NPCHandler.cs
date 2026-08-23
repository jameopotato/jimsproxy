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
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_BANKER_ACTIVATE)]
    [PacketHandler(Opcode.CMSG_BINDER_ACTIVATE)]
    [PacketHandler(Opcode.CMSG_LIST_INVENTORY)]
    [PacketHandler(Opcode.CMSG_SPIRIT_HEALER_ACTIVATE)]
    [PacketHandler(Opcode.CMSG_TALK_TO_GOSSIP)]
    [PacketHandler(Opcode.CMSG_TRAINER_LIST)]
    [PacketHandler(Opcode.CMSG_BATTLEMASTER_HELLO)]
    [PacketHandler(Opcode.CMSG_AREA_SPIRIT_HEALER_QUERY)]
    [PacketHandler(Opcode.CMSG_AREA_SPIRIT_HEALER_QUEUE)]
    void HandleInteractWithNPC(InteractWithNPC interact)
    {
        WorldPacket packet = new WorldPacket(interact.GetUniversalOpcode());
        packet.WriteGuid(interact.CreatureGUID.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_GOSSIP_SELECT_OPTION)]
    void HandleGossipSelectOption(GossipSelectOption gossip)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_GOSSIP_SELECT_OPTION);
        packet.WriteGuid(gossip.GossipUnit.To64());
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            packet.WriteUInt32(gossip.GossipID);
        packet.WriteUInt32(gossip.GossipIndex);
        if (!String.IsNullOrEmpty(gossip.PromotionCode))
            packet.WriteCString(gossip.PromotionCode);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_BUY_BANK_SLOT)]
    void HandleBuyBankSlot(BuyBankSlot bank)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_BUY_BANK_SLOT);
        packet.WriteGuid(bank.Guid.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_TRAINER_BUY_SPELL)]
    void HandleTrainerBuySpell(TrainerBuySpell buy)
    {
        uint realSpellId = GameData.GetRealSpell(buy.SpellID);

        // Intercept stale-click and duplicate-click trainer buys. The 1.14 trainer
        // panel doesn't honor mid-interaction SMSG_TRAINER_LIST refreshes — once
        // open, the spell list is rendered from the initial open. Stale entries
        // can still be clicked, producing useless "Failed to learn Spell X"
        // round-trips. Three predicates cover the common cases:
        //   * inKnownSpells       — player has the spell directly
        //   * inTrainerKnownSet   — server's last trainer list marked it Known
        //                           (catches supersede case where the spell was
        //                           removed from CurrentPlayerKnownSpells by
        //                           SMSG_SUPERCEDED but the trainer still treats
        //                           the lower rank as effectively-known)
        //   * isInFlightDuplicate — same-spell second click within ~2.5s, before
        //                           the first buy's response had a chance to
        //                           update either set above (rapid double-click)
        // When any fires, send a friendly chat message and close the gossip
        // panel; the player has to reopen, which yields a fresh trainer list
        // with HandleTrainerList's initial Known filter applied cleanly.
        bool inKnownSpells = GetSession().GameState.CurrentPlayerKnownSpells.Contains(realSpellId);
        bool inTrainerKnownSet = GetSession().GameState.LastTrainerListKnownSpells.Contains(realSpellId);
        long nowMs = System.Environment.TickCount64;
        long sinceInFlightMs = nowMs - GetSession().GameState.InFlightTrainerBuyTickMs;
        bool isInFlightDuplicate = GetSession().GameState.InFlightTrainerBuySpellId == realSpellId
            && sinceInFlightMs >= 0 && sinceInFlightMs < 2500;
        if (LegacyVersion.ExpansionVersion <= 1 && (inKnownSpells || inTrainerKnownSet || isInFlightDuplicate))
        {
            Log.Event("spell.trainer_buy.intercepted_already_known", new
            {
                real_spell_id = realSpellId,
                learn_spell_id = buy.SpellID,
                in_known_spells = inKnownSpells,
                in_trainer_known_set = inTrainerKnownSet,
                is_in_flight_duplicate = isInFlightDuplicate,
                in_flight_age_ms = sinceInFlightMs,
                known_count = GetSession().GameState.CurrentPlayerKnownSpells.Count,
            });
            ChatPkt chat = new ChatPkt(GetSession(), ChatMessageTypeModern.System,
                "You already know that spell. The trainer list has been refreshed.");
            SendPacket(chat);
            GossipComplete gossipClose = new GossipComplete();
            SendPacket(gossipClose);
            return;
        }


        WorldPacket packet = new WorldPacket(Opcode.CMSG_TRAINER_BUY_SPELL);
        packet.WriteGuid(buy.TrainerGUID.To64());
        if (ModernVersion.ExpansionVersion > 1 &&
            LegacyVersion.ExpansionVersion <= 1)
        {
            buy.SpellID = GetSession().GameState.GetLearnSpellFromRealSpell(buy.SpellID);
        }
        packet.WriteUInt32(buy.SpellID);
        SendPacketToServer(packet);

        // Track in-flight buy so rapid same-spell double-clicks can be dropped
        // before the response lands (see intercept block above).
        GetSession().GameState.InFlightTrainerBuySpellId = realSpellId;
        GetSession().GameState.InFlightTrainerBuyTickMs = nowMs;

        // Ban defense: speculatively remove predecessor rank from known spells.
        // Kronos calls RemoveSpell(prev) unconditionally but gates the
        // SMSG_SUPERCEDED_SPELL notification on IsInWorld(). When that gate is
        // briefly false, the predecessor disappears server-side without telling
        // us. Without this mirror, the cast-block guard lets through a cast of
        // the now-unlearned predecessor → autoban.
        // Restored on explicit SMSG_TRAINER_BUY_FAILED (see Client/NPCHandler).
        if (LegacyVersion.ExpansionVersion <= 1 &&
            GameData.SpellRankPredecessor.TryGetValue(realSpellId, out uint predecessor) &&
            predecessor != 0)
        {
            bool removed = GetSession().GameState.ApplyTrainerBuyPredecessorRemoval(realSpellId, predecessor);
            Log.Event("spell.trainer_buy.predecessor_speculatively_removed", new
            {
                real_spell_id = realSpellId,
                learn_spell_id = buy.SpellID,
                predecessor_spell_id = predecessor,
                was_in_known_set = removed,
                known_count = GetSession().GameState.CurrentPlayerKnownSpells.Count,
            });
        }
        else
        {
            GetSession().GameState.PendingTrainerBuySpellId = 0u;
            GetSession().GameState.PendingTrainerBuyRemovedPredecessor = 0u;
        }
    }

    [PacketHandler(Opcode.CMSG_CONFIRM_RESPEC_WIPE)]
    void HandleConfirmRespecWipe(ConfirmRespecWipe respec)
    {
        switch (respec.RespecType)
        {
            case SpecResetType.Talents:
            {
                WorldPacket packet = new WorldPacket(Opcode.MSG_TALENT_WIPE_CONFIRM);
                packet.WriteGuid(respec.TrainerGUID.To64());
                SendPacketToServer(packet);
                break;
            }
            case SpecResetType.PetTalents:
            {
                WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_UNLEARN);
                packet.WriteGuid(respec.TrainerGUID.To64());
                SendPacketToServer(packet);
                break;
            }
            default:
            {
                Log.Print(LogType.Error, $"Unhandled respec type {respec.RespecType}.");
                break;
            }
        }
    }
}

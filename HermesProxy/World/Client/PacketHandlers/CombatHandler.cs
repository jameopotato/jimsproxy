using Framework;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_ATTACK_START)]
    void HandleAttackStart(WorldPacket packet)
    {
        SAttackStart attack = new();
        attack.Attacker = packet.ReadGuid().To128(GetSession().GameState);
        attack.Victim = packet.ReadGuid().To128(GetSession().GameState);

        if (attack.Attacker == GetSession().GameState.CurrentPlayerGuid)
            GetSession().GameState.WaitingForAttackStart = false;

        SendPacketToClient(attack);
    }
    [PacketHandler(Opcode.SMSG_ATTACK_STOP)]
    void HandleAttackStop(WorldPacket packet)
    {
        SAttackStop attack = new();
        attack.Attacker = packet.ReadPackedGuid().To128(GetSession().GameState);
        var rawVictim = packet.ReadPackedGuid();
        attack.Victim = rawVictim.To128(GetSession().GameState);
        attack.NowDead = packet.ReadUInt32() != 0;

        var state = GetSession().GameState;
        if (attack.Attacker == state.CurrentPlayerGuid)
        {
            if (state.DeferredAttackStop)
            {
                state.DeferredAttackStop = false;
                state.CurrentAttackTarget = default;
                WorldPacket stopPacket = new WorldPacket(Opcode.CMSG_ATTACK_STOP);
                SendPacketToServer(stopPacket, Opcode.MSG_NULL_ACTION);
            }
            else if (!state.WaitingForAttackStart)
            {
                // Server-initiated stop without our SWING: Gouge / Cheap Shot / Blind /
                // Feign Death / stealth / Vanish. Must clear CurrentAttackTarget here or
                // the next CMSG_ATTACK_SWING gets eaten by the dedupe guard at Server/CombatHandler.cs:16.
                state.CurrentAttackTarget = default;
            }
            else if (rawVictim == state.CurrentAttackTarget)
            {
                // Server rejected our SWING with ATTACK_STOP (no prior ATTACK_START):
                // target died or became invalid between our SWING and server processing.
                // Clear the state so the de-dupe guard doesn't eat future attacks.
                state.WaitingForAttackStart = false;
                state.CurrentAttackTarget = default;
            }
            // else: WaitingForAttackStart is true but victim != CurrentAttackTarget —
            // target-switch sequence, the new SWING already set CurrentAttackTarget.
        }

        SendPacketToClient(attack);
    }
    [PacketHandler(Opcode.SMSG_ATTACKER_STATE_UPDATE)]
    void HandleAttackerStateUpdate(WorldPacket packet)
    {
        AttackerStateUpdate attack = new();
        uint hitInfo = packet.ReadUInt32();
        attack.HitInfo = LegacyVersion.ConvertHitInfoFlags(hitInfo);
        attack.AttackerGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        attack.VictimGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        attack.Damage = packet.ReadInt32();
        attack.OriginalDamage = attack.Damage;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_3_9183))
            attack.OverDamage = packet.ReadInt32();
        else
            attack.OverDamage = -1;

        byte subDamageCount = packet.ReadUInt8();
        for (int i = 0; i < subDamageCount; i++)
        {
            SubDamage subDmg = new();

            uint school = packet.ReadUInt32();
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                school = (1u << (byte)school);

            subDmg.SchoolMask = school;
            subDmg.FloatDamage = packet.ReadFloat();
            subDmg.IntDamage = packet.ReadInt32();

            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_3_9183) ||
                hitInfo.HasAnyFlag(HitInfo.PartialAbsorb | HitInfo.FullAbsorb))
                subDmg.Absorbed = packet.ReadInt32();

            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_3_9183) ||
                hitInfo.HasAnyFlag(HitInfo.PartialResist | HitInfo.FullResist))
                subDmg.Resisted = packet.ReadInt32();

            attack.SubDmg.Add(subDmg);
        }

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_3_9183))
            attack.VictimState = packet.ReadUInt8();
        else
            attack.VictimState = (byte)packet.ReadUInt32();

        attack.AttackerState = packet.ReadInt32();
        attack.MeleeSpellID = packet.ReadUInt32();

        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_3_9183) ||
            hitInfo.HasAnyFlag(HitInfo.Block))
            attack.BlockAmount = packet.ReadInt32();

        if (hitInfo.HasAnyFlag(HitInfo.RageGain))
            attack.RageGained = packet.ReadInt32();

        if (hitInfo.HasAnyFlag(HitInfo.Unk0))
        {
            attack.UnkState = new();
            attack.UnkState.State1 = packet.ReadUInt32();
            attack.UnkState.State2 = packet.ReadFloat();
            attack.UnkState.State3 = packet.ReadFloat();
            attack.UnkState.State4 = packet.ReadFloat();
            attack.UnkState.State5 = packet.ReadFloat();
            attack.UnkState.State6 = packet.ReadFloat();
            attack.UnkState.State7 = packet.ReadFloat();
            attack.UnkState.State8 = packet.ReadFloat();
            attack.UnkState.State9 = packet.ReadFloat();
            attack.UnkState.State10 = packet.ReadFloat();
            attack.UnkState.State11 = packet.ReadFloat();
            attack.UnkState.State12 = packet.ReadUInt32();
            packet.ReadUInt32();
            packet.ReadUInt32();
        }

        SendPacketToClient(attack);

        // JimsProxy (#320): on a real swing for the local player, record the swing time
        // and flush any deferred BASEATTACKTIME so the speed change reaches the addon at
        // the moment the new server-side cadence becomes effective. The HitInfo.OffHand
        // bit picks the right slot.
        if (attack.AttackerGUID == GetSession().GameState.CurrentPlayerGuid)
        {
            int slot = (hitInfo & (uint)HitInfo.OffHand) != 0 ? 1 : 0;
            FlushDeferredBaseAttackTime(slot);

            // JimsProxy (judgement-refresh-on-melee): our own swing refreshes our Judgement
            // debuff on the victim server-side; synthesize the duration reset so the 1.14
            // client's debuff timer follows (the 1.12 server doesn't echo enemy-debuff durations).
            RefreshOwnJudgementOnVictim(attack.VictimGUID);
        }

        // Threat translation: feed melee swing damage into the threat tracker.
        // Vanilla damage threat is 1.0 × raw damage, no school weighting.
        GetSession().ThreatTracker.OnDamage(attack.AttackerGUID, attack.VictimGUID, attack.Damage);
    }

    // JimsProxy (#320): record the swing timestamp and, if a BASEATTACKTIME change was
    // deferred while the in-flight swing was running, synthesize a values-update carrying
    // just the new AttackRoundBaseTime so addons re-poll UnitAttackSpeed with the correct
    // post-swing value. Called from HandleAttackerStateUpdate (slot per HitInfo.OffHand)
    // and from the combat-exit / attack-stop paths (both slots) so a pending value never
    // outlives the swing window.
    void FlushDeferredBaseAttackTime(int slot)
    {
        var state = GetSession().GameState;
        state.LastAttackerStateUpdateMs[slot] = Environment.TickCount64;
        if (!state.HasPendingBaseAttackTime[slot])
            return;

        uint pending = state.PendingBaseAttackTime[slot];
        UpdateObject updateObject = new UpdateObject(state);
        ObjectUpdate update = new ObjectUpdate(state.CurrentPlayerGuid, UpdateTypeModern.Values, GetSession());
        update.UnitData.AttackRoundBaseTime[slot] = pending;
        updateObject.ObjectUpdates.Add(update);
        SendPacketToClient(updateObject);

        state.LastSentBaseAttackTime[slot] = pending;
        state.HasPendingBaseAttackTime[slot] = false;
    }

    // JimsProxy (judgement-refresh-on-melee): mirror cmangos Unit.cpp DealMeleeDamage — a
    // paladin's melee hit resets their OWN Judgement debuff (JoL/JoW/JoC/JoJ) on the victim
    // to full duration server-side. The 1.12 server doesn't echo enemy-debuff durations, so
    // the modern client's debuff timer would just count down. Re-emit a full-duration aura
    // update for any of our Judgements on the victim. Gated to a paladin local player; an
    // untracked caster is treated as ours since only the local paladin reaches here.
    void RefreshOwnJudgementOnVictim(WowGuid128 victim)
    {
        var state = GetSession().GameState;
        WowGuid128 localPlayer = state.CurrentPlayerGuid;
        if (localPlayer == default || victim == default)
            return;
        if (state.CurrentPlayerClass != (byte)Class.Paladin)
            return;

        var updateFields = state.GetCachedObjectFieldsLegacy(victim);
        if (updateFields == null)
            return;
        int auraField = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_AURA);
        if (auraField < 0)
            return;

        int slots = LegacyVersion.GetAuraSlotsCount();
        for (byte i = 0; i < slots; i++)
        {
            if (!updateFields.TryGetValue(auraField + i, out var field))
                continue;
            uint spellId = field.UInt32Value;
            if (spellId == 0 || !GameData.IsJudgementDebuff(spellId))
                continue;
            WowGuid128 caster = state.GetAuraCaster(victim, i, spellId);
            if (caster != localPlayer && caster != default)
                continue;

            SendAuraRefreshUpdate(victim, spellId, localPlayer, i, updateFields, forceFullRemaining: true);
            // JimsProxy: gate this per-swing diagnostic behind DebugOutput (fires on every melee hit while a Judgement is up).
            if (Framework.Settings.DebugOutput)
                Framework.Logging.Log.Event("judgement.refresh.synth", new
                {
                    victim_low = victim.GetCounter(),
                    slot = (int)i,
                    spell_id = spellId,
                });
        }
    }
    [PacketHandler(Opcode.SMSG_ATTACKSWING_NOTINRANGE)]
    void HandleAttackSwingNotInRange(WorldPacket packet)
    {
        AttackSwingError attack = new();
        attack.Reason = AttackSwingErr.NotInRange;
        SendPacketToClient(attack);
    }
    [PacketHandler(Opcode.SMSG_ATTACKSWING_BADFACING)]
    void HandleAttackSwingBadFacing(WorldPacket packet)
    {
        AttackSwingError attack = new();
        attack.Reason = AttackSwingErr.BadFacing;
        SendPacketToClient(attack);
    }
    [PacketHandler(Opcode.SMSG_ATTACKSWING_DEADTARGET)]
    void HandleAttackSwingDeadTarget(WorldPacket packet)
    {
        AttackSwingError attack = new();
        attack.Reason = AttackSwingErr.DeadTarget;
        SendPacketToClient(attack);
    }
    [PacketHandler(Opcode.SMSG_ATTACKSWING_CANT_ATTACK)]
    void HandleAttackSwingCantAttack(WorldPacket packet)
    {
        AttackSwingError attack = new();
        attack.Reason = AttackSwingErr.CantAttack;
        SendPacketToClient(attack);
    }
    [PacketHandler(Opcode.SMSG_CANCEL_COMBAT)]
    void HandleCancelCombat(WorldPacket packet)
    {
        GetSession().GameState.CurrentAttackTarget = default;
        GetSession().GameState.WaitingForAttackStart = false;
        GetSession().GameState.DeferredAttackStop = false;
        CancelCombat combat = new();
        SendPacketToClient(combat);

        // JimsProxy (#320): combat ended — flush any deferred BASEATTACKTIME for both slots
        // so a SnD/buff applied just before combat exit doesn't sit pending forever.
        FlushDeferredBaseAttackTime(0);
        FlushDeferredBaseAttackTime(1);

        // Drop every mob's threat list — vanilla 1.12 doesn't emit a
        // "threat ended" signal, so without this the modern client retains
        // every mob we ever fought and Luna / ThreatPlates leave red
        // indicators lit on stale nameplates.
        GetSession().ThreatTracker.OnLocalPlayerLeftCombat();
    }
    [PacketHandler(Opcode.SMSG_AI_REACTION)]
    void HandleAIReaction(WorldPacket packet)
    {
        AIReaction reaction = new();
        reaction.UnitGUID = packet.ReadGuid().To128(GetSession().GameState);
        reaction.Reaction = packet.ReadUInt32();
        SendPacketToClient(reaction);
    }
    [PacketHandler(Opcode.SMSG_PARTY_KILL_LOG)]
    void HandlePartyKillLog(WorldPacket packet)
    {
        PartyKillLog log = new();
        log.Player = packet.ReadGuid().To128(GetSession().GameState);
        log.Victim = packet.ReadGuid().To128(GetSession().GameState);
        SendPacketToClient(log);

        // Mob just died — drop its threat list immediately so the modern
        // client's threat APIs go quiet on this unit instead of waiting for
        // the corpse-despawn SMSG_DESTROY_OBJECT.
        GetSession().ThreatTracker.ClearMob(log.Victim);
    }
}

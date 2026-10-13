using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_PARTY_COMMAND_RESULT)]
    void HandlePartyCommandResult(WorldPacket packet)
    {
        PartyCommandResult party = new PartyCommandResult();
        party.Command = (byte)packet.ReadUInt32();
        party.Name = packet.ReadCString();
        uint partyResult = packet.ReadUInt32();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            party.Result = (byte)partyResult;
        else
            party.Result = (byte)((PartyResultVanilla)partyResult).CastEnum<PartyResultModern>();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            party.ResultData = packet.ReadUInt32();
        SendPacketToClient(party);
    }

    [PacketHandler(Opcode.SMSG_GROUP_DECLINE)]
    void HandleGroupDecline(WorldPacket packet)
    {
        GroupDecline party = new GroupDecline();
        party.Name = packet.ReadCString();
        SendPacketToClient(party);
    }

    [PacketHandler(Opcode.SMSG_PARTY_INVITE)]
    void HandleGroupInvite(WorldPacket packet)
    {
        PartyInvite party = new PartyInvite();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            party.CanAccept = packet.ReadBool();

        var realm = GetSession().RealmManager.GetRealm(GetSession().RealmId)!;
        party.InviterRealm = new VirtualRealmInfo(realm.Id.GetAddress(), true, false, realm.Name, realm.NormalizedName);

        party.InviterName = packet.ReadCString();
        party.InviterGUID = GetSession().GameState.GetPlayerGuidByName(party.InviterName);
        if (party.InviterGUID == default)
        {
            party.InviterBNetAccountId = WowGuid128.Empty;
        }
        else
            party.InviterBNetAccountId = GetSession().GetBnetAccountGuidForPlayer(party.InviterGUID);

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
        {
            party.ProposedRoles = packet.ReadUInt32();
            var lfgSlotsCount = packet.ReadUInt8();
            for (var i = 0; i < lfgSlotsCount; ++i)
                party.LfgSlots.Add(packet.ReadInt32());
            party.LfgCompletedMask = packet.ReadInt32();
        }

        SendPacketToClient(party);
    }

    [PacketHandler(Opcode.SMSG_GROUP_LIST, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    void HandleGroupListVanilla(WorldPacket packet)
    {
        // JimsProxy: master-loot state used to be cleared unconditionally here, which
        // meant every SMSG_GROUP_LIST -- raid leader change, instance re-entry resync,
        // member join/leave -- would null out MasterLootCandidates and reset
        // LastMasterLootSentTarget. The vanilla server only emits SMSG_LOOT_MASTER_LIST
        // once per corpse (to whoever opens loot first; see comment in HandleLootMasterList),
        // so after the clear the next SMSG_LOOT_RESPONSE for that corpse fell back to
        // group.PlayerList -- the entire 40-player raid, not the real in-range candidate
        // list -- and re-emitted LootList for an already-displayed corpse, auto-opening
        // the popup for a stale item. Net effect: leader can't distribute loot after
        // promotion or instance re-entry. The natural lifecycle (LOOT_RELEASE clears the
        // sent-target, fresh SMSG_LOOT_MASTER_LIST overwrites candidates) covers cleanup
        // for active sessions; we only need to reset when the group is fully destroyed
        // (membersCount == 0 branch below).
        PartyUpdate party = new PartyUpdate();
        party.SequenceNum = GetSession().GameState.GroupUpdateCounter++;
        bool isRaid = packet.ReadBool();
        byte ownSubGroupAndFlags = packet.ReadUInt8();
        party.PartyIndex = (byte)(isRaid && GetSession().GameState.IsInBattleground() ? 1 : 0);
        party.PartyGUID = WowGuid128.Create(HighGuidType703.Party, (ulong)(1000 + party.PartyIndex));
        if (party.PartyIndex != 0)
            party.PartyFlags |= GroupFlags.FakeRaid;

        var uniqueMembers = new HashSet<WowGuid128>();
        uint membersCount = packet.ReadUInt32();
        if (membersCount > 0)
        {
            if (isRaid)
                party.PartyFlags |= GroupFlags.Raid;

            party.DifficultySettings = new PartyDifficultySettings();
            party.DifficultySettings.DungeonDifficultyID = DifficultyModern.Normal;

            if (ModernVersion.ExpansionVersion > 1)
                party.DifficultySettings.RaidDifficultyID = DifficultyModern.Raid25N;
            else
                party.DifficultySettings.RaidDifficultyID = DifficultyModern.Raid40;

            if (party.PartyIndex != 0)
                party.PartyType = GroupType.PvP;
            else
                party.PartyType = GroupType.Normal;

            PartyPlayerInfo player = new PartyPlayerInfo();
            player.GUID = GetSession().GameState.CurrentPlayerGuid;
            player.Name = GetSession().GameState.GetPlayerName(player.GUID);
            player.Subgroup = (byte)(ownSubGroupAndFlags & 0xF);
            player.Flags = (ownSubGroupAndFlags & 0x80) != 0 ? GroupMemberFlags.Assistant : GroupMemberFlags.None;
            player.Status = GroupMemberOnlineStatus.Online;
            //MIRASU: Own class from the session cache (populated at char-select). Without
            //MIRASU: this, self shows in party UI with no class icon.
            player.ClassId = GetSession().GameState.GetUnitClass(player.GUID); //MIRASU
            party.PlayerList.Add(player);

            bool allAssist = true;
            for (uint i = 0; i < membersCount; i++)
            {
                PartyPlayerInfo member = new PartyPlayerInfo();
                member.Name = packet.ReadCString();
                member.GUID = packet.ReadGuid().To128(GetSession().GameState);
                member.Status = (GroupMemberOnlineStatus)packet.ReadUInt8();
                NotePartyMemberOnlineEdge(member.GUID, member.Status.HasFlag(GroupMemberOnlineStatus.Online)); // JimsProxy (#513, rule 5)
                byte subGroupAndFlags = packet.ReadUInt8();
                member.Subgroup = (byte)(subGroupAndFlags & 0xF);
                member.Flags = (subGroupAndFlags & 0x80) != 0 ? GroupMemberFlags.Assistant : GroupMemberFlags.None;
                //MIRASU: If the member isn't in this session's player cache yet (very common
                //MIRASU: for 2-box where session B never saw session A's character until the
                //MIRASU: group forms), GetUnitClass returns Class.Warrior as a default — which
                //MIRASU: paints the wrong class icon in the party UI and, combined with the
                //MIRASU: subsequent SMSG_QUERY_PLAYER_NAME_RESPONSE correcting it a moment later,
                //MIRASU: apparently confuses the 1.14 client into showing the member as "Unknown"
                //MIRASU: until rejoin. Prefer leaving ClassId as 0 (None) here so the client
                //MIRASU: relies solely on the name-query response for the class.
                if (GetSession().GameState.CachedPlayers.ContainsKey(member.GUID)) //MIRASU
                    member.ClassId = GetSession().GameState.GetUnitClass(member.GUID); //MIRASU
                                                                                       // else: leave as Class.None (0); client will populate from SMSG_QUERY_PLAYER_NAME_RESPONSE
                if (!member.Flags.HasAnyFlag(GroupMemberFlags.Assistant))
                    allAssist = false;

                if (!uniqueMembers.Contains(member.GUID))
                {
                    party.PlayerList.Add(member);
                    uniqueMembers.Add(member.GUID);
                }

                Session.GameState.UpdatePlayerCache(member.GUID, new PlayerCache
                { // it is not guaranteed that the client will invoke a QUERY_PLAYER_NAME. Client caches in between logins
                    Name = member.Name,
                    ClassId = member.ClassId,
                });
            }

            if (allAssist)
                party.PartyFlags |= GroupFlags.EveryoneAssistant;

            party.LeaderGUID = packet.ReadGuid().To128(GetSession().GameState);

            party.LootSettings = new PartyLootSettings();
            party.LootSettings.Method = (LootMethod)packet.ReadUInt8();
            party.LootSettings.LootMaster = packet.ReadGuid().To128(GetSession().GameState);
            party.LootSettings.Threshold = packet.ReadUInt8();

            GetSession().GameState.WeWantToLeaveGroup = false;
            GetSession().GameState.CurrentGroups[party.PartyIndex] = party;
            GetSession().GameState.PrunePartyAuraSlots(); // JimsProxy (#513 follow-up)
        }
        else
        {
            //MIRASU: Kronos (TrinityCore-1.12) sends an empty SMSG_GROUP_LIST as a
            //MIRASU: "clear state" packet BEFORE sending the populated one on party
            //MIRASU: formation. If we weren't tracking an active group at this index,
            //MIRASU: this empty is a protocol-level no-op for the modern client — do
            //MIRASU: not synthesize a GroupUninvite, which the 1.14 client then treats
            //MIRASU: as "you were kicked" and corrupts the party-UI state so members
            //MIRASU: appear offline. Only send the uninvite if we actually had a group
            //MIRASU: to dismiss. See jsonl trace L1474 @ 2026-04-23T15:48:27.
            bool hadActiveGroupM = GetSession().GameState.CurrentGroups[party.PartyIndex] != null; //MIRASU

            party.PartyFlags |= GroupFlags.Destroyed;
            if (party.PartyIndex == 0)
                party.PartyGUID = WowGuid128.Empty;
            party.LeaderGUID = WowGuid128.Empty;
            party.MyIndex = -1;
            GetSession().GameState.CurrentGroups[party.PartyIndex] = null;
            GetSession().GameState.PrunePartyAuraSlots(); // JimsProxy (#513 follow-up)

            // JimsProxy: group fully destroyed (kick / disband / leave) -- drop any cached
            // master-loot state so a future group doesn't inherit stale candidates.
            GetSession().GameState.MasterLootCandidates = null;
            GetSession().GameState.LastMasterLootSentTarget = default;

            if (hadActiveGroupM && !GetSession().GameState.WeWantToLeaveGroup) //MIRASU - was: if (!WeWantToLeaveGroup)
                SendPacketToClient(new GroupUninvite()); // Send kick message

            if (!hadActiveGroupM) //MIRASU
            { //MIRASU
                //MIRASU: Skip forwarding the synthetic destroyed-party packet entirely
                //MIRASU: when there was no prior group — it confuses the modern client.
                return; //MIRASU
            } //MIRASU
        }

        SendPacketToClient(party);
    }

    [PacketHandler(Opcode.SMSG_GROUP_LIST, ClientVersionBuild.V2_0_1_6180)]
    void HandleGroupListTBC(WorldPacket packet)
    {
        // JimsProxy: see HandleGroupListVanilla -- master-loot state clear moved into
        // the membersCount == 0 (group destroyed) branch so leader change / instance
        // re-entry don't break an active master-loot session.
        PartyUpdate party = new PartyUpdate();
        party.SequenceNum = GetSession().GameState.GroupUpdateCounter++;
        bool isRaid = packet.ReadBool();
        bool isBattleground = packet.ReadBool();
        byte ownSubGroup = packet.ReadUInt8();
        byte ownGroupFlags = packet.ReadUInt8();
        party.PartyIndex = (byte)(isBattleground ? 1 : 0);
        party.PartyGUID = packet.ReadGuid().To128(GetSession().GameState);
        if (party.PartyIndex != 0)
            party.PartyFlags |= GroupFlags.FakeRaid;

        var uniqueMembers = new HashSet<WowGuid128>();
        uint membersCount = packet.ReadUInt32();
        if (membersCount > 0)
        {
            if (isRaid)
                party.PartyFlags |= GroupFlags.Raid;

            if (party.PartyIndex != 0)
                party.PartyType = GroupType.PvP;
            else
                party.PartyType = GroupType.Normal;

            PartyPlayerInfo player = new PartyPlayerInfo();
            player.GUID = GetSession().GameState.CurrentPlayerGuid;
            player.Name = GetSession().GameState.GetPlayerName(player.GUID);
            player.Subgroup = ownSubGroup;
            player.Flags = (GroupMemberFlags)ownGroupFlags;
            player.Status = GroupMemberOnlineStatus.Online;
            party.PlayerList.Add(player);

            bool allAssist = true;
            for (uint i = 0; i < membersCount; i++)
            {
                PartyPlayerInfo member = new PartyPlayerInfo();
                member.Name = packet.ReadCString();
                member.GUID = packet.ReadGuid().To128(GetSession().GameState);
                member.Status = (GroupMemberOnlineStatus)packet.ReadUInt8();
                NotePartyMemberOnlineEdge(member.GUID, member.Status.HasFlag(GroupMemberOnlineStatus.Online)); // JimsProxy (#513, rule 5)
                member.Subgroup = packet.ReadUInt8();
                member.Flags = (GroupMemberFlags)packet.ReadUInt8();
                
                if (!member.Flags.HasAnyFlag(GroupMemberFlags.Assistant))
                    allAssist = false;

                if (!uniqueMembers.Contains(member.GUID))
                {
                    party.PlayerList.Add(member);
                    uniqueMembers.Add(member.GUID);
                }

                Session.GameState.UpdatePlayerCache(member.GUID, new PlayerCache
                { // it is not guaranteed that the client will invoke a QUERY_PLAYER_NAME. Client caches in between logins
                    Name = member.Name,
                    ClassId = member.ClassId,
                });
            }

            if (allAssist)
                party.PartyFlags |= GroupFlags.EveryoneAssistant;

            party.LeaderGUID = packet.ReadGuid().To128(GetSession().GameState);

            party.LootSettings = new PartyLootSettings();
            party.LootSettings.Method = (LootMethod)packet.ReadUInt8();
            party.LootSettings.LootMaster = packet.ReadGuid().To128(GetSession().GameState);
            party.LootSettings.Threshold = packet.ReadUInt8();

            party.DifficultySettings = new PartyDifficultySettings();
            int difficultyId = packet.ReadUInt8();
            party.DifficultySettings.DungeonDifficultyID = ((DifficultyLegacy)difficultyId).CastEnum<DifficultyModern>();

            if (ModernVersion.ExpansionVersion > 1)
                party.DifficultySettings.RaidDifficultyID = DifficultyModern.Raid25N;
            else
                party.DifficultySettings.RaidDifficultyID = DifficultyModern.Raid40;

            GetSession().GameState.WeWantToLeaveGroup = false;
            GetSession().GameState.CurrentGroups[party.PartyIndex] = party;
            GetSession().GameState.PrunePartyAuraSlots(); // JimsProxy (#513 follow-up)
        }
        else
        {
            //MIRASU: See HandleGroupListVanilla — TBC variant has same empty-before-populated
            //MIRASU: pattern on TrinityCore-derived servers.
            bool hadActiveGroupM = GetSession().GameState.CurrentGroups[party.PartyIndex] != null; //MIRASU

            party.PartyFlags |= GroupFlags.Destroyed;
            if (party.PartyIndex == 0)
                party.PartyGUID = WowGuid128.Empty;
            party.LeaderGUID = WowGuid128.Empty;
            party.MyIndex = -1;
            GetSession().GameState.CurrentGroups[party.PartyIndex] = null;
            GetSession().GameState.PrunePartyAuraSlots(); // JimsProxy (#513 follow-up)

            // JimsProxy: group fully destroyed -- drop cached master-loot state so a future
            // group doesn't inherit stale candidates. See HandleGroupListVanilla for full note.
            GetSession().GameState.MasterLootCandidates = null;
            GetSession().GameState.LastMasterLootSentTarget = default;

            if (hadActiveGroupM && !GetSession().GameState.WeWantToLeaveGroup) //MIRASU
                SendPacketToClient(new GroupUninvite()); // Send kick message

            if (!hadActiveGroupM) //MIRASU
                return; //MIRASU
        }

        SendPacketToClient(party);
    }

    [PacketHandler(Opcode.SMSG_GROUP_UNINVITE)]
    void HandleGroupUninvite(WorldPacket packet)
    {
        GroupUninvite party = new GroupUninvite();
        SendPacketToClient(party);
    }

    [PacketHandler(Opcode.SMSG_GROUP_NEW_LEADER)]
    void HandleGroupNewLeader(WorldPacket packet)
    {
        GroupNewLeader party = new GroupNewLeader();
        party.Name = packet.ReadCString();
        party.PartyIndex = GetSession().GameState.GetCurrentPartyIndex();
        SendPacketToClient(party);
    }

    [PacketHandler(Opcode.MSG_RAID_READY_CHECK, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    void HandleRaidReadyCheckVanilla(WorldPacket packet)
    {
        if (!packet.CanRead())
        {
            ReadyCheckStarted ready = new ReadyCheckStarted();
            ready.InitiatorGUID = GetSession().GameState.GetCurrentGroupLeader();
            ready.PartyIndex = GetSession().GameState.GetCurrentPartyIndex();
            ready.PartyGUID = GetSession().GameState.GetCurrentGroupGuid();
            SendPacketToClient(ready);
        }
        else
        {
            ReadyCheckResponse ready = new ReadyCheckResponse();
            ready.Player = packet.ReadGuid().To128(GetSession().GameState);
            ready.IsReady = packet.ReadBool();
            ready.PartyGUID = GetSession().GameState.GetCurrentGroupGuid();
            SendPacketToClient(ready);

            GetSession().GameState.GroupReadyCheckResponses++;
            if (GetSession().GameState.GroupReadyCheckResponses >= GetSession().GameState.GetCurrentGroupSize())
            {
                GetSession().GameState.GroupReadyCheckResponses = 0;
                ReadyCheckCompleted completed = new ReadyCheckCompleted();
                completed.PartyIndex = GetSession().GameState.GetCurrentPartyIndex();
                completed.PartyGUID = GetSession().GameState.GetCurrentGroupGuid();
                SendPacketToClient(completed);
            }
        }
    }

    [PacketHandler(Opcode.MSG_RAID_READY_CHECK, ClientVersionBuild.V2_0_1_6180)]
    void HandleRaidReadyCheck(WorldPacket packet)
    {
        ReadyCheckStarted ready = new ReadyCheckStarted();
        ready.InitiatorGUID = packet.ReadGuid().To128(GetSession().GameState);
        ready.PartyIndex = GetSession().GameState.GetCurrentPartyIndex();
        ready.PartyGUID = GetSession().GameState.GetCurrentGroupGuid();
        SendPacketToClient(ready);
    }

    [PacketHandler(Opcode.MSG_RAID_READY_CHECK_CONFIRM, ClientVersionBuild.V2_0_1_6180)]
    void HandleRaidReadyCheckConfirm(WorldPacket packet)
    {
        ReadyCheckResponse ready = new ReadyCheckResponse();
        ready.Player = packet.ReadGuid().To128(GetSession().GameState);
        ready.IsReady = packet.ReadBool();
        ready.PartyGUID = GetSession().GameState.GetCurrentGroupGuid();
        SendPacketToClient(ready);

        GetSession().GameState.GroupReadyCheckResponses++;
        if (GetSession().GameState.GroupReadyCheckResponses >= GetSession().GameState.GetCurrentGroupSize())
        {
            GetSession().GameState.GroupReadyCheckResponses = 0;
            ReadyCheckCompleted completed = new ReadyCheckCompleted();
            completed.PartyIndex = GetSession().GameState.GetCurrentPartyIndex();
            completed.PartyGUID = GetSession().GameState.GetCurrentGroupGuid();
            SendPacketToClient(completed);
        }
    }

    [PacketHandler(Opcode.MSG_RAID_READY_CHECK_FINISHED, ClientVersionBuild.V2_0_1_6180)]
    void HandleRaidReadyCheckFinished(WorldPacket packet)
    {
        ReadyCheckCompleted ready = new ReadyCheckCompleted();
        ready.PartyIndex = GetSession().GameState.GetCurrentPartyIndex();
        ready.PartyGUID = GetSession().GameState.GetCurrentGroupGuid();
        SendPacketToClient(ready);
    }

    [PacketHandler(Opcode.MSG_RAID_TARGET_UPDATE)]
    void HandleRaidTargetUpdate(WorldPacket packet)
    {
        bool isFullUpdate = packet.ReadBool();
        if (isFullUpdate)
        {
            SendRaidTargetUpdateAll update = new SendRaidTargetUpdateAll();
            update.PartyIndex = GetSession().GameState.GetCurrentPartyIndex();
            while (packet.CanRead())
            {
                sbyte symbol = packet.ReadInt8();
                WowGuid128 guid = packet.ReadGuid().To128(GetSession().GameState);
                update.TargetIcons.Add(new Tuple<sbyte, WowGuid128>(symbol, guid));
            }
            SendPacketToClient(update);
        }
        else
        {
            SendRaidTargetUpdateSingle update = new SendRaidTargetUpdateSingle();
            update.PartyIndex = GetSession().GameState.GetCurrentPartyIndex();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                update.ChangedBy = packet.ReadGuid().To128(GetSession().GameState);
            else
                update.ChangedBy = GetSession().GameState.CurrentPlayerGuid;
            
            update.Symbol = packet.ReadInt8();
            update.Target = packet.ReadGuid().To128(GetSession().GameState);
            SendPacketToClient(update);
        }
    }

    [PacketHandler(Opcode.SMSG_SUMMON_REQUEST)]
    void HandleSummonRequest(WorldPacket packet)
    {
        SummonRequest summon = new SummonRequest();
        summon.SummonerGUID = packet.ReadGuid().To128(GetSession().GameState);
        summon.SummonerVirtualRealmAddress = GetSession().RealmId.GetAddress();
        summon.AreaID = packet.ReadInt32();
        packet.ReadUInt32(); // time to accept
        SendPacketToClient(summon);
    }

    uint _requestBgPlayerPosCounter = 0;

    [PacketHandler(Opcode.SMSG_PARTY_MEMBER_PARTIAL_STATE, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    //MIRASU: Same OOB-truncation risk as HandlePartyMemberStatsFull — see comment
    //MIRASU: block on that handler. Kronos sends SMSG_PARTY_MEMBER_PARTIAL_STATE with
    //MIRASU: flag-gated fields that can be truncated for distant members.
    void HandlePartyMemberStats(WorldPacket packet)
    {
        
        if (GetSession().GameState.CurrentMapId == (uint)BattlegroundMapID.WarsongGulch &&
           (GetSession().GameState.HasWsgAllyFlagCarrier || GetSession().GameState.HasWsgHordeFlagCarrier))
        {
            if (_requestBgPlayerPosCounter++ > 10) // don't spam every time somebody moves
            {
                WorldPacket packet2 = new WorldPacket(Opcode.MSG_BATTLEGROUND_PLAYER_POSITIONS);
                SendPacket(packet2);
                _requestBgPlayerPosCounter = 0;
            }
        }

        PartyMemberPartialState state = new PartyMemberPartialState();
        state.AffectedGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        var updateFlags = (GroupUpdateFlagVanilla)packet.ReadUInt32();

       
            try //MIRASU: OOB-tolerant region — see HandlePartyMemberStatsFull
            {
                if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Status))
                {
                    state.StatusFlags = packet.ReadUInt8();// GroupMemberOnlineStatus
                    NotePartyMemberOnlineEdge(state.AffectedGUID, (state.StatusFlags.Value & (ushort)GroupMemberOnlineStatus.Online) != 0); // JimsProxy (#513, rule 5)
                }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.CurrentHealth))
            state.CurrentHealth = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.MaxHealth))
            state.MaxHealth = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PowerType))
            state.PowerType = packet.ReadUInt8();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.CurrentPower))
            state.CurrentPower = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.MaxPower))
            state.MaxPower = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Level))
            state.Level = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Zone))
            state.ZoneID = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Position))
        {
            state.Position = new PartyMemberPartialState.Vector3_UInt16();
            state.Position.X = packet.ReadInt16();
            state.Position.Y = packet.ReadInt16();
        }

        // JimsProxy (#513 follow-up, out-of-view party buff lists): the legacy masks are
        // slot-indexed deltas (bit i = slot i, negative bit j = slot 32 + j, spell id 0 =
        // cleared), while the 1.14 client REPLACES the member's stored list with every
        // aura-bearing party state. Forwarding the delta as a standalone list shrank the
        // member's buff list on screen to the last change until the next full state, and a
        // SpellId 0 entry stops an addon's buff loop early. Patch the member's slot table and
        // emit the complete non-zero list instead. Auras stays null when no aura bit is set
        // (the #435 rule: a non-null empty list wipes the display). See PartyAuraSlotTable.
        bool memberAuraBitsPresent = false;
        var memberAuraChanges = new List<PartyAuraSlotTable.SlotChange>();
        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Auras))
        {
            memberAuraBitsPresent = true;
            var auraMask = packet.ReadUInt32(); // Positive Aura Mask: bit i = slot i
            for (int i = 0; i < PartyAuraSlotTable.PositiveSlots; ++i)
                if ((auraMask & (1u << i)) != 0)
                    memberAuraChanges.Add(new PartyAuraSlotTable.SlotChange(i, packet.ReadUInt16()));
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.AurasNegative))
        {
            memberAuraBitsPresent = true;
            var auraMask = packet.ReadUInt16(); // Negative Aura Mask: bit j = slot 32 + j
            for (int j = 0; j < PartyAuraSlotTable.NegativeSlots; ++j)
                if ((auraMask & (1u << j)) != 0)
                    memberAuraChanges.Add(new PartyAuraSlotTable.SlotChange(PartyAuraSlotTable.PositiveSlots + j, packet.ReadUInt16()));
        }

        if (memberAuraBitsPresent)
        {
            var memberSlots = GetSession().GameState.GetOrAddPartyAuraSlots(GetSession().GameState.PartyMemberAuraSlots, state.AffectedGUID);
            // JimsProxy (#513): stamp out-of-view starts and removals against the table as it
            // was before this delta (the previous occupants decide first-seen versus relist).
            StampPartyAuraStartsFromDelta(state.AffectedGUID, memberSlots, memberAuraChanges, updateFlags.HasFlag(GroupUpdateFlagVanilla.Status));
            PartyAuraSlotTable.ApplyDelta(memberSlots, memberAuraChanges);
            state.Auras = PartyAuraSlotTable.BuildAuraList(memberSlots);
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetGuid))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.NewPetGuid = packet.ReadGuid().To128(GetSession().GameState);
        }
            

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetName))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.NewPetName = packet.ReadCString();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetModelId))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.DisplayID = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetCurrentHealth))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.Health = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetMaxHealth))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.MaxHealth = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetPowerType))
            packet.ReadUInt8(); // Pet Power Type

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetCurrentPower))
            packet.ReadInt16(); // Pet Current Power

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetMaxPower))
            packet.ReadInt16(); // Pet Max Power

        // JimsProxy (#513 follow-up): the pet's aura masks get the same slot-table treatment,
        // keyed by the member guid (a pet's own guid changes on every summon).
        bool petAuraBitsPresent = false;
        var petAuraChanges = new List<PartyAuraSlotTable.SlotChange>();
        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetAuras))
        {
            petAuraBitsPresent = true;
            var auraMask = packet.ReadUInt32(); // Pet Positive Aura Mask: bit i = slot i
            for (int i = 0; i < PartyAuraSlotTable.PositiveSlots; ++i)
                if ((auraMask & (1u << i)) != 0)
                    petAuraChanges.Add(new PartyAuraSlotTable.SlotChange(i, packet.ReadUInt16()));
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetAurasNegative))
        {
            petAuraBitsPresent = true;
            var auraMask = packet.ReadUInt16(); // Pet Negative Aura Mask: bit j = slot 32 + j
            for (int j = 0; j < PartyAuraSlotTable.NegativeSlots; ++j)
                if ((auraMask & (1u << j)) != 0)
                    petAuraChanges.Add(new PartyAuraSlotTable.SlotChange(PartyAuraSlotTable.PositiveSlots + j, packet.ReadUInt16()));
        }

        if (petAuraBitsPresent)
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            var petSlots = GetSession().GameState.GetOrAddPartyAuraSlots(GetSession().GameState.PartyPetAuraSlots, state.AffectedGUID);
            PartyAuraSlotTable.ApplyDelta(petSlots, petAuraChanges);
            state.Pet.Auras = PartyAuraSlotTable.BuildAuraList(petSlots);
        }
        } //MIRASU: end try — exit normally with fully-parsed state
        catch (System.IndexOutOfRangeException exM) //MIRASU
        { //MIRASU
            Log.Event("packet.partial", new //MIRASU
            { //MIRASU
                direction = "s2c", //MIRASU
                opcode_universal = "SMSG_PARTY_MEMBER_PARTIAL_STATE", //MIRASU
                reason = "flag_truncated", //MIRASU
                update_flags = (uint)updateFlags, //MIRASU
                affected_guid = state.AffectedGUID.ToString(), //MIRASU
                message = exM.Message, //MIRASU
            }); //MIRASU
        } //MIRASU
        catch (System.ArgumentOutOfRangeException exM2) //MIRASU
        { //MIRASU - BinaryPrimitives throws this variant on span underrun
            Log.Event("packet.partial", new //MIRASU
            { //MIRASU
                direction = "s2c", //MIRASU
                opcode_universal = "SMSG_PARTY_MEMBER_PARTIAL_STATE", //MIRASU
                reason = "flag_truncated", //MIRASU
                update_flags = (uint)updateFlags, //MIRASU
                affected_guid = state.AffectedGUID.ToString(), //MIRASU
                message = exM2.Message, //MIRASU
            }); //MIRASU
        } //MIRASU

        MergeAndRefreshPartyStats(state, updateFlags.HasFlag(GroupUpdateFlagVanilla.PetGuid));
        CachePartyPetName(state.Pet);
        // JimsProxy (review follow-up): partial states stream continuously for every
        // member (health ticks alone), so an ungated per-packet event is ~10/s of
        // permanent JSONL in a 40-raid. DebugOutput-gated; pet.name.* stays ungated
        // (rare, per-edge).
        if (Framework.Settings.DebugOutput)
            Log.Event("party.partial_state.translated", new
            {
                member_guid = state.AffectedGUID.ToString(),
                update_flags = (uint)updateFlags,
                level = state.Level,
                aura_count = state.Auras?.Count,
                has_pet = state.Pet != null,
                pet_guid = state.Pet != null ? state.Pet.NewPetGuid.ToString() : null,
                pet_name = state.Pet?.NewPetName,
                pet_health = state.Pet?.Health,
            });
        SendPacketToClient(state);
    }

    // JimsProxy (#513, stats start classes): out-of-view party buffs. A spell newly listed in a
    // non-bulk partial state was applied within about one second of this packet (the server
    // sends these deltas on a short grid), so its start is known to that bound:
    // STATS_FIRST_SEEN. The same spell relisted alone in a later non-bulk partial is a refresh:
    // STATS_RELIST. A bulk listing (a full state, a status-flag partial, two or more restates,
    // or a partial that restates the member's last full list within 3 s of it) records
    // nothing (rule 2). A cleared or swapped slot drops the old spell's record unless the same
    // spell sits in another slot (rule 4). Pet buffs are not stamped (a pet's guid changes with
    // every summon). The decisions are in AuraTimerProvenance; the records are consumed by the
    // aura emit path when the member comes back into view.
    void StampPartyAuraStartsFromDelta(WowGuid128 member, uint[] tableBefore, List<PartyAuraSlotTable.SlotChange> changes, bool hasStatusFlag)
    {
        var gameState = GetSession().GameState;
        // A member in view is timed by the in-view paths; a stats delta racing a view entry must
        // not stamp a running buff as fresh or wipe the in-view slot state.
        if (gameState.GetCachedObjectFieldsLegacy(member) != null)
            return;
        gameState.PartyMemberLastFullAuras.TryGetValue(member, out var lastFull);
        long? sinceFull = lastFull.Spells != null ? Environment.TickCount64 - lastFull.Tick : null;
        var result = AuraTimerProvenance.ClassifyPartyDelta(tableBefore, changes, hasStatusFlag, lastFull.Spells, sinceFull,
            GameData.GetModernSpellId, GameData.GetAuraSpellDuration,
            id => GameData.TalentDurationModifiers.ContainsKey(id), GameData.IsComboPointFinisher);

        foreach (var cleared in result.Cleared)
            ClearPartyAuraRecordForSlot(member, cleared.Slot, cleared.LegacySpellId, cleared.SpellStillElsewhere);

        if (result.Bulk)
        {
            if (Framework.Settings.DebugOutput)
                Log.Event("party.aura.bulk_listing", new
                {
                    member_low = member.GetCounter(),
                    has_status = hasStatusFlag,
                    restated = result.RestatedCount,
                    ms_since_full = sinceFull,
                });
            return;
        }

        // Not knowable out of view (a variable duration, or one too short for the one-second
        // bound): a fresh application must not inherit an older record of the same spell.
        foreach (var modernId in result.UnknownFreshModernIds)
            gameState.ClearAuraExpiry(member, (int)modernId);

        foreach (var stamp in result.Stamps)
        {
            gameState.RecordAuraExpiry(member, (int)stamp.ModernSpellId, stamp.FullMs, stamp.StartClass, stamp.Source, stamp.FullMs, AuraTimerProvenance.StatsStartBoundMs);
            // Fix-working breadcrumb, frequent in a raid: DebugOutput only (review of #547).
            if (Framework.Settings.DebugOutput)
                Log.Event("party.aura.start_stamped", new
                {
                    member_low = member.GetCounter(),
                    slot = stamp.Slot,
                    spell_id = stamp.ModernSpellId,
                    start_class = stamp.StartClass.ToString(),
                    full_ms = stamp.FullMs,
                });
        }
    }

    // Rule 4: a slot that lost its spell (cleared or swapped out of view) drops that spell's
    // record unless the same spell still sits in another slot, and drops the slot's state.
    void ClearPartyAuraRecordForSlot(WowGuid128 member, int slot, uint legacySpellId, bool spellStillElsewhere)
    {
        var gameState = GetSession().GameState;
        if (!spellStillElsewhere)
            gameState.ClearAuraExpiry(member, (int)GameData.GetModernSpellId(legacySpellId));
        gameState.ClearAuraDuration(member, (byte)slot);
        gameState.ClearAuraCaster(member, (byte)slot);
        gameState.ClearLastEmittedAura(member, (byte)slot);
    }

    // Rule 2 (bulk) and rule 4 (removals) for a full state: remember the list and when it
    // came, and clear the records of spells the full list no longer carries.
    void NotePartyAuraFullState(WowGuid128 member, uint[] tableBefore, List<PartyAuraSlotTable.SlotChange> changes)
    {
        var gameState = GetSession().GameState;
        var listedModern = new HashSet<uint>();
        var listedLegacy = new HashSet<uint>();
        foreach (var change in changes)
        {
            if (change.LegacySpellId == 0)
                continue;
            listedLegacy.Add(change.LegacySpellId);
            listedModern.Add(GameData.GetModernSpellId(change.LegacySpellId));
        }
        if (gameState.GetCachedObjectFieldsLegacy(member) == null)
        {
            for (int s = 0; s < tableBefore.Length; s++)
            {
                uint before = tableBefore[s];
                if (before != 0 && !listedLegacy.Contains(before))
                    ClearPartyAuraRecordForSlot(member, s, before, spellStillElsewhere: false);
            }
            // Records of spells the full list does not carry are stale whatever the table held
            // (after a relog the table starts empty, the carried-over map does not).
            var keep = new HashSet<int>();
            foreach (var id in listedModern)
                keep.Add((int)id);
            gameState.DropAuraExpiriesForUnitExcept(member, keep);
        }
        gameState.PartyMemberLastFullAuras[member] = (Environment.TickCount64, listedModern);
    }

    // Rule 5: the server pauses a member's buffs while they are offline (field-verified
    // 2026-10-12: a member's own Fortitude came back 7.0 s longer after a 7.1 s logout). The
    // offline edge marks the member; the return edge (this status, or their create, whichever
    // comes first) shifts every expiry recorded for them by the offline interval, so the
    // watcher's timer and the buff's caster survive the member's relog.
    void NotePartyMemberOnlineEdge(WowGuid128 member, bool online)
    {
        var gameState = GetSession().GameState;
        gameState.PartyMemberLastOnline.TryGetValue(member, out bool wasOnline);
        if (wasOnline && !online)
        {
            gameState.MarkPartyMemberOffline(member);
            // Fix-working breadcrumb: DebugOutput only (review of #547).
            if (Framework.Settings.DebugOutput)
                Log.Event("party.aura.offline_mark", new { member_low = member.GetCounter() });
        }
        else if (online && gameState.ShiftAuraExpiriesIfReturning(member) is var (offlineMs, shifted))
        {
            // Fix-working breadcrumb: DebugOutput only (review of #547).
            if (Framework.Settings.DebugOutput)
                Log.Event("party.aura.offline_shift", new { member_low = member.GetCounter(), offline_ms = offlineMs, shifted, source = "status" });
        }
        gameState.PartyMemberLastOnline[member] = online;
    }

    // The legacy server only re-sends party fields when they change, but the modern
    // client expires member/pet data it hasn't seen refreshed — merge each delta into
    // a per-member snapshot and re-attach it to every forwarded partial update.
    void MergeAndRefreshPartyStats(PartyMemberPartialState state, bool petGuidInPacket)
    {
        var gameState = GetSession().GameState;
        if (state.Level.HasValue)
            gameState.PartyMemberLevels[state.AffectedGUID] = state.Level.Value;
        else if (gameState.PartyMemberLevels.TryGetValue(state.AffectedGUID, out var level))
            state.Level = level;

        var pets = gameState.PartyPetStats;
        if (petGuidInPacket && (state.Pet == null || state.Pet.NewPetGuid == default))
        {
            // guid arrived as zero — the pet was dismissed, drop the snapshot
            pets.Remove(state.AffectedGUID);
            gameState.PartyPetAuraSlots.Remove(state.AffectedGUID); // JimsProxy (#513 follow-up)
            if (state.Pet != null)
                state.Pet.ExplicitClear = true;
            return;
        }
        pets.TryGetValue(state.AffectedGUID, out var cached);
        if (state.Pet != null)
        {
            cached ??= new PartyMemberPetStats();
            if (state.Pet.NewPetGuid != default)
            {
                // JimsProxy (#513 follow-up): a different pet guid is a new pet; its aura
                // slots start empty rather than inheriting the previous pet's.
                if (cached.NewPetGuid != state.Pet.NewPetGuid)
                    gameState.PartyPetAuraSlots.Remove(state.AffectedGUID);
                cached.NewPetGuid = state.Pet.NewPetGuid;
            }
            if (!string.IsNullOrEmpty(state.Pet.NewPetName))
                cached.NewPetName = state.Pet.NewPetName;
            if (state.Pet.DisplayID.HasValue)
                cached.DisplayID = state.Pet.DisplayID;
            if (state.Pet.Health.HasValue)
                cached.Health = state.Pet.Health;
            if (state.Pet.MaxHealth.HasValue)
                cached.MaxHealth = state.Pet.MaxHealth;
            pets[state.AffectedGUID] = cached;
        }
        if (cached == null || cached.NewPetGuid == default)
            return;
        state.Pet ??= new PartyMemberPetStats();
        if (state.Pet.NewPetGuid == default)
            state.Pet.NewPetGuid = cached.NewPetGuid;
        if (string.IsNullOrEmpty(state.Pet.NewPetName))
            state.Pet.NewPetName = cached.NewPetName;
        state.Pet.DisplayID ??= cached.DisplayID;
        state.Pet.Health ??= cached.Health;
        state.Pet.MaxHealth ??= cached.MaxHealth;
    }

    // Full-state counterpart: refresh the snapshots and backfill level if the legacy
    // full state omitted it (a full state would otherwise zero it client-side).
    void SnapshotPartyStatsFromFull(PartyMemberFullState state, bool petGuidInPacket, bool levelInPacket)
    {
        var gameState = GetSession().GameState;
        if (levelInPacket)
            gameState.PartyMemberLevels[state.MemberGuid] = state.Level;
        else if (gameState.PartyMemberLevels.TryGetValue(state.MemberGuid, out var level))
            state.Level = level;

        if (state.Pet != null && state.Pet.NewPetGuid != default)
        {
            gameState.PartyPetStats[state.MemberGuid] = new PartyMemberPetStats
            {
                NewPetGuid = state.Pet.NewPetGuid,
                NewPetName = state.Pet.NewPetName,
                DisplayID = state.Pet.DisplayID,
                Health = state.Pet.Health,
                MaxHealth = state.Pet.MaxHealth,
            };
        }
        else if (petGuidInPacket)
            gameState.PartyPetStats.Remove(state.MemberGuid);
    }

    // Remember party pet names so pet name queries can be answered even when the
    // legacy server returns an empty name for an out-of-range pet.
    void CachePartyPetName(PartyMemberPetStats? pet)
    {
        if (pet == null || pet.NewPetGuid == default || pet.NewPetGuid == WowGuid128.Empty || string.IsNullOrEmpty(pet.NewPetName))
            return;
        var gameState = GetSession().GameState;
        uint petNumber = pet.NewPetGuid.GetEntry();
        gameState.CachedPetNumbers[petNumber] = pet.NewPetGuid;
        gameState.PartyPetNames[petNumber] = pet.NewPetName;
        Log.Event("pet.name.party_stats_cached", new
        {
            pet_guid = pet.NewPetGuid.ToString(),
            pet_number = petNumber,
            name = pet.NewPetName,
        });
    }

    [PacketHandler(Opcode.SMSG_PARTY_MEMBER_PARTIAL_STATE, ClientVersionBuild.V2_0_1_6180)]
    void HandlePartyMemberStatsTbc(WorldPacket packet)
    {
        if (GetSession().GameState.CurrentMapId == (uint)BattlegroundMapID.WarsongGulch &&
           (GetSession().GameState.HasWsgAllyFlagCarrier || GetSession().GameState.HasWsgHordeFlagCarrier))
        {
            if (_requestBgPlayerPosCounter++ > 10) // don't spam every time somebody moves
            {
                WorldPacket packet2 = new WorldPacket(Opcode.MSG_BATTLEGROUND_PLAYER_POSITIONS);
                SendPacket(packet2);
                _requestBgPlayerPosCounter = 0;
            }
        }

        PartyMemberPartialState state = new PartyMemberPartialState();
        state.AffectedGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
        var updateFlags = (GroupUpdateFlagTBC)packet.ReadUInt32();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Status))
            state.StatusFlags = packet.ReadUInt16();// GroupMemberOnlineStatus

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.CurrentHealth))
            state.CurrentHealth = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.MaxHealth))
            state.MaxHealth = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PowerType))
            state.PowerType = packet.ReadUInt8();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.CurrentPower))
            state.CurrentPower = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.MaxPower))
            state.MaxPower = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Level))
            state.Level = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Zone))
            state.ZoneID = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Position))
        {
            state.Position = new PartyMemberPartialState.Vector3_UInt16();
            state.Position.X = packet.ReadInt16();
            state.Position.Y = packet.ReadInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Auras))
        {
            if (state.Auras == null)
                state.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt64();

            for (byte i = 0; i < LegacyVersion.GetAuraSlotsCount(); ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                packet.ReadUInt8(); // unk
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Positive;
                }
                state.Auras.Add(aura);
            }
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetGuid))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.NewPetGuid = packet.ReadGuid().To128(GetSession().GameState);
        }


        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetName))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.NewPetName = packet.ReadCString();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetModelId))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.DisplayID = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetCurrentHealth))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.Health = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetMaxHealth))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.MaxHealth = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetPowerType))
            packet.ReadUInt8(); // Pet Power Type

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetCurrentPower))
            packet.ReadInt16(); // Pet Current Power

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetMaxPower))
            packet.ReadInt16(); // Pet Max Power

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetAuras))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            if (state.Pet.Auras == null)
                state.Pet.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt64();

            for (byte i = 0; i < LegacyVersion.GetAuraSlotsCount(); ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                packet.ReadUInt8(); // unk
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Positive;
                }
                state.Pet.Auras.Add(aura);
            }
        }

        MergeAndRefreshPartyStats(state, updateFlags.HasFlag(GroupUpdateFlagTBC.PetGuid));
        CachePartyPetName(state.Pet);
        SendPacketToClient(state);
    }

    [PacketHandler(Opcode.SMSG_PARTY_MEMBER_FULL_STATE, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    //MIRASU: Kronos/TrinityCore-1.12 sends truncated SMSG_PARTY_MEMBER_FULL_STATE
    //MIRASU: for party members in other zones/maps — the update-flag mask advertises
    //MIRASU: fields that aren't actually serialized, causing flag-gated ReadUInt16/8
    //MIRASU: calls below to walk past end-of-buffer and throw IndexOutOfRangeException.
    //MIRASU: The dispatcher in WorldClient.HandlePacket rethrows, killing the session.
    //MIRASU: Reported as HermesProxy issues #376 #313 #292 #140. We swallow the OOB,
    //MIRASU: emit a structured packet.partial event for visibility, and forward whatever
    //MIRASU: state we managed to parse — the client gracefully degrades (stale fields
    //MIRASU: for the distant member) rather than disconnecting.
    void HandlePartyMemberStatsFull(WorldPacket packet)
    {
        
        if (GetSession().GameState.CurrentMapId == (uint)BattlegroundMapID.WarsongGulch &&
           (GetSession().GameState.HasWsgAllyFlagCarrier || GetSession().GameState.HasWsgHordeFlagCarrier))
        {
            if (_requestBgPlayerPosCounter++ > 10) // don't spam every time somebody moves
            {
                WorldPacket packet2 = new WorldPacket(Opcode.MSG_BATTLEGROUND_PLAYER_POSITIONS);
                SendPacket(packet2);
                _requestBgPlayerPosCounter = 0;
            }
        }

        PartyMemberFullState state = new PartyMemberFullState();
        //MIRASU: Default to Online. GroupMemberOnlineStatus.Offline = 0x0 happens to be
        //MIRASU: the default value of StatusFlags, so when Kronos sends a tiny stats
        //MIRASU: packet without the Status update-flag bit set (common when one character
        //MIRASU: of a 2-box pair hands stats to the other before full party sync), the
        //MIRASU: modern client's party UI shows that member as offline.
        state.StatusFlags = GroupMemberOnlineStatus.Online; //MIRASU
        if (GetSession().GameState.IsInBattleground())
        {
            state.PartyType[0] = 0;
            state.PartyType[1] = 2;
        }
        else
        {
            state.PartyType[0] = 1;
            state.PartyType[1] = 0;
        }
        
        state.MemberGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
        var updateFlags = (GroupUpdateFlagVanilla)packet.ReadUInt32();
        //MIRASU: Kronos sometimes sends SMSG_PARTY_MEMBER_FULL_STATE with only the
        //MIRASU: Status bit set in updateFlags — a status-only delta masquerading as a
        //MIRASU: full state. If we built a PartyMemberFullState and sent it, all the
        //MIRASU: other fields (HP, level, zone, position, auras, pet) would serialize
        //MIRASU: as zero and clobber the modern client's cached values, making members
        //MIRASU: show as offline/0HP after the first delta. Instead, translate this into
        //MIRASU: a PartyMemberPartialState with only StatusFlags set — all other nullable
        //MIRASU: fields are null so Write() skips them and the client's cache is preserved.
        //MIRASU: See jsonl trace L3192/L3456 @ 2026-04-23T16:36:15 for the Kronos pattern.
        if ((updateFlags & ~GroupUpdateFlagVanilla.Status) == GroupUpdateFlagVanilla.None) //MIRASU
        { //MIRASU
            var statusOnlyM = new PartyMemberPartialState(); //MIRASU
            statusOnlyM.AffectedGUID = state.MemberGuid; //MIRASU
            if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Status)) //MIRASU
            { //MIRASU
                statusOnlyM.StatusFlags = (ushort)(GroupMemberOnlineStatus)packet.ReadUInt8(); //MIRASU
                NotePartyMemberOnlineEdge(state.MemberGuid, (statusOnlyM.StatusFlags.Value & (ushort)GroupMemberOnlineStatus.Online) != 0); // JimsProxy (#513, rule 5)
            } //MIRASU
            MergeAndRefreshPartyStats(statusOnlyM, false);
            SendPacketToClient(statusOnlyM); //MIRASU
            return; //MIRASU - skip the full-state build entirely
        } //MIRASU  
        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Status))
        {
            state.StatusFlags = (GroupMemberOnlineStatus)packet.ReadUInt8();
            NotePartyMemberOnlineEdge(state.MemberGuid, state.StatusFlags.HasFlag(GroupMemberOnlineStatus.Online)); // JimsProxy (#513, rule 5)
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.CurrentHealth))
            state.CurrentHealth = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.MaxHealth))
            state.MaxHealth = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PowerType))
            state.PowerType = packet.ReadUInt8();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.CurrentPower))
            state.CurrentPower = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.MaxPower))
            state.MaxPower = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Level))
            state.Level = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Zone))
            state.ZoneID = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Position))
        {
            state.PositionX = packet.ReadInt16();
            state.PositionY = packet.ReadInt16();
        }

        // JimsProxy (#513 follow-up): a full state lists every occupied slot, so rebuild the
        // member's slot table from it and emit the complete list (a 0 entry is never emitted).
        // See the partial-state handler and PartyAuraSlotTable.
        bool memberAuraBitsPresent = false;
        var memberAuraChanges = new List<PartyAuraSlotTable.SlotChange>();
        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Auras))
        {
            memberAuraBitsPresent = true;
            var auraMask = packet.ReadUInt32(); // Positive Aura Mask: bit i = slot i
            for (int i = 0; i < PartyAuraSlotTable.PositiveSlots; ++i)
                if ((auraMask & (1u << i)) != 0)
                    memberAuraChanges.Add(new PartyAuraSlotTable.SlotChange(i, packet.ReadUInt16()));
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.AurasNegative))
        {
            memberAuraBitsPresent = true;
            var auraMask = packet.ReadUInt16(); // Negative Aura Mask: bit j = slot 32 + j
            for (int j = 0; j < PartyAuraSlotTable.NegativeSlots; ++j)
                if ((auraMask & (1u << j)) != 0)
                    memberAuraChanges.Add(new PartyAuraSlotTable.SlotChange(PartyAuraSlotTable.PositiveSlots + j, packet.ReadUInt16()));
        }

        if (memberAuraBitsPresent)
        {
            var memberSlots = GetSession().GameState.GetOrAddPartyAuraSlots(GetSession().GameState.PartyMemberAuraSlots, state.MemberGuid);
            // JimsProxy (#513, rule 2): a full state is a bulk listing and records no start; it
            // only remembers what it listed (for the bulk-window check on the partials that
            // follow) and clears the records of spells the list no longer carries (rule 4).
            NotePartyAuraFullState(state.MemberGuid, memberSlots, memberAuraChanges);
            PartyAuraSlotTable.RebuildFromFull(memberSlots, memberAuraChanges);
            state.Auras = PartyAuraSlotTable.BuildAuraList(memberSlots);
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetGuid))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.NewPetGuid = packet.ReadGuid().To128(GetSession().GameState);
        }


        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetName))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.NewPetName = packet.ReadCString();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetModelId))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.DisplayID = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetCurrentHealth))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.Health = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetMaxHealth))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.MaxHealth = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetPowerType))
            packet.ReadUInt8(); // Pet Power Type

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetCurrentPower))
            packet.ReadInt16(); // Pet Current Power

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetMaxPower))
            packet.ReadInt16(); // Pet Max Power

        // JimsProxy (#513 follow-up): pet auras in a full state rebuild the pet's slot table.
        bool petAuraBitsPresent = false;
        var petAuraChanges = new List<PartyAuraSlotTable.SlotChange>();
        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetAuras))
        {
            petAuraBitsPresent = true;
            var auraMask = packet.ReadUInt32(); // Pet Positive Aura Mask: bit i = slot i
            for (int i = 0; i < PartyAuraSlotTable.PositiveSlots; ++i)
                if ((auraMask & (1u << i)) != 0)
                    petAuraChanges.Add(new PartyAuraSlotTable.SlotChange(i, packet.ReadUInt16()));
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetAurasNegative))
        {
            petAuraBitsPresent = true;
            var auraMask = packet.ReadUInt16(); // Pet Negative Aura Mask: bit j = slot 32 + j
            for (int j = 0; j < PartyAuraSlotTable.NegativeSlots; ++j)
                if ((auraMask & (1u << j)) != 0)
                    petAuraChanges.Add(new PartyAuraSlotTable.SlotChange(PartyAuraSlotTable.PositiveSlots + j, packet.ReadUInt16()));
        }

        if (petAuraBitsPresent)
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            var petSlots = GetSession().GameState.GetOrAddPartyAuraSlots(GetSession().GameState.PartyPetAuraSlots, state.MemberGuid);
            PartyAuraSlotTable.RebuildFromFull(petSlots, petAuraChanges);
            state.Pet.Auras = PartyAuraSlotTable.BuildAuraList(petSlots);
        }
        SnapshotPartyStatsFromFull(state, updateFlags.HasFlag(GroupUpdateFlagVanilla.PetGuid), updateFlags.HasFlag(GroupUpdateFlagVanilla.Level));
        CachePartyPetName(state.Pet);
        // JimsProxy (review follow-up): DebugOutput-gated with its partial-state
        // sibling above — full states are rarer but ride the same raid-scale bursts.
        if (Framework.Settings.DebugOutput)
            Log.Event("party.full_state.translated", new
            {
                member_guid = state.MemberGuid.ToString(),
                update_flags = (uint)updateFlags,
                status = (ushort)state.StatusFlags,
                level = state.Level,
                zone = state.ZoneID,
                current_health = state.CurrentHealth,
                max_health = state.MaxHealth,
                aura_count = state.Auras?.Count ?? 0,
                has_pet = state.Pet != null,
                pet_guid = state.Pet != null ? state.Pet.NewPetGuid.ToString() : null,
                pet_name = state.Pet?.NewPetName,
                pet_health = state.Pet?.Health,
            });
        SendPacketToClient(state); //MIRASU: Restore missing call — was lost during earlier brace-rearrangement edits. Without this the modern client never gets the initial full-state on party join, so members show as offline/unknown until a PARTIAL_STATE delta arrives (which may never include Status+Health together).     
    }

    [PacketHandler(Opcode.SMSG_PARTY_MEMBER_FULL_STATE, ClientVersionBuild.V2_0_1_6180)]
    void HandlePartyMemberStatsFullTBC(WorldPacket packet)
    {
        if (GetSession().GameState.CurrentMapId == (uint)BattlegroundMapID.WarsongGulch &&
           (GetSession().GameState.HasWsgAllyFlagCarrier || GetSession().GameState.HasWsgHordeFlagCarrier))
        {
            if (_requestBgPlayerPosCounter++ > 10) // don't spam every time somebody moves
            {
                WorldPacket packet2 = new WorldPacket(Opcode.MSG_BATTLEGROUND_PLAYER_POSITIONS);
                SendPacket(packet2);
                _requestBgPlayerPosCounter = 0;
            }
        }

        PartyMemberFullState state = new PartyMemberFullState();
        if (GetSession().GameState.IsInBattleground())
        {
            state.PartyType[0] = 0;
            state.PartyType[1] = 2;
        }
        else
        {
            state.PartyType[0] = 1;
            state.PartyType[1] = 0;
        }

        state.MemberGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
        var updateFlags = (GroupUpdateFlagTBC)packet.ReadUInt32();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Status))
            state.StatusFlags = (GroupMemberOnlineStatus)packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.CurrentHealth))
            state.CurrentHealth = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.MaxHealth))
            state.MaxHealth = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PowerType))
            state.PowerType = packet.ReadUInt8();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.CurrentPower))
            state.CurrentPower = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.MaxPower))
            state.MaxPower = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Level))
            state.Level = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Zone))
            state.ZoneID = packet.ReadUInt16();

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Position))
        {
            state.PositionX = packet.ReadInt16();
            state.PositionY = packet.ReadInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.Auras))
        {
            if (state.Auras == null)
                state.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt64();

            for (byte i = 0; i < LegacyVersion.GetAuraSlotsCount(); ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                packet.ReadUInt8(); // unk
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Positive;
                }
                state.Auras.Add(aura);
            }
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetGuid))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.NewPetGuid = packet.ReadGuid().To128(GetSession().GameState);
        }


        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetName))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.NewPetName = packet.ReadCString();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetModelId))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.DisplayID = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetCurrentHealth))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.Health = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetMaxHealth))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            state.Pet.MaxHealth = packet.ReadUInt16();
        }

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetPowerType))
            packet.ReadUInt8(); // Pet Power Type

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetCurrentPower))
            packet.ReadInt16(); // Pet Current Power

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetMaxPower))
            packet.ReadInt16(); // Pet Max Power

        if (updateFlags.HasFlag(GroupUpdateFlagTBC.PetAuras))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            if (state.Pet.Auras == null)
                state.Pet.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt64();

            for (byte i = 0; i < LegacyVersion.GetAuraSlotsCount(); ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                packet.ReadUInt8(); // unk
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Positive;
                }
                state.Pet.Auras.Add(aura);
            }
        }

        SnapshotPartyStatsFromFull(state, updateFlags.HasFlag(GroupUpdateFlagTBC.PetGuid), updateFlags.HasFlag(GroupUpdateFlagTBC.Level));
        CachePartyPetName(state.Pet);
        SendPacketToClient(state);
    }

    [PacketHandler(Opcode.MSG_MINIMAP_PING)]
    void HandleMinimapPing(WorldPacket packet)
    {
        MinimapPing ping = new MinimapPing();
        ping.SenderGUID = packet.ReadGuid().To128(GetSession().GameState);
        ping.Position = packet.ReadVector2();
        SendPacketToClient(ping);
    }

    [PacketHandler(Opcode.MSG_RANDOM_ROLL)]
    void HandleRandomRoll(WorldPacket packet)
    {
        RandomRoll roll = new RandomRoll();
        roll.Min = packet.ReadInt32();
        roll.Max = packet.ReadInt32();
        roll.Result = packet.ReadInt32();
        roll.Roller = packet.ReadGuid().To128(GetSession().GameState);
        roll.RollerWowAccount = GetSession().GetGameAccountGuidForPlayer(roll.Roller);
        SendPacketToClient(roll);
    }
}

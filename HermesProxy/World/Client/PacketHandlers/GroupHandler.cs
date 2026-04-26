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

        GetSession().GameState.MasterLootCandidates = null;
        GetSession().GameState.LastMasterLootSentTarget = default;
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
        GetSession().GameState.MasterLootCandidates = null;
        GetSession().GameState.LastMasterLootSentTarget = default;
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
                    state.StatusFlags = packet.ReadUInt8();// GroupMemberOnlineStatus

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

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Auras))
        {
            if (state.Auras == null)
                state.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt32(); // Positive Aura Mask

            byte maxAura = 32;

            for (byte i = 0; i < maxAura; ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Positive;
                }
                state.Auras.Add(aura);
            }
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.AurasNegative))
        {
            if (state.Auras == null)
                state.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt16(); // Negative Aura Mask

            byte maxAura = 48;

            for (byte i = 0; i < maxAura; ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Negative;
                }
                state.Auras.Add(aura);
            }
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

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetAuras))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            if (state.Pet.Auras == null)
                state.Pet.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt32(); // Pet Positive Aura Mask

            byte maxAura = 32;

            for (byte i = 0; i < maxAura; ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Positive;
                }
                state.Pet.Auras.Add(aura);
            }
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetAurasNegative))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            if (state.Pet.Auras == null)
                state.Pet.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt16(); // Pet Negative Aura Mask

            byte maxAura = 48;

            for (byte i = 0; i < maxAura; ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Negative;
                }
                    state.Pet.Auras.Add(aura);
                }
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

        SendPacketToClient(state);
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
                statusOnlyM.StatusFlags = (ushort)(GroupMemberOnlineStatus)packet.ReadUInt8(); //MIRASU
            SendPacketToClient(statusOnlyM); //MIRASU
            return; //MIRASU - skip the full-state build entirely
        } //MIRASU  
        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Status))
            state.StatusFlags = (GroupMemberOnlineStatus)packet.ReadUInt8();

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

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.Auras))
        {
            if (state.Auras == null)
                state.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt32(); // Positive Aura Mask

            byte maxAura = 32;

            for (byte i = 0; i < maxAura; ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Positive;
                }
                state.Auras.Add(aura);
            }
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.AurasNegative))
        {
            if (state.Auras == null)
                state.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt16(); // Negative Aura Mask

            byte maxAura = 48;

            for (byte i = 0; i < maxAura; ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Negative;
                }
                state.Auras.Add(aura);
            }
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

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetAuras))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            if (state.Pet.Auras == null)
                state.Pet.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt32(); // Pet Positive Aura Mask

            byte maxAura = 32;

            for (byte i = 0; i < maxAura; ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Positive;
                }
                state.Pet.Auras.Add(aura);
            }
        }

        if (updateFlags.HasFlag(GroupUpdateFlagVanilla.PetAurasNegative))
        {
            if (state.Pet == null)
                state.Pet = new PartyMemberPetStats();
            if (state.Pet.Auras == null)
                state.Pet.Auras = new List<PartyMemberAuraStates>();

            var auraMask = packet.ReadUInt16(); // Pet Negative Aura Mask

            byte maxAura = 48;

            for (byte i = 0; i < maxAura; ++i)
            {
                if ((auraMask & (1ul << i)) == 0)
                    continue;

                PartyMemberAuraStates aura = new PartyMemberAuraStates();
                aura.SpellId = packet.ReadUInt16();
                if (aura.SpellId != 0)
                {
                    aura.ActiveFlags = 1;
                    aura.AuraFlags = (ushort)AuraFlagsModern.Negative;
                }
                state.Pet.Auras.Add(aura);
            }
        }
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

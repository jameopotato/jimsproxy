using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using static HermesProxy.World.Server.Packets.PVPMatchStatisticsMessage;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_BATTLEFIELD_LIST, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    void HandleBattlefieldListVanilla(WorldPacket packet)
    {
        BattlefieldList bglist = new BattlefieldList();
        bglist.BattlemasterGuid = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = bglist.BattlemasterGuid;
        bglist.BattlemasterListID = GameData.GetBattlegroundIdFromMapId(packet.ReadUInt32());
        packet.ReadUInt8(); // bracket id
        var instancesCount = packet.ReadUInt32();
        for (var i = 0; i < instancesCount; i++)
        {
            int instanceId = packet.ReadInt32();
            bglist.BattlefieldInstances.Add(instanceId);
        }
        SendPacketToClient(bglist);
    }

    [PacketHandler(Opcode.SMSG_BATTLEFIELD_LIST, ClientVersionBuild.V2_0_1_6180, ClientVersionBuild.V3_0_2_9056)]
    void HandleBattlefieldListTBC(WorldPacket packet)
    {
        BattlefieldList bglist = new BattlefieldList();
        bglist.BattlemasterGuid = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = bglist.BattlemasterGuid;
        bglist.BattlemasterListID = packet.ReadUInt32();
        packet.ReadUInt8(); // bracket id
        var instancesCount = packet.ReadUInt32();
        for (var i = 0; i < instancesCount; i++)
        {
            int instanceId = packet.ReadInt32();
            bglist.BattlefieldInstances.Add(instanceId);
        }
        SendPacketToClient(bglist);
    }

    [PacketHandler(Opcode.SMSG_BATTLEFIELD_LIST, ClientVersionBuild.V3_0_2_9056)]
    void HandleBattlefieldListWotLK(WorldPacket packet)
    {
        BattlefieldList bglist = new BattlefieldList();
        bglist.BattlemasterGuid = packet.ReadGuid().To128(GetSession().GameState);
        GetSession().GameState.CurrentInteractedWithNPC = bglist.BattlemasterGuid;
        bglist.PvpAnywhere = packet.ReadBool(); // from UI
        bglist.BattlemasterListID = packet.ReadUInt32();
        bglist.MinLevel = packet.ReadUInt8();
        bglist.MaxLevel = packet.ReadUInt8();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_3_11685))
        {
            packet.ReadBool(); // Has Win
            packet.ReadInt32(); // Winner Honor Reward
            packet.ReadInt32(); // Winner Arena Reward
            packet.ReadInt32(); // Loser Honor Reward

            if (packet.ReadBool()) // Is random
            {
                bglist.HasRandomWinToday = packet.ReadBool();
                packet.ReadInt32(); // Random Winner Honor Reward
                packet.ReadInt32(); // Random Winner Arena Reward
                packet.ReadInt32(); // Random Loser Honor Reward
            }
        }
        var instancesCount = packet.ReadUInt32();
        for (var i = 0; i < instancesCount; i++)
        {
            int instanceId = packet.ReadInt32();
            bglist.BattlefieldInstances.Add(instanceId);
        }
        SendPacketToClient(bglist);
    }
    
    [PacketHandler(Opcode.SMSG_BATTLEFIELD_STATUS, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    void HandleBattlefieldStatusVanilla(WorldPacket packet)
    {
        BattlefieldStatusHeader hdr = new BattlefieldStatusHeader();
        hdr.Ticket.Id = 1 + packet.ReadUInt32(); // Queue Slot
        hdr.Ticket.RequesterGuid = GetSession().GameState.CurrentPlayerGuid;
        hdr.Ticket.Time = GetSession().GameState.GetBattleFieldQueueTime(hdr.Ticket.Id);
        hdr.Ticket.Type = RideType.Battlegrounds;

        uint mapId = packet.ReadUInt32();
        if (mapId != 0)
        {
            uint battlefieldListId = GameData.GetBattlegroundIdFromMapId(mapId);
            hdr.BattlefieldListIDs.Add(battlefieldListId);
            packet.ReadUInt8(); // bracket id
            hdr.InstanceID = packet.ReadUInt32();
            BattleGroundStatus status = (BattleGroundStatus)packet.ReadUInt32();
            switch (status)
            {
                case BattleGroundStatus.WaitQueue:
                {
                    BattlefieldStatusQueued queue = new BattlefieldStatusQueued();
                    queue.Hdr = hdr;
                    queue.AverageWaitTime = packet.ReadUInt32();
                    queue.WaitTime = packet.ReadUInt32();
                    SendPacketToClient(queue);
                    break;
                }
                case BattleGroundStatus.WaitJoin:
                {
                    BattlefieldStatusNeedConfirmation confirm = new BattlefieldStatusNeedConfirmation();
                    confirm.Hdr = hdr;
                    confirm.Mapid = mapId;
                    confirm.Timeout = packet.ReadUInt32();
                    SendPacketToClient(confirm);
                    break;
                }
                case BattleGroundStatus.InProgress:
                {
                    BattlefieldStatusActive active = new BattlefieldStatusActive();
                    active.Hdr = hdr;
                    active.Mapid = mapId;
                    active.ShutdownTimer = packet.ReadUInt32();
                    active.StartTimer = packet.ReadUInt32();
                    if (active.ShutdownTimer == 0)
                    {
                        BattlegroundInit init = new BattlegroundInit();
                        init.Milliseconds = 1154756799;
                        SendPacketToClient(init);
                    }
                    SendPacketToClient(active);
                    break;
                }
                default:
                {
                    Log.Print(LogType.Error, $"Unexpected BG status {status}.");
                    break;
                }
            }
        }
        else
        {
            uint queuedMapId = GetSession().GameState.GetBattleFieldQueueType(hdr.Ticket.Id);
            // JimsProxy (#504): after a disconnect and rejoin inside a battleground the queue slot
            // has never been seen (nothing was stored since the relog), so no stored value can
            // match the current map. The raid was filed in slot 1 by the map fallback in that
            // state, so clear it on the same evidence: a never-seen slot ending while the character
            // is still on the battleground map. A slot queued after the relog stores its map at
            // queue time, and a slot that already ended stores 0; neither is mistaken for this one.
            // Known residual: an invite for ANOTHER battleground that was pending at the disconnect
            // and expires afterwards ends a never-seen slot too and would clear the raid early.
            bool unknownSlotEndingInBattleground = !GetSession().GameState.HasBattleFieldQueueType(hdr.Ticket.Id) &&
                GetSession().GameState.CurrentGroups[1] != null &&
                GetSession().GameState.IsInBattleground();
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180) &&
                (queuedMapId == GetSession().GameState.CurrentMapId || unknownSlotEndingInBattleground))
            {
                // Clear BG group properly on vanilla servers.
                var bgGroup = GetSession().GameState.CurrentGroups[1];
                if (bgGroup != null)
                {
                    PartyUpdate party = new PartyUpdate();
                    party.SequenceNum = GetSession().GameState.GroupUpdateCounter++;
                    party.PartyFlags = GroupFlags.FakeRaid | GroupFlags.Destroyed;
                    party.PartyIndex = 1;
                    party.PartyGUID = bgGroup.PartyGUID;
                    party.LeaderGUID = WowGuid128.Empty;
                    party.MyIndex = -1;
                    GetSession().GameState.CurrentGroups[1] = null;
                    SendPacketToClient(party);
                }
            }

            BattlefieldStatusFailed failed = new BattlefieldStatusFailed();
            failed.Ticket = hdr.Ticket;
            failed.Reason = 30;
            failed.BattlefieldListId = GameData.GetBattlegroundIdFromMapId(queuedMapId);
            SendPacketToClient(failed);
            GetSession().GameState.BattleFieldQueueTimes.Remove(hdr.Ticket.Id);
        }
        GetSession().GameState.StoreBattleFieldQueueType(hdr.Ticket.Id, mapId);

        // JimsProxy (#504): a slot ending while the character still stands on a battleground map
        // opens the exit window: the server restores the home group before the teleport out, and
        // that group list must not be filed as the battleground raid by the map fallback. Any
        // status that names a map (a new queue, a re-entry) closes the window again.
        if (mapId == 0)
        {
            uint? currentMap = GetSession().GameState.CurrentMapId;
            if (currentMap != null && GameData.GetBattlegroundIdFromMapId(currentMap.Value) != 0)
                GetSession().GameState.MarkBattlegroundExited(currentMap.Value);
        }
        else
            GetSession().GameState.ClearBattlegroundExited();
    }

    [PacketHandler(Opcode.SMSG_BATTLEFIELD_STATUS, ClientVersionBuild.V2_0_1_6180)]
    void HandleBattlefieldStatusTBC(WorldPacket packet)
    {
        BattlefieldStatusHeader hdr = new BattlefieldStatusHeader();
        hdr.Ticket.Id = 1 + packet.ReadUInt32(); // Queue Slot
        hdr.Ticket.RequesterGuid = GetSession().GameState.CurrentPlayerGuid;
        hdr.Ticket.Time = GetSession().GameState.GetBattleFieldQueueTime(hdr.Ticket.Id);
        hdr.Ticket.Type = RideType.Battlegrounds;

        hdr.ArenaTeamSize = packet.ReadUInt8();
        packet.ReadUInt8(); // unk
        uint battlefieldListId = packet.ReadUInt32();
        packet.ReadUInt16(); // 0x1F90

        if (battlefieldListId != 0)
        {
            hdr.BattlefieldListIDs.Add(battlefieldListId);

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_3_11685))
            {
                hdr.RangeMin = packet.ReadUInt8();
                hdr.RangeMax = packet.ReadUInt8();
            }

            hdr.InstanceID = packet.ReadUInt32();
            hdr.IsArena = packet.ReadBool();
            BattleGroundStatus status = (BattleGroundStatus)packet.ReadUInt32();
            switch (status)
            {
                case BattleGroundStatus.WaitQueue:
                {
                    BattlefieldStatusQueued queue = new BattlefieldStatusQueued();
                    queue.Hdr = hdr;
                    queue.AverageWaitTime = packet.ReadUInt32();
                    queue.WaitTime = packet.ReadUInt32();
                    SendPacketToClient(queue);
                    break;
                }
                case BattleGroundStatus.WaitJoin:
                {
                    BattlefieldStatusNeedConfirmation confirm = new BattlefieldStatusNeedConfirmation();
                    confirm.Hdr = hdr;
                    confirm.Mapid = packet.ReadUInt32();
                    if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_5_12213))
                        packet.ReadUInt64(); // unk
                    confirm.Timeout = packet.ReadUInt32();
                    SendPacketToClient(confirm);
                    break;
                }
                case BattleGroundStatus.InProgress:
                {
                    BattlefieldStatusActive active = new BattlefieldStatusActive();
                    active.Hdr = hdr;
                    active.Mapid = packet.ReadUInt32();
                    if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_5_12213))
                        packet.ReadUInt64(); // unk
                    active.ShutdownTimer = packet.ReadUInt32();
                    active.StartTimer = packet.ReadUInt32();
                    active.ArenaFaction = packet.ReadUInt8();
                    if (active.ShutdownTimer == 0)
                    {
                        BattlegroundInit init = new BattlegroundInit();
                        init.Milliseconds = 1154756799;
                        SendPacketToClient(init);
                    }
                    SendPacketToClient(active);
                    break;
                }
                default:
                {
                    Log.Print(LogType.Error, $"Unexpected BG status {status}.");
                    break;
                }
            }
        }
        else
        {
            BattlefieldStatusFailed failed = new BattlefieldStatusFailed();
            failed.Ticket = hdr.Ticket;
            failed.Reason = 30;
            failed.BattlefieldListId = GetSession().GameState.GetBattleFieldQueueType(hdr.Ticket.Id);
            SendPacketToClient(failed);
            GetSession().GameState.BattleFieldQueueTimes.Remove(hdr.Ticket.Id);
        }
        GetSession().GameState.StoreBattleFieldQueueType(hdr.Ticket.Id, battlefieldListId);
    }

    [PacketHandler(Opcode.MSG_PVP_LOG_DATA, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    void HandlePvPLogDataVanilla(WorldPacket packet)
    {
        PVPMatchStatisticsMessage pvp = new PVPMatchStatisticsMessage();
        if (packet.ReadBool()) // Has Winner
            pvp.Winner = packet.ReadUInt8();

        // JimsProxy (#505): rows the player cache cannot resolve are placed by raid membership.
        bool? localIsAlliance = GetScoreboardLocalFaction();
        int placeholdersLogged = 0;

        int count = packet.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            PVPMatchPlayerStatistics player = new PVPMatchPlayerStatistics();
            player.PlayerGUID = packet.ReadGuid().To128(GetSession().GameState);
            player.Rank = packet.ReadInt32();
            player.Kills = packet.ReadUInt32();
            player.Honor = new();
            player.Honor.HonorKills = packet.ReadUInt32();
            player.Honor.Deaths = packet.ReadUInt32();
            player.Honor.ContributionPoints = packet.ReadUInt32();

            int statsCount = packet.ReadInt32();
            for (int j = 0; j < statsCount; j++)
                player.Stats.Add(packet.ReadUInt32());

            // JimsProxy (#505): every row is still read off the wire; none past the 80-row bound is sent.
            if (pvp.Statistics.Count >= ScoreboardRowResolver.MaxRows)
                continue;

            ApplyBattlegroundScoreboardRow(pvp, player, localIsAlliance, ref placeholdersLogged);
            pvp.Statistics.Add(player);
        }
        LogScoreboardRowsCapped(count, pvp.Statistics.Count);
        SendPacketToClient(pvp);
    }

    [PacketHandler(Opcode.MSG_PVP_LOG_DATA, ClientVersionBuild.V2_0_1_6180)]
    void HandlePvPLogDataTBC(WorldPacket packet)
    {
        PVPMatchStatisticsMessage pvp = new PVPMatchStatisticsMessage();
        if (packet.ReadBool()) // Has Arena Teams
        {
            pvp.ArenaTeams = new ArenaTeamsInfo();
            pvp.ArenaTeams.Guids[0] = WowGuid128.Empty;
            pvp.ArenaTeams.Guids[1] = WowGuid128.Empty;

            for (int i = 0; i < 2; i++)
            {
                packet.ReadUInt32(); // Rating Lost
                packet.ReadUInt32(); // Rating gained
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                    packet.ReadUInt32(); // MMR
            }

            for (int i = 0; i < 2; i++)
            {
                pvp.ArenaTeams.Names[i] = packet.ReadCString();
            }
        }

        if (packet.ReadBool()) // Has Winner
            pvp.Winner = packet.ReadUInt8();

        // JimsProxy (#505): arena rows take their faction from the wire, so only a battleground
        // board needs the local side to place rows the player cache cannot resolve.
        bool isArena = pvp.ArenaTeams != null;
        bool? localIsAlliance = isArena ? null : GetScoreboardLocalFaction();
        int placeholdersLogged = 0;

        int count = packet.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            PVPMatchPlayerStatistics player = new PVPMatchPlayerStatistics();
            player.PlayerGUID = packet.ReadGuid().To128(GetSession().GameState);
            player.Kills = packet.ReadUInt32();

            if (!isArena)
            {
                player.Honor = new();
                player.Honor.HonorKills = packet.ReadUInt32();
                player.Honor.Deaths = packet.ReadUInt32();
                player.Honor.ContributionPoints = packet.ReadUInt32();
            }
            else
            {
                player.Faction = packet.ReadBool();
                pvp.PlayerCount[player.Faction ? 1 : 0]++;
            }

            player.DamageDone = packet.ReadUInt32();
            player.HealingDone = packet.ReadUInt32();

            int statsCount = packet.ReadInt32();
            for (int j = 0; j < statsCount; j++)
                player.Stats.Add(packet.ReadUInt32());

            // JimsProxy (#505): every row is still read off the wire; none past the 80-row bound is sent.
            if (pvp.Statistics.Count >= ScoreboardRowResolver.MaxRows)
                continue;

            if (isArena)
            {
                // JimsProxy (#505): the wire faction and count above stand; a miss keeps the old placeholder.
                GetSession().GameState.CachedPlayers.TryGetValue(player.PlayerGUID, out var cache);
                var row = ScoreboardRowResolver.ResolveUnplaced(cache);
                player.Sex = row.Sex;
                player.PlayerRace = row.Race;
                player.PlayerClass = row.Class;
            }
            else
                ApplyBattlegroundScoreboardRow(pvp, player, localIsAlliance, ref placeholdersLogged);
            pvp.Statistics.Add(player);
        }
        LogScoreboardRowsCapped(count, pvp.Statistics.Count);
        SendPacketToClient(pvp);
    }

    // JimsProxy (#505): the local player's faction, read once per battleground board. Trusted only
    // when the player cache holds the local character's race, because IsAlliancePlayer reads a
    // missing or race-less entry as Horde. Null means unknown: rows the cache cannot resolve then
    // keep the pre-#505 placeholder (Faction false, Male Human).
    bool _scoreboardLocalFactionUnknownLogged; // JimsProxy (#505): log the unknown local faction once per session

    bool? GetScoreboardLocalFaction()
    {
        var gameState = GetSession().GameState;
        var self = gameState.CurrentPlayerGuid;
        if (gameState.CachedPlayers.TryGetValue(self, out var cache) && cache.RaceId != Race.None)
            return gameState.IsAlliancePlayer(self);

        if (!_scoreboardLocalFactionUnknownLogged)
        {
            _scoreboardLocalFactionUnknownLogged = true;
            Log.Event("battleground.scoreboard.local_faction_unknown", new
            {
                player_low = self.GetCounter(),
                cached = cache != null,
            });
        }
        return null;
    }

    // JimsProxy (#505): fills Sex/Race/Class/Faction of one battleground (not arena) row and counts
    // it in PlayerCount, which the stock frame shows next to each faction tab (the vanilla board
    // used to leave both counts at 0).
    void ApplyBattlegroundScoreboardRow(PVPMatchStatisticsMessage pvp, PVPMatchPlayerStatistics player, bool? localIsAlliance, ref int placeholdersLogged)
    {
        var gameState = GetSession().GameState;
        gameState.CachedPlayers.TryGetValue(player.PlayerGUID, out var cache);

        // Raid membership only matters for a row the cache cannot resolve.
        bool ownTeam = !ScoreboardRowResolver.HasRace(cache)
            && ScoreboardRowResolver.IsOwnTeam(player.PlayerGUID, gameState.CurrentPlayerGuid, gameState.CurrentGroups);
        var row = localIsAlliance is bool local
            ? ScoreboardRowResolver.Resolve(cache, ownTeam, local)
            : ScoreboardRowResolver.ResolveUnplaced(cache);

        player.Sex = row.Sex;
        player.PlayerRace = row.Race;
        player.PlayerClass = row.Class;
        player.Faction = row.Faction;
        ScoreboardRowResolver.CountByFaction(pvp.PlayerCount, row.Faction);

        if (!row.FromCache && placeholdersLogged < ScoreboardRowResolver.MaxPlaceholderLogsPerBoard)
        {
            placeholdersLogged++;
            Log.Event("battleground.scoreboard.row_placeholder", new
            {
                player_low = player.PlayerGUID.GetCounter(),
                own_team = ownTeam,
                faction_alliance = row.Faction,
            });
        }
    }

    // JimsProxy (#505): once per board, when the wire carried more rows than the 80-row bound.
    static void LogScoreboardRowsCapped(int rowsOnWire, int rowsSent)
    {
        if (rowsOnWire <= ScoreboardRowResolver.MaxRows)
            return;

        Log.Event("battleground.scoreboard.rows_capped", new
        {
            rows_on_wire = rowsOnWire,
            rows_sent = rowsSent,
        });
    }

    BattlegroundPlayerPosition ReadBattlegroundPlayerPosition(WorldPacket packet)
    {
        BattlegroundPlayerPosition position = new BattlegroundPlayerPosition();
        position.Guid = packet.ReadGuid().To128(GetSession().GameState);
        position.Pos = packet.ReadVector2();
        return position;
    }

    [PacketHandler(Opcode.MSG_BATTLEGROUND_PLAYER_POSITIONS, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    void HandleBattlegroundPlayerPositionsVanilla(WorldPacket packet)
    {
        GetSession().GameState.FlagCarrierGuids.Clear();
        BattlegroundPlayerPositions bglist = new BattlegroundPlayerPositions();
        uint teamMembersCount = packet.ReadUInt32();
        for (uint i = 0; i < teamMembersCount; i++)
        {
            ReadBattlegroundPlayerPosition(packet);
        }

        bool hasFlagCarrier = packet.ReadBool();
        if (hasFlagCarrier)
        {
            var position = ReadBattlegroundPlayerPosition(packet);

            if (GetSession().GameState.IsAlliancePlayer(position.Guid))
            {
                position.IconID = 1;
                position.ArenaSlot = 3;
            }
            else
            {
                position.IconID = 2;
                position.ArenaSlot = 2;
            }

            bglist.FlagCarriers.Add(position);
            GetSession().GameState.FlagCarrierGuids.Add(position.Guid);
        }
        SendPacketToClient(bglist);
    }

    [PacketHandler(Opcode.MSG_BATTLEGROUND_PLAYER_POSITIONS, ClientVersionBuild.V2_0_1_6180)]
    void HandleBattlegroundPlayerPositionsTBC(WorldPacket packet)
    {
        BattlegroundPlayerPositions bglist = new BattlegroundPlayerPositions();
        uint teamMembersCount = packet.ReadUInt32();
        uint flagCarriersCount = packet.ReadUInt32();
        for (uint i = 0; i < teamMembersCount; i++)
        {
            ReadBattlegroundPlayerPosition(packet);
        }
        GetSession().GameState.FlagCarrierGuids.Clear();
        for (uint i = 0; i < flagCarriersCount; i++)
        {
            var position = ReadBattlegroundPlayerPosition(packet);

            if (GetSession().GameState.IsAlliancePlayer(position.Guid))
            {
                position.IconID = 1;
                position.ArenaSlot = 3;
            }
            else
            {
                position.IconID = 2;
                position.ArenaSlot = 2;
            }

            bglist.FlagCarriers.Add(position);
            GetSession().GameState.FlagCarrierGuids.Add(position.Guid);
        }
        SendPacketToClient(bglist);
    }

    [PacketHandler(Opcode.SMSG_BATTLEGROUND_PLAYER_JOINED)]
    [PacketHandler(Opcode.SMSG_BATTLEGROUND_PLAYER_LEFT)]
    void HandleBattlegroundPlayerLeftOrJoined(WorldPacket packet)
    {
        BattlegroundPlayerLeftOrJoined player = new BattlegroundPlayerLeftOrJoined(packet.GetUniversalOpcode(false));
        player.Guid = packet.ReadGuid().To128(GetSession().GameState);
        SendPacketToClient(player);
    }

    [PacketHandler(Opcode.SMSG_AREA_SPIRIT_HEALER_TIME)]
    void HandleAreaSpiritHealerTime(WorldPacket packet)
    {
        AreaSpiritHealerTime healer = new AreaSpiritHealerTime();
        healer.HealerGuid = packet.ReadGuid().To128(GetSession().GameState);
        healer.TimeLeft = packet.ReadUInt32();
        SendPacketToClient(healer);
    }

    [PacketHandler(Opcode.SMSG_PVP_CREDIT)]
    void HandlePvPCredit(WorldPacket packet)
    {
        PvPCredit credit = new PvPCredit();
        credit.OriginalHonor = packet.ReadInt32();
        credit.Target = packet.ReadGuid().To128(GetSession().GameState);
        credit.Rank = packet.ReadUInt32();
        SendPacketToClient(credit);
    }

    [PacketHandler(Opcode.SMSG_PLAYER_SKINNED)]
    void HandlePlayerSkinned(WorldPacket packet)
    {
        PlayerSkinned skinned = new PlayerSkinned();
        if (packet.CanRead())
            skinned.FreeRepop = packet.ReadBool();
        SendPacketToClient(skinned);
    }
}

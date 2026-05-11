using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_CHAT_JOIN_CHANNEL)]
    void HandleChatJoinChannel(JoinChannel join)
    {
        if (GetSession().WorldClient != null)
            GetSession().WorldClient!.SendChatJoinChannel(join.ChatChannelId, join.ChannelName, join.Password);
    }

    [PacketHandler(Opcode.CMSG_CHAT_LEAVE_CHANNEL)]
    void HandleChatLeaveChannel(LeaveChannel leave)
    {
        if (GetSession().WorldClient != null)
        {
            GetSession().GameState.LeftChannelName = leave.ChannelName;
            GetSession().WorldClient!.SendChatLeaveChannel(leave.ZoneChannelID, leave.ChannelName);
        }
    }

    [PacketHandler(Opcode.CMSG_CHAT_CHANNEL_OWNER)]
    [PacketHandler(Opcode.CMSG_CHAT_CHANNEL_ANNOUNCEMENTS)]

    void HandleChatChannelCommand(ChannelCommand command)
    {
        WorldPacket packet = new WorldPacket(command.GetUniversalOpcode());
        packet.WriteCString(command.ChannelName);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_CHAT_CHANNEL_LIST)]
    void HandleChatChannelList(ChannelCommand command)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CHAT_CHANNEL_LIST);
        packet.WriteCString(command.ChannelName);
        SendPacketToServer(packet);
        GetSession().GameState.ChannelDisplayList = false;
    }

    [PacketHandler(Opcode.CMSG_CHAT_CHANNEL_DISPLAY_LIST)]
    void HandleChatChannelDisplayList(ChannelCommand command)
    {
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_CHAT_CHANNEL_LIST);
            packet.WriteCString(command.ChannelName);
            SendPacketToServer(packet);
        }
        else
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_CHAT_CHANNEL_DISPLAY_LIST);
            packet.WriteCString(command.ChannelName);
            SendPacketToServer(packet);
        }
        GetSession().GameState.ChannelDisplayList = true;
    }

    [PacketHandler(Opcode.CMSG_CHAT_CHANNEL_DECLINE_INVITE)]
    void HandleChatChannelDeclineInvite(ChannelCommand command)
    {
        if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            return;

        WorldPacket packet = new WorldPacket(Opcode.CMSG_CHAT_CHANNEL_DECLINE_INVITE);
        packet.WriteCString(command.ChannelName);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_AFK)]
    void HandleChatMessageAFK(ChatMessageAFK afk)
    {
        var toBeSentTextParts = ConvertTextMessageIntoMaxLengthParts(afk.Text);
        if (toBeSentTextParts.Count < 1)
            return;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            GetSession().WorldClient!.SendMessageChatWotLK(ChatMessageTypeWotLK.Afk, 0, toBeSentTextParts[0], "", "");
        else
            GetSession().WorldClient!.SendMessageChatVanilla(ChatMessageTypeVanilla.Afk, 0, toBeSentTextParts[0], "", "");
    }

    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_DND)]
    void HandleChatMessageDND(ChatMessageDND dnd)
    {
        var toBeSentTextParts = ConvertTextMessageIntoMaxLengthParts(dnd.Text);
        if (toBeSentTextParts.Count < 1)
            return;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            GetSession().WorldClient!.SendMessageChatWotLK(ChatMessageTypeWotLK.Dnd, 0, toBeSentTextParts[0], "", "");
        else
            GetSession().WorldClient!.SendMessageChatVanilla(ChatMessageTypeVanilla.Dnd, 0, toBeSentTextParts[0], "", "");
    }

    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_CHANNEL)]
    void HandleChatMessageChannel(ChatMessageChannel channel)
    {
        var toBeSentTextParts = ConvertTextMessageIntoMaxLengthParts(channel.Text);
        foreach (string text in toBeSentTextParts)
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                GetSession().WorldClient!.SendMessageChatWotLK(ChatMessageTypeWotLK.Channel, channel.Language, text, channel.Target, "");
            else
                GetSession().WorldClient!.SendMessageChatVanilla(ChatMessageTypeVanilla.Channel, channel.Language, text, channel.Target, "");
        }
    }

    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_WHISPER)]
    void HandleChatMessageWhisper(ChatMessageWhisper whisper)
    {
        var toBeSentTextParts = ConvertTextMessageIntoMaxLengthParts(whisper.Text);
        foreach (string text in toBeSentTextParts)
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                GetSession().WorldClient!.SendMessageChatWotLK(ChatMessageTypeWotLK.Whisper, whisper.Language, text, "", whisper.Target);
            else
                GetSession().WorldClient!.SendMessageChatVanilla(ChatMessageTypeVanilla.Whisper, whisper.Language, text, "", whisper.Target);
        }
    }

    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_EMOTE)]
    void HandleChatMessageEmote(ChatMessageEmote emote)
    {
        var toBeSentTextParts = ConvertTextMessageIntoMaxLengthParts(emote.Text);
        if (toBeSentTextParts.Count < 1)
            return;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            GetSession().WorldClient!.SendMessageChatWotLK(ChatMessageTypeWotLK.Emote, 0, toBeSentTextParts[0], "", "");
        else
            GetSession().WorldClient!.SendMessageChatVanilla(ChatMessageTypeVanilla.Emote, 0, toBeSentTextParts[0], "", "");
    }

    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_GUILD)]
    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_OFFICER)]
    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_PARTY)]
    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_RAID)]
    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_RAID_WARNING)]
    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_SAY)]
    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_YELL)]
    [PacketHandler(Opcode.CMSG_CHAT_MESSAGE_INSTANCE_CHAT)]
    void HandleChatMessage(ChatMessage packet)
    {
        // JimsProxy threat-translation Phase 1 smoke test. The 1.12 vanilla server
        // does not broadcast threat data; we plan to calculate threat client-side
        // (port of LibThreatClassic2) and synthesize SMSG_THREAT_UPDATE for the
        // modern client. Before committing to the full engine port, prove the
        // client accepts synthesized threat opcodes by firing a hardcoded threat
        // value when the player /says one of these debug commands:
        //   "jpthreattest"  -> emit SMSG_THREAT_UPDATE on current target
        //   "jpthreatclear" -> emit SMSG_THREAT_CLEAR  on current target
        // Doesn't forward the chat to the legacy server. Drop these handlers
        // (and the SMSG packet writers stay) once the engine port lands.
        string chatText = packet.Text ?? string.Empty;
        string trimmed = chatText.Trim();
        if (trimmed.Equals("jpthreattest", StringComparison.OrdinalIgnoreCase))
        {
            FireThreatSmokeTest(clear: false);
            return;
        }
        if (trimmed.Equals("jpthreatclear", StringComparison.OrdinalIgnoreCase))
        {
            FireThreatSmokeTest(clear: true);
            return;
        }

        ChatMessageTypeModern type;

        switch (packet.GetUniversalOpcode())
        {
            case Opcode.CMSG_CHAT_MESSAGE_SAY:
                type = ChatMessageTypeModern.Say;
                break;
            case Opcode.CMSG_CHAT_MESSAGE_YELL:
                type = ChatMessageTypeModern.Yell;
                break;
            case Opcode.CMSG_CHAT_MESSAGE_GUILD:
                type = ChatMessageTypeModern.Guild;
                break;
            case Opcode.CMSG_CHAT_MESSAGE_OFFICER:
                type = ChatMessageTypeModern.Officer;
                break;
            case Opcode.CMSG_CHAT_MESSAGE_PARTY:
                type = ChatMessageTypeModern.Party;
                break;
            case Opcode.CMSG_CHAT_MESSAGE_RAID:
                type = ChatMessageTypeModern.Raid;
                break;
            case Opcode.CMSG_CHAT_MESSAGE_RAID_WARNING:
                type = ChatMessageTypeModern.RaidWarning;
                break;
            case Opcode.CMSG_CHAT_MESSAGE_INSTANCE_CHAT:
                if (GetSession().GameState.IsInBattleground())
                    type = ChatMessageTypeModern.Battleground;
                else
                    type = ChatMessageTypeModern.Party;
                break;
            default:
                Log.Print(LogType.Error, $"HandleMessagechatOpcode : Unknown chat opcode ({packet.GetOpcode()})");
                return;
        }

        var toBeSentTextParts = ConvertTextMessageIntoMaxLengthParts(chatText);
        foreach (string text in toBeSentTextParts)
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                ChatMessageTypeWotLK chatMsg = type.CastEnum<ChatMessageTypeWotLK>();
                GetSession().WorldClient!.SendMessageChatWotLK(chatMsg, packet.Language, text, "", "");
            }
            else
            {
                ChatMessageTypeVanilla chatMsg = type.CastEnum<ChatMessageTypeVanilla>();
                GetSession().WorldClient!.SendMessageChatVanilla(chatMsg, packet.Language, text, "", "");
            }
        }
    }

    [PacketHandler(Opcode.CMSG_CHAT_ADDON_MESSAGE)]
    void HandleAddonMessage(ChatAddonMessage packet)
    {
        if (packet.Params.Prefix == "JP")
        {
            HandleJimsPlusSideband(packet.Params.Text);
            return;
        }

        uint language = (uint)Language.Addon;
        string body = AddonInteropTranslator.TranslateOutbound(packet.Params.Prefix, packet.Params.Text);
        if (string.IsNullOrEmpty(body)) return;
        string text = packet.Params.Prefix + '\t' + body;

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            ChatMessageTypeWotLK chatMsg = packet.Params.Type.CastEnum<ChatMessageTypeWotLK>();
            GetSession().WorldClient!.SendMessageChatWotLK(chatMsg, language, text, "", "");
        }
        else
        {
            ChatMessageTypeVanilla chatMsg = packet.Params.Type.CastEnum<ChatMessageTypeVanilla>();
            GetSession().WorldClient!.SendMessageChatVanilla(chatMsg, language, text, "", "");
        }
    }

    [PacketHandler(Opcode.CMSG_CHAT_ADDON_MESSAGE_TARGETED)]
    void HandleAddonMessageTargeted(ChatAddonMessageTargeted packet)
    {
        if (packet.Params.Prefix == "JP")
        {
            HandleJimsPlusSideband(packet.Params.Text);
            return;
        }

        uint language = (uint)Language.Addon;
        string body = AddonInteropTranslator.TranslateOutbound(packet.Params.Prefix, packet.Params.Text);
        if (string.IsNullOrEmpty(body)) return;
        string text = packet.Params.Prefix + '\t' + body;
        string channelName = packet.ChannelGuid.IsEmpty() ? "" :
            GetSession().GameState.GetChannelName((int)packet.ChannelGuid.GetCounter());

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            ChatMessageTypeWotLK chatMsg = packet.Params.Type.CastEnum<ChatMessageTypeWotLK>();
            GetSession().WorldClient!.SendMessageChatWotLK(chatMsg, language, text, channelName, packet.Target);
        }
        else
        {
            ChatMessageTypeVanilla chatMsg = packet.Params.Type.CastEnum<ChatMessageTypeVanilla>();
            GetSession().WorldClient!.SendMessageChatVanilla(chatMsg, language, text, channelName, packet.Target);
        }
    }

    [PacketHandler(Opcode.CMSG_SEND_TEXT_EMOTE)]
    void HandleSendTextEmote(CTextEmote emote)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SEND_TEXT_EMOTE);
        packet.WriteInt32(emote.EmoteID);
        packet.WriteInt32(emote.SoundIndex);
        packet.WriteGuid(emote.Target.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_CHAT_REGISTER_ADDON_PREFIXES)]
    void HandleChatRegisterAddonPrefixes(ChatRegisterAddonPrefixes addons)
    {
        foreach (var prefix in addons.Prefixes)
            GetSession().GameState.AddonPrefixes.Add(prefix);
    }

    [PacketHandler(Opcode.CMSG_CHAT_UNREGISTER_ALL_ADDON_PREFIXES)]
    void HandleChatUnregisterAllAddonPrefixes(EmptyClientPacket addons)
    {
        GetSession().GameState.AddonPrefixes.Clear();
    }

    private static List<string> ConvertTextMessageIntoMaxLengthParts(string originalTextMessage)
    {
        List<string> toBeSendTextParts = new List<string>();
        const int maxAllowedTextLength = 255;
        if (originalTextMessage.Length <= maxAllowedTextLength)
        {
            // We fit in a single packet
            toBeSendTextParts.Add(originalTextMessage);
        }
        else
        {
            // We must split the text into chunks of max length 255
            // Since we dont want to break item links, we first split the text by links
            var linkBegin = @"(?=\|c[a-f0-9]{8}\|H)";
            var linkEnd = @"(?<=\|h\|r)";
            var splitted = Regex.Split(originalTextMessage, $"{linkBegin}|{linkEnd}");
            var splittedAndSlicedToMaxLength = splitted.SelectMany(x => x.Chunk(maxAllowedTextLength));

            var strBuilder = new StringBuilder();
            foreach (var part in splittedAndSlicedToMaxLength)
            {
                if ((strBuilder.Length + part.Length) > maxAllowedTextLength)
                { // Flush now
                    toBeSendTextParts.Add(strBuilder.ToString());
                    strBuilder.Clear();
                }
                strBuilder.Append(part);
            }

            // Flush last part of the message
            toBeSendTextParts.Add(strBuilder.ToString());
        }

        return toBeSendTextParts;
    }

    private void FireThreatSmokeTest(bool clear)
    {
        var session = GetSession();
        if (session?.WorldClient == null)
        {
            Log.Print(LogType.Warn, "jpthreat smoke test: no WorldClient yet");
            return;
        }

        var gameState = session.GameState;
        var playerGuid = gameState.CurrentPlayerGuid;
        if (playerGuid.GetCounter() == 0)
        {
            SendDebugChat("jpthreat: no player GUID yet");
            return;
        }

        // Resolve the player's current target via UNIT_FIELD_TARGET on the cached
        // legacy fields. This is the entity the WoW client considers "target" — the
        // mob the player has selected and is hopefully attacking.
        var fields = gameState.GetCachedObjectFieldsLegacy(playerGuid);
        if (fields == null)
        {
            SendDebugChat("jpthreat: no cached fields for player");
            return;
        }

        int targetFieldIdx = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_TARGET);
        if (targetFieldIdx < 0)
        {
            SendDebugChat("jpthreat: UNIT_FIELD_TARGET not mapped");
            return;
        }

        WowGuid64 targetGuid64 = fields.GetGuidValue(targetFieldIdx);
        if (targetGuid64 == WowGuid64.Empty)
        {
            SendDebugChat("jpthreat: no target selected");
            return;
        }

        WowGuid128 targetGuid128 = targetGuid64.To128(gameState);

        Framework.Logging.Log.Event("threat.smoke_test", new
        {
            mode = clear ? "clear" : "update",
            player_guid = playerGuid.ToString(),
            target_guid = targetGuid128.ToString(),
        });

        if (clear)
        {
            var pkt = new ThreatClearPkt
            {
                UnitGUID = targetGuid128,
            };
            session.WorldClient.SendPacketToClient(pkt);
            SendDebugChat($"jpthreat: sent SMSG_THREAT_CLEAR for {targetGuid128}");
            return;
        }

        // Synthesize a threat list with just the player at 12345 raw threat.
        // Modern wire format packs threat × 100, so emitting 1234500 should
        // surface as 12345 in the addons that divide by 100. Useful sanity
        // signal: the number 12345 is recognizable in TinyThreat / Skada.
        var update = new ThreatUpdatePkt
        {
            UnitGUID = targetGuid128,
        };
        update.ThreatList.Add(new ThreatInfo
        {
            ThreaterGUID = playerGuid,
            Threat = 1234500L,
        });
        session.WorldClient.SendPacketToClient(update);

        // Also emit HIGHEST_THREAT_UPDATE so any addon that listens specifically
        // for "you are top threat" sees it. The player is the only threater so
        // they're necessarily highest.
        var highest = new HighestThreatUpdatePkt
        {
            UnitGUID = targetGuid128,
            HighestThreatGUID = playerGuid,
        };
        highest.ThreatList.Add(new ThreatInfo
        {
            ThreaterGUID = playerGuid,
            Threat = 1234500L,
        });
        session.WorldClient.SendPacketToClient(highest);

        SendDebugChat($"jpthreat: sent SMSG_THREAT_UPDATE + HIGHEST for {targetGuid128} (12345 raw / 1234500 wire)");
    }

    private void SendDebugChat(string text)
    {
        var session = GetSession();
        if (session?.WorldClient == null) return;
        var msg = new ChatPkt(session, ChatMessageTypeModern.System, text);
        session.WorldClient.SendPacketToClient(msg);
    }

    // JimsProxy sideband: addon-driven commands the 1.14 modern client wire
    // protocol no longer carries. Body grammar:
    //   "1" / "0"          — handshake on/off (legacy castbar/HC interop)
    //   "<cmd>\t<args>..." — structured command, e.g. "Z\tStormwind City"
    private void HandleJimsPlusSideband(string body)
    {
        if (string.IsNullOrEmpty(body)) return;

        if (body == "0" || body == "1")
        {
            GetSession().GameState.JimsPlusSideband = body == "1";
            return;
        }

        int tab = body.IndexOf('\t');
        if (tab <= 0) return;

        string cmd = body.Substring(0, tab);
        string args = body.Substring(tab + 1);

        switch (cmd)
        {
            case "Z":
                HandleZoneSideband(args);
                break;
        }
    }

    // Zone sideband: addon's ZONE_CHANGED_NEW_AREA hook reports the player's
    // new zone name (GetRealZoneText). vmangos's Player::UpdateLocalChannels
    // is a server-side no-op — the 1.12 native client drives zone-channel
    // rejoin itself via CMSG_LEAVE/JOIN, but the 1.14 modern client doesn't.
    // We synthesize that behavior here so General/Trade/LocalDefense flip
    // to the new zone without a relog.
    private void HandleZoneSideband(string zoneName)
    {
        if (string.IsNullOrWhiteSpace(zoneName)) return;
        zoneName = zoneName.Trim();

        if (!GameData.AreaIdsByName.TryGetValue(zoneName, out uint newZoneId))
        {
            Log.Event("chat.zone_sideband.unknown_name", new { zone_name = zoneName });
            return;
        }

        var gs = GetSession().GameState;
        uint oldZoneId = gs.CurrentZoneId;
        if (oldZoneId == newZoneId) return;

        var worldClient = GetSession().WorldClient;
        Log.Event("chat.zone_sideband", new
        {
            zone_name = zoneName,
            old_zone_id = oldZoneId,
            new_zone_id = newZoneId,
            world_client_alive = worldClient != null,
        });

        gs.CurrentZoneId = newZoneId;
        worldClient?.SyncZoneChannels(oldZoneId, newZoneId);
    }
}

using Framework;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Globalization;
using Framework.Logging;
using static HermesProxy.World.Server.Packets.ChannelListResponse;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    // Handlers for SMSG opcodes coming the legacy world server
    [PacketHandler(Opcode.SMSG_CHANNEL_NOTIFY)]
    void HandleChannelNotify(WorldPacket packet)
    {
        ChatNotify type = (ChatNotify)packet.ReadUInt8();

        if (type == ChatNotify.InvalidName)           // hack, because of some silly reason this type
            packet.ReadBytes(3);                      // has 3 null bytes before the invalid channel name

        string channelName = packet.ReadCString();

        switch (type)
        {
            case ChatNotify.PlayerAlreadyMember:
            case ChatNotify.Invite:
            case ChatNotify.ModerationOn:
            case ChatNotify.ModerationOff:
            case ChatNotify.AnnouncementsOn:
            case ChatNotify.AnnouncementsOff:
            case ChatNotify.PasswordChanged:
            case ChatNotify.OwnerChanged:
            case ChatNotify.Joined:
            case ChatNotify.Left:
            case ChatNotify.VoiceOn:
            case ChatNotify.VoiceOff:
            {
                packet.ReadGuid();
                break;
            }
            case ChatNotify.YouJoined:
            {
                ChannelFlags flags;
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                    flags = (ChannelFlags)packet.ReadUInt8();
                else
                    flags = (ChannelFlags)packet.ReadUInt32();
                int channelId = packet.ReadInt32();
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                    packet.ReadInt32(); // unk

                if (channelId == 0)
                    channelId = (int)GameData.GetChatChannelIdFromName(channelName);

                GetSession().GameState.SetChannelId(channelName, channelId);

                ChannelNotifyJoined joined = new ChannelNotifyJoined();
                joined.Channel = channelName;
                joined.ChannelFlags = flags;
                joined.ChatChannelID = channelId;
                joined.ChannelGUID = WowGuid128.Create(HighGuidType703.ChatChannel, (uint)GetSession().GameState.CurrentMapId!, (uint)GetSession().GameState.CurrentZoneId!, (ulong)channelId);
                SendPacketToClient(joined);

                break;
            }
            case ChatNotify.YouLeft:
            {
                ChannelNotifyLeft left = new ChannelNotifyLeft();
                left.Channel = channelName;
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                {
                    left.ChatChannelID = packet.ReadInt32();
                    left.Suspended = packet.ReadBool(); // Banned?
                }
                else
                {
                    left.ChatChannelID = GetSession().GameState.ChannelIds[channelName];
                    left.Suspended = false;
                }

                // do not send leave notification for default channels when changing zones
                if (String.Equals(GetSession().GameState.LeftChannelName, channelName) ||
                    GameData.GetChatChannelIdFromName(channelName) == 0)
                    SendPacketToClient(left);
                break;
            }
            case ChatNotify.PlayerNotFound:
            case ChatNotify.ChannelOwner:
            case ChatNotify.PlayerNotBanned:
            case ChatNotify.PlayerInvited:
            case ChatNotify.PlayerInviteBanned:
            {
                packet.ReadCString(); // Player Name
                break;
            }
            case ChatNotify.ModeChange:
            {
                packet.ReadGuid();
                packet.ReadUInt8(); // Old ChannelMemberFlag
                packet.ReadUInt8(); // New ChannelMemberFlag
                break;
            }
            case ChatNotify.PlayerKicked:
            case ChatNotify.PlayerBanned:
            case ChatNotify.PlayerUnbanned:
            {
                packet.ReadGuid(); // Bad
                packet.ReadGuid(); // Good
                break;
            }
            case ChatNotify.TrialRestricted:
            {
                packet.ReadGuid();
                break;
            }
            case ChatNotify.WrongPassword:
            case ChatNotify.NotMember:
            case ChatNotify.NotModerator:
            case ChatNotify.NotOwner:
            case ChatNotify.Muted:
            case ChatNotify.Banned:
            case ChatNotify.InviteWrongFaction:
            case ChatNotify.WrongFaction:
            case ChatNotify.InvalidName:
            case ChatNotify.NotModerated:
            case ChatNotify.Throttled:
            case ChatNotify.NotInArea:
            case ChatNotify.NotInLfg:
                break;
        }
    }

    [PacketHandler(Opcode.SMSG_CHANNEL_LIST)]
    void HandleChannelList(WorldPacket packet)
    {
        ChannelListResponse list = new ChannelListResponse();
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            list.Display = packet.ReadBool();
        else
            list.Display = GetSession().GameState.ChannelDisplayList;
        list.ChannelName = packet.ReadCString();
        list.ChannelFlags = (ChannelFlags)packet.ReadUInt8();
        int count = packet.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            ChannelPlayer member = new ChannelPlayer();
            member.Guid = packet.ReadGuid().To128(GetSession().GameState);
            member.VirtualRealmAddress = GetSession().RealmId.GetAddress();
            member.Flags = packet.ReadUInt8();
            list.Members.Add(member);
        }
        SendPacketToClient(list);
    }

    [PacketHandler(Opcode.SMSG_CHAT, ClientVersionBuild.Zero, ClientVersionBuild.V2_0_1_6180)]
    void HandleServerChatMessageVanilla(WorldPacket packet)
    {
        ChatMessageTypeVanilla chatType = (ChatMessageTypeVanilla)packet.ReadUInt8();
        uint language = packet.ReadUInt32();
        string senderName = "";
        WowGuid128 sender = default;
        WowGuid128 receiver = default;
        string channelName = "";

        switch (chatType)
        {
            case ChatMessageTypeVanilla.MonsterWhisper:
            //case CHAT_MSG_RAID_BOSS_WHISPER:
            case ChatMessageTypeVanilla.RaidBossEmote:
            case ChatMessageTypeVanilla.MonsterEmote:
                packet.ReadUInt32(); // Sender Name Length
                senderName = packet.ReadCString();
                receiver = packet.ReadGuid().To128(GetSession().GameState);
                break;
            case ChatMessageTypeVanilla.Say:
            case ChatMessageTypeVanilla.Party:
            case ChatMessageTypeVanilla.Yell:
                sender = packet.ReadGuid().To128(GetSession().GameState);
                packet.ReadGuid(); // Sender Guid again
                break;
            case ChatMessageTypeVanilla.MonsterSay:
            case ChatMessageTypeVanilla.MonsterYell:
                sender = packet.ReadGuid().To128(GetSession().GameState);
                packet.ReadUInt32(); // Sender Name Length
                senderName = packet.ReadCString();
                receiver = packet.ReadGuid().To128(GetSession().GameState);
                break;

            case ChatMessageTypeVanilla.Channel:
                channelName = packet.ReadCString();
                packet.ReadUInt32(); // Player Rank
                sender = packet.ReadGuid().To128(GetSession().GameState);
                break;
            default:
                sender = packet.ReadGuid().To128(GetSession().GameState);
                break;
        }

        switch (chatType)
        {
            case ChatMessageTypeVanilla.BattlegroundAlliance:
            case ChatMessageTypeVanilla.BattlegroundHorde:
                Utility.Swap(ref sender, ref receiver);
                break;
        }

        uint textLength = packet.ReadUInt32();
        string text = packet.ReadString(textLength);
        var chatTag = (ChatTag)packet.ReadUInt8();
        var chatFlags = chatTag.CastEnum<ChatFlags>();

        if (Session.GameState.IgnoredPlayers.Contains(sender) && !chatFlags.HasFlag(ChatFlags.GM) && chatType != ChatMessageTypeVanilla.Ignored)
        {
            if (chatType == ChatMessageTypeVanilla.Whisper)
            { // In legacy versions the client handled the ignore itself and also sends a "You are ignored" message back.
                WorldPacket ignoreResponsePacket = new WorldPacket(Opcode.CMSG_CHAT_REPORT_IGNORED);
                ignoreResponsePacket.WriteGuid(sender!.To64());
                SendPacketToServer(ignoreResponsePacket);
            }
            return;
        }
        
        string addonPrefix = "";
        if (!ChatPkt.CheckAddonPrefix(GetSession().GameState.AddonPrefixes, ref language, ref text, ref addonPrefix))
            return;

        // JimsProxy (chat-link-suffix 2026-05-07): expand vanilla 4-field item links back into
        // modern Classic 1.14 12-field format on the way to the modern client. The legacy server
        // requires the vanilla format for its anti-spam validation (name match against signed
        // randomProperty in ItemRandomSuffix.dbc), but the modern client parses the chat-link
        // positionally as modern format — without expansion the suffix ID lands in a gem slot
        // and the tooltip renders the base item without "of the X" stats.
        text = ExpandVanillaItemLinkToModern(text);
        text = MaybeScrambleForeignLanguage(text, language);

        ChatMessageTypeModern chatTypeModern = chatType.CastEnum<ChatMessageTypeModern>();
        ChatPkt chat = new ChatPkt(GetSession(), chatTypeModern, text, language, sender, senderName, receiver, "", channelName, chatFlags, addonPrefix);
        SendPacketToClient(chat);
    }

    /// <summary>
    /// JimsProxy: rewrites vanilla 4-field item hyperlinks into modern Classic 1.14 12-field
    /// format. Vanilla format <c>|Hitem:itemID:enchantID:randomProperty:creator|h</c> becomes
    /// <c>|Hitem:itemID:enchantID:0:0:0:0:suffixID:0:linkLevel:0:0:0|h</c> with suffix at modern
    /// position 5 (always positive — modern client uses sign-agnostic positive lookup at this
    /// position). randomProperty &lt; 0 means ItemRandomSuffix.dbc; the absolute value is the
    /// DBC ID. Items with no suffix (randomProperty == 0) still get expanded so the modern
    /// client always sees a consistent format.
    /// </summary>
    private static string ExpandVanillaItemLinkToModern(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("|Hitem:"))
            return text;
        return System.Text.RegularExpressions.Regex.Replace(text,
            @"\|Hitem:(\d+):(-?\d+):(-?\d+):(-?\d+)\|h",
            match =>
            {
                string itemId = match.Groups[1].Value;
                int enchant = int.TryParse(match.Groups[2].Value, out var e) ? e : 0;
                int randomProp = int.TryParse(match.Groups[3].Value, out var r) ? r : 0;
                int suffixModern = randomProp < 0 ? -randomProp : randomProp;
                Framework.Logging.Log.Event("chat.item_link.expanded", new
                {
                    item_id = itemId,
                    enchant_id = enchant,
                    random_property_signed = randomProp,
                    suffix_modern_positive = suffixModern,
                });
                return $"|Hitem:{itemId}:{enchant}:0:0:0:0:{suffixModern}:0:60:0:0:0|h";
            });
    }

    [PacketHandler(Opcode.SMSG_CHAT, ClientVersionBuild.V2_0_1_6180)]
    [PacketHandler(Opcode.SMSG_GM_MESSAGECHAT, ClientVersionBuild.V2_0_1_6180)]
    void HandleServerChatMessageWotLK(WorldPacket packet)
    {
        ChatMessageTypeWotLK chatType = (ChatMessageTypeWotLK)packet.ReadUInt8();
        uint language = packet.ReadUInt32();
        WowGuid128 sender = packet.ReadGuid().To128(GetSession().GameState);
        string senderName = "";
        WowGuid128 receiver;
        string receiverName = "";
        string channelName = "";

        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_1_0_6692))
            packet.ReadInt32(); // Constant time

        switch (chatType)
        {
            case ChatMessageTypeWotLK.Achievement:
            case ChatMessageTypeWotLK.GuildAchievement:
            {
                receiver = packet.ReadGuid().To128(GetSession().GameState);
                break;
            }
            case ChatMessageTypeWotLK.WhisperForeign:
            {
                uint senderNameLength = packet.ReadUInt32();
                senderName = packet.ReadString(senderNameLength);
                receiver = packet.ReadGuid().To128(GetSession().GameState);
                break;
            }
            case ChatMessageTypeWotLK.BattlegroundNeutral:
            case ChatMessageTypeWotLK.BattlegroundAlliance:
            case ChatMessageTypeWotLK.BattlegroundHorde:
            {
                receiver = packet.ReadGuid().To128(GetSession().GameState);
                switch (receiver.GetHighType())
                {
                    case HighGuidType.Creature:
                    case HighGuidType.Vehicle:
                    case HighGuidType.GameObject:
                    case HighGuidType.Transport:
                    case HighGuidType.Pet:
                        uint senderNameLength = packet.ReadUInt32();
                        senderName = packet.ReadString(senderNameLength);
                        break;
                }
                break;
            }
            case ChatMessageTypeWotLK.MonsterSay:
            case ChatMessageTypeWotLK.MonsterYell:
            case ChatMessageTypeWotLK.MonsterParty:
            case ChatMessageTypeWotLK.MonsterEmote:
            case ChatMessageTypeWotLK.MonsterWhisper:
            case ChatMessageTypeWotLK.RaidBossEmote:
            case ChatMessageTypeWotLK.RaidBossWhisper:
            case ChatMessageTypeWotLK.BattleNet:
            {
                uint senderNameLength = packet.ReadUInt32();
                senderName = packet.ReadString(senderNameLength);
                receiver = packet.ReadGuid().To128(GetSession().GameState);
                switch (receiver.GetHighType())
                {
                    case HighGuidType.Creature:
                    case HighGuidType.Vehicle:
                    case HighGuidType.GameObject:
                    case HighGuidType.Transport:
                        uint receiverNameLength = packet.ReadUInt32();
                        receiverName = packet.ReadString(receiverNameLength);
                        break;
                }
                break;
            }
            default:
            {
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056) &&
                    packet.GetUniversalOpcode(false) == Opcode.SMSG_GM_MESSAGECHAT)
                {
                    uint gmNameLength = packet.ReadUInt32();
                    packet.ReadString(gmNameLength);
                }

                if (chatType == ChatMessageTypeWotLK.Channel)
                    channelName = packet.ReadCString();

                receiver = packet.ReadGuid().To128(GetSession().GameState);
                break;
            }
        }

        switch (chatType)
        {
            case ChatMessageTypeWotLK.BattlegroundAlliance:
            case ChatMessageTypeWotLK.BattlegroundHorde:
                Utility.Swap(ref sender, ref receiver);
                break;
        }

        uint textLength = packet.ReadUInt32();
        string text = packet.ReadString(textLength);
        var chatFlags = (ChatFlags)packet.ReadUInt8();

        if (LegacyVersion.InVersion(ClientVersionBuild.V2_0_1_6180, ClientVersionBuild.V3_0_2_9056) &&
            packet.GetUniversalOpcode(false) == Opcode.SMSG_GM_MESSAGECHAT)
        {
            uint gmNameLength = packet.ReadUInt32();
            packet.ReadString(gmNameLength);
        }

        uint achievementId = 0;
        if (chatType == ChatMessageTypeWotLK.Achievement || chatType == ChatMessageTypeWotLK.GuildAchievement)
            achievementId = packet.ReadUInt32();

        if (Session.GameState.IgnoredPlayers.Contains(sender) && !chatFlags.HasFlag(ChatFlags.GM) && chatType != ChatMessageTypeWotLK.Ignored)
        {
            if (chatType == ChatMessageTypeWotLK.Whisper)
            { // In legacy versions the client handled the ignore itself and also sends a "You are ignored" message back.
                WorldPacket ignoreResponsePacket = new WorldPacket(Opcode.CMSG_CHAT_REPORT_IGNORED);
                ignoreResponsePacket.WriteGuid(sender!.To64());
                ignoreResponsePacket.WriteUInt8(0); // unk
                SendPacketToServer(ignoreResponsePacket);
            }
            return;
        }

        string addonPrefix = "";
        if (!ChatPkt.CheckAddonPrefix(GetSession().GameState.AddonPrefixes, ref language, ref text, ref addonPrefix))
            return;

        text = MaybeScrambleForeignLanguage(text, language);

        ChatMessageTypeModern chatTypeModern = chatType.CastEnum<ChatMessageTypeModern>();
        ChatPkt chat = new ChatPkt(GetSession(), chatTypeModern, text, language, sender, senderName, receiver, receiverName, channelName, chatFlags, addonPrefix, achievementId);
        SendPacketToClient(chat);
    }

    //MIRASU - Resolves the local player's racial language list and scrambles the chat body when
    //MIRASU   the language being broadcast isn't one the receiver should be able to read. Vanilla
    //MIRASU   1.12 emulators ship plain text + language ID and depend on the legacy 1.12 client
    //MIRASU   to scramble; the modern 1.14 client doesn't honour that for legacy IDs, so without
    //MIRASU   this hook foreign-faction speech leaks through unobfuscated. See HermesProxy issues
    //MIRASU   #100 (cross-faction comprehension) and #213 (unknown languages plain).
    private string MaybeScrambleForeignLanguage(string text, uint language)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var localPlayer = GetSession().GameState.CurrentPlayerInfo;
        if (localPlayer == null)
            return text;

        if (LanguageScrambler.CanUnderstand(localPlayer.RaceId, localPlayer.ClassId, language))
            return text;

        if (!LanguageScrambler.HasSyllabary(language))
        {
            Log.Event("chat.scramble.skipped", new
            {
                reason = "no_syllabary",
                language,
                receiverRace = localPlayer.RaceId.ToString(),
                receiverClass = localPlayer.ClassId.ToString(),
            });
            return text;
        }

        return LanguageScrambler.Scramble(text, language);
    }

    public void SendMessageChatVanilla(ChatMessageTypeVanilla type, uint lang, string msg, string channel, string to)
    {
        if (HandleHermesInternalChatCommand(msg))
        {
            return; // was handled by us
        }
        Log.Print(LogType.Debug, "RAW CHAT INTERCEPTED: " + msg);
        // JimsProxy (chat-link-suffix 2026-05-07): preserve enchant + random-suffix when
        // collapsing modern item-link inner fields to vanilla format. The original fix
        // (f667118) replaced everything between :itemID and |h with :0:0:0, dropping the
        // suffixID at position 5 — so "Sword of the Eagle" arrived at the legacy server as a
        // plain "Sword" link, and the broadcast SMSG_CHAT either silently dropped the suffix
        // or got rejected entirely.
        //
        // Modern WoW Classic 1.14 itemString fields after itemID:
        //   [0] enchantID, [1-4] gem1-4, [5] suffixID, [6] uniqueID, [7] linkLevel, ...
        // Vanilla 1.12 itemString fields after itemID:
        //   [0] enchantID, [1] randomProperty (signed; matches modern suffixID), [2] creator
        msg = System.Text.RegularExpressions.Regex.Replace(msg, @"\|Hitem:(\d+)([^|]*)\|h(\[[^\]]*\])?", match =>
        {
            string itemId = match.Groups[1].Value;
            string raw = match.Groups[2].Value;
            string nameTag = match.Groups[3].Value;
            // The captured group starts with ':' (the separator after itemID). Substring(1)
            // skips that leading separator so Split(':') preserves empty positions correctly
            // — earlier TrimStart(':') was eating leading-empty fields and shifting indices.
            string[] inner = raw.Length > 0 && raw[0] == ':'
                ? raw.Substring(1).Split(':')
                : raw.Split(':');
            // JimsProxy (chat-link-suffix 2026-05-07): WoW Classic Era 1.14 itemString fields
            // after itemID, verified empirically against bundle jimsproxy-20260507-183921:
            //   [0] enchantID     [1-4] gem1-4              [5] suffixID
            //   [6] uniqueID      [7] linkLevel             [8..] specID/upgrade/etc.
            // (matches the Wowpedia public docs after the leading-colon Substring(1) fix.)
            int enchant = inner.Length > 0 && int.TryParse(inner[0], out var e) ? e : 0;
            int suffix  = inner.Length > 5 && int.TryParse(inner[5], out var s) ? s : 0;
            // JimsProxy (chat-link-suffix 2026-05-07): vanilla wire chat-link convention is
            // signed — negative randomProperty → ItemRandomSuffix.dbc lookup (variable-stat
            // "of the Bear"), positive → ItemRandomProperties.dbc (fixed bonuses).
            // Modern Classic 1.14 stores both as positive at position 5; assume ItemRandomSuffix
            // (the dominant case for "of the X" suffix items) and negate. If a value is already
            // negative we leave it alone — modern client may pre-sign it.
            if (suffix > 0)
                suffix = -suffix;
            Framework.Logging.Log.Event("chat.item_link.translated", new
            {
                item_id = itemId,
                inner_raw = raw,
                inner_fields = inner,
                inner_field_count = inner.Length,
                enchant_id = enchant,
                suffix_id = suffix,
                display_name = nameTag,
            });
            return $"|Hitem:{itemId}:{enchant}:{suffix}:0|h{nameTag}";
        });
        WorldPacket packet = new WorldPacket(Opcode.CMSG_MESSAGECHAT);
        packet.WriteUInt32((uint)type);
        packet.WriteUInt32(lang);

        switch (type)
        {
            case ChatMessageTypeVanilla.Channel:
                packet.WriteCString(channel);
                packet.WriteCString(msg);
                break;
            case ChatMessageTypeVanilla.Whisper:
                packet.WriteCString(to);
                packet.WriteCString(msg);
                break;
            case ChatMessageTypeVanilla.Say:
            case ChatMessageTypeVanilla.Emote:
            case ChatMessageTypeVanilla.Yell:
            case ChatMessageTypeVanilla.Party:
            case ChatMessageTypeVanilla.Guild:
            case ChatMessageTypeVanilla.Officer:
            case ChatMessageTypeVanilla.Raid:
            case ChatMessageTypeVanilla.RaidLeader:
            case ChatMessageTypeVanilla.RaidWarning:
            case ChatMessageTypeVanilla.Battleground:
            case ChatMessageTypeVanilla.BattlegroundLeader:
            case ChatMessageTypeVanilla.Afk:
            case ChatMessageTypeVanilla.Dnd:
                packet.WriteCString(msg);
                break;
        }

        SendPacket(packet);
    }

    // TODO: make all of these available via HTML ingame support menu (as soon as we can influence the page)
    private bool HandleHermesInternalChatCommand(string msg)
    {
        // Marks a quest as completed
        // Useful for /run print(C_QuestLog.IsQuestFlaggedCompleted($questId))
        // !qcomplete <questId>
        if (msg.StartsWith("!qcomplete"))
        {
            var questIdStr = msg.Remove(0, "!qcomplete".Length);
            if (!uint.TryParse(questIdStr, NumberStyles.Integer, NumberFormatInfo.InvariantInfo, out var questId))
            {
                GetSession().SendHermesTextMessage($"Chat command invalid questId format '{questIdStr}'");
                return true;
            }
            GetSession().GameState.CurrentPlayerStorage.CompletedQuests.MarkQuestAsCompleted(questId);
            return true;
        }

        // Marks a quest as uncompleted
        // !quncomplete <questId>
        if (msg.StartsWith("!quncomplete"))
        {
            var questIdStr = msg.Remove(0, "!quncomplete".Length);
            if (!uint.TryParse(questIdStr, NumberStyles.Integer, NumberFormatInfo.InvariantInfo, out var questId))
            {
                GetSession().SendHermesTextMessage($"Chat command invalid questId format '{questIdStr}'");
                return true;
            }
            GetSession().GameState.CurrentPlayerStorage.CompletedQuests.MarkQuestAsNotCompleted(questId);
            return true;
        }

        return false;
    }

    public void SendMessageChatWotLK(ChatMessageTypeWotLK type, uint lang, string msg, string channel, string to)
    {
        if (HandleHermesInternalChatCommand(msg))
        {
            return; // was handled by us
        }

        WorldPacket packet = new WorldPacket(Opcode.CMSG_MESSAGECHAT);
        packet.WriteUInt32((uint)type);
        packet.WriteUInt32(lang);

        switch (type)
        {
            case ChatMessageTypeWotLK.Channel:
                packet.WriteCString(channel);
                packet.WriteCString(msg);
                break;
            case ChatMessageTypeWotLK.Whisper:
                packet.WriteCString(to);
                packet.WriteCString(msg);
                break;
            case ChatMessageTypeWotLK.Say:
            case ChatMessageTypeWotLK.Emote:
            case ChatMessageTypeWotLK.Yell:
            case ChatMessageTypeWotLK.Party:
            case ChatMessageTypeWotLK.PartyLeader:
            case ChatMessageTypeWotLK.Guild:
            case ChatMessageTypeWotLK.Officer:
            case ChatMessageTypeWotLK.Raid:
            case ChatMessageTypeWotLK.RaidLeader:
            case ChatMessageTypeWotLK.RaidWarning:
            case ChatMessageTypeWotLK.Battleground:
            case ChatMessageTypeWotLK.BattlegroundLeader:
            case ChatMessageTypeWotLK.Afk:
            case ChatMessageTypeWotLK.Dnd:
                packet.WriteCString(msg);
                break;
        }

        SendPacket(packet);
    }

    [PacketHandler(Opcode.SMSG_EMOTE)]
    void HandleEmote(WorldPacket packet)
    {
        EmoteMessage emote = new EmoteMessage();
        emote.EmoteID = packet.ReadUInt32();
        emote.Guid = packet.ReadGuid().To128(GetSession().GameState);
        // JimsProxy (emote-state-diag 2026-05-07): capture emote broadcasts so we can pair them
        // with subsequent emote.state.update events when triaging stuck-dance bugs.
        Framework.Logging.Log.Event("emote.broadcast", new
        {
            emote_id = emote.EmoteID,
            target_low = emote.Guid.GetCounter(),
            is_player_target = emote.Guid == GetSession().GameState.CurrentPlayerGuid,
        });
        // JimsProxy (dance-stuck-on-movement 2026-05-07): track the player's last looping
        // emote. EMOTE_ONESHOT_DANCE (10) is the known case — Classic 1.14 client loops it
        // until another SMSG_EMOTE arrives, and Kronos/Twinstar don't broadcast one on move.
        // Any new SMSG_EMOTE for the player overrides/clears the tracker (matches the actual
        // client-side behavior — a new emote replaces the active loop).
        if (emote.Guid == GetSession().GameState.CurrentPlayerGuid)
        {
            if (IsClientLoopingEmote(emote.EmoteID))
            {
                GetSession().GameState.LastLoopingEmoteId = emote.EmoteID;
                GetSession().GameState.LastLoopingEmoteTickMs = Environment.TickCount64;
            }
            else
            {
                // Non-looping emote breaks any active loop on the client side; mirror that.
                GetSession().GameState.LastLoopingEmoteId = 0;
            }
        }
        SendPacketToClient(emote);
    }

    /// <summary>
    /// JimsProxy: returns true if the given EMOTE_ONESHOT_* ID is one that the modern
    /// Classic 1.14 client treats as a looping animation client-side (continues until a
    /// new SMSG_EMOTE arrives). Currently just EMOTE_ONESHOT_DANCE (10). Add others here
    /// if reports surface for /sleep, /kneel, etc.
    /// </summary>
    private static bool IsClientLoopingEmote(uint emoteId)
    {
        return emoteId == 10; // EMOTE_ONESHOT_DANCE
    }

    [PacketHandler(Opcode.SMSG_TEXT_EMOTE)]
    void HandleTextEmote(WorldPacket packet)
    {
        STextEmote emote = new STextEmote();
        emote.SourceGUID = packet.ReadGuid().To128(GetSession().GameState);
        emote.SourceAccountGUID = GetSession().GetGameAccountGuidForPlayer(emote.SourceGUID);
        emote.EmoteID = packet.ReadInt32();
        emote.SoundIndex = packet.ReadInt32();
        uint nameLength = packet.ReadUInt32();
        string targetName = packet.ReadString(nameLength);
        emote.TargetGUID = GetSession().GameState.GetPlayerGuidByName(targetName);
        SendPacketToClient(emote);
    }

    [PacketHandler(Opcode.SMSG_PRINT_NOTIFICATION)]
    void HandlePrintNotification(WorldPacket packet)
    {
        PrintNotification notify = new PrintNotification();
        notify.NotifyText = packet.ReadCString();
        SendPacketToClient(notify);
    }

    [PacketHandler(Opcode.SMSG_CHAT_PLAYER_NOTFOUND)]
    void HandleChatPlayerNotFound(WorldPacket packet)
    {
        ChatPlayerNotfound error = new ChatPlayerNotfound();
        error.Name = packet.ReadCString();
        SendPacketToClient(error);
    }

    [PacketHandler(Opcode.SMSG_DEFENSE_MESSAGE)]
    void HandleDefenseMessage(WorldPacket packet)
    {
        DefenseMessage message = new DefenseMessage();
        message.ZoneID = packet.ReadUInt32();
        packet.ReadUInt32(); // message length
        message.MessageText = packet.ReadCString();
        SendPacketToClient(message);
    }

    [PacketHandler(Opcode.SMSG_CHAT_SERVER_MESSAGE)]
    void HandleChatServerMessage(WorldPacket packet)
    {
        ChatServerMessage message = new ChatServerMessage();
        message.MessageID = packet.ReadInt32();
        message.StringParam = packet.ReadCString();
        SendPacketToClient(message);
    }

    public void SendChatJoinChannel(int channelId, string channelName, string password)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CHAT_JOIN_CHANNEL);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            packet.WriteInt32(channelId);
            packet.WriteUInt8(0); // Has Voice
            packet.WriteUInt8(0); // Joined by zone update
        }
        packet.WriteCString(channelName);
        packet.WriteCString(password);
        SendPacketToServer(packet);
    }

    public void SendChatLeaveChannel(int channelId, string channelName)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CHAT_LEAVE_CHANNEL);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            packet.WriteInt32(channelId);
        packet.WriteCString(channelName);
        SendPacketToServer(packet);
    }
}

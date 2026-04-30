using System;
using System.Linq;
using Framework.Constants;
using Framework.Logging;
using HermesProxy.Auth;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    // Handlers for CMSG opcodes coming from the modern client
    [PacketHandler(Opcode.CMSG_ENUM_CHARACTERS)]
    void HandleEnumCharacters(EnumCharacters charEnum)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_ENUM_CHARACTERS);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_GET_ACCOUNT_CHARACTER_LIST)]
    void HandleGetAccountCharacterList(GetAccountCharacterListRequest request)
    {
        // Safely hold the UI back in a background task so we don't freeze the main proxy thread!
        System.Threading.Tasks.Task.Run(async () =>
        {
            int timeout = 0;
            // Wait up to 2 seconds for the server to reply and fill the cache
            while (GetSession().GameState.OwnCharacters.Count == 0 && timeout < 100)
            {
                await System.Threading.Tasks.Task.Delay(20);
                timeout++;
            }

            GetAccountCharacterListResult response = new();
            response.Token = request.Token;

            foreach (var ownCharacter in GetSession().GameState.OwnCharacters)
            {
                response.CharacterList.Add(new AccountCharacterListEntry
                {
                    AccountId = WowGuid128.Create(HighGuidType703.WowAccount, GetSession().GameAccountInfo.Id),
                    CharacterGuid = ownCharacter.CharacterGuid,
                    RealmVirtualAddress = GetSession().RealmId.GetAddress(),
                    RealmName = "",
                    LastLoginUnixSec = ownCharacter.LastLoginUnixSec,
                    Name = ownCharacter.Name ?? string.Empty,
                    Race = ownCharacter.RaceId,
                    Class = ownCharacter.ClassId,
                    Sex = ownCharacter.SexId,
                    Level = ownCharacter.Level,
                });
            }

            SendPacket(response);
        });
    }

    [PacketHandler(Opcode.CMSG_GENERATE_RANDOM_CHARACTER_NAME)]
    void HandleGenerateRandomCharacterNameRequest(GenerateRandomCharacterNameRequest randomCharacterName)
    {
        GenerateRandomCharacterNameResult result = new();

        // The client can generate the name itself
        result.Success = false;

        SendPacket(result);
    }

    [PacketHandler(Opcode.CMSG_CREATE_CHARACTER)]
    void HandleCreateCharacter(CreateCharacter charCreate)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CREATE_CHARACTER);
        packet.WriteCString(charCreate.CreateInfo.Name);
        packet.WriteUInt8((byte)charCreate.CreateInfo.RaceId);
        packet.WriteUInt8((byte)charCreate.CreateInfo.ClassId);
        packet.WriteUInt8((byte)charCreate.CreateInfo.Sex);

        CharacterCustomizations.ConvertModernCustomizationsToLegacy(charCreate.CreateInfo.Customizations, out byte skin, out byte face, out byte hairStyle, out byte hairColor, out byte facialhair);
        packet.WriteUInt8(skin);
        packet.WriteUInt8(face);
        packet.WriteUInt8(hairStyle);
        packet.WriteUInt8(hairColor);
        packet.WriteUInt8(facialhair);
        packet.WriteUInt8(0); // outfit
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_CHAR_DELETE)]
    void HandleCharDelete(CharDelete charDelete)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CHAR_DELETE);
        packet.WriteGuid(charDelete.Guid.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_LOADING_SCREEN_NOTIFY)]
    void HandleLoadScreen(LoadingScreenNotify loadingScreenNotify)
    {
        if (loadingScreenNotify.MapID >= 0)
            GetSession().GameState.CurrentMapId = loadingScreenNotify.MapID;

        //MIRASU: /reload on the 1.14.2 client fires CMSG_LOADING_SCREEN_NOTIFY(Showing=true)
        //MIRASU: then CMSG_LOADING_SCREEN_NOTIFY(Showing=false) with no client disconnect.
        //MIRASU: During the window the client tears down its UI aura cache and expects the
        //MIRASU: server to re-push via fresh SMSG_AURA_UPDATE. Retail servers honour this;
        //MIRASU: Kronos (1.12) doesn't know what a /reload is (the opcode didn't exist in
        //MIRASU: vanilla) so it never re-sends anything, leaving the player's buff bar empty
        //MIRASU: until some later event re-dirties the aura update-fields. Mob debuffs and
        //MIRASU: party buffs survive because the client rebuilds those from their UnitFrame
        //MIRASU: data on /reload; only self-auras rely on the SMSG_AURA_UPDATE event stream.
        //MIRASU: On Showing==false (reload done), we synthesize a full
        //MIRASU: SMSG_AURA_UPDATE(updateAll=true) for the current player by walking our
        //MIRASU: cached legacy update-fields for their own GUID. Mirrors the relog path
        //MIRASU: (UpdateHandler.cs aura loop around line 2088) including the NoCaster clamp.
        if (!loadingScreenNotify.Showing)
        {
            var state = GetSession().GameState;
            var playerGuid = state.CurrentPlayerGuid;
            var worldClient = GetSession().WorldClient;
            if (playerGuid != default && worldClient != null)
            {
                var cachedFields = state.GetCachedObjectFieldsLegacy(playerGuid);
                if (cachedFields != null)
                {
                    int UNIT_FIELD_AURA = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_AURA);
                    if (UNIT_FIELD_AURA > 0)
                    {
                        AuraUpdate auraUpdate = new AuraUpdate(playerGuid, true);
                        int aurasCount = LegacyVersion.GetAuraSlotsCount();
                        for (byte i = 0; i < aurasCount; i++)
                        {
                            var auraData = worldClient.ReadAuraSlot(i, playerGuid, cachedFields);
                            if (auraData == null)
                                continue;

                            int durationLeft;
                            int durationFull;
                            state.GetAuraDuration(playerGuid, i, out durationLeft, out durationFull);
                            if (durationLeft > 0 && durationFull > 0)
                            {
                                auraData.Flags |= AuraFlagsModern.Duration;
                                auraData.Duration = durationFull;
                                auraData.Remaining = durationLeft;
                            }

                            var castUnit = state.GetAuraCaster(playerGuid, i, auraData.SpellID);
                            auraData.CastUnit = castUnit;
                            if (castUnit == default)
                                auraData.Flags |= AuraFlagsModern.NoCaster;

                            AuraInfo aura = new AuraInfo();
                            aura.Slot = i;
                            aura.AuraData = auraData;
                            auraUpdate.Auras.Add(aura);
                        }
                        worldClient.SendPacketToClient(auraUpdate);
                    }
                }
            }
        }
    }

    [PacketHandler(Opcode.CMSG_QUERY_PLAYER_NAME)]
    void HandleNameQueryRequest(QueryPlayerName queryPlayerName)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_NAME_QUERY);
        packet.WriteGuid(queryPlayerName.Player.To64());
        SendPacketToServer(packet, GetSession().GameState.IsInWorld ? Opcode.MSG_NULL_ACTION : Opcode.SMSG_LOGIN_VERIFY_WORLD);
    }

    [PacketHandler(Opcode.CMSG_QUERY_PLAYER_NAMES)]
    void HandleNamesQueryRequest(QueryPlayerNames queryPlayerNames)
    {
        foreach (var guid in queryPlayerNames.Players)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_NAME_QUERY);
            packet.WriteGuid(guid.To64());
            SendPacketToServer(packet, GetSession().GameState.IsInWorld ? Opcode.MSG_NULL_ACTION : Opcode.SMSG_LOGIN_VERIFY_WORLD);
        }
    }

    [PacketHandler(Opcode.CMSG_PLAYER_LOGIN)]
    void HandlePlayerLogin(PlayerLogin playerLogin)
    {
        if (GetSession().WorldClient == null || !GetSession().WorldClient!.IsConnected())
        {
            Log.Print(LogType.Error, "WorldClient is disconnected, cannot enter world.");
            AbortLogin(LoginFailureReason.NoWorld);
            return;
        }

        if (!GetSession().GameState.CachedPlayers.TryGetValue(playerLogin.Guid, out var selectedChar))
        {
            Log.Print(LogType.Error, $"Player tried to log in with unknown char id: {playerLogin.Guid}");
            return;
        }

        var realm = GetSession().RealmManager.GetRealm(GetSession().RealmId);
        if (realm == null)
        {
            Log.Print(LogType.Error, $"Player tried to log in to unknown realm id: {GetSession().RealmId}");
            return;
        }

        GetSession().AccountMetaDataMgr.SaveLastSelectedCharacter(realm.Name, selectedChar.Name!, playerLogin.Guid.Low, Time.UnixTime);

        if (GetSession().AuthClient != null)
            GetSession().AuthClient.Disconnect();

        SendConnectToInstance(ConnectToSerial.WorldAttempt1);
        GetSession().GameState.IsConnectedToInstance = true;
        GetSession().GameState.IsFirstEnterWorld = true;
        GetSession().GameState.CurrentPlayerGuid = playerLogin.Guid;
        GetSession().GameState.CurrentPlayerInfo = GetSession().GameState.OwnCharacters.Single(x => x.CharacterGuid == playerLogin.Guid);
        GetSession().GameState.CurrentPlayerStorage.LoadCurrentPlayer();

        //MIRASU - eager disk-load of quest item running totals so the dict is hot before the
        //MIRASU   first item pickup of the session. Otherwise, if SMSG_QUEST_UPDATE_ADD_ITEM
        //MIRASU   arrives buffered (template not cached), SMSG_ITEM_PUSH_RESULT processes
        //MIRASU   ~150ms earlier with an empty dict and Kronos's null cache progress -- the
        //MIRASU   over-head quest toast then renders "1/N" until the buffered credit replays
        //MIRASU   and updates it to the correct count. Eager load ensures dict[key]=stored_before
        //MIRASU   at the moment ITEM_PUSH_RESULT fires, eliminating the flicker.
        GetSession().EnsureQuestItemProgressRestored();

        WorldPacket packet = new WorldPacket(Opcode.CMSG_PLAYER_LOGIN);
        packet.WriteGuid(playerLogin.Guid.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_LOGOUT_REQUEST)]
    void HandleLogoutRequest(LogoutRequest logoutRequest)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_LOGOUT_REQUEST);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_LOGOUT_CANCEL)]
    void HandleLogoutCancel(LogoutCancel logoutCancel)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_LOGOUT_CANCEL);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_REQUEST_PLAYED_TIME)]
    void HandleRequestPlayedTime(RequestPlayedTime played)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_REQUEST_PLAYED_TIME);
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            packet.WriteBool(played.TriggerScriptEvent);
        SendPacketToServer(packet);
        GetSession().GameState.ShowPlayedTime = played.TriggerScriptEvent;
    }

    [PacketHandler(Opcode.CMSG_SET_TITLE)]
    void HandleTogglePvP(SetTitle title)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_TITLE);
        packet.WriteInt32(title.TitleID);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_TOGGLE_PVP)]
    void HandleTogglePvP(TogglePvP pvp)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_TOGGLE_PVP);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SET_PVP)]
    void HandleTogglePvP(SetPvP pvp)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_TOGGLE_PVP);
        packet.WriteBool(pvp.Enable);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SET_ACTION_BUTTON)]
    void HandleSetActionButton(SetActionButton button)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_ACTION_BUTTON);
        packet.WriteUInt8(button.Index);
        packet.WriteUInt16(button.Action);
        packet.WriteUInt16(button.Type);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_SET_ACTION_BAR_TOGGLES)]
    void HandleSetActionBarToggles(SetActionBarToggles bars)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_ACTION_BAR_TOGGLES);
        packet.WriteUInt8(bars.Mask);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_UNLEARN_SKILL)]
    void HandleUnlearnSkill(UnlearnSkill skill)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_UNLEARN_SKILL);
        packet.WriteUInt32(skill.SkillLine);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_PLAYER_SHOWING_CLOAK)]
    [PacketHandler(Opcode.CMSG_PLAYER_SHOWING_HELM)]
    void HandleShowHelmOrCloak(PlayerShowingHelmOrCloak show)
    {
        WorldPacket packet = new WorldPacket(show.GetUniversalOpcode());
        packet.WriteBool(show.Showing);
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_INSPECT)]
    void HandleInspect(Inspect inspect)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_INSPECT);
        packet.WriteGuid(inspect.Target.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_INSPECT_HONOR_STATS)]
    void HandleInspectHonorStats(Inspect inspect)
    {
        WorldPacket packet = new WorldPacket(Opcode.MSG_INSPECT_HONOR_STATS);
        packet.WriteGuid(inspect.Target.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_INSPECT_PVP)]
    void HandleInspectArenaTeams(Inspect inspect)
    {
        if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
        {
            WorldPacket packet = new WorldPacket(Opcode.MSG_INSPECT_ARENA_TEAMS);
            packet.WriteGuid(inspect.Target.To64());
            SendPacketToServer(packet);
        }
        else
        {
            InspectPvP pvp = new InspectPvP();
            pvp.PlayerGUID = inspect.Target;
            pvp.ArenaTeams.Add(new ArenaTeamInspectData());
            pvp.ArenaTeams.Add(new ArenaTeamInspectData());
            pvp.ArenaTeams.Add(new ArenaTeamInspectData());
            SendPacket(pvp);
        }
    }

    [PacketHandler(Opcode.CMSG_CHARACTER_RENAME_REQUEST)]
    void HandleCharacterRenameRequest(CharacterRenameRequest rename)
    {
        WorldPacket packet = new WorldPacket(Opcode.CMSG_CHARACTER_RENAME_REQUEST);
        packet.WriteGuid(rename.Guid.To64());
        packet.WriteCString(rename.NewName);
        SendPacketToServer(packet);
    }
}

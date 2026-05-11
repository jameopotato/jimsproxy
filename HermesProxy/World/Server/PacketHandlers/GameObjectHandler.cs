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
    [PacketHandler(Opcode.CMSG_GAME_OBJ_USE)]
    void HandleGameObjUse(GameObjUse use)
    {
        // MC Runes of Warding (176951-176957) are TYPE_BUTTON with a lock that only the
        // Aqual/Eternal Quintessence (items 17333/22754) can open via OPEN_LOCK. A bare
        // right-click sends CMSG_GAME_OBJ_USE which never triggers the dousing spell on
        // the legacy server, so the rune just silently does nothing. Since our flag
        // override (#374) made the rune cursor-targetable, this surface is also reachable
        // by right-click — print a notification so players know what's expected instead of
        // wondering why the rune ignored them.
        uint entry = use.Guid.GetEntry();
        if (entry >= 176951 && entry <= 176957)
        {
            Log.Event("mc_rune.right_click_hint_sent", new
            {
                guid = use.Guid.ToString(),
                entry = entry,
            });

            PrintNotification notify = new PrintNotification();
            notify.NotifyText = "Requires Quintessence";
            SendPacket(notify);
        }

        // Kronos and similar 1.12 servers disable meeting stones server-side
        // (no summon ritual, no LFG queue). The 1.14 client doesn't know this
        // — right-clicking a meeting stone GO as group leader still initiates
        // the summon-ritual UI flow locally. Drop the CMSG_GAME_OBJ_USE at the
        // proxy boundary so no server-side state changes and no SMSG_SUMMON_*
        // packets get generated. Surface a notification so the player knows
        // the click did register (just intentionally no-op'd).
        //
        // Vanilla GameObjectType 23 = TYPE_MEETINGSTONE. Read the cached
        // GAMEOBJECT_TYPE_ID for this GUID — populated when the legacy server
        // CREATE_OBJECT'd the GO. Falls through if we somehow never cached
        // the type (rare; would require interacting with an unknown GO).
        var goFields = GetSession().GameState.GetCachedObjectFieldsLegacy(use.Guid);
        if (goFields != null)
        {
            int goTypeFieldIdx = LegacyVersion.GetUpdateField(GameObjectField.GAMEOBJECT_TYPE_ID);
            if (goTypeFieldIdx >= 0 && goFields.TryGetValue(goTypeFieldIdx, out var typeField))
            {
                const int TYPE_MEETINGSTONE = 23;
                if (typeField.Int32Value == TYPE_MEETINGSTONE)
                {
                    Log.Event("meeting_stone.use_blocked", new
                    {
                        guid = use.Guid.ToString(),
                        entry = entry,
                    });

                    PrintNotification notify = new PrintNotification();
                    notify.NotifyText = "Meeting stones are disabled on this server";
                    SendPacket(notify);
                    return;
                }
            }
        }

        WorldPacket packet = new WorldPacket(Opcode.CMSG_GAME_OBJ_USE);
        packet.WriteGuid(use.Guid.To64());
        SendPacketToServer(packet);
    }

    [PacketHandler(Opcode.CMSG_GAME_OBJ_REPORT_USE)]
    void HandleGameObjUse(GameObjReportUse use)
    {
        GetSession().GameState.CurrentInteractedWithGO = use.Guid;
    }
}

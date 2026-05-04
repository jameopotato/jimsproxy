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

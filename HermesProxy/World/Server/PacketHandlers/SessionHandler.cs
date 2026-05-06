using System;
using System.Collections.Generic;
using Bgs.Protocol;
using Bgs.Protocol.GameUtilities.V1;
using BNetServer.Services;
using Framework.Constants;
using Framework.IO;
using Framework.Logging;
using Framework.Serialization;
using Framework.Util;
using Framework.Web;
using Google.Protobuf;
using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    [PacketHandler(Opcode.CMSG_CHANGE_REALM_TICKET)]
    void HandleChangeRealmTicket(ChangeRealmTicket request)
    {
        ChangeRealmTicketResponse response = new();
        response.Token = request.Token;

        // JimsProxy: removed AuthClient.Reconnect() that previously fired here when
        // the realmd socket was closed. Kronos closes the realmd TCP after sending the
        // realmlist, so Reconnect() fired on EVERY realm select — doing a full SRP6
        // LOGON_CHALLENGE that doubled the auth count per login and tripped Kronos's
        // rate limiter after a few login cycles. The session key from the initial
        // ConnectToAuthServer persists in the realmd DB until the next login, so the
        // world server's CMSG_AUTH_SESSION validation still works without re-authing.
        Log.Event("realm.swap.change_ticket", new
        {
            phase = "ok",
            auth_connected = GetSession().AuthClient != null && GetSession().AuthClient.IsConnected(),
        });

        _bnetRpc.SetClientSecret(request.Secret);

        response.Allow = true;
        response.Ticket = new ByteBuffer(new byte[1]);

        SendPacket(response);
    }

    [PacketHandler(Opcode.CMSG_BATTLENET_REQUEST)]
    void HandleBattlenetRequest(BattlenetRequest request)
    {
        if (_bnetRpc == null)
        {
            Log.Print(LogType.Error, $"Client tried {Opcode.CMSG_BATTLENET_REQUEST} without authentication");
            return;
        }

        _bnetRpc.Invoke(
            serviceId: 0,
            (OriginalHash)request.Method.GetServiceHash(),
            request.Method.GetMethodId(),
            request.Method.Token,
            new CodedInputStream(request.Data)
        );
    }
}

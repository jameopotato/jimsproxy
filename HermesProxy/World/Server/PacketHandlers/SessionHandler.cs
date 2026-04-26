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
using AuthResult = HermesProxy.Auth.AuthResult;

namespace HermesProxy.World.Server;

public partial class WorldSocket
{
    [PacketHandler(Opcode.CMSG_CHANGE_REALM_TICKET)]
    void HandleChangeRealmTicket(ChangeRealmTicket request)
    {
        ChangeRealmTicketResponse response = new();
        response.Token = request.Token;

        // JimsProxy: realm-swap diagnostics. CHANGE_REALM_TICKET is the modern client's
        // signal that it intends to (re)select a realm — fires both on initial pick from
        // the realm list and on any subsequent swap (e.g. PTR↔Live). The Reconnect()
        // below only runs if AuthClient is currently disconnected; if it's still alive
        // the ticket reuses the existing SRP-derived session key. The session key prefix
        // before/after lets us see whether a fresh SRP key was actually derived, since
        // the new realm will reject reuse of the prior realm's key.
        bool authWasConnected = GetSession().AuthClient != null && GetSession().AuthClient.IsConnected();
        string keyBefore = SafeAuthKeyPrefix(GetSession().AuthClient);
        bool reconnectAttempted = false;
        string? reconnectResult = null;

        if (GetSession().AuthClient != null && !GetSession().AuthClient.IsConnected())
        {
            reconnectAttempted = true;
            var rc = GetSession().AuthClient.Reconnect();
            reconnectResult = rc.ToString();
            if (rc != AuthResult.SUCCESS)
            {
                Log.Event("realm.swap.change_ticket", new
                {
                    phase = "reconnect_failed",
                    auth_was_connected = authWasConnected,
                    reconnect_attempted = reconnectAttempted,
                    reconnect_result = reconnectResult,
                    auth_key_prefix_before = keyBefore,
                });
                Log.Print(LogType.Error, "Failed to reconnect to auth server.");
                response.Allow = false;
                SendPacket(response);
                return;
            }
        }
        // GetSession().AuthClient.SendRealmListUpdateRequest();

        string keyAfter = SafeAuthKeyPrefix(GetSession().AuthClient);
        Log.Event("realm.swap.change_ticket", new
        {
            phase = "ok",
            auth_was_connected = authWasConnected,
            reconnect_attempted = reconnectAttempted,
            reconnect_result = reconnectResult,
            auth_key_prefix_before = keyBefore,
            auth_key_prefix_after = keyAfter,
            auth_key_changed = keyBefore != keyAfter,
        });

        _bnetRpc.SetClientSecret(request.Secret);

        response.Allow = true;
        response.Ticket = new ByteBuffer(new byte[1]);

        SendPacket(response);
    }

    // JimsProxy: 4-byte hex prefix of the SRP-derived session key. Safe against null
    // AuthClient and empty key arrays. Used by realm.swap.* events so we can correlate
    // whether the key actually rotated between phases without leaking the full secret.
    private static string SafeAuthKeyPrefix(HermesProxy.Auth.AuthClient? authClient)
    {
        if (authClient == null) return "<null>";
        try
        {
            var key = authClient.GetSessionKey();
            if (key == null || key.Length == 0) return "<empty>";
            int n = Math.Min(4, key.Length);
            return Convert.ToHexString(key, 0, n);
        }
        catch
        {
            return "<error>";
        }
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

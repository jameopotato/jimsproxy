using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using HermesProxy.Enums;
using System.Numerics;
using Framework.Constants;
using Framework.Cryptography;
using Framework;
using Framework.IO;
using Framework.Logging;
using HermesProxy.World.Enums;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using Framework.Networking;
using HermesProxy.World.Server;
using HermesProxy.World; // JimsProxy: KnownBenignOpcodes
using System.Collections.Frozen;
using System.Diagnostics;

namespace HermesProxy.World.Client;

public partial class WorldClient
{
    Socket _clientSocket = null!;
    bool? _isSuccessful;
    uint _queuePosition;
    string _username = null!;
    Realm _realm = null!;
    LegacyWorldCrypt _worldCrypt = null!;
    FrozenDictionary<Opcode, Action<WorldPacket>> _packetHandlers = null!;
    GlobalSessionData _globalSession = null!;
    byte[] _authSessionKey = null!; //MIRASU: captured at connect time so we don't depend on GetSession().AuthClient surviving a realm swap
    readonly Lock _sendLock = new();
    Timer? _keepAliveTimer;
    uint _keepAlivePingSerial;
    const int KeepAliveIntervalMs = 30_000;

    // JimsProxy silent-stall watchdog: if the legacy server stops delivering
    // s2c traffic for this long while we're still sending keepalive pings,
    // declare the WorldClient stale and trigger the unplanned-reconnect path.
    // Bundle 20260505-131650 caught Twinstar going silent for 150s under
    // Stormwind crowd load -- no FIN, no RST, just no data flowing -- which
    // froze spell casts (waiting on SMSG_SPELL_GO that never came) while
    // movement still worked client-side via extrapolation. The unplanned-
    // reconnect path was wired but only fires on TCP RST/FIN; without a
    // disconnect signal it never triggered, leaving the user in a ghost
    // world until WoW itself crashed. 60s = 2 keepalive intervals + buffer;
    // anything shorter risks false positives on transient hiccups, anything
    // longer leaves the user staring at a frozen UI.
    const int SilentStallThresholdMs = 60_000;

    // JimsProxy: last-inbound-opcode tracking for disconnect diagnostics. When
    // the legacy connection dies unexpectedly (zombie state, AFK DC), capturing
    // the most recent server-to-proxy opcode + its arrival tick narrows down
    // whether the death was idle (no traffic in N seconds), correlated with a
    // specific opcode (parser bug), or mid-flight on a known packet.
    Opcode _lastInboundOpcode;
    uint _lastInboundOpcodeRaw;
    volatile int _lastInboundOpcodeTick;

    // JimsProxy (gap-A modern-death watchdog): flips true the first keepalive tick both modern
    // sockets (RealmSocket + InstanceSocket) are observed open. Guards the login/handshake window
    // (before the sockets are assigned to the session) so the death check never false-fires
    // mid-login -- the mirror of the silent-stall watchdog's `_lastInboundOpcodeTick != 0` guard.
    bool _modernClientWasAlive;

    // packet order is not always the same as new client, sometimes we need to delay packet until another one
    Dictionary<Opcode, List<WorldPacket>> _delayedPacketsToServer = null!;
    Dictionary<Opcode, List<ServerPacket>> _delayedPacketsToClient = null!;

    public WorldClient()
    {
        InitializePacketHandlers();
    }

    public GlobalSessionData GetSession()
    {
        return _globalSession;
    }

    public GlobalSessionData Session => _globalSession;

    public bool ConnectToWorldServer(Realm realm, GlobalSessionData globalSession)
    {
        _worldCrypt = null!;
        _realm = realm;
        _globalSession = globalSession;
        _username = globalSession.Username;
        _isSuccessful = null;
        _delayedPacketsToServer = new Dictionary<Opcode, List<WorldPacket>>();
        _delayedPacketsToClient = new Dictionary<Opcode, List<ServerPacket>>();

        //MIRASU: snapshot the realmd session key here. Realm-swap (PTR<->Live) can null out
        //        GetSession().AuthClient before Kronos sends AuthChallenge, causing a NRE in
        //        SendAuthResponse. Capturing now decouples the handshake from AuthClient lifetime.
        if (globalSession.AuthClient == null)
        {
            Log.Event("world.mangos.connect_no_authclient", new
            {
                realm_name = realm.Name,
                username = globalSession.Username,
            });
            Log.Print(LogType.Error, "ConnectToWorldServer: AuthClient is null on session, cannot derive realmd session key. Aborting world connect.");
            _isSuccessful = false;
            return false;
        }
        _authSessionKey = globalSession.AuthClient.GetSessionKey();
        if (_authSessionKey == null || _authSessionKey.Length == 0)
        {
            Log.Event("world.mangos.connect_empty_sessionkey", new
            {
                realm_name = realm.Name,
                username = globalSession.Username,
            });
            Log.Print(LogType.Error, "ConnectToWorldServer: realmd session key is empty. Aborting world connect.");
            _isSuccessful = false;
            return false;
        }

        Log.Print(LogType.Network, "Connecting to world server...");
        try
        {
            var ip = NetworkUtils.ResolveOrDirectIPv4(realm.ExternalAddress);
            Log.Print(LogType.Network, $"World Server address {realm.ExternalAddress}:{realm.Port} resolved as {ip}:{realm.Port}");
            // JimsProxy: structured event
            Log.Event("world.mangos.connect", new
            {
                host = realm.ExternalAddress,
                resolved_ip = ip.ToString(),
                port = (int)realm.Port,
                realm_name = realm.Name,
            });
            globalSession.GameState.ResetRttSmoothing();
            _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            // Connect to the specified host.
            var endPoint = new IPEndPoint(ip, realm.Port);
            _clientSocket.BeginConnect(endPoint, ConnectCallback, null);
        }
        catch (Exception ex)
        {
            Log.Print(LogType.Error, $"Socket Error: {ex.Message}");
            Log.Event("world.mangos.connect_error", new { error = ex.Message });
            _isSuccessful = false;
        }

        while (_isSuccessful == null)
        {
            Thread.Sleep(1);
        }

        return (bool)_isSuccessful;
    }

    public bool IsAuthenticated()
    {
        return _isSuccessful == true;
    }

    private void InitializeEncryption(byte[] sessionKey)
    {
        switch (Settings.ServerBuild)
        {
            case ClientVersionBuild.V1_12_1_5875:
            case ClientVersionBuild.V1_12_2_6005:
            case ClientVersionBuild.V1_12_3_6141:
                _worldCrypt = new VanillaWorldCrypt();
                break;
            case ClientVersionBuild.V2_4_3_8606:
                _worldCrypt = new TbcWorldCrypt();
                break;
        }

        if (_worldCrypt != null)
            _worldCrypt.Initialize(sessionKey);
    }

    public void Disconnect()
    {
        // JimsProxy (camp login-eviction merge): fail-open. A legacy disconnect
        // while the login stream is held must not strand the client with nothing —
        // flush whatever was captured, unmodified. Sends go straight to the
        // instance socket (not SendPacketToClientDirect): Disconnect can run on
        // teardown threads where that path's wait-for-instance-socket loop could
        // block, and if the modern side is already gone there is nobody to flush to.
        var heldOnDisconnect = GetSession()?.GameState?.LoginEvictionHold.TryReleaseAll();
        if (heldOnDisconnect != null)
        {
            var instanceSocket = GetSession()?.InstanceSocket;
            if (instanceSocket != null)
            {
                foreach (var held in heldOnDisconnect)
                    instanceSocket.SendPacket(held);
            }
            Log.Event("login.eviction_hold.flushed_fail_open", new
            {
                held_packets = heldOnDisconnect.Count,
                sent_to_client = instanceSocket != null,
            });
        }

        // JimsProxy (camp stun lock, step 2): same fail-open for control ops held by
        // the pre-create op hold.
        var heldOpsOnDisconnect = GetSession()?.GameState?.PreCreateOpHold.ReleaseAll();
        if (heldOpsOnDisconnect != null && heldOpsOnDisconnect.Count > 0)
        {
            var opsSocket = GetSession()?.InstanceSocket;
            if (opsSocket != null)
            {
                foreach (var heldOp in heldOpsOnDisconnect)
                    opsSocket.SendPacket(heldOp);
            }
            Log.Event("login.precreate_op_hold.flushed_fail_open", new
            {
                op_count = heldOpsOnDisconnect.Count,
                sent_to_client = opsSocket != null,
            });
        }

        // JimsProxy (worldentry root-ceremony breadcrumb): a client close is the
        // most common reaction to a movement lockup — flush the ceremony accounting
        // here so the final arrival's breadcrumb isn't lost with the session (the
        // other flush anchors are the NEXT login-verify / transfer, which never
        // come). Read-and-log only, no sends; idempotent (tracker resets on flush),
        // so racing Disconnect calls are safe.
        if (GetSession()?.GameState != null)
            FlushWorldEntryCeremony("disconnect");

        StopKeepAliveTimer();

        if (!IsConnected())
            return;

        // JimsProxy: teardown must not throw. A real FIN on the receive thread and the
        // silent-stall watchdog can race into Disconnect() on the same client; the second
        // Shutdown/Disconnect then throws SocketException/ObjectDisposedException and aborts
        // the caller's teardown path (matching the silent-stall path's own try/catch).
        try
        {
            _clientSocket.Shutdown(SocketShutdown.Both);
            _clientSocket.Disconnect(false);
        }
        catch (Exception ex)
        {
            Log.Event("session.worldclient.disconnect_error", new
            {
                exception_type = ex.GetType().Name,
                message = ex.Message,
            });
        }

        if (GetSession().WorldClient == this)
            GetSession().WorldClient = null;
    }

    public bool IsConnected()
    {
        return _clientSocket != null && _clientSocket.Connected;
    }

    public void SetNoDelay(bool enable)
    {
        _clientSocket?.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, enable);
    }

    public uint GetQueuePosition()
    {
        return _queuePosition;
    }

    private void ConnectCallback(IAsyncResult AR)
    {
        try
        {
            Log.Print(LogType.Network, "Connection established!");

            _clientSocket.EndConnect(AR);
            _clientSocket.ReceiveBufferSize = 65535;
            _clientSocket.NoDelay = true;

            _ = Task.Run(ReceiveLoop);
        }
        catch (Exception ex)
        {
            Log.Print(LogType.Error, $"Connect Error: {ex.Message}");
            if (_isSuccessful == null)
                _isSuccessful = false;
        }
    }

    private async Task<bool> ReceiveBufferFully(Memory<byte> bufferToFill)
    {
        int alreadyReceived = 0;

        while (alreadyReceived < bufferToFill.Length)
        {
            int received = await _clientSocket.ReceiveAsync(
                bufferToFill[alreadyReceived..],
                SocketFlags.None
            ).ConfigureAwait(false);
            
            if (received == 0)
                return false;

            alreadyReceived += received;
        }

        return true;
    }

    private readonly byte[] _headerBuffer = new byte[LegacyServerPacketHeader.StructSize];

    private void HandleDisconnect(string reason)
    {
        Log.PrintNet(LogType.Error, LogNetDir.S2P, $"Socket Closed By GameWorldServer ({reason})");
        if (_isSuccessful == null)
        {
            _isSuccessful = false;
        }
        else
        {
            // CompareExchange MUST run before Disconnect(). Disconnect() also nulls
            // session.WorldClient at line ~184 (if (GetSession().WorldClient == this)
            // GetSession().WorldClient = null;), so calling Disconnect() first leaves
            // CompareExchange comparing against a null field and reporting
            // wasActiveWorldClient=false even when this WorldClient WAS the active one.
            // The bug: legacy server closes a healthy connection → HandleDisconnect runs
            // → Disconnect() nulls the field → CompareExchange sees null → suppresses the
            // reconnect → modern client stays connected but every CMSG drops via the
            // c2s.dropped_during_disconnect path until the client surrenders ~50s later.
            // Issue #229 (BG-exit DC repro on Kronos V). The silent-stall watchdog at
            // SendKeepAlivePing already uses the correct order — this brings the
            // FIN/RST paths into consistency.
            //
            // JimsProxy realm-swap context: previously called GetSession().OnDisconnect()
            // here, which tore down the entire BNet session including any newly-created
            // WorldClient for a different realm during a swap. Now: only null our slot
            // if it's still pointing at us (a fresh swap may have already replaced it
            // with a new WorldClient — don't clobber that). Done via Interlocked.CompareExchange
            // so HandleDisconnect and the ReceiveLoop catch can't both pass the check on the
            // same TCP RST and race to spin up duplicate reconnects.
            var session = GetSession();
            bool wasActiveWorldClient = false;
            if (session != null)
            {
                var prior = Interlocked.CompareExchange(ref session.WorldClient, null, this);
                wasActiveWorldClient = ReferenceEquals(prior, this);
            }
            Disconnect();
            Log.Event("session.ondisconnect.suppressed", new
            {
                reason = "worldclient_legacy_disconnect",
                disconnect_reason = reason,
                last_opcode = _lastInboundOpcode.ToString(),
                last_opcode_raw = _lastInboundOpcodeRaw,
                ms_since_last_opcode = _lastInboundOpcodeTick == 0 ? -1 : Environment.TickCount - _lastInboundOpcodeTick,
                was_active_world_client = wasActiveWorldClient,
            });
            if (wasActiveWorldClient)
            {
                if (session!.IsLogoutIntentional())
                {
                    Log.Event("session.unplanned_reconnect.skipped_intentional_logout", new
                    {
                        disconnect_reason = reason,
                    });
                    session.PropagateUnplannedDcToModern(
                        Guid.NewGuid().ToString("N")[..8],
                        "intentional_logout");
                }
                else
                {
                    session.TryUnplannedReconnectAndPropagate(this);
                }
            }
        }
    }

    // JimsProxy (#450): a preemptive player attack stop armed by SMSG_PARTY_KILL_LOG is
    // normally released by the trailing killing-blow ATTACKER_STATE_UPDATE in the same
    // burst (see CombatHandler.HandlePartyKillLog). When no hit trails — spell killing
    // blows — release it as soon as the socket has no more buffered packets: same read
    // pass, sub-ms later than the old inline emit, and never before a hit that is
    // already sitting in the buffer. A mid-burst TCP segment boundary can drain early;
    // that only reproduces the old inline ordering, never anything worse.
    private void FlushPreemptAttackStopAtDrain()
    {
        var state = GetSession()?.GameState;
        if (state == null || state.PendingPreemptAttackStopVictim == default)
            return;
        if (!LegacySocketDrained())
            return;

        var victim = state.TakePreemptAttackStopForFlush();
        if (victim != default)
            SendPreemptAttackStop(victim, "drain");
    }

    // JimsProxy (fishing recast wedge): same drain rule for the held channel zero-update —
    // the previous bobber's teardown anchors arrive in the same read pass as the stale
    // zero-update or not at all, so drain is the release point for a genuine one.
    private void FlushHeldChannelZeroUpdateAtDrain()
    {
        var state = GetSession()?.GameState;
        if (state == null || (state.HeldLocalChannelZeroUpdate == null && !state.StaleBobberTeardownSeenThisPass))
            return;
        if (!LegacySocketDrained())
            return;
        ReleaseHeldChannelZeroUpdateAtDrain();
    }

    private bool LegacySocketDrained()
    {
        try
        {
            return _clientSocket.Available == 0;
        }
        catch
        {
            return true; // socket torn down — flush rather than leak what is armed
        }
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (true)
            {
                if (!await ReceiveBufferFully(_headerBuffer.AsMemory()))
                {
                    HandleDisconnect("header");
                    return;
                }

                if (_worldCrypt != null)
                    _worldCrypt.Decrypt(_headerBuffer, LegacyServerPacketHeader.StructSize);

                LegacyServerPacketHeader header = new();
                header.Read(_headerBuffer);
                ushort packetSize = header.Size;

                if (packetSize == 0)
                {
                    continue;
                }

                byte[] buffer = new byte[packetSize];

                // copy the opcode into the new buffer
                buffer[0] = _headerBuffer[2];
                buffer[1] = _headerBuffer[3];

                if (!await ReceiveBufferFully(buffer.AsMemory(2, packetSize - 2)))
                {
                    HandleDisconnect("payload");
                    return;
                }

                WorldPacket packet = new WorldPacket(buffer);
                packet.SetReceiveTime(Environment.TickCount);
                HandlePacket(packet);
                FlushPreemptAttackStopAtDrain();
                FlushHeldChannelZeroUpdateAtDrain();
            }
        }
        catch(Exception e)
        {
            Log.PrintNet(LogType.Error, LogNetDir.S2P, $"Packet Read Error: {e.Message}{Environment.NewLine}{e.StackTrace}");
            if (_isSuccessful == null)
                _isSuccessful = false;
            else
            {
                // Same ordering rule as HandleDisconnect: CompareExchange before
                // Disconnect() so Disconnect's internal null-assignment doesn't
                // falsely set wasActiveWorldClient=false. See HandleDisconnect's
                // comment for the full rationale (issue #229).
                var session = GetSession();
                bool wasActiveWorldClient = false;
                if (session != null)
                {
                    var prior = Interlocked.CompareExchange(ref session.WorldClient, null, this);
                    wasActiveWorldClient = ReferenceEquals(prior, this);
                }
                Disconnect();
                Log.Event("session.ondisconnect.suppressed", new
                {
                    reason = "worldclient_receive_loop_exception",
                    exception_type = e.GetType().Name,
                    exception_message = e.Message,
                    last_opcode = _lastInboundOpcode.ToString(),
                    last_opcode_raw = _lastInboundOpcodeRaw,
                    ms_since_last_opcode = _lastInboundOpcodeTick == 0 ? -1 : Environment.TickCount - _lastInboundOpcodeTick,
                    was_active_world_client = wasActiveWorldClient,
                });
                if (wasActiveWorldClient)
                {
                    if (session!.IsLogoutIntentional())
                    {
                        Log.Event("session.unplanned_reconnect.skipped_intentional_logout", new
                        {
                            exception_type = e.GetType().Name,
                            exception_message = e.Message,
                        });
                        session.PropagateUnplannedDcToModern(
                            Guid.NewGuid().ToString("N")[..8],
                            "intentional_logout");
                    }
                    else
                    {
                        int? socketErrorCode = (e is SocketException se) ? (int)se.SocketErrorCode : null;
                        session.TryUnplannedReconnectAndPropagate(this, e.GetType().Name, e.Message, socketErrorCode);
                    }
                }
            }
        }
    }

    // C P>S: Sends data to world server
    private void SendPacket(WorldPacket packet)
    {
        lock (_sendLock)
        {
            try
            {
                ByteBuffer buffer = new ByteBuffer();
                LegacyClientPacketHeader header = new LegacyClientPacketHeader();

                header.Size = (ushort)(packet.GetSize() + sizeof(uint)); // size includes the opcode
                header.Opcode = packet.GetOpcode();
                header.Write(buffer);

                Log.PrintNet(LogType.Debug, LogNetDir.P2S, $"Sending opcode {LegacyVersion.GetUniversalOpcode(header.Opcode)} ({header.Opcode}) with size {header.Size}.");

                byte[] headerArray = buffer.GetData();
                if (_worldCrypt != null)
                    _worldCrypt.Encrypt(headerArray, LegacyClientPacketHeader.StructSize);
                buffer.Clear();
                buffer.WriteBytes(headerArray);

                buffer.WriteBytes(packet.GetData(), packet.GetSize());

                _clientSocket.Send(buffer.GetData(), SocketFlags.None);
            }
            catch (Exception ex)
            {
                Log.PrintNet(LogType.Error, LogNetDir.P2S, $"Packet Write Error: {ex.Message}");
                if (_isSuccessful == null)
                    _isSuccessful = false;
            }
        }
    }

    public void SendPacketToClient(ServerPacket packet, Opcode delayUntilOpcode = Opcode.MSG_NULL_ACTION)
    {
        Opcode opcode = packet.GetUniversalOpcode();
        if (delayUntilOpcode != Opcode.MSG_NULL_ACTION)
        {
            if (_delayedPacketsToClient.ContainsKey(delayUntilOpcode))
                _delayedPacketsToClient[delayUntilOpcode].Add(packet);
            else
            {
                List<ServerPacket> packets = new List<ServerPacket>();
                packets.Add(packet);
                _delayedPacketsToClient.Add(delayUntilOpcode, packets);
            }
            return;
        }

        SendPacketToClientDirect(packet);
        SendDelayedPacketsToClientOnOpcode(opcode);
    }

    private void SendPacketToClientDirect(ServerPacket packet)
    {
        // JimsProxy (camp login-eviction merge): while an instanced login-verify is
        // held, every world packet queues behind it in arrival order (flushed by the
        // release sites in MovementHandler/UpdateHandler/Disconnect). Realm packets
        // are char-select traffic outside the world stream and pass through.
        if (packet.GetConnection() != ConnectionType.Realm &&
            GetSession().GameState.LoginEvictionHold.TryEnqueue(packet))
            return;

        var gameState = GetSession().GameState;
        var pendingPackets = gameState.PendingUninstancedPackets;
        var pendingLock = gameState.PendingUninstancedPacketsLock;
        if (packet.GetConnection() == ConnectionType.Realm)
        {
            GetSession().RealmSocket.SendPacket(packet);
        }
        else
        {
            if (GetSession().InstanceSocket == null &&
               !gameState.IsConnectedToInstance)
            {
                lock (pendingLock)
                {
                    if (GetSession().InstanceSocket == null &&
                        !gameState.IsConnectedToInstance)
                    {
                        pendingPackets.Enqueue(packet);
                        Log.PrintNet(LogType.Warn, LogNetDir.P2C, $"Can't send opcode {packet.GetUniversalOpcode()} ({packet.GetOpcode()}) before entering world! Queue");
                        return;
                    }
                }
            }

            // block these packets until connected to instance
            while (GetSession().InstanceSocket == null)
            {
                if (GetSession().IsInCharacterSelect)
                {
                    Log.PrintNet(LogType.Debug, LogNetDir.P2C, $"Dropping {packet.GetUniversalOpcode()} — session is at character select.");
                    return;
                }
                Log.PrintNet(LogType.Network, LogNetDir.P2C, $"Waiting to send {packet.GetUniversalOpcode()} ({packet.GetOpcode()}).");
                System.Threading.Thread.Sleep(200);
            }

            var socket = GetSession().InstanceSocket;
            if (pendingPackets.Count > 0)
            {
                lock (pendingLock)
                {
                    while (pendingPackets.TryDequeue(out var oldPacket))
                    {
                        socket.SendPacket(oldPacket);
                    }
                }
            }

            socket.SendPacket(packet);
        }
    }

    public void SendPacketToServer(WorldPacket packet, Opcode delayUntilOpcode = Opcode.MSG_NULL_ACTION)
    {
        Opcode opcode = packet.GetUniversalOpcode(false);
        if (delayUntilOpcode != Opcode.MSG_NULL_ACTION)
        {
            if (_delayedPacketsToServer.ContainsKey(delayUntilOpcode))
                _delayedPacketsToServer[delayUntilOpcode].Add(packet);
            else
            {
                List<WorldPacket> packets = new List<WorldPacket>();
                packets.Add(packet);
                _delayedPacketsToServer.Add(delayUntilOpcode, packets);
            }
            return;
        }

        SendPacket(packet);
        SendDelayedPacketsToServerOnOpcode(opcode);
    }

    private void SendDelayedPacketsToServerOnOpcode(Opcode opcode)
    {
        if (_delayedPacketsToServer.ContainsKey(opcode))
        {
            List<WorldPacket> packets = _delayedPacketsToServer[opcode];
            for (int i = packets.Count - 1; i >= 0; i--)
            {
                SendPacket(packets[i]);
                packets.RemoveAt(i);
            }
        }
    }

    private void SendDelayedPacketsToClientOnOpcode(Opcode opcode)
    {
        // Flush in FORWARD (arrival) order. The old reverse loop sent last-queued first, which
        // this queue's other users happen to tolerate -- the cooldown-histories delay
        // (SMSG_SEND_UNLEARN_SPELLS key) is always a single packet per flush, and the
        // collision-height delay (SMSG_UPDATE_OBJECT key) holds packets for distinct movers,
        // so order across them is irrelevant. (The #384 name-query delay lives on the
        // SERVER-bound queue, not this one.) But reverse order CORRUPTS an
        // order-sensitive multi-packet burst. #410 delays SMSG_SET_PROFICIENCY here: it's an
        // absolute cumulative mask -- the server emits one packet per proficiency carrying the
        // running total, so only the LAST (full) mask is correct and the client keeps the last
        // one it processes. Reversed, a Rogue collapsed to Leather-only armor / Sword1H-only
        // weapons (final mask = the FIRST, smallest one), wrongly reddening cloth/thrown/misc
        // items it can use. Detach the list before sending so a re-entrant flush can't double-send.
        if (_delayedPacketsToClient.TryGetValue(opcode, out var packets))
        {
            _delayedPacketsToClient.Remove(opcode);
            foreach (var packet in packets)
                SendPacketToClientDirect(packet);
        }
    }

    // JimsProxy (relogin name "Unknown"): the delayed-send queues live on the WorldClient
    // instance, so a CMSG_NAME_QUERY queued at char-select (delayUntil SMSG_LOGIN_VERIFY_WORLD)
    // is discarded when HandlePlayerLogin recreates the WorldClient on relogin — and the
    // player's own name then resolves to "Unknown" in target / target-of-target frames. Carry
    // the SERVER-bound queue (only name queries are ever delayed there) over to the new
    // instance so it flushes on the new login-verify. The client-bound queue is intentionally
    // NOT migrated: it holds world-state-specific ordering packets that must not cross a relogin.
    public void AdoptDelayedServerPacketsFrom(WorldClient previous)
    {
        if (previous == null || previous == this)
            return;
        foreach (var (opcode, packets) in previous._delayedPacketsToServer)
        {
            if (!_delayedPacketsToServer.TryGetValue(opcode, out var list))
                _delayedPacketsToServer[opcode] = list = new List<WorldPacket>();
            list.AddRange(packets);
        }
        previous._delayedPacketsToServer.Clear();
    }

    /// <summary>
    /// Opcodes the legacy server may legitimately send before SMSG_AUTH_RESPONSE
    /// which we don't (yet) translate. They arrive during the auth handshake
    /// and were previously setting _isSuccessful=false, killing the connection
    /// before SMSG_AUTH_RESPONSE had a chance to succeed.
    /// </summary>
    private static bool IsIgnorableDuringHandshake(Opcode op)
    {
        switch (op)
        {
            case Opcode.SMSG_WARDEN_DATA:       // Warden challenge (Kronos/Twinstar, even with ReportedOS=OSX)
                return true;
            default:
                return false;
        }
    }

    private void HandlePacket(WorldPacket packet)
    {
        Opcode universalOpcode = packet.GetUniversalOpcode(false);
        _lastInboundOpcode = universalOpcode;
        _lastInboundOpcodeRaw = packet.GetOpcode();
        _lastInboundOpcodeTick = Environment.TickCount;
        Log.PrintNet(LogType.Debug, LogNetDir.S2P, $"Received opcode {universalOpcode} ({packet.GetOpcode()}).");

        // JimsProxy: structured packet.in (s2c — from legacy server)
        uint packetSizeJP = packet.GetSize();
        uint rawOpcodeJP = packet.GetOpcode();
        bool hasHandlerJP =
            universalOpcode == Opcode.SMSG_AUTH_CHALLENGE ||
            universalOpcode == Opcode.SMSG_AUTH_RESPONSE ||
            universalOpcode == Opcode.SMSG_ADDON_INFO ||
            _packetHandlers.ContainsKey(universalOpcode);
        if (Settings.DebugOutput)
            Log.Event("packet.in", new
            {
                direction = "s2c",
                opcode_universal = universalOpcode.ToString(),
                opcode_raw = rawOpcodeJP,
                size = packetSizeJP,
                has_handler = hasHandlerJP,
            });

        long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            switch (universalOpcode)
            {
                case Opcode.SMSG_AUTH_CHALLENGE:
                    HandleAuthChallenge(packet);
                    break;
                case Opcode.SMSG_AUTH_RESPONSE:
                    HandleAuthResponse(packet);
                    break;
                case Opcode.SMSG_ADDON_INFO:
                    HandleAddonInfo(packet);
                    break;
                default:
                    if (_packetHandlers.ContainsKey(universalOpcode))
                    {
                        _packetHandlers[universalOpcode](packet);
                    }
                    else if (KnownBenignOpcodes.IsModernOnly(universalOpcode))
                    {
                        // Modern-only subsystem — drop silently, no handshake taint.
                        Log.Event("packet.ignored", new
                        {
                            direction = "s2c",
                            opcode_universal = universalOpcode.ToString(),
                            opcode_raw = rawOpcodeJP,
                            size = packetSizeJP,
                            reason = "modern_only",
                        });
                    }
                    else
                    {
                        // JimsProxy: don't fail the handshake on ignorable opcodes
                        // (e.g. SMSG_WARDEN_DATA on Kronos). The upstream logic set
                        // _isSuccessful=false for ANY unknown opcode arriving before
                        // SMSG_AUTH_RESPONSE, which kills the connection before auth
                        // can complete.
                        Log.PrintNet(LogType.Warn, LogNetDir.S2P, $"No handler for opcode {universalOpcode} ({packet.GetOpcode()}) (Got unknown packet from WorldServer)");
                        Log.Event("packet.untranslated", new
                        {
                            direction = "s2c",
                            opcode_universal = universalOpcode.ToString(),
                            opcode_raw = rawOpcodeJP,
                            size = packetSizeJP,
                        });
                        if (_isSuccessful == null && !IsIgnorableDuringHandshake(universalOpcode))
                            _isSuccessful = false;
                    }
                    break;
            }

            long elapsedTicks = Stopwatch.GetElapsedTime(startTimestamp).Ticks;
            if (HermesProxy.Server.MetricsEnabled)
                HermesProxy.Server.Metrics.RecordServerToClientLatency(universalOpcode, elapsedTicks);

            if (hasHandlerJP && Settings.DebugOutput)
            {
                Log.Event("packet.translated", new
                {
                    direction = "s2c",
                    opcode_universal = universalOpcode.ToString(),
                    opcode_raw = rawOpcodeJP,
                    duration_us = elapsedTicks / (TimeSpan.TicksPerMillisecond / 1000),
                });
            }
        }
        catch (Exception exJP)
        {
            Log.Event("packet.error", new
            {
                direction = "s2c",
                opcode_universal = universalOpcode.ToString(),
                opcode_raw = rawOpcodeJP,
                exception_type = exJP.GetType().FullName,
                message = exJP.Message,
                stack_first_line = exJP.StackTrace?.Split('\n')[0]?.Trim(),
            });
            throw;
        }

        SendDelayedPacketsToServerOnOpcode(universalOpcode);
    }

    // Diagnostic-only handler for SMSG_ADDON_INFO. Parses the server's response
    // to the addon section we sent in CMSG_AUTH_SESSION and emits a structured
    // event so bug bundles capture the round-trip. Modern client never sees
    // this opcode — it's already in IsIgnorableDuringHandshake.
    private void HandleAddonInfo(WorldPacket packet)
    {
        // GetData() returns the full WorldPacket buffer, opcode at [0..1] and the
        // body at [2..]. The 2-byte opcode was consumed by WorldPacket(byte[]).ctor.
        byte[] body = packet.GetData();
        const int OpcodeHeaderLen = 2;
        int bodyLen = body.Length - OpcodeHeaderLen;
        if (bodyLen <= 0)
        {
            Log.Event("auth.addon_info.empty", new { });
            return;
        }
        byte[] payload = new byte[bodyLen];
        Buffer.BlockCopy(body, OpcodeHeaderLen, payload, 0, bodyLen);

        byte[]? receivedFlags = AuthSessionAddons.ParseAddonInfoResponse(payload);
        if (receivedFlags == null)
        {
            Log.Event("auth.addon_info.parse_failed", new
            {
                payload_size = payload.Length,
            });
            return;
        }

        byte[] derivedFlags = AuthSessionAddons.Derive();
        bool mismatch = false;
        for (int i = 0; i < 4; i++)
        {
            if (receivedFlags[i] != derivedFlags[i]) { mismatch = true; break; }
        }

        if (mismatch)
        {
            // Server's flag-field validation rejected our bytes and pushed
            // replacement values. Shouldn't happen given Derive()'s clamping;
            // if it does, investigate Derive() logic.
            Log.Event("auth.addon_info.server_override", new
            {
                derived = BitConverter.ToUInt32(derivedFlags, 0),
                server = BitConverter.ToUInt32(receivedFlags, 0),
            });
        }
        else
        {
            Log.Event("auth.addon_info.echoed_match", new
            {
                flags = BitConverter.ToUInt32(receivedFlags, 0),
            });
        }
    }

    private void HandleAuthChallenge(WorldPacket packet)
    {
        if (Settings.ServerBuild >= ClientVersionBuild.V3_3_5a_12340)
        {
            uint one = packet.ReadUInt32();
        }

        uint seed = packet.ReadUInt32();

        if (Settings.ServerBuild >= ClientVersionBuild.V3_3_5a_12340)
        {
            BigInteger seed1 = packet.ReadBytes(16).ToBigInteger();
            BigInteger seed2 = packet.ReadBytes(16).ToBigInteger();
        }

        var rand = System.Security.Cryptography.RandomNumberGenerator.Create();
        byte[] bytes = new byte[4];
        rand.GetBytes(bytes);
        BigInteger ourSeed = bytes.ToBigInteger();

        SendAuthResponse((uint)ourSeed, seed);
    }

    public void SendAuthResponse(uint clientSeed, uint serverSeed)
    {
        uint zero = 0;

        byte[] authResponse = HashAlgorithm.SHA1.Hash
        (
            Encoding.ASCII.GetBytes(_username.ToUpper()),
            BitConverter.GetBytes(zero),
            BitConverter.GetBytes(clientSeed),
            BitConverter.GetBytes(serverSeed),
            _authSessionKey //MIRASU: was GetSession().AuthClient.GetSessionKey() — captured in ConnectToWorldServer to survive realm swaps
        );

        WorldPacket packet = new WorldPacket(Opcode.CMSG_AUTH_SESSION);
        packet.WriteUInt32((uint)Settings.ServerBuild);
        packet.WriteUInt32(_realm.Id.Index);
        packet.WriteBytes(_username.ToUpper().ToCString());

        if (Settings.ServerBuild >= ClientVersionBuild.V3_0_2_9056)
            packet.WriteUInt32(zero); // LoginServerType

        packet.WriteUInt32(clientSeed);

        if (Settings.ServerBuild >= ClientVersionBuild.V3_3_5a_12340)
        {
            packet.WriteUInt32(_realm.Id.Region);
            packet.WriteUInt32(_realm.Id.Site);
            packet.WriteUInt32(_realm.Id.Index);
        }

        if (Settings.ServerBuild >= ClientVersionBuild.V3_2_0_10192)
            packet.WriteUInt64(zero); // DosResponse

        packet.WriteBytes(authResponse);

        // Build the addon-data section programmatically from the canonical
        // Blizzard_* addon records (replaces the old hardcoded byte literal
        // captured from a wire trace). See AuthSessionAddons for the wire
        // format and the server quirk that requires non-zero flag bytes.
        byte[] flagBytes = AuthSessionAddons.Derive();
        byte[] addonBytes = AuthSessionAddons.BuildAddonAuthSection(flagBytes);
        packet.WriteBytes(addonBytes);

        // MIRASU (Kronos parser 2026-05-23): Kronos's _HandleAuthSession reads
        // one byte past the addon section (locale or similar trailing field).
        // Without it, every login produces a server-side ByteBufferException
        // "pos N size N value with size: 1" — non-fatal (auth still succeeds)
        // but spams the server log. vmangos's parser stops at the digest read
        // and never touches the addon section in WorldSocket, so the extra
        // trailing byte is unread leftover and ignored. Vanilla-path only;
        // TBC+ already writes its own trailing fields.
        if (Settings.ServerBuild < ClientVersionBuild.V2_0_1_6180)
            packet.WriteUInt8(0);

        Log.Event("auth.addon_section.sent", new
        {
            flags_uint32 = BitConverter.ToUInt32(flagBytes, 0),
        });

        SendPacket(packet);

        InitializeEncryption(_authSessionKey); //MIRASU: was GetSession().AuthClient.GetSessionKey()
    }

    private void HandleAuthResponse(WorldPacket packet)
    {
        AuthResult result = (AuthResult)packet.ReadUInt8();

        // Billing/expansion fields are only present on the *first* SMSG_AUTH_RESPONSE
        // (the full one). CMaNGOS/VMaNGOS-style 1.12 servers send subsequent
        // queue-update packets in a stripped form: just uint8(result) + uint32(position).
        // If a Kronos build sends billing on every packet, this branch will read
        // the wrong bytes — flag this if launch-day logs show queue positions
        // jumping into the millions.
        if (_isSuccessful == null)
        {
            uint billingTimeRemaining = packet.ReadUInt32();
            byte billingFlags = packet.ReadUInt8();
            uint billingTimeRested = packet.ReadUInt32();

            if (Settings.ServerBuild >= ClientVersionBuild.V2_0_1_6180)
            {
                byte expansion = packet.ReadUInt8();
            }
        }

        if (result == AuthResult.AUTH_OK)
        {
            Log.Print(LogType.Network, "Authentication succeeded!");
            // Race fix: previously _queuePosition reset and WaitQueueFinish were
            // BOTH gated on RealmSocket != null. If Kronos released us from queue
            // before the modern client's EnterEncryptedModeAck arrived (which is
            // what sets RealmSocket), _queuePosition stayed at the stale value
            // and the next SendAuthResponse(Ok, GetQueuePosition()) embedded a
            // bogus WaitInfo — modern client showed permanent queue UI with no
            // follow-up WaitQueueFinish to dismiss it.
            //
            // Now: always reset _queuePosition. Only send WaitQueueFinish if
            // RealmSocket exists (meaning the AuthResponse was already sent and
            // queue UI may already be on screen). If RealmSocket is null, the
            // deferred AuthResponse will go out with queuePos=0 and no queue UI
            // is ever shown — no Finish needed.
            bool wasQueued = _queuePosition != 0;
            _queuePosition = 0;
            if (wasQueued)
            {
                var realmSocket = GetSession().RealmSocket;
                Log.Event("auth.queue.released", new
                {
                    had_realm_socket = realmSocket != null,
                });
                realmSocket?.SendAuthWaitQue(0);
            }
            _isSuccessful = true;
            StartKeepAliveTimer();
            // JimsProxy m_OverSpeedPings antiflood fix:
            // Replaced 3-pings-at-login burst with a single immediate ping. vmangos-
            // family servers (Twinstar, Kronos) flag any ping arriving <27s after the
            // prior one as an "over-speed ping" via m_OverSpeedPings counter. The
            // original 3 probes 1s apart added 2 over-speed strikes per session,
            // accumulating toward the kick threshold across hours. Stacked with the
            // doubled-forward bug (also fixed in this PR — see WorldSocket.cs
            // CMSG_PING handler) this is what was triggering "Socket Closed By
            // GameWorldServer (header)" 1-2 hours into a session. One immediate
            // probe still seeds RTT 30s ahead of the first keepalive (preserves
            // issue #43 adaptive-fire-offset convergence) without tripping the
            // counter — this single probe is the first ping on the connection so
            // there's no prior to be over-speed against.
            SendKeepAlivePing(null);
        }
        else if (result == AuthResult.AUTH_WAIT_QUEUE)
        {
            _queuePosition = packet.ReadUInt32();
            Log.Print(LogType.Network, $"Position in queue is {_queuePosition}.");
            bool isInitial = _isSuccessful == null;
            var realmSocket = GetSession().RealmSocket;
            Log.Event("auth.queue.position", new
            {
                position = _queuePosition,
                is_initial = isInitial,
                had_realm_socket = realmSocket != null,
            });
            if (!isInitial)
                realmSocket?.SendAuthWaitQue(_queuePosition);
            _isSuccessful = true;
        }
        else
        {
            Log.Print(LogType.Network, "Authentication failed!");
            _isSuccessful = false;
        }
    }

    public void SendPing(uint ping, uint latency)
    {
        if (!IsConnected() || _isSuccessful == false)
            return;

        WorldPacket packet = new WorldPacket(Opcode.CMSG_PING);
        packet.WriteUInt32(ping);
        packet.WriteUInt32(latency);
        SendPacket(packet);
        GetSession().GameState?.RecordPingSent(ping);
    }

    private void StartKeepAliveTimer()
    {
        _keepAliveTimer = new Timer(SendKeepAlivePing, null, KeepAliveIntervalMs, KeepAliveIntervalMs);
    }

    private void StopKeepAliveTimer()
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
    }

    // JimsProxy (gap-A): a modern-client socket counts as dead if it's null (already torn out of
    // the session on close) or no longer open. Defensive try/catch -- a socket disposed in the
    // close race can throw on the Connected probe; treat that as dead too.
    private static bool IsModernSocketDead(WorldSocket? socket)
    {
        if (socket == null)
            return true;
        try { return !socket.IsOpen(); }
        catch { return true; }
    }

    private void SendKeepAlivePing(object? state)
    {
        // Race guard: the timer's queued callback can still execute briefly
        // after Disconnect() / StopKeepAliveTimer() races with it. Bail
        // before we try to ping or trip the watchdog on a corpse.
        if (!IsConnected() || _isSuccessful == false)
            return;

        // JimsProxy (T1 guaranteed-closure companion): pump the cast watchdog on the keepalive
        // tick so an orphaned cast (server sent no GO / CAST_FAILED / SPELL_FAILURE) still gets
        // its synthetic closure when the player goes idle. The existing watchdog otherwise only
        // runs on the next inbound spell packet, so a truly idle orphan leaks until the next cast.
        // The 2.5s per-cast deadline and the eviction logic are unchanged — this only adds a pump
        // cadence (≤ one keepalive interval late). Gated with the T1 bundle so OFF is byte-identical.
        // RunWatchdogEviction is safe to call cross-thread: it takes PendingCastsLock, and returns
        // without touching the queues when nothing is overdue.
        if (Framework.Settings.IdentityPinnedCastIdsActive)
            GetSession()?.RunWatchdogEviction();

        // JimsProxy (gap-A modern-death watchdog): the exact mirror of the silent-stall watchdog
        // below, in the opposite direction. If the MODERN client dies abruptly (hard crash /
        // taskkill / network partition) it never sends CMSG_LOG_DISCONNECT, so nothing tears down
        // this legacy WorldClient -- it pings Kronos every 30s forever and the character is a ghost
        // in-world (raid slot held, heals wasted) until the player relogs. Once we've seen the
        // modern client alive, if BOTH its sockets are gone and it wasn't an intentional logout, we
        // tear the legacy side down so the server logs the character out.
        //
        // Swap-safe by construction: a realm/char switch re-establishes the modern sockets in well
        // under a second (far inside this 30s tick), so a coarse tick never sees a mid-switch
        // transient as a death; and a switch always sets IsLogoutIntentional, gating out even a tick
        // that landed in that sub-second window. Unlike the silent-stall path this does NOT
        // reconnect -- the player is gone, so we set the intentional flag first to suppress the
        // auto-reconnect that HandleDisconnect would otherwise trigger.
        {
            var deathSession = GetSession();
            if (deathSession != null)
            {
                bool modernGone = IsModernSocketDead(deathSession.RealmSocket)
                               && IsModernSocketDead(deathSession.InstanceSocket);
                if (!modernGone)
                {
                    _modernClientWasAlive = true;
                }
                else if (ModernDeathWatchdog.ShouldTearDown(
                             _modernClientWasAlive, modernGone,
                             deathSession.IsLogoutIntentional(),
                             ReferenceEquals(deathSession.WorldClient, this)))
                {
                    Log.Event("session.modern_client_death.detected", new
                    {
                        keepalive_serial = _keepAlivePingSerial,
                        had_realm_socket = deathSession.RealmSocket != null,
                        had_instance_socket = deathSession.InstanceSocket != null,
                    });
                    deathSession.SetLogoutIntentional();                                  // suppress HandleDisconnect's auto-reconnect
                    Interlocked.CompareExchange(ref deathSession.WorldClient, null, this); // drop the slot (defense in depth)
                    try { Disconnect(); }
                    catch (Exception ex)
                    {
                        Log.Event("session.modern_client_death.disconnect_error", new
                        {
                            exception_type = ex.GetType().Name,
                            message = ex.Message,
                        });
                    }
                    StopKeepAliveTimer();
                    // Guarded like the Disconnect() above: AuthClient.Disconnect does a raw
                    // Shutdown/Disconnect behind a stale Socket.Connected check, so a socket
                    // concurrently reset/disposed by another teardown path throws — and an
                    // unhandled exception on this Timer ThreadPool callback would kill the
                    // whole process instead of logging one ghost character out.
                    try { deathSession.AuthClient?.Disconnect(); }                        // stop pinging Kronos -> server logs the char out
                    catch (Exception ex)
                    {
                        Log.Event("session.modern_client_death.auth_disconnect_error", new
                        {
                            exception_type = ex.GetType().Name,
                            message = ex.Message,
                        });
                    }
                    return;                                                               // do NOT fall through to the silent-stall reconnect
                }
            }
        }

        // JimsProxy silent-stall watchdog: before sending the next keepalive
        // ping, check whether the legacy server has gone silent on us. The
        // existing reconnect path (HandleDisconnect / receive-loop catch)
        // only fires on TCP RST/FIN; if Twinstar just stops sending data
        // without closing, no disconnect ever fires and the player sits in
        // a frozen ghost world. We piggyback on the keepalive timer rather
        // than a dedicated watchdog timer to avoid extra timer churn -- if
        // pings are firing, we're alive enough to check.
        //
        // Skip when _lastInboundOpcodeTick is still 0 (no s2c packets ever
        // received -- mid-handshake, treat as alive).
        if (_lastInboundOpcodeTick != 0)
        {
            int elapsedMs = Environment.TickCount - _lastInboundOpcodeTick;
            if (elapsedMs > SilentStallThresholdMs)
            {
                Log.Event("session.worldclient.silent_stall_detected", new
                {
                    ms_since_last_inbound = elapsedMs,
                    threshold_ms = SilentStallThresholdMs,
                    last_opcode = _lastInboundOpcode.ToString(),
                    last_opcode_raw = _lastInboundOpcodeRaw,
                    keepalive_serial = _keepAlivePingSerial,
                });

                // Mirror the receive-loop disconnect path: take the slot if
                // we still own it, then trigger the existing reconnect logic.
                // CompareExchange so a racing HandleDisconnect on a real
                // FIN arriving in the same window can't double-fire the
                // reconnect (TryUnplannedReconnectAndPropagate also has its
                // own CAS guard, but defense in depth is cheap).
                var session = GetSession();
                bool wasActiveWorldClient = false;
                if (session != null)
                {
                    var prior = Interlocked.CompareExchange(ref session.WorldClient, null, this);
                    wasActiveWorldClient = ReferenceEquals(prior, this);
                }
                try { Disconnect(); }
                catch (Exception ex)
                {
                    Log.Event("session.worldclient.silent_stall_disconnect_error", new
                    {
                        exception_type = ex.GetType().Name,
                        message = ex.Message,
                    });
                }
                StopKeepAliveTimer();
                if (wasActiveWorldClient && session != null)
                {
                    session.TryUnplannedReconnectAndPropagate(
                        this,
                        originalExceptionType: "SilentStall",
                        originalExceptionMessage: $"No s2c packets for {elapsedMs}ms (threshold {SilentStallThresholdMs}ms)",
                        originalSocketErrorCode: null);
                }
                return;
            }
        }

        uint serial = Interlocked.Increment(ref _keepAlivePingSerial);
        SendPing(serial | 0x80000000, 0);
    }

    private void SendRttProbes()
    {
        for (int i = 0; i < 3; i++)
            new Timer(_ => SendKeepAlivePing(null), null, i * 1000, Timeout.Infinite);
    }

    public void InitializePacketHandlers()
    {
        Dictionary<Opcode, Action<WorldPacket>> dict = [];

        foreach (var methodInfo in typeof(WorldClient).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            foreach (var msgAttr in methodInfo.GetCustomAttributes<PacketHandlerAttribute>())
            {
                if (msgAttr == null)
                    continue;

                if (msgAttr.Opcode == Opcode.MSG_NULL_ACTION)
                    continue;

                if (dict.ContainsKey(msgAttr.Opcode))
                {
                    Log.Print(LogType.Error, $"Tried to override OpcodeHandler of {_packetHandlers[msgAttr.Opcode]} with {methodInfo.Name} (Opcode {msgAttr.Opcode})");
                    continue;
                }

                var parameters = methodInfo.GetParameters();
                if (parameters.Length == 0)
                {
                    Log.Print(LogType.Error, $"Method: {methodInfo.Name} Has no parameters");
                    continue;
                }

                if (parameters[0].ParameterType != typeof(WorldPacket))
                {
                    Log.Print(LogType.Error, $"Method: {methodInfo.Name} has wrong BaseType");
                    continue;
                }

                var del = (Action<WorldPacket>)Delegate.CreateDelegate(typeof(Action<WorldPacket>), this, methodInfo);

                dict[msgAttr.Opcode] = del;
            }
        }

        _packetHandlers = dict.ToFrozenDictionary();

        // JimsProxy: report handler count for s2c dispatch
        Log.Event("handlers.registered.s2c", new
        {
            count = _packetHandlers.Count,
        });
    }
}

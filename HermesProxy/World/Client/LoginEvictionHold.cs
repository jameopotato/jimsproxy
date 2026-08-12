using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Client;

// JimsProxy (camp instance-reset bad login): hold-and-merge state machine for
// instanced-map logins.
//
// Kronos denies an over-cap instance re-entry as a two-step: SMSG_LOGIN_VERIFY_WORLD
// placing the player at their stale inside-instance position, then ~10ms later
// SMSG_TRANSFER_PENDING + SMSG_NEW_WORLD evicting them to the continent. The 1.14
// client cannot follow a login-load that is immediately aborted by a transfer — the
// login loading screen never dismisses (its nested loading-screen enable is never
// unwound), even though the client fully processes the transfer underneath. The
// healthy post-eviction relogin (a single clean login straight to the continent)
// proves the merged shape works.
//
// So: when a login-verify names an instanced map, the client-facing world stream is
// held here in arrival order. Release is strictly packet-driven — no timers:
//   - TRANSFER_PENDING arrives  -> swallowed client-side, phase = TransferSeen
//   - NEW_WORLD arrives         -> merge: the held login-verify is rewritten to the
//                                  transfer's destination and the queue is flushed;
//                                  the caller answers the server's transfer handshake
//                                  (MSG_MOVE_WORLDPORT_ACK) itself
//   - first UPDATE_OBJECT       -> healthy login, queue flushed unmodified
//   - legacy disconnect         -> fail-open, queue flushed unmodified
//
// This class is pure state — no sockets, no logging. Callers (the WorldClient
// legacy handlers) perform the sends and emit the events, which keeps every
// transition unit-testable.
public sealed class LoginEvictionHold
{
    public enum HoldPhase { Inactive, Holding, TransferSeen }

    private readonly Lock _lock = new();
    private HoldPhase _phase = HoldPhase.Inactive;
    private readonly List<ServerPacket> _queue = new();
    private LoginVerifyWorld? _heldVerify;
    private WorldServerInfo? _heldServerInfo;
    private long _startTick;
    private uint _loginMapId;
    private uint _pendingDestinationMapId;

    public HoldPhase Phase { get { lock (_lock) return _phase; } }
    public int QueuedCount { get { lock (_lock) return _queue.Count; } }
    public long StartTick { get { lock (_lock) return _startTick; } }
    public uint LoginMapId { get { lock (_lock) return _loginMapId; } }
    public uint PendingDestinationMapId { get { lock (_lock) return _pendingDestinationMapId; } }

    // Pure gate. The hold only ever arms for a fresh login (not a mid-session or
    // seamless-reconnect verify — IsInWorld is already true there) that names an
    // instanced map. Continent logins (maps 0/1) can't be the doomed half of an
    // eviction, and holding them would tax every ordinary login.
    public static bool ShouldBegin(bool enabled, bool alreadyInWorld, uint loginMapId)
    {
        return enabled && !alreadyInWorld && loginMapId > 1;
    }

    // Arm the hold. Must run BEFORE the login-verify is handed to the send path so
    // the verify itself lands at the head of the queue. The reference is kept so a
    // merge can rewrite its destination in place (packets serialize at send time).
    public void Begin(LoginVerifyWorld verify, long nowTick)
    {
        lock (_lock)
        {
            _phase = HoldPhase.Holding;
            _queue.Clear();
            _heldVerify = verify;
            _heldServerInfo = null;
            _startTick = nowTick;
            _loginMapId = verify.MapID;
            _pendingDestinationMapId = 0;
        }
    }

    // The WorldServerInfo built alongside the login-verify carries instance
    // difficulty fields; register it so a merge can rewrite them to match the
    // destination map. No-op when the hold isn't armed.
    public void RegisterWorldServerInfo(WorldServerInfo info)
    {
        lock (_lock)
        {
            if (_phase != HoldPhase.Inactive)
                _heldServerInfo = info;
        }
    }

    // Send-path hook: while the hold is armed every world packet queues behind the
    // held login-verify in arrival order. Returns false once released so the same
    // call site sends directly for the rest of the session.
    public bool TryEnqueue(ServerPacket packet)
    {
        lock (_lock)
        {
            if (_phase == HoldPhase.Inactive)
                return false;
            _queue.Add(packet);
            return true;
        }
    }

    // A transfer arriving during the hold is the eviction announcing itself.
    // Returns true when the caller must swallow the packet client-side.
    public bool OnTransferPending(uint destinationMapId)
    {
        lock (_lock)
        {
            if (_phase == HoldPhase.Inactive)
                return false;
            _phase = HoldPhase.TransferSeen;
            _pendingDestinationMapId = destinationMapId;
            return true;
        }
    }

    // The client never saw the swallowed TRANSFER_PENDING, so an abort must be
    // swallowed too. Drops back to plain holding: the original login stands and
    // the first UPDATE_OBJECT releases it as a healthy login.
    public bool OnTransferAborted()
    {
        lock (_lock)
        {
            if (_phase != HoldPhase.TransferSeen)
                return false;
            _phase = HoldPhase.Holding;
            _pendingDestinationMapId = 0;
            return true;
        }
    }

    // NEW_WORLD during the hold: rewrite the held login-verify (and the difficulty
    // fields of the held WorldServerInfo) to the transfer's destination — always
    // from the payload, never hardcoded; different dungeons evict to map 0 or 1 —
    // and hand the queue back for an in-order flush. Also accepts a bare NEW_WORLD
    // without a preceding TRANSFER_PENDING (defensive: it still names where the
    // server is putting the player). Returns null when no hold is armed.
    public List<ServerPacket>? TryMergeOnNewWorld(uint mapId, Vector3 position, float orientation)
    {
        lock (_lock)
        {
            if (_phase == HoldPhase.Inactive || _heldVerify == null)
                return null;
            _heldVerify.MapID = mapId;
            _heldVerify.Pos.X = position.X;
            _heldVerify.Pos.Y = position.Y;
            _heldVerify.Pos.Z = position.Z;
            _heldVerify.Pos.Orientation = orientation;
            if (_heldServerInfo != null)
            {
                _heldServerInfo.DifficultyID = mapId > 1 ? 1u : 0u;
                _heldServerInfo.InstanceGroupSize = mapId > 1 ? 5u : null;
            }
            return Deactivate();
        }
    }

    // First object update while plainly holding = no eviction is coming; release.
    // While TransferSeen the transfer wins: on the wire the destination's creates
    // never precede its NEW_WORLD, and releasing here would hand the client the
    // doomed instance load this hold exists to prevent.
    public List<ServerPacket>? TryReleaseOnFirstUpdateObject()
    {
        lock (_lock)
        {
            if (_phase != HoldPhase.Holding)
                return null;
            return Deactivate();
        }
    }

    // Fail-open release from any armed phase (legacy disconnect / teardown).
    public List<ServerPacket>? TryReleaseAll()
    {
        lock (_lock)
        {
            if (_phase == HoldPhase.Inactive)
                return null;
            return Deactivate();
        }
    }

    private List<ServerPacket> Deactivate()
    {
        _phase = HoldPhase.Inactive;
        var packets = new List<ServerPacket>(_queue);
        _queue.Clear();
        _heldVerify = null;
        _heldServerInfo = null;
        return packets;
    }
}

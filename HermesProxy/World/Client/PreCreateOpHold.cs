using System.Collections.Generic;
using System.Threading;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Client;

// JimsProxy (camp instance-reset stun lock, step 2): pre-create self-op hold.
//
// The wedge login's turn+cast lock is an ORDERING defect (Mirasu R56, wire-confirmed
// 13/13 on our own captures): the server's self-create is stalled ~600-800ms, so the
// arrival control ops (SMSG_MOVE_ROOT / SMSG_CONTROL_UPDATE / SMSG_MOVE_UNROOT) —
// sent at their normal time — reach the client BEFORE the player object exists. The
// client then constructs the player in a control-lost state and bakes the lock in at
// construction; no later flag value can clear it (server's own stun-clear, our create
// strip, /reload all fail — only relog re-constructs). Every healthy entry in the
// corpus (logins AND transfers) delivers creates before control ops.
//
// So: enforce the healthy order. From login-verify until the first self create block
// forwards, self-addressed control ops are held here in arrival order; the caller
// releases them immediately after the create's UPDATE_OBJECT (and its aura updates)
// go out. Zero-cost on healthy logins — their ops already arrive after the create, so
// nothing is ever held. Release is strictly packet-driven — no timers:
//   - first self create block forwarded  -> release in order (the fix)
//   - login failed                       -> discard (no world to send into)
//   - legacy disconnect                  -> fail-open flush
//
// The wedge cannot be detected before the ops arrive (login.stuck_stun fires AT the
// create — after the ops passed) and the step-1 merge doesn't cover no-transfer wedge
// logins, so this is a general per-login rule, not a wedge-gated one.
//
// Pure state — no sockets, no logging. Callers (the WorldClient legacy handlers)
// perform the sends and emit the events, keeping every transition unit-testable.
public sealed class PreCreateOpHold
{
    public enum HoldPhase { Inactive, Armed, ReleasePending }

    private readonly Lock _lock = new();
    private HoldPhase _phase = HoldPhase.Inactive;
    private readonly List<ServerPacket> _held = new();
    private long _armTick;

    public HoldPhase Phase { get { lock (_lock) return _phase; } }
    public int HeldCount { get { lock (_lock) return _held.Count; } }
    public long ArmTick { get { lock (_lock) return _armTick; } }

    // Pure gate. Arms on every fresh login (map-agnostic — direct wedge logins land
    // on continents too); never on a seamless-reconnect verify (client already in
    // world, its player object already constructed).
    public static bool ShouldArm(bool enabled, bool alreadyInWorld)
    {
        return enabled && !alreadyInWorld;
    }

    public void Arm(long nowTick)
    {
        lock (_lock)
        {
            _phase = HoldPhase.Armed;
            _held.Clear();
            _armTick = nowTick;
        }
    }

    // Capture a self-addressed control op arriving before the self create. The
    // caller has already checked the mover is the local player. Returns false once
    // released/inactive so the same call site sends directly.
    public bool TryCapture(ServerPacket packet)
    {
        lock (_lock)
        {
            if (_phase != HoldPhase.Armed)
                return false;
            _held.Add(packet);
            return true;
        }
    }

    // The first self create block this login is being translated — the actual flush
    // happens at the end of the enclosing UPDATE_OBJECT's processing, after the
    // create (and its aura updates) have been sent. No-op when not armed.
    public void NoteSelfCreateForwarding()
    {
        lock (_lock)
        {
            if (_phase == HoldPhase.Armed)
                _phase = HoldPhase.ReleasePending;
        }
    }

    // End of the UPDATE_OBJECT that carried the self create: hand back the held ops
    // for an in-order flush. Null unless a release is actually pending; may be an
    // empty list (healthy login — armed, nothing was ever held).
    public List<ServerPacket>? TakeForRelease()
    {
        lock (_lock)
        {
            if (_phase != HoldPhase.ReleasePending)
                return null;
            return Deactivate();
        }
    }

    // Fail-open release from any armed phase (legacy disconnect / login failed).
    public List<ServerPacket>? ReleaseAll()
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
        var packets = new List<ServerPacket>(_held);
        _held.Clear();
        return packets;
    }
}

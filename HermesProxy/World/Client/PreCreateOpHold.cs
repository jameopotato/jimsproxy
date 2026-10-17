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
// JimsProxy (P1-2, D-06): the same release point also carries the proxy's own self
// packets that are BUILT while the self create is being translated (the deferred
// transport-clear teleport and the post-teleport run-speed reassert, both fired from
// inside the movement block of that create). Sent at build time they left 1-3 packets
// ahead of the UPDATE_OBJECT that creates the player and were never acknowledged: the
// client ignores a movement packet for a unit it has not received yet. They go to a
// separate list (DeferPastSelfCreate) so the control-op hold above is untouched:
// TryCapture, the phases and the held list behave exactly as before, and a non-empty
// TakeForRelease is still the wedge signature. TakeDeferredForRelease hands the list
// back at the end of every UPDATE_OBJECT, after TakeForRelease; while the control-op
// hold is still active (a login whose self create has not been translated yet) it
// waits for that create, like the held ops do.
//
// Pure state — no sockets, no logging. Callers (the WorldClient legacy handlers)
// perform the sends and emit the events, keeping every transition unit-testable.
public sealed class PreCreateOpHold
{
    public enum HoldPhase { Inactive, Armed, ReleasePending }

    private readonly Lock _lock = new();
    private HoldPhase _phase = HoldPhase.Inactive;
    private readonly List<ServerPacket> _held = new();
    private readonly List<ServerPacket> _pastSelfCreate = new();
    private long _armTick;

    public HoldPhase Phase { get { lock (_lock) return _phase; } }
    public int HeldCount { get { lock (_lock) return _held.Count; } }
    public int DeferredCount { get { lock (_lock) return _pastSelfCreate.Count; } }
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
            _pastSelfCreate.Clear();
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
    // Deferred self synths are dropped, not flushed: they are the proxy's own packets
    // for a create that will not be forwarded on this connection.
    public List<ServerPacket>? ReleaseAll()
    {
        lock (_lock)
        {
            _pastSelfCreate.Clear();
            if (_phase == HoldPhase.Inactive)
                return null;
            return Deactivate();
        }
    }

    // JimsProxy (P1-2, D-06): a self packet the proxy built while translating the
    // UPDATE_OBJECT that carries the player's own create (the caller is inside that
    // create's movement block). It must reach the client after that UPDATE_OBJECT.
    // Never declines, in any phase: Armed (a login whose create is now being parsed)
    // releases it behind the held ops; Inactive (a world port, a seamless reconnect, the
    // control-op hold switched off) releases it at the end of the current UPDATE_OBJECT.
    public void DeferPastSelfCreate(ServerPacket packet)
    {
        lock (_lock)
            _pastSelfCreate.Add(packet);
    }

    // End of every UPDATE_OBJECT, called AFTER TakeForRelease so the order stays held
    // ops then deferred synths, which is their emission order (the held ops were all
    // captured before the create; the synths are built while it is parsed). Null when
    // nothing is deferred, or while the control-op hold is still active: Armed means a
    // login's self create has not been translated yet, so the synths wait for the update
    // that releases the held ops; ReleasePending means TakeForRelease has not run yet,
    // and the synths must not overtake the ops it is about to hand back.
    public List<ServerPacket>? TakeDeferredForRelease()
    {
        lock (_lock)
        {
            if (_pastSelfCreate.Count == 0 || _phase != HoldPhase.Inactive)
                return null;
            var packets = new List<ServerPacket>(_pastSelfCreate);
            _pastSelfCreate.Clear();
            return packets;
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

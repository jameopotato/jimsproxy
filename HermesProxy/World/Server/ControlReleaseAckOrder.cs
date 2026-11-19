using System.Collections.Generic;
using HermesProxy.World.Enums;

namespace HermesProxy.World.Server;

/// <summary>
/// JimsProxy (mount speed kept after Will of the Forsaken, 2026-10-18): makes the 1.12 server receive a
/// controller's acknowledgement of a controlled unit's speed change before the controller's release of
/// that unit, while the server still counts the controller as the unit's mover. Unit-tested in
/// <c>HermesProxy.Tests/World/ControlReleaseAckOrderTests</c>.
///
/// Mechanism: Will of the Forsaken cast while mounted under a Mind Control dismounts the victim while the
/// priest still controls it, so the server sends the victim's new run speed to the priest's client as a
/// forced speed change, followed by the end of the control. When both reach the 1.14 client together it
/// sends CMSG_SET_ACTIVE_MOVER (and CMSG_FAR_SIGHT) for the release first and the acknowledgement after. The
/// server ignores an acknowledgement for a unit the session no longer moves, never passes the new speed on
/// to the victim, and the victim keeps mount speed until its next speed change (2026-10-18 18:52:57.766:
/// SET_ACTIVE_MOVER(0), FAR_SIGHT 0 and SET_ACTIVE_MOVER(priest), then the acknowledgement for the victim at
/// .781). When the two packets reach the client apart it acknowledges first and the victim is corrected.
///
/// Rule: a forced speed change for a unit other than the player is remembered from just before it goes to
/// the client until the client acknowledges it. If the client releases that unit first (CMSG_SET_ACTIVE_MOVER
/// naming another unit, or CMSG_FAR_SIGHT, while the unit is its mover), everything it sends to the server
/// from that packet on is held in order, and the acknowledgement for the unit goes out ahead of it, which
/// releases the hold. The client sends that acknowledgement a frame after the release (15 ms at 18:52:57);
/// a transfer pauses its movement, so it resets this gate. Two bounds keep a
/// lost acknowledgement from holding traffic: a hold that reaches <see cref="MaxHeld"/> packets is let go as
/// it stands, and taking a unit over again forgets what was expected for it.
/// </summary>
public sealed class ControlReleaseAckOrder
{
    public const int MaxHeld = 16;
    private const int MaxExpected = 16;

    private readonly object _lock = new();
    private readonly List<(WowGuid128 Unit, Opcode AckOpcode, uint Counter)> _expected = new();
    private WowGuid128 _mover;
    private List<WorldPacket>? _held;
    private WowGuid128 _heldFor;
    // Mirrors _held != null, written under the lock: every packet the client sends reads it, so the
    // common case (no hold) takes no lock.
    private volatile bool _holding;

    /// <summary>Server side: a forced speed change for <paramref name="unit"/> (not the player) is about to
    /// go to the client. Call before sending it, so the acknowledgement can never arrive first.</summary>
    public void ExpectAck(WowGuid128 unit, Opcode ackOpcode, uint counter)
    {
        lock (_lock)
        {
            if (_expected.Count >= MaxExpected)
                _expected.RemoveAt(0);
            _expected.Add((unit, ackOpcode, counter));
        }
    }

    /// <summary>Client side: CMSG_SET_ACTIVE_MOVER (<paramref name="newMover"/> = the unit it names) or
    /// CMSG_FAR_SIGHT (<paramref name="newMover"/> = null). Call before sending it. True when this packet
    /// starts a hold, so it and everything after it wait for the acknowledgement.</summary>
    public bool OnClientMoverChange(WowGuid128? newMover)
    {
        lock (_lock)
        {
            WowGuid128 left = _mover;
            if (newMover is { } named)
            {
                if (named == left)
                    return false;
                _mover = named;
                _expected.RemoveAll(e => e.Unit == named);
            }

            if (_held != null || !IsExpected(left))
                return false;
            _held = new List<WorldPacket>();
            _heldFor = left;
            _holding = true;
            return true;
        }
    }

    /// <summary>Client side: a packet about to go to the server. True when it was taken (held, or part of an
    /// overflowing hold returned in <paramref name="overflow"/>, to be sent at once in that order).</summary>
    public bool TryHold(WorldPacket packet, out List<WorldPacket>? overflow)
    {
        overflow = null;
        if (!_holding)
            return false;
        lock (_lock)
        {
            if (_held == null)
                return false;
            _held.Add(packet);
            if (_held.Count < MaxHeld)
                return true;
            overflow = _held;
            _held = null;
            _holding = false;
            WowGuid128 unit = _heldFor;
            _expected.RemoveAll(e => e.Unit == unit);
            return true;
        }
    }

    /// <summary>Client side: a speed acknowledgement. True when it must go to the server now, ahead of the
    /// held packets (it is for the unit the hold waits on); <paramref name="release"/> then carries the held
    /// packets to send right after it, when this was the last acknowledgement the hold waited for.</summary>
    public bool OnAck(WowGuid128 unit, Opcode ackOpcode, uint counter, out List<WorldPacket>? release)
    {
        release = null;
        lock (_lock)
        {
            int i = _expected.FindIndex(e => e.Unit == unit && e.AckOpcode == ackOpcode && e.Counter == counter);
            if (i < 0)
                i = _expected.FindIndex(e => e.Unit == unit && e.AckOpcode == ackOpcode);
            if (i >= 0)
                _expected.RemoveAt(i);

            if (_held == null || unit != _heldFor)
                return false;
            if (!IsExpected(unit))
            {
                release = _held;
                _held = null;
                _holding = false;
            }
            return true;
        }
    }

    /// <summary>A transfer pauses the client's movement, so no acknowledgement can come: forget everything.
    /// Returns the packets of a hold in progress, to be sent in that order.</summary>
    public List<WorldPacket>? Reset()
    {
        lock (_lock)
        {
            _expected.Clear();
            var held = _held;
            _held = null;
            _holding = false;
            return held;
        }
    }

    private bool IsExpected(WowGuid128 unit)
    {
        foreach (var e in _expected)
            if (e.Unit == unit)
                return true;
        return false;
    }
}

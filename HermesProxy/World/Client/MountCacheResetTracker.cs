using System.Collections.Generic;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (#417 symptom 2, another player's mount not shown after a portal, 2026-10-18): decides when
/// the client must be given a MountDisplayID of 0 for another player before that player's mount, so the
/// mount is shown. Unit-tested in <c>HermesProxy.Tests/World/MountCacheResetTests</c>.
///
/// Mechanism: when this client's last view of another player was mounted, and the player comes back into
/// view unmounted (a create with no mount display) and then mounts the same mount, the client gets that
/// mount display and shows no mount, while it moves the player at mount speed (2026-10-18, two boxes: the
/// observer's last view of the dwarf at 22:52:37 was mount 14346; the observer took a portal at 22:52:53;
/// the dwarf came back unmounted at 22:52:55 and mounted 14346 again at 22:53:17, unseen). Given a mount
/// display of 0 first, the client shows the mount (2026-10-19, the same two boxes, 09:40:47).
///
/// Rule: per other player, remember the last mount display the client was given. A create without a
/// mount for a player last given a non-zero mount arms a reset: the next values update giving that same
/// mount gets a 0 first. A different mount, a dismount, or a create that carries the mount disarms it.
/// Called only on the server-packet thread.
/// </summary>
public sealed class MountCacheResetTracker
{
    private readonly Dictionary<WowGuid128, int> _lastGiven = new();
    private readonly HashSet<WowGuid128> _armed = new();

    /// <summary>One update for another player. <paramref name="mountInBlock"/>: the server's block carries
    /// the mount display; <paramref name="mountValue"/>: its value (0 when absent). True when the client must
    /// get a mount display of 0 for this player before this update's mount.</summary>
    public bool OnUpdate(WowGuid128 guid, bool isCreate, bool mountInBlock, int mountValue)
    {
        if (isCreate && !(mountInBlock && mountValue != 0))
        {
            // Created unmounted after a non-zero mount: the same mount next needs a 0 first.
            if (_lastGiven.TryGetValue(guid, out int last) && last != 0)
                _armed.Add(guid);
            return false;
        }

        if (!mountInBlock)
            return false;

        // A create that carries the mount goes out as sent; only a values update gets the reset.
        bool needsReset = !isCreate && mountValue != 0 && _armed.Contains(guid) &&
                          _lastGiven.TryGetValue(guid, out int held) && held == mountValue;
        _armed.Remove(guid);
        _lastGiven[guid] = mountValue;
        return needsReset;
    }
}

using System.Collections.Generic;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (#417 symptom 2, another player's mount not shown after a portal, 2026-10-18): decides when
/// the client must be given a MountDisplayID of 0 for another player before that player's mount, so the
/// mount is shown. Unit-tested in <c>HermesProxy.Tests/World/MountCacheResetTests</c>.
///
/// Mechanism: the client keeps the object of a player who leaves its view for a while and reuses it if
/// that player comes back. The mount the client last applied for that object stays with it, and the
/// client ignores a mount display equal to it. So when this client last saw a player mounted, the player
/// comes back into view unmounted on the reused object, and then mounts the same mount, the client gets
/// that mount display and shows no mount, while it moves the player at mount speed (2026-10-18, two
/// boxes: the observer's last view of the dwarf at 22:52:37 was mount 14346; the observer took a portal
/// at 22:52:53; the dwarf came back unmounted at 22:52:55 and mounted 14346 again at 22:53:17, unseen).
///
/// Rule: per other player, remember the last mount display the client was given. A create without a
/// mount for a player last given a non-zero mount leaves the client possibly holding that mount. The
/// next update that gives the same mount then needs a 0 first. If the client had freed and rebuilt the
/// object instead, it holds 0 and that 0 changes nothing. A different mount, or a dismount, needs no
/// reset. Called only on the server-packet thread.
/// </summary>
public sealed class MountCacheResetTracker
{
    private readonly Dictionary<WowGuid128, int> _lastGiven = new();
    private readonly HashSet<WowGuid128> _maybeStale = new();

    /// <summary>One update for another player. <paramref name="mountInBlock"/>: the server's block carries
    /// the mount display; <paramref name="mountValue"/>: its value (0 when absent). True when the client must
    /// get a mount display of 0 for this player before this update's mount.</summary>
    public bool OnUpdate(WowGuid128 guid, bool isCreate, bool mountInBlock, int mountValue)
    {
        if (isCreate && !(mountInBlock && mountValue != 0))
        {
            // Created unmounted: a reused object still holds the mount the client last applied.
            if (_lastGiven.TryGetValue(guid, out int last) && last != 0)
                _maybeStale.Add(guid);
            return false;
        }

        if (!mountInBlock)
            return false;

        // A create that carries the mount shows it on a reused object too (the client rebuilds the mount
        // from the created field), so only a values update can be the ignored one.
        bool needsReset = !isCreate && mountValue != 0 && _maybeStale.Contains(guid) &&
                          _lastGiven.TryGetValue(guid, out int held) && held == mountValue;
        _maybeStale.Remove(guid);
        _lastGiven[guid] = mountValue;
        return needsReset;
    }
}

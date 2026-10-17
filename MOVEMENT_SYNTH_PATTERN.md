# Movement-state synth race trap & deferred-synth pattern

Quick reference for anyone (human or AI) adding a proxy → client synthesized
`SMSG_MOVE_TELEPORT` (or similar movement-state reset) to unlock a wedged
modern client. Background and rationale are in commits `dc39c39`, `fc86903`,
`a3e763a`, `f414896`.

## TL;DR

If you're synthesizing a movement-state packet after the proxy receives
`SMSG_NEW_WORLD` or `SMSG_LOGIN_VERIFY_WORLD`, **do not fire the synth
inline** in the handler. **Defer it** to the player's first post-arming
`SMSG_UPDATE_OBJECT`. Otherwise you will introduce a latency-dependent race
that NA testers (~150ms) can't repro but EU testers (~35ms) hit consistently.

## The trap

```
t+0      proxy receives SMSG_NEW_WORLD from legacy server
         forwards NEW_WORLD to modern client
         INLINE synth fires here  ← TRAP
t+50ms   proxy receives small UPDATE_OBJECT (transport object header)
t+1.37s  proxy receives SMSG_COMPRESSED_UPDATE_OBJECT (destination map state)
t+1.38s  player UpdateObject inside the compressed packet re-attaches
         transport (e.g., player lands on a destination zep tower)
```

At ~150ms latency, the inline synth happens to land at the modern client
AFTER the destination's `COMPRESSED_UPDATE_OBJECT` — synth clears stale
state, server re-attaches via the natural flow, everything works.

At ~35ms latency, the synth lands BEFORE the destination state. Server's
subsequent natural re-attach then conflicts with the just-cleared client
state, and the modern client wedges into one of two failure modes:

- **Linear-movement wedge** (forward/back/strafe all blocked) — observed
  on post-zep transit landing on a destination transport.
- **Turn-input gate** (forward/back/strafe work, rotation blocked) —
  observed on login already on a transport.

## The pattern

Use the existing infrastructure in `HermesProxy/GlobalSessionData.cs`:

```csharp
public enum DeferredTransportSynthMode { None, NewWorld, Login }
public DeferredTransportSynthMode PendingDeferredTransportSynth;
```

### Arming (handler side)

In `MovementHandler.HandleNewWorld` (and now `CharacterHandler.HandleLoginVerifyWorld`):

```csharp
GetSession().GameState.PendingDeferredTransportSynth =
    DeferredTransportSynthMode.NewWorld;  // or .Login
```

Don't fire the synth inline. Just arm.

### Dispatching (deferred firing)

In `UpdateHandler.ReadMovementUpdateBlock`, inside the existing
`if (guid == GetSession().GameState.CurrentPlayerGuid)` block (right after
the `movement.player_update.transport_state` diagnostic):

```csharp
var pendingMode = GetSession().GameState.PendingDeferredTransportSynth;
if (pendingMode != DeferredTransportSynthMode.None)
{
    GetSession().GameState.PendingDeferredTransportSynth = DeferredTransportSynthMode.None;
    bool destinationOnTransport = !moveInfo.TransportGuid.IsEmpty();
    bool alwaysFire = pendingMode == DeferredTransportSynthMode.Login;
    if (!alwaysFire && destinationOnTransport)
    {
        // NewWorld + on destination transport: SKIP. Natural NEW_WORLD load
        // already unlocked the client; firing here risks re-wedging.
        Log.Event("movement.transport_clear.deferred_skipped", ...);
    }
    else
    {
        FireDeferredTransportClearSynth(guid, moveInfo.Position, moveInfo.Orientation, pendingMode.ToString());
    }
}
```

### Wire ordering caveat

The dispatcher runs INSIDE `ReadMovementUpdateBlock`, which runs WHILE
`HandleUpdateObject` is parsing the legacy COMPRESSED_UPDATE_OBJECT. The
modern UpdateObject isn't actually sent to the client until line ~419's
`SendPacketToClient(updateObject)` at the end of the handler. So if the
dispatcher fires the synth inline, the wire order to the client is:

  1. synth (fires from within ReadMovementUpdateBlock at proxy time T)
  2. modern UpdateObject (fires at end of handler, T+~50ms)

The synth lands FIRST, then the UpdateObject. Whether this matters
depends on the synth payload:

- **For clear-stale-flag synths** (TransportGUID=empty when player is NOT
  on a destination transport): the trailing UpdateObject also has no
  transport attach, so it doesn't conflict. Inline order works.
- **For state-refresh synths** (where you wanted the synth to land AFTER
  the UpdateObject so it could "fix up" something): this pattern does
  NOT give you that. Validated 2026-05-17: stashing the synth payload
  and firing it after `SendPacketToClient(updateObject)` did make the
  wire order "updateObject → synth" — but for the transport-clear case
  this caused worse behavior (synth dismounted player from transport →
  fall into ocean). The fix was to SKIP the synth entirely when the
  destination shows the player on a transport.

If you genuinely need "synth lands after destination state with no race",
stash + fire after `SendPacketToClient(updateObject)` works at the wire
level. Make sure your synth payload is correct for that ordering.

**Update 2026-10-16 (P1-2): the synth is now sent after the UpdateObject.**
The inline order above turned out to make the login and world-port synths
no-ops: captures show none of them acknowledged, because the client ignores
a movement packet for a unit it has not received yet, and the synth left
1-3 packets ahead of the UpdateObject that creates the player. The
dispatcher now hands the synth to `PreCreateOpHold.DeferPastSelfCreate`,
and `HandleUpdateObject` sends it right after that UpdateObject, behind any
control ops the pre-create hold releases. The post-teleport run-speed
reassert (built in the same movement block) takes the same route. Wire
order today:

  1. modern UpdateObject (the player's create)
  2. control ops held by the pre-create hold, if any
  3. the deferred synth(s), in the order they were built

This is the "updateObject → synth" order of the 2026-05-17 experiment; the
failure seen then was the on-transport destination, which the dispatcher
skips (`TransportClearGate`). New synths built inside a self create's
movement block should use `DeferPastSelfCreate` rather than sending inline.

## Why Login is no longer special-cased

Earlier drafts of this pattern always-fired the Login synth, including
when the player was on a transport. Bad idea — validated 2026-05-17
(log jimsproxy-20260517-102517.jsonl): tester logged in mid-flight on a
zep, synth fired with TransportGUID=empty, modern client teleported them
off the zep into mid-air, fell into the ocean at Silithus.

`SMSG_MOVE_TELEPORT` semantically means "teleport to this world position
[and attach to this transport]". The packet has no transport-offset
field and no "state refresh" flag. A TransportGUID-preserving synth
still tells the client "teleport you onto this transport at this world
point" — visual snap, possible re-detach. There is no known no-op
flavor of MoveTeleport.

So when the player is legitimately on a transport at login, we SKIP the
synth. The trade-off: the modern client's turn-input gate stays engaged
(player can strafe / move forward+back but can't rotate) until the next
natural world transition (zep docking) releases it. That's a worse UX
than rotation-works-immediately, but VASTLY better than falling into
ocean. If a future client-state-reset opcode is discovered that releases
the gate without dismount, revisit.

For non-transport logins, the synth is a harmless no-op (clearing a
transport flag that wasn't set) — fires safely.

Since P1-2 (2026-10-16) that login synth actually reaches the client after
the create (see the update under "Wire ordering caveat"): it is a teleport
to the create's own position with no transport, the client acknowledges
it, and the proxy drops the ack (the 0xFFFFFFFF sentinel). Before P1-2 the
client ignored it, so its effect on the wire is new with P1-2.

## Diagnostic events to grep when triaging

- `movement.transport_clear.synthesized` — the synth fired. Includes
  `deferred: true` and `mode` (NewWorld | Login).
- `movement.transport_clear.deferred_skipped` — dispatcher skipped the
  synth because the destination has the player legitimately on a
  transport. `mode` field tells you which path armed (NewWorld | Login).
- `movement.transport_clear.send_completed` / `send_failed` — outbound
  send wrapper diagnostics.
- `movement.player_update.transport_state` — fires once per
  transport-state transition on the player. Useful for confirming the
  destination's attachment state.

## When NOT to use this pattern

- For pure proxy → client packets that don't depend on the modern
  client's movement-state machine (chat synthesis, aura updates, etc.).
- For synths that need to fire BEFORE the destination state is processed
  (rare — if you find one, you almost certainly have a different
  architectural problem).

## Future synth patterns to vet against this

- Mount/dismount stuck states
- AFK timeout re-entry
- Instance entry / vehicle entry
- Resurrection at graveyards

If you add a new client-state-reset synth, ask yourself:

1. Does the destination state arrive in a separate later packet?
2. Could the synth land at the client before that state?
3. If yes to both, use the deferred-dispatcher pattern. Add a new enum
   variant to `DeferredTransportSynthMode` if the conditional skip logic
   differs.

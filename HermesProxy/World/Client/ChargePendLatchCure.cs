using HermesProxy.World.Enums;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (charge strafe-latch cure, 2026-08-28): pure decision logic for the
/// pending-strafe orphan cure, extracted for unit tests (WorldEntryCeremonyTracker
/// precedent).
///
/// The 1.14.2 client queues a strafe key pressed mid-air during a locked-trajectory
/// (forward-held) jump as PendingStrafeLeft/Right instead of applying it. When a
/// Charge spline hijacks the fall, the key's release mid-spline is swallowed
/// without clearing the pend, and the spline-exit landing APPLIES the stale pend as
/// a real strafe flag with no key behind it. The character then strafes on its own
/// (transmitted in every movementinfo) until the player touches a strafe-axis key,
/// because non-strafe inputs only toggle their own flag bits. Wire-proven on 3/3
/// field latches + 2/2 deliberate repros (2026-08-28 .pkt flag decode); the arming
/// signature (Pend flag in the CHANGE_TRANSPORT the client emits at charge GO)
/// appeared on exactly the latching charges across 4 sessions / 83 charges.
///
/// Cure: a synthetic force ROOT+UNROOT pulse to the client, fired the moment the
/// orphan is OBSERVED — the first post-arm client movement packet whose flags show
/// the armed pend's REAL strafe bit set with the pend bit gone (the apply). The
/// apply lands at the client's SPLINE_DONE or at the FALL_LAND up to ~250ms later
/// (both shapes wire-observed 2026-08-29); every subsequent movement packet also
/// carries the applied flag, so the detector catches within one packet either way.
/// Corpus-proven client behavior (~40 natural self force-root episodes, 2026-08-28
/// rootscan): a force-root wipes ALL client movement flags and makes the client
/// emit the matching stop opcodes (which forward to the server normally,
/// correcting its view too); a force-unroot makes the client rebuild flags from
/// PHYSICAL key state, re-emitting starts for genuinely held keys in the same
/// millisecond. The pulse is therefore safe in both worlds: orphaned flag wiped,
/// genuinely held key resumes instantly.
///
/// v1 fire anchor was WRONG (2026-08-29 field falsification): it waited for the
/// player's post-charge SPLINE_UNROOT, but the charge-bracketing spline
/// root/unroot Kronos sends is addressed to the charge TARGET, never the charging
/// player (mover GUIDs decoded = the victim mobs). No self anchor packet exists —
/// and consequently there is also no moved-while-server-rooted exposure to time
/// around: the server never roots the charging player at all.
/// </summary>
public static class ChargePendLatchCure
{
    /// <summary>
    /// The proven orphan-makers: pending strafe STARTS. Pending stops
    /// (PendStop/PendStrafeStop) resolve safely at spline exit (07-11 C1 wire
    /// evidence), and PendingForward did not orphan in the one specimen carrying
    /// it; scope stays on the strafe pends until evidence says otherwise.
    /// </summary>
    public const uint PendingStartStrafeMask =
        (uint)(MovementFlagModern.PendingStrafeLeft | MovementFlagModern.PendingStrafeRight);

    /// <summary>
    /// Covers arm-to-fire across the longest observed charge spline (~0.95s) plus
    /// the server's unroot lag (~0.6s) with slack; expires stale arms (e.g. a real
    /// transport boarding that carried a pend flag, where no spline-unroot follows).
    /// </summary>
    public const long ArmTtlMs = 3000;

    /// <summary>
    /// Synth MoveCounters inside the WorldEntryCeremonyTracker.IsSynthCounter range
    /// so the existing ack-swallow path covers them (the legacy server never sent
    /// these ops; a spurious force-ack can feed Kronos's malformed-input kick
    /// counters). 0xFFFFFF02 is taken by the carried-root cure unroot.
    /// </summary>
    public const uint SynthCounterRoot = 0xFFFFFF03;
    public const uint SynthCounterUnroot = 0xFFFFFF04;

    /// <summary>
    /// Arm on the charge-GO CHANGE_TRANSPORT (the packet we already intercept and
    /// drop) when the client's reported flags carry a pending strafe start.
    /// </summary>
    public static bool ShouldArm(uint modernMovementFlags)
        => (modernMovementFlags & PendingStartStrafeMask) != 0;

    public static bool IsArmed(long armedAtMs, long nowMs)
        => armedAtMs != 0 && nowMs - armedAtMs <= ArmTtlMs;

    /// <summary>
    /// The real strafe bit(s) the armed pend will turn into when the spline-exit
    /// landing applies it: PendingStrafeLeft -> StrafeLeft, PendingStrafeRight ->
    /// StrafeRight.
    /// </summary>
    public static uint ExpectedRealStrafeMask(uint armedFlags)
    {
        uint mask = 0;
        if ((armedFlags & (uint)MovementFlagModern.PendingStrafeLeft) != 0)
            mask |= (uint)MovementFlagModern.StrafeLeft;
        if ((armedFlags & (uint)MovementFlagModern.PendingStrafeRight) != 0)
            mask |= (uint)MovementFlagModern.StrafeRight;
        return mask;
    }

    /// <summary>
    /// True when a post-arm movement packet shows the orphan in existence: the
    /// armed pend's real strafe bit is now set AND the pend bit itself is gone
    /// (applied, no longer queued). Mid-spline packets still carrying the pend
    /// don't fire; neither does the arming packet itself (real bit absent).
    /// </summary>
    public static bool ShouldFire(uint armedFlags, uint currentFlags)
        => (currentFlags & ExpectedRealStrafeMask(armedFlags)) != 0
           && (currentFlags & armedFlags & PendingStartStrafeMask) == 0;

    public static bool IsCureCounter(uint moveCounter)
        => moveCounter == SynthCounterRoot || moveCounter == SynthCounterUnroot;
}

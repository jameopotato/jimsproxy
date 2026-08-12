using System;
using System.Collections.Generic;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (BG-exit movement lockup, 2026-08-03): pure state and decision logic
/// behind the carried-root cure and its breadcrumb, extracted for unit tests
/// (TransportClearGate precedent).
///
/// 1. The always-on "ceremony unclosed" breadcrumb. Every Kronos arrival delivers a
///    fixed force-op ceremony to the player — SMSG_MOVE_ROOT ×2 at the loading
///    boundary, SMSG_MOVE_UNROOT ~1s later — verified on 18/18 arrivals across two
///    machines (2026-08-02 field captures). A root whose unroot leg fails is the
///    mechanism of the post-BG-exit movement lockup (translation dead,
///    turning/casting alive, /reload cures — harness-reproduced exactly). The
///    tracker counts the ceremony's legs and their client acks between arrival
///    anchors; an opened-but-not-observably-closed ceremony logs ONE always-on
///    JSONL line, so a field Export Diagnostics carries the discriminator without
///    DebugOutput.
///
/// 2. The carried-root cure gate (ShouldCureCarriedRoot below) — THE FIX.
/// </summary>
public class WorldEntryCeremonyTracker
{
    public bool Active;
    public string Anchor = "";
    public long AnchorTickMs;
    // Bumped from two threads (world-client handler = forwards, modern-socket
    // handler = acks) via Interlocked; a torn read only costs a breadcrumb.
    public int RootsForwarded;
    public int RootAcks;
    public int UnrootsForwarded;
    public int UnrootAcks;
    // Mirasu R40 branch (c): when the 1.12 unit has CLIENT_CONTROL_LOST (set by
    // BattleGround::BlockMovement at BG end — the exact window an instant
    // "Leave Battleground" click races), the server emits SPLINE_MOVE_ROOT/UNROOT
    // instead of the FORCE family (unacked). R40's "wrong slot" theory — that a
    // spline-unroot cannot clear a force-root — was DISPROVEN live (R5 run,
    // 2026-08-03: a force-unroot re-dialected to the spline family still freed the
    // player), so a mixed-family pair is NOT itself a strand at normal post-init
    // timing; only mid-load spline delivery remains untested. Healthy corpus has
    // ZERO self spline root legs (0/18 arrivals), so ANY occurrence is still
    // capture-worthy — it marks the CONTROL_LOST emission window even though the
    // family itself is not the lock.
    public int SplineRootsForwarded;
    public int SplineUnrootsForwarded;
    public bool InitMoverCompleteSeen;

    public void Begin(string anchor, long nowMs)
    {
        Anchor = anchor;
        AnchorTickMs = nowMs;
        RootsForwarded = 0;
        RootAcks = 0;
        UnrootsForwarded = 0;
        UnrootAcks = 0;
        SplineRootsForwarded = 0;
        SplineUnrootsForwarded = 0;
        InitMoverCompleteSeen = false;
        Active = true;
    }

    public void Reset()
    {
        Active = false;
        Anchor = "";
        AnchorTickMs = 0;
    }

    /// <summary>
    /// A ceremony that opened at least one root and did not observably close it:
    /// no unroot forwarded, or the unroot never acked, or a root's ack is missing
    /// (the stuck-stun golden capture's fingerprint — the client discards a force
    /// op it can't apply, and the discard is visible as the missing ack). A real
    /// gameplay root held across a transfer also matches — this is a breadcrumb
    /// for correlation, not an alarm.
    /// </summary>
    public static bool IsUnclosed(int rootsForwarded, int rootAcks, int unrootsForwarded, int unrootAcks)
        => rootsForwarded > 0
           && (unrootsForwarded == 0 || unrootAcks == 0 || rootAcks < rootsForwarded);

    /// <summary>
    /// The always-on emission gate: an unclosed force ceremony, OR any self
    /// spline-family root leg at all (zero occurrences across the entire healthy
    /// corpus — any appearance is the R40 branch-(c) wrong-family signature and
    /// must be captured).
    /// </summary>
    public static bool IsAnomalous(int rootsForwarded, int rootAcks, int unrootsForwarded, int unrootAcks,
                                   int splineRoots, int splineUnroots)
        => IsUnclosed(rootsForwarded, rootAcks, unrootsForwarded, unrootAcks)
           || splineRoots > 0 || splineUnroots > 0;

    /// <summary>
    /// Counter for the proxy-synthesized cure unroot. The range is recognizable in
    /// captures, disjoint from the legacy server's constant 0 and below the
    /// 0xFFFFFFFF transport-clear teleport sentinel. Acks bearing these counters
    /// are swallowed by the proxy (logged, never forwarded to the legacy server,
    /// which never sent the op). Live-verified 2026-08-03: the 42597 client applies
    /// and acks a force-op carrying this counter.
    /// </summary>
    public const uint SynthCounterUnroot = 0xFFFFFF02;
    public static bool IsSynthCounter(uint counter) => counter >= 0xFFFFFF00 && counter < 0xFFFFFFFF;

    /// <summary>
    /// The carried-root cure gate (THE FIX). A spam-clicked "Leave Battleground"
    /// departs while the BG-end root is being removed; the server's unroot fires in
    /// the between-maps window and is silently discarded (cmangos Unit.cpp:751
    /// `!IsInWorld()` — deterministic, Mirasu R40 (a)). The client then arrives
    /// carrying a force-root the server no longer knows about — the exact reported
    /// lockup (harness-proven: R2 drop_unroot reproduced symptom + /reload cure).
    /// Cure: if the client crosses a loading boundary (NEW_WORLD or a same-map
    /// teleport) believing itself rooted, synthesize the missing unroot.
    ///
    /// Deliberately NOT gated on the destination's movement flags: a player's
    /// server-side m_movementInfo is an ECHO of the client's own reported state
    /// (the root-ack the client sent when it got rooted), so a stranded client
    /// poisons that evidence — the first verification run proved the flag-gated
    /// version can never fire. Belief-only is safe: a legitimately rooted arrival
    /// is re-rooted by the server's own arrival ceremony (ROOT ×2 fires on every
    /// cross-map arrival, 18/18 field captures) right after our unroot; the
    /// same-map residue (hearthing away during the last seconds of a live root
    /// aura) is cosmetic. Fires zero times across all healthy captures (nobody
    /// crosses a boundary rooted).
    /// </summary>
    public static bool ShouldCureCarriedRoot(bool clientBelievesRooted)
        => clientBelievesRooted;
}


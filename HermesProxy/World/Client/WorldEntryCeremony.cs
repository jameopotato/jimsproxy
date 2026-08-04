using System;
using System.Collections.Generic;

namespace HermesProxy.World.Client;

/// <summary>
/// JimsProxy (worldentry root-ceremony instrumentation 2026-08-03): pure state and
/// decision logic behind two things, extracted for unit tests (TransportClearGate
/// precedent):
///
/// 1. The always-on "ceremony unclosed" breadcrumb. Every Kronos arrival delivers a
///    fixed force-op ceremony to the player — SMSG_MOVE_ROOT ×2 at the loading
///    boundary, SMSG_MOVE_UNROOT ~1s later — verified on 18/18 arrivals across two
///    machines (2026-08-02 field captures). A root whose unroot leg fails is the
///    leading mechanism for the post-BG-exit movement lockup (translation dead,
///    turning/casting alive, /reload cures). The tracker counts the ceremony's legs
///    and their client acks between arrival anchors; an opened-but-not-observably-
///    closed ceremony logs ONE always-on JSONL line, so a field Export Diagnostics
///    can carry the discriminator without DebugOutput.
///
/// 2. The dev-only delivery harness (config-gated, default off, see
///    Settings.WorldEntryHarnessMode) used to force the ceremony race on demand
///    instead of waiting for a slow machine to lose it naturally.
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
    // instead of the FORCE family. Unacked, and applied to a different slot than a
    // force-root — a force-root + spline-unroot pair strands the root with no
    // packet dropped anywhere. Healthy corpus has ZERO self spline root legs
    // (0/18 arrivals), so ANY occurrence is capture-worthy.
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
    /// Dev-harness synthetic force-op counters (synth_root_preinit mode). The
    /// range is recognizable in captures, disjoint from both the legacy server's
    /// constant 0 and the mint range (which grows upward from 1001), and below the
    /// 0xFFFFFFFF transport-clear teleport sentinel. Acks bearing these counters
    /// are swallowed by the proxy (logged, never forwarded to the legacy server,
    /// which never sent the op).
    /// </summary>
    public const uint SynthCounterRoot = 0xFFFFFF01;
    public const uint SynthCounterUnroot = 0xFFFFFF02;
    public static bool IsSynthCounter(uint counter) => counter >= 0xFFFFFF00 && counter < 0xFFFFFFFF;

    /// <summary>
    /// The carried-root cure gate (THE FIX). A spam-clicked "Leave Battleground"
    /// departs while the BG-end root is being removed; the server's unroot fires in
    /// the between-maps window and is silently discarded (cmangos Unit.cpp:751
    /// `!IsInWorld()` — deterministic, Mirasu R40 (a)). The client then arrives
    /// carrying a force-root the server no longer knows about — the exact reported
    /// lockup (harness-proven: R2 drop_unroot reproduced symptom + /reload cure).
    /// Cure: at the player's own destination update, if the client crossed the
    /// boundary believing itself rooted while the server's authoritative movement
    /// state says mobile, synthesize the missing unroot. Fires zero times across
    /// all 18 healthy field captures (nobody crosses a boundary rooted); a
    /// legitimately-rooted crossing keeps the gate closed via the destination flag.
    /// </summary>
    public static bool ShouldCureCarriedRoot(bool clientBelievesRooted, bool destinationRooted)
        => clientBelievesRooted && !destinationRooted;
}

/// <summary>
/// Dev-harness MoveCounter minting (Settings.WorldEntryMintMoveCounters): the legacy
/// server sends every force-op with MoveCounter=0, forever (wire-verified — 39/39
/// ops across the 2026-08-02 captures), so the modern client's pending-ack
/// bookkeeping only ever sees duplicate keys. Minting replaces outbound force-op
/// counters with session-monotonic values and restores the original on the ack path
/// so the legacy server still sees its own value. The offset makes minted values
/// recognizable in .pkt captures.
/// </summary>
public class MoveCounterMint
{
    internal const uint MintBase = 1000;
    private uint _next = MintBase;
    private readonly Dictionary<uint, uint> _mintedToOriginal = new();
    private readonly object _lock = new();

    public uint Mint(uint original)
    {
        lock (_lock)
        {
            // Defensive bound: unacked ops should be rare; if the client stops
            // acking (the exact failure we hunt) don't grow without limit.
            if (_mintedToOriginal.Count > 512)
                _mintedToOriginal.Clear();
            uint minted = ++_next;
            _mintedToOriginal[minted] = original;
            return minted;
        }
    }

    public bool TryResolve(uint minted, out uint original)
    {
        lock (_lock)
        {
            if (_mintedToOriginal.TryGetValue(minted, out original))
            {
                _mintedToOriginal.Remove(minted);
                return true;
            }
            return false;
        }
    }
}

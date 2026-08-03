using HermesProxy.World.Client;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (worldentry root-ceremony instrumentation): the unclosed-ceremony
/// breadcrumb predicate and the dev-harness MoveCounter mint.
/// See <see cref="WorldEntryCeremonyTracker"/> / <see cref="MoveCounterMint"/>.
/// </summary>
public class WorldEntryCeremonyTests
{
    // The healthy wire shape (18/18 field arrivals): ROOT ×2 both acked, UNROOT acked.
    [Fact]
    public void IsUnclosed_HealthyCeremony_False()
    {
        Assert.False(WorldEntryCeremonyTracker.IsUnclosed(
            rootsForwarded: 2, rootAcks: 2, unrootsForwarded: 1, unrootAcks: 1));
    }

    // No ceremony at all (no roots seen between anchors) — nothing to report.
    [Fact]
    public void IsUnclosed_NoRoots_False()
    {
        Assert.False(WorldEntryCeremonyTracker.IsUnclosed(0, 0, 0, 0));
        Assert.False(WorldEntryCeremonyTracker.IsUnclosed(0, 0, 1, 1));
    }

    // The T1 shape (server never sent the unroot): roots acked, no unroot forwarded.
    [Fact]
    public void IsUnclosed_UnrootNeverForwarded_True()
    {
        Assert.True(WorldEntryCeremonyTracker.IsUnclosed(2, 2, 0, 0));
    }

    // The T2/T3' shape (client swallowed the unroot): forwarded but never acked.
    [Fact]
    public void IsUnclosed_UnrootForwardedNeverAcked_True()
    {
        Assert.True(WorldEntryCeremonyTracker.IsUnclosed(2, 2, 1, 0));
    }

    // The stuck-stun golden-capture fingerprint: a root leg discarded (ack short).
    [Fact]
    public void IsUnclosed_RootAckShort_True()
    {
        Assert.True(WorldEntryCeremonyTracker.IsUnclosed(2, 1, 1, 1));
        Assert.True(WorldEntryCeremonyTracker.IsUnclosed(1, 0, 1, 1));
    }

    [Fact]
    public void Tracker_BeginResetsCountsAndActivates()
    {
        var tracker = new WorldEntryCeremonyTracker();
        tracker.RootsForwarded = 5;
        tracker.UnrootAcks = 3;
        tracker.InitMoverCompleteSeen = true;

        tracker.Begin("new_world", 12345);

        Assert.True(tracker.Active);
        Assert.Equal("new_world", tracker.Anchor);
        Assert.Equal(12345, tracker.AnchorTickMs);
        Assert.Equal(0, tracker.RootsForwarded);
        Assert.Equal(0, tracker.RootAcks);
        Assert.Equal(0, tracker.UnrootsForwarded);
        Assert.Equal(0, tracker.UnrootAcks);
        Assert.False(tracker.InitMoverCompleteSeen);

        tracker.Reset();
        Assert.False(tracker.Active);
    }

    // Minted counters are session-monotonic, unique, and above the recognizable base
    // (never colliding with the legacy server's constant 0).
    [Fact]
    public void Mint_ProducesMonotonicUniqueCounters()
    {
        var mint = new MoveCounterMint();
        uint first = mint.Mint(0);
        uint second = mint.Mint(0);
        Assert.True(first > MoveCounterMint.MintBase);
        Assert.True(second > first);
    }

    // The ack path must recover the exact original per minted value, once.
    [Fact]
    public void Mint_ResolveReturnsOriginalOnce()
    {
        var mint = new MoveCounterMint();
        uint mintedZero = mint.Mint(0);
        uint mintedSeven = mint.Mint(7);

        Assert.True(mint.TryResolve(mintedSeven, out uint original));
        Assert.Equal(7u, original);
        Assert.True(mint.TryResolve(mintedZero, out original));
        Assert.Equal(0u, original);

        // Consumed — a duplicate ack no longer resolves.
        Assert.False(mint.TryResolve(mintedZero, out _));
    }

    // Counters we never minted (pre-toggle ops, other ack kinds) must not resolve —
    // the ack path forwards them untouched.
    [Fact]
    public void Mint_UnknownCounterDoesNotResolve()
    {
        var mint = new MoveCounterMint();
        mint.Mint(0);
        Assert.False(mint.TryResolve(0, out _));
        Assert.False(mint.TryResolve(999999, out _));
    }

    // R40 branch (c): ANY self spline-family root leg is anomalous — zero occur in
    // the healthy corpus, and a spline unroot after a force root is the stranded-
    // root wrong-family signature.
    [Fact]
    public void IsAnomalous_SplineLegPresent_True()
    {
        // Force ceremony healthy but the unroot came as spline too — capture it.
        Assert.True(WorldEntryCeremonyTracker.IsAnomalous(2, 2, 1, 1, 0, 1));
        // Root arrived spline-family (control lost at arrival).
        Assert.True(WorldEntryCeremonyTracker.IsAnomalous(0, 0, 0, 0, 1, 0));
    }

    [Fact]
    public void IsAnomalous_NoSplineLegs_MatchesIsUnclosed()
    {
        Assert.False(WorldEntryCeremonyTracker.IsAnomalous(2, 2, 1, 1, 0, 0));
        Assert.True(WorldEntryCeremonyTracker.IsAnomalous(2, 2, 0, 0, 0, 0));
    }

    // Sentinel counters (synth harness ops) must be disjoint from the legacy
    // constant 0, the mint range, and the 0xFFFFFFFF transport-clear sentinel.
    [Fact]
    public void SynthCounters_DisjointFromMintAndLegacyValues()
    {
        Assert.True(WorldEntryCeremonyTracker.IsSynthCounter(WorldEntryCeremonyTracker.SynthCounterRoot));
        Assert.True(WorldEntryCeremonyTracker.IsSynthCounter(WorldEntryCeremonyTracker.SynthCounterUnroot));
        Assert.False(WorldEntryCeremonyTracker.IsSynthCounter(0));
        Assert.False(WorldEntryCeremonyTracker.IsSynthCounter(0xFFFFFFFF));

        var mint = new MoveCounterMint();
        for (int i = 0; i < 1000; i++)
            Assert.False(WorldEntryCeremonyTracker.IsSynthCounter(mint.Mint(0)));
    }
}

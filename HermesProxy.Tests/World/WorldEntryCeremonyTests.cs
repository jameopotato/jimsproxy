using HermesProxy.World.Client;
using Xunit;

namespace HermesProxy.Tests.World;

/// <summary>
/// JimsProxy (BG-exit movement lockup): the unclosed-ceremony
/// breadcrumb predicates and the carried-root cure gate.
/// See <see cref="WorldEntryCeremonyTracker"/>.
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

    // THE FIX's gate: belief-only. The destination's movement flags are an echo of
    // the client's own reported state (a stranded client poisons them — the first
    // verification run proved a flag-gated version can never fire), so crossing a
    // boundary rooted is itself the cure condition; a legitimately rooted arrival
    // is re-rooted by the server's own arrival ceremony.
    [Fact]
    public void ShouldCureCarriedRoot_TruthTable()
    {
        Assert.True(WorldEntryCeremonyTracker.ShouldCureCarriedRoot(clientBelievesRooted: true));
        // Healthy crossing (18/18 field captures) -> never fires.
        Assert.False(WorldEntryCeremonyTracker.ShouldCureCarriedRoot(clientBelievesRooted: false));
    }

    // The cure unroot's sentinel counter must be disjoint from the legacy server's
    // constant 0 and from the 0xFFFFFFFF transport-clear teleport sentinel, so the
    // ack swallow can never eat a legitimate ack.
    [Fact]
    public void SynthCounter_DisjointFromLegacyValues()
    {
        Assert.True(WorldEntryCeremonyTracker.IsSynthCounter(WorldEntryCeremonyTracker.SynthCounterUnroot));
        Assert.False(WorldEntryCeremonyTracker.IsSynthCounter(0));
        Assert.False(WorldEntryCeremonyTracker.IsSynthCounter(0xFFFFFFFF));
    }
}

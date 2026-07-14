using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// Pins KtmThreatOriginator.ShouldEmit — the change-gate / throttle / gap-suppress
// decision for proxy-originated KLHTM broadcasts (used when the local client has
// no KTM addon of its own). Pure static, so it needs no GlobalSessionData graph.
public class KtmThreatOriginatorTests
{
    private const long Now = 100_000;
    private const long LongAgo = Now - 10_000;   // outside the 5s client-KTM window
    private const long Recent = Now - 1_000;     // inside the 5s client-KTM window
    private const long Interval = 2_000;

    [Fact]
    public void ShouldEmit_FreshPositiveThreat_Emits()
    {
        // lastEmitMs 0 (never emitted) bypasses the throttle; value changed; no client KTM.
        Assert.True(KtmThreatOriginator.ShouldEmit(
            threat: 5000, now: Now, lastClientKtmMs: LongAgo,
            lastEmittedValue: -1, lastEmitMs: 0, interval: Interval));
    }

    [Fact]
    public void ShouldEmit_ClientKtmActive_Suppressed()
    {
        // The local client emitted its own KLHTM recently → the rewrite owns the stream.
        Assert.False(KtmThreatOriginator.ShouldEmit(
            threat: 5000, now: Now, lastClientKtmMs: Recent,
            lastEmittedValue: -1, lastEmitMs: 0, interval: Interval));
    }

    [Fact]
    public void ShouldEmit_ValueUnchanged_ChangeGated()
    {
        Assert.False(KtmThreatOriginator.ShouldEmit(
            threat: 5000, now: Now, lastClientKtmMs: LongAgo,
            lastEmittedValue: 5000, lastEmitMs: Now - 10_000, interval: Interval));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ShouldEmit_NoPositiveThreat_Suppressed(long threat)
    {
        Assert.False(KtmThreatOriginator.ShouldEmit(
            threat: threat, now: Now, lastClientKtmMs: LongAgo,
            lastEmittedValue: -1, lastEmitMs: 0, interval: Interval));
    }

    [Fact]
    public void ShouldEmit_WithinThrottle_Suppressed()
    {
        // 1.5s since last emit < 2s interval → throttled, even though the value changed.
        Assert.False(KtmThreatOriginator.ShouldEmit(
            threat: 5200, now: Now, lastClientKtmMs: LongAgo,
            lastEmittedValue: 5000, lastEmitMs: Now - 1_500, interval: Interval));
    }

    [Fact]
    public void ShouldEmit_PastThrottle_Emits()
    {
        // 2.1s since last emit >= 2s interval and the value changed → emit.
        Assert.True(KtmThreatOriginator.ShouldEmit(
            threat: 5200, now: Now, lastClientKtmMs: LongAgo,
            lastEmittedValue: 5000, lastEmitMs: Now - 2_100, interval: Interval));
    }

    [Fact]
    public void ShouldEmit_WarriorIntervalShorter_EmitsWhereBaseWouldNot()
    {
        // 1.5s since last emit: past the 1s warrior interval but not the 2s base.
        long lastEmit = Now - 1_500;
        Assert.True(KtmThreatOriginator.ShouldEmit(
            threat: 5200, now: Now, lastClientKtmMs: LongAgo,
            lastEmittedValue: 5000, lastEmitMs: lastEmit, interval: 1_000));   // warrior
        Assert.False(KtmThreatOriginator.ShouldEmit(
            threat: 5200, now: Now, lastClientKtmMs: LongAgo,
            lastEmittedValue: 5000, lastEmitMs: lastEmit, interval: 2_000));   // non-warrior
    }
}

using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (ranged auto-repeat per-tick CastID): each auto-repeat tick's SPELL_GO records its
// CastID so the tick's SMSG_SPELL_NON_MELEE_DAMAGE_LOG is stamped with the same id, the way a
// native server keys a shot and its impact. The handler wiring needs a live socket; these pin
// the queue rules it relies on: hits pair in shot order even when a max-range hit lands after
// the next GO, the queue never holds more than the two newest ticks, and a tick whose hit never
// came (target died mid-flight) is skipped instead of shifting every later pairing by one shot.
public class AutoRepeatTickCastIdPairingTests
{
    private const uint Shoot = 5019;
    private static readonly WowGuid128 Caster = new(0x1111, 0x2222);
    private static WowGuid128 Tick(ulong n) => new(n, 0xCA57);

    [Fact]
    public void HitLandingAfterNextGo_PairsWithItsOwnTick()
    {
        var session = GameSessionData.CreateForTesting();
        session.RecordAutoRepeatTickCastId(Caster, Shoot, Tick(1), nowMs: 0);
        session.RecordAutoRepeatTickCastId(Caster, Shoot, Tick(2), nowMs: 1500); // GO 2 before hit 1 lands

        Assert.True(session.TryPairAutoRepeatDamageLog(Caster, Shoot, nowMs: 1600, out var first, out var remaining));
        Assert.Equal(Tick(1), first);
        Assert.Equal(1, remaining);
        Assert.True(session.TryPairAutoRepeatDamageLog(Caster, Shoot, nowMs: 3100, out var second, out remaining));
        Assert.Equal(Tick(2), second);
        Assert.Equal(0, remaining);
        Assert.False(session.TryPairAutoRepeatDamageLog(Caster, Shoot, nowMs: 3200, out _, out _));
    }

    [Fact]
    public void QueueKeepsOnlyTheTwoNewestTicks()
    {
        var session = GameSessionData.CreateForTesting();
        session.RecordAutoRepeatTickCastId(Caster, Shoot, Tick(1), nowMs: 0);
        session.RecordAutoRepeatTickCastId(Caster, Shoot, Tick(2), nowMs: 100);
        session.RecordAutoRepeatTickCastId(Caster, Shoot, Tick(3), nowMs: 200);

        Assert.True(session.TryPairAutoRepeatDamageLog(Caster, Shoot, nowMs: 300, out var first, out _));
        Assert.Equal(Tick(2), first);
        Assert.True(session.TryPairAutoRepeatDamageLog(Caster, Shoot, nowMs: 300, out var second, out _));
        Assert.Equal(Tick(3), second);
    }

    [Fact]
    public void TickWhoseHitNeverCame_IsSkippedNotPairedWithTheNextHit()
    {
        var session = GameSessionData.CreateForTesting();
        session.RecordAutoRepeatTickCastId(Caster, Shoot, Tick(1), nowMs: 0); // target died mid-flight, no damage log
        long later = GameSessionData.AutoRepeatTickCastIdMaxAgeMs + 1000;
        session.RecordAutoRepeatTickCastId(Caster, Shoot, Tick(2), nowMs: later);

        Assert.True(session.TryPairAutoRepeatDamageLog(Caster, Shoot, nowMs: later + 150, out var paired, out var remaining));
        Assert.Equal(Tick(2), paired);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void TicksAreKeyedPerCasterAndSpell()
    {
        var session = GameSessionData.CreateForTesting();
        var otherCaster = new WowGuid128(0x3333, 0x4444);
        session.RecordAutoRepeatTickCastId(Caster, Shoot, Tick(1), nowMs: 0);

        Assert.False(session.TryPairAutoRepeatDamageLog(otherCaster, Shoot, nowMs: 100, out _, out _));
        Assert.False(session.TryPairAutoRepeatDamageLog(Caster, 75, nowMs: 100, out _, out _));
        Assert.True(session.TryPairAutoRepeatDamageLog(Caster, Shoot, nowMs: 100, out var paired, out _));
        Assert.Equal(Tick(1), paired);
    }
}

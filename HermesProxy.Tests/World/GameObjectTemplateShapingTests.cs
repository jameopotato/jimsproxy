using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// Pins the #403 hunter-trap disarm fix: the 1.14 client fabricates
// "Requires Disarm Trap (300)" for lock rows with Skill=0 (vanilla lock 12,
// carried by every hunter trap) and blocks the right-click disarm pre-send.
// RemapTrapLock presents lock 13 (explicit Disarm skill 50 — the lowest disarm
// lock in 1.14.2's Lock.db2) instead, which the client's gate compares honestly.
// Client-display only; the legacy server arbitrates the actual disarm cast.
public class GameObjectTemplateShapingTests
{
    [Fact]
    public void RemapTrapLock_TrapWithVanillaDisarmLock_RemapsTo13()
    {
        Assert.Equal(13, GameObjectTemplateShaping.RemapTrapLock(6, 12));
    }

    [Theory]
    // TRAP-type GOs with any other lock (or no lock) pass through — hazard /
    // environmental traps and dungeon trip-traps keep their real lock.
    [InlineData(6, 0)]
    [InlineData(6, 13)]
    [InlineData(6, 57)]
    public void RemapTrapLock_TrapWithOtherLock_PassesThrough(uint type, int lockId)
    {
        Assert.Equal(lockId, GameObjectTemplateShaping.RemapTrapLock(type, lockId));
    }

    [Theory]
    // Non-TRAP types never touched, even with lock 12 — chests (3), goobers (10,
    // incl. the #387 Rookery Egg after its TRAP->GOOBER override runs first).
    [InlineData(3u)]
    [InlineData(10u)]
    [InlineData(15u)]
    public void RemapTrapLock_NonTrapType_PassesThrough(uint type)
    {
        Assert.Equal(12, GameObjectTemplateShaping.RemapTrapLock(type, 12));
    }
}

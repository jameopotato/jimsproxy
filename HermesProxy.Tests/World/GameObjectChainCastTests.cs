using System;
using HermesProxy;
using HermesProxy.World;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (issue #334): coverage for HasStartedCastOnGameObject — the predicate
// that gates the same-GO chain-cast drop in CMSG_CAST_SPELL handling. The bug it
// fixes: spam-clicking a Whipper Root at the end of its 5s harvest cast caused the
// proxy to hold the chain press and release it 1ms after SPELL_GO, preempting the
// legacy server's loot-creating subspell (15343 "Create Whipper Root Tubers") and
// silently losing the item.
public class GameObjectChainCastTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    private static WowGuid128 MakeGameObjectGuid(uint entry, ulong counter) =>
        WowGuid128.Create(HighGuidType703.GameObject, 0, entry, counter);

    private static WowGuid128 MakeCreatureGuid(uint entry, ulong counter) =>
        WowGuid128.Create(HighGuidType703.Creature, 0, entry, counter);

    private static ClientCastRequest MakeCast(uint spellId, WowGuid128 target, bool hasStarted)
    {
        return new ClientCastRequest
        {
            SpellId = spellId,
            Timestamp = Environment.TickCount,
            TargetGuid = target,
            HasStarted = hasStarted,
        };
    }

    [Fact]
    public void EmptyQueue_ReturnsFalse()
    {
        var session = NewSession();
        var target = MakeGameObjectGuid(164725, 1); // Whipper Root entry id, arbitrary
        Assert.False(session.HasStartedCastOnGameObject(target));
    }

    [Fact]
    public void EmptyTargetGuidArgument_ReturnsFalse()
    {
        // AoE / self-cast presses have an empty TargetGuid. The guard short-circuits
        // before walking the queue so we never match an empty-vs-empty TargetGuid.
        var session = NewSession();
        var goTarget = MakeGameObjectGuid(164725, 1);
        session.PendingNormalCasts.Enqueue(MakeCast(22810, goTarget, hasStarted: true));

        Assert.False(session.HasStartedCastOnGameObject(WowGuid128.Empty));
    }

    [Fact]
    public void NonGameObjectArgument_ReturnsFalse()
    {
        // A unit target shouldn't match a started GO cast even if (impossibly) the
        // GUID values aligned. The high-type guard rejects non-GO arguments outright.
        var session = NewSession();
        var creatureTarget = MakeCreatureGuid(720, 1);
        Assert.False(session.HasStartedCastOnGameObject(creatureTarget));
    }

    [Fact]
    public void StartedCastOnDifferentGameObject_ReturnsFalse()
    {
        // Player completed a harvest on GO A, then a new press arrives targeting GO B.
        // The bug only manifests on same-GO chain spam — different-GO chains must fall
        // through to the existing hold path so mining/herb chain-gather still works.
        var session = NewSession();
        var goA = MakeGameObjectGuid(164725, 1);
        var goB = MakeGameObjectGuid(164725, 2);

        session.PendingNormalCasts.Enqueue(MakeCast(22810, goA, hasStarted: true));

        Assert.False(session.HasStartedCastOnGameObject(goB));
    }

    [Fact]
    public void StartedCastOnSameGameObject_ReturnsTrue()
    {
        // The exact issue #334 scenario: same GO, started cast, new press targeting
        // the same GO. Caller drops the press silently to avoid the post-SPELL_GO race.
        var session = NewSession();
        var go = MakeGameObjectGuid(164725, 7);

        session.PendingNormalCasts.Enqueue(MakeCast(22810, go, hasStarted: true));

        Assert.True(session.HasStartedCastOnGameObject(go));
    }

    [Fact]
    public void PendingCastNotYetStarted_ReturnsFalse()
    {
        // A cast that's been forwarded but hasn't received SPELL_START yet shouldn't
        // trigger the drop — the script-subspell race only opens once the first cast
        // has actually started server-side.
        var session = NewSession();
        var go = MakeGameObjectGuid(164725, 1);

        session.PendingNormalCasts.Enqueue(MakeCast(22810, go, hasStarted: false));

        Assert.False(session.HasStartedCastOnGameObject(go));
    }

    [Fact]
    public void MultipleStartedCasts_MatchingOneInQueue_ReturnsTrue()
    {
        // Defensive: the queue can hold more than one entry. Match if any started cast
        // shares the target.
        var session = NewSession();
        var goA = MakeGameObjectGuid(164725, 1);
        var goB = MakeGameObjectGuid(164725, 2);

        session.PendingNormalCasts.Enqueue(MakeCast(22810, goA, hasStarted: true));
        session.PendingNormalCasts.Enqueue(MakeCast(22810, goB, hasStarted: true));

        Assert.True(session.HasStartedCastOnGameObject(goA));
        Assert.True(session.HasStartedCastOnGameObject(goB));
    }
}

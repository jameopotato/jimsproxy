using System;
using HermesProxy;
using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (#379 form-exit): state tests for the form-exit defer window in GameSessionData.
// The window is armed by HandleCancelAura when the cancelled aura is a shapeshift form, and
// one-shot consumed by the next local SMSG_SPELL_START so it can be deferred past the model swap.
public class FormExitWindowTests
{
    private static GameSessionData NewSession() => GameSessionData.CreateForTesting();

    [Fact]
    public void TryConsumeFormExitWindow_NeverOpened_ReturnsFalse()
    {
        var session = NewSession();
        Assert.False(session.TryConsumeFormExitWindow());
    }

    [Fact]
    public void TryConsumeFormExitWindow_OpenWindow_ConsumesOnce()
    {
        var session = NewSession();
        session.OpenFormExitWindow(500);

        Assert.True(session.TryConsumeFormExitWindow());
        // One-shot: only the auto-shift cast's START is deferred, not later unrelated STARTs.
        Assert.False(session.TryConsumeFormExitWindow());
    }

    [Fact]
    public void TryConsumeFormExitWindow_ExpiredWindow_ReturnsFalse()
    {
        var session = NewSession();
        session.OpenFormExitWindow(-1); // deadline already in the past

        Assert.False(session.TryConsumeFormExitWindow());
    }

    [Fact]
    public void OpenFormExitWindow_Rearm_ExtendsDeadline()
    {
        var session = NewSession();
        session.OpenFormExitWindow(-1); // stale window (form cancelled long ago, no cast)
        session.OpenFormExitWindow(500); // fresh form-cancel re-arms

        Assert.True(session.TryConsumeFormExitWindow());
    }

    [Fact]
    public void ShapeshiftFormSpells_CoversCancellableForms_ExcludesNonForms()
    {
        // Vanilla 1.12 SPELL_AURA_MOD_SHAPESHIFT auras a player can CANCEL_AURA out of.
        Assert.True(GameData.ShapeshiftFormSpells.Contains(768));    // Cat Form
        Assert.True(GameData.ShapeshiftFormSpells.Contains(5487));   // Bear Form
        Assert.True(GameData.ShapeshiftFormSpells.Contains(9634));   // Dire Bear Form
        Assert.True(GameData.ShapeshiftFormSpells.Contains(1066));   // Aquatic Form
        Assert.True(GameData.ShapeshiftFormSpells.Contains(783));    // Travel Form
        Assert.True(GameData.ShapeshiftFormSpells.Contains(24858));  // Moonkin Form
        Assert.True(GameData.ShapeshiftFormSpells.Contains(2645));   // Ghost Wolf
        Assert.True(GameData.ShapeshiftFormSpells.Contains(15473));  // Shadowform

        // Ordinary self-buff cancels must NOT open the window (ice-92's correction in #379).
        Assert.False(GameData.ShapeshiftFormSpells.Contains(1459));  // Arcane Intellect
        Assert.False(GameData.ShapeshiftFormSpells.Contains(1784));  // Stealth (no model swap)
        Assert.False(GameData.ShapeshiftFormSpells.Contains(467));   // Thorns
    }
}

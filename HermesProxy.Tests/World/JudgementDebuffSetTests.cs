using HermesProxy.World;
using Xunit;

namespace HermesProxy.Tests.World;

public class JudgementDebuffSetTests
{
    // The set must contain every paladin Judgement DEBUFF aura (the ones applied to the
    // target) so the local paladin's melee hit can synthesize the duration refresh.
    [Theory]
    [InlineData(20185u)] // Judgement of Light R1
    [InlineData(20344u)] // Judgement of Light R2
    [InlineData(20345u)] // Judgement of Light R3
    [InlineData(20346u)] // Judgement of Light R4
    [InlineData(20186u)] // Judgement of Wisdom R1
    [InlineData(20354u)] // Judgement of Wisdom R2
    [InlineData(20355u)] // Judgement of Wisdom R3
    [InlineData(21183u)] // Judgement of the Crusader R1
    [InlineData(20188u)] // Judgement of the Crusader R2
    [InlineData(20300u)] // Judgement of the Crusader R3
    [InlineData(20301u)] // Judgement of the Crusader R4
    [InlineData(20302u)] // Judgement of the Crusader R5
    [InlineData(20303u)] // Judgement of the Crusader R6
    [InlineData(20184u)] // Judgement of Justice
    public void IsJudgementDebuff_DebuffAura_ReturnsTrue(uint spellId)
    {
        Assert.True(GameData.IsJudgementDebuff(spellId));
    }

    // These must NOT be in the set: the JoL/JoW proc effects (heal/mana cast on the
    // attacker, not a debuff on the target), the intermediate triggers, the Judgement
    // cast itself, and unrelated spells. Including a proc id would refresh a non-debuff.
    [Theory]
    [InlineData(20267u)] // Judgement of Light R1 — heal proc effect
    [InlineData(20341u)] // Judgement of Light R2 — heal proc effect
    [InlineData(20268u)] // Judgement of Wisdom R1 — mana proc effect
    [InlineData(20352u)] // Judgement of Wisdom R2 — mana proc effect
    [InlineData(5373u)]  // Judgement of Light Intermediate
    [InlineData(1826u)]  // Judgement of Wisdom Intermediate
    [InlineData(20271u)] // Judgement (the cast)
    [InlineData(133u)]   // Fireball — unrelated
    public void IsJudgementDebuff_NonDebuff_ReturnsFalse(uint spellId)
    {
        Assert.False(GameData.IsJudgementDebuff(spellId));
    }
}

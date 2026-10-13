using HermesProxy.World;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy pet-bar fix: the proxy forces the six command/stance slots on every forwarded
// SMSG_PET_SPELLS and refuses a client set-action that would drop a spell onto one. The
// shapes here are taken from the capture jimsproxy-20261007-232558 (Voidwalker "Juk'doc").
public class PetActionBarEnforceTests
{
    private static uint Cmd(CommandStates c) => PetActionBar.Pack((uint)c, ActiveStates.Command);
    private static uint React(ReactStates r) => PetActionBar.Pack((uint)r, ActiveStates.Reaction);
    private static uint On(uint spell) => PetActionBar.Pack(spell, ActiveStates.Enabled);
    private static uint Off(uint spell) => PetActionBar.Pack(spell, ActiveStates.Disabled);
    private static uint Passive(uint spell) => PetActionBar.Pack(spell, ActiveStates.Passive);

    private const uint S1 = 0x4bf3, S2 = 0x2dfe, S3 = 0x4558, S4 = 0x45be, GreatStamina = 19596;

    private static uint[] Healthy() => new[]
    {
        Cmd(CommandStates.Attack), Cmd(CommandStates.Follow), Cmd(CommandStates.Stay),
        Off(S1), Off(S2), PetActionBar.Pack(0, ActiveStates.Passive), PetActionBar.Pack(0, ActiveStates.Passive),
        React(ReactStates.Aggressive), React(ReactStates.Defensive), React(ReactStates.Passive),
    };

    // The exact persisted broken bar from the capture: a spell in slot 0, spells in 7-9.
    private static uint[] BrokenFromCapture() => new[]
    {
        Off(S1), Cmd(CommandStates.Follow), Cmd(CommandStates.Stay),
        Off(S2), 0x81000000u, Off(S2), Off(S3),
        Off(S4), 0x81000000u, Off(S3),
    };

    [Fact]
    public void Pack_RoundTrips()
    {
        uint packed = On(S1);
        Assert.Equal(0xC1000000u | S1, packed);
        Assert.Equal(S1, PetActionBar.ActionOf(packed));
        Assert.Equal(ActiveStates.Enabled, PetActionBar.TypeOf(packed));
    }

    [Fact]
    public void Enforce_HealthyBar_UnchangedAndReturnsFalse()
    {
        var bar = Healthy();
        var before = (uint[])bar.Clone();

        bool changed = PetActionBar.EnforceControlSlots(bar);

        Assert.False(changed);
        Assert.Equal(before, bar);
    }

    [Fact]
    public void Enforce_CaptureBrokenBar_RestoresControlsAndRescuesWhatFits()
    {
        var bar = BrokenFromCapture();

        bool changed = PetActionBar.EnforceControlSlots(bar);

        Assert.True(changed);
        Assert.Equal(Cmd(CommandStates.Attack), bar[0]);
        Assert.Equal(Cmd(CommandStates.Follow), bar[1]);
        Assert.Equal(Cmd(CommandStates.Stay), bar[2]);
        Assert.Equal(React(ReactStates.Aggressive), bar[7]);
        Assert.Equal(React(ReactStates.Defensive), bar[8]);
        Assert.Equal(React(ReactStates.Passive), bar[9]);
        // This captured bar had a spell spilled into slot 0 (S1) and slot 7 (S4) plus a
        // third in slot 9 (S3) that is already in an ability slot. With only one free ability
        // slot, S1 (slot 0, processed first) is rescued, S4 can't fit and stays dropped (still
        // in the pet's spell list, re-draggable), S3 is already present. No control slot is
        // left holding a spell.
        Assert.Equal(1, CountSpell(bar, S1));
        Assert.Equal(0, CountSpell(bar, S4));
        Assert.Equal(Cmd(CommandStates.Attack), bar[0]);
    }

    [Fact]
    public void Enforce_TypicalBroken_ControlsKnockedOff_AllRescued()
    {
        // The actual report: Attack and the stances knocked off by spells, abilities intact
        // with room. Everything comes back.
        var bar = new[]
        {
            Off(S1), Cmd(CommandStates.Follow), Cmd(CommandStates.Stay),
            Off(S2), 0u, 0u, 0u,
            Off(S3), React(ReactStates.Defensive), React(ReactStates.Passive),
        };

        bool changed = PetActionBar.EnforceControlSlots(bar);

        Assert.True(changed);
        Assert.Equal(Cmd(CommandStates.Attack), bar[0]);
        Assert.Equal(React(ReactStates.Aggressive), bar[7]);
        Assert.Equal(1, CountSpell(bar, S1)); // rescued from slot 0
        Assert.Equal(1, CountSpell(bar, S2)); // untouched in slot 3
        Assert.Equal(1, CountSpell(bar, S3)); // rescued from slot 7
    }

    [Fact]
    public void Enforce_DisplacedSpellAlreadyOnBar_NotDuplicated()
    {
        var bar = Healthy();
        bar[0] = Off(S1); // S1 is also already in slot 3

        bool changed = PetActionBar.EnforceControlSlots(bar);

        Assert.True(changed);
        Assert.Equal(Cmd(CommandStates.Attack), bar[0]);
        Assert.Equal(1, CountSpell(bar, S1));
    }

    [Fact]
    public void Enforce_EmptyBar_BecomesCommandsAndStancesOnly()
    {
        var bar = new uint[10];

        PetActionBar.EnforceControlSlots(bar);

        Assert.Equal(Cmd(CommandStates.Attack), bar[0]);
        Assert.Equal(React(ReactStates.Passive), bar[9]);
        for (int i = PetActionBar.FirstSpellSlot; i <= PetActionBar.LastSpellSlot; i++)
            Assert.Equal(0u, bar[i]);
    }

    [Fact]
    public void Enforce_PassiveInControlSlot_NotRescuedAsAbility()
    {
        var bar = Healthy();
        bar[8] = Passive(GreatStamina); // a passive buff wrongly in a stance slot

        PetActionBar.EnforceControlSlots(bar);

        Assert.Equal(React(ReactStates.Defensive), bar[8]);
        Assert.Equal(0, CountSpell(bar, GreatStamina)); // passives are never placed on the bar
    }

    [Theory]
    [InlineData(3u, true)]   // ability slot: any write allowed
    [InlineData(6u, true)]
    [InlineData(0u, false)]  // spell dropped on a command slot: refused
    [InlineData(2u, false)]
    [InlineData(7u, false)]  // spell dropped on a stance slot: refused
    [InlineData(9u, false)]
    public void IsClientWriteAllowed_SpellWrite(uint index, bool allowed)
    {
        Assert.Equal(allowed, PetActionBar.IsClientWriteAllowed(index, Off(S1)));
    }

    [Fact]
    public void IsClientWriteAllowed_CanonicalControlWrite_Allowed()
    {
        // The 1.14 client can't send these, but if it did they match and are harmless.
        Assert.True(PetActionBar.IsClientWriteAllowed(0, Cmd(CommandStates.Attack)));
        Assert.True(PetActionBar.IsClientWriteAllowed(9, React(ReactStates.Passive)));
    }

    private static int CountSpell(uint[] bar, uint spell)
    {
        int n = 0;
        foreach (uint slot in bar)
            if (PetActionBar.IsCastable(slot) && PetActionBar.ActionOf(slot) == spell) n++;
        return n;
    }
}

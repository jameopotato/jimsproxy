using HermesProxy.World.Enums;

namespace HermesProxy.World;

// JimsProxy: the pet action bar as the 1.12 server stores it: ten packed slots,
// (action | type << 24). Vanilla CharmInfo::InitPetActionBar lays a fresh pet out as
//   0-2  commands   Attack, Follow, Stay                 (ACT_COMMAND)
//   3-6  abilities  the pet's spells                     (ACT_ENABLED / ACT_DISABLED)
//   7-9  reactions  Aggressive, Defensive, Passive       (ACT_REACTION)
//
// The 1.12 server persists the whole bar per pet, but it only honours a
// CMSG_PET_SET_ACTION that writes a *spell*; a write of a command or reaction button
// is silently dropped (proven in the capture jimsproxy-20261007-232558: a reset wrote
// Attack into slot 0 and the stances into 7-9, and the very next summon came back with
// those slots still holding the spells that had displaced them, while the reset's
// ability write to slot 4 did stick). So once a player drops a spell onto a command or
// stance slot, that control button is gone with no way to put it back from the 1.14
// client, which has no pet-command spellbook entries to drag.
//
// Fix: don't rely on the server storing the control slots at all. The proxy forces the
// six control slots to their canonical buttons on every SMSG_PET_SPELLS it forwards
// (EnforceControlSlots), so a broken pet heals on its next summon, and it refuses a
// client set-action that would drop a spell onto one of those slots
// (IsClientWriteAllowed), so the breakage can't recur.
public static class PetActionBar
{
    public const int SlotCount = 10;
    public const int FirstSpellSlot = 3;
    public const int LastSpellSlot = 6;

    private static readonly uint[] ControlSlots = { 0, 1, 2, 7, 8, 9 };

    private const uint ActionMask = 0x00FFFFFF;

    public static uint Pack(uint action, ActiveStates type) => (action & ActionMask) | ((uint)type << 24);
    public static uint ActionOf(uint packed) => packed & ActionMask;
    public static ActiveStates TypeOf(uint packed) => (ActiveStates)(packed >> 24);

    // A castable spell: on the bar or in the SMSG_PET_SPELLS spell list with an autocast
    // state. Passives and empty slots (action 0) are never placed.
    public static bool IsCastable(uint packed)
    {
        var type = TypeOf(packed);
        return ActionOf(packed) != 0 && (type == ActiveStates.Enabled || type == ActiveStates.Disabled);
    }

    public static bool IsControlSlot(uint index) => index <= 2 || (index >= 7 && index <= 9);

    // The button vanilla fixes into a control slot. 0 for an ability slot.
    public static uint CanonicalControl(uint index) => index switch
    {
        0 => Pack((uint)CommandStates.Attack, ActiveStates.Command),
        1 => Pack((uint)CommandStates.Follow, ActiveStates.Command),
        2 => Pack((uint)CommandStates.Stay, ActiveStates.Command),
        7 => Pack((uint)ReactStates.Aggressive, ActiveStates.Reaction),
        8 => Pack((uint)ReactStates.Defensive, ActiveStates.Reaction),
        9 => Pack((uint)ReactStates.Passive, ActiveStates.Reaction),
        _ => 0,
    };

    // A client set-action may touch any ability slot, but a control slot only with its
    // own canonical button (which the 1.14 client can't send anyway — any spell write
    // there is the displacement bug, so it is refused).
    public static bool IsClientWriteAllowed(uint index, uint action)
        => !IsControlSlot(index) || action == CanonicalControl(index);

    /// <summary>
    /// Forces the six control slots to their canonical command/stance buttons, in place.
    /// A castable spell evicted from a control slot is rescued into the first free ability
    /// slot if it is not already on the bar (dropped only if 3-6 are all full). Ability
    /// slots are otherwise left exactly as they came. Returns true if anything changed, so
    /// a healthy bar is forwarded untouched.
    /// </summary>
    public static bool EnforceControlSlots(uint[] buttons)
    {
        if (buttons == null || buttons.Length < SlotCount)
            return false;

        bool changed = false;
        foreach (uint slot in ControlSlots)
        {
            uint want = CanonicalControl(slot);
            if (buttons[slot] == want)
                continue;

            uint evicted = buttons[slot];
            buttons[slot] = want;
            changed = true;
            if (IsCastable(evicted))
                Rescue(buttons, evicted);
        }
        return changed;
    }

    private static void Rescue(uint[] buttons, uint spell)
    {
        uint id = ActionOf(spell);
        for (int i = FirstSpellSlot; i <= LastSpellSlot; i++)
            if (ActionOf(buttons[i]) == id)
                return; // already on the bar
        for (int i = FirstSpellSlot; i <= LastSpellSlot; i++)
            if (ActionOf(buttons[i]) == 0)
            {
                buttons[i] = spell;
                return;
            }
        // no free ability slot: the spell stays in the pet's spell list, re-draggable.
    }
}

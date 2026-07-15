namespace HermesProxy.World;

// JimsProxy (#403): GameObject template shaping for the modern client.
//
// The 1.14 client gates GO right-click interaction on the template's lock row
// (Lock.db2). For lock rows whose required Skill is 0, the client FABRICATES a
// requirement instead of treating 0 as "no gate" the way the 1.12 client did —
// hunter traps (vanilla lock 12, Skill=0) render "Requires Disarm Trap (300)"
// and the client refuses the right-click auto-cast pre-send (the disarm never
// reaches the wire; only a CMSG_GAME_OBJ_REPORT_USE ping goes out). Explicitly
// casting Disarm Trap (1842) from the spellbook bypasses the GO gate and the
// legacy server happily executes the disarm, proving the gate is client-local.
//
// Fix: remap trap lock 12 -> 13. Lock 13 carries an explicit Disarm skill of 50
// — the lowest disarm lock in 1.14.2's Lock.db2 (the only other is 55 = 200) —
// which the client compares honestly. In-game verified 2026-07-14 (Kronos PTR,
// level-60 rogue vs hunter): right-click disarm works end-to-end and the server
// flips the trap's GO flags to InUse on success.
//
// The remap is client-display only: the query response never returns to the
// legacy server, which arbitrates the actual disarm cast with its own template.
// Known limitation: the client-side gate under lock 13 needs the player's
// disarm-relevant skill >= 50; low-skill rogues keep the spellbook workaround.
public static class GameObjectTemplateShaping
{
    public const uint GameObjectTypeTrap = 6;
    public const int VanillaDisarmLock = 12;      // Skill=0 -> client fabricates "(300)"
    public const int ModernDisarmLockSkill50 = 13; // explicit Disarm 50, lowest in 1.14.2

    // Returns the lock id (Data[0]) to present to the modern client for a GO
    // template of the given type. Pass-through for everything except TRAP-type
    // templates carrying the vanilla disarm lock.
    public static int RemapTrapLock(uint goType, int lockId)
    {
        if (goType == GameObjectTypeTrap && lockId == VanillaDisarmLock)
            return ModernDisarmLockSkill50;
        return lockId;
    }
}

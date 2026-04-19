// JimsProxy: opcodes we know cannot be translated because the subsystem
// simply didn't exist in 1.12.1. Silencing these in the console + downgrading
// the JSONL event from "packet.untranslated" (implies bug) to "packet.ignored"
// (implies intentional no-op) lets real Kronos-specific translation gaps
// stand out when reading logs.
//
// Source list: RESEARCH.md §4.Z — each subsystem has a citation-worthy reason
// for being absent in vanilla. Grow this list cautiously; when in doubt, leave
// the opcode in the untranslated pile so we at least get a warning for it.

using System.Collections.Generic;
using HermesProxy.World.Enums;

namespace HermesProxy.World;

/// <summary>
/// Opcodes that originate from modern-client subsystems that didn't exist
/// in 1.12.1 vanilla and therefore have no sensible translation target.
/// Safe to drop silently.
/// </summary>
public static class KnownBenignOpcodes
{
    public static readonly HashSet<Opcode> ModernOnly = new()
    {
        // Battle Pay / in-game shop (introduced Cataclysm/MoP)
        Opcode.CMSG_BATTLE_PAY_ACK_FAILED_RESPONSE,
        Opcode.CMSG_BATTLE_PAY_CANCEL_OPEN_CHECKOUT,
        Opcode.CMSG_BATTLE_PAY_CONFIRM_PURCHASE_RESPONSE,
        Opcode.CMSG_BATTLE_PAY_DISTRIBUTION_ASSIGN_TO_TARGET,
        Opcode.CMSG_BATTLE_PAY_DISTRIBUTION_ASSIGN_VAS,
        Opcode.CMSG_BATTLE_PAY_GET_PRODUCT_LIST,
        Opcode.CMSG_BATTLE_PAY_GET_PURCHASE_LIST,
        Opcode.CMSG_BATTLE_PAY_OPEN_CHECKOUT,
        Opcode.CMSG_BATTLE_PAY_REQUEST_PRICE_INFO,
        Opcode.CMSG_BATTLE_PAY_START_PURCHASE,
        Opcode.CMSG_BATTLE_PAY_START_VAS_PURCHASE,
        Opcode.CMSG_UPDATE_VAS_PURCHASE_STATES,
        Opcode.CMSG_GET_UNDELETE_CHARACTER_COOLDOWN_STATUS,

        // Calendar v2 (Wrath added a basic calendar; 1.14 replaced it)
        Opcode.CMSG_CALENDAR_GET_NUM_PENDING,

        // LFG v2 / Group Finder UI (Wrath and later)
        Opcode.CMSG_LFG_LIST_GET_STATUS,

        // Battle Pets / Pet Battles (MoP)
        Opcode.CMSG_BATTLE_PET_REQUEST_JOURNAL,

        // Achievements / Guild Achievements (Wrath / Cataclysm)
        Opcode.CMSG_GUILD_SET_ACHIEVEMENT_TRACKING,

        // Modern client telemetry (all post-vanilla; safe to drop)
        Opcode.CMSG_REPORT_CLIENT_VARIABLES,
        Opcode.CMSG_REPORT_ENABLED_ADDONS,
        Opcode.CMSG_REPORT_KEYBINDING_EXECUTION_COUNTS,
        Opcode.CMSG_VIOLENCE_LEVEL,
        Opcode.CMSG_DISCARDED_TIME_SYNC_ACKS,

        // Queued login messages end marker (modern Bnet)
        Opcode.CMSG_QUEUED_MESSAGES_END,

        // GM v2 (Wrath reworked the ticket system)
        Opcode.CMSG_GM_TICKET_GET_CASE_STATUS,

        // Cooldown categories, forced reactions, countdown timers,
        // cemetery list UI — all introduced post-Wrath
        Opcode.CMSG_REQUEST_CATEGORY_COOLDOWNS,
        Opcode.CMSG_REQUEST_FORCED_REACTIONS,
        Opcode.CMSG_QUERY_COUNTDOWN_TIMER,
        Opcode.CMSG_REQUEST_CEMETERY_LIST,

        // Modern UI interactions (Legion+)
        Opcode.CMSG_CLOSE_INTERACTION,
        Opcode.CMSG_QUERY_QUEST_COMPLETION_NPCS,

        // Added 2026-04-17 from Block 1 Test 1.1 cycle 1:
        Opcode.CMSG_MOVE_SET_COLLISION_HEIGHT_ACK, // Wrath+ mount-resizes-hitbox ack
        Opcode.CMSG_GUILD_GET_RANKS,               // Cata+ guild UI (annotated "// Cata only" in Opcodes.cs)

        // Added 2026-04-17 from Block 1 Test 1.2 (20-min AFK session):
        Opcode.CMSG_GET_ACCOUNT_NOTIFICATIONS,     // Modern account notification poll (MoP+)

        // Rest-XP notification packet — post-Wrath server→client
        // (Note: the modern client still expects this for rested XP banner
        //  in the player frame. Not translating means no visual feedback
        //  for rested state, but gameplay is unaffected. Filed as backlog.)
        Opcode.SMSG_SET_REST_START,
    };

    /// <summary>True if the opcode is known to originate from a modern-client
    /// subsystem that didn't exist in 1.12.1 and has no translation target.</summary>
    public static bool IsModernOnly(Opcode opcode) => ModernOnly.Contains(opcode);
}

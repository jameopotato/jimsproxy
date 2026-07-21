// JimsProxy: opcodes where the other side of the protocol-bridge has no
// equivalent, so there's nothing to translate. Two shapes:
//   (1) Modern client sends c2s for a subsystem that never existed in 1.12
//       (Battle Pay, Calendar v2, etc.) -- the legacy server would ignore it
//       if we forwarded it.
//   (2) Legacy server sends s2c for a 1.12-only opcode with no dispatch slot
//       in the 1.14.2 client (SMSG_SET_REST_START) -- the modern client gets
//       the equivalent UX via a different channel (update fields), or the
//       concept has no modern rendering at all.
// Silencing these in the console + downgrading the JSONL event from
// "packet.untranslated" (implies bug) to "packet.ignored" (implies
// intentional no-op) lets real translation gaps stand out when reading logs.
//
// Each entry has a citation-worthy reason for being absent on the destination
// side. Grow this list cautiously;
// when in doubt, leave the opcode in the untranslated pile so we at least get
// a warning for it.
//
// PROCESS RULE (2026-07-20 audit): before adding an s2c opcode here, verify it
// has NO entry in Enums/V2_5_3_41750/Opcode.cs — the table 1.14.2 builds
// dispatch from (per Opcodes.GetOpcodesDefiningBuild; NOT V1_14_1_40688). An
// entry there means the client CAN consume it and the right fix is a
// translation, not a drop. Cautionary tale: "MSG_MOVE_TIME_SKIPPED has no
// modern equivalent" was false (SMSG_MOVE_SKIP_TIME 0x2E18 exists) — it is
// translated, not benign-listed (PR #434), and must never appear in this
// list. For c2s entries the check is V1_12_1_5875/Opcode.cs absence.
// Full triage: DROPPED-S2C-AUDIT.md.

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

        //MIRASU: Cata+ live party-window update poll. Modern client sends this
        //MIRASU: when the party UI opens; 1.12 servers push stats via SMSG_PARTY_MEMBER_*
        //MIRASU: unsolicited, so there's nothing to translate. Silence it.
        Opcode.CMSG_REQUEST_PARTY_JOIN_UPDATES,

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

        // Vanilla rest-state timer packet (0x21E, u32 restStart). No dispatch
        // slot in the 1.14.2 table (absent from V2_5_3_41750), and nothing is
        // lost: rested/resting UI on the modern client is driven by the
        // RestInfo update fields, which UpdateHandler already translates from
        // PLAYER_REST_STATE_EXPERIENCE / PLAYER_BYTES_2 (UpdateHandler.cs
        // ~3774). An earlier note here claimed the modern client "still
        // expects this for the rested banner" — wrong on both counts.
        Opcode.SMSG_SET_REST_START,

        // ── Added 2026-07-20 from the corpus-wide dropped-s2c audit ──
        // (DROPPED-S2C-AUDIT.md; every entry mechanically verified: s2c
        // absent from V2_5_3_41750, c2s absent from V1_12_1_5875.)

        // Kronos sends 1.12 Warden module data (~2×/session, 39 bytes, seen
        // in 414/520 corpus sessions). There is no bridge: the 1.14 client
        // speaks Warden3 (a different module system), and anti-cheat
        // bridging is out of scope. The corpus shows Kronos tolerates the
        // unanswered handshake (hours-long sessions throughout).
        Opcode.SMSG_WARDEN_DATA,

        // GO spawn-in animation (0x214, u64 goGuid). No 1.14.2 slot; modern
        // clients convey spawn animation via GO create/update state.
        // Cosmetic-only loss.
        Opcode.SMSG_GAMEOBJECT_SPAWN_ANIM,

        // Chain-visual retarget list (0x330). No 1.14.2 slot; modern chain
        // visuals ride SPELL_GO hit targets.
        Opcode.SMSG_SPELL_UPDATE_CHAIN_TARGETS,

        // Ack for the proxy-synthesized SMSG_SUSPEND_TOKEN
        // (Client/PacketHandlers/MovementHandler.cs sends it fire-and-forget;
        // nothing gates on the response).
        Opcode.CMSG_SUSPEND_TOKEN_RESPONSE,

        // Modern AH "pending sales" poll; vanilla delivers sale results as
        // mail — there is no queryable pending-sales list to answer with.
        Opcode.CMSG_AUCTION_LIST_PENDING_SALES,

        // WoW Token purchase-log poll (retail shop subsystem).
        Opcode.CMSG_COMMERCE_TOKEN_GET_LOG,

        // Party role (tank/heal/dps) assignment — vanilla has no relay
        // channel; role icons stay client-local.
        Opcode.CMSG_SET_ROLE,

        // Character-list reorder persistence (modern account feature).
        Opcode.CMSG_REORDER_CHARACTERS,

        // Hardware survey / advanced-combat-logging toggle / follow-usage
        // telemetry — no legacy destination.
        Opcode.CMSG_ENGINE_SURVEY,
        Opcode.CMSG_SET_ADVANCED_COMBAT_LOGGING,
        Opcode.CMSG_USED_FOLLOW,

    };

    /// <summary>True if the opcode is known to originate from a modern-client
    /// subsystem that didn't exist in 1.12.1 and has no translation target.</summary>
    public static bool IsModernOnly(Opcode opcode) => ModernOnly.Contains(opcode);
}

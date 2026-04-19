# JimsProxy Diagnostics Log

Findings from in-game playtesting on Kronos. Each session is captured by Phase 1's
JSONL writer (`Logs/jimsproxy-*.jsonl`); analyzed with
`launcher-tauri/scripts/analyze-jsonl.ps1`. Bugs surface as either
`packet.untranslated` (missing handler) or `packet.error` (handler threw).

## Status legend

- **OPEN** — observed, no fix yet
- **FIX IN PROGRESS** — handler being written
- **FIXED** — handler shipped, link to commit
- **WONTFIX** — out of scope (e.g. modern-only subsystem; goes on KNOWN_BENIGN)

---

## 2026-04-18 — Block 2 priest leveling (post-Xian55-rebase)

**Session:** `jimsproxy-20260418-222249.jsonl` (16,159 events, ~10 min, JAMEOPOTATO priest L1->3 in Northshire)
**Build:** JimsProxy 4.2.5+7(20f0505) on Xian55 v4.2.4 + 7 fork commits
**Translation rate:** 98.4% (7,971 of 8,104 packet.in translated; 64 ignored as KNOWN_BENIGN)
**Errors:** 0 packet.error
**Disconnects:** 0 unexpected
**Verdict:** 3 non-Warden untranslated opcodes — see below.

### SMSG_SPELL_FAILURE (307) — FIX IN PROGRESS

**Hits:** 7 in 10 min (every spell failure during leveling)
**Symptom in-game:** Caster spells feel unresponsive. When Smite/Heal/SW:Pain fails for OOM, OOR, or LoS, the modern client never receives the failure notification. The cast bar doesn't reset; the spell button stays in the "casting" visual state until the next valid cast clears it. The player has no idea why their spell didn't go off.

**Root cause:** No s2c handler in Xian55's tree. The sibling `SMSG_SPELL_FAILED_OTHER` (broadcast variant for nearby observers) IS handled in `SpellHandler.cs:267`, but the caster-direct `SMSG_SPELL_FAILURE` was missed. Modern packet class `SpellFailure` already exists in `SpellPackets.cs:916`; only the s2c parser+forwarder was missing.

**Fix:** New `[PacketHandler(Opcode.SMSG_SPELL_FAILURE)]` in `SpellHandler.cs` mirroring the FAILED_OTHER pattern (caster GUID, spell ID, reason byte, pending-cast lookup, modern packet construction). The 1.12 wire format includes the reason byte (unlike the broadcast variant), so we read it via `packet.CanRead()` + `LegacyVersion.ConvertSpellCastResult`.

**Verification:** New gameplay session on Kronos with priest spells failing intentionally (cast Smite at impossible range, cast Heal with no mana). Expect zero `packet.untranslated SMSG_SPELL_FAILURE` in resulting JSONL; expect cast bar to reset visually with appropriate red error text.

### SMSG_TRAINER_BUY_SUCCEEDED (435) — OPEN

**Hits:** 2 in 10 min (priest learned 2 spells from trainer at L2 / L3)
**Symptom in-game:** No "You learn X" banner / sound when buying spells from a trainer. The trainer UI doesn't refresh to mark the now-learned spell as ✓; player has to close and reopen the window.

**Root cause:** No s2c handler. The `SMSG_TRAINER_BUY_SUCCEEDED` packet from 1.12 mangos is dropped silently after the spell is actually learned (the learning itself works because the spellbook update arrives via `SMSG_LEARNED_SPELL`); only the visual confirmation is lost.

**Fix scope:** Mid-effort. Need to verify the modern client's expected packet layout (likely `SMSG_TRAINER_BUY_RESULT` or similar), construct it, send. Likely 30 lines of handler code following the pattern of other trainer-related handlers.

### MSG_MOVE_TIME_SKIPPED (793, s2c) — OPEN

**Hits:** 1 in 10 min
**Symptom in-game:** None observed.
**Notes:** This opcode is normally CMSG (client→server) for the client to inform the server it was unfocused/paused for some duration. Server-originated `MSG_MOVE_TIME_SKIPPED` is unusual and may be Kronos-specific (anti-cheat clock-sync probe? NPC time-skip relay for nearby observers?). Low priority — investigate if it correlates with any rubber-banding reports later.

**Status:** OPEN — needs more data. May be benign and graduate to KNOWN_BENIGN.

---

## Methodology notes

### Combat correctness gate (RESEARCH.md section 4.C)

The current `analyze-jsonl.ps1` reports "88 of 97 casts >200ms to SMSG_SPELL_GO" — this is a **false-positive-heavy** metric. The script naively pairs every `CMSG_CAST_SPELL` with the next `SMSG_SPELL_GO`, but `SMSG_SPELL_GO` is broadcast for ALL nearby spell activity (other priests training, mobs casting, ambient FX), not just the player's spells. To get a real combat-correctness signal, the JSONL needs to expose the caster GUID on `SMSG_SPELL_GO` so we can filter to the player's own casts. Backlog item.

### Allowlist validation

All 64 `packet.ignored` events in this session matched our KNOWN_BENIGN allowlist (Battle Pay, Calendar v2, telemetry, etc.) — confirming the post-rebase allowlist is sized correctly. New entries from Block 1 v2 (`CMSG_GET_ACCOUNT_NOTIFICATIONS`, `CMSG_GUILD_GET_RANKS`, `CMSG_MOVE_SET_COLLISION_HEIGHT_ACK`) all hit during this session, validating the additions.

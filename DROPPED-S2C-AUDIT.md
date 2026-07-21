# Dropped s2c packet audit — JimsProxy log corpus

**Date:** 2026-07-20 · **Code audited at:** beta HEAD `e5a6898` (this worktree) · **Read-only pass — no code changed.**

Goal: find legacy server→client opcodes the proxy drops that the 1.14 client could actually consume
(generalizing the `MSG_MOVE_TIME_SKIPPED` precedent). Primary discovery channel: the structured JSONL
drop events (`packet.untranslated`, `packet.ignored`).

---

## 1. Corpus

| | |
|---|---|
| Files scanned | **521** JSONL (520 unique sessions; 1 duplicate skipped by name+size) |
| Volume | **3.50 GB**, 19,027,372 lines, **0 parse errors** |
| Date span | 2026-05-03 → 2026-07-20 (today) |
| Proxy versions | 5.1.5-beta.6 → **5.1.9-beta.2** (all recent builds still show every finding below) |
| Sources | box1 `kronoswow\Hermes\Logs` (176), box2 `kronoswow_box2\Hermes\Logs` (27), 14 test-build folders `Downloads\JimsProxy-*\Logs` (~250), tester-exported logs `Downloads\` root + `drive-download-*` (43, incl. 300 MB raid-day sessions) |
| Searched, empty | Desktop diagnostics zips (none exist), `Desktop\World of Warcraft`, `proxy-publish-*` |

Schema verified against `Framework/Logging/Log.cs` and the two emit sites
(`WorldClient.cs:674/691` s2c, `WorldSocket.cs:430/441` c2s). Payload keys are snake_case
(`direction` = `"s2c"`/`"c2s"`, `opcode_universal`, `opcode_raw`, `size`, `reason`).
`size` is consistent with **2-byte legacy opcode word + body** across all 12 s2c opcodes.
`opcode_raw` for s2c is the **legacy** (1.12) opcode value. `guid.legacy.unknown` (1,299 hits)
confirmed as a separate class — excluded.

**Opcode-table correction to the audit brief:** 1.14.2 builds resolve to the
**`V2_5_3_41750`** opcode table, not `V1_14_1_40688` (`Opcodes.GetOpcodesDefiningBuild`,
`Enums/Opcodes.cs:70-76`). All "modern slot" verdicts below are against `V2_5_3_41750`
(cross-checked against `V1_14_1_40688`; identical presence for every candidate).

## 2. Headline

45 distinct dropped opcodes: **12 s2c** (the target class), 33 c2s.
**8 of 12 s2c opcodes have a 1.14.2 dispatch slot.** Six are trivially translatable.
One active misclassification landmine found in an unmerged branch (§6).

## 3. Ranked triage table — s2c drops

Sizes are min/mode/max (incl. 2-byte legacy opcode). "Slot" = present in `V2_5_3_41750`.

| # | Universal opcode | Legacy → 1.14.2 raw | Hits | Sess | Size | Slot | Feasibility | Plausible lost behavior | Value | Conf |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `MSG_MOVE_TIME_SKIPPED` | 0x319 → `SMSG_MOVE_SKIP_TIME` 0x2E18 | **15,084** | **236** | 8/9/10 | **Y** | **trivial** | Observed players' movement time-base never corrected after their client hitches → jerky/lagging/warping *other* players | **STRONG** | high |
| 2 | `SMSG_PET_MODE` | 0x17A → 0x258C | 297 | 4 | 14 | **Y** | **trivial** | Pet action-bar react/command state never server-synced (summon restore, charm/possess, server-side changes) | **STRONG** (pet classes; visible impact unconfirmed) | med |
| 3 | `SMSG_QUEST_LOG_FULL` | 0x195 → 0x2A87 | 14 | 11 | 2 | **Y** | **trivial** (empty body) | Accepting a quest with a full log silently does nothing — no error shown | **STRONG** (per-event UX; classic "impossible to describe" bug) | med-high |
| 4 | `SMSG_SPELL_OR_DAMAGE_IMMUNE` | 0x263 → 0x2C2F | 48 | 7 | 23 | **Y** | trivial | No "Immune" feedback for spells on immune targets (melee immune comes via ATTACKERSTATEUPDATE, which is translated) | WEAK | high |
| 5 | `SMSG_PROC_RESIST` | 0x260 → 0x2752 | 557 | 33 | 23 | **Y** | trivial-moderate | Missing combat-log line when a weapon/aura proc is resisted → procs look like they silently did nothing | WEAK | med |
| 6 | `SMSG_ITEM_TIME_UPDATE` | 0x1EA → 0x274B | 69 | 12 | 14 | **Y** | trivial | Duration-limited items don't tick/expire visibly; item countdown stale until relog | WEAK | high |
| 7 | `SMSG_SPELL_MISS_LOG` | 0x24B → 0x2C41 | 14 | 7 | 28 | **Y** | moderate (modern shape unverified) | Spell-miss combat-log entries for the log-only miss path | WEAK | low-med |
| 8 | `SMSG_UPDATE_LAST_INSTANCE` | 0x320 → 0x2681 | 4 | 4 | 6 | **Y** (struct exists, already synthesized at world-transfer, `Client/…/MovementHandler.cs:539`) | trivial | Last-instance hint outside transfers | WEAK→NONE | high |
| 9 | `SMSG_SET_REST_START` | 0x21E → **none** | 870 | 405 | 6 | **N** | n/a | **None** — rested state is already delivered via `RestInfo` update fields (`UpdateHandler.cs:3774-3830`) | NONE | high |
| 10 | `SMSG_WARDEN_DATA` | 0x2E6 → (Warden3 family only) | 839 | 414 | 39 | N (semantically) | not feasible/desirable | Anti-cheat bridging out of scope; Kronos demonstrably tolerates unanswered Warden (hours-long sessions) | NONE (class C) | high |
| 11 | `SMSG_GAMEOBJECT_SPAWN_ANIM` | 0x214 → **none** | 421 | 36 | 10 | **N** | hard/speculative | GO spawn-in animation cosmetic (body = u64 GO guid only) | NONE→WEAK | med |
| 12 | `SMSG_SPELL_UPDATE_CHAIN_TARGETS` | 0x330 → **none** | 9 | 3 | 26 | **N** | n/a | Chain-visual retargeting; modern chain visuals ride SPELL_GO | NONE | high |

No bimodal size distributions — the only size variance (#1: 8/9/10) is packed-GUID length, not sub-cases.

**High-frequency-everywhere flag (per brief):** `MSG_MOVE_TIME_SKIPPED` is the only s2c opcode
dropped at high frequency in essentially every *play* session (236/520; absent only in login-only
or solo-empty sessions; 7,322 hits in a single tester raid day). SET_REST_START/WARDEN appear in
~80% of sessions but are the NONE class.

## 4. Top translate-next candidates

### 4.1 `MSG_MOVE_TIME_SKIPPED` → `SMSG_MOVE_SKIP_TIME` — the flagship
- **Mechanism [SOURCED]:** vanilla server relays a nearby player's time skip to observers:
  vmangos `HandleMoveTimeSkippedOpcode` rebroadcasts `MSG_MOVE_TIME_SKIPPED {PackedGuid mover, uint32 lag}`
  to the move-set. Modern TrinityCore does the *identical* thing with the *identical* trigger, as
  `SMSG_MOVE_SKIP_TIME {MoverGUID, uint32 TimeSkipped}` (`WorldSession::HandleMoveTimeSkippedOpcode`,
  TC master `MovementHandler.cpp`). The 1.14.2 client both **sends** the CMSG (we already forward it,
  `Server/PacketHandlers/MovementHandler.cs:341`) and **has the dispatch slot** for the SMSG (0x2E18).
  Retail-era clients receive this packet routinely — it keeps the mover's movement-time base aligned.
- **What dropping costs [INFERENCE, strong]:** when another player's client hitches (alt-tab, loading,
  long GC), all their subsequent movement packets carry timestamps advanced by the skipped amount. Our
  client never learns about the skip → observed-mover interpolation runs on a stale time base → the
  *other* player appears to lag behind / warp / jitter until something re-syncs. This is squarely in the
  long-standing "observed players stuck/warping" complaint family (same observed-unit-fidelity bucket
  as #352's drawn-bow fix). 15k occurrences across every multiplayer session make it the highest-volume
  untranslated s2c opcode by 18×.
- **Implementation sketch:** Client handler reads PackedGuid + uint32 → translate GUID legacy→modern →
  new `MoveSkipTime` ServerPacket (guid128 + uint32; struct doesn't exist yet, pattern = `SuspendToken`,
  `MovementPackets.cs:773`). Guard: skip if the unit is destroyed/unknown to the client (same rationale
  as the existing `movement.dropped_stray_after_destroy` guard — 53k such events show stray movement
  after destroy is common). The MOVEMENT_SYNTH_PATTERN.md deferred-synth trap does **not** apply (this
  is a peer-mover broadcast, not a self-movement reset) — but see Unknown U1.
- **Blocker to kill (§6):** `mongrul/alpha` commit `eb31138` silently benign-lists this opcode on the
  false premise that "the modern 1.14 client doesn't have an equivalent inbound opcode." It must not
  merge; the correct disposition is translation.

### 4.2 `SMSG_PET_MODE` — pet-bar state sync (hunters/warlocks)
- **Mechanism:** vanilla sends `{u64 petGuid, u32 packedMode}` (body 12 = observed size 14−2)
  whenever the pet's react/command state is set server-side (stance clicks are acked this way; charm/
  possess and summon-restore also emit it) [reference-core knowledge; packing layout to verify against
  cmangos-classic at impl]. Modern packet [SOURCED, TC master `PetPackets.cpp`]:
  `PetMode { PetGUID; uint8 CommandState; uint8 Flag; uint8 ReactState }` — same information, unpacked.
- **Cost of dropping:** every server-side mode change is invisible; the pet bar shows whatever the
  client last assumed. 242 drops in one tester hunter session (2026-05-26) = the whole session's worth
  of acks. Self-clicks are probably optimistic client-side, so the visible loss concentrates in
  desync cases (state restored on summon, feign death, charm) — mechanism solid, user-visible impact
  **unconfirmed** (no pet-bar bug reports in hand; also nobody has played a pet class in the corpus
  since May 26, which is why last-seen is old — the gap is live on beta, no handler exists).
- **Gain:** closes an entire class-family desync channel for two classes, at ~20 lines.

### 4.3 `SMSG_QUEST_LOG_FULL` — un-silence a silent failure
- Empty-body packet, slot 0x2A87, modern struct is literally
  `QuestLogFull : ServerPacket(SMSG_QUEST_LOG_FULL, 0)` [SOURCED, TC master `QuestPackets.h`].
  Translation = forward the opcode. 11 different sessions hit it — real players with full logs clicked
  "Accept" and got nothing. Only open question is whether the 1.14 client renders the red error from
  this opcode or expects `SMSG_DISPLAY_GAME_ERROR` (U4) — send-and-see, zero risk.

### 4.4 Combat-log fidelity bundle (one small PR)
`SMSG_SPELL_OR_DAMAGE_IMMUNE` (body u64+u64+u32+u8 → modern `{CasterGUID, VictimGUID, uint32 SpellID,
1-bit IsPeriodic}` [SOURCED]) + `SMSG_PROC_RESIST` (→ modern `{Caster, Target, int32 SpellID, optional
Rolled/Needed}` — send with optionals absent [SOURCED]) + `SMSG_ITEM_TIME_UPDATE` (u64+u32 → identical
modern shape [SOURCED]). All three are read-N-fields → write-N-fields translations with no state.
Individually WEAK; bundled they remove ~700 corpus drops of player-visible combat-log/countdown loss.

## 5. Correctly dropped — leave alone (do not re-litigate)

**s2c:**
- `SMSG_WARDEN_DATA` — class C. Bridging 1.12 Warden modules to a 1.14 client is neither feasible nor
  desirable; corpus proves Kronos tolerates unanswered Warden. *Housekeeping suggestion (not done):*
  re-tag to `packet.ignored` with an accurate reason — it is the #3 noisiest `untranslated` and is pure
  alarm fatigue.
- `SMSG_SET_REST_START` — correctly benign **in effect** (no 1.14.2 slot), but the entry's comment is
  wrong in both directions (§6).
- `SMSG_GAMEOBJECT_SPAWN_ANIM`, `SMSG_SPELL_UPDATE_CHAIN_TARGETS` — no 1.14.2 slot; cosmetic loss only.
  Benign-list candidates *with correct citations* ("absent from V2_5_3_41750"), not with vibes.

**c2s ignored list (all 34 entries):** mechanically validated — **zero** of them exist in the
`V1_12_1_5875` table (script in §8). Semantic-twin caveats, all resolved safe:
`CMSG_GUILD_GET_RANKS` (ranks already push-synthesized, `Client/…/GuildHandler.cs:279` → `SMSG_GUILD_RANKS`),
`CMSG_REQUEST_FORCED_REACTIONS` (vanilla is push-based, rare content), `CMSG_GM_TICKET_GET_CASE_STATUS`
(login poll; vanilla ticket family is separate — watch the help UI, low risk).

**c2s untranslated → benign-able** (scoped separately from this audit's s2c target, listed for the
worklist): `CMSG_AUCTION_LIST_PENDING_SALES` (1,849), `CMSG_COMMERCE_TOKEN_GET_LOG` (1,606),
`CMSG_SET_ROLE`, `CMSG_REORDER_CHARACTERS`, `CMSG_ENGINE_SURVEY`, `CMSG_SET_ADVANCED_COMBAT_LOGGING`,
`CMSG_USED_FOLLOW` — all with no 1.12 target. Two that are *not* mere noise:
- `CMSG_SUSPEND_TOKEN_RESPONSE` (147/60 sessions): the proxy itself **sends** `SuspendToken`
  (`Client/…/MovementHandler.cs:409`) — the client's ack should be consumed by a no-op handler, not
  logged as a translation gap.
- `CMSG_EMOTE` (1,260/216 sessions): the **only** c2s untranslated opcode with a same-name 1.12 opcode
  (0x102). Modern client sends it empty (stop-emote); vanilla expects `u32 emoteId`. Possible translate
  = forward with `ONESHOT_NONE` to clear state-emotes for observers [INFERENCE — verify vanilla
  HandleEmoteOpcode semantics before touching].

## 6. KnownBenignOpcodes re-audit

- **List-level: sound.** All 35 entries hold against the opcode tables (34 c2s absent from 1.12;
  the 1 s2c entry absent from 1.14.2).
- **`SMSG_SET_REST_START` inline note is wrong twice** (`KnownBenignOpcodes.cs:96-99`): it claims the
  modern client "still expects this for rested XP banner" (it *cannot* — no such opcode in 1.14.2) and
  that not translating loses rested visual feedback (rested state is already translated via
  `PLAYER_REST_STATE_EXPERIENCE` → `ActivePlayerData.RestInfo`, `UpdateHandler.cs:3774-3830`).
  The "filed as backlog" item is phantom — delete it. (Quick in-game confirm of the zzz icon closes it.)
- **Header comment stale** (`:7-9`): cites `SMSG_TRAINER_BUY_SUCCEEDED` as a benign-drop example, but it
  has since been *translated* (`Client/…/NPCHandler.cs:305`) — which is also the house precedent that
  benign entries can graduate to real translations.
- **Landmine: `eb31138` on `mongrul/alpha` (unmerged)** adds `Opcode.MSG_MOVE_TIME_SKIPPED` to the
  benign list stating "The modern 1.14 client doesn't have an equivalent inbound opcode and handles peer
  drift via its own movement extrapolation" — **false**: `SMSG_MOVE_SKIP_TIME = 0x2E18` exists in both
  `V2_5_3_41750` and `V1_14_1_40688`, and modern servers actively send it. This is exactly the
  misclassification pattern the audit was commissioned to catch. Recommendation: do not merge; translate
  instead (§4.1). Every corpus build, including the tested `5.1.8-alpha.1+46(b41eb0c)`, predates this
  commit — the corpus still logs the drop loudly.
- **Process rule reaffirmed by this audit:** a benign-list addition must cite absence from
  `V2_5_3_41750` (for s2c) or `V1_12_1_5875` (for c2s). Both existing mistakes were comment/premise
  errors, not table errors.

## 7. Symptom correlation — honest result

No statistical signal. The six symptom-named sessions ("stuck sunder" ×2, "stuck BS near yetis",
"Plague cloud not clearing", "stuck at completed window", "late melee swing after death") contain only
the ambient drop set — no candidate spikes near any of them (the Plague-cloud session notably has
**zero** `SMSG_GAMEOBJECT_SPAWN_ANIM`, killing the tempting GO-visual link). The correlation case for
the candidates is therefore **mechanistic, not temporal**:

- `MOVE_SKIP_TIME` ↔ the chronic "observed players lag/warp/rubber-band" family (only candidate with
  both ubiquity and a direct mechanism).
- `PET_MODE` ↔ any future "pet bar shows wrong stance" report.
- `QUEST_LOG_FULL` ↔ "clicked accept, nothing happened".
- Explicit negative: **none of these drops plausibly feeds the stuck-spell-visual investigation**
  (lifecycles #1/#2) — the dropped SPELL_* opcodes are combat-log-only. That question is closed here.

## 8. Explicit unknowns & capture asks

| # | Unknown | How to resolve |
|---|---|---|
| U1 | Does the proxy rebase observed-mover movement times anywhere (which would partially mask or interact with SKIP_TIME)? | Implementation-time review of the movement translation path before wiring §4.1. |
| U2 | 1.14.2 wire layout of `MoveSkipTime`/`PetMode`/`ProcResist` etc. — TC **master** (retail) shapes are sourced; classic-era 2.5.3 could differ in bit-packing. | Check WowPacketParser classic-era modules (or a .pkt) per packet at impl time. These are simple packets; risk is low. |
| U3 | Vanilla `SMSG_PET_MODE` u32 packing (react/command/flags bit order). | cmangos-classic/vmangos source read at impl; 30 seconds. |
| U4 | Does the 1.14 client render the "quest log is full" red error from `SMSG_QUEST_LOG_FULL`, or only via `SMSG_DISPLAY_GAME_ERROR`? | Send-and-see with a full log (12-quest character, accept a 13th). |
| U5 | `SMSG_PET_MODE` visible impact on current build. | Capture ask: one hunter/warlock session on 5.1.9+, toggle pet stances, feign death, note any pet-bar mismatch; the JSONL will show the drops regardless. |
| U6 | What content triggers `SMSG_GAMEOBJECT_SPAWN_ANIM` bursts (120/session peaks)? Payload GUID isn't logged. | Only if someone wants the cosmetic: a vanilla-side .pkt capture, or temporarily log the guid at the drop site. Not worth a slot otherwise. |
| U7 | Post-fix validation for §4.1. | Two-box capture: box A alt-tabs 10 s while moving in view of box B; compare box B's view of A before/after the translation. |

## 9. Method appendix

- Aggregator: `aggregate_drops.py` (session scratchpad), one pass over all files; per-opcode
  count / session set / size histogram / raws / first-last timestamps / per-session counts; full
  output in `agg_summary.json`.
- Direction values are lowercase `s2c`/`c2s`; s2c `opcode_raw` is the legacy wire value (decimal).
- Universal↔version mapping: `Enums/Opcodes.cs` (`GetOpcodeValueForVersion` = name-based lookup into
  the per-build enum; presence in `V2_5_3_41750/Opcode.cs` ⇒ the 1.14.2 client has a dispatch slot).
- Modern packet shapes quoted from TrinityCore master (`MovementHandler.cpp`, `PetPackets.cpp`,
  `CombatLogPackets.cpp`, `ItemPackets.cpp`, `QuestPackets.h`); legacy relay behavior from vmangos
  (`MovementHandler.cpp`). Legacy body sizes corroborated against the corpus size column for all 12.

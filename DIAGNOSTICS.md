# JimsProxy Diagnostics Log

Findings from in-game playtesting on Kronos. Each session is captured by Phase 1's
JSONL writer (`Logs/jimsproxy-*.jsonl`); analyzed with
`launcher-tauri/scripts/analyze-jsonl.ps1`. Bugs surface as either
`packet.untranslated` (missing handler) or `packet.error` (handler threw).

## 2026-04-20 — Proxy-side work PAUSED

A collaborator is now actively maintaining their own fork of Xian55 HermesProxy with
their own fix pipeline. To avoid duplicated effort, this fork's proxy-side fixes are
paused; focus shifts to the launcher side (making proxy binaries easy to swap in/out
for A/B testing between our build, the collaborator's build, and reference
HermesProxy).

**Still OPEN at time of pause (see entries below for detail):**
- PW:S visual — alternating pattern (works one session, not the next). Bisect `f908789`..`ef95e6b` with two-run-per-commit methodology would isolate. Not currently being worked.
- Tooltip proficiency coloring — Kronos-specific (works on Ashen-wow with same proxy+client). Needs cross-server packet capture diff to isolate the Twinstar-custom field difference.
- `SMSG_SPELL_EXECUTE_LOG` — combat log damage lines. ~30 effect variants to handle. Deferred, cosmetic.
- `CMSG_EMOTE` (modern opcode 13632) — probable handler gap. 1 hit during 2026-04-19 Ironforge session. Unverified if emote actually played in-game.
- `MSG_MOVE_TIME_SKIPPED` (s2c) — 1 hit per day, unclear significance.
- `CMSG_COMMERCE_TOKEN_GET_LOG` + `CMSG_AUCTION_LIST_PENDING_SALES` — pending KNOWN_BENIGN additions.
- Skill-snapshot instrumentation (`5c8b4c0`) was reverted after it correlated with a client `#132 ACCESS_VIOLATION` crash. Redesign needed before re-attempting: single-flag guard, once-per-session emission, not per-SMSG_UPDATE_OBJECT.

**Shipped + verified fixes** (ef95e6b tip includes all of these):
- `SMSG_SPELL_FAILURE` handler (priest spell-fail UX)
- Quest reward-index cache race fix
- `SMSG_TRAINER_BUY_SUCCEEDED` KNOWN_BENIGN
- `RequiredSkill` derivation from item Class+SubClass
- Warden handshake tolerance
- Phase 1 JSONL instrumentation + analyzer workflow
- Xian55 rebase (18 months of upstream fixes)
- Trim-safety (PublishTrimmed=false)

Collaborator may pick up any of the OPEN items independently.

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

### Quest turn-in: "quest template is missing" on item-reward quests — FIX IN PROGRESS

**Hits:** 1 explicit `QuestHandler | Error` log at 22:44:47 during the session. JSONL shows 16x CMSG_QUEST_GIVER_CHOOSE_REWARD vs 14x SMSG_QUEST_GIVER_QUEST_COMPLETE — 2 turn-in attempts failed and had to be retried.

**User report:** "I find if I take a while deciding on an item reward (this only seems to happen for quests with item reward options), then it will fail to turn in the first time. If I go through the accepting text quicker it succeeds." Matches exactly: quests WITHOUT item-reward choice hit an early-return before the template lookup, so only item-choice quests trigger the bug.

**Root cause:** The server-side `CMSG_QUEST_GIVER_CHOOSE_REWARD` handler in `QuestHandler.cs:98` needs to translate the modern client's chosen `itemId` back to a 1.12-style choice index. To do that it calls `GameData.GetQuestTemplate(questId)` and iterates `UnfilteredChoiceItems`. The `QuestTemplates` dictionary is only populated by `SMSG_QUERY_QUEST_INFO_RESPONSE` (`QueryHandler.cs:262`), which requires the 1.14 client to have issued `CMSG_QUERY_QUEST_INFO` at some point. But the 1.14 client often has the quest cached from accept-time and skips the query at reward-choice time, leaving `QuestTemplates` empty for that questId.

Meanwhile, `SMSG_QUEST_GIVER_OFFER_REWARD_MESSAGE` (the packet that triggers the reward-choice UI) already carries the full choice item list (`ReadExtraQuestInfo` reads it at `QuestHandler.cs:77`) — but the proxy was forwarding those IDs to the client and then discarding them.

**Fix:** New parallel cache `GameData.OfferedRewardChoiceItems : Dictionary<questId, uint[]>` populated when `SMSG_QUEST_GIVER_OFFER_REWARD_MESSAGE` arrives. The choose-reward handler consults this cache first, falling back to the original `QuestTemplate` lookup for backwards compatibility. No roundtrip to server needed; the data is always warm by the time the user clicks.

**Verification:** Turn in several item-reward quests on Kronos, deliberately hovering over reward choices for 10-30s each. Expect zero `QuestHandler | Error` lines; expect 1:1 CMSG_QUEST_GIVER_CHOOSE_REWARD to SMSG_QUEST_GIVER_QUEST_COMPLETE ratio in the JSONL.

## 2026-04-18 — Block 3 retest (verified Block 2 fixes + new findings)

**Session:** `jimsproxy-20260418-225733.jsonl` (33,487 events, ~23.5 min, JAMEOPOTATO priest L3-5)
**Build:** JimsProxy 4.2.5+10(19d54be) — Block 2 fixes installed
**Translation rate:** 98.9% (up from 98.4%)
**Errors:** 0 packet.error, 0 QuestHandler errors (VERIFIED)
**SMSG_SPELL_FAILURE count:** 0 (VERIFIED — fix 88eee5b works)

### SMSG_SPELL_EXECUTE_LOG (588) — DEFERRED (significant scope)

**Hits:** 3 in 23 min. Combat log missing spell damage/heal lines ("Your Smite hits Wolf for 15 Holy damage").

**Scope:** Unlike SMSG_SPELL_FAILURE, this requires writing the modern packet class from scratch PLUS a parser that handles ~30 effect-type variants (SPELL_EFFECT_POWER_DRAIN, _HEAL, _INTERRUPT_CAST, _DURABILITY_DAMAGE, _OPEN_LOCK, _CREATE_ITEM, _SUMMON, _DISPEL, _ENERGIZE, etc.) each with different sub-fields. Estimated 200+ lines, high risk of subtle bugs without ground-truth sniff data. Deferred until we have more urgent gaps closed or a specific user-visible symptom beyond "combat log is quieter than vanilla."

**Impact:** Cosmetic. Damage still lands, cooldowns work, mobs die. The combat log is just less verbose.

### SMSG_TRAINER_BUY_SUCCEEDED (435) — FIXED (added to KNOWN_BENIGN)

**Hits:** 2 in 23 min (priest bought 2 spells at trainer).

**Root cause:** This opcode exists in the 1.12 server's opcode table but was removed in 1.14. The modern client doesn't have it. The correct UX — "You have learned X" banner and sound — fires on `SMSG_LEARNED_SPELL`, which is correctly translated and arrived on time in the same session (verified: 2x SMSG_LEARNED_SPELL translated at 23:19:18 and 23:19:19, alongside the 2 TRAINER_BUY_SUCCEEDED warnings).

**Fix:** Added to `KnownBenignOpcodes.ModernOnly` allowlist with a new category comment explaining the bidirectional nature of the allowlist (modern-only c2s AND legacy-only s2c both have "nothing to translate to" on the other side). Wording in the file updated to reflect both directions.

## 2026-04-19 — Block 4 iterative fixes (split diagnosis)

**Sessions:** `jimsproxy-20260419-151457.jsonl` (focused 2-min test on Kronos)
**Build:** JimsProxy 4.2.5+15(f908789) — instrumentation build with spell.cast events

### PW:Shield visual missing — RESOLVED (intermittent / not reproducible)

User reported last night that PW:Shield didn't show the bubble visual. After shipping spell.cast instrumentation, retest showed:

- spell 17 (PW:S) translated correctly: `spell_id=17`, `spell_visual_id=242477`, `visual_lookup_missing=False` for both SMSG_SPELL_START and SMSG_SPELL_GO phases
- User confirmed bubble visual now renders on cast

Likely cause: situational / first-cast-after-login race or game-state issue. Could not reproduce with the instrumentation in place, so nothing to fix. The spell.cast event is still valuable instrumentation for future spell-path diagnosis — keeping it.

### Item red-restriction indicator missing — SPLIT into 2 sub-bugs

**User reports TWO separate visual issues — previously conflated into one.** Refining based on Block 4 live testing:

**Sub-bug 4A: Red border around item icon — RESOLVED** (commit 19d54be or earlier).
Small Throwing Knife shows a red border in bag/vendor UI correctly. Client knows item is unequippable. Works as expected on Kronos.

**Sub-bug 4B: Tooltip type-line coloring — STILL OPEN, needs more diagnosis.**
The word "Thrown" directly under "Small Throwing Knife" in the tooltip stays WHITE. Expected: RED (priest lacks Thrown proficiency). Confirmed working correctly on Ashen-wow (different 1.12 server), so this is Kronos-specific server data issue.

**Hypothesis iteration:**

- H1 (wrong): modern client uses `ItemSparse.RequiredSkill` to color the type-line. → Shipped derivation fix 982d416 to populate this field. Did not resolve. Fix remains shipped as incidental-value (will correctly color explicit "Requires X" lines if they appear on higher-level gear).
- H2 (wrong): modern client reads spellbook for proficiency. → User's spellbook screenshot showed only Attack/Shoot/racials. No explicit proficiency spells. Yet priest CAN use maces/wands/etc so proficiency isn't coming from spellbook.
- H3 (leading candidate): modern client reads `PLAYER_SKILL_INFO` update fields to decide tooltip coloring. User's `/run GetSkillLineInfo()` output showed 6 visible skills (Maces, Wands, Unarmed, Cloth, Defense, Holy). The visible API may not expose all 128 update-field slots — Kronos may populate hidden Thrown-at-0 entries the client reads but the API hides.

**Next diagnostic (shipped 5c8b4c0):** `player.skills.snapshot` JSONL event that dumps every non-zero SkillLineID the proxy forwards. Run a login session, check JSONL for the snapshot, compare vs API-visible skills. If snapshot contains Thrown (id=176) or other suspicious entries, we filter at proxy. If not, hypothesis H3 is also wrong and the modern client has a default-white-for-unknown behavior — bigger fix, likely synthesize `PLAYER_FIELD_WEAPON_PROFICIENCY_FLAGS`.

**Also pending — 3-item hover test:**
- Priest-usable mace / dagger → expected WHITE
- Axe / 2H sword → expected RED
- Leather armor → expected RED

Which of these render WHITE will scope the fix: Thrown-specific vs all-subclasses-broken.

### Item red-restriction `RequiredSkill` derivation — SHIPPED (incidental win)

**Refined report:** Border IS red for restricted items (client knows item is unequippable). Tooltip TEXT for "Requires Thrown" stays white instead of red (client doesn't know WHICH requirement is unmet).

**Critical diagnostic:** Same bug present on Kronos but NOT on Ashen-wow (different 1.12 server). So the proxy/client pipeline works when given good data — the issue is Kronos-specific server data.

**Root cause:** 1.12 mangos Item Template serializer populates `RequiredSkillId` differently by server. Ashen-wow apparently populates it correctly for weapon/armor proficiency items; Kronos seems to leave it at 0 for many items including thrown weapons and leather armor. With `RequiredSkillId=0`, the modern client can flag the item as unequippable (via other logic: AllowableClass, equipment-slot proficiency) but can't render the specific "Requires Thrown" line in red because no skill is declared required.

**Fix (commit pending):** In `ItemTemplate.ReadFromLegacyPacket`, after parsing `RequiredSkillId` from the server, if it's 0, derive from ItemClass+SubClass. Mappings sourced from TrinityCore's `ItemPrototype::GetSkill()`:

- Weapon subclass 0/1 (1H/2H Axe) → skill 44 / 172
- Weapon subclass 2 (Bow) → skill 45
- Weapon subclass 3 (Gun) → skill 46
- Weapon subclass 4/5 (1H/2H Mace) → skill 54 / 160
- Weapon subclass 6 (Polearm) → skill 229
- Weapon subclass 7/8 (1H/2H Sword) → skill 43 / 55
- Weapon subclass 10 (Staff) → skill 136
- Weapon subclass 13 (Fist) → skill 473
- Weapon subclass 15 (Dagger) → skill 173
- Weapon subclass 16 (Thrown) → skill 176
- Weapon subclass 18/19 (Crossbow/Wand) → skill 226 / 228
- Weapon subclass 20 (Fishing Pole) → skill 356
- Armor subclass 1-4 (Cloth/Leather/Mail/Plate) → skill 415 / 414 / 413 / 293
- Armor subclass 6 (Shield) → skill 433

Derivation is purely additive — only fires when server sends 0, never overrides good data. Since Ashen-wow already populates the field correctly, this fix only changes behavior on servers like Kronos that don't.

**Verification:** Next session, inspect a thrown weapon tooltip. Should now show "Requires Thrown" in red. Keep noting any remaining tooltip lines that stay white — those might need additional derivation (e.g., RequiredSkillLevel, weapon-damage-type fields).

### Spell visuals missing for Stoneform / Weakened Soul / spell 31248 — DEFERRED

Same analyzer session surfaced 4 other spells with `visual_lookup_missing=True`:

- 20595, 20596 — Stoneform (dwarf racial cleanse)
- 6788 — Weakened Soul (PW:S side effect debuff that prevents re-shield for 15s)
- 31248 — unknown (some buff)

All minor effects, low gameplay impact. Deferred until we have bigger fish or they correlate with another user-visible symptom.

## Earlier findings (pre-Block 4)

### Item red-restriction indicator missing (user report) — RESOLVED via RequiredSkill derivation above

**Report:** Priest sees axes / leather armor as not-red in bag & tooltips. In vanilla WoW these would show with red-text "Axes" / "Leather" proficiency requirement because priest can't use them.

**Investigation so far:**
- `SMSG_SEND_KNOWN_SPELLS` arrives once at login (159 bytes), contains priest proficiency spells (cloth 9078, daggers 1180, staves 199, one-hand maces 198, wands 5009). Forwarded cleanly to client.
- `SMSG_ITEM_QUERY_SINGLE_RESPONSE` ~30+ per session, all translated (469-539 bytes).
- CSV spot-check (items 25 Worn Shortsword, 35 Bent Staff, 118 Minor Healing Potion, 2048 Anvilmar Hammer, 6098 Neophyte's Robe): ALL have `AllowableClass=-1` (no class restriction) and `RequiredSkill=0` (no skill requirement). The CSV ships totally permissive.
- `GameData.cs:2987` has a COMMENTED-OUT check: `//row.AllowableClass != (short)item.AllowedClasses ||`. An Explore agent suggested uncommenting, but this would almost certainly make things WORSE: 1.12 mangos sends `0` (means "no restriction" in vanilla semantics) while modern client expects `-1` for the same meaning. Uncommenting would overwrite CSV's `-1` with server's `0`, making the client think NO class can use the item.

**Hypothesis:** The red-restriction logic is client-side and depends on the modern client's weapon/armor-proficiency check against the item's `ItemClass`/`ItemSubclass` vs the player's known proficiency spells. If the correct proficiency spells are in the client's spellbook (which the JSONL suggests they are), the client should compute red-ness itself. Something in the item-subclass translation path may be off, or the modern client's proficiency-check logic has a regression we can't see without specific item IDs.

**Next-session ask:** Capture 1-2 specific items that SHOULD be red but aren't. Mouseover, note the exact item name / ID (addon like `/run print(GetItemInfo(bag,slot))` or RightClickMenu helps). With that ID I can grep the CSV, cross-check the server's `SMSG_ITEM_QUERY_SINGLE_RESPONSE` size/timing from the matching JSONL, and narrow down whether it's a CSV data issue or a proxy translation issue.

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

---

2026-04-23 — Redemption Headpiece helm visual lost on zone-in — FIXED
Sessions: `jimsproxy-20260423-203341.jsonl` through `jimsproxy-20260423-215903.jsonl` (6 traces across the investigation)
Build: JimsProxy on Xian55 v4.2.4 + Mirasu fork tip + 5 rounds of `mirasu.helm.*` diagnostic instrumentation (since reverted; see `REVERT_DIAGNOSTICS.md`)
Verdict: Hotfix-flow bug. Proxy fabricates a malformed `ItemAppearance` record whenever Kronos's `SMSG_ITEM_QUERY_SINGLE_RESPONSE` carries a `DisplayID` that has no corresponding row in the modern reference CSVs. This corrupts the client's item-visual attachment state for the affected item.
Symptom in-game
Paladin wearing Redemption Headpiece (item 22428, T3 head, Holy-glow item visual). Helmet mesh + glow render correctly on login. After any zone transition (Deeprun Tram was the consistent repro), the helmet model and its attached holy glow vanish from the local-player self-render. Other players continue to see the helmet normally. `/reload`, unequipping and re-equipping Redemption, or swapping to another helm and back all fail to restore the visual. Only a full relog clears it.
The bug is item-ID specific, not class-wide or T3-wide:
Redemption Headpiece (22428) — breaks every time.
Avenger's Crown (21387, no item visual) — never breaks.
Dreadnaught Helmet (22418, T3 with item visual) — never breaks.
Bug does not reproduce on native 1.14.2 → Blizzard Classic Era. Bug does not reproduce on native 1.12 → same Kronos realm. Bug only reproduces on 1.14.2 → JimsProxy → Kronos. This cleanly placed the fault in the proxy pipeline rather than client or server.
Root cause
Kronos (TrinityCore-1.12-based) sends `DisplayID = 35612` for item 22428 in `SMSG_ITEM_QUERY_SINGLE_RESPONSE`. Modern reference data (`CSV/ItemIdToDisplayId1.csv`) maps item 22428 → `DisplayID = 36972`. The proxy treats this divergence as a signal that the client's baseline needs a hotfix update, and runs both appearance-family generators:
`GameData.GenerateItemAppearanceUpdateIfNeeded` — called first per item-query. Looks up the Kronos-sent DisplayID in the modern `ItemAppearanceStore`. For display 35612 this lookup returns null (no `ItemAppearance` row in `CSV/ItemAppearance1.csv` references display 35612; it exists only in `CSV/ItemDisplayIdToFileDataId1.csv`). The `else` branch of this function then fabricates a brand-new `ItemAppearance` record via `AddItemAppearanceRecord(item)` → `UpdateItemAppearanceRecord`, which hard-codes `DisplayType = 11` (with the telling comment `// todo find out`; 11 is wrong — head-slot should be 0), sets `ItemDisplayInfoID` to Kronos's stale 35612, and pushes the fabricated record to the client as a `HotfixStatus.Valid` hotfix.
`GameData.GenerateItemModifiedAppearanceUpdateIfNeeded` — called immediately after. Detects the Kronos↔CSV DisplayID mismatch, calls `UpdateItemModifiedAppearanceRecord` which repoints item 22428 at the freshly-fabricated appearance, and pushes that as a second hotfix.
Net effect: the client receives two hotfixes that replace its correct CSV baseline (22428 → appearance 69172 → display 36972 → file 133117) with garbage (22428 → fabricated appearance → display 35612 → DisplayType 11). The client's item-visual attachment pipeline reads ItemDisplayInfo.m_itemVisual via the (now-broken) appearance chain, fails to resolve the glow, and additionally tears down the helmet geoset as collateral damage. Because the corruption lives in the client's hotfix-applied appearance state, nothing short of a session restart clears it — re-equip events re-read the same corrupted chain.
Dreadnaught and other T3 items don't trigger the bug because Kronos's DisplayID for those items happens to match the modern reference data, so the fabrication branch never fires.
Why wire-level diagnostics were misleading
The `SMSG_UPDATE_OBJECT` for self-player on login vs zone-in is byte-identical except for legitimate state deltas (position, MoveTime, Resting flag — all expected). VisibleItems[0] = 22428 in both. Five rounds of increasingly-granular wire instrumentation (`mirasu.helm.visible_item_write`, `mirasu.helm.player_flags_translated`, `mirasu.helm.builder_write`, `mirasu.helm.raw_bytes`) all came back clean. The poisoning is in derived state (hotfix table entries flushed at startup/login), not in the packet stream that carries the update. The decisive diagnostic was `mirasu.helm.kronos_item_template` (Edit E in `QueryHandler.HandleItemQueryResponse`) which captured Kronos's DisplayID = 35612 directly and enabled the static CSV lookup that revealed no matching `ItemAppearance` row.
Fix
Two sibling edits in `HermesProxy/World/GameData.cs`, both `//MIRASU`-tagged:
`GenerateItemAppearanceUpdateIfNeeded` (original logic at lines ~3290–3320) — added an early-return before `AddItemAppearanceRecord` that checks whether the item already has a valid `ItemModifiedAppearance` pointing at a resolvable `ItemAppearance` in the CSV baseline. If so, skip fabrication entirely and return null — the client's CSV baseline for this item is already correct and must not be overwritten by a fabricated record derived from Kronos's stale DisplayID.
`GenerateItemModifiedAppearanceUpdateIfNeeded` (original logic at lines ~3322–3356) — added an early-return that verifies `GetItemAppearanceByDisplayId(item.DisplayID)` resolves before proceeding to `UpdateItemModifiedAppearanceRecord` + `UpdateHotfix` + `GenerateHotFixMessage`. If it doesn't resolve, skip the hotfix — same reasoning. This is defense-in-depth; the Appearance-side fix is what actually prevents the bug, but this also catches any future case where an existing fabricated appearance would otherwise trigger a re-point.
Neither fix touches the normal path where Kronos and CSV agree (Dreadnaught, Avenger's Crown, the vast majority of items). Only the specific "Kronos-sent DisplayID has no matching ItemAppearance in modern reference data" failure mode is changed — from "fabricate and push" to "skip and keep client baseline".
General bug class
Any item where Kronos's (or any TrinityCore-1.12-based server's) `Item.DisplayID` has drifted from the value in the modern Classic Era reference CSVs and the Kronos value has no corresponding `ItemAppearance` row will hit the same fabrication path. Redemption Headpiece is the observed case; there are likely others, especially among 1.12-era items that received display-id reassignments in later Classic patches or 2019 Classic launch data.
A startup-time scan comparing stored-item DisplayIDs to `ItemAppearanceStore` coverage would enumerate all affected items up-front. Not currently implemented; worth considering if the bug class recurs for other items.
Evidence
`jimsproxy-20260423-215356.jsonl` line 356: `mirasu.helm.kronos_item_template` for entry 22428 shows `displayId: 35612`.
`CSV/ItemIdToDisplayId1.csv` line for entry 22428: `22428,36972`.
`CSV/ItemAppearance1.csv`: no row with `ItemDisplayInfoID = 35612` (grep returns empty).
`CSV/ItemDisplayIdToFileDataId1.csv`: row `35612,133117` exists — same FileDataID as 36972, suggesting 35612 is a legacy/redirect DisplayID that predates the appearance-table structure added in later expansions.
`jimsproxy-20260423-215903.jsonl` (post-fix trace): only one `kronos_item_template` event per session, helmet visual survives tram zone transitions, no regression on Dreadnaught or other T3 items.
Upstream relevance
This bug exists in HermesProxy upstream and Xian55 fork as well — neither has the appearance-resolution guard in the relevant Generate functions. The JimsProxy Mirasu-fork fix is candidate for upstream contribution. Same symptoms would affect any item with a similar Kronos↔modern-reference DisplayID divergence on any TrinityCore-1.12-flavored private server routed through HermesProxy to a 1.14.x+ client.

# Research: 1.14 Client on Kronos via JimsProxy

Synthesis of community, code, and upstream research done 2026-04-17 to inform the JimsProxy roadmap. Three parallel research streams: HermesProxy GitHub archaeology, Kronos community testing reports, and a gameplay-to-opcode translation test matrix. See the bottom of this doc for sources.

---

## TL;DR

1. **No prior public documentation of 1.14 client + HermesProxy on Kronos specifically.** Our users will be the first documented cohort. Phase 1 JSONL telemetry is exactly the right move.
2. **"Sugarproxy" has no public footprint** — not in forums, archives, fork networks, or issue trackers. Possibly apocryphal; we can't learn from something we can't find.
3. **[Xian55/HermesProxy](https://github.com/Xian55/HermesProxy) is the standout active fork** — v4.2.4 released 2026-04-16, 617 commits post-archive, Span-based zero-alloc packet rewrite, successful .NET 10 migration, ongoing bug fixes. **Strong candidate to rebase onto.**
4. **17 real GitHub-issue-numbered bugs** with specific code pointers make up the bug catalogue — section 3.
5. **The gameplay test matrix** in section 4 maps what a user does in game to which opcodes our JSONL should see. Red flags = untranslated opcodes or `packet.error` in specific subsystems.
6. **Kronos is not a vanilla mangos** — custom LoS, custom engineering items, custom GM command set, actively tuned Warden. Upstream issue tracker has zero Kronos mentions, so risks there are uncharted.

---

## 1. Prior-art landscape

### Nobody has written this up

Reddit (r/wowservers, r/classicwow, r/Kronos), kronos-wow.com forum, twinstar-community.com, and archive searches return **no dedicated 1.14-on-Kronos write-ups**. The closest reference is Ownedcore's generic "1.14 on 1.12 servers" guide, which lumps Kronos in with Everlook / Vanilla Gaming / Turtle without Kronos-specific notes.

**Kronos 4 public testing (launched Oct 29 2021)** focused on gameplay/scripting parity, not client compatibility. No surfaced beta report mentions 1.14 client testing.

### Kronos official position

- Official FAQ does not mention 1.14, HermesProxy, or `ReportedOS=OSX`.
- The only Warden-related quote from Kronos is aspirational: *"We have a working Warden in place as well as several other mechanics that will hopefully prevent all cheating"* — [kronos-wow.com FAQ](https://www.kronos-wow.com/frequently-asked-questions/).
- The widespread `ReportedOS=OSX` advice to bypass Warden is **community folklore, not an official sanction**. Mac OSX Warden was historically more lax in mangos-derived cores.
- Official Kronos About page confirms: **custom core derived from mangos-zero**, rewritten line-of-sight checks, custom engineering items with real malfunction rates.

### Ecosystem status

- HermesProxy (WowLegacyCore) **archived 2024-11-30**, read-only.
- Winterspring Launcher (0blu) **archived 2025-07-26**, read-only.
- Active forks of HermesProxy do exist — see section 2.
- No alternative packet translator (Chinese, European, or otherwise) has a public presence despite the "Sugarproxy" reference we started with.

**Implication:** the public ecosystem around this is effectively frozen. Our fork + telemetry gives us a maintained path forward.

---

## 2. The Xian55 rebase question

### What Xian55 provides

[Xian55/HermesProxy](https://github.com/Xian55/HermesProxy) at time of research (v4.2.4, 2026-04-16):

| Capability | Impact on us |
|---|---|
| 617 commits post-archive | 18 months of community bug fixes we'd otherwise port one-by-one |
| Zero-alloc `Span<T>` packet rewrite (317×–1948× speedup, 84.7% packet coverage via `ISpanWritable`) | Perf foundation we can't easily re-create |
| Working .NET 10 migration | Answers the question we shelved — but we don't yet know if it also triggers Kronos Warden |
| Explicit bug-fix commits we traced: "Translate ranged attack power", master-loot fixes, AH stack posting, movement spline flags | Cherry-pick equivalents or take them wholesale |
| Active maintainer, commits landing regularly | An upstream to contribute back to rather than maintain alone |

### Cost of rebasing

- Re-apply our two commits (Phase 1 logging hooks + SMSG_WARDEN_DATA handshake tolerance) onto Xian55's tree. The logging one needs real adaptation — their Span-based dispatch doesn't have the same hook sites as the archived master. The warden fix is ~10 lines and clean to port.
- Re-run the Kronos login smoke test. Specifically: does Xian55's .NET 10 build trigger Warden on Kronos the way our naive net10 port did? We don't know. If yes, we keep our net6 pin; if no, we inherit their net10 work.
- One-time effort; afterwards, keeping up is just periodic submodule bumps + merge.

### Recommendation

**Rebase onto Xian55.** Open a follow-up task. Order of operations:

1. Add Xian55 as a second remote in `jimsproxy/`, fetch
2. Create a `rebase/xian55-base` branch from their current HEAD
3. Cherry-pick our three commits in order: `31d175c` (fork metadata) → warden fix → Phase 1 logging
4. Resolve conflicts (expect biggest ones in `WorldClient.HandlePacket` due to Span rewrite)
5. Build, smoke-test Kronos login
6. If green: replace master. If net10 build triggers Warden again: keep net6 SDK pin on top of Xian55 via our existing `global.json`.
7. Bump launcher submodule; test full flow; push.

Budget rough estimate: 2–4 hours of focused work.

---

## 3. Known-bug catalogue (rank-ordered by gameplay impact)

All issue numbers refer to [github.com/WowLegacyCore/HermesProxy](https://github.com/WowLegacyCore/HermesProxy/issues) unless noted. "Cherry-pick target" = an existing PR or Xian55 commit we can pull in.

### Tier 1 — blocking / session-killing

| # | Symptom | Opcode / area | Fix available? | Kronos-relevant? |
|---|---|---|---|---|
| **#376 #313 #292 #140** | Group invite → party-stats OOB exception; disconnect on multi-map party | `CMSG_REQUEST_PARTY_JOIN_UPDATES` (13816), `HandlePartyMemberStatsFull` | Likely in Xian55 | Yes |
| **#370** | Strafe-jump while running triggers server anticheat → kick | `MSG_MOVE_*` field corruption (`CHANGE_STAND_STATE` on AFK clear) | PR #373 (walk-lock) partially | Yes — Kronos's anticheat is tuned |
| **#351 #223 #189** | Character stays walking after slow/root debuff expires | `MOVE_SPLINE_DONE` + falling flag | PR #373 (open, unmerged) | Yes |
| **#320** | Warden `HandleCheatChecksRequest` → `"Unsupported address for READ_MEMORY"` | `SMSG_WARDEN_DATA` | None upstream; **we already have a tolerance patch** | **Yes — directly hits Kronos** |
| #337 | Random `Socket Closed By Server` on login | `AuthClient` | Workaround: restart Hermes | Observed in our testing |
| #365 | Auth opcodes 74 & 33 "No handler", kicked pre-realm | Auth handshake | None upstream | **High: Twinstar-custom auth risk** |

### Tier 2 — major feature breakage

| # | Symptom | Area | Fix available? |
|---|---|---|---|
| **#311** | Hunter Auto Shot unreliable, Multi-Shot broken, Rogue ability interruption | `SMSG_SPELL_GO` clears `CurrentClientSpecialCast` | PR #311 (11 commits, open) |
| #305 #255 | `SMSG_SPELL_FAILED_OTHER` does not fire addon `SPELL_CAST_FAILED` event | Spell cast feedback | PR #305 (merged by ratkosrb?) |
| #363 | Rogue Relentless Strikes refunds 50 energy instead of 25 on 1.14.2 | Talent mirror | Hand-patch |
| #350 | Slice & Dice usable with 0 combo points, gives 1-pt duration | Rogue | Hand-patch |
| #278 | Warrior Overpower usable without dodge proc | Combat state tracking | Hand-patch |
| #333 | Rogue poison apply → instant DC; packed-GUID parse fails in enchant-log handler | `ReadPackedGuid` in poison enchant log | Hand-patch |
| #287 #304 #205 #358 | Auctionator / Aux / Auctioneer: can't split stacks, `ReadAuctionItem` reads past stream, bought items linger | `HandleAuctionListItemsResult` | Hand-patch; Xian55 has AH fixes |
| #197 #186 | DC when linking green items, "+3 white items with text" | Item-link parser | Hand-patch |
| #196 #141 | Mail sender name blank; lockbox issues | Mail rendering + lockbox | Hand-patch |
| #62 | `InventoryResults` enum off-by-one for values ≥29 | Inventory | PR #63 (+1 shift) |
| #176 | Hunter pet permanently abandoned after logout | Pet persistence | Hand-patch |
| #111 | Fish In A Bucket quest permadisconnect; `PlayerQuestTracker.WriteAllCompletedIntoArray` OOB | Quest compressed block | Hand-patch |
| #124 | AH sidepanel category filter returns no results; search bar still works | `CMSG_AUCTION_LIST_ITEMS` 1.14 layout | Hand-patch |
| #307 | `AddOns.txt` not written to `WTF/Account/`; addon on/off broken | `CMSG_REPORT_ENABLED_ADDONS` (14086) | Hand-patch |
| #177 | Spurious "You are not in a guild" messages | Guild status | Hand-patch |
| #346 | Masterloot: second looter can't distribute | Loot chain | Xian55 has master-loot fixes |

### Tier 3 — content / situational

| # | Symptom | Area |
|---|---|---|
| #353 | AQ40 NPC IDs wrong (e.g. Mindslayer 15246 appears as 15250) → WeakAuras fires on wrong NPCs | DB mapping |
| #374 | Aqual / Eternal Quintessence "out of range" for MC Runes of Warding | Spell target validation |
| #332 | Diamond Flask bugged | Consumable |
| #352 | C'Thun Dark Glare movement oscillates | AQ40 boss |
| #357 | Same-faction arena targeting broken on cmangos TBC | Arena |
| #277 | Guild charter hover crash | Social UI |
| #233 | DC on zone entry to Eastern Plaguelands | Zone transition |
| #121 #187 #190 #179 #81 #31 | Transport/zeppelin/elevator DCs — UC→Grom'Gol zep, Telredor elevator, Deeprun Tram direction | Transport pathing |
| #326 | Generic `Unable to read beyond end of the stream` | Catch-all: packet size mismatch |

### Cherry-pick targets (shortest path to quick wins)

From the archived HermesProxy PR list:

- **PR #373** — strip falling flag on `MOVE_SPLINE_DONE` → fixes walk-lock (#351 et al)
- **PR #311** — Hunter Auto Shot + related ability tracking (11 commits, bundle)
- **PR #305** — `SMSG_SPELL_FAILED_OTHER` addon event firing
- **PR #63** — `InventoryResults` enum shift for values ≥29
- **PR #368** — TCP NoDelay for reduced input lag

Most of these are already likely present in Xian55's fork. If we rebase onto Xian55, we get them implicitly.

---

## 4. Gameplay-to-opcode test matrix

### Principle

Every subsystem gets a **concrete gameplay action**, a **set of expected JSONL events**, and **red flags** to watch for. Walk through the list in-game, the JSONL either confirms green or tells us exactly what to look at.

Refer to `Logs/jimsproxy-*.jsonl` after each test; analyze with:
```powershell
Get-Content $f | ForEach-Object { ConvertFrom-Json $_ } |
  Where-Object { $_.eventType -eq 'packet.untranslated' } |
  Group-Object { $_.payload.opcode_universal } | Format-Table Count,Name
```

### Healthy pattern
- Every `packet.in {has_handler: true}` followed by `packet.translated` with positive `duration_us`
- `packet.untranslated` only for opcodes on the known-benign allowlist (section 4.Z)
- Zero `packet.error` events

### 4.A Login + zoning (Priority 1)
**Test:** Log in → char select → enter world → `/hearth` → zone into instance → `/rl` inside → leave.

**Expected c2s:** `CMSG_PLAYER_LOGIN` (heavy, ~140ms translation is normal), `CMSG_LOADING_SCREEN_NOTIFY`, `CMSG_WORLDPORT_RESPONSE`.
**Expected s2c:** `SMSG_LOGIN_VERIFY_WORLD`, `SMSG_TUTORIAL_FLAGS`, `SMSG_TRANSFER_PENDING`, `SMSG_NEW_WORLD`, `SMSG_INITIAL_SPELLS`.

**Red flags:** `SMSG_*` opcodes issued while `CMSG_PLAYER_LOGIN` is still in flight (mis-ordered startup burst — known Hermes failure mode). Any DC during `/rl` inside instance.

### 4.B Movement (Priority 1)
**Test:** Walk 5s forward, strafe L/R, jump in place, strafe-jump while running (the #370 case), mount, dismount, fall off a ledge, swim.

**Expected c2s:** `MSG_MOVE_START_FORWARD`, `MSG_MOVE_HEARTBEAT` (~500ms cadence while moving), `MSG_MOVE_STOP`, `MSG_MOVE_JUMP`, `MSG_MOVE_FALL_LAND`, `CMSG_MOVE_TIME_SKIPPED`, `CMSG_MOVE_SPLINE_DONE`.

**Red flags:** Any `MSG_MOVE_*` as `packet.untranslated`. Any character rubber-banding while moving (server desync). Strafe-jump → DC = #370 reproduced. Slow debuff expires → character stuck walking = #351/#189 reproduced.

### 4.C Combat — auto-attack + abilities (Priority 1)
**Test:** Target training dummy, `/startattack`, `/stopattack`, cast Frostbolt (rank 1), Hunter Auto Shot or Shoot Wand, cast instant (Counterspell, Rejuvenation).

**Expected c2s:** `CMSG_ATTACKSWING`, `CMSG_ATTACKSTOP`, `CMSG_CAST_SPELL`, `CMSG_CANCEL_CAST`, `CMSG_CANCEL_AUTO_REPEAT_SPELL`.
**Expected s2c:** `SMSG_ATTACKSTART`, `SMSG_ATTACKERSTATEUPDATE` (per swing), `SMSG_SPELL_START`, `SMSG_SPELL_GO`, `SMSG_SPELL_FAILURE`, `SMSG_SPELL_COOLDOWN`.

**Red flags:** `CMSG_CAST_SPELL` translated but no `SMSG_SPELL_GO` within ~200ms (server silently rejected — targeting struct diverged). Any `SMSG_SPELL_*` in untranslated list. Hunter Auto Shot stops after first volley = #311. Rogue Slice & Dice with 0 combo = #350.

### 4.D Auras / buffs / debuffs (Priority 2)
**Test:** Apply Power Word: Fortitude, Renew, Rejuvenation HoT. Let a DoT tick on a mob. Eat/drink. Scroll of Stamina. Watch durations update and fade.

**Expected s2c:** `SMSG_UPDATE_AURA_DURATION` (opcode 311), `SMSG_SET_EXTRA_AURA_INFO`, `SMSG_PERIODIC_AURA_LOG` (per-tick), `SMSG_AURACASTLOG`.

**Red flags:** Buff appears in combat log but no icon in player frame → 1.12-style `UNIT_FIELD_AURA` in `SMSG_UPDATE_OBJECT` not being expanded to modern `SMSG_AURA_UPDATE`. `SMSG_PERIODIC_AURA_LOG` in untranslated list. `duration_us` ~0 on `SMSG_UPDATE_OBJECT > 2 KB` (likely skipped).

### 4.E Inventory (Priority 1 for loot, 2 for cosmetic)
**Test:** Loot a mob, swap bag items, drag gear to char slot, vendor-sell, buy stack, split stack, destroy item, right-click-use potion.

**Expected c2s:** `CMSG_AUTOSTORE_LOOT_ITEM`, `CMSG_LOOT`, `CMSG_LOOT_RELEASE`, `CMSG_SWAP_ITEM`, `CMSG_SWAP_INV_ITEM`, `CMSG_AUTOEQUIP_ITEM`, `CMSG_DESTROYITEM`, `CMSG_USE_ITEM`, `CMSG_SELL_ITEM`, `CMSG_BUY_ITEM`.
**Expected s2c:** `SMSG_ITEM_PUSH_RESULT`, `SMSG_INVENTORY_CHANGE_FAILURE`, `SMSG_LOOT_RESPONSE`, `SMSG_ITEM_QUERY_SINGLE_RESPONSE` (opcode 88).

**Red flags:** `SMSG_ITEM_PUSH_RESULT` untranslated → new items invisible until relog. `SMSG_INVENTORY_CHANGE_FAILURE` on legal moves → bag geometry diverges. Linking a green item DCs = #197.

### 4.F Chat + addon channels (Priority 2)
**Test:** `/say hi`, `/whisper <self>` (or alt), `/p`, `/g`, `/yell`, `/join General`, `/emote wave`. Install DBM or WeakAuras and watch addon handshake. API `SendAddonMessage("PFX","payload","PARTY")`.

**Red flags:** Chat sent but not echoed = language-enum size diverged between 1.12 (4 bytes) and 1.14 (expanded). `CMSG_CHAT_MESSAGE_*` (14000s) in untranslated. `CMSG_CHAT_MESSAGE_ADDON_*` in untranslated → addon sync broken. (#307 is the parent issue for addon system failures.)

### 4.G Group / party (Priority 1)
**Test:** Invite alt, form party, promote leader, set loot method = master, kick a member, leave, convert to raid, set raid marks.

**Expected c2s:** `CMSG_GROUP_INVITE`, `CMSG_GROUP_ACCEPT`, `CMSG_GROUP_UNINVITE`, `CMSG_GROUP_SET_LEADER`, `CMSG_LOOT_METHOD`, `CMSG_CONVERT_TO_RAID`.
**Expected s2c:** `SMSG_PARTY_COMMAND_RESULT`, `SMSG_GROUP_LIST` (heavy — layout shifted 1.12 → 1.14), `SMSG_GROUP_INVITE`, `SMSG_PARTY_MEMBER_STATS_FULL`.

**Red flags:** `SMSG_GROUP_LIST` untranslated or `duration_us > 50000` = #376 (parser thrashing / DC on join). `SMSG_PARTY_MEMBER_STATS` untranslated = #292 (heartbeat every ~2s for out-of-range members).

### 4.H Trade (Priority 2)
**Test:** Right-click alt → Initiate Trade → drop 10 silver → drop item → Accept.
**Expected opcodes:** `CMSG_INITIATE_TRADE`, `CMSG_SET_TRADE_GOLD`, `CMSG_SET_TRADE_ITEM`, `CMSG_ACCEPT_TRADE`, `CMSG_CANCEL_TRADE`; `SMSG_TRADE_STATUS`, `SMSG_TRADE_STATUS_EXTENDED`.
**Red flags:** Trade window opens then immediately closes = `TRADE_STATUS_EXTENDED` struct size mismatch.

### 4.I Mail (Priority 2)
**Test:** Visit mailbox, send letter to alt (+1c +item), retrieve, return, delete.
**Red flags:** Mail list empty despite visible items → `SMSG_MAIL_LIST_RESULT` struct mismatch. Blank mail sender name = #196.

### 4.J Auction (Priority 2)
**Test:** Auctioneer → query "linen cloth" → post an item → bid on different listing → cancel own auction.
**Red flags:** Category filter empty = #124 (sidepanel bug). Posting spams 20× for a single stack = #287. Bought items linger in list = #358.

### 4.K Bank (Priority 2)
**Test:** Banker → open bank → deposit → withdraw → buy bag slot.
**Red flags:** Bank frame stays empty → `SMSG_UPDATE_OBJECT` for bank entity missing `PLAYER_FIELD_BANK_SLOT_*`.

### 4.L Quests (Priority 1)
**Test:** Level-1 quest giver → accept → complete objective → turn in → choose reward.
**Red flags:** Minimap exclamation / question marks wrong = `SMSG_QUESTGIVER_STATUS_MULTIPLE` translation off. Stuck in a quest-accept DC = Hermes issue #361-class cascade. `CMSG_QUERY_QUEST_COMPLETION_NPCS` (12664) is benign (yellow-dot display only).

### 4.M PvP Battlegrounds (Priority 2, PvP only)
**Test:** Battlemaster → queue WSG → accept pop → enter → flag cap → leave.
**Red flags:** Queue counter frozen at 0 = `SMSG_BATTLEFIELD_STATUS` untranslated. Scoreboard blank = `SMSG_PVP_LOG_DATA` size mismatch.

### 4.N Warden — fundamentally incompatible (Priority 1)
**Test:** Idle logged in for 20 min. Run a ~45-min raid encounter.

1.12 mangos servers **generally did not run Warden**; Kronos **does**. The 1.14 client expects either periodic `SMSG_WARDEN_DATA` with a specific module format (Cataclysm-era = 32-byte module IDs + SHA256 hashes) **or** nothing. Kronos sends mangos-flavored Warden packets the 1.14 client doesn't fully understand.

**Current mitigation:** Our warden-tolerance fix (WorldClient.HandlePacket) ignores the packet during handshake so the connection doesn't die. That gets us logged in. We haven't verified whether Kronos kicks us mid-session after enough ignored warden challenges.

**Red flags:** `SMSG_WARDEN_DATA` in untranslated followed by a disconnect 5–20 min later = Warden timeout kick. If disconnect is within ~60s of a recent `SMSG_WARDEN_DATA`, that's the smoking gun.

**Long-term fix options:**
1. Synthesize a benign `CMSG_WARDEN_DATA` reply ("module not loaded" ack)
2. Strip Warden entirely in both directions
3. Translate Kronos's scheme into what modern client expects

### 4.Z Known-benign allowlist (modern-only opcodes → ignore)

These subsystems did not exist in 1.12 and can safely appear as `packet.untranslated` with no gameplay loss:

- **Battle Pay / shop:** `CMSG_BATTLE_PAY_*`, `CMSG_UPDATE_VAS_PURCHASE_STATES`, `CMSG_GET_UNDELETE_CHARACTER_COOLDOWN_STATUS`
- **Battle Pets / Pet Battles:** `CMSG_BATTLE_PET_*`, `SMSG_BATTLEPET_*`
- **Calendar v2:** `CMSG_CALENDAR_*`
- **LFG v2 / Group Finder / Scenarios:** `CMSG_LFG_LIST_*`, `CMSG_LFG_JOIN`, `CMSG_SCENARIO_*`
- **Achievements / Guild Achievements:** `CMSG_GUILD_SET_ACHIEVEMENT_TRACKING`, `CMSG_QUERY_GUILD_XP`, `CMSG_GUILD_BANK_*`
- **Artifacts / Transmog / Heirlooms / Toy Box / Garrison / Island Expeditions / Warfronts / Azerite / Covenants:** entire families absent in 1.12
- **Telemetry:** `CMSG_REPORT_CLIENT_VARIABLES`, `CMSG_REPORT_KEYBINDING_EXECUTION_COUNTS`, `CMSG_VIOLENCE_LEVEL`, `CMSG_DISCARDED_TIME_SYNC_ACKS`
- **Modern account plumbing:** `CMSG_QUEUED_MESSAGES_END`, some `CMSG_BATTLENET_REQUEST` sub-commands
- **GM v2:** `CMSG_GM_TICKET_GET_CASE_STATUS`
- **Introduced post-Wrath:** `CMSG_REQUEST_CATEGORY_COOLDOWNS`, `CMSG_REQUEST_FORCED_REACTIONS`, `CMSG_QUERY_COUNTDOWN_TIMER`, `CMSG_REQUEST_CEMETERY_LIST`

**Implementation:** we should turn this into a `KNOWN_BENIGN` set in `WorldSocket` so these don't taint the `packet.error` counts in our telemetry dashboards later. File as a backlog item: *"Add KNOWN_BENIGN allowlist; emit `packet.ignored` instead of `packet.untranslated` for those."*

---

## 5. Kronos-specific risks

Kronos is mangos-zero-derived with substantial customization. Things to actively watch for that wouldn't show up in the upstream issue tracker:

1. **Custom auth handshake** — Twinstar's login infrastructure (shared with their WotLK/TBC realms at `login.twinstar-wow.com`) may emit non-standard auth opcodes. The archived upstream's issue #365 references unhandled AuthClient opcodes 74 & 33 on some cores; we haven't seen these yet but should watch the realmd auth phase.
2. **Warden variant** — already confirmed firing on us. Mitigated via tolerance patch. Long-term translation required.
3. **Custom engineering items / trinkets** — Arcanite Dragonling, Gnomish Cloaking Device, reflectors, etc. Watch for `SMSG_SPELL_FAILURE` with unusual error codes when these proc. Turtle WoW's documented "red question mark" symptom for custom items is a risk profile to assume until disproven.
4. **Rewritten line-of-sight** — some spell cast failures may be correct-from-Kronos but look like proxy bugs. Cross-reference with Kronos-side testing.
5. **Custom GM / anticheat opcodes** — outside standard 1.12 ranges; will produce `packet.untranslated` log spam. Identify per-opcode and either translate or add to allowlist.
6. **`KronosGMAddon`** — published separately; may rely on addon-channel messaging we need to keep intact.

**Action:** when the first Kronos-specific `packet.untranslated` opcode shows up in telemetry with a raw opcode outside the standard 1.12 map, capture its size and payload and open a named research task.

---

## 6. First-pass test plan (concrete actions)

Run in order, ~2 hours total. After each test block, dump JSONL summary via the PowerShell analyzer in section 4.

### Block 1 — session stability (30 min)
1. Login / character select / world enter × 3
2. 20-minute AFK idle, watch for Warden-related disconnect
3. `/hearth` → wait for hearth → zone loading
4. Enter a low-level dungeon (RFC/DM/WC), `/rl` inside, hearth out

**Pass criteria:** zero `packet.error`, no disconnects, Warden pattern catalogued.

### Block 2 — movement + combat correctness (30 min)
5. Run 100 yards, strafe-jump while running (#370 repro)
6. Apply slow effect (let a mob daze you via attack-from-behind, or have a caster Frostbolt you), let it expire — watch for walk-lock
7. Auto-attack a training dummy for 2 min, cast 10× Frostbolts
8. Hunter: Auto Shot for 2 min — does it drop out after first volley? (#311)

**Pass criteria:** zero movement-opcode untranslated, `SMSG_SPELL_GO` follows every `CMSG_CAST_SPELL` within 200ms, no DC from strafe-jumping.

### Block 3 — social + economy (30 min)
9. `/say`, `/w self`, `/p`, `/g`, `/join World`, `/emote` cycle
10. Visit auctioneer, search "linen cloth", try category-filter sidepanel (#124 repro)
11. Mail self a letter + 1c + a grey item; check mail sender name (#196 repro)
12. Bank: deposit/withdraw, buy a bag slot
13. Trade: open trade window with alt or self-trade-cancel

**Pass criteria:** all opcodes translated, AH sidepanel filter works, mail sender name visible.

### Block 4 — group + grind (30 min)
14. Invite alt or group with a friend
15. Walk ~50 yards apart so members are on different map-chunks (#376 trigger)
16. Kill 10 mobs while grouped; check loot distribution
17. Leave party; reform; convert to raid
18. Quest: accept, complete a kill objective, turn in, take reward

**Pass criteria:** no group-DC, `SMSG_GROUP_LIST` duration < 10ms, `SMSG_ITEM_PUSH_RESULT` for loot.

After this punch list: we've either found 5-15 new opcodes to file, or we're in remarkable shape. Either outcome, write it up in this doc's appendix.

---

## 7. Sources

### Agent 1 — HermesProxy GitHub archaeology
- [WowLegacyCore/HermesProxy repo](https://github.com/WowLegacyCore/HermesProxy)
- [Issues](https://github.com/WowLegacyCore/HermesProxy/issues) — refs #22 #111 #121 #124 #140 #141 #176 #177 #179 #186 #187 #189 #190 #197 #205 #223 #233 #277 #278 #287 #292 #304 #307 #311 #313 #320 #326 #332 #333 #337 #346 #350 #351 #352 #353 #357 #358 #361 #363 #365 #366 #370 #374 #376
- [PR #63](https://github.com/WowLegacyCore/HermesProxy/pull/63) — InventoryResults enum shift
- [PR #305](https://github.com/WowLegacyCore/HermesProxy/pull/305) — SMSG_SPELL_FAILED_OTHER addon event
- [PR #311](https://github.com/WowLegacyCore/HermesProxy/pull/311) — Hunter Auto Shot bundle
- [PR #368](https://github.com/WowLegacyCore/HermesProxy/pull/368) — TCP NoDelay
- [PR #373](https://github.com/WowLegacyCore/HermesProxy/pull/373) — MOVE_SPLINE_DONE falling flag strip
- [Xian55/HermesProxy fork](https://github.com/Xian55/HermesProxy) — active v4.2.4
- [ratkosrb/HermesProxy fork](https://github.com/ratkosrb/HermesProxy) — maintainer-of-record post-archive
- [bhavinamin/HermesProxy](https://github.com/bhavinamin/HermesProxy), [TheGhostGroup/HermesProxy](https://github.com/TheGhostGroup/HermesProxy), [locked-chest/HermesProxy](https://github.com/locked-chest/HermesProxy), [wolono/HermesProxy](https://github.com/wolono/HermesProxy) — other active forks

### Agent 2 — Kronos community research
- [Kronos WoW official FAQ](https://www.kronos-wow.com/frequently-asked-questions/)
- [Kronos About (custom core)](https://www.kronos-wow.com/about/)
- [Kronos Bug Reporting](https://www.kronos-wow.com/bug-reporting/)
- [Ownedcore 1.14 on 1.12 guide](https://www.ownedcore.com/forums/world-of-warcraft/world-of-warcraft-emulator-servers/wow-emu-guides-tutorials/993349-how-use-1-14-x-2-5-x-classic-clients-vanilla-1-12-tbc-2-4-3-servers.html)
- [WinterspringLauncher (0blu, archived)](https://github.com/0blu/WinterspringLauncher)
- [HackMD private server comparison](https://hackmd.io/@gripfeast3/HkyNnx4Wi)
- [NeoGAF Kronos 4 OT](https://www.neogaf.com/threads/kronos-4-ot-blizzlike-private-vanilla-wow-pvp-server-29th-october-25th-character-creation.1620091/)

### Agent 3 — Protocol references
- [wowdev.wiki Opcodes](https://wowdev.wiki/Opcodes)
- [wowdev.wiki Packets/Login/Vanilla](https://wowdev.wiki/Packets/Login/Vanilla)
- [mangoszero/server Opcodes.h](https://github.com/mangoszero/server/blob/master/src/game/Server/Opcodes.h)
- [mangostwo/server Warden.cpp](https://github.com/mangostwo/server/blob/master/src/game/Warden/Warden.cpp)
- [WoWTools SMSG_WARDEN_DATA parser](https://github.com/tomrus88/WoWTools/blob/master/src/WoWPacketViewer/Parsers/Warden/SMSG_WARDEN_DATA.cs)
- [WoWTools MSG_MOVE_ALL parser](https://github.com/tomrus88/WoWTools/blob/master/src/WoWPacketViewer/Parsers/MSG_MOVE_ALL.cs)
- [tripleslash/wowscout](https://github.com/tripleslash/wowscout)
- [Jordan Whittle — Exploiting Warden](https://jordanwhittle.com/posts/exploiting-warden/)
- [Wowpedia — Warden](https://wowpedia.fandom.com/wiki/Warden_(software))
- [TrinityCore Movement wiki](https://trinitycore.atlassian.net/wiki/spaces/tc/pages/721256449/Movement)

### Local evidence
- `Logs/jimsproxy-20260417-213629.jsonl` — 940 events, first full-gameplay session (2 min in-world)
- `Logs/jimsproxy-20260417-214628.jsonl` — multi-attempt auth session (FAIL_UNKNOWN_ACCOUNT → FAIL_INTERNAL_ERROR → SUCCESS)

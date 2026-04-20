# Evening test brief — 2026-04-19

Designed to work even when you're tired. Each section is a discrete test; do them in any order; stop whenever. Nothing is blocking anything else.

---

## Step 0 — install the queued build (30 sec)

The launcher was still running when you stepped away. A new instrumentation build is queued and needs to replace the running binary.

```powershell
Stop-Process -Name JimsProxy -Force -ErrorAction SilentlyContinue; Copy-Item "C:\Users\mouse\AppData\Roaming\kronoswow\launcher-tauri\jimsproxy\build\publish\JimsProxy.exe" "C:\Users\mouse\AppData\Roaming\kronoswow\Hermes\JimsProxy.exe" -Force; Write-Host "Installed:"; (Get-Item "C:\Users\mouse\AppData\Roaming\kronoswow\Hermes\JimsProxy.exe").LastWriteTime
```

Timestamp should show today and a time newer than 15:27. If the one-liner errors, just close the launcher normally and re-run.

---

## Test A — settle the "Thrown" red/white question (3 min, no code change needed yet)

This is the CORE diagnostic. Takes 3 hovers and tells us everything.

1. Launch. Log into the priest on Kronos.
2. Go to a vendor (Kharanos vendors work — general goods / weapon merchant).
3. **Hover 3 items** and note the color of the type-line (the word right under the item name):

| Item | Priest can use? | Expected | What you saw |
|---|---|---|---|
| A 1H Mace / Dagger (you have one in bag) | ✅ Yes | **White** | ? |
| An Axe or Two-Handed Sword (vendor has them) | ❌ No | **Red** | ? |
| Any Leather chest/pants (vendor has them) | ❌ No | **Red** | ? |

Screenshot all three tooltips if you can. Focus on the type-line color (e.g., "One-Hand Axe" in red vs white).

**This tells us which of three branches we're on:**
- **All three match expected** → Thrown-specific Kronos bug. Tiny fix.
- **Mace white, Axe/Leather ALSO WHITE** → modern client isn't checking proficiency at all for Kronos data. Big fix (likely `PLAYER_FIELD_WEAPON_PROFICIENCY_FLAGS` synthesis).
- **Mace RED** (priest's own weapon showing unusable) → proxy is dropping a proficiency signal the client needs. Different fix.

No in-game action needed beyond hovering — don't even have to buy anything.

---

## Test B — capture skill-snapshot JSONL data (1 min, automatic)

The new instrumentation (commit `5c8b4c0`) dumps the priest's FULL skill list as the proxy forwards it to the client. This is more detail than the `/run` API gives you. It runs automatically on login; you don't have to do anything.

After Test A, just close the launcher. Then run:

```powershell
cd C:\Users\mouse\AppData\Roaming\kronoswow\launcher-tauri
powershell -ExecutionPolicy Bypass -File scripts/analyze-jsonl.ps1
```

Paste the whole analyzer output to me. I'll grep it for `player.skills.snapshot` and we'll see if Kronos is sneaking in a Thrown-at-0 entry that the API hides.

---

## Test C — verify the `RequiredSkill` derivation shipped last (1 min)

The fix in commit `982d416` populates `RequiredSkill` by ItemClass+SubClass when Kronos sends 0. This SHOULD make any item with an actual "Requires X" line render that line red when the priest doesn't have skill X.

The Small Throwing Knife tooltip you screenshotted doesn't have a "Requires" line, so you can't verify with that item. For verification, try:

1. Find a **higher-level weapon** (your trainer may have a rank-2 ability that requires a specific weapon, or check vendors for items level 10+)
2. Or just confirm the log shows the derivation firing:

```powershell
Select-String -Path "C:\Users\mouse\AppData\Roaming\kronoswow\Hermes\Logs\jimsproxy-*.jsonl" -Pattern '"RequiredSkill"'
```

If you see lines mentioning RequiredSkill being non-zero in the reconciliation, that part of the fix is working regardless of the tooltip symptom.

---

## Test D — just play for 10-15 min, see what else breaks (optional)

If you've got energy, pick one of these activities and just play. The analyzer will surface any new untranslated opcodes or errors that turn up.

- Trade with an NPC / merchant
- Send yourself mail
- Visit the auction house in Ironforge
- Travel by gryphon to another zone
- Group-invite an alt (if you have one)
- Enter a dungeon entrance (don't need to fight anything, just crossing the threshold surfaces loading/zone packets)

Anything you try that feels broken, note it with:
- What you did
- What happened
- What you expected

Then the analyzer will tell us if there's packet-level evidence.

---

## Current issues at a glance

| Issue | Status | Next action |
|---|---|---|
| Xian55 rebase | ✅ DONE, verified in gameplay | — |
| SMSG_SPELL_FAILURE (caster spell-fail UX) | ✅ FIXED + verified | — |
| Quest reward-index fail-on-first-click | ✅ FIXED + verified | — |
| SMSG_TRAINER_BUY_SUCCEEDED | ✅ Allowlisted (no modern equivalent) | — |
| PW:Shield visual missing | ✅ RESOLVED (intermittent, self-resolved) | — |
| Tooltip "Thrown" white instead of red | 🔍 Needs Test A + B | **Your next session** |
| `RequiredSkill` derivation | ✅ Shipped (incidental win for higher-level items) | Test C optional |
| SMSG_SPELL_EXECUTE_LOG (combat log detail) | ⏸ DEFERRED (high scope, low impact) | — |
| MSG_MOVE_TIME_SKIPPED s2c | ⏸ OPEN (priority 3) | — |
| Stoneform / Weakened Soul visuals | ⏸ DEFERRED (low impact) | — |

---

## All docs you might want

| File | What |
|---|---|
| `jimsproxy/EVENING-TESTS-2026-04-19.md` | **← you are here** |
| `jimsproxy/MORNING-BRIEF-2026-04-19.md` | This morning's summary of the full night's work |
| `jimsproxy/DIAGNOSTICS.md` | Full bug list with status |
| `jimsproxy/CHANGES.md` | Chronological change log |
| `jimsproxy/RESEARCH.md` | Test matrix for Blocks 1-4 |
| `launcher-tauri/scripts/analyze-jsonl.ps1` | Session analyzer |

---

## Realistic minimum evening

If you just want to stop the investigation and rest: **skip everything above, play normally, and we pick up tomorrow with whatever surfaces.** You've already shipped 4 real gameplay fixes in two days. The "Thrown" tooltip thing is visual-only — doesn't affect what you can equip or kill things with.

If you have 5 focused minutes: **Test A is the highest-value single thing.** Three hovers in a vendor window. That alone closes or opens the investigation.

If you have 15 minutes: Test A + B gives us definitive data to write a targeted fix in the morning.

No pressure either way. Have a good evening.

# Morning brief — 2026-04-19

> Written 2026-04-18 ~23:35. Read this first.

## First thing when you open the laptop

**Stop the launcher** (PID 10256 was still running at bedtime — you logged out of WoW but didn't hit Stop in the launcher). Then the new Block 3 binary will install automatically, or run:

```powershell
Copy-Item "C:\Users\mouse\AppData\Roaming\kronoswow\launcher-tauri\jimsproxy\build\publish\JimsProxy.exe" `
          "C:\Users\mouse\AppData\Roaming\kronoswow\Hermes\JimsProxy.exe" -Force
```

## What shipped tonight — 10 JimsProxy commits + 2 launcher commits

| Commit | What | Status |
|---|---|---|
| `c1bc9b1` | Initial fork metadata | Stable |
| `e63c1f3` | Phase 1 JSONL logging (ported onto Xian55) | Stable |
| `829f43d` | Kronos world-auth: tolerate SMSG_WARDEN_DATA | Stable |
| `3d2f57c` | RESEARCH.md | Docs |
| `8e66eb2` | KNOWN_BENIGN allowlist (31 opcodes) | Stable |
| `20f0505` | Block 1 follow-ups (telemetry + log readability) | Stable |
| `35a98b9` | Trim-safety: disable PublishTrimmed + defensive resolver/root | Stable |
| `88eee5b` | **SMSG_SPELL_FAILURE handler** | ✅ Verified — 0 hits across 2 retest sessions |
| `19d54be` | **Quest reward-index cache fix** | ✅ Verified — 0 QuestHandler errors in retest |
| `84dcfbb` | SMSG_TRAINER_BUY_SUCCEEDED → KNOWN_BENIGN + Block 3 diagnostics | Queued (needs install) |

Launcher-tauri:
| Commit | What |
|---|---|
| `c8d73f0` | Bump jimsproxy submodule + net6→net10 script/docs fix |
| `47e9b28` | `scripts/analyze-jsonl.ps1` — session analyzer |

## Confirmed wins (verified in retest JSONL)

- **Xian55 rebase is live.** 18 months of upstream fixes absorbed. Kronos Warden accepts the net10 binary (the old "Warden rejects net10" theory was wrong).
- **Spell-failure UX fixed.** Priest spells that fail (OOM, OOR, LoS) now reset the cast bar and show red error text. Was 12+ untranslated SMSG_SPELL_FAILURE warnings per session → **0**.
- **Quest reward-choice fixed.** Item-reward quests with deliberation no longer fail on first click. The cache-at-offer-time approach bypasses the race entirely. **0 QuestHandler errors** in retest.
- **Translation rate climbed** from 98.4% → 98.9% over gameplay sessions.
- **Trim safety documented.** PublishTrimmed=false shipped because Xian55 has 4+ reflection hot paths the trimmer silently breaks. ~80MB binary, ~37MB compressed. Fine.

## New findings from your last session (~3.5 min, 23:25-23:29)

### 1. Power Word: Shield visual missing

**Symptom:** No bubble shield graphic when you cast PW:S.

**Not a packet-layer bug.** The JSONL shows 0 errors, 0 untranslated (except the usual one-off Warden). The aura is applying correctly — the client just isn't rendering the visual.

**Likely cause:** `GameData.GetSpellVisual(spellId)` at `SpellHandler.cs:307/384` is the lookup that maps a 1.12 spell ID → modern `SpellXSpellVisualID`. If spell 17 (PW:S rank 1) isn't mapped (or maps to 0), the client gets "no visual kit" for the aura and renders nothing.

**Next steps to investigate:**
- Check if `GameData.GetSpellVisual(17)` returns 0 or a valid ID
- Look at the CSV `SpellXSpellVisual.csv` (or similar) that backs the mapping
- Compare to a spell that DOES show a visual (e.g., Smite — does it have an impact effect?)

### 2. Red-restriction: "Small Throwing Knife" on priest

**You typed "Smaller" — found it in the CSV as "Small Throwing Knife" (item 2947).** The CSV data:
```
Id=2947  AllowableClass=-1  RequiredSkill=0  InventoryType=25 (Thrown)
```

Fully permissive. No class bitmask, no skill requirement. The modern client is supposed to compute red-ness from:
1. `InventoryType=25` (Thrown) → requires Thrown proficiency spell (2567 or 2764)
2. Player's spellbook from SMSG_SEND_KNOWN_SPELLS — priest DOES NOT have either thrown spell

The proxy forwards SMSG_SEND_KNOWN_SPELLS correctly (1 hit at login, 159 bytes, translated in 862µs). So the data the client needs IS arriving.

**My hypothesis (untested):** Vanilla mangos doesn't send `SMSG_SET_PROFICIENCY` at login (we saw 0 of those in every session). The modern 1.14 client may rely on explicit `SMSG_SET_PROFICIENCY` packets rather than computing proficiency from the spellbook. If that's true, **the fix is to synthesize `SMSG_SET_PROFICIENCY` at login** based on the character's class:

- Read the player's class (stored in `GlobalSessionData.ClassId`)
- For each class, emit a `SMSG_SET_PROFICIENCY(class=2, mask=<weapon bitmask>)` and `SMSG_SET_PROFICIENCY(class=4, mask=<armor bitmask>)`
- Example for priest: weapon mask = bits 4+10+15+19 = `0x88410`, armor mask = bit 1 = `0x2`

This is a ~60-line fix with moderate risk. Want me to prototype it next session? The risk is: if the modern client already DOES compute from spellbook, this would be harmless. If it OVERRIDES based on SMSG_SET_PROFICIENCY, it might introduce new inconsistencies if our class→mask tables have gaps.

### 3. MSG_MOVE_TIME_SKIPPED (793, s2c) — still unresolved

Seen once previously; not in this session. Low priority.

## Open bugs triage for next session

Priority order:

1. **PW:S visual** — quick check of `GameData.GetSpellVisual(17)` + the CSV backing data. 15 min to diagnose. Fix complexity unknown until we see the data.
2. **Red-restriction** — prototype the synthesize-SMSG_SET_PROFICIENCY fix. ~60 lines. Needs a retest session to confirm. If it doesn't fix, we've ruled out the proficiency-packet path and can look at ItemClass/Subclass encoding.
3. **SMSG_SPELL_EXECUTE_LOG** (deferred) — combat log spell damage lines. 200+ lines of handler code covering ~30 effect variants. Low gameplay impact (damage still lands), high implementation risk. Defer until we have sniff data or it's the last remaining gap.
4. **MSG_MOVE_TIME_SKIPPED** — low priority, investigate if it correlates with rubber-banding later.

## Full documentation snapshot

- `jimsproxy/CHANGES.md` — chronological change log with commit citations
- `jimsproxy/DIAGNOSTICS.md` — all bugs found + status (OPEN / FIXED / DEFERRED / PENDING)
- `jimsproxy/RESEARCH.md` — Tier 1/2/3 bug catalogue + test matrix
- `jimsproxy/REBASE-REPORT.md` — the rebase go/no-go analysis (completed)
- `launcher-tauri/scripts/analyze-jsonl.ps1` — session analyzer; auto-finds latest JSONL
- `launcher-tauri/scripts/build-jimsproxy.ps1` — full build + bundle

## Verification snapshot (ready for you to check)

Latest session: `C:\Users\mouse\AppData\Roaming\kronoswow\Hermes\Logs\jimsproxy-20260418-232526.jsonl`

**If you run the analyzer on that file:**
```powershell
cd C:\Users\mouse\AppData\Roaming\kronoswow\launcher-tauri
powershell -ExecutionPolicy Bypass -File scripts/analyze-jsonl.ps1
```

Expected: 97.2% translation rate, 0 errors, 1 untranslated (Warden, expected). Verdict: **CLEAN.**

The packet layer is genuinely healthy. The remaining bugs are all in the client-render-from-data layer and need either missing-packet synthesis (proficiency) or a spell-visual-ID table check (PW:S).

---

**Bottom line:** Xian55 rebase + 3 real gameplay fixes + 1 robust analyzer script + detailed follow-up plan. That's 3-4 days of work in a single evening. Take a lazy morning, skim this, and decide what you want to pick up next.

# Spell-State Desync Investigation — stuck looping spell sound + animation

**Status:** v1.4 (2026-06-13). Living document — updated each log-driven iteration.

> ### ✎ v1.4 — H7 FIX (Phase 1) — PR [#372](https://github.com/jameopotato/jimsproxy/pull/372) (base `beta`)
> **Mechanism, now verified against the CMSG side:** the double-send only produces two same-spell entries in **Low-Latency mode**, where `HandleCastSpell` bypasses the CMSG dup guard (`HasNonStartedPendingCastForSpell`, Server `SpellHandler.cs:345`) and forwards every press immediately. In *normal* mode the dup guard drops the second press, so H7 can't occur. **The tester is low-latency → in Low-Latency mode → unprotected by the dup guard → hits H7.** That's a real config-specific confirmation.
> **The fix is caller-aware** (`TryDequeuePendingNormalCast(spellId, out cast, preferStarted)`):
> - `SMSG_SPELL_GO` and real failures pass `preferStarted: true` → resolve the **started** entry (START/GO pair); fall back to oldest unstarted (skip-START instants).
> - a duplicate's `NOT_READY`/`SpellInProgress` rejection passes `preferStarted: false` → consume the **unstarted** dup and **spare the started entry** for its in-flight GO.
> This pairs the double-send correctly in **both** packet orderings (GO-before-fail *and* fail-before-fail). My first one-liner only handled GO→started and would have let a dup's failure steal the started entry in the fail-before-GO ordering — a latent hole the caller-aware version closes. `DebugOutput`-gated diagnostics `cast.go.prefer_started` / `cast.fail.spared_started` fire exactly when each direction resolves the ambiguity. Build clean, **541 tests pass** (6 in `CastGoPreferStartedTests`).
> **Still patch-as-test, not a declared fix** (§1.5 — 0-for-10): confirmation = the tester's stuck Blade Flurry stops AND those diagnostics appear. If it persists, H7 wasn't their funnel entry → fall to E0/H1.

> ### ★ v1.3 REFRAME (tester correction — supersedes the aura framing as the lead)
> The tester clarified the looping thing is **the CAST animation/sound** (the short one you get *on press*), **not** an aura/buff sound — there may be no sound while the aura is active. It is **intermittent** ("only sometimes"), and the tester's strong prior is that **a jimsproxy change introduced it** (not the Xian55/HermesProxy base, not sugarproxy — the leading competitor, see §7.6). This reframes the investigation from "missing aura-stop edge" to a **REGRESSION HUNT in our cast-CastID machinery**, and promotes a new leading hypothesis **H7** (§5):
>
> *(Correction: an earlier draft and `RESEARCH.md` called "sugarproxy" apocryphal/unfindable — that was wrong. It is **糖糖代理 (Táng Táng Dàilǐ)**, a real, documented Chinese-language proxy [heitu.org/doc/糖糖代理.html](https://heitu.org/doc/%E7%B3%96%E7%B3%96%E4%BB%A3%E7%90%86.html); English-only searches missed it. Its architecture is summarized in §7.6 and materially informs §7.5.)*
>
> **H7 — START↔GO CastID mis-pairing from the prefer-unstarted dequeue, on same-spell double-sends.** `TryMarkPendingNormalCastStarted` marks the first *un-started* entry **A** at `SMSG_SPELL_START` (`GlobalSessionData.cs:1295`); `TryDequeuePendingNormalCast` *prefers the first un-started* entry at `SMSG_SPELL_GO` (`:1237-1242`). Blade Flurry is off-GCD and **double-sends** (two un-started entries A, B). START stamps **A**, GO grabs **B** → the forwarded START and GO carry **different CastIDs** → the client's cast visual (opened on A) never gets a matching GO → **stuck cast animation/sound**. #362's GO-side recovery can't catch it: it's an `else if` reached only when the normal dequeue *fails*, and here it *succeeds on the wrong entry* (`SpellHandler.cs:1727`). **Intermittent** because it only bites when the GO arrives before the duplicate's `CAST_FAILED` removes B (timing). **jimsproxy-specific** (FIFO queue + prefer-unstarted + minted CastIDs are ours). This is the best-fitting candidate to date — but per §1.5 it is **unconfirmed until a log shows START's CastID-counter ≠ GO's CastID-counter on a stuck cast with no `cast.go.castid_recovered`** (E7). It may also be only one entry in the funnel.
>
> Consequence: **H1/H5 (aura state-kit) are demoted** — they remain candidates for the #337 "holy hands"/Insignia *cosmetic* reports, but the Blade-Flurry repro the tester actually sees is a **cast-lifecycle** bug. §3's aura model is no longer the lead; §3.6 (new) is.

**Status (prior):** v1.2 (2026-06-13). Living document — updated each log-driven iteration.
**v1.1 added:** a second bug *shape* (H5 — the proxy's own aura "Flicker" re-emit re-triggers the kit, confirmed live for Blade Flurry, currently unlogged), the looping-emote precedent (`ChatHandler.cs`), and experiment **E0 (chatter-vs-silence)** as the decisive first capture.
**v1.2 added (git archaeology + targeted external research):** three findings that reshape the FIX, not the model — see the box below and §7.

> ### ⚠ Hard constraints on any fix (learned from history — read before proposing one)
> 1. **Over-cancelling CAUSES this exact symptom.** `1f58cba` ("imp Firebolt sound stuck"): forwarding rapid `CancelSpellVisual`s (a pet-failure storm, 10+/sec) *chained into a stuck looping cast sound* on the 1.14 client; the fix was to dedup to the first. ⇒ The 1.14 client's stuck-loop is itself **inducible by rapid visual-churn packets**. A naive "emit `CancelSpellVisual` on aura fade" (the H1 fix) **must be debounced per `(source, visualID)`** and must never fire per-tick / per-stack, or it recreates the bug.
> 2. **Don't re-add an artificial spell delay.** Upstream's `Server/ClientSpellDelay` (`Thread.Sleep`, default 15 ms) was **deliberately deleted in v4.1** (`279a67a`, "root cause was single-variable tracking overwritten during spell spam → CastID mismatches") and replaced by the queue/CastID model jimsproxy uses now. It slept on the **cast path** (START/GO/CMSG, local-player-gated), never the aura/visual path — so it only ever masked the cast-packet spacing race.
> 3. **Don't remove the Flicker; remove its CLEAR.** `SendAuraRefreshUpdate`'s clear→reapply is load-bearing (enemy-DoT clocks, recast-timer resets depend on it). External research: an in-place duration refresh does **not** replay the apply visual, but a clear→re-add **does** (the client reads slot-emptied-then-filled as a new application). ⇒ The clean Shape-B fix is "refresh duration in place; drop the CLEAR for kit-bearing auras," not deleting the Flicker.
**Author loop:** Claude (Opus 4.8) writes theories → tester (rogue, low-latency, Blade Flurry) brings targeted JSONL → confirm/kill → only then a fix.
**No code changes have been made this run.** This is understanding + a plan to confirm it.
**Any resulting fix PR targets `beta`** (master is stable-only — promote later).

> How to use this doc: §1 is the thesis. §2 is the falsification test set (the hard
> observations every hypothesis must pass). §3 is the lifecycle model. §5 is the ranked,
> falsifiable hypothesis table. §6 is the experiment menu to bring back logs against. §7
> is the architecture verdict. §9 is the verified code map (file:line). Update §5 confidence
> and §6 results as captures come in.

---

## 1. TL;DR — the thesis

The bug family has **two independent lifecycles**, not one:

1. **The CAST lifecycle** — `SMSG_SPELL_START` ↔ `SMSG_SPELL_GO` paired by **CastID**, plus the pending-cast queue / GCD. This is what #341, #344, #352, #362, #365, #366, #367 have been patching. It is now heavily armoured. **#362 lives entirely here.**

2. **The AURA → VISUAL-KIT → SOUND lifecycle** — a *persistent ("state") SpellVisualKit* that the modern 1.14 client plays **while an aura is active** (Blade Flurry's whirling blades, on-use-trinket glow, the priest "holy hands"). This kit has a looping animation + looping sound. **In the proxy, this lifecycle has no synthesized "stop" edge at all.** Every one of the proxy's six `CancelSpellVisual` emit sites is on a *cast-failure* path (§9.C). Nothing — not aura removal, not natural `SPELL_GO` completion, not the out-of-range buff-bar wipe — ever tells the client to stop a state kit.

The stuck loop is **lifecycle #2**. That is why it **survives #362** (a lifecycle-#1 fix), why it is **instant-cast-and-aura specific** (only aura spells own a state kit; instants compress the START/GO/aura packets into one tick and race the client's kit↔aura binding), and — the sharpest fingerprint — why **`/reload` does not clear it but `RestartSoundSystem()` does**: `/reload` only re-pushes the *local player's aura icons* (`CharacterHandler.cs:119`) and sends **zero** `CancelSpellVisual`; the looping kit sound is a free-running sound-engine channel that only a sound-engine reset, zone, or relog can reclaim.

> **v1.1 update (2026-06-13) — there are TWO bug *shapes* inside lifecycle #2, and we don't yet know which:**
> - **Shape A — the client free-runs a kit the proxy never stops (H1).** The proxy emits no stop edge; the kit loops on its own. *Log signature during the stuck period: SILENCE — the proxy sends nothing.*
> - **Shape B — the proxy actively RE-TRIGGERS the kit (H5, new).** `SendAuraRefreshUpdate` performs a deliberate **"Flicker" — it CLEARS the aura slot then RE-APPLIES it** (`SpellHandler.cs:3195-3205`), fired from `HandleSpellGo` for any spell in `AuraSpells` the target already has (`SpellHandler.cs:1739-1759`). **Blade Flurry (13877) is in `AuraSpells`** (`AuraSpells1.csv:4044`). A clear→re-add reads to the client as aura-removed-then-re-added, which **re-kicks the state kit's sound/animation**. The same re-emit machinery has a *documented history of misfiring on every proc* ("Hemorrhage proc refreshed Kidney Shot on every hit," `UpdateHandler.cs:2964-2966`). *Log signature during the stuck period: CHATTER — repeated aura re-emits.* **This path is currently UNLOGGED**, so existing captures are blind to it.
>
> Shapes A and B are not exclusive — the Flicker (B) is a strong candidate for *breaking the kit↔aura binding* so the later real aura-clear can't stop it (A sustains what B starts). **The single most decisive next capture is whether the proxy is silent or chattering while the loop runs (E0).** This splits the two shapes and dictates the fix.

External evidence independently triangulates the same mechanism (§8): vmangos maintainers debugging stuck *channel* visuals concluded "a cleanup packet the client expects is not being sent" ([vmangos#3227]); the canonical server-side "glowing hands" bug is exactly a missing stop packet, visible to all nearby players; and — the smoking gun — **upstream HermesProxy shipped a config workaround that adds an artificial ~20 ms delay to spells "to fix stuck sounds and animations."** A *timing knob* masking the bug proves a **race/ordering problem in the instant-cast→aura-visual translation**, not a one-off bad spell.

**Leading fix direction (see §7 for the full verdict):** the missing edge is *deterministic* — synthesize `CancelSpellVisual` when an aura that owns a persistent kit is removed (generalizing the #189/#213 "cancel-on-failure" family to the "cancel-on-aura-fade" event). This needs **no timer** because aura-fade is a server event the proxy already sees (`UpdateHandler.cs:3088` `aura.slot.cleared`). Frame it as the first instance of a small systemic principle — *every kit the proxy lets the client start must have a deterministic stop trigger* — rather than yet another per-spell `else if`.

---

## 1.5 Calibration — why this bug resists fixes, and how much to trust the above

**Read this before believing any confidence label in §5.** This symptom has now survived ~10 fixes (§9.A), each of which shipped with its own confident, internally-consistent mechanistic story. That track record is *evidence* — and the thing it most supports is **not** "everyone missed the real root cause," but: **"stuck looping spell sound/animation" is a SYMPTOM FUNNEL, not one bug.** #186 (pet double-sound), #189 (cast-failure pose), #213 (wand aim), #352 (observed bow), #362 (cast orphan) were genuinely *different* desyncs that all surface as the same visible artifact, because the 1.14 client's kit/sound subsystem has many ways to get stuck and almost no way to get unstuck across the translation seam.

Consequences I should have led with, not buried:
- **A confirmed-and-fixed mechanism may not kill the symptom.** If the tester's stuck Blade Flurry is funnel-entry X and we confirm+fix entry Y, the loop persists and it looks like "another failed fix." Every prior fixer likely hit exactly this. So the goal this round is **not** "find THE root cause" — it is **build instrumentation that makes each stuck instance self-classify** (which funnel entry it is), then fix entries one at a time with eyes open.
- **My "two clean shapes, one capture bisects them" framing is itself too tidy.** Real captures here are red-herring-laden (the brief warns of this). E0 is a *first classifier*, not a guillotine; expect messy/intermittent results and more than two outcomes.

Honest confidence audit of the load-bearing claims:
| Claim | I rated it | Honest rating | Why downgraded |
|---|---|---|---|
| Stuck loop is lifecycle-2 (aura/visual), not cast | implied HIGH | **MEDIUM** | "Cast path is well-armoured" is an assumption; O6 (stuck button/blocked cast) are lifecycle-1 symptoms I dismissed as mere co-occurrence — they could be the same root. |
| `/reload`-no / `RestartSoundSystem`-yes ⇒ sound-engine-level, decoupled from aura table | treated as fact | **inference** | It's a deduction about client internals I cannot see; plausible, not proven. It's my sharpest clue *and* my least verifiable. |
| Not latency-gated ⇒ sub-tick ordering race | HIGH | **MEDIUM** | The actual repro is ONE low-latency tester; the "20–200 ms" reports may be different funnel entries conflated under one symptom. |
| H5 (Flicker) is co-leading | co-leading | **contributing, not co-leading** | The Flicker fires per-refresh-of-an-already-present-aura, not continuously — explains a re-triggered/doubled sound, weakly explains "loops until zone." |
| It's an aura spell | O1, treated as given | **mostly, not always** | The brief says "mostly Blade Flurry, **sometimes other casts**." If some stuck instances are non-aura, the cast-pose family (H6) is implicated and O1's aura-specificity is softer than stated. |

The model in §3 is still the best-organized available account, and the §7 fix constraints are solid (they're from history, not theory). But hold the hypothesis *rankings* loosely: the most likely truth is that **two or three of H1/H2/H5/H6 are all real and the tester hits more than one.**

---

## 2. The hard observations (the falsification test set)

Any theory must explain ALL of these. Each hypothesis in §5 is scored against this list.

| # | Observation | Why it discriminates |
|---|---|---|
| O1 | Almost always an **instant cast that applies an AURA** (Blade Flurry, on-use trinkets, holy hands). Not channels, not cast-time. | A persistent/"state" kit exists only for aura spells; instants collapse START+GO+aura into one tick. |
| O2 | **`RestartSoundSystem()` clears the stuck sound; `/reload` does NOT.** Relog/zone resets everything. | The loop lives in the **sound/render engine**, decoupled from the Lua/UI and the aura *table*. Rules out anything `/reload` would fix (cast-bar, action-button, aura-icon re-push). |
| O3 | Can appear on **other party members**, not just self. | The aura→kit seam is identical for party units; `/reload` only re-pushes the *local* player's auras. |
| O4 | Reported **~20 ms to ~200 ms latency — NOT latency-gated.** | The trigger is sub-millisecond packet *ordering* within a server tick, not RTT. |
| O5 | **Survives #362** (tester is on a build with GO-side CastID recovery and still loops). | The stuck state is not the CAST lifecycle #362 repairs. |
| O6 | A stuck-lit "pending" action button / blocked cast / blocked auto-attack **sometimes** accompanies it, sometimes not. | Those are lifecycle-#1 symptoms that *can* co-occur but are not the core looping artifact. |

---

## 3. Model — the full lifecycle the 1.14 client expects, and where closure breaks

### 3.1 Two parallel state machines

```
LIFECYCLE 1 — CAST (paired by CastID)
  CMSG_CAST_SPELL ──> [pending-cast queue entry: ClientGUID + minted ServerGUID]
       server: SMSG_SPELL_START ──> client opens "pending cast" slot, plays PRECAST/CAST kit
       server: SMSG_SPELL_GO    ──> client pairs by CastID, closes slot, plays CAST/IMPACT kit
       server: SMSG_SPELL_FAILURE / FAILED_OTHER / CAST_FAILED ──> client cancels slot
  closure = a CastID-matched GO, or a failure. (#341/#344/#352/#362/#365/#366/#367 all live here.)

LIFECYCLE 2 — AURA → STATE-VISUAL-KIT → SOUND   (NOT paired by CastID)
  server: SMSG_SPELL_GO (aura spell) ──> client plays the spell's SpellVisual, which includes
                                         the STATE kit (SpellVisual "buff-active" column)
  server: UNIT_FIELD_AURA slot SET   ──> client registers aura; binds the state kit to it
       ... aura active: kit loops animation + sound ...
  server: UNIT_FIELD_AURA slot CLEAR ──> client SHOULD stop the state kit
  closure = the client correctly bound the kit to the aura AND the aura-clear arrives AND the
            client honours it. NONE of these is guaranteed across the translation seam.
```

The disease is entirely in **Lifecycle 2**, and the proxy does not model Lifecycle 2 at all. It forwards/translates the *start* edges (the `SPELL_GO` that triggers the kit; the `aura.slot.set` that registers the aura) but synthesizes **no stop edge** for the kit. See §9.

### 3.2 The translation seam for auras (where 1.12 ≠ 1.14)

- **1.12 auras are not packets.** They are fields inside `SMSG_UPDATE_OBJECT`: the `UNIT_FIELD_AURA` slot array (32 slots), aura flags/levels, and `UNIT_FIELD_AURASTATE`. *Apply* = slot set; *remove* = slot cleared in a later update ([wowdev SMSG_UPDATE_OBJECT]).
- **The proxy translates** each changed slot into a modern `SMSG_AURA_UPDATE` (`AuraInfo`) — apply at `UpdateHandler.cs:2985-3078` (`aura.slot.set`), remove at `UpdateHandler.cs:3079-3098` (`aura.slot.cleared`, emits an *empty* `AuraInfo` to drop the icon). It also pushes durations from `SMSG_UPDATE_AURA_DURATION` (`SpellHandler.cs:2923`) and `SMSG_SET_EXTRA_AURA_INFO` (`SpellHandler.cs:2972`), and a state-refresh on `SPELL_GO` for an `AuraSpells` spell (`SpellHandler.cs:1739-1759`).
- **The 1.14 client's state kit** (modern `SMSG_PLAY_SPELL_VISUAL_KIT` has explicit `KitType` precast/cast/impact/**state**/channel + `Duration`) is a much stronger lifecycle object than 1.12's fire-and-forget `SMSG_PLAY_SPELL_VISUAL`. It tracks "is this kit still active" and is therefore *more* sensitive to a state-kit that is started but never told to stop ([TrinityCore#19486], [wowdev DB/SpellVisualKit]).
- **The closure gap:** the proxy converts the aura *icon* lifecycle faithfully (set/clear → `AuraInfo`) but never converts the aura *visual-kit* lifecycle. The icon clears; the kit keeps running. There is no `SMSG_CANCEL_SPELL_VISUAL` anywhere on the aura path (§9.C — all six cancels are failure-gated).

### 3.3 Why instants race (the binding problem, O1 + O4)

On **Kronos/Twinstar**, instants are *not* GO-only — the server emits `SMSG_SPELL_START` even for 0-cast-time spells (verified in-code: "Kronos 1.12 emits SMSG_SPELL_START even for instants," `SpellHandler.cs:1601-1602`; and the pet "double-sound" note, `SpellHandler.cs:1403-1425`). So for an instant aura cast the client receives, within one server tick:

```
SMSG_SPELL_START (kit fires)  +  SMSG_SPELL_GO (kit fires AGAIN)  +  UNIT_FIELD_AURA slot set
```

The modern client "fires the sound on each [START and GO]" (`SpellHandler.cs:1405-1408`). When the GO's state kit and the aura registration arrive in the wrong order or the same millisecond, the client can play the state kit **unbound to any aura** — so even its own aura-clear logic has nothing to stop. This is exactly what a small artificial delay fixes upstream (§8). For **cast-time** spells the START (T0) and GO+aura (T0+casttime) are naturally separated, so the kit binds cleanly — which is why O1 holds (instant-only).

### 3.4 The proxy's own aura RE-EMIT paths (the re-trigger suspects — new in v1.1)

Two proxy-synthesized paths can make the client see an aura **removed-then-re-added** (or refreshed) *without the aura actually changing server-side* — each a candidate to re-kick the state kit:

1. **The explicit "Flicker"** — `SendAuraRefreshUpdate` sends an empty-slot `AuraUpdate` then a full one (`SpellHandler.cs:3195-3205`, literally commented "1. The Flicker … 2. The Reapplication"). It exists to reset the duration timer on recast. It is called from `HandleSpellGo` (`:1739-1759`) whenever a `SPELL_GO` arrives for an `AuraSpells` spell already present on the target — **Blade Flurry qualifies** (`AuraSpells1.csv:4044`). It is **not logged**, so current captures can't see it fire.
2. **The AURAAPPLICATIONS-quad re-emit** — the aura loop re-emits a slot when its packed stack-count byte changes (`UpdateHandler.cs:2943-2978`). This has a **documented misfire history**: before per-byte diffing was added, "Hemorrhage cast on a target with Kidney Shot in an adjacent slot triggered a Flicker refresh of the Kidney Shot timer on **every Hemorrhage proc**" (`:2964-2966`). The per-byte diff narrowed it, but the path is intricate and proc-driven, and the team is *still* chasing "aura apply/remove vs SPELL_PERIODIC ticks" here (`:2980-2981`, Rupture lingering-icon). Any residual quad edge that re-emits an active aura would re-kick its kit on every proc/tick → a *sustained* loop.

If either path fires repeatedly while a kit-bearing aura is up, the result is a **proxy-driven re-trigger loop**, not a client free-run. That is Shape B (H5) and it has the opposite log signature from Shape A.

### 3.5 The 1.14 client loops one-shots until told to stop — already proven for emotes (precedent)

This is not a spell-only quirk. `ChatHandler.cs:699-728` documents the **identical disease for emotes**: "EMOTE_ONESHOT_DANCE (10) … the Classic 1.14 client **loops it until another `SMSG_EMOTE` arrives**, and Kronos/Twinstar don't broadcast one on move." The proxy already keeps a `LastLoopingEmoteId` tracker (`GameState`) precisely to know it must synthesize the terminator the 1.12 server never sends. Two takeaways: (a) "modern client loops a one-shot until an explicit stop the legacy server omits" is a **client-wide behavior**, strongly corroborating the spell-kit model; (b) the proxy **already has a track-the-active-loop-to-terminate-it pattern** — extending it to visual kits is consistent with existing house style, not a new paradigm (see §7).

---

## 4. Why the native 1.12 client is immune (Q2)

There is **no seam**: the 1.12 server and 1.12 client share one implicit, state-coupled termination model. The state visual is shown *because* an aura occupies a slot and is torn down *with* that slot, in the same object-manager update, parsed by the same engine that drew it. No packet is translated, reordered, re-timed, or re-keyed. The kit, the aura, and the sound are one coherent object lifecycle.

Bridging a 1.14 client in breaks three things at once:
1. **Re-keying** — the 1.14 client wants CastID-paired closure and kit `Duration`/`KitType` semantics the 1.12 wire never carried; the proxy must *manufacture* closure the 1.12 server never sends.
2. **Re-timing/Re-ordering** — translation puts `SPELL_START`, `SPELL_GO`, and the aura update on the wire as separate modern packets whose relative order/spacing the client is newly sensitive to (the kit↔aura binding race, §3.3).
3. **Stronger client bookkeeping** — the modern engine *tracks* active kits and pending casts, so a lost/unpairable stop strands a tracked object instead of being silently dropped. (Even Blizzard's own 1.13.2 Classic client shipped with "spell visuals … incorrectly continue to loop after the initial cast" — the modern engine failing to terminate vanilla-era visuals, with no proxy involved — §8.)

**How to make 1.14 behave like 1.12 while keeping its features:** have the proxy own the one thing the native pairing gave for free — *guaranteed closure*. For every kit the proxy lets the client start, bind a deterministic stop to the server event that ends it (aura-clear, cast dequeue, channel end, unit destroy). That restores the implicit 1.12 coupling explicitly, without removing any 1.14 capability. See §7.

---

## 5. Hypotheses — ranked, falsifiable

Ranked by how well each explains **all** of O1–O6. Confidence is updated as logs land.

### H7 — START↔GO CastID mis-pairing from prefer-unstarted dequeue on same-spell double-sends. **NEW LEADING (post tester correction). Confidence: MEDIUM-HIGH that the code path exists and fits; UNCONFIRMED until E7.**

**Mechanism (exact, code-grounded).**
1. Off-GCD instant double-send → two un-started pending entries for the same spell: **A** then **B** (`Server/.../SpellHandler.cs:296-308`, both `Enqueue`d; off-GCD so not swept).
2. `SMSG_SPELL_START` → `TryMarkPendingNormalCastStarted` marks the **first un-started** match = **A** (`GlobalSessionData.cs:1291-1300`); `HandleSpellStart` stamps the forwarded START with `A.ServerGUID` and records `PlayerForwardedCastIds[spell]=A.ServerGUID` (`SpellHandler.cs:1262-1266,1458`). **Client opens its cast visual keyed to A.**
3. `SMSG_SPELL_GO` → `TryDequeuePendingNormalCast` **prefers the first un-started** match (`GlobalSessionData.cs:1237-1242`) = **B** (A is now started). GO is stamped `B.ServerGUID` and a `SpellPrepare` for B is sent (`SpellHandler.cs:1569-1587`).
4. START carried **A**, GO carries **B** → **the client never closes cast A → stuck cast animation + looping cast sound.** GO opens+closes B (the second of the "two spell visuals").
5. **#362 cannot save it:** its recovery is an `else if` (`:1727`) reached only when the normal dequeue *returns nothing*; here it returned B, so the recovery is skipped and `PlayerForwardedCastIds[A]` is never consulted.

**Why intermittent (O-"only sometimes"):** the bug needs **B still present at GO time**. The duplicate press's `CAST_FAILED`/`NOT_READY` also dequeues a same-spell entry (and under `SuppressSpellCastErrors` does so *silently*, `SpellHandler.cs:345-355`). If that `CAST_FAILED` lands **before** the GO, B is gone, GO falls back to the started A (`:1260-1265`), and START/GO match — no bug. GO-before-dupFailed → stuck; dupFailed-before-GO → fine. Pure ordering race, latency-independent ⇒ matches O4.

**Explains:** O1 (off-GCD instants are what double-send; the tester spams BF) ✓; O2 (cast kit is sound/render-engine → `/reload` no, `RestartSoundSystem` yes) ✓; O4 (sub-tick ordering, not RTT) ✓; **O5 (survives #362 *by construction*)** ✓✓✓; O6 (a genuinely orphaned cast → stuck button / blocked next cast are native consequences) ✓; the tester's "two spell visuals, cast one sticks" ✓; "jimsproxy introduced it" ✓ (this whole queue/mint/dequeue layer is ours). O3 (party members) ~ — would need the *observed* caster to hit an analogous mis-pair; weaker, may be a different funnel entry.

**What would FALSIFY it (take seriously — 0-for-10 record):**
- E7 shows a stuck cast whose START and GO carry the **same** CastID counter → H7 dead for that instance.
- Stuck instances occur with **no second same-spell entry** in the queue at GO (single press, queue depth 1) → H7 not the cause there.
- The base Xian55 build (no jimsproxy queue) reproduces the same stuck BF → it's not our regression and H7 is wrong about origin.
- It reproduces on a **cast-time** aura spell (where START and GO are seconds apart and only one entry is ever pending) → points back at H1/aura, not H7.

**Confidence honesty:** the *code path is real and I read it*; what's unproven is that it's what the tester hits (vs one of several funnel entries) and that the server actually leaves B unstarted at GO. MEDIUM-HIGH on "this is a genuine latent bug," LOWER on "this is THE one." E7 is one capture and decisive for this hypothesis.

**Relation to #362:** same lifecycle, adjacent code — #362 patched the *dequeue-returns-nothing* orphan; H7 is the *dequeue-returns-the-wrong-same-spell-entry* orphan, which #362's structure can't reach. If confirmed, the fix is to pair GO with the entry START actually marked (match by identity / prefer the *started* entry for a spell that has one), not "prefer unstarted." That's arguably the durable fix the FIFO-by-spellId design has been missing.

---

### H1 — Aura-bound "state" SpellVisualKit has no proxy-synthesized stop edge (+ instant binding race). **DEMOTED to #337 cosmetic-glow candidate (tester says the BF case is the cast sound, not aura). Confidence: LOW-MEDIUM for the BF repro; still plausible for "holy hands"/Insignia.**

*(Original HIGH rating retracted; see §1.3/§1.5/§1.6.)*

#### (former framing retained for the cosmetic-visual variant) Aura-bound "state" SpellVisualKit has no proxy-synthesized stop edge (+ instant binding race). **Confidence: MEDIUM** (downgraded from HIGH per §1.5 — best single account, but rests on the unverified `/reload`-vs-`RestartSoundSystem` inference).

**Mechanism.** Two reinforcing sub-faults:
- **H1a (the missing edge):** when an aura that owns a persistent/state kit is *removed*, the proxy emits only an empty `AuraInfo` (drops the icon) and **never** a `CancelSpellVisual`. All six cancel sites are failure-gated (§9.C). The 1.14 client does **not** reliably stop a running kit just because its aura left the aura table — PR #189 established the sibling fact that "`SMSG_SPELL_FAILURE` alone does not reliably cancel the modern client's caster-side visual kit; only `CancelSpellVisual` does." So the kit loops until the sound engine is reset.
- **H1b (the binding race):** for instants (Kronos emits START+GO+aura in one tick, §3.3), the client mis-binds the state kit so even its own aura-clear can't stop it. A ~20 ms delay masks exactly this upstream (§8).

**Explains:** O1 (state kit ⇔ aura, instant ⇔ START/GO/aura collision) ✓✓; O2 (kit is a free-running sound-engine channel; `/reload` sends icons only, no `CancelSpellVisual`, `CharacterHandler.cs:119`; `RestartSoundSystem` reclaims the channel) ✓✓✓; O3 (party auras use the same seam; `/reload` is local-player-only) ✓; O4 (sub-tick ordering, not RTT) ✓; O5 (separate lifecycle from #362) ✓✓; O6 (the button-lit cast artifacts are lifecycle-#1 co-occurrences) ✓.

**Struggles with:** nothing in O1–O6 outright. Open soft spot: it assumes the client truly won't stop a kit on aura-clear — must be confirmed (does the *buff icon* disappear while the loop continues? E6.1). If the icon also stays, H4 is in play instead.

**Relation to #362:** orthogonal. #362 re-stamps an orphan *GO's* CastID to close the cast bar (lifecycle 1, keyed `ConcurrentDictionary<uint spellId, …>` in `HandleSpellGo`). It cannot touch a kit that has no cast-pairing to repair. PR #362's own body flags #337 (trinket glow) and #345 as "may or may not be covered — report findings," i.e. explicitly not claimed. H1 is the #337 lifecycle.

---

### H5 — The proxy's aura RE-EMIT (Flicker / quad) re-triggers the state kit. **DEMOTED (tester: it's the cast sound, not the aura). Confidence: was co-leading → now a secondary/cosmetic candidate; the mechanism is real but likely not the BF cast-visual repro.**

**Mechanism.** The proxy makes the client re-see an aura it already has — via the explicit Flicker (clear+reapply, `SpellHandler.cs:3195-3205`) on every `SPELL_GO` for an already-present `AuraSpells` spell (Blade Flurry included, `AuraSpells1.csv:4044`), and/or the AURAAPPLICATIONS-quad re-emit (`UpdateHandler.cs:2943-2978`, documented to have misfired "every Hemorrhage proc," `:2964-2966`). Each re-see can re-kick the state kit's sound+animation. Repeated firing (per recast, per proc, per shared-quad neighbour change) → a **proxy-driven re-trigger loop**. External research confirms the client treats a clear→re-add as a *new application* (replays the apply visual/sound), whereas an in-place refresh does not — so the CLEAR is the load-bearing trigger.

**Direct in-codebase precedent (strong).** `1f58cba` ("imp Firebolt sound stuck") proves the 1.14 client enters a **stuck looping sound when it receives rapid-fire visual packets** — there, 10+/sec `CancelSpellVisual`s from a pet-failure storm "chained into a stuck cast sound," fixed by deduping to the first (`GlobalSessionData.cs:325-331`). That is the *same failure mode* as H5: rapid visual churn (cancels there, Flicker re-applies here) → stuck loop. So Shape B is not speculative — the codebase has already seen and patched one instance of exactly this client behaviour.

**Explains:** O1 ✓✓ (only `AuraSpells` hit the Flicker; Blade Flurry confirmed in the set; procs while a self-buff is up are the rogue's normal combat loop); O2 ✓ (re-kicked kit sound is sound-engine state; `/reload` doesn't stop the proxy) — **with a caveat**: if the proxy is *continuously* re-emitting, `RestartSoundSystem` would clear the sound only for it to return on the next re-emit; the observation that it *stays* cleared argues the re-emits are **bursty/event-bounded** (e.g. only during active combat/procs) rather than free-running, OR argues for Shape A (H1) instead. **E0 resolves this directly.** O3 ✓✓ (observing a party member's `SPELL_GO` for an aura they already have flickers *their* slot on your client → re-kicks *their* kit; #362 never covered observed casters anyway); O4 ✓ (re-emit is event-driven, not RTT); O5 ✓✓ (entirely separate from #362's cast pairing); O6 ✓.

**Struggles with:** the "stays cleared after `RestartSoundSystem`" detail unless the re-emits are bursty (above). Needs E0 to confirm chatter exists at all.

**Relation to #362:** none — different code path entirely. Relation to H1: complementary. The Flicker's CLEAR may be what **orphans** the kit (breaks its aura binding) so H1's missing-stop-edge then lets it free-run. If E0 shows chatter → H5 is primary; if E0 shows silence → H1 is primary and the Flicker is at most the binding-breaker at cast time.

**Why this was missed in v1:** the Flicker path emits no `Log.Event`, so it is invisible in every capture taken so far; v1 reasoned only from the visible (failure-path) cancel machinery.

---

### H2 — Double SpellVisualKit fire on instant casts (START *and* GO both kick the kit). **Confidence: MEDIUM (contributing mechanism, likely a facet of H1b).**

**Mechanism.** Kronos emits both `SPELL_START` and `SPELL_GO` for an instant; the client fires the kit's sound on **each** (`SpellHandler.cs:1405-1408`). PR #186 fixed exactly this for *pet* auto-casts by **suppressing the redundant START** ("succubus Lesser Invisibility 7870 … fires sound on each → stuck/repeated sound," merge `bb92257`). The **local-player** path was never given the same suppression — PR #72 tried it, was reverted, and the code now forwards START for all spells (`SpellHandler.cs:1360-1364`).

**Explains:** O1 ✓ (instant-only collision), O3 ✓ (observed casters also get START+GO), O4 ✓, O5 ✓, O2 ✓ (sound engine). **Struggles with:** the *persistence*. A double-fired one-shot cast kit would double, not loop forever. It loops only if the kit is the **aura state kit** — i.e. H2 is the *trigger* and H1's state kit is the *thing that loops*. Treat H2 as a sub-case of H1b for aura spells, and as an independent candidate only for spells whose **cast** kit itself loops.

**Relation to #362:** none (#362 is cast-pairing; this is kit double-fire). Direct kin to #186.

---

### H3 — `SMSG_PLAY_SPELL_VISUAL` forwarded fire-and-forget with no cancel. **Confidence: LOW–MEDIUM.**

**Mechanism.** `HandlePlaySpellVisualKit` (`SpellHandler.cs:2914-2921`) reads `Unit` + `KitRecID` and forwards raw, with no tracking and no guaranteed `CancelSpellVisual`. Vanilla `SMSG_PLAY_SPELL_VISUAL` is fire-and-forget by design (§8). If Kronos sends one for a looping kit and never a terminator, it loops.

**Explains:** a stuck *visual* generically. **Struggles with:** O1 specifically — the Blade-Flurry/trinket/holy-hands repros are aura-state visuals, which are driven by the aura slot, not by a standalone `SMSG_PLAY_SPELL_VISUAL`. A real closure gap, but probably not the primary repro. Worth a cheap check (does a stuck instance carry a `SMSG_PLAY_SPELL_VISUAL`?).

**Relation to #362:** none.

---

### H4 — Aura-remove translation is lost (proxy never emits `aura.slot.cleared`). **Confidence: LOW (but cheap and clean to falsify).**

**Mechanism.** If the server clears the aura via a path the proxy doesn't translate (mask bit not set, or remove conveyed only by a mechanism the slot loop misses), the client keeps both the icon **and** the kit. The proxy's own cache also keeps it, so `/reload`'s cached re-push (`CharacterHandler.cs:132`) re-applies the stale aura — another way `/reload` fails to fix it.

**Explains:** O2 (via stale cache), O3, O5. **Struggles with:** O1 (no reason it'd be instant-specific), and the absence of a reported **stuck buff icon** — H4 predicts the icon persists; the symptom describes sound+animation, not a stuck buff. The discriminator is trivial (E6.1: is the icon there?). If logs show `aura.slot.cleared` *does* fire at fade and the icon *does* clear, H4 is dead and H1 stands.

---

### H6 — Residual cast-spam race orphans the CAST-pose kit (no aura required). **Promoted from "rejected" per §1.5. Confidence: MEDIUM — deliberately under-weighted in v1, needs a fair test.**

**Mechanism.** I dismissed lifecycle-1 too fast. The "hands/glowing" stuck pose is the **`CastKit`**, not an aura kit (§8). The historical "stuck spell when spammed" bug was fixed by delay→**queue/CastID tracking** (`279a67a`), whose own commit message blames "single-variable tracking overwritten **during spell spam** → CastID mismatches." The tester **spams** Blade Flurry. If the current queue/CastID model still has a residual spam race (a *different* race than the double-send #362 fixed), it leaves a cast START/GO unpaired → the cast-pose kit (and its looping cast sound) never terminates. **No aura needed** — the aura-specificity of O1 would just reflect *what the rogue happens to spam*, not a causal requirement.

**Explains:** O2 (cast-pose kit is sound/render-engine, so `/reload` won't clear it, `RestartSoundSystem` will) ✓; O5 (#362 fixed one specific cast race; a residual one survives) ✓✓; O6 (stuck-lit button / blocked cast are *native* cast-lifecycle symptoms — H6 explains them directly, where H1/H5 hand-wave them as "co-occurrence") ✓✓; O4 ✓; O3 (only if the observed cast also races — weaker) ~; O1 ✓ *if* "instant aura" is incidental-to-what's-spammed, **✗ if** stuck instances are provably aura-gated.

**Struggles with:** the strongest objection — **#362 aimed squarely at the Blade-Flurry cast orphan and the tester still loops.** So H6 requires a *distinct* residual cast race #362 doesn't cover (off-GCD double-send interacting with the queue; a spam pattern that overruns the single-shot `PlayerForwardedCastIds` recovery). Plausible but unproven. **Discriminator:** if E0/E2 show the stuck cast has a *cast-bar / button* artifact and a START with no paired GO (lifecycle-1 fingerprints), H6 is live; if it's purely a buff-glow with a clean cast bar, H6 is out and it's H1/H5.

**Why this matters for calibration:** H6 vs H1/H5 is the cast-lifecycle-vs-aura-lifecycle fork. v1 committed to the aura side and built an elaborate story there; an honest read of O6 + the spam history says the cast side deserved a seat at the table. The instrumentation must distinguish them per-instance, not assume.

---

### Rejected / out-of-family (verified, not the chase)
- **CAST-lifecycle orphans** (#341/#344/#362/#365/#366/#367): repair the cast bar / action button / GCD. They explain O6's *accompaniments* but not the looping kit (fail O2/O5).
- **Channel sub-family** (#230/#244/#231): `MSG_CHANNEL_*` desyncs — same disease shape ("missing cleanup," vmangos#3227) but a different lifecycle; O1 says the repro is instant, not channel.
- **Auto-repeat aim latch** (#213 wand, #352 observed bow): same "no retract" structure on the *ranged* kit; already fixed for those specific cases via failure-path / held-START replay.

---

## 6. Experiments — confirm/deny per hypothesis

Bring back the **smallest** capture that discriminates. Run each in **both forwarding modes** — Queue and Low-Latency — because mode changes packet timing/ordering and is itself a discriminator (a timing-sensitive bug behaves differently between modes; a pure missing-edge bug does not).

Existing JSONL events that already exist and matter: `aura.slot.set`, `aura.slot.cleared`, `cast.go.castid_recovered`, `spell.start.suppressed_pet_auto_double_sound`, `spell.failed_other.routed` (carries `sentCancelVisual`), `gcd.begin`. Enable `DebugOutput`. **Logging blind spot to fix first:** the Flicker (`SendAuraRefreshUpdate`) and the AURAAPPLICATIONS-quad re-emit currently log nothing distinctive, so Shape B is invisible — see E6.4.

### E7 — START vs GO CastID on a stuck Blade Flurry. *Decisive for H7 (the new lead). RUN FIRST.*
The cleanest kill/confirm in the whole doc, using events that already exist.
- **Do:** spam Blade Flurry in combat (the natural repro) until one sticks; note the wall-clock instant it sticks. A few minutes of normal rogue play should yield one.
- **Capture (existing events, enable `DebugOutput`):** for the stuck cast, the `SMSG_SPELL_START` and `SMSG_SPELL_GO` forwarded **CastID counters**; `cast.received` (shows the double-send — two `CMSG_CAST_SPELL` for the same spell, distinct client CastIDs); `cast.non_started_swept` / queue-depth fields; whether a `cast.go.castid_recovered` fired; any `cast.error_suppressed` for BF around that instant.
- **Confirms H7:** START's stamped CastID counter **≠** GO's stamped CastID counter, **no** `cast.go.castid_recovered`, and the queue held **≥2** BF entries (the double-send) at GO. That is the prefer-unstarted mis-pair, exactly.
- **Falsifies H7:** START and GO carry the **same** CastID counter (then the stuck visual is *not* a START/GO mis-pair — pivot to E0/H1 or H6), **or** only one BF entry ever existed (no double-send to mis-pair).
- **Proposed one-line diagnostic if the existing CastID fields are ambiguous (describe, don't add):** in `HandleSpellGo`, when a local-player BF GO dequeues an entry, log `{started_entry_castid, dequeued_entry_castid, queue_depth, had_started_entry_for_spell}` — makes the A-vs-B mis-pair unambiguous in one line.

### E0 — CHATTER vs SILENCE during the stuck loop. *Splits Shape A (H1) from Shape B (H5). Run if E7 falsifies H7.*
This is the highest-value capture and reframes everything else.
- **Do:** Get a loop stuck (Blade Flurry in combat). While it is audibly looping and the player is doing **nothing** (no presses, standing still), let it run ~10 s. Then `/run Sound_GameSystem_RestartSoundSystem()` and note whether the sound **stays** gone or **returns within a second or two**.
- **Capture:** the full JSONL for that idle stuck window — specifically any repeating `aura.slot.set`/`aura.slot.cleared` (and, once E6.4 is added, `aura.refresh.flicker`) on the looping unit's GUID.
- **Confirms Shape B / H5:** the window is **full of repeating aura re-emits** at a regular cadence (and/or the sound returns right after `RestartSoundSystem`). Fix lives in the re-emit/Flicker path.
- **Confirms Shape A / H1:** the window is **silent** (no proxy packets for that unit) and the sound **stays gone** after `RestartSoundSystem`. Fix is the cancel-on-fade edge.
- **Also capture the cast-lifecycle fork (H6):** in the same window, note whether there's a stuck cast-bar / lit action button and whether the triggering cast had a `SMSG_SPELL_START` with no paired `SMSG_SPELL_GO`. Present ⇒ H6 (cast-pose orphan) is live; absent (clean cast bar, pure buff-glow) ⇒ H6 out.
- **Calibration:** this is a *first classifier*, not a guillotine. Expect messy or intermittent results, possibly a mix (the symptom is a funnel, §1.5). One clean capture narrows the field; it likely won't settle it in one shot. Don't over-read a single run.

### E1 — Blade Flurry, cast then let it **expire naturally** (do not recast). *Targets H1, H4.*
- **Do:** Stealth-free, out of combat, one Blade Flurry; stand still; wait the full duration; observe. Repeat ~5× per mode.
- **Capture:** the `aura.slot.set` at cast and the `aura.slot.cleared` ~15 s later for that slot/spell; the `SMSG_SPELL_START`/`SMSG_SPELL_GO` pair at cast.
- **Confirms H1 / kills H4:** `aura.slot.cleared` **fires** at expiry **and** the buff icon disappears, **yet** the sound/animation keeps looping → the aura left correctly but the kit was never told to stop (H1a). 
- **Confirms H4 instead:** **no** `aura.slot.cleared` ever fires (or the icon stays lit) while the loop runs → the remove was lost in translation.

### E2 — Capture **packet ordering** of a STUCK vs a NON-STUCK Blade Flurry. *Targets H1b, H2.*
- **Do:** Spam Blade Flurry across several GCDs until one sticks; mark wall-clock when it sticks.
- **Capture:** relative timestamps of `SMSG_SPELL_START`, `SMSG_SPELL_GO`, and `aura.slot.set` for each cast; diff the stuck one against a clean one.
- **Confirms H1b/H2:** the stuck cast shows START+GO+aura within the same ~1 ms (or START after GO), the clean one shows them spread → ordering/collision is the trigger. Cross-check: if **Low-Latency mode sticks more often than Queue** (or vice-versa), the bug is timing-sensitive (H1b/H2). If the **stick rate is identical across modes**, the trigger is the missing edge itself (H1a), independent of timing.

### E3 — **`/reload` vs `RestartSoundSystem` vs target-frame check** while stuck. *Targets O2, H1 vs H4.*
- **Do:** With a loop active: (a) `/run Sound_GameSystem_RestartSoundSystem()` → note if sound stops but **animation** continues; (b) reproduce, then `/reload` → note nothing changes; (c) while stuck, screenshot the player buff bar.
- **Confirms H1:** sound stops on (a), animation may persist; buff icon is **absent** (aura gone, kit orphaned). Kills H4.
- **Hints H4:** buff icon **present** during the loop.

### E4 — **Party-member** occurrence. *Targets O3, H1.*
- **Do:** Two accounts; party up; the *other* rogue/priest casts the instant aura repeatedly while the tester observes; tester does nothing.
- **Capture:** on the tester's proxy, `aura.slot.set`/`aura.slot.cleared` for the *observed* caster's GUID; whether `/reload` on the tester clears an observed-unit loop (it should not — local-only re-push).
- **Confirms H1:** observed-unit loops occur and survive the observer's `/reload` → the seam is unit-agnostic, fix must cover non-local casters.

### E5 — On-use trinket + "holy hands" cross-check (issue #337). *Targets H1 generality.*
- **Do:** Reproduce with an on-use trinket (Insignia) and a priest holy-hands trigger, same expire-naturally protocol as E1.
- **Confirms H1 is the family, not one spell:** identical `aura.slot.cleared`-fires-but-loops-continues signature across all three.

### E6 — Proposed one-line diagnostics (describe only — do **not** add yet)
- **E6.1 (decisive for H1):** at the `aura.slot.cleared` branch (`UpdateHandler.cs:3088`), log the *previous* occupant's `spell_id` **and** `GameData.GetSpellVisualIdFromXSpellVisual(visual)` for that spell — i.e. *"here is the `CancelSpellVisual` we could have sent and didn't."* If the stuck spell's id appears here with a non-zero resolved visual at the moment of fade, the fix target is proven and the surgical fix is a few lines at this exact site.
- **E6.2 (decisive for H1b/H2):** a single event at `SMSG_SPELL_GO` for `AuraSpells` instants logging `{spell_id, had_spell_start_this_tick, ms_since_start, aura_set_seen}` so a stuck capture shows whether START/GO/aura collided.
- **E6.3 (H3):** log every forwarded `SMSG_PLAY_SPELL_VISUAL` (`SpellHandler.cs:2920`) with `unit`,`KitRecID`; if a stuck instance has one and no later cancel, H3 is live for that spell.
- **E6.4 (decisive for H5 — the highest-value diagnostic to add):** emit an `aura.refresh.flicker` event inside `SendAuraRefreshUpdate` (`SpellHandler.cs:3195`) with `{target_low, spell_id, slot, caster_is_player}`, and tag the AURAAPPLICATIONS-quad re-emit branch (`UpdateHandler.cs:2978`) with `appsOnlyChanged=true`. Without this, Shape B is unfalsifiable from logs — E0's "chatter" can't be attributed. This is the one diagnostic worth adding before the next capture.

---

## 7. Architecture verdict (Q3) — surgical-first, in a systemic frame; **hybrid, but NOT timer-convergence**

**Recommendation: do the surgical fix, but adopt it as the first instance of one small principle.** Weighed honestly on effort / game-feel / speed:

**The surgical fix is cheap, deterministic, and high-confidence — but it has a sharp edge (see constraint 1).** The missing edge in §3 is the aura-fade → kit-stop transition, and aura-fade is a **server event the proxy already processes** (`aura.slot.cleared`, `UpdateHandler.cs:3088`; plus the FAR_OBJECTS wipe, `UpdateHandler.cs:538`). Synthesize a `CancelSpellVisual` there for any cleared aura whose spell owns a persistent/state visual (resolve via the same `GetSpellVisualIdFromXSpellVisual` the failure paths already use). This is **not "one more exception"** — it is *completing a structurally-absent edge*, and it generalizes the #189/#213 "cancel-on-failure" pattern to "cancel-on-aura-fade." No timer. Idempotent (clear-if-present). It directly kills the #337/Blade-Flurry-aura case if E1/E3 confirm Shape A (H1). **But it must be debounced per `(source, visualID)`** — the `1f58cba` "imp Firebolt" precedent proves that rapid repeated cancels *cause* the stuck loop. A single cancel at the one slot-clear event is safe; a cancel that fires on every quad re-emit / stack tick is not. Gate it to the genuine slot→0 transition, once. **Do this only if E0 says Shape A (silence).**

**If E0 says Shape B (chatter), prefer the no-new-packet fix.** Q4 research confirms an *in-place* duration refresh does not replay the apply visual, while the Flicker's clear→re-add does. So for kit-bearing auras, refresh the duration with a **single** `AuraUpdate` and drop the CLEAR half of the Flicker (`SpellHandler.cs:3196-3200`). This **removes** churn rather than adding a cancel, sidesteps constraint 1 entirely, and is the lowest-risk fix of all — it neither deletes the load-bearing Flicker (constraint 3) nor adds cancels. It is the leading candidate if the loop is proxy-driven.

**If E0 shows Shape B (H5) instead, the fix is different and equally surgical:** stop the proxy from re-kicking the kit. Either (a) suppress the Flicker's CLEAR half for kit-bearing auras (refresh the duration with a single in-place `AuraUpdate` rather than clear+reapply — the clear is what reads as "aura ended" to the kit), or (b) tighten the quad re-emit so an unchanged kit-bearing aura never re-emits. Both are deterministic, no timer, and narrower than H1's fix. E0 + E6.4 tell us which branch to take; do **not** build the cancel-on-fade edge if the real driver turns out to be a re-trigger we can simply stop emitting.

**The systemic pole is real but should be entered through the front door, not as a rewrite.** The deeper lesson from the PR archaeology (§9.A, ten point-fixes) is that the proxy meticulously models **cast state** (the pending-cast queue, CastID maps, GCD) but does **not model visual-kit state at all** — it forwards starts and hopes the server's implicit stops suffice, which they never do across the seam. The principle to adopt: **every visual/sound kit the proxy lets the client start must register a deterministic stop trigger.** Concretely, a tiny per-unit "active kit registry" keyed `(unit, spellVisualId)` whose entries are retired by the deterministic server events the proxy already sees — aura-clear, cast dequeue, channel end, `SMSG_DESTROY_OBJECT`. #186, #189, #213, and the H1 fix all become *registrations against one mechanism* instead of four bespoke `else if`s, and future kit bugs plug in for free. **There is already a working precedent for this exact pattern in the codebase:** the looping-emote tracker (`ChatHandler.cs:699-728`, `LastLoopingEmoteId`) tracks an active client-side loop so the proxy can synthesize the stop the 1.12 server never sends — a visual-kit registry is the same idea generalized.

**On the "no-timeout/heuristic rule generates the exception pile" tension — partly dissolve it.** Two findings matter:
1. The rule is **already not absolute**: the codebase runs a watchdog that force-evicts cast-time spells whose `SMSG_SPELL_GO` never arrives (`GlobalSessionData.cs:1492` `DrainExpiredWatchdogCasts`, `:2550` `RunWatchdogEviction`, `:2357` `WatchdogDeadlineMs` = TickCount64 + 2500 ms). So a *bounded staleness sweep already has precedent here* — a visual-kit watchdog would not violate house style; it would match it.
2. But for H1 you **don't need a timer** — aura-fade is deterministic. The exception pile in lifecycle #2 comes **not** from the no-timeout rule, but from *never modeling kit closure as a first-class concern*. The convergence/anti-entropy literature (§8) says self-healing from *truly lost* transitions needs periodic assertion — true — but here the transition isn't lost, it's *never synthesized*. Reserve a low-frequency reconciliation sweep (drive expected kits off the authoritative aura set; emit idempotent stops on diff — Agent-B pattern (a)/(e)) as the **backstop** for the residual "server genuinely never sent the aura-clear" case, gated behind whether E1 ever shows a *missing* `aura.slot.cleared`. If E1 shows the clear always fires, no sweep is needed at all — pure deterministic edge.

**Verdict:** **Hybrid, surgical-first.** Ship the deterministic `CancelSpellVisual`-on-aura-fade edge (kills the confirmed case, no timer, house-style-clean). Refactor the three existing kit-cancel fixes + the new one behind a minimal active-kit registry (absorbs the family; still event-driven, no timer). Hold the timer-based convergence sweep in reserve, justified only if logs prove genuinely-lost aura-clears (H4-adjacent), where the existing watchdog precedent makes it defensible. This honours game-feel (instant fix, no added latency unlike the upstream 20 ms delay), speed (a few lines at a known site), and correctness (deterministic), while leaving a clean path to the systemic model without a speculative rewrite.

---

## 7.5 The all-encompassing treatment — is there ONE fix for the whole race class? (the architecture answer)

This is the question the brief actually asked and the patch-hunting (H7, the dequeue) kept dodging. Short answer: **yes for the cast-pairing race family — and it is a recognized pattern, not an exotic rewrite.** Longer answer below, honestly bounded.

### 7.5.1 What KIND of problem this is (the one-sentence diagnosis)
Every bug in this family is the same shape: **the proxy must reconstruct a stateful, identity-keyed cast lifecycle (what the 1.14 client demands: START↔GO paired by CastID) from a legacy event stream that stripped the key (1.12 `SMSG_SPELL_START/GO/FAILED` carry `spellId + caster`, never the client's CastID).** With the key gone, the proxy re-derives the client↔server↔render correspondence by **heuristics** — FIFO-by-spellId, prefer-unstarted, timing windows. This is the textbook **"correlation without a correlation key over a totally-ordered but aliasing channel"** problem. Every "race" is a heuristic mis-match under concurrency (rapid/duplicate same-spell casts, server-initiated procs, reordered GO/FAILED).

### 7.5.2 Why the point-patches never converge
The correspondence is decided **independently in ≥4 places** with subtly different rules: mark-at-START (`TryMarkPendingNormalCastStarted`, first-unstarted), dequeue-at-GO (`TryDequeuePendingNormalCast`, *prefer*-unstarted), peek-at-FAILED (`FirstOrDefault`), recover-at-GO-fallback (#362, spellId-keyed). **You cannot make a system converge when its correctness depends on N separately-maintained heuristics all agreeing** — fix one site and the mis-match reappears at another. H7 is literally two of those sites disagreeing. That structure *generates* the exception pile; it isn't bad luck.

### 7.5.3 The race-condition playbook, applied
The well-researched best practices for exactly this class:
1. **Single decision point / single source of truth.** Decide "which cast is this server event for" **once**, in one place; every consumer reads that decision. Never re-derive it at START, again at GO, again at FAILED.
2. **Stable identity assigned at creation, immutable thereafter.** Give each cast one client-facing CastID at `CMSG_CAST_SPELL` time and stamp **every** forwarded packet for that cast with it. Never re-mint, never re-select.
3. **Deterministic routing by total order.** TCP already gives total order and the server resolves client casts in submission order; route events with one ordered cursor, not per-event re-matching.
4. **Idempotency + dedup for at-least-once.** The off-GCD **double-send** is a duplicate delivery — dedup by client CastID; make the transition idempotent.
5. **Explicit state machine, not implicit dictionaries.** One FSM per cast (`PENDING→STARTED→{COMPLETED|FAILED}`); illegal transitions are logged, not silently guessed into the wrong entry.
6. **Convergence/anti-entropy for the irreducible residue.** Server-initiated events (procs, GO-subspells) have *no* client press to correlate to, and some transitions are genuinely lost — for those, hold authoritative state and assert it on a bounded sweep (the **watchdog precedent already exists**, `GlobalSessionData.cs:1492/2550`).

### 7.5.4 The concrete design — a single Cast Correspondence authority
Replace the multi-site heuristics with **one** structure:
- **One entry per in-flight cast, created at `CMSG_CAST_SPELL`, keyed by the client's own CastID (`ClientGUID`)** — the one reliable key, and the client *supplies* it.
- The proxy assigns the client-facing `ServerCastID` **once** at creation; it never changes.
- Server `START/GO/FAILED` route to entries by **one** deterministic discipline: a submission-order cursor for client-initiated casts, with an **explicit separate path** for server-initiated/unsolicited events (procs, subspells) that mint their *own* identity and never borrow a client entry's.
- **Every** forwarded packet for a cast is stamped with that entry's fixed `ServerCastID`. START and GO therefore match **by construction**, not by re-matching.
- One invariant, enforced in one place: *"every forwarded START's CastID receives exactly one terminating event (GO or FAILED) stamped with the same CastID."* The watchdog closes any entry that violates it (the convergence backstop).

### 7.5.5 The key realization — this is GENERALIZING code we already have, not a rewrite
**#362 already does the load-bearing move** — record the forwarded START CastID, re-stamp the terminating GO to match — **but only as a last-ditch `else if` fallback** (`SpellHandler.cs:1727`). Promote it from *fallback* to *the rule*: **always** stamp the terminating event with the START's recorded CastID for that cast (FIFO-consistent per spellId, so two genuine concurrent casts still pair correctly). The moment the GO's CastID stops depending on *which entry the dequeue heuristic picked*, **H7 evaporates, #362's orphan case evaporates, and the double-send class evaporates** — all from one invariant instead of patch #11. That is the "single source of truth + immutable identity" practice achieved by elevating an existing mechanism. Deterministic; **no new timer**.

### 7.5.6 Honest cost / feel / speed (the brief's weighing)
- **Surgical (just fix H7's dequeue to prefer the started entry):** hours; kills one race; is exception #11; the next interleaving surfaces later.
- **The generalization (CastID pinned at START = the rule; all terminating events re-stamped):** ~1–2 focused days; reuses #362's mechanism and the existing queue; **collapses the entire START/GO/FAILED mis-pair family into one invariant**; deterministic, no timer. **This is the recommended all-encompassing fix for the cast-pairing races.** It also lets you *delete* latency-adding bespoke machinery (the GCD-hold complexity the ping investigation flagged) — plausibly fixing the **30–40 ms overhead and the stuck-cast race together**, because both grow from the same over-engineered cast-state layer.
- **Full FSM + reconciliation:** larger; justified only if logs show the aura-visual lifecycle (H1/H5) and server-initiated residue still leak after the cast-pairing fix. Defer until then.

### 7.5.7 The honest limit (don't oversell "all")
"All race conditions" has a boundary. The design above deterministically closes the **cast-pairing** family. It does **not** automatically fix the **aura→visual-kit** correspondence (H1/H5 — a different mapping: aura-presence→kit, not press→event) nor the truly **key-less server-initiated** residue; those need their own closure rule (cancel/refresh-on-fade, §7) and the convergence backstop respectively. So the complete picture is **one deterministic cast-correspondence core + two small closure rules at the visual/aura edges + a bounded watchdog for the lost-transition residue** — which is finite and principled, versus the open-ended exception pile. That is the real answer to "one way to fix all of these": not literally one line, but **one invariant per correspondence, enforced in one place, instead of N heuristics re-decided everywhere.**

## 7.6 Competitor reference — sugarproxy / 糖糖代理 ([doc](https://heitu.org/doc/%E7%B3%96%E7%B3%96%E4%BB%A3%E7%90%86.html))

The leading competitor (Chinese-language; English-only searches missed it — it is **NOT** apocryphal). Its documented architecture validates §7.5's direction and reframes one of our own choices:

- **Sequential vs parallel cast model.** The doc contrasts the "traditional" model — *"客户端施放法术 → 服务器返回结果 → 客户端收到结果 → 施放下一个技能"* (cast → wait for result → receive → cast next; each failure costs ~2 round-trips) — with sugarproxy's *"类似于并行操作的模型"* (**parallel-operation model**): cast requests queue and stream to the server **without waiting** for prior responses.
- **Feature #21 verbatim:** *"开发技能队列模块，**不加延迟**修复**卡技能**"* — a skill-queue module that fixes **stuck skills without adding delay**; *"重写宠物技能机制，将宠物技能也加入技能队列中"* — pets folded into the **same** queue.
- **Its "stronger than HermesProxy" list is our exact bug family:** debuff-refresh-on-reapply (= our Flicker), no slow-walk after CC, no UI shake after warrior charge/intercept, **no ~300 ms off-GCD instant delay (Sprint/Evasion)**, interrupted cast bar clears, hunter auto-shot unstuck, **卡潜行 (stuck stealth)** and **卡技能 (stuck skills)** fixed.

**The exact model the doc describes.** Legacy/Hermes: `cast → wait for server result → receive → cast next` — every cast costs **2 round-trips + server processing** before the next can go. Tangtang: the next cast is forwarded **without waiting** for the prior's result, so requests hit the server **concurrently**. Worked example from the doc (50 ms latency, Arcane Explosion fails on GCD then succeeds): sequential pays `50+50+GCD + keyboard-gap + 50+50 + 2× server-proc`; parallel pays `50+50+GCD` *overlapped with* `keyboard-gap+50+50 + 1× server-proc` — **~100 ms saved per failed-then-retried cast.**

**What this tells us (carefully — it's a feature doc, not source):**
1. **The competitive bar is a unified, no-added-delay, *parallel* skill queue** (pets included) — consistent with §7.5.4/§7.5.5 (single authority, no timer).
2. **The decisive realization: a parallel cast model is ONLY safe with identity-based correspondence.** Firing many casts without waiting puts *more* same-spell casts in flight at once — which would make jimsproxy's FIFO-prefer-unstarted heuristic mis-match *more* often, not less. For Tangtang to fire in parallel AND not have "卡技能," its server-event→press correspondence **cannot** be the fragile heuristic ours is — it must be identity/order-pinned. So the competitor's success is concrete evidence that **§7.5's identity-pinned correspondence is both the race fix and the prerequisite for the latency fix.** One change unlocks both.
3. **Our GCD-hold/serialize choice (issue #43) is the likely shared root of BOTH complaints.** jimsproxy *holds and serializes* casts to tame mid-GCD mashing (the NOT_READY failure storm); that holding both adds the latency players feel AND manufactures the multiple same-spell pending entries H7 mis-routes. But jimsproxy **already has the other half of Tangtang's answer** — `SuppressSpellCastErrors` silently eats NOT_READY/SpellInProgress (`SpellHandler.cs:345`). So the path is visible: **go parallel + suppress the failure storm client-side (like Tangtang) instead of hold-and-serialize**, on top of identity-pinned matching. That plausibly removes the race *and* closes the 30–40 ms gap.
4. **Caveat:** the doc lists *卡技能/卡潜行* (stuck skills/stealth) but not specifically a looping *cast sound*, and it's behavior-not-source — so it's a validated **direction**, not a drop-in spec. We're inferring "identity-based correspondence" from "parallel + no stuck skills," which is strong but not stated.

## 7.7 Architecture rework — design & phased migration (the spell-casting arch change)

The §7.5 verdict, made concrete against the code, plus the explicit **Hold-and-Fire cleanup**. This is a design to approve before implementing — it touches the most-patched code in the repo, so it is **phased**, each phase independently shippable and testable.

### 7.7.1 Target model — identity-pinned, parallel cast correspondence
- **Immutable identity.** Each cast gets one client-facing CastID, fixed at `CMSG_CAST_SPELL` time (reuse the client's own `ClientGUID`, or mint once). **Every** forwarded server event for that cast (`SPELL_START`/`SPELL_GO`/`FAILED`) is stamped with it. START/GO pair by construction — no re-matching, no prefer-started/unstarted heuristic, no #362 fallback.
- **Parallel forward.** Casts go to the server **immediately**, without waiting for the prior result (sugarproxy's "并行" model, §7.6) — i.e. the current **Low-Latency path becomes the only path**; the GCD hold-and-fire is deleted.
- **Failure suppression absorbs the flood.** Firing in parallel re-introduces the mid-GCD `NOT_READY` storm the hold was added (issue #43) to prevent. We already have the antidote: `SuppressSpellCastErrors` eats `NOT_READY`/`SpellInProgress` client-side (`SpellHandler.cs:345`). Make it always-on for the parallel path so the client never sees the storm.
- **GCD shown, not enforced.** The client still needs the action-bar GCD swirl: keep the `SpellCooldownPkt` synth on a successful GO (`SpellHandler.cs:1639`), but **stop holding casts** — the swirl is cosmetic; the server is the GCD authority.

### 7.7.2 Phased migration (each phase = one PR to `beta`, each independently verifiable)
- **Phase 0 — DONE (#372).** H7 prefer-started dequeue. Stops the immediate stuck-cast for the double-send; buys breathing room. Doesn't change architecture.
- **Phase 1 — identity-pinning (lower risk, additive).** Make "the terminating event's CastID = the START's recorded CastID for that cast" **the rule**, not #362's last-ditch `else if`. Keep the queue and the hold for now. This deterministically kills the entire START/GO/FAILED **mis-pair class** (H7 and its cousins) without changing cast *timing/feel*. Mostly additive; easy to test (extend `CastGoPreferStartedTests` / `PlayerForwardedCastIdsTests`). **Recommended regardless of the latency decision.**
- **Phase 2 — go parallel + Hold-and-Fire cleanup (higher risk, changes feel).** Delete the GCD hold-and-fire; forward all casts immediately; make failure-suppression always-on; keep GCD-cooldown synth + watchdog. This is what closes the 30–40 ms gap and matches sugarproxy. **This is the phase that needs the cleanup step below and the most testing.**

### 7.7.3 Cleanup — the Hold-and-Fire code to REMOVE (Phase 2)
Located; precise file:line inventory being compiled by recon. The removal set (GlobalSessionData.cs unless noted):
- `BeginGcd` (`:1967`) + the `_gcdExpiryTimer` `System.Threading.Timer`, `OnGcdTimerElapsed` (`:2052`), `_gcdGeneration`/`_gcdTimerHasFired` staleness guards.
- `OnGcdHeldCastFire` (`:401`) + `WorldSocket.ForwardHeldGcdCast`, `TakeHeldCastIfReady` (`:1607`), `PeekHeldGcdCast` (`:2044`), `CancelGcdHold` (`:1993`).
- Cast-time holds: `HoldCastDuringCastTime` (`:408`), `TakeHeldCastTimeCast` (`:418`), `ClearHeldCastTimeCast` (`:431`), `ForceHoldCast` (`:1592`).
- The hold gate: `TryHoldCastDuringGcd` (`:1932`), `IsGcdHoldActive` (`:1902`), and the `if (!isOffGcd)` hold branch in `HandleCastSpell` (Server SpellHandler).
- `GetAdaptiveFireOffsetMs` (`:1651`) + RTT smoothing **iff** only used for the fire offset (verify — RTT may feed other features).
- **Simplifies away:** the `IsOffGcd` sweep *exemption* (#344/#366) — once every cast forwards in parallel, "off-GCD doesn't get held" is the default, so the special case largely dissolves.

### 7.7.4 KEEP / REWIRE (do NOT delete)
`SuppressSpellCastErrors` (becomes load-bearing), the watchdog (`DrainExpiredWatchdogCasts`/`RunWatchdogEviction` — still need to evict orphaned started casts), the `CancelSpellVisual` failure-synth family (#189/#213/#367), the GCD `SpellCooldownPkt` synth (cosmetic swirl), the threat/HealComm/finisher-snapshot hooks, `ClearNonStartedNormalCasts` (still useful for button-lit cleanup), and the aura/visual closure rules from §7 (separate lifecycle).

### 7.7.5 The real risk to validate before Phase 2
**The parallel `NOT_READY` flood.** Issue #43's history (4 `NOT_READY` failures per Arcane-Explosion GCD on Kronos) is the reason the hold exists. Going parallel re-creates that flood; suppression hides it from the *client*, but we must verify it causes no **server-side** problem — Kronos has a *tuned anticheat* (`RESEARCH.md` §5) that could flag rapid cast spam, and the failures must not desync any other state. **Phase 2 needs a Kronos capture of a parallel cast-spam burst** (cast Arcane Explosion through its GCD repeatedly) watching for: anticheat kick, `packet.error`, or any state the suppressed failures leave dangling. Sugarproxy ships parallel on these same servers, so it's almost certainly safe — but "almost certainly" is exactly what's burned us before.

### 7.7.6 The decision for the user
- **Phase 1 only** — deterministically fixes the desync *class*; keeps current latency/feel; low risk. 
- **Phase 1 + 2** — also closes the latency gap and matches the competitor; deletes a large, concurrency-tricky machinery (net simplification); higher risk, needs the §7.7.5 Kronos validation.
Recommendation: **do Phase 1 now** (it's the right fix and low-risk), and treat **Phase 2 as a deliberate follow-up** gated on the parallel-flood capture — not because it's wrong, but because it's the kind of timing change that deserves its own validation cycle rather than riding along with the correctness fix.

## 8. External parallels (Q4) — fuel, condensed (full cites in §10)

- **The mechanism, named by other devs.** vmangos maintainers on stuck *channel* visuals: "suspected there is some sort of cleanup packet that the client is expecting but is not receiving" ([vmangos#3227]). The classic **"glowing hands"** bug: a missing stop packet → "the target never stops playing the visual hands-glowing and waving cast animation," **server-side and visible to everyone** (= O3). AzerothCore #19102 *proves* the shape: re-adding a dropped `SMSG_CAST_FAILED` for `SPELL_STATE_PREPARING` un-sticks casts — "send the stop packet again."
- **The smoking gun (now pinned, with a correction).** Upstream **HermesProxy added two artificial-delay keys, `ServerSpellDelay` + `ClientSpellDelay` (default 15 ms each, "Like 20" is only the troubleshooting suggestion), "to fix stuck spell animations/sounds."** Verified from source: the delay is a `Thread.Sleep` at the head of the **cast-path handlers** — the shared `HandleSpellStartOrGo` parser (gating both `SMSG_SPELL_START` and `SMSG_SPELL_GO`), **local-player-gated**, plus the CMSG cast/item-use handlers — **never the aura `UPDATE_OBJECT`/`AuraUpdate` or the visual-kit packets.** So it spaces the local player's own START/GO away from each other and the surrounding aura update ⇒ confirms the **instant-cast START+GO+aura same-tick spacing race** (§3.3), and tells us a *visual-only, aura-fade-driven* fix avoids the DPS cost because the upstream knob paid it on the **cast** path. **jimsproxy deliberately removed both keys in v4.1** (`279a67a`) in favour of the queue/CastID model — so re-adding a delay is a known-abandoned dead end (§7 constraint 2).
- **The missing edge has a name.** `SpellVisual.dbc` carries separate kit columns per phase: `PrecastKit`/`CastKit`/`ImpactKit` (the cast lifecycle — this is where the **"glowing/holy hands" cast-pose** lives, *not* an aura glow), **`StateKit`** (the buff-active visual — Blade Flurry whirl, trinket glow, food/drink), **`StateDoneKit`** (the explicit teardown kit played when the state ends, added in 3.1.0), and `ChannelKit`. The native engine has both an aura-bound teardown and an explicit `SMSG_CANCEL_SPELL_VISUAL_KIT` (`SendCancelSpellVisualKit`). TrinityCore #19486: a kit sent with `Duration = 0` plays **indefinitely until told to stop**. The 1.12 server sends none of these modern stop edges; the proxy synthesizes none for the aura path ⇒ the `StateKit` free-runs. **Note the repro split this implies:** "holy/glowing hands" (cast-pose) can be a cast-lifecycle orphan (closer to #189/#362), while Blade Flurry/trinkets are `StateKit` orphans (Shapes A/B) — the symptom cluster spans both, which is itself consistent with O1/O5.
- **Not just proxies.** Blizzard's own **WoW Classic 1.13.2** shipped with "several spell visuals for Hunter, Warlock, and Paladin … incorrectly continue to loop after the initial spell cast or impact." The modern engine itself struggles to terminate vanilla-era kits — supporting H1's premise that the client won't self-stop a state kit.
- **On-use items are a known weak spot.** cmangos #1070: item-triggered casts route visuals differently from normal casts (= the trinket repro).
- **Distributed-systems framing.** A state-coupled visual is a *derived view of the authoritative aura set*; modeling it as `f(current auras)` rather than a flag toggled by start/stop deltas makes it convergent-by-construction (delta-state CRDT intuition, [arXiv:1603.01529]). Pure delta-application is brittle to one lost stop — exactly this bug. Anti-entropy / periodic assertion is the textbook self-heal, at the cost of a sweep tick. Make any re-emitted stop **idempotent** (clear-if-present) for safe at-least-once. This is the theory backing the §7 backstop sweep — but only for genuinely-lost transitions, not the never-synthesized edge H1 targets.
- **"sugarproxy":** no discoverable public WoW project by that name (negative result).

---

## 9. Verified code map (file:line — personally read unless marked ⟦agent⟧)

### 9.A The two lifecycles in the PR record
- **Lifecycle 1 (CAST / CastID / GCD):** #341 (`4246754`), #344 (`6f99de2`), #352 (`e05495f`, observed-bow held-START replay), #362 (`5603c3d`, GO-side CastID recovery), #365 (`2b524ce`, item-use orphan eviction), #366 (`ce2fc91`), #367 (`8a51dd8`, pet GCD release).
- **Lifecycle 2 (AURA/VISUAL/SOUND):** #186 (`bb92257`, suppress pet instant START → double-sound), #189 (`cbd0bf2`, cancel-on-failure visual for local player), #213 (`af1e556`, extend cancel to auto-repeat). **The aura-fade→kit-stop edge is in NO merged PR — only open issue #337.**

### 9.B Aura apply/remove translation (the seam)
- `UpdateHandler.cs:2985-3078` — slot set → `AuraInfo` apply (`aura.slot.set` at :3045).
- `UpdateHandler.cs:3079-3098` — slot clear → **empty `AuraInfo`** to drop icon (`aura.slot.cleared` at :3088). **No `CancelSpellVisual` here.** ← H1a fix site.
- `UpdateHandler.cs:538` — FAR_OBJECTS → empty `AuraUpdate(guid,true)` wipes buff bar. **No `CancelSpellVisual`.**
- `CharacterHandler.cs:119-161` — `/reload` re-push: `AuraUpdate(playerGuid, true)` walking cached slots, **local player only**, **no `CancelSpellVisual`**. ← explains O2/O3.
- `SpellHandler.cs:1739-1759` — `SPELL_GO` aura-refresh (`SendAuraRefreshUpdate`); `:2923` `HandleUpdateAuraDuration`; `:2972` `HandleSetExtraAuraInfo`. All emit `AuraInfo`, none emit a kit stop.

### 9.B′ Proxy aura RE-EMIT paths (Shape B / H5 — the re-trigger suspects)
- `SpellHandler.cs:3121-3206` `SendAuraRefreshUpdate` — the **"Flicker"**: empty-slot `AuraUpdate` (`:3196-3200`) then full reapply (`:3203-3205`). **Emits no `Log.Event`** → invisible in captures (add E6.4). Fires from `HandleSpellGo` `:1739-1759` for `AuraSpells` already on target.
- `HermesProxy/CSV/AuraSpells1.csv:4044` (and `AuraSpells2.csv:2855`) — **Blade Flurry 13877 is in the set** → the Flicker path is live for the primary repro spell. (Trinkets 2457/2458 also present, `:569-570`.)
- `UpdateHandler.cs:2943-2978` — AURAAPPLICATIONS-quad re-emit; documented misfire "every Hemorrhage proc refreshed Kidney Shot" at `:2964-2966`; active periodic-tick investigation noted at `:2980-2981`.
- `ChatHandler.cs:685-728` — **precedent**: `LastLoopingEmoteId` tracker; 1.14 client loops `EMOTE_ONESHOT_DANCE` until an explicit `SMSG_EMOTE` the 1.12 server never sends. Same disease, already mitigated by a track-the-loop pattern.
- `GroupHandler.cs:516-1074` — party-member aura states go to the **party-frame** path (`PartyMemberAuraStates`), separate from the 3D-visual seam; an *in-range* party member's stuck world-visual still flows through the normal `UpdateObject`/`SPELL_GO` seam above (so O3 is the same mechanism on another GUID, not a distinct bug).

### 9.C Every `CancelSpellVisual` is failure-gated (no aura/success path)
`SpellHandler.cs:522` (pet cast failed), `:807` (FAILED_OTHER mob interrupt), `:823` (FAILED_OTHER pet), `:1130` & `:1148` (SPELL_FAILURE pet), `:1197` (SPELL_FAILURE local player — the #189/#213 gate). Packet class: `SpellPackets.cs:989`.

### 9.D Cast lifecycle / #362 / kit-fire
- `SpellHandler.cs:1237` `HandleSpellStart`; `:1452-1458` records `PlayerForwardedCastIds[spellId]`; `:1488` `HandleSpellGo`; `:1717-1737` #362 GO-side recovery (`cast.go.castid_recovered`).
- `SpellHandler.cs:1403-1425` — pet instant START suppression (the "double-sound" / kit-fires-on-each note). `:1360-1364` — player instant START is forwarded for all spells (PR #72 reverted).
- `SpellHandler.cs:2914-2921` `HandlePlaySpellVisualKit` — forwards `SMSG_PLAY_SPELL_VISUAL` raw, no cancel (H3).

### 9.E Sound forwards (fire-and-forget; no stop opcode handled)
- `MiscHandler.cs:202` `HandlePlaySound`, `:211` `HandlePlayObjectSound`, `:195` `HandlePlayMusic`. **No `SMSG_STOP_SOUND` handler exists** — confirms the looping sound is a kit side-effect, not a standalone packet.

### 9.F Timer precedent (house-style nuance for §7)
- `GlobalSessionData.cs:2357` `WatchdogDeadlineMs`, `:1492` `DrainExpiredWatchdogCasts`, `:2550` `RunWatchdogEviction` — bounded staleness eviction already exists for cast-time spells.
- ⟦agent⟧ No aura-duration sweep / kit reconciliation exists (Agent C). Aura lifetimes are entirely server-event-driven.

### 9.G Prior art & dead ends (git archaeology — avoid re-treading)
- **Flicker lineage:** origin single re-emit `534c15d` (Xian55, 2026-01-23); the clear+reapply "Flicker" block `ffe3197` (Mirasu, v4.3.0-beta). **Never reverted; load-bearing.** Don't delete it (constraint 3).
- **Quad phantom-refresh — 3 narrowings, never a clean fix:** `4f9b83f` (per-slot gate) → `5eae9a6` (stack byte-diff, `appsOnlyChanged`) → `cf6b418`/#325 (clamp reversal, the Hemorrhage/Kidney-Shot fix at `UpdateHandler.cs:2964`). Structurally recurring; the durable fix (diff the raw wire quad instead of reconstructing from a post-processed cache) has **not** been attempted.
- **Over-cancel regression:** `1f58cba` ("imp Firebolt sound stuck") — rapid failure-path `CancelSpellVisual`s chained into a stuck loop; deduped to first (`GlobalSessionData.cs:325-331`). **The constraint-1 evidence.**
- **SpellDelay:** added upstream `a933f71` (v3.4), **removed `279a67a` (v4.1)** for queue/CastID tracking. Don't re-add (constraint 2).
- **Never attempted anywhere in history (0 hits across `--all`):** `CancelSpellVisual`/kit-stop on aura-fade / SPELL_GO-success / slot-clear; `RestartSoundSystem`; `StopSpellVisualKit`. The cancel-on-fade avenue is genuinely unexplored — no prior failure to fear, but heed constraint 1.
- **Adjacent channel-loop work** (other forks/branches): `dd271f7` "write ChannelObjects DynamicUpdateField for channel-loop anim", `ab2a28e` "unique CastIDs per channel tick" — the nearest existing "looping animation" handling, for the channel sub-family.

### 9.H Correction to v1 framing (from external research)
"Holy/glowing hands" is the **cast-pose kit** (`CastKit`/`PrecastKit`), not an aura `StateKit` — so §1/§3.1's example list mixes two sub-families: cast-pose (hands, cast-lifecycle) vs `StateKit` (Blade Flurry whirl / trinket glow). The looping-sound repro that survives #362 is the `StateKit` family; the hands case may be a cast-lifecycle orphan. E5 should treat them as potentially distinct.

---

## 10. Open questions / next steps

0. **H7 patch is applied (v1.4) — now run the tester on the patched build (this IS E7).** Have the rogue play their normal Blade Flurry rotation with `DebugOutput` on. Confirmation: (a) the stuck cast loop **stops happening**, and (b) `cast.go.prefer_started` events appear in the JSONL (proving the H7 ambiguity was occurring and is now resolved to the started entry). If the loop **persists**, H7 was not their funnel entry → proceed to E0/E6.4 (aura shapes) and re-examine. Decide on commit/PR-to-`beta` only after the tester confirms.
0b. **Then (only if E7 kills H7) add E6.4 + run E0 — chatter vs silence** for the aura shapes (H1/H5). The aura path is now the *cosmetic*/#337 branch, not the BF lead.
1. **Then E1 + E3** (Blade Flurry expire-naturally, both modes). Within Shape A, these split H1 (loop persists after a confirmed `aura.slot.cleared` + icon gone) from H4 (no clear / icon stays).
2. **Does the buff *icon* persist during the loop?** (E3c screenshot.) Single most decisive observation not yet in hand. Predicted absent (H1).
3. **Queue vs Low-Latency stick-rate** (E2): timing-sensitive (H1b/H2) vs missing-edge (H1a). Decides whether the fix also needs an ordering tweak or just the cancel edge.
4. **Confirm the client won't self-stop a state kit on aura-clear.** If E1 shows the clear fires + icon clears + loop continues, this is proven and the §7 surgical fix is greenlit. If logs ever show a *missing* `aura.slot.cleared`, escalate to the H4/backstop-sweep branch.
5. **Resolve the visual id at fade** (E6.1 diagnostic) — proves we hold the data to synthesize `CancelSpellVisual` at `UpdateHandler.cs:3088`.
6. Once H1 is confirmed: scope the fix as (a) the deterministic cancel-on-fade edge, then (b) optional refactor behind a minimal active-kit registry (absorbs #186/#189/#213). PR → `beta`.

### Citations (external, from §8)
vmangos#3227 · ownedcore "Looping spell animations" / "Glowing Hands" · azerothcore#19102, #18226 · HermesProxy v3.4 release notes (artificial spell delay) · Blizzard "WoW Classic 1.13.2 Known Issues" (loop-after-cast) · cmangos/issues#1070 · gtker SMSG_SPELL_START / SMSG_SPELL_GO · wowdev SMSG_UPDATE_OBJECT / DB/SpellVisual / DB/SpellVisualKit · TrinityCore#19486 · arXiv:1603.01529 (delta-state CRDTs) · GeeksforGeeks anti-entropy · Gaffer On Games state synchronization. (Full URLs in the research appendix held by the investigation thread; WebFetch was blocked this run so values like exact opcode hex are name-confirmed only.)

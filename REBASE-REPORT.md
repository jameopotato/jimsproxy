# Xian55 Rebase — Go/No-Go Report

**Status as of 2026-04-18 (async prep session — no Kronos login test yet):**

- `rebase/xian55` branch was already created from `xian55/master` HEAD (`2f62a4e`) before this session.
- Cherry-pick **1 of 6 landed** on top (`c1bc9b1` — "Initial fork: set AssemblyName, add CHANGES.md, fork-specific README"). Conflict was trivial (README header + retained-upstream section). Resolved by keeping JimsProxy branding and Xian55's expanded body below.
- Cherry-pick **2 of 6 attempted and aborted** — 8-file conflict on Phase 1 logging. Aborted for analysis; branch is clean.
- 4 remaining cherry-picks not yet attempted.
- `.NET 10 SDK 10.0.202` and `.NET 6 SDK 6.0.428` both present on this machine — either target can be built locally.

**Verdict: GO.** Rebase is mechanically achievable in one more focused session. No findings so far invalidate the §2 recommendation.

---

## Key architectural findings

### 1. Xian55's new metrics system ≠ our Phase 1 JSONL — they coexist

`Framework/Metrics/ProxyMetrics.cs` is per-opcode latency histograms (min/max/avg/p50/p95/p99), opt-in via `--metrics` command-line arg, console-summary-at-shutdown. Our Phase 1 `Log.Event()` JSONL emits per-packet structured events for offline analysis. Different purpose, different sink.

At the c2s dispatch site in `WorldSocket.HandlePacket`, Xian55 added:
```csharp
if (HermesProxy.Server.MetricsEnabled)
{
    long t = Stopwatch.GetTimestamp();
    handler.Invoke(this, packet);
    HermesProxy.Server.Metrics.RecordClientToServerLatency(universalOpcode, Stopwatch.GetElapsedTime(t).Ticks);
}
```
Our Phase 1 patch wraps `handler.Invoke` in try/catch and emits `packet.translated` / `packet.error`. **Both belong.** Resolution: keep Xian55's metric-record call, wrap with our try/catch, emit both.

### 2. Xian55 has no `Log.Event()` equivalent

Confirmed via `git grep` on `xian55/master/Framework/Logging/Log.cs`. Our JSONL emitter (Log.Event + StartStructuredLog + structured_log_path surfacing) is purely additive. Their Log.cs added `LogType.SpanMiss` and `LogType.SpanStats`; ours added `LogType.Verbose`. Merge by keeping all three new log types.

### 3. `global.json` .NET 6 pin becomes OBSOLETE on Xian55

`xian55/master` has no `global.json`. Target framework is centrally set to `net10.0` in `Directory.Packages.props`. Adding our net6 pin on top would break the build (SDK 6 cannot target net10).

**The net6-vs-net10 question becomes empirical, not a config decision:**
- The *reason* bd8b2e3 pinned net6 was "our naive net10 port triggered Warden on Kronos" — that was OUR net10 port.
- Xian55 has a tested .NET 10 migration (PR #16 merged, plus `feature/dotnet11` branch in-progress).
- Does Xian55's net10 trigger Kronos Warden? **Unknown.** Testable only at home.
- Decision branches:
  - If green → inherit Xian55's net10, drop our global.json pin entirely (cleanest)
  - If Warden triggers → we need a containment plan; possibly can't use Xian55 on Kronos until their net10 path is Warden-safe. This would be the rebase's go/no-go on a bigger scale.

### 4. Our follow-up commits' hook sites survive in Xian55

- `57eb471`'s pre-dispatch `packet.in` moves the hook to `WorldSocket.ReadDataHandler`'s switch — that function still exists in Xian55 at line 238 (renamed to `ReadData` but semantically unchanged).
- `3eda291` adds a new file `HermesProxy/World/KnownBenignOpcodes.cs` — not present in Xian55, purely additive.
- Both of these ride on top of 71c368a's dispatch hooks being in place.

### 5. Xian55 code style conventions (from jimsproxy/CLAUDE.md)

- File-scoped namespaces in newer code (`namespace Foo;`)
- Nullable enabled solution-wide (`string? X`, `null!` initializers)
- Central package management (no version attrs on `<PackageReference>`)
- CypherCore GPL v3 headers preserved on ported files

Our Phase 1 files use legacy `namespace Foo { ... }` and lack nullable annotations. When porting, match Xian55 style to avoid CI `WarningsAsErrors` for `nullable`.

---

## Per-commit cherry-pick plan

### ✅ 1. `31d175c` — Fork metadata (DONE)

Landed as `c1bc9b1`. README was manually resolved; `.csproj` auto-merged preserving `AssemblyName=JimsProxy` + Copyright. `CHANGES.md` added cleanly.

**Follow-up idea (non-blocking):** update the `**Upstream:**` line in README to reflect the new fork chain (`JimsProxy ← Xian55/HermesProxy ← WowLegacyCore/HermesProxy (archived)`). Scope-creep; can be a separate commit.

### ⏳ 2. `71c368a` — Phase 1 structured JSONL logging (NEEDS MANUAL PORT)

8 files conflict; all resolvable but none auto-mergeable. **Budget: 60-90 minutes** for a careful port that matches Xian55 style.

Per-file port plan:

| File | Nature | Action |
|------|--------|--------|
| `Framework/Logging/Log.cs` | +125 lines pure additive (Log.Event, StartStructuredLog, LogType.Verbose, flush-on-exit) | Merge enum (keep Verbose + Xian55's SpanMiss/SpanStats). Append our static methods at end. Match file-scoped namespace. |
| `HermesProxy/HermesProxy.config` | 2 new `<add key>` entries (StructuredLog, VerboseLog) | Add to Xian55's config at the logging section (find the new layout — Xian55 reorganized). |
| `HermesProxy/Configuration/Settings.cs` | 2 new fields + 2 reads | Add `public static bool StructuredLog;` and `VerboseLog;` with Xian55's `null!`/non-nullable style, 2 corresponding `config.GetBoolean(...)` reads. |
| `HermesProxy/Server.cs` | session.start / session.end emits | Port at matching function body positions. |
| `HermesProxy/Auth/AuthClient.cs` | realmd.connect / realmd.handshake emits | Ditto. |
| `HermesProxy/World/Server/WorldSocket.cs` | world.client.connect + packet.in + try/catch around handler + handlers.registered.c2s | **Wrap** Xian55's metrics-recording Invoke in our try/catch. Both metrics record AND our packet.translated/.error fire. |
| `HermesProxy/World/Client/WorldClient.cs` | world.mangos.connect + packet.in + try/catch + handlers.registered.s2c | Same wrap-around approach. |
| `HermesProxy/GlobalSessionData.cs` | session.disconnect emit in OnDisconnect | Port at matching location. |

**Verification of port:** `dotnet build` on rebase/xian55 after this cherry-pick must compile clean. Smoke test deferred to evening Kronos login.

### ⏳ 3. `bd8b2e3` — Warden fix + net6 pin (SPLIT INTO TWO)

The original commit bundles two concerns. On Xian55 they need to split:

**3a. Warden handshake tolerance (KEEP)**
- ~24 lines in `HermesProxy/World/Client/WorldClient.cs`: new `IsIgnorableDuringHandshake(Opcode)` helper + conditional `_isSuccessful = false` guard in HandlePacket default case.
- Mechanical. Low risk. Applies cleanly if we've already placed Phase 1's try/catch wrapper (it goes in the same function).
- **Expected outcome:** no conflict with Xian55 — they don't have a fix for this; upstream issue #320 is still open.

**3b. `global.json` net6 pin (DROP)**
- Do not re-apply. Xian55 is net10; pinning net6 breaks their build.
- Also drop the `HermesProxy.csproj` `TargetFramework` back-to-net6 line if present in our patch.
- If Kronos evening smoke test FAILS on Xian55's net10 because of Warden bytes mismatch → open a separate investigation; don't blanket-pin. We may be able to ship a self-contained net10 binary whose Warden signature is acceptable; or we may need to help Xian55 make their net10 path Warden-compatible; or we roll back to a pre-net10 Xian55 commit and pin there.

Also from bd8b2e3: the "back-ported Log.StartStructuredLog() eager-open" (15 lines in Log.cs, 4 lines in Settings.cs) — this was part of step 2's Phase 1 port; should already be in place by the time we get here. Verify no duplicate addition.

### ⏳ 4. `7ea2f2d` — RESEARCH.md (TRIVIAL)

Pure doc add. Should cherry-pick clean; even if minor conflict, trivial merge.

### ⏳ 5. `3eda291` — KNOWN_BENIGN allowlist + packet.ignored (LOW RISK)

- `HermesProxy/World/KnownBenignOpcodes.cs`: new file, not present in Xian55 → adds clean.
- `HermesProxy/World/Server/WorldSocket.cs` + `HermesProxy/World/Client/WorldClient.cs`: adds `IsModernOnly` check to default-case branch of HandlePacket — coexists with our Phase 1 hooks; both still there after step 2.
- Mechanical port once step 2 landed.

### ⏳ 6. `57eb471` — Block 1 follow-ups (LOW-MODERATE RISK)

Three changes:
- **KNOWN_BENIGN v2** (+3 opcodes): 3-line edit to `KnownBenignOpcodes.cs`. Trivial.
- **Pre-dispatch `packet.in` instrumentation**: moves emission site from `HandlePacket` to `ReadDataHandler` switch top, removes the now-duplicate emission inside `HandlePacket`. Xian55's `ReadData()` is the same function (renamed). Port straightforward if step 2 placed the hooks cleanly.
- **Benign realmd close re-tag** in `AuthClient.ReceiveCallback`: ~15 lines conditioning LogType/message on whether auth completed. Independent of Xian55's changes there — should apply cleanly.

---

## Evening action plan (ordered)

Open a clean terminal session on the `rebase/xian55` branch and execute:

1. **Port 71c368a manually** per the per-file table above. ~60-90min. Commit with `git commit -m "Phase 1: structured JSONL logging (ported onto Xian55)"`.
2. **`dotnet build`** (uses Xian55's net10 target). Fix any nullable warnings-as-errors from our ported files. If build is clean, move on.
3. **Port bd8b2e3 warden fix only.** Skip the global.json. ~15min.
4. **Cherry-pick 7ea2f2d RESEARCH.md.** 1min.
5. **Cherry-pick 3eda291 KNOWN_BENIGN.** ~10min (re-port dispatch hooks if not clean).
6. **Cherry-pick 57eb471 Block 1 follow-ups.** ~15min.
7. **`dotnet build`** — must be green.
8. **Replace `Hermes/JimsProxy.exe`** with the new build output (back up the old one first: `Hermes/JimsProxy.exe.pre-xian55-rebase`).
9. **Kronos login smoke test.** Watch for:
    - Warden-related disconnect during world-auth → .NET 10 bytes are the issue; fall back to pinned Xian55 commit pre-net10
    - Clean character-select + world-enter → GO, rebase succeeds
    - Any new `packet.error` from combined Xian55+JimsProxy code → inspect JSONL, patch incrementally
10. If login is green, **bump launcher submodule** (`launcher-tauri`) and rebuild the `.exe`. Already-existing build script (`scripts/build-jimsproxy.ps1`) may need a tweak because it assumes `-p:UsePublishBuildSettings=true` — verify it still produces a single-file exe on net10.

**Total budget**: ~2.5 hours focused work at home. Within the §2 original 2-4hr estimate.

---

## If evening goes sideways

| Failure | Fallback |
|---------|----------|
| `dotnet build` fails on nullable warnings | Add `#nullable disable` at top of each new JimsProxy file; revisit annotation later |
| `dotnet build` fails on missing xUnit v3 / Sep / MTP packages | Run `dotnet restore` first; Xian55 uses central package management and some packages land via Directory.Packages.props |
| Kronos login: client gets stuck at "Logging in to game server" | Check JSONL for `packet.untranslated` or `world.mangos.connect_error`. If Warden-flavored, the net10 bytes are suspect |
| Kronos login: Warden bans account (severe) | STOP. Revert `Hermes/JimsProxy.exe` to `.pre-xian55-rebase` backup. Contact Kronos support to lift the ban if possible. Retry only with net6-built tree (may require pinning Xian55 to pre-net10 commit) |
| Xian55's metrics default-on breaks something | `--metrics` is opt-in; they default off. Verify MetricsEnabled=false is the default path |
| Something subtler | Keep both exes around. We can bisect between "pre-rebase master" and "post-rebase xian55" to locate the regression |

---

## Non-scope reminders

- **RESEARCH.md catalog verification (Task B)** and **JSONL analyzer script (Task C)** are separate async-friendly tasks from this session's menu. Either can be done during future work hours without blocking the evening's rebase execution.
- **Block 2 gameplay tests** depend on a working build on Kronos, which this rebase is a prerequisite for (we want to collect Block 2 data against the post-rebase binary so findings map onto the future maintained fork, not the dead-end master).
- **Fork attribution edit** (README "Upstream:" line pointing to Xian55) is cosmetic and can be folded into a later commit.

---

_Generated during 2026-04-18 async prep session. Author: Claude + jameopotato._

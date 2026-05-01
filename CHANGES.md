# JimsProxy Changelog

A fork of [WowLegacyCore/HermesProxy](https://github.com/WowLegacyCore/HermesProxy) (archived November 2024), adapted for the Classic 1.14 Launcher with emphasis on diagnostic visibility for fixing translation bugs against Twinstar/Kronos servers.

## Entry template

```
## YYYY-MM-DD — Brief title
**Issue:** what was wrong (with log evidence if available)
**Change:** what was changed, where (file paths)
**Verification:** how we confirmed the fix
```

---

## Dispatch & Hook Points Map

This section documents where in the HermesProxy source the packet dispatch happens and where we've added instrumentation. Updated as we find each site.

*(to be populated during Phase 1 implementation — see plan at launcher-tauri for context)*

| Concern | File(s) | Entry point | Notes |
|---------|---------|-------------|-------|
| Program startup | `HermesProxy/Server.cs` | `Main` / static constructor | |
| Auth handshake | `HermesProxy/Auth/**` | *TBD* | |
| World server (faces client) | `HermesProxy/World/Server/**` | *TBD* | |
| World client (faces mangosd) | `HermesProxy/World/Client/**` | *TBD* | |
| Packet dispatch (modern → legacy) | *TBD* | *TBD* | |
| Packet dispatch (legacy → modern) | *TBD* | *TBD* | |

---

## Changes

## 2026-04-08 — Initial fork from HermesProxy master

**Issue:** HermesProxy upstream is archived; we need a maintained fork for the Classic 1.14 Launcher with better diagnostics for identifying translation bugs on Twinstar/Kronos servers.

**Change:**
- Mirrored from `WowLegacyCore/HermesProxy` via `git push --mirror` (full history preserved, including all tags v3.1 through v3.10)
- `HermesProxy/HermesProxy.csproj`: added `<AssemblyName>JimsProxy</AssemblyName>` so the output binary is `JimsProxy.exe` (was `HermesProxy.exe`)
- `HermesProxy/HermesProxy.csproj`: updated `Copyright` and `Authors` to credit both upstream and fork
- Added this `CHANGES.md` and updated `README.md`

**License:** Preserved GPL v3 LICENSE file unchanged. Our fork is also GPL v3. Source distribution obligation applies.

**Verification:** `dotnet publish -p:UsePublishBuildSettings=true -c Release -r win-x64 -o build/` produces `build/JimsProxy.exe` (to be confirmed once build script is written).

## 2026-04-18 — Xian55 rebase + Phase 1/Warden/KNOWN_BENIGN ports

Rebased onto [Xian55/HermesProxy](https://github.com/Xian55/HermesProxy) HEAD `2f62a4e` (v4.2.4) for 18 months of community fixes (Span-based packet rewrite, fixes for many of the issues catalogued in RESEARCH.md, working .NET 10 migration). All our previously-shipped JimsProxy changes ported on top:

- **Phase 1 structured JSONL logging** (originally `71c368a`): all 8 hook sites ported to match Xian55 file-scoped/nullable style. Critical: Phase 1's per-packet try/catch wrapper coexists with Xian55's `ProxyMetrics.RecordC{lient,Server}To{Server,Client}Latency` calls — both fire on every dispatch (one is opt-in via `--metrics`, the other always-on JSONL).
- **Warden handshake tolerance** (originally `bd8b2e3`): the C# fix landed; the bd8b2e3 net6 SDK pin was deliberately dropped because Xian55 is centrally net10. Whether Xian55's net10 build triggers Kronos Warden the way our naive net10 port did is the open empirical question for evening smoke test.
- **KNOWN_BENIGN allowlist** (originally `3eda291`): new file `HermesProxy/World/KnownBenignOpcodes.cs` (file-scoped namespace, matches Xian55 style). 31 opcodes marked as modern-only. Used by both WorldSocket (c2s) and WorldClient (s2c) to emit `packet.ignored` instead of `packet.untranslated` for known noise — keeps signal-to-noise ratio high in JSONL.

The `Log.StartStructuredLog()` eager-open (originally back-ported into bd8b2e3 as a side effect) was folded into the Phase 1 port commit since Settings.cs needs to call it during config load — before bd8b2e3's other changes.

Pre-rebase master HEAD: `95f5bf1`. New `rebase/xian55` branch tracks Xian55/master; this is the live branch going forward if the smoke test passes.

### Block 1 follow-ups (originally `57eb471`, ported manually post-rebase)

Three observation-layer improvements applied on top of the rebase commits above. Cherry-pick conflicted because 57eb471 was written against the pre-Xian55 structure (legacy namespace braces, HandlePacket without metrics coexistence); reapplied surgically:

1. **KNOWN_BENIGN v2:** +3 opcodes (`CMSG_MOVE_SET_COLLISION_HEIGHT_ACK`, `CMSG_GUILD_GET_RANKS`, `CMSG_GET_ACCOUNT_NOTIFICATIONS`) flagged as modern-only based on Block 1 Test 1.1 / 1.2 `packet.untranslated` noise. All Wrath+ / Cata+ / MoP+ subsystems with no 1.12 equivalent. Totals: 34 modern-only opcodes.
2. **Pre-dispatch `packet.in`:** previously hooked inside `HandlePacket`, which left 9 inline-handled opcodes (`CMSG_PING`, `CMSG_KEEP_ALIVE`, `CMSG_AUTH_SESSION`, `CMSG_LOG_DISCONNECT`, `CMSG_ENABLE_NAGLE`, `CMSG_CONNECT_TO_FAILED`, `CMSG_ENTER_ENCRYPTED_MODE_ACK`, `CMSG_AUTH_CONTINUED_SESSION`, `CMSG_SERVER_TIME_OFFSET_REQUEST`) invisible to JSONL. Hook moved upstream to `ReadData` switch entry; payload now carries `path: "inline" | "dispatch"` for pathway grouping. Unblocks AFK-kick investigation where we couldn't see CMSG_PING timing.
3. **Benign realmd close re-tag:** every successful login produced a red `Error | AuthClient | Socket Closed By Server` after auth completion because Kronos realmd intentionally closes the socket after serving the realmlist. `ReceiveCallback` now checks `_response.Task.IsCompleted` — if true, logs `Network` with "Realmd disconnected after successful auth (expected)"; if false, keeps the `Error` + `SetAuthResponse(FAIL_INTERNAL_ERROR)` path for real auth failures.

No behaviour changes to dispatch or translation; all three are observation-layer improvements.

## 2026-04-29 — Latency fixes: Nagle ignore + GCD double-cast window + RTT-adaptive offset

Three related latency improvements, shipped as PR #87 targeting beta.

### CMSG_ENABLE_NAGLE ignore

**Issue:** The 1.14 client sends `CMSG_ENABLE_NAGLE` when the user unchecks "Optimize Network for Speed," re-enabling Nagle's algorithm (~200ms write coalescing) on both the client-facing and server-facing sockets. For a game proxy that needs low-latency bidirectional forwarding, this adds pure delay.

**Change:** `World/Server/WorldSocket.cs` — the `CMSG_ENABLE_NAGLE` case no longer calls `SetNoDelay(false)`. The packet is still processed through decryption so the AES-GCM nonce counter stays in sync (per Xian55 fix `2960e77`). TCP_NODELAY, set at connection time in `WorldSocketManager` and `WorldClient.ConnectCallback`, is now permanent for the session.

**Verification:** JSONL confirms two `CMSG_ENABLE_NAGLE` events in test session when the setting is unchecked. No latency degradation observed post-fix.

### GCD double-cast window fix

**Issue:** `OnGcdTimerElapsed` (GlobalSessionData.cs) was clearing `_gcdExpireTimestampMs = 0` when the hold timer fired, removing GCD protection during the ~RTT window before the server responded with `SMSG_SPELL_GO`. Spam-presses during this window bypassed all guards (`IsGcdHoldActive()` and `HasStartedNormalCast()` both returned false) and sent duplicate casts to the server.

**Change:** Three sub-fixes in `GlobalSessionData.cs` and `World/Server/PacketHandlers/SpellHandler.cs`:

1. **Don't clear `_gcdExpireTimestampMs` on timer fire** — GCD stays active until natural expiry. Presses between `fireAt` and `expireAt` are caught by `IsGcdHoldActive()` and stored in the now-empty `_heldGcdCast` slot. The next `BeginGcd` (from incoming `SMSG_SPELL_GO`) picks them up.

2. **`HasForwardedPendingCast()` guard** (position 3 in HandleCastSpell) — catches presses after local GCD expires but before `SMSG_SPELL_GO` arrives. Uses `ForceHoldCast()` to store in `_heldGcdCast` and `SendCastRequestFailed` for displaced casts.

3. **Fire held cast on failure** — `TakeHeldCastIfReady()` in `HandleCastFailed` fires any held cast immediately when the server rejects the forwarded cast and GCD is no longer active, preventing the player from getting stuck.

Guard ordering in HandleCastSpell:
1. `HasStartedNormalCast()` → DROP (cast-time duplicates)
2. `IsGcdHoldActive()` → HOLD (instant-cast GCD window)
3. `HasForwardedPendingCast()` → HOLD (post-GCD forwarded cast window)
4. *(reserved for PR #86's `HasInFlightNormalCastForSpell()` → DROP)*
5. Normal forward path

**Verification:** Arcane Explosion spam on Kronos — each GCD window should produce exactly one `spell.held_fire` event. LoS-fail during spam should fire the held cast on failure.

### RTT-adaptive GCD fire offset

**Issue:** With `SpellCastEarlyFireOffsetMs = 0`, the held cast fires at exact local GCD expiry. Since `SMSG_SPELL_GO` must travel back from the server (~RTT/2) before the next GCD starts, each GCD cycle takes ~1700ms instead of 1500ms (verified: Arcane Explosion JSONL data shows consistent ~1700ms intervals, implying ~200ms RTT to Kronos).

**Change:** `GlobalSessionData.cs` + `World/Client/PacketHandlers/MiscHandler.cs` + `World/Client/WorldClient.cs` + `World/Client/PacketHandlers/SpellHandler.cs`:

- **RTT measurement:** Timestamps forwarded `CMSG_PING` sends in `WorldClient.SendPing` (covers both client-originated and proxy keepalive pings), measures `SMSG_PONG` returns in `MiscHandler.HandlePingResponse`. EMA-smoothed (alpha=0.2), requires 3 samples before activating (~45s warm-up at 30s ping interval with both client and keepalive pings contributing).
- **Adaptive offset:** `GetAdaptiveFireOffsetMs()` returns `Clamp(Round(smoothedRtt * 0.5), 0, 100)`. RTT/2 means the cast arrives at the server right as the server-side GCD expires. Capped at 100ms (covers up to 200ms RTT).
- **Static fallback:** During warm-up (`< 3` samples), falls back to the `SpellCastEarlyFireOffsetMs` config value (default 0, manual clamp 0–50ms). The adaptive path can reach 100ms because EMA smoothing provides its safety margin; the manual path stays conservative at 50ms max.
- **Diagnostics:** `rtt.sample` events log each measurement. `gcd.begin` events now emit `fire_offset_ms` (adaptive) and `smoothed_rtt_ms` for post-session analysis.

**Verification:** After 2+ minutes of play, `gcd.begin` events should show `fire_offset_ms > 0`. Arcane Explosion GCD intervals should decrease from ~1700ms to ~1600ms. Zero or near-zero `NOT_READY` failures expected.

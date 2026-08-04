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

## 2026-07-30 — Text emotes no longer cancel channels (#244)

**Issue:** /clap (any anim-bearing text emote) mid-bandage canceled the channel (#244). Vanilla's `HandleTextEmoteOpcode` (vmangos ChatHandler.cpp) interrupts channels + strips ANIM_CANCELS-flagged auras for every anim-bearing text emote; the 1.14 client happily sends the emote mid-channel and Kronos killed the bandage. Wire-proven on PTR 2026-07-30: channel death arrives one RTT (220ms) after `CMSG_SEND_TEXT_EMOTE`, vs natural 8.06s completion on the `/e` chat-emote control.

**Change:** `Server/ChatHandler.HandleSendTextEmote` drops text emotes while the local channel window is open; window tracked in `GameSessionData` from `MSG_CHANNEL_START/UPDATE` (duration-bounded + 2s grace, closed early on zero-time update). Event: `emote.text.dropped_channeling`. NOTE: the emote is silently dropped, not queued — whether true 1.14 wants queue-and-release is an open question on #244 (Blizzard Classic Era test requested); extracted as a single-concern slice from the #433 batch per the new one-by-one process.

**Verification:** PTR both phases wire-verified 2026-07-30 — unfixed: /clap and /dance each kill the channel in one RTT, `/e` control completes; fixed: both held (`dropped_channeling` events, ids 24/34), channel runs full 8s, post-channel /clap forwards normally with SMSG_EMOTE echo.

## 2026-07-30 — Player collision height matches the rendered model (#359)

**Issue:** Female tauren stuck in doorways they visibly cleared (Tarren Mill, #359) — fit at fresh login, permanently stuck after any shapeshift/unshift, mount/dismount, or cross-map teleport; relog cured it. Wire evidence (`.pkt` decode, 2026-07-29/30 PTR sessions): the proxy's synthesized `SMSG_MOVE_SET_COLLISION_HEIGHT` sent 3.29861 for ♀ tauren while the client rendered her at 2.47396-equivalent scale — the May 2026 tauren render fix (52ab6f88, CMS hotfix K=0.75) changed what the client renders but the collision math kept reading stock CMS, a 4/3 inflation. Fresh login only "worked" because a cache-ordering accident meant creates never emitted the packet at all (two competing height sources). The doorway was measured to sit in (3.012, 3.299): a visibly *taller* dire bear at a truthful 3.000 fit while the ♀ humanoid at an inflated 3.299 did not. Same inflation existed for ♂ tauren (3.012, sub-threshold on this door). The upstream `Math.Max` hitbox floor (_BLU 2023) also inflated CMS<1 forms (travel/NE cat/NE moonkin) above their visible size.

**Change:** collision ≡ visible model, one formula, every state (`HermesProxy/World/Client/PacketHandlers/UpdateHandler.cs`):
- `ComputeVisualCollisionHeight` (new, pure static): `CollisionHeight × ModelScale × CMS_effective × wire scale` — exactly the render pipeline established by 52ab6f88's DB2 parse. Drops the upstream Max() floor and the displayId-keyed `GetModelData` lookup.
- `GameData.GetClientEffectiveDisplayScale` + parsed `HotfixCreatureDisplayScales` (`GameData.cs`): the CMS the client actually renders with — hotfix override when pushed, else stock; auto-gated by which hotfix file the client build loads.
- Create-path cache publish reordered (`ReadValuesUpdateBlockOnCreate`): field cache now written before the store hook, matching values-path semantics, so login/teleport creates emit the same collision packet as every later update — one height source in all states. Side effect: the `unit.mount.changed`/`unit.dynamic_flags.changed`/`unit.npc_flags.changed` DebugOutput diagnostics stop firing their create-noise (0→X on create); note their genuine-transition logging was already dead (values path mutates the cache in place before the comparison) — pre-existing, not addressed here.
- New DebugOutput diagnostic `unit.collision_height.sent` (guid, display, mount, raw scale, height, reason).

Value deltas (vanilla): ♀ tauren humanoid 3.29861→2.47396, ♂ 3.01219→2.25914, travel 1.66667→1.33333, NE cat 1.875→1.6875, NE moonkin 2.125→1.9125, scale-buffed players now uniform; bear/dire bear/tauren cat/aquatic/ghost wolf/gnomes/all CMS=1 races byte-identical.

**Verification:** truth-table unit tests (`CollisionHeightTests`, 12 cases incl. field-observed corpus values) — red-run against the extracted upstream formula reproduced the wire values exactly (3.29861/1.66667/1.875) proving faithful extraction, green with the parity formula; full suite 771/771. Field: PTR ♀+♂ tauren at the Tarren Mill door — login/shift/unshift/mount/teleport must all produce identical packets (2.47396 ♀) and identical door behavior; forms and a non-tauren control unchanged on the wire.

## 2026-07-30 — Forward the "quest log is full" error (SMSG_QUEST_LOG_FULL)

**Issue:** Accepting a quest with a full log silently did nothing — vanilla answers with SMSG_QUEST_LOG_FULL (0x195, empty body) and the proxy dropped it (11 corpus sessions in DROPPED-S2C-AUDIT.md, #433; also personally observed — the error simply never appears through the proxy).

**Change:** Forward as modern `QuestLogFull` (0x2A87, empty body, TC-master-sourced) — `QuestPackets.cs` class + `QuestHandler.HandleQuestLogFull`. First s2c translation extracted from the #433 batch under the one-fix-one-PR process.

**Verification:** empty-body layout test; field gate (audit question U4): fill the quest log on PTR, accept one more — the red "Your quest log is full." error must appear. If the 1.14 client turns out to only render this via SMSG_DISPLAY_GAME_ERROR, this slice gets reworked before merge rather than shipping a silent no-op.

## 2026-07-30 — Chronoboon tooltip refreshes at every login from the server's own template push (#446, Mirasu)

**Issue:** The boon's server-rewritten tooltip (the stored world-buff list) went stale on the 1.14 client whenever the state changed where the proxy couldn't see it (e.g. store/restore in a native 1.12 session, then a proxied login) — the client caches item templates per id and never re-queries, worst case a supercharged boon rendering the default empty tooltip until the next proxied use.

**Change:** Kronos-gated, keyed on the boon entry (`GameData.KronosChronoboonEntry` 25007). Kronos PUSHES the per-player rebuilt boon template unsolicited during login (a solicited entry query answers with the BASE template — 2026-07-30 log — so the push is the only correct login-time source); `HandleItemQueryResponse` remembers it, and `StoreObjectUpdateInternal` mints a fresh alias from it before the entry substitution on the boon's create — the natural create carries the never-seen alias id, no destroy+recreate in the normal path. Unchanged relogs skip the re-mint (Name/Description compare). Ordering fallback parks the GUID until the push lands (then mint + destroy/recreate, the validated use-path flow). Cooldown correctness: `SMSG_SEND_KNOWN_SPELLS` item cooldowns now also captured keyed by item entry (store and restore are different spells), and the true remaining sweep is repainted after the aliased create forwards. Events: `item.chronoboon.login_refresh_at_create` / `_waiting_push` / `_after_push` / `login_cooldown_repaint`.

**Verification:** Kronos PTR 2026-07-30 — native-1.12-stored buffs show correctly at 1.14 login first try; a pre-fix stale alias was detected and self-healed; restore and re-store through the proxy each re-minted from the post-use push; bag clink intact; no parse failures in session JSONL.

## 2026-07-30 — Pair Kronos's empty-GUID inventory rejection with the pending item cast (#442)

**Issue:** An item double-press straddling its own SPELL_GO (window = client frame time, so low raid FPS widens it) passes the then-empty dup guard; Kronos rejects the second CMSG_USE_ITEM with SMSG_INVENTORY_CHANGE_FAILURE carrying result 23 and BOTH item GUIDs EMPTY (sealed live 2026-07-29, raw wire `17|0000|0000|00`). The GUID-keyed dequeue matches nothing and the orphaned queue entry is unreapable (unstarted, IsOffGcd since #345, never SPELL_FAILURE-peeked) — `HasForwardedPendingCast()` jams every on-GCD press silently until relog or cross-map transfer, and that item stays dead for the session.

**Change:** `GlobalSessionData.TryDequeueOldestUnstartedItemCast` — an anonymous (empty-GUID) rejection pairs FIFO with the oldest forwarded-unstarted item-use entry and releases its button with DontReport (`ItemHandler.cs`, both wire variants). Deterministic packet pairing, no timers. Review hardening: empty GUIDs never enter the GUID-keyed dequeue (`TryDequeueItemCast` early-return — a `default` GUID would value-match normal spell entries and evict a healthy cast), and the vanilla handler gates the fallback on the pre-backfill WIRE GUID so the keyring move-backfill can't mask it. Known accepted edge: two different items in flight plus an unrelated empty-GUID inventory failure inside one RTT can wrong-evict — cosmetic, self-limiting. Ungated events: `cast.item_use_rejected_by_inventory` (result, dequeued_via guid|fifo_fallback), `cast.result.discarded` (the previously-invisible status≠2 CAST_FAILED swallow).

**Verification:** 9 state-layer tests (`ItemUseInventoryRejectionTests`, review pins proved red against unguarded code first); suite green. Field gate before tag: spam a gem across its own GO at `/console maxfps 10` — expect `dequeued_via: fifo_fallback`, no `spell.held_pending`, casting uninterrupted, item usable after cooldown.

## 2026-08-02 — World-entry window tripwire (stage 0, DebugOutput-gated)

**Issue:** The world-transition loading screen is an unguarded forwarding window: from SMSG_TRANSFER_PENDING until CMSG_WORLD_PORT_RESPONSE the proxy forwards everything the legacy server sends straight through, while the modern client silently discards state addressed to movers it hasn't created yet (documented precedent: the stuck-logout-stun create-baked root, #431). Suspected to explain a family of stuck-state bugs — post-BG-exit movement lockup (primary, `/reload`-curable), stuck action buttons, taxi/mount desync — whose intermittence would track loading-screen duration (machine-dependent). Nothing was measured yet: no record of what actually lands in the window or how long the window lasts. See WORLD-ENTRY-CONTRACT-INVESTIGATION.md.

**Change:** Diagnostics only, zero behavior change, all emission gated on `DebugOutput`. `GameSessionData` gains four window-telemetry anchors (seq, transfer-pending tick, NEW_WORLD tick, forward count). Events: `worldentry.window.opened` / `.new_world` / `.aborted` (Client/MovementHandler.cs), `worldentry.window.closed` with the NEW_WORLD→ack `duration_ms` (Server/MovementHandler.cs), and the core tripwire `worldentry.window.forward` in `WorldSocket.SendPacket` — the sole choke point to the modern client — logging seq/phase/conn/opcode/size/ms plus best-effort `MoverGUID` (cached per-type reflection, window-only) and `mover_is_self`. Per-window cap of 1000 forward lines; `.closed` reports `forward_lines_suppressed` so truncation is never silent. `worldentry.client_signal` records the candidate readiness signals (`loading_screen_notify`, `set_active_mover`, `init_active_mover_complete`) with window-relative timing to pick the right replay trigger for a future hold-and-replay contract.

**Verification:** Build clean, suite 781/781. Field use: a few BG exits / hearths / portals on a DebugOutput build — window empty of state-bearing packets kills the hypothesis; force ops / teleports / player-addressed state in the window names the hold-list for the stage-2 contract.

## 2026-08-03 — Carried-root cure + root-ceremony breadcrumb (the BG-exit movement lockup)

**Issue:** After leaving a battleground — reported specifically by players who spam-click "Leave Battleground" the instant it appears — the player arrives able to turn, mount and cast but unable to move or jump, permanently, cured only by `/reload` or relog (#328 family). Root cause chain, established over rounds of RE + live harness runs: every Kronos arrival force-roots the player (ROOT ×2, unroot ~1s later — wire-verified 18/18 arrivals); an instant leave departs while the BG-end root is being removed, the server's unroot fires in the between-maps window and is silently discarded (cmangos `Unit.cpp:751` `!IsInWorld()` — deterministic), and the client arrives still force-rooted while the server considers it mobile. Harness-proven equivalence: swallowing exactly one unroot reproduces the reported symptom table including the `/reload` cure.

**Change:** `World/Client/WorldEntryCeremony.cs` (pure logic + tests, TransportClearGate precedent). **(a) The cure:** `GameSessionData.ClientBelievesRooted` tracks what the client was last told about its root state (set on self force/spline root forwarded, cleared on either-family unroot — live-verified the client honors both); on crossing a loading boundary still rooted, the proxy synthesizes the missing force-unroot: cross-map at the player's first destination update after NEW_WORLD (delivered end-of-UPDATE_OBJECT, the stuck-stun pattern, so it can never race the create), same-map (hearth/tele/portal) at the client's teleport-ack. Sentinel counter `0xFFFFFF02`; the client's ack is swallowed (`worldentry.carried_root.cure_acked`) so the legacy server never sees an ack for an op it didn't send. Gate is belief-ONLY — deliberately not gated on destination movement flags, which are an echo of the client's own reported (stuck) state and provably veto the cure forever; a legitimately rooted cross-map arrival is re-rooted by the server's own arrival ceremony. **(b) Always-on breadcrumb:** per-arrival ceremony accounting (root/unroot forwards + acks + spline-family legs + mover-init) flushed at the next boundary; an unclosed ceremony logs ONE `worldentry.ceremony.unclosed` line so any field Export Diagnostics carries the discriminator (never-sent vs client-swallowed vs wrong-family) with DebugOutput off.

**Verification:** Suite 791/791 (8 new: unclosed/anomalous truth tables anchored to the healthy 18/18 shape and the stuck-stun golden capture's missing-ack fingerprint, belief-only gate, sentinel disjointness). Live end-to-end on PTR (2026-08-03): manufactured strand at login (harness swallowed one unroot; player fully movement-locked, `/reload`-curable — the red case), then with the cure: hearth and mage teleport each arrived mobile with no `/reload`, wire showing `carried_root.armed` → `carried_root_cured` → the client ACKING the synth unroot. Field gate: BG spam-click reports should cease; any residual lockup now arrives with the breadcrumb naming its failure mode.

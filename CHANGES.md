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

## 2026-08-29 — Cure the post-Charge stuck-strafe latch (orphaned pending-strafe flag)

**Issue:** after a Charge the character sometimes came out of the spline stuck strafing —
transmitted in every movement packet, so server-visible — until a strafe key was pressed;
forward, backward and turn keys could not clear it. Wire-proven mechanism (.pkt movement-flag
decode over 4 sessions / 83 charges): the 1.14.2 client queues a strafe key pressed mid-air
during a forward-held jump as `PendingStrafeLeft/Right` instead of applying it; when the Charge
spline hijacks the fall, the key release mid-spline is swallowed without clearing the pend, and
the spline-exit landing applies the stale pend as a real strafe flag with no key behind it. A
pending strafe start in the `CMSG_MOVE_CHANGE_TRANSPORT` the client emits at charge GO appeared
on exactly the latching charges (3/3 field latches + 2/2 deliberate reproductions).

**Change:** `World/Client/ChargePendLatchCure.cs` (new, pure decision logic): arm predicate
(pending strafe starts only), fire predicate (armed pend's real bit set with the pend bit gone,
i.e. the orphan observed), 3 s arm TTL, synth counters 0xFFFFFF03/04 inside the established
`IsSynthCounter` swallow range. `Server/PacketHandlers/MovementHandler.cs`: arm inside the
existing CHANGE_TRANSPORT drop (`pend_latch_armed` added to that always-on event for wild
frequency); fire at the top of `HandlePlayerMove` on the first client packet showing the pend
applied — a synthetic SMSG_MOVE_ROOT + SMSG_MOVE_UNROOT pulse to the client (corpus-proven: a
force-root wipes all client movement flags and makes the client emit the matching stop opcodes,
which forward and correct the server's view too; a force-unroot rebuilds flags from physical
key state, so a genuinely held key resumes same-frame — safe in both worlds); dedicated ack
branch in `HandleMoveForceAck2` swallows both acks. `Client/PacketHandlers/MovementHandler.cs`:
a real self force-root between arm and fire disarms. Kronos's charge-bracketing spline
root/unroot addresses the charge TARGET, never the charging player, so no server anchor exists
and the cure fires on the orphan's own appearance. Config kill switch `ChargePendLatchCure`
(default true) gates the pulse only. Diagnostics `charge.pend_latch.cure_sent` /
`charge.pend_latch.cure_acked` are DebugOutput-gated (review 2026-09-05).

**Verification:** 24 unit tests (`ChargePendLatchCureTests`) pin the arm/fire predicates
against the verbatim wire shapes of every specimen plus the counter-range invariants (cure acks
can never reach the legacy server); suite 985/985. Field-verified 2026-08-29, 5 recipe attempts:
2 armed → 2 cures → 0 latches, both ROOT acks returned `client_flags=Root` only (the orphaned
strafe wiped), same-frame ROOT→UNROOT applied in order, cure within ~100 ms of touchdown, no
false-positive fires anywhere in the session.

---

## 2026-08-22 — Repair the Release workflow (Windows-only) so releases carry assets

**Issue:** `.github/workflows/Release.yml` already builds and attaches packaged binaries plus
checksums — but it had **failed every one of its 10 runs** and had not run at all since
2026-05-20. That is why every release, including v5.1.9, all four v5.2.0 betas and v5.2.0 itself,
shipped with **zero assets** and had to be cut by hand. Two causes, both from inheriting the
workflow unchanged across the fork:

1. **Binary name.** `HermesProxy.csproj` sets `<AssemblyName>JimsProxy</AssemblyName>`, so publish
   emits `JimsProxy.exe`. The workflow still referenced `publish/HermesProxy` when marking the
   binary executable, in the MacOS `lipo` step, and in the verify step's smoke run. `chmod` on a
   non-existent file exits non-zero, killing the Ubuntu leg first and letting fail-fast cancel
   Windows and MacOS — exactly the job pattern in the final run.
2. **RuntimeIdentifier conflict.** `HermesProxy.csproj` pins
   `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` whenever `UsePublishBuildSettings` is set,
   which all three matrix legs passed. The Ubuntu leg (`--use-current-runtime`) and the MacOS leg
   (`--runtime osx-arm64`) were each fighting a csproj hardcoded to Windows.

Run logs are past GitHub's retention (HTTP 410), so the diagnosis is from source rather than logs.

**Change:** `.github/workflows/Release.yml` — dropped the Ubuntu and MacOS matrix legs and made
`build` a single `windows-latest` job. JimsProxy is a Windows-only fork (csproj pins `win-x64`,
the launcher is Windows-only, and `WowClassic_ForCustomServers.exe` exists only on Windows), so
those legs could never have produced a usable artifact. Publish now writes straight to `-o publish`
instead of globbing `bin/Release/*/publish`. Added an explicit publish-output assertion for
`JimsProxy.exe` + `HermesProxy.config` + `CSV/` so a rename breaks the build loudly instead of
silently producing an empty archive. The verify job asserts on **archive contents** (binary,
config, and a full CSV set) rather than executing the binary, which an Ubuntu runner
cannot do. Assets are renamed `JimsProxy-<tag>-win-x64.zip`. The release step now uploads into an
existing release instead of failing on create, since tags are frequently cut by hand.

An in-archive `README.txt` with setup notes was drafted for this change but split out
(2026-08-23): packaged prose belongs to the parked standalone-docs effort and ships only after
its own review. The archive is the three pieces the manual setup needs: `JimsProxy.exe`,
`HermesProxy.config`, `CSV/`.

**Verification:** YAML structure checked (5 jobs, single `windows-latest` build job, no tabs); no
stale binary references remain. The packaged layout matches the
`JimsProxy-v5.2.0-beta.4-win-x64.zip` asset built by hand and attached to the v5.2.0-beta.4
release on 2026-08-22, whose contents were field-tested against Kronos on 2026-08-20. **Field gate
pending:** the workflow itself has not been run since the fix — the first `workflow_dispatch` will
prove it end to end. Note that `workflow_dispatch` only becomes available once this file is on the
**default branch**, so attaching assets to an already-published tag requires it to reach `master`.

---

## 2026-08-20 — Correct the shipped config defaults for standalone (non-launcher) use

**Issue:** `HermesProxy/HermesProxy.config` is what anyone building from source or running the
proxy without the launcher actually gets, and it had drifted from the client this fork targets.
`ClientBuild` shipped as `40618` (1.14.0) while development, testing, and the launcher's own
client check all pin 1.14.2 build `42597` (`repair.rs:145`, `EXPECTED_CLIENT_VERSION`) — and a
`ClientBuild` that does not match the running client fails login outright. `PacketsLog` shipped
as `true`, so every standalone session wrote a full packet capture to disk unprompted and
indefinitely; the launcher already forced this off, so only standalone users paid for it.
Separately, `ThreatEngine` and `ServerType` were the only two keys in the file with no
`Option/Description/Default` comment block, leaving a standalone user no way to learn what they
do — `ServerType` in particular silently selects the fork-specific item-data overlays loaded at
startup.

**Change:** `HermesProxy/HermesProxy.config` — `ClientBuild` `40618` → `42597`; `PacketsLog`
`true` → `false`; added full comment blocks for `ThreatEngine` and `ServerType`, documenting
`ServerType` values as `Kronos` (default) and `Generic`, with `Generic` stated as scaffolding for
future server support rather than a tested configuration. `ServerAddress` (`127.0.0.1`),
`ServerBuild` (`auto`) and `ClientSeed` deliberately unchanged — the first two are correct
generic defaults, and `ClientSeed` is the static-seed fallback users should never edit.
Data-only; no code change. The launcher writes its own config from a template (`setup.rs`) and
is unaffected.

**Verification:** XML parses clean, BOM preserved, file remains pure ASCII. **Field gate passed
2026-08-20** — a standalone run against Kronos using only the documented manual steps (take the
shipped config, edit `ServerAddress` and nothing else, start `JimsProxy.exe`, log in) reached
in-game with no errors, with no launcher involved at any step. That clears the change under
test: `ClientBuild=42597` is correct against the real 1.14.2 client and produces no version
mismatch. Test composition: 5.2.0-beta.4 exe (`f842db7d`) + `CSV/` + this config.

---

## 2026-08-20 — Unlock the shop's epic Reins of the Nightsaber (12303)

**Issue:** an undead player could not use the shop-bought **Reins of the Nightsaber (12303)**.
PR #280 unlocked 8627 — same display name, but NOT the item the shop sells — and missed 12303,
which stayed AllowableRace=1101 (Alliance-only) in both hotfix files. Wire analysis (30 beta.4
sessions) confirmed the proxy delivers this lock to every client at every login (the 42597 client
re-requests the full custom hotfix set per login), so the mount was blocked in every normal
session; the player's occasional "it worked" sessions are consistent with the hotfix failing to
apply that session and the client falling back to its more permissive native data. Audit against
the shop page's real item IDs confirmed 12303 is the ONLY sold mount still locked; the other 15
were correctly unlocked by #280 (whose delivery was verified clean on the wire, e.g. 8628 served
as -1 in 30/30 sessions).

**Change:** `CSV/Hotfix/ItemSparse1.csv` + `CSV/Hotfix/ItemSparse1.kronos.csv` — 12303
AllowableRace 1101 → -1. Data-only; no code change; no hotfix IDs shift.

**Verification:** full suite green; wire captures prove the bands this row rides are served every
login. Field gate: the reporting undead uses their Reins of the Nightsaber on a current build.

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

## 2026-07-31 — Hold the ghost-swing preempt stop until after the killing-blow ASU (#450)

**Issue:** A melee killing blow could present as a fresh swing on the corpse — floating damage text plus swing sound seconds late, after the loot window was already open (#450). Wire-proven (`modern_42597_1785515213.pkt` + `jimsproxy-20260731-092645.jsonl`): Kronos emits the killing blow's SMSG_ATTACKER_STATE_UPDATE **after** SMSG_PARTY_KILL_LOG in the same-ms burst — its norm for every melee kill (death handling runs nested inside swing processing, so announcements unwind inside-out) — and #389's preemptive SMSG_ATTACK_STOP (NowDead), emitted inline from the kill-log handler, landed between the kill and the hit; the modern client, told its attack had already stopped, re-played the late hit as a new swing. Secondary finding fixed here too: the trailing blow's damage re-created the dead mob's ThreatTracker list (THREAT_UPDATE/HIGHEST_THREAT_UPDATE for a corpse until the death values-update cleared it ~20ms later).

**Change:** The preempt stop is now **armed** by `HandlePartyKillLog` (same `TryClearSettledAttackTargetOnDeath` gate — the #321 swing-state contract is untouched) and flushed hit-first by whichever trigger fires first (`Client/CombatHandler.cs`, `WorldClient.cs`, state on `GameSessionData`): the local player's ASU on the armed victim forwards → the stop rides right behind it (`flush:"asu"`); the receive loop finds the legacy socket drained when no hit trails, e.g. spell killing blows (`flush:"drain"` — same read pass, not a timer; a mid-burst TCP segment boundary can only reproduce the old inline ordering); a second kill re-arms (`flush:"rearm"`, older stop flushed immediately); the server's own player ATTACK_STOP echo consumes the armed stop silently (no duplicate). `combat.attack_stop_preempted` keeps its name and gains the `flush` field. ThreatTracker (`ThreatTracker.cs`): `OnMobKilled` (kill-log path only) tombstones the guid and clears; `AddThreat`/`SetThreat`/`SetToTop` no-op while tombstoned (DebugOutput-gated `threat.dead_mob_add_ignored`); any lifecycle signal — combat-state observation on either edge, destroy, leave-combat wipe, reset — disarms, so a respawned mob reusing the guid is never blocked; evade/leash `ClearMob` deliberately does not tombstone.

**Verification:** 14 state tests (`AttackStopPreemptOrderingTests` + `ThreatDeadMobTombstoneTests`), tombstone red-proven. Suite 847/847 after the beta merge-in. **In-game 2026-08-12** (session `20260812-141139`, 9 melee kills): `flush:"asu"` 9/9, kill→blow gap 0–6ms, tombstone 9/9 with zero corpse-threat resurrections; same-hour stock control (`20260812-142924`, same mobs, 5.2.0-beta.2) showed the inverted presentation the fix removes (mob visibly dying before the final hit renders). Residual on both builds, out of scope here: the "damage text a beat after death" inherent to Kronos's announcement order, and `ATTACKSWING_DEADTARGET` errors 0.2–1.6s after every kill (Kronos keeps the server-side swing running because the post-#389 client no longer sends CMSG_ATTACK_STOP after kills — planned follow-up PR). Issue #450 stays open for field observation at the beta this ships in.

## 2026-08-02 — Resurrection sickness keeps its debuff timer (same-tick aura slot swap race)

**Issue:** Res sickness at a spirit healer showed no countdown on the debuff for the whole session. Wire-proven in `jimsproxy-20260802-182258.jsonl`: vanilla cores send SMSG_UPDATE_AURA_DURATION immediately at aura apply but batch the field update to end of tick, so on a spirit-healer res the sickness timer arrives ~8 ms BEFORE the UPDATE_OBJECT that swaps the player's slot 32 directly from Ghost (8326) to Res Sickness (15007) — no empty pass, deterministic every res. The UpdateHandler swap-wipe guard (added for the Rupture Y→X stale-duration bleed in #435's squash `8cdcb544`, 2026-07-27 — making this a v5.2.0-beta.1 regression; 5.1.9 stable unaffected) saw prev-emitted Ghost ≠ 15007 and cleared the freshly stored server-authoritative duration; every fallback then missed (sentinel `-1`, no AuraDurations CSV row for 15007, expiry restore is create-only) and the aura went out without the Duration flag (`aura.slot.set` `duration_full:0, duration_left:-1`, still 0/-1 on the re-emit 16 s later).

**Change:** `GameSessionData` gains `UnitAuraDurationPushTime` — per (unit, slot) TickCount written ONLY by the two server duration-push handlers (`HandleUpdateAuraDuration`, `HandleSetExtraAuraInfo` in Client/SpellHandler.cs), never by emit-path stores (finisher snapshot, expiry restore), plus `HasFreshAuraDurationPush` (1 s window, wraparound-safe). The swap guard in Client/UpdateHandler.cs keeps the stored duration when a push for that exact slot is fresh (emits ungated `aura.duration.preserved_on_swap`), and still wipes otherwise — stale-Y durations are always older than the window at swap time, so the Rupture fix is preserved (pinned by test red-proof against the tempting wrong implementation that reads the shared emit-path timestamp dict). `ClearAuraDuration`/`EvictUnitAuraState` drop the push entry with the rest. Known accepted edge: a self-aura duration refresh followed within 1 s by a direct same-slot replacement would briefly show the old spell's duration on the new one — requires refresh+replace across adjacent server ticks, self-corrects on the next update. Noted, unfixed here: `HandleUpdateAuraDuration` still attaches a racing push to the *cached* (stale) slot occupant and records its expiry under that old spell — cosmetic 8 ms blip, overwritten by the swap emit.

**Verification:** 9 state-layer tests (`AuraDurationSwapPreserveTests`) incl. the logged 8 ms sequence end-to-end at state level and the emit-path-stores-don't-arm pin; red-proof pass confirmed each implementation line is load-bearing (wrong-dict, missing clears → 4 red). Suite 790/790. Field gate: die, res at a spirit healer, expect `aura.duration.preserved_on_swap` (prev 8326 → 15007) and a ticking countdown on the debuff.

## 2026-08-03 — Carried-root cure + root-ceremony breadcrumb (the BG-exit movement lockup)

**Issue:** After leaving a battleground — reported specifically by players who spam-click "Leave Battleground" the instant it appears — the player arrives able to turn, mount and cast but unable to move or jump, permanently, cured only by `/reload` or relog (#328 family). Root cause chain, established over rounds of RE + live harness runs: every Kronos arrival force-roots the player (ROOT ×2, unroot ~1s later — wire-verified 18/18 arrivals); an instant leave departs while the BG-end root is being removed, the server's unroot fires in the between-maps window and is silently discarded (cmangos `Unit.cpp:751` `!IsInWorld()` — deterministic), and the client arrives still force-rooted while the server considers it mobile. Harness-proven equivalence: swallowing exactly one unroot reproduces the reported symptom table including the `/reload` cure.

**Change:** `World/Client/WorldEntryCeremony.cs` (pure logic + tests, TransportClearGate precedent). **(a) The cure:** `GameSessionData.ClientBelievesRooted` tracks what the client was last told about its root state (set on self force/spline root forwarded, cleared on either-family unroot — live-verified the client honors both); on crossing a loading boundary still rooted, the proxy synthesizes the missing force-unroot: cross-map at the player's first destination update after NEW_WORLD (delivered end-of-UPDATE_OBJECT, the stuck-stun pattern, so it can never race the create), same-map (hearth/tele/portal) at the client's teleport-ack. Sentinel counter `0xFFFFFF02`; the client's ack is swallowed (`worldentry.carried_root.cure_acked`) so the legacy server never sees an ack for an op it didn't send. Gate is belief-ONLY — deliberately not gated on destination movement flags, which are an echo of the client's own reported (stuck) state and provably veto the cure forever; a legitimately rooted cross-map arrival is re-rooted by the server's own arrival ceremony. Config kill switch `WorldEntryCarriedRootCure` (default true; cure synth only — the breadcrumb stays active regardless). **(b) Always-on breadcrumb:** per-arrival ceremony accounting (root/unroot forwards + acks + spline-family legs + mover-init) flushed at the next boundary (next login-verify / transfer / legacy disconnect); an unclosed ceremony logs ONE `worldentry.ceremony.unclosed` line so any field Export Diagnostics carries the discriminator (never-sent vs client-swallowed vs wrong-family) with DebugOutput off.

**Verification:** Suite 791/791 (10 new: unclosed/anomalous truth tables anchored to the healthy 18/18 shape and the stuck-stun golden capture's missing-ack fingerprint, belief-only gate, sentinel disjointness). Live end-to-end on PTR (2026-08-03): manufactured strand at login (harness swallowed one unroot; player fully movement-locked, `/reload`-curable — the red case), then with the cure: hearth and mage teleport each arrived mobile with no `/reload`, wire showing `carried_root.armed` → `carried_root_cured` → the client ACKING the synth unroot. Field gate: BG spam-click reports should cease; any residual lockup now arrives with the breadcrumb naming its failure mode.

## 2026-08-08 — Merge login-time server evictions into one clean load (instance-reset trick stuck-loading)

**Issue:** Logging in while instance-reset-capped after the Maraudon reset trick (hand lead to an alt, /camp inside, alt kicks the offline main) sticks the first login on the loading screen forever (force-close required). Wire-proven (DebugOutput captures 132646 L4, 181335 L2; "Bad Logins 4" corpus 7/7): the login lands at the stale inside-instance position (`SMSG_LOGIN_VERIFY_WORLD`, e.g. map 36/349) and 8–19ms later the server evicts to the continent (`SMSG_TRANSFER_PENDING` + `SMSG_NEW_WORLD` → map 0/1) because the capped character can't get a new instance copy. The 1.14 client fully processes the transfer (WORLD_PORT_RESPONSE +431ms, INIT_ACTIVE_MOVER_COMPLETE +688ms, server streams live) but the aborted login-load's loading screen never dismisses (nested loading-screen enable never unwound — Mirasu R48 §2(b)). The healthy post-force-close relogin — a single clean login straight to the continent — proves the merged shape works.

**Change:** Hold-and-merge for instanced-map logins (`World/Client/LoginEvictionHold.cs`, state on `GameSessionData`, wiring in Client `CharacterHandler`/`MovementHandler`/`UpdateHandler`/`WorldClient`). When LOGIN_VERIFY_WORLD names MapID > 1, the client-facing world stream is held in arrival order (proxy-side processing unchanged; realm traffic passes). Release is strictly packet-driven — no timers:
- TRANSFER_PENDING arrives → swallowed client-side; the NEW_WORLD that follows is **merged**: the held login-verify is rewritten to the transfer's destination (map/pos/facing from the payload — different dungeons evict to map 0 or 1; never hardcoded), the held `WorldServerInfo` difficulty fields are rewritten to match, the queue flushes, TRANSFER_PENDING/SuspendToken/NEW_WORLD/ResumeToken are never sent, and the proxy synthesizes the `MSG_MOVE_WORLDPORT_ACK` the client will never produce. `IsFirstEnterWorld` stays true (the SMSG_INITIALIZE_FACTIONS TimeSyncRequest synth and LOGIN_SET_TIME_SPEED must still run) and the deferred transport synth stays in Login mode.
- First SMSG_UPDATE_OBJECT arrives instead → healthy login, queue flushes unmodified (cost = the create-arrival delay, ~4–200ms observed, instanced logins only).
- TRANSFER_ABORTED while holding → swallowed (client never saw the transfer), hold drops back to plain holding.
- Legacy disconnect → fail-open flush in `WorldClient.Disconnect` (direct instance-socket sends; teardown threads must not enter the wait-for-socket loop).
Config kill switch `LoginEvictionMerge` (default true). Events: `login.eviction_merge.merged` / `login.eviction_hold.transfer_pending` / `.transfer_aborted` / `.flushed_fail_open` always-on (fire only on the bug/anomaly); `.started` / `.released_healthy` DebugOutput-gated (fire per instanced login). Deliberate divergence from the client-acked path: `PendingPostTeleportRunSpeedReassert` is not armed (the client does a full login load; speeds come from the create). The downstream stunned/no-turn state on the SECOND login (construction-deep client latch) is a separate fix, pending client-RE confirmation — this change cures the first-login hang, which is also the gateway to that state.

**Verification:** 20 state-machine tests (`LoginEvictionHoldTests`): arming gate (instanced/continent/reconnect/kill-switch), arrival-order release, transfer-wins-over-update rule, merge rewrite both directions (continent + instanced destinations), bare-NEW_WORLD defensive merge, abort recovery, fail-open from both armed phases, stray-registration edge. Red-proven: neutering the merge rewrite fails exactly the 3 rewrite tests. Full suite 801/801. Field gate before tag: capped Deadmines/Mara reset repro (James's 2-account variant) with DebugOutput+PacketsLog — expect `login.eviction_merge.merged` on the previously-stuck login, loading screen dismisses, `loading_screen_notify(showing=false)` present, and the follow-up login needing no force-close. **Field-verified 2026-08-09** (session 20260809-002143: merged event, dismissal +568ms, landed outside, no force-close; healthy control unchanged).

## 2026-08-09 — The wedge-login "stunned" lock and invisible world: stale CurrentMapId poisons every UPDATE_OBJECT's map header (instance-reset stun, step 2 — THE root cause)

**Issue:** The second half of the capped instance-reset bad login: the player lands outside but **cannot turn or cast** ("You can't do that while you are stunned") while walk/strafe work; `/reload` doesn't fix it, relog does. The same logins also historically showed the **invisible-mobs / partial-world** symptom (reporter's original complaint). Root cause — a pre-existing upstream line of our own, exposed by any login whose landing map differs from the map the client *opened its loading screen with*: `Server/PacketHandlers/CharacterHandler.HandleLoadScreen` trusted the client's `CMSG_LOADING_SCREEN_NOTIFY` MapID unconditionally and wrote it into `GameState.CurrentMapId`. The notify **echoes the char-list map** (e.g. 36, where the player camped inside the instance), not where the server actually put them (map 0 after the eviction/merge). And `UpdateObject.Write()` (`UpdatePackets.cs`) stamps `CurrentMapId` into the **MapID header of every `SMSG_UPDATE_OBJECT`** — which the 1.14 client validates against its own map, **silently discarding mismatched packets**. So from the loading screen's dismissal onward the client received no object update at all: the server's stun-clear (+1.2s) never applied (turn/cast stay locked — the client rationally still believes it is stunned), and no new creates applied (invisible world). Walk/strafe kept working because unroots ride `SMSG_MOVE_UNROOT` force-ops, which carry no map header. `/reload` can't fix proxy-side corruption; relog sends a fresh, correct notify. Diagnosed via a build-interleaved field experiment (5/5 clean with the fix vs 0/4 without, same evening, same repro) after the client-side "construction latch" and "op ordering" hypotheses were falsified in vivo; the smoking-gun capture shows `showing=false, map_id=36` arriving while the client stands on map 0, with `CurrentMapId` corrupted from that instant.

**Change:** `Server/PacketHandlers/CharacterHandler.HandleLoadScreen` — only accept the client's notify map when the proxy has nothing better (`CurrentMapId == null`); server-derived sources (`LOGIN_VERIFY_WORLD` / `NEW_WORLD` / `INIT_WORLD_STATES`) are authoritative and always follow. One-line guard; the client's word is now only used to seed an otherwise-unknown map at session start.

**Verification:** field-proven the night of 2026-08-09 across an interleaved build matrix (all runs the same capped-reset wedge, two characters): builds without the guard locked every time (18:43, 22:11, 23:14); builds with it were clean five out of five (21:05, 21:13, 21:14, 22:15 ×2) — including back-to-back slot-switch comparisons minutes apart. Mechanism confirmed three ways: the map-header write site in `UpdateObject.Write()`, the captured stale-notify write in the locked runs, and probe packets built from the corrupted value targeting map 36 while the player stood on map 0. Field gate before tag: capped wedges show turn+cast immediately with full mob visibility; healthy logins unchanged.

## 2026-08-09 — Hold pre-create self control ops until the first self create block (login hardening)

**Issue:** On the wedge login the self-create is server-stalled ~600–800ms, so the arrival control ops (`SMSG_MOVE_ROOT` + `SMSG_CONTROL_UPDATE`, then root/ctrl/unroot) reach the client **before the player object exists** — the only entry shape in the corpus with ops-before-create (healthy logins and transfers deliver creates first, 12/12). Those early ops went permanently unacked by the client (documented R49 §4 dead-zone behavior). Originally built as the suspected stun-lock cure; the field falsified that (the lock was the map-header bug above), but the hold measurably fixes the protocol pathology: with it, the client acks **every** force-op leg (previously 2 roots → 1 ack; now 4/4 legs acked at ~+377ms).

**Change:** `World/Client/PreCreateOpHold.cs` (state machine, state on `GameSessionData`). From login-verify until the first self create block forwards, self-addressed `MOVE_ROOT`/`MOVE_UNROOT`/`CONTROL_UPDATE` (+ its paired walk-fix speed reassert) / server `MOVE_TELEPORT` are held in arrival order (capture sites = their Client/MovementHandler translation handlers; our own transport-clear synth is deliberately not held). Release is packet-driven, no timers: the self create's `UPDATE_OBJECT` and aura updates forward → held ops flush in order behind them (marked in `DetectStuckLogoutStunAtSelfCreate`, flushed at `HandleUpdateObject`'s tail); login failed → discard; legacy disconnect → fail-open flush. General per-login rule (zero-cost on healthy logins — nothing is ever held); never arms on seamless-reconnect verifies. Kill switch `LoginPreCreateOpHold` (default true). Events: `login.precreate_op_hold.released` (always-on, fires only when ops were actually held), `.flushed_fail_open` / `.discarded_login_failed`.

**Verification:** 13 state-machine tests (`PreCreateOpHoldTests`), red-proven (neutering the create-gating fails exactly the release-requires-create test). Full suite 814/814. Field: wedge logins show the release event with all legs subsequently acked; healthy logins show no event and are unchanged. Shipped as part of the tested-together configuration that went 5/5 in the field.

## 2026-08-11 — Stop printing every outbound chat body to the debug console

**Issue:** `SendMessageChatVanilla` logged the whole outbound message body on every chat the player sent: `Log.Print(LogType.Debug, "RAW CHAT INTERCEPTED: " + msg)`. Two costs. **Privacy** — every caller routes through it, whisper bodies included (`Server/PacketHandlers/ChatHandler.cs:146`), so any session with `DebugOutput=true` streamed the player's private messages to the console; that is the developer's permanent setting and the one we ask reporters to turn on. **Corrupted output** — addon-language bodies are DEFLATE binary, decoded 1:1 as Latin1 since `c63d6150` (#361) so all 256 byte values survive; embedded `0x0A` split the line into fragments that carried no `HH:MM:SS | Debug |` prefix, and the C0 controls rendered in the launcher's Debug Console as unlabelled memory-dump gibberish. The line was never instrumentation anyone wanted: `f667118` ("Fix item linking in chat", 2026-04-20) added it as a bare `Console.WriteLine` while developing the item-link regex directly beneath it, and `451a9c68` demoted it to Debug a week later because it was drowning out warnings in diagnostic bundles. `Log.Print` output never reaches the JSONL, so it could not be used for post-hoc diagnosis either — and the WotLK twin `SendMessageChatWotLK` has never had the line.

**Change:** Removed the `Log.Print` (`World/Client/PacketHandlers/ChatHandler.cs`), leaving a comment at the site so it isn't reintroduced. No replacement trace: the outbound-chat concerns we actually debug each already emit a targeted `Log.Event` at their own translation point (`chat.item_link.translated` for the very fix this line was scaffolding for, plus the HealComm, PallyPower interop, KTM threat and JP sideband events), and a generic per-message event would flood the JSONL with addon heartbeats — the exact noise `451a9c68` was fighting. The Latin1 decode is load-bearing for addon comms and is untouched. A sweep of every `Log.Print` / `Log.PrintNet` in `HermesProxy/` and `Framework/` confirms this was the only message body reaching the console; the rest log identity or metadata (account name at auth, `char@realm` on AccountData errors, realm names, CSV-vs-server item-name mismatches) and are unchanged. Left deliberately alone for now: three ungated `Log.Event` calls that put **addon** payload bodies in the JSONL — `addon.whisper_inform.dropped` (`Client/PacketHandlers/ChatHandler.cs:314`) and `AddonInteropTranslator.cs:278`/`:336` — which are named diagnostics attached to the live PallyPower work, though they are per-message and the JSONL is what Export Diagnostics ships.

**Verification:** Build clean, suite 781/781. Console output is the entire behavioural surface, so there is nothing else to drive. The launcher already handles the cosmetic half independently (continuation fragments inherit their parent message's suppression verdict, and lines containing C0/DEL characters are dropped), so multi-line proxy output that matters — stack traces — still displays in full.

## 2026-08-12 — Next-melee / auto-repeat slot wedge on suppressed transient failure (Raptor Strike stops working)

**Issue:** With `SuppressSpellCastErrors` on, a transient CAST_FAILED (NotReady/SpellInProgress) for a next-melee or auto-repeat spell was swallowed by the suppression early-return in `HandleCastFailed` without clearing `CurrentClientNextMeleeCast`/`CurrentClientAutoRepeatCast` — those slots live outside `PendingNormalCasts`, so the dequeue there misses them. The occupied slot then makes `HandleCastSpell` locally reject every later press (silent, never forwarded) until relog. Trigger is routine: the 1.14 client's spell-queue window sends the press ~0.3s before the vanilla cooldown ends, Kronos bounces NotReady, wedge. Evidence: jimsproxy-20260812-180528.jsonl — `cast.error_suppressed reason_id=60` at t=741.8s, then ~100 Raptor Strike presses with `cast.received` and nothing else through log end; identical single-suppression→107-dead-presses signature in jimsproxy-20260811-185746.jsonl (that day's NOTINRANGE/BADFACING spam was the auto-attack swing, not the strike — presses never left the proxy).

**Change:** `World/Client/PacketHandlers/SpellHandler.cs` (`HandleCastFailed`): the suppression early-return now excludes failures matching either special slot (mirrors the exclusion the transient-stale-drop branch above it already had) so they fall through to the special-cast branch, which resolves and clears the slot exactly as it always did with suppression off; that branch sends `DontReport` instead of the converted reason when suppressing, so the red error text stays hidden while the button state clears. New event `cast.special_slot_failure_resolved` (spell_id, reason_id, slot, suppressed, retry_scheduled) — DebugOutput-gated (gated at merge per the diagnostics policy: it fires on every special-slot failure including each Auto Shot out-of-range bounce). Blast radius: only spells that can occupy the slots — on-next-swing attacks (Heroic Strike, Raptor Strike, Cleave, Maul + NPC variants in MeleeSpells1.csv) and auto-repeat (Auto Shot, wand Shoot) — and only under suppression+transient; suppression off is bit-for-bit unchanged.

**Verification:** Build clean, suite 847/847 on this branch. Field-tested 2026-08-12 on the stacked build (Raptor Strike no longer wedges through repeated cooldown-window presses). Field gate: spam Raptor Strike through its 6s cooldown on a target that stays alive — pre-fix this wedges within a few fights; post-fix expect `cast.special_slot_failure_resolved slot=next_melee suppressed=true` at each early-press bounce (DebugOutput on) and the next press after cooldown fires normally.

## 2026-08-13 — Clear the auto-attack handshake on an empty-victim ATTACK_STOP

**Issue:** Charging a mob that was evading/leashing back from another fight locked the player out of fighting it — zero melee swings for the rest of the session, and (via rage starvation) abilities refused client-side, reading as a broad combat lockout; movement unaffected (reported 2026-08-12, warrior vs a wolf returning from killing a critter). Wire-proven (`modern_42597_1786595484.pkt`): Kronos answers the Charge with `SMSG_ATTACKSTOP(player, EMPTY)` instead of `ATTACK_START` when it refuses the engage; `HandleAttackStop` treated any non-matching victim as a target-switch and preserved the handshake, so `CurrentAttackTarget` stayed pinned and the swing de-dupe guard ate every later `CMSG_ATTACK_SWING` at that mob. 2-of-8 Charges in the capture hit it (both against mobs fresh from another fight); the empty-victim sibling of `8c2c34e6`, which fixed the named-victim form untested.

**Change:** `GameSessionData` gains two pure operations — `TryBeginLocalPlayerAttackSwing` (the de-dupe guard + handshake set, from Server/CombatHandler) and `ApplyLocalPlayerAttackStop` (the four-branch stop bookkeeping, from Client/CombatHandler, returning a `PlayerAttackStopOutcome` so the socket layer only owns forwarding a deferred stop). One behavioral clause added: an **empty** stop victim counts as a rejection (it cannot be a stale stop for the old target of a switch — it means "you have no attack target at all") and clears `CurrentAttackTarget` + `WaitingForAttackStart`. Over-clearing is the safe direction: worst case is one redundant `CMSG_ATTACK_SWING` forwarded — what the client asked for; recovery is inherent since a cleared target always passes the de-dupe guard.

**Verification:** 7 tests (`AttackStopEmptyVictimWedgeTests`) pinning all four stop branches + both swing branches — the truth table `8c2c34e6` never had; the wedge test replays the captured packet sequence verbatim. Red-green-red: reverting only the empty-victim clause fails exactly that test with exactly the fall-through outcome. Suite 854/854. In-game gate on the re-cut beta.3 build: charge a mob returning from another fight — auto-attack must engage (or recover) normally; before the fix, 2-for-2 wedges.

## 2026-08-13 — Chronoboon chat links: silently dropped by Kronos V / dead for receivers

**Issue:** Shift-clicking a Chronoboon Displacer into chat produced a message nobody ever saw, or a dead link. Three stacked failures. (1) After first use the boon is presented to the 1.14 client under a throwaway alias entry (the dynamic-tooltip mechanism from #385), so the outbound link carried an entry the legacy server has never heard of. (2) With the id fixed, live Kronos V still silently dropped the whole message while PTR accepted a byte-identical link — live validates the link's `[name]` against item 25007's static proto name ("Chronoboon Displacer") and our link carried the dynamic charged name ("Supercharged Chronoboon Displacer XL"). (3) With the name fixed, the echoed 25007 link rendered dead on our own client, which never re-queries 25007 (it renders from incomplete login-pushed data). Logs: jimsproxy-20260810-135900.jsonl (`chat.item_link.translated item_id=135138` outbound), PTR-vs-live captures 150112 (identical outbound, PTR round-trips, live drops), 151035 (base name accepted live).

**Change:** `World/Client/PacketHandlers/ChatHandler.cs`, three-part translation. c2s: any alias-band item id in an outbound link is rewritten to the real entry 25007 (`GameData.IsItemEntryAlias` range check, so an alias evicted by a mid-session re-mint still resolves), and any boon link's `[name]` is forced to the static base name (new `GameData.KronosChronoboonBaseName`) so it passes the server's validation. s2c: our OWN echoed 25007 link (gated on sender == current player) is rewritten to `GameData.CurrentChronoboonAlias` — the newest alias the client has already fully resolved, set at mint in `QueryHandler.cs` — with the state-correct filled/empty display name taken from the alias template. Incoming boon links from other players are deliberately untouched: they encode the sender's stored buffs in the link fields and resolve to the sender's boon (rewriting them to our alias showed the wrong boon — regression caught in log 152722 and fixed with the self-message gate). `CurrentChronoboonAlias` is reset at each character login (`Server/PacketHandlers/CharacterHandler.cs`) so a boon-less character never inherits another character's alias. Diagnostics (`chat.item_link.alias_resolved` / `.chronoboon_to_alias` / `.chronoboon_outbound`) are DebugOutput-gated, and the outbound event records the rewritten LINK substring only, never the message body — both applied at merge per the diagnostics + #463 privacy policies.

**Verification:** Confirmed live on Kronos V 2026-08-10 (log 151035): boon link accepted and rebroadcast, echo rewritten 25007→alias, rendered with the charged name and full buff tooltip. Cross-character stale-alias regression re-tested after the self-message gate. Build clean, suite 847/847 on this branch. Known limitation (accepted): our 1.14 client's outbound link reads as an empty boon to OTHER players — the buff-encoding fields are stripped by the alias round-trip; native 1.12 senders are unaffected.

## 2026-08-14 — Restore the speculatively-removed predecessor rank on confirmed learn (Shadowguard lower ranks "not learned")

**Issue:** After training a new rank of a downrankable spell at a trainer, every lower rank of that spell was locally rejected with "ability is not learned" for the rest of the session (relog cures) — reported on the troll priest racial Shadowguard, which works normally on a native 1.12 client. Cause: the Kronos-IsInWorld-race ban defense (`Server/PacketHandlers/NPCHandler.HandleTrainerBuySpell`) speculatively removes the predecessor rank from `CurrentPlayerKnownSpells` on every trainer buy, and `HandleLearnedSpell` deliberately left it removed on a confirmed learn. That matched Kronos's behavior for genuine supersede chains (Stealth, profession tiers — the one SMSG_SUPERCEDED_SPELLS in the log corpus is Journeyman First Aid) but not for downrankable chains: Kronos keeps lower ranks known server-side (proven by native 1.12 downranking working), so the cast-block-unknown-spells guard was false-positive blocking legal casts. `SpellRankChain.csv` covers every 1.12 name+rank chain, so all downrankable class spells were affected, not just racials; the racial surfaced it because downranking Shadowguard (cheaper refresh) is routine.

**Change:** The trainer-buy predecessor bookkeeping is extracted into four pure operations on `GameSessionData` (the attack-stop pattern): `ApplyTrainerBuyPredecessorRemoval` (buy, from Server/NPCHandler), `ApplyLearnedSpellKnownState` (learn, from Client/SpellHandler — the behavioral change: a confirmed learn for the pending buy now restores the removed predecessor, new always-on event `spell.trainer_buy.predecessor_restored_on_learn`), `ApplySupercededSpellKnownState` (supersede, unchanged semantics: remove + confirm-without-restore), `ApplyTrainerBuyFailedKnownState` (explicit failure, unchanged semantics incl. the non-matching-failure clear-without-restore fail-safe). All supersede orderings stay ban-safe: SUPERCEDED-first clears the pending state so the learn never restores; LEARNED-first restores transiently and the following SUPERCEDED's unconditional remove wins; the Twizzy no-response race (the autoban the defense exists for) confirms nothing, so the removal stands and the cast guard keeps blocking.

**Verification:** 11 truth-table tests (`TrainerBuyPredecessorRestoreTests`) covering every ordering: downrank restore, both supersede arrival orders, the no-response ban case, both FAILED id forms (real + learn-wrapper), non-matching FAILED fail-safe, unrelated learn/supersede mid-window, unknown-predecessor buy, single-slot pending re-buy. Double red-proof: neutering the restore fails exactly the 3 restore tests; neutering the supersede pending-confirm fails exactly the ban-critical SUPERCEDED-then-LEARN test. Suite 865/865. Live-verified 2026-08-14 both directions on Kronos: pre-fix build repro'd the lockout (`spell.cast.blocked_unknown_spell`), post-fix build restored (`spell.trainer_buy.predecessor_restored_on_learn`, lower rank casts). Remaining field check: train a genuine supersede tier (First Aid book/Stealth-style) and confirm the old tier stays gone.

## 2026-08-19 — Observed-caster CastID pairing: predecessor's echo can no longer kill the successor's bar; killed-then-fired GOes recover their CastID (#484, #485)

**Issue:** Two defects in the observed (non-local, non-pet) caster cast tracker, both surfaced by a one-line field report ("certain player castbars are cancelling/interrupting as soon as they start") and quantified against the 15-session / 1.31 GB Mirasu corpus (2026-08-04→16). (1) #484: `OtherCasterActiveCastIds` was a single slot per (caster, spell); a rapid same-spell recast overwrote the predecessor's CastID, so the predecessor's late cancel broadcast — Kronos delivers it 0–554 ms AFTER the superseding SPELL_START (heal-snipe / chain-cast spam) — popped the SUCCESSOR's ID: the terminator was stamped with the new cast's identity and the synthesized SMSG_SPELL_INTERRUPT_LOG + SMSG_CANCEL_SPELL_VISUAL dismissed the new bar 0–1 ms after it appeared (9 corpus instances: Healing Touch 10181, Shadow Bolt 11660, Greater Heal 9875). The local-player path already fixed this exact shape with a per-spell FIFO (`_playerForwardedStartCastIds`); the observed path never got it. #471's live-cast dedup bypass makes the mis-forward MORE likely in exactly this window, so the FIFO is its missing prerequisite. (2) #485: Kronos broadcasts SMSG_SPELL_FAILED_OTHER for casts it then COMPLETES (killed-then-fired: 536 of 16,704 observed-player cast-time casts, 18 casters — one Frostbolt-spamming mage alone 439); the terminator popped the tracked entry, so the following SPELL_GO minted a fresh CastID the client never saw start.

**Change:** `GlobalSessionData.cs`: the single-slot map becomes a short per-(caster, spell) FIFO (`_observedLiveCastIds`) with packet-paired rules derived from the server running at most ONE live cast per unit — a terminator pairs with the OLDEST tracked entry (`TryPairObservedTerminatorCastId`, reporting `pairedLiveCast=false` when it consumed a superseded predecessor), a GO pairs with the NEWEST (`TryPairObservedGoCastId`) and purges anything older (the predecessor's echo window provably closes at the successor's GO, so no zombie can outlive one cast cycle); a START keeps only the direct predecessor alongside the new cast. The ID a terminator consumed is stashed (`_observedTerminatedCastIds`, invalidated by any same-key START or GO) and `TryRecoverTerminatedObservedCastId` lets a killed-then-fired GO re-use it, so START/terminator/GO reference one cast. `World/Client/PacketHandlers/SpellHandler.cs`: both terminator handlers (`HandleSpellFailedOther`, `HandleSpellFailure`) gate the interrupt-kit synthesis on `pairedLiveCast` — those packets are caster-addressed (no cast identity on the wire), so when the terminator consumed a predecessor's entry they would have dismissed the successor's on-screen bar; the FAILED_OTHER itself still forwards with the predecessor's CastID for the combat log. New always-on field `pairedLiveCast` on `spell.failed_other.routed` / `spell.failure.routed` (corpus sweepability, #477 precedent); new DebugOutput-gated `cast.observed_go_after_terminator` marks each killed-then-fired recovery. `ResetInFlightCastState` clears both structures. Single-live-cast killed-then-fired (the mage's 439) is NOT fixable proxy-side without holding packets — the bar still flickers; #485 stays open for the Kronos-side question. Blast radius: observed casters only; local player, local pet, and the fallback deterministic-seed path are bit-for-bit unchanged; with no recast-overlap the FIFO degenerates to the old single-slot behavior exactly.

**Verification:** 11 new tests (`ObservedCastIdPairingTests`) driving the pairing methods directly: the #484 defect shape (echo consumes predecessor, not live cast; GO still pairs the live cast), legit single-cast interrupt unchanged, double-echo residual pins today's outcome, GO purge, killed-then-fired recovery + stash invalidation by START and by GO, zombie hygiene on third START, key independence, reset. Existing dedup suite adapted to the new API with intent unchanged (8/8). Double red-proof: reverting the terminator to newest-pop single-slot semantics fails exactly the 3 pairing tests; neutering the stash recovery fails exactly the killed-then-fired test. Suite 946/946. Field gate: in the next corpus, `spell.failed_other.routed pairedLiveCast=false` should appear on heal-snipe clusters with the successor's bar surviving, and killed-then-fired becomes directly sweepable as terminator castIdCounter == following GO castIdCounter.

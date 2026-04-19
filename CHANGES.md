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

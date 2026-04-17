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

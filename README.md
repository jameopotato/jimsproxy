# JimsProxy

A maintained fork of [HermesProxy](https://github.com/WowLegacyCore/HermesProxy) — a protocol translation proxy that lets WoW Classic 1.14.2 clients connect to vanilla 1.12.1 servers. Primary target: [Kronos](https://www.kronos-wow.com/).

The upstream HermesProxy project was [archived in November 2024](https://github.com/WowLegacyCore/HermesProxy). JimsProxy is rebased onto [Xian55's fork](https://github.com/Xian55/HermesProxy) (April 2026) for 18 months of community fixes, then adds Kronos-specific translation fixes and diagnostic tooling on top.

**License:** GPL v3 (inherited from upstream — see `LICENSE`)

## Installation

Three routes, all on Windows. Linux and macOS are covered in the manual guide's appendix (community-supported, built from source). The 1.14.2 (build 42597) game client is not included in any route.

### Classic WoW Launcher

The **Classic WoW Launcher** ([jimothy.cc/install](https://jimothy.cc/install)) is a Windows desktop application: proxy installation and updates (stable or beta), realm selection, addon management and profiles, keybinding and macro import, optional auto-login, multibox support, and proxy feature control (Cast Pipeline, 41-yard nameplates, Threat Engine, iMorph compatibility).

### Quick-start bundle (no launcher)

One zip, one double-click. The bundle's installer locates the 1.14.2 client, installs the current proxy next to it, configures the proxy and the client, and creates a Play command; running it again offers update, reconfigure, and uninstall. Guide: [docs/QUICK-INSTALL.md](docs/QUICK-INSTALL.md).

### Manual install

[docs/MANUAL-INSTALL.md](docs/MANUAL-INSTALL.md) covers every step by hand with the exact commands: download the current archive ([stable](https://jimothy.cc/proxy/stable/latest) or [beta](https://jimothy.cc/proxy/beta/latest)), add the configuration file, set the server address, and run the proxy with `scripts/play.bat` or manually.

## What This Fork Adds

- **Kronos protocol compatibility** — login, realm switching, character handling, auction house, chat links, GM tickets, transports and flight paths translated for Twinstar's MaNGOS fork (`ServerType`)
- **Cast pipeline** — spell queue with an adjustable window, latency-adaptive GCD release, off-GCD handling, optional low-latency mode
- **Stuck-state and disconnect fixes** — looping cast animations, lit action buttons, auto-attack and Auto Shot recovery, movement lockups after teleports and battleground exits, auto-reconnect after unplanned disconnects
- **Aura and timer accuracy** — vanilla duration data, combo-point scaling, other units' remaining buff time, swing-timer correctness
- **Threat engine** — synthesized threat for threat-meter addons (opt-in)
- **1.12 visual parity** — NPC, pet and player scale from vanilla data, animations, emotes, tooltips and Kronos item-data corrections
- **Addon interoperability** — the bundled JimsPlus addon; PallyPower and HealComm bridges between 1.12 and 1.14 players; intact compressed addon communication
- **Diagnostics** — structured JSONL session logs, bug reports from the launcher, per-opcode latency metrics, a kill switch for every shipped fix

See [CHANGES.md](CHANGES.md) for the full changelog.

## Supported Versions

### Modern Client (what you play with)

| Version | Expansion   | Build Range   |
|---------|-------------|---------------|
| 1.14.0  | Classic Era | 39802 - 40618 |
| 1.14.1  | Classic Era | 40487 - 42032 |
| 1.14.2  | Classic Era | 41858 - 42597 |

### Legacy Server (what emulators run)

| Version | Expansion | Build | Server Software        |
|---------|-----------|-------|------------------------|
| 1.12.1  | Vanilla   | 5875  | CMaNGOS, VMaNGOS, etc. |
| 1.12.2  | Vanilla   | 6005  | CMaNGOS, VMaNGOS, etc. |
| 1.12.3  | Vanilla   | 6141  | CMaNGOS, VMaNGOS, etc. |

Development and testing target **1.14.2 build 42597**. The other builds are inherited from upstream and are not regularly exercised.

## Configuration

The proxy reads `HermesProxy.config` (XML) from the folder containing its executable. The launcher manages this file for launcher installs; manual installs edit it by hand, and usually only `ServerAddress`. Every key the proxy reads, with its default and whether you should touch it, is documented in [docs/configuration.md](docs/configuration.md).

CLI arguments override config values for one run:

```bash
JimsProxy --config MyServer.config
JimsProxy --set ServerAddress=logon.example.com --set ServerPort=3724
JimsProxy --no-version-check
```

The full flag list is in [docs/MANUAL-INSTALL.md](docs/MANUAL-INSTALL.md#command-line-flags).

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (`winget install --id Microsoft.DotNet.SDK.10`).

> **Note:** The project directory is still named `HermesProxy/` for upstream compatibility. The output binary is `JimsProxy.exe`.

```bash
# Clone
git clone https://github.com/jameopotato/jimsproxy.git
cd jimsproxy

# Build
dotnet build HermesProxy

# Run tests
dotnet test

# Publish (self-contained single-file exe + CSV data)
dotnet publish HermesProxy --configuration Release --use-current-runtime -p:UsePublishBuildSettings=true -o build/
```

Output: `build/JimsProxy.exe` (or `build/JimsProxy` on Linux and macOS) + `build/CSV/` + `build/HermesProxy.config` — the same layout as a manual install ([docs/MANUAL-INSTALL.md](docs/MANUAL-INSTALL.md)), config included. The build is self-contained; no .NET runtime is needed where it runs.

To test a build with the launcher, either copy it over the bundled proxy in your game's `Hermes/` directory, or add `build/JimsProxy.exe` as a custom slot under **Settings → Proxy Binary** and switch to it:

```
copy build\JimsProxy.exe <game_dir>\Hermes\JimsProxy.exe
xcopy /E /Y build\CSV <game_dir>\Hermes\CSV\
```

.NET 6 will not work — the target framework is `net10.0` (set centrally in `Directory.Packages.props`).

## Acknowledgements

- [CypherCore](https://github.com/CypherCore/CypherCore) and [BotFarm](https://github.com/jackpoz/BotFarm) — foundational code
- [Modox](https://github.com/mdx7) — reverse engineering work on Classic clients
- [Xian55/HermesProxy](https://github.com/Xian55/HermesProxy) — maintained fork we rebased onto (April 2026)
- [WowLegacyCore/HermesProxy](https://github.com/WowLegacyCore/HermesProxy) — original upstream (archived November 2024)
- JimsProxy contributors: [Mirasu](https://github.com/Mongrul), [Erkagoon](https://github.com/erkagoon)
- Beta testers: Anexia, k, Sh1NoX

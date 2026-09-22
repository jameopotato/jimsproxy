# JimsProxy

A maintained fork of [HermesProxy](https://github.com/WowLegacyCore/HermesProxy) — a protocol translation proxy that lets WoW Classic 1.14.2 clients connect to vanilla 1.12.1 servers. Primary target: [Kronos](https://www.kronos-wow.com/).

The upstream HermesProxy project was [archived in November 2024](https://github.com/WowLegacyCore/HermesProxy). JimsProxy is rebased onto [Xian55's fork](https://github.com/Xian55/HermesProxy) (April 2026) for 18 months of community fixes, then adds Kronos-specific translation fixes and diagnostic tooling on top.

**License:** GPL v3 (inherited from upstream — see `LICENSE`)

## Quick Install (Windows)

The **Classic WoW Launcher** ([jimothy.cc/install](https://jimothy.cc/install)) installs JimsProxy, updates it (stable or beta channel), manages addons, and starts the proxy and the game together. Windows only.

## Manual Install (no launcher)

[docs/MANUAL-INSTALL.md](docs/MANUAL-INSTALL.md) covers installing and running the proxy without the launcher: download the current archive ([stable](https://jimothy.cc/proxy/stable/latest) or [beta](https://jimothy.cc/proxy/beta/latest)), add the configuration file, set the server address, and run it with `scripts/play.bat` or manually. The main procedure targets Windows; Linux and macOS are in the appendix (community-supported, built from source). The 1.14.2 (build 42597) game client is not included.

## What This Fork Adds

- **Kronos translation fixes** — spell casting, realm switching, disconnects, combat log, auction house, and dozens of packet translation bugs fixed for Twinstar's MaNGOS fork
- **Structured JSONL logging** — every packet, translation, and lifecycle event emitted to machine-readable logs for diagnosing issues
- **Spell system overhaul** — cast-time spell queue, GCD sweep sync, RTT-adaptive fire offset, off-GCD macro support
- **Auto-reconnect** — recovers from unplanned server disconnects without manual relogin
- **NPC and pet scale parity** — creature sizes match vanilla 1.12 proportions
- **Bundled with JimsProxy Launcher** — one-click setup, automatic updates, addon management
- **Active development** — more fixes and features coming

See [CHANGES.md](CHANGES.md) for the full changelog.

## Supported Versions
### Recommended Client Version and Build
1.14.2 build 42597

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

```bash
cp build/JimsProxy.exe <game_dir>/Hermes/JimsProxy.exe
cp -r build/CSV/* <game_dir>/Hermes/CSV/
```

.NET 6 will not work — the target framework is `net10.0` (set centrally in `Directory.Packages.props`).

## Acknowledgements

- [CypherCore](https://github.com/CypherCore/CypherCore) and [BotFarm](https://github.com/jackpoz/BotFarm) — foundational code
- [Modox](https://github.com/mdx7) — reverse engineering work on Classic clients
- [Xian55/HermesProxy](https://github.com/Xian55/HermesProxy) — maintained fork we rebased onto (April 2026)
- [WowLegacyCore/HermesProxy](https://github.com/WowLegacyCore/HermesProxy) — original upstream (archived November 2024)
- JimsProxy contributors: [Mirasu](https://github.com/Mongrul), [Erkagoon](https://github.com/erkagoon)
- Beta testers: Anexia, k, Sh1NoX

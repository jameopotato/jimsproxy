# JimsProxy

A maintained fork of [HermesProxy](https://github.com/WowLegacyCore/HermesProxy) — a protocol translation proxy that lets WoW Classic 1.14.2 clients connect to vanilla 1.12.1 private servers. Primary target: [Kronos](https://www.kronos-wow.com/).

The upstream HermesProxy project was [archived in November 2024](https://github.com/WowLegacyCore/HermesProxy). JimsProxy is rebased onto [Xian55's fork](https://github.com/Xian55/HermesProxy) (April 2026) for 18 months of community fixes, then adds Kronos-specific translation fixes and diagnostic tooling on top.

**License:** GPL v3 (inherited from upstream — see `LICENSE`)

## Quick Start (optional)

Download the **JimsProxy Launcher** from [jimothy.cc/install](https://jimothy.cc/install). The launcher handles proxy updates, game launch, addon management, and configuration — no manual setup required.

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

## Configuration

The proxy reads `HermesProxy.config` (XML format) from the working directory. The JimsProxy Launcher manages this automatically.

For advanced use, CLI arguments override config values:

```bash
JimsProxy --config MyServer.config
JimsProxy --set ServerAddress=logon.example.com --set ServerPort=3724
JimsProxy --no-version-check
```

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (`winget install --id Microsoft.DotNet.SDK.10`).

> **Note:** The project directory is still named `HermesProxy/` for upstream compatibility. The output binary is `JimsProxy.exe`.

```bash
# Clone
git clone https://github.com/jameopotato/jimsproxy.git
cd jimsproxy

# Build
dotnet build HermesProxy

# Run tests (320 tests)
dotnet test

# Publish (self-contained single-file exe + CSV data)
dotnet publish HermesProxy \
  --configuration Release \
  --use-current-runtime \
  -p:UsePublishBuildSettings=true \
  -o build/
```

Output: `build/JimsProxy.exe` + `build/CSV/` + `build/HermesProxy.config`

To test locally, copy the build output to your game's `Hermes/` directory:

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

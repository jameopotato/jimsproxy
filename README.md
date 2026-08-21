# JimsProxy

A maintained fork of [HermesProxy](https://github.com/WowLegacyCore/HermesProxy) — a protocol translation proxy that lets WoW Classic 1.14.2 clients connect to vanilla 1.12.1 servers. Primary target: [Kronos](https://www.kronos-wow.com/).

The upstream HermesProxy project was [archived in November 2024](https://github.com/WowLegacyCore/HermesProxy). JimsProxy is rebased onto [Xian55's fork](https://github.com/Xian55/HermesProxy) (April 2026) for 18 months of community fixes, then adds Kronos-specific translation fixes and diagnostic tooling on top.

**License:** GPL v3 (inherited from upstream — see `LICENSE`)

---

## Getting Started

There are two ways to run JimsProxy. Both are supported.

| | **Launcher** | **Manual** |
|---|---|---|
| Setup | One click | Edit one config value |
| Proxy updates | Automatic | You re-download |
| Game client | Handled for you | **You supply it** |
| Addons, keybind import, repair | Included | Not included |

**Most people want the launcher.** Download it from [jimothy.cc/install](https://jimothy.cc/install) — it handles proxy updates, game launch, addon management, and configuration, and you can stop reading here.

**If you'd rather run the proxy yourself**, follow [Manual Setup](#manual-setup) below.

---

## Manual Setup

### What you need first

**1. The proxy.** Grab a release from [Releases](https://github.com/jameopotato/jimsproxy/releases), or [build it yourself](#building-from-source). You want a folder containing:

```
JimsProxy.exe
HermesProxy.config
CSV/            (game data tables — required)
```

All three must sit **in the same folder**. The proxy re-homes its working directory to wherever the `.exe` lives, so it reads `CSV/` and the config from beside itself no matter how you launch it.

> The config file is named **`HermesProxy.config`**, not `JimsProxy.config`, even though the binary is `JimsProxy.exe`. The project kept upstream's filename for compatibility.

**2. A game client — you supply this.** JimsProxy does not distribute game binaries. You need:

| | |
|---|---|
| Version | **WoW Classic Era 1.14.2** |
| Build | **42597** |
| Executable | **`WowClassic_ForCustomServers.exe`** |

The custom-servers executable is **required**. A stock `WowClassic.exe` will only ever talk to Blizzard's servers — it cannot be pointed at a private server, and no proxy can change that.

Your client build must match the `ClientBuild` value in the config **exactly**, or login fails.

### Step 1 — Point the proxy at your server

Open `HermesProxy.config` and set `ServerAddress`. For Kronos:

```xml
<add key="ServerAddress" value="login.twinstar-wow.com" />
```

For any other 1.12 server, use its logon address — the same thing you'd put in `SET REALMLIST`.

Everything else ships ready to go. In particular you should **not** need to touch `ClientBuild` (already `42597`), `ServerBuild` (`auto` picks the right legacy version), or `ClientSeed` (leave this one alone entirely — the real per-build seeds load automatically from `CSV/BuildAuthSeeds.csv`).

### Step 2 — Point your client at the proxy

Edit `WTF/Config.wtf` in your game folder:

```
SET portal "127.0.0.1:1119"
```

`1119` is the proxy's `BNetPort`. If you change one, change both — a mismatch here is the single most common "it just won't connect."

Classic Era has no `realmlist.wtf`; `SET portal` is the whole story.

### Step 3 — Start the proxy

Run `JimsProxy.exe`. Wait for this line:

```
Starting WorldSocket service
```

That's the ready signal — it's the last of the four listeners to come up, so everything is bound once you see it. **Don't launch the game before it appears.**

### Step 4 — Start the game

Launch `WowClassic_ForCustomServers.exe` directly.

### Step 5 — Log in

Use your normal account credentials for the server you're connecting to.

To stop, close the proxy's console window.

---

## Configuration

The proxy reads `HermesProxy.config` (XML) from the folder containing the executable. Every key is documented inline in that file; the ones that matter for setup:

| Key | Default | What it's for |
|---|---|---|
| `ServerAddress` | `127.0.0.1` | Your server's logon address. **The one value you must set.** |
| `ServerPort` | `3724` | Logon port. |
| `ServerBuild` | `auto` | Legacy server version. `auto` picks from `ClientBuild`; or pin `5875` (1.12.1). |
| `ClientBuild` | `42597` | Must match your client exactly. `42597` = 1.14.2. |
| `ClientSeed` | *(static)* | Auth seed fallback. **Don't change this.** |
| `BNetPort` | `1119` | What your `Config.wtf` portal points at. |
| `RealmPort` / `InstancePort` / `RestPort` | `8084` / `8086` / `8081` | Other listeners. Change only on a port conflict. |
| `ServerType` | `Kronos` | Server fork. Selects fork-specific item data. `Generic` exists as scaffolding for future server support and is **not a tested configuration** today. |
| `DebugOutput` | `false` | Extra console detail. |
| `PacketsLog` | `false` | Write a packet capture per session. Large — turn on only for bug reports. |
| `StructuredLog` | `true` | JSONL diagnostic events in `Logs/`. Leave on; it's what makes bug reports actionable. |

### Command line

CLI arguments override config values:

```
JimsProxy --config MyServer.config
JimsProxy --set ServerAddress=logon.example.com --set ServerPort=3724
JimsProxy --no-version-check
JimsProxy --metrics
```

| Flag | Description |
|---|---|
| `--config <path>` | Use a different config file (default: `HermesProxy.config`) |
| `--set Key=Value` | Override a single config value; repeatable |
| `--no-version-check` | Skip the update check at startup |
| `--metrics` | Enable per-opcode latency metrics |

Running several servers? Keep a config per server and pick with `--config`.

### Chat commands

Typed into any chat box:

| Command | Effect |
|---|---|
| `!qcomplete <questId>` | Mark a quest complete in the proxy's tracking |
| `!quncomplete <questId>` | Undo the above |

---

## Troubleshooting

**"World Server is Down", or the client never reaches character select.**
Usually the portal and `BNetPort` disagree. `SET portal "127.0.0.1:1119"` must match `BNetPort` in the config.

**The proxy exits immediately, or reports a bind/port error.**
It needs four free ports on `127.0.0.1`: **1119, 8084, 8086, 8081**. Almost always this is a previous proxy still running — check Task Manager for `JimsProxy.exe` / `HermesProxy.exe` and end it.

A leftover proxy is worth taking seriously: if one is still holding the ports, it will keep serving your session quite happily, and you'll think you're running a build you aren't.

A Windows `WSAEACCES (10013)` specifically means something else already owns the port — another program, a Windows port reservation, or a proxy running with higher privileges than you.

**Login fails, or the client complains about version.**
`ClientBuild` doesn't match your client. 1.14.2 is `42597`. Confirm the executable you're launching is really `WowClassic_ForCustomServers.exe`.

**Quest progress or keybindings look wrong after updating.**
The proxy keeps per-account state in `AccountData/` beside the executable. When you move to a new version, copy your existing `AccountData/` folder across — otherwise quest tracking starts from scratch.

**Antivirus flags the executable.**
It's an ~80 MB unsigned self-contained .NET binary that opens listening sockets. That shape draws false positives. Build from source if you'd rather not take our word for it.

**Filing a bug.**
Set `DebugOutput=true` and reproduce, then attach `Logs/jimsproxy-*.jsonl`. If asked for a packet capture, also set `PacketsLog=true` — and turn it back off afterwards.

> Note: unlike upstream HermesProxy, JimsProxy **ignores** the client's "Optimize Network for Speed" setting on the local connection (it's a no-op over loopback), so you don't need to keep it enabled to avoid disconnects.

---

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

Development and testing target **1.14.2 build 42597**. The others are inherited from upstream and are not regularly exercised.

### Legacy Server (what emulators run)

| Version | Expansion | Build | Server Software        |
|---------|-----------|-------|------------------------|
| 1.12.1  | Vanilla   | 5875  | CMaNGOS, VMaNGOS, etc. |
| 1.12.2  | Vanilla   | 6005  | CMaNGOS, VMaNGOS, etc. |
| 1.12.3  | Vanilla   | 6141  | CMaNGOS, VMaNGOS, etc. |

Kronos is the server this fork is built and tested against.

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (`winget install --id Microsoft.DotNet.SDK.10`).

> **Note:** The project directory is still named `HermesProxy/` for upstream compatibility. The output binary is `JimsProxy.exe`.

```
git clone https://github.com/jameopotato/jimsproxy.git
cd jimsproxy

dotnet build HermesProxy
dotnet test

dotnet publish HermesProxy --configuration Release --use-current-runtime -p:UsePublishBuildSettings=true -o build/
```

That produces a self-contained single-file build in `build/` — `JimsProxy.exe`, `CSV/`, and `HermesProxy.config`, which is exactly the layout [Manual Setup](#manual-setup) expects. No .NET runtime is needed on the machine that runs it.

.NET 6 will not work — the target framework is `net10.0`, set centrally in `Directory.Packages.props`.

## Acknowledgements

- [CypherCore](https://github.com/CypherCore/CypherCore) and [BotFarm](https://github.com/jackpoz/BotFarm) — foundational code
- [Modox](https://github.com/mdx7) — reverse engineering work on Classic clients
- [Xian55/HermesProxy](https://github.com/Xian55/HermesProxy) — maintained fork we rebased onto (April 2026)
- [WowLegacyCore/HermesProxy](https://github.com/WowLegacyCore/HermesProxy) — original upstream (archived November 2024)
- JimsProxy contributors: [Mirasu](https://github.com/Mongrul), [Erkagoon](https://github.com/erkagoon)
- Beta testers: Anexia, k, Sh1NoX

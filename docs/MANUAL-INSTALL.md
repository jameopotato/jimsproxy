# JimsProxy manual installation

Install and run JimsProxy without the launcher. This page targets Windows; Linux and macOS are
in the [appendix](#appendix-linux-and-macos-community-supported).

Two alternatives perform the installation automatically: the
[quick-start bundle](QUICK-INSTALL.md) (a zip whose installer sets up the proxy next to an
existing client) and the Classic WoW Launcher at [jimothy.cc/install](https://jimothy.cc/install).

Contents

- [Requirements](#requirements)
- [Install](#install)
- [Run](#run)
- [JimsPlus addon (optional)](#jimsplus-addon-optional)
- [Update](#update)
- [Uninstall](#uninstall)
- [Troubleshooting](#troubleshooting)
- [Appendix: Linux and macOS (community-supported)](#appendix-linux-and-macos-community-supported)
- [Reference](#reference)
- [Reporting a bug](#reporting-a-bug)

---

## Requirements

- Windows 10 or 11, 64-bit. Windows PowerShell 5.1 (included) runs every command below. No
  administrator rights are needed.
- WoW Classic Era **1.14.2, build 42597**, with `WowClassic_ForCustomServers.exe`. The game
  client is not included. Any other build fails at login; the unmodified `WowClassic.exe`
  connects only to Blizzard.
- An account on the server. For Kronos: [kronos-wow.com](https://www.kronos-wow.com).
- Network access to `jimothy.cc` (proxy archive) and `raw.githubusercontent.com`
  (configuration file, play scripts).

### Verifying the client build

Right-click `WowClassic_ForCustomServers.exe`, select **Properties → Details**, and read
**File version**. It must be `1.14.2.42597`.

### Paths

The commands use `C:\Games\Kronos` as the base folder, with the client at
`C:\Games\Kronos\World of Warcraft\_classic_era_\`. Substitute your own paths. The proxy folder
`Hermes` goes next to the `World of Warcraft` folder; the play scripts find the client through
this layout:

```
C:\Games\Kronos\
├── Hermes\                          the proxy (created in step 1)
└── World of Warcraft\
    └── _classic_era_\
        ├── WowClassic_ForCustomServers.exe
        ├── Data\
        ├── Interface\
        └── WTF\
```

---

## Install

Open PowerShell (Windows key, type `powershell`, Enter) and paste each block.

### 1. Create the proxy folder

```powershell
New-Item -ItemType Directory -Force "C:\Games\Kronos\Hermes"
```

If the client folder holds `_classic_era_` directly (no `World of Warcraft` level), create
`Hermes` next to `_classic_era_` instead.

### 2. Download and extract the proxy

| Channel | URL |
|---|---|
| Stable | <https://jimothy.cc/proxy/stable/latest> |
| Beta (newer changes, less testing) | <https://jimothy.cc/proxy/beta/latest> |

```powershell
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri "https://jimothy.cc/proxy/stable/latest" -OutFile "$env:TEMP\jimsproxy-bundle.zip"
Expand-Archive -Path "$env:TEMP\jimsproxy-bundle.zip" -DestinationPath "C:\Games\Kronos\Hermes" -Force
```

`Hermes` now contains `JimsProxy.exe`, `CSV`, `Addons`, and `manifest.json`. The first line
disables the progress bar, which otherwise slows the download roughly twentyfold on Windows
PowerShell 5.1.

Browser alternative: the URL saves `JimsProxy-<version>.zip`; extract its contents directly into
`Hermes` (with **Extract All**, replace the suggested destination, or the files land in a
`JimsProxy-<version>` sub-folder).

To verify the download against the channel manifest (`sha256` in
`https://jimothy.cc/proxy/<channel>/latest.json`):

```powershell
(Get-FileHash "$env:TEMP\jimsproxy-bundle.zip" -Algorithm SHA256).Hash -eq (Invoke-RestMethod "https://jimothy.cc/proxy/stable/latest.json").sha256
```

`JimsProxy.exe` is not code-signed and opens listening ports; antivirus software may quarantine
it, and SmartScreen may warn on first start (**More info → Run anyway**). Add the `Hermes` folder
to the antivirus exclusion list rather than restoring the file alone.

### 3. Add the configuration file

The archive contains no configuration file; without `HermesProxy.config` (the name is inherited
from upstream) the proxy exits with `Config loading failed`.

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jameopotato/jimsproxy/master/HermesProxy/HermesProxy.config" -OutFile "C:\Games\Kronos\Hermes\HermesProxy.config"
```

### 4. Set the server address

```powershell
$cfg = "C:\Games\Kronos\Hermes\HermesProxy.config"
(Get-Content $cfg) -replace '(<add key="ServerAddress" value=")[^"]*(")', '${1}login.twinstar-wow.com$2' | Set-Content $cfg
```

Equivalent edit in a text editor: change `<add key="ServerAddress" value="127.0.0.1" />` to the
login address.

| Server | `ServerAddress` |
|---|---|
| Kronos | `login.twinstar-wow.com` |
| Kronos 2 | `login2.twinstar-wow.com` |
| Kronos 3 | `login3.twinstar-wow.com` |

No other key needs changing for Kronos. All keys: [configuration reference](configuration.md).

### 5. Download the play scripts

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jameopotato/jimsproxy/master/scripts/play.bat" -OutFile "C:\Games\Kronos\Hermes\play.bat"
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jameopotato/jimsproxy/master/scripts/play.ps1" -OutFile "C:\Games\Kronos\Hermes\play.ps1"
```

`play.bat` runs one session: it checks that the proxy's ports are free, sets the client's
portal line, starts the proxy, waits for its ready signal, starts the game, and stops the proxy
when the game exits. If the client is not in the layout above, append `-GameExe "<path to
WowClassic_ForCustomServers.exe>"` to the `powershell` line in `play.bat`. All parameters are
documented in the header of `play.ps1`.

---

## Run

Double-click `Hermes\play.bat`. The game starts after the console prints
`[play] proxy is ready on 127.0.0.1:1119`; the console must stay open during the session. Exit
the game to stop the proxy. The proxy's console output is saved to `Hermes\play-console.log`.

Without the scripts:

1. `_classic_era_\WTF\Config.wtf` must contain `SET portal "127.0.0.1:1119"` (create the file if
   it does not exist). `1119` is the proxy's `BNetPort`.
2. Start `JimsProxy.exe` and wait for the line `Starting WorldSocket service`.
3. Start `WowClassic_ForCustomServers.exe`.
4. After the game exits, close the proxy window or press `Ctrl+C`. `taskkill /F` skips the
   log flush.

---

## JimsPlus addon (optional)

JimsPlus ships in the archive (`Hermes\Addons\JimsPlus`) and pairs with the proxy: cast bars,
tooltip and name fixes, an options panel (`/jp`). The proxy disables the addon channel when the
versions differ, so copy it again after each proxy update.

```powershell
Copy-Item -Recurse -Force "C:\Games\Kronos\Hermes\Addons\JimsPlus" "C:\Games\Kronos\World of Warcraft\_classic_era_\Interface\AddOns\JimsPlus"
```

---

## Update

Stop the proxy, then run the step 2 commands again. The archive contains neither
`HermesProxy.config` nor `AccountData`, so both are left unchanged. Copy JimsPlus again if it is
installed.

The four most recent releases of each channel remain available by version, for example
`https://jimothy.cc/proxy/stable/JimsProxy-5.2.0.zip`. The installed version is in
`Hermes\manifest.json` and on the `Version` line the proxy prints at startup.

## Uninstall

Delete the `Hermes` folder (`AccountData` inside it holds per-account state; keep a copy for a
later reinstall), delete `_classic_era_\Interface\AddOns\JimsPlus`, and remove the `SET portal`
line from `_classic_era_\WTF\Config.wtf`.

---

## Troubleshooting

**"World Server is Down", or no realm list.** The client is not reaching the proxy: the portal
line does not match `BNetPort`, or the game was started before `Starting WorldSocket service`.

**The proxy exits immediately, or reports a port in use.** Ports 1119, 8081, 8084 and 8086 must
be free. End any running `JimsProxy.exe` (or `HermesProxy.exe`) in Task Manager. A
`WSAEACCES (10013)` error means another program, a Windows port reservation, or a proxy running
with higher privileges holds the port.

**`Config loading failed`.** `HermesProxy.config` is not next to `JimsProxy.exe`, or was saved as
`HermesProxy.config.txt`. `The verification of the config failed` names the invalid value on the
preceding line.

**Login fails, or `Unsupported ClientBuild`.** The client build is not 42597, or the unmodified
`WowClassic.exe` was started.

**`Invoke-WebRequest` fails with an SSL or TLS error.** The server requires TLS 1.2 or newer.
Run `[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12` in the
same window first.

**The download link returns 503.** The channel is paused while a release is published. Retry
later.

**The downloaded file is a few KB, or `Expand-Archive` rejects it.** An error page was saved
instead of the archive. Download again, or verify the hash as in step 2.

**A yellow "update available" message at proxy startup.** A version check against the archived
upstream project; ignore it. The play scripts pass `--no-version-check`.

**Quest progress or settings wrong after an update.** `AccountData` was deleted with the
`Hermes` folder. Restore it from the previous installation; the update procedure above keeps it.

Unlike upstream HermesProxy, JimsProxy ignores the client's *Optimize Network for Speed* setting
on the local connection; it does not need to be enabled to avoid disconnects.

---

## Appendix: Linux and macOS (community-supported)

Windows is the tested platform. The proxy builds and runs natively on Linux and macOS; the game
client does not. No prebuilt Linux or macOS proxy binaries are published, so both platforms
build from source.

### Linux (community-supported)

Build with the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then set
the server address and fetch the play script:

```bash
git clone https://github.com/jameopotato/jimsproxy.git
cd jimsproxy
dotnet publish HermesProxy --configuration Release --use-current-runtime -p:UsePublishBuildSettings=true -o ~/kronos/Hermes
chmod +x ~/kronos/Hermes/JimsProxy
sed -i 's|<add key="ServerAddress" value="[^"]*"|<add key="ServerAddress" value="login.twinstar-wow.com"|' ~/kronos/Hermes/HermesProxy.config
curl -L -o ~/kronos/Hermes/play.sh https://raw.githubusercontent.com/jameopotato/jimsproxy/master/scripts/play.sh
chmod +x ~/kronos/Hermes/play.sh
```

The source build includes `HermesProxy.config`, so step 3 does not apply. The client runs under
Wine or Proton (reported working: Wine 9+ or GE-Proton with DXVK, in a dedicated prefix, without
the Battle.net application). The proxy listens on `127.0.0.1`, which Wine shares with the host.
If the game window stays black or the client crashes at start, set `SET gxApi "D3D11"` in
`WTF/Config.wtf`.

`play.sh` does what `play.bat` does. It starts the game with `wine "<game-exe>"` by default;
`--game-cmd` hands the start to Lutris, Bottles or Steam, and `--help` lists the options:

```bash
~/kronos/Hermes/play.sh --game-exe "$HOME/kronos/World of Warcraft/_classic_era_/WowClassic_ForCustomServers.exe"
```

Without the script: run `./JimsProxy` in a terminal, wait for `Starting WorldSocket service`,
start the client, and stop the proxy with `Ctrl+C` after the game exits.

### macOS (community-supported)

The proxy builds with the same `dotnet publish` command on Apple Silicon and Intel, and `play.sh`
runs the proxy side (`--game-cmd` supplies the command that starts the game). If the proxy exits
with `AesGcm is not supported on your platform`, it prints the remedy on the following lines.
The project has not tested a client configuration on macOS; the Windows client under Apple's
Game Porting Toolkit or a Wine-based tool (CrossOver, Whisky) is the available route.

---

## Reference

### Command line flags

Flags override `HermesProxy.config` for the current run.

| Flag | Description |
|---|---|
| `--config <path>` | Use a different configuration file (default: `HermesProxy.config` beside the executable) |
| `--set Key=Value` | Override a single configuration value; repeatable |
| `--no-version-check` | Skip the update check at startup |
| `--metrics` | Print per-opcode latency metrics every 60 s |

### Chat commands

| Command | Effect |
|---|---|
| `!qcomplete <questId>` | Mark a quest complete in the proxy's tracking |
| `!quncomplete <questId>` | Reverse `!qcomplete` |

### Files and ports

All files are written next to the executable.

| Path | Contents |
|---|---|
| `Logs\jimsproxy-<date>-<time>.jsonl` | Structured diagnostic log, one per session |
| `AccountData\<account>\` | Per-account state the 1.14 client expects the server to persist |
| `PacketsLog\` | Packet captures, only when `PacketsLog` is `true` |
| `play-console.log` | Console output of the most recent session (play scripts) |

| Key | Default | Purpose |
|---|---|---|
| `BNetPort` | `1119` | Login; the port in the client's `SET portal` line |
| `RestPort` | `8081` | Login REST calls |
| `RealmPort` | `8084` | Realm list and character selection |
| `InstancePort` | `8086` | Game world; `Starting WorldSocket service` is its ready signal |

A second proxy instance needs its own copy of `Hermes` with a different port set (the launcher
uses `1120 / 8082 / 8085 / 8087`) and a client portal that references the second `BNetPort`.

### Download manifest

`https://jimothy.cc/proxy/<channel>/latest.json` describes the current release: `version`,
`url`, `sha256`, `pub_date`, `notes`, and `build` metadata. `latest` redirects to `url`. While a
channel is paused, `latest` returns 503 and `latest.json` returns 204.

### Configuration keys

[configuration.md](configuration.md) documents all 40 keys.

---

## Reporting a bug

Open an issue at [github.com/jameopotato/jimsproxy/issues](https://github.com/jameopotato/jimsproxy/issues)
with the proxy version (`manifest.json` or the startup `Version` line), the session's
`Logs\jimsproxy-*.jsonl`, and, on Linux or macOS, the tool and version used to run the client.
Set `DebugOutput` to `true` and reproduce if more detail is requested; set `PacketsLog` to `true`
only for a requested capture, then reset it.

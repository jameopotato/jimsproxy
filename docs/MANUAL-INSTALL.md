# JimsProxy manual installation guide

This guide describes how to install, configure, and run JimsProxy without the Classic WoW
Launcher. The main procedure targets Windows. Linux and macOS are covered in the
[appendix](#appendix-linux-and-macos-community-supported).

Two alternatives on Windows perform the Installation section automatically: the
[quick-start bundle](QUICK-INSTALL.md), a zip whose installer sets up the proxy next to an
existing client, and the Classic WoW Launcher, available at
[jimothy.cc/install](https://jimothy.cc/install), a desktop application that also updates the
proxy (stable or beta channel), manages addons, and starts the proxy and the game together.

Contents

- [Overview](#overview)
- [Prerequisites](#prerequisites)
- [Conventions](#conventions)
- [Installation](#installation)
  - [Step 1: Create the proxy folder](#step-1-create-the-proxy-folder)
  - [Step 2: Download and extract the proxy](#step-2-download-and-extract-the-proxy)
  - [Step 3: Download the configuration file](#step-3-download-the-configuration-file)
  - [Step 4: Set the server address](#step-4-set-the-server-address)
  - [Step 5: Download the play scripts](#step-5-download-the-play-scripts)
- [Running the proxy and the game](#running-the-proxy-and-the-game)
  - [With the play scripts](#with-the-play-scripts)
  - [Manually](#manually)
- [Installing the JimsPlus addon (optional)](#installing-the-jimsplus-addon-optional)
- [Updating](#updating)
- [Uninstalling](#uninstalling)
- [Troubleshooting](#troubleshooting)
- [Appendix: Linux and macOS (community-supported)](#appendix-linux-and-macos-community-supported)
- [Reference](#reference)
- [Reporting a bug](#reporting-a-bug)

---

## Overview

JimsProxy is a local proxy that translates between the WoW Classic Era 1.14.2 client and a
vanilla 1.12 server such as Kronos. The game client connects to the proxy on the local machine;
the proxy connects to the server.

A manual installation consists of:

1. A folder containing the proxy executable, its data files, and its configuration file.
2. One configuration value, `ServerAddress`, set to the server's login address.
3. One line in the game client's `Config.wtf` that directs the client to the proxy.

Once installed, each play session starts the proxy, waits until it is ready, starts the game,
and stops the proxy after the game exits. The supplied play scripts perform this sequence; the
[Manually](#manually) section describes the same sequence step by step.

## Prerequisites

| Requirement | Details |
|---|---|
| Operating system | Windows 10 or Windows 11, 64-bit. For Linux and macOS, see the [appendix](#appendix-linux-and-macos-community-supported). |
| Game client | WoW Classic Era **1.14.2, build 42597**. JimsProxy does not include or distribute the game client. The build must match exactly; other builds fail at login. See [Verifying the client build](#verifying-the-client-build). |
| Custom-server capable executable | One of the two options under [Client executable options](#client-executable-options). |
| Server account | An account for the server. For Kronos, create one at [kronos-wow.com](https://www.kronos-wow.com). |
| Disk space | Approximately 200 MB next to the game client. |
| Privileges | None. No step requires administrator rights. |

### Verifying the client build

1. In File Explorer, right-click the game executable (`WowClassic_ForCustomServers.exe` or
   `WowClassic.exe`) and select **Properties**.
2. Open the **Details** tab and read **File version**.

Expected value: `1.14.2.42597`.

### Client executable options

The unmodified `WowClassic.exe` connects only to Blizzard's servers. One of the following is
required for it to connect to the proxy:

- **`WowClassic_ForCustomServers.exe`**: a build of the client executable that accepts a custom
  server. The steps in this guide assume this executable.
- **The [Arctium WoW Launcher](https://github.com/Arctium/WoW-Launcher)** with the unmodified
  `WowClassic.exe`. The Arctium launcher patches the client in memory when it starts it. Place
  it in the game folder that contains `_classic_era_` and start it with the arguments
  `--staticseed --version=ClassicEra`. Where this guide says to start
  `WowClassic_ForCustomServers.exe`, start the Arctium launcher instead. The `ClientSeed`
  value in the proxy configuration corresponds to the seed that `--staticseed` uses; do not
  change it.

## Conventions

- **Paths.** Examples use `C:\Games\Kronos` as the base folder and
  `C:\Games\Kronos\World of Warcraft\_classic_era_\` as the game client folder. Substitute
  your own paths throughout.
- **PowerShell.** Several steps provide a PowerShell command block as an alternative to the
  File Explorer procedure. To open PowerShell, press the Windows key, type `powershell`, and
  press Enter. Paste a block in full and press Enter. Windows PowerShell 5.1 (included with
  Windows) is sufficient.
- **Expected result.** Each step ends with the state to verify before continuing.

---

## Installation

### Step 1: Create the proxy folder

The proxy is installed in a folder named `Hermes` located next to the `World of Warcraft`
folder. The play scripts locate the game client through this layout.

```
C:\Games\Kronos\
├── Hermes\                                  the proxy folder (created in this step)
└── World of Warcraft\                       the game client
    └── _classic_era_\
        ├── WowClassic_ForCustomServers.exe
        ├── Data\
        ├── Interface\
        └── WTF\
```

**File Explorer**

1. Open the folder that contains the `World of Warcraft` folder (`C:\Games\Kronos` in the
   example).
2. Right-click an empty area, select **New → Folder**, and name the folder `Hermes`.

**PowerShell**

```powershell
New-Item -ItemType Directory -Force "C:\Games\Kronos\Hermes"
```

If the game client folder contains `_classic_era_` directly (no `World of Warcraft` level),
create `Hermes` next to `_classic_era_`. Other layouts are supported by passing the game
executable's path to the play script; see [Step 5](#step-5-download-the-play-scripts).

**Expected result:** `C:\Games\Kronos\Hermes` exists and is empty.

### Step 2: Download and extract the proxy

The proxy is distributed as a zip archive.

| Channel | URL | Contents |
|---|---|---|
| Stable | <https://jimothy.cc/proxy/stable/latest> | The current release |
| Beta | <https://jimothy.cc/proxy/beta/latest> | Pre-release builds with newer changes and less testing |

The archive contains `JimsProxy.exe`, the `CSV` data folder, the `Addons` folder (the JimsPlus
addon), and `manifest.json` (version information). It does not contain a configuration file;
Step 3 adds one.

**File Explorer**

1. Open the URL for the chosen channel. The browser saves a file named like
   `hermes-bundle-5.2.0.zip` to the Downloads folder.
2. Right-click the downloaded file and select **Extract All…**.
3. In the destination field, replace the suggested path with `C:\Games\Kronos\Hermes` and
   select **Extract**.

**PowerShell**

```powershell
Invoke-WebRequest -Uri "https://jimothy.cc/proxy/stable/latest" -OutFile "$env:TEMP\jimsproxy-bundle.zip"
Expand-Archive -Path "$env:TEMP\jimsproxy-bundle.zip" -DestinationPath "C:\Games\Kronos\Hermes" -Force
```

**Expected result:** `C:\Games\Kronos\Hermes` contains `JimsProxy.exe`, `CSV`, `Addons`, and
`manifest.json` directly. If the contents are inside a sub-folder (for example
`Hermes\hermes-bundle-5.2.0\JimsProxy.exe`), move them up into `Hermes` and delete the empty
sub-folder.

> **Note:** `JimsProxy.exe` is not code-signed and opens listening ports on the local machine.
> Antivirus software may quarantine it, and Windows SmartScreen may display "Windows protected
> your PC" on first start. To proceed with SmartScreen, select **More info**, then **Run
> anyway**. If antivirus software quarantines the file, restore it and add the `Hermes` folder
> to the exclusion list; restoring the file without an exclusion is usually undone at the next
> scan.

### Step 3: Download the configuration file

The proxy reads its settings from `HermesProxy.config` in the same folder as `JimsProxy.exe`.
The file name is inherited from the upstream project. The proxy exits with
`Config loading failed` if the file is missing.

**PowerShell**

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jameopotato/jimsproxy/master/HermesProxy/HermesProxy.config" -OutFile "C:\Games\Kronos\Hermes\HermesProxy.config"
```

**Browser**

1. Open
   [HermesProxy.config](https://raw.githubusercontent.com/jameopotato/jimsproxy/master/HermesProxy/HermesProxy.config).
2. Press `Ctrl+S`.
3. Select `C:\Games\Kronos\Hermes` as the location, set **Save as type** to **All files**, set
   the file name to `HermesProxy.config`, and save.

**Expected result:** `C:\Games\Kronos\Hermes\HermesProxy.config` exists. The name must be
exactly `HermesProxy.config`; a browser may append `.txt` if **Save as type** is left at the
default.

### Step 4: Set the server address

The downloaded configuration has `ServerAddress` set to `127.0.0.1`. Set it to the server's
login address.

| Server (name in the launcher) | `ServerAddress` |
|---|---|
| Kronos | `login.twinstar-wow.com` |
| Kronos 2 | `login2.twinstar-wow.com` |
| Kronos 3 | `login3.twinstar-wow.com` |

For another vanilla 1.12 server, use its login address (the value used with `SET REALMLIST`
on a 1.12 client).

**Notepad**

1. Right-click `C:\Games\Kronos\Hermes\HermesProxy.config` and select **Open with → Notepad**.
2. Locate the line:

   ```xml
   <add key="ServerAddress" value="127.0.0.1" />
   ```

3. Replace `127.0.0.1` with the login address:

   ```xml
   <add key="ServerAddress" value="login.twinstar-wow.com" />
   ```

4. Press `Ctrl+S` and close Notepad.

**PowerShell**

```powershell
$cfg = "C:\Games\Kronos\Hermes\HermesProxy.config"
(Get-Content $cfg) -replace '(<add key="ServerAddress" value=")[^"]*(")', '${1}login.twinstar-wow.com$2' | Set-Content $cfg
```

No other value needs to change for Kronos: `ClientBuild` is `42597`, `ServerBuild` is `auto`
(resolves to 1.12.1 for a 1.14 client), `ServerType` is `Kronos`, and `ClientSeed` is a
fallback value the proxy overrides at runtime. All keys are documented in the
[configuration reference](configuration.md).

**Expected result:** the `ServerAddress` line in the file contains the login address.

### Step 5: Download the play scripts

`play.bat` and `play.ps1` run one play session: they verify that the proxy's ports are free,
set the client's portal line, start the proxy, wait for its ready signal, start the game, and
stop the proxy after the game exits. They are optional; the [Manually](#manually) section
describes the same sequence without them.

**PowerShell**

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jameopotato/jimsproxy/master/scripts/play.bat" -OutFile "C:\Games\Kronos\Hermes\play.bat"
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jameopotato/jimsproxy/master/scripts/play.ps1" -OutFile "C:\Games\Kronos\Hermes\play.ps1"
```

**Browser:** open each URL, press `Ctrl+S`, and save into `C:\Games\Kronos\Hermes` with
**Save as type** set to **All files** and the names `play.bat` and `play.ps1`.

The scripts locate the game executable through the folder layout in Step 1. For any other
layout, pass the executable's path: open `play.bat` in Notepad and append `-GameExe "<path>"`
to the `powershell` line, for example:

```
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0play.ps1" %* -GameExe "D:\Games\_classic_era_\WowClassic_ForCustomServers.exe"
```

For the Arctium launcher, pass its path and arguments:
`-GameExe "C:\Games\Kronos\World of Warcraft\Arctium WoW Launcher.exe" -GameArgs "--staticseed --version=ClassicEra"`.

`play.bat` runs `play.ps1` with the PowerShell execution policy bypassed for that script only.
The script's parameters (`-ProxyDir`, `-GameExe`, `-GameArgs`, `-TimeoutSeconds`,
`-KeepProxy`, `-NoPortalFix`) are documented in the header of `play.ps1`.

**Expected result:** `C:\Games\Kronos\Hermes` contains `play.bat` and `play.ps1`.

---

## Running the proxy and the game

### With the play scripts

1. Double-click `C:\Games\Kronos\Hermes\play.bat`. A console window opens and logs each
   action.
2. Wait for the line:

   ```
   [play] proxy is ready on 127.0.0.1:1119
   ```

   The game starts immediately after this line. The console window must remain open for the
   duration of the session.
3. At the login screen, enter the account name and password, then select a realm and a
   character.
4. To end the session, exit the game. The console window prints
   `[play] stopping the proxy...` and closes when the proxy has exited.

On the first run, the script also writes `SET portal "127.0.0.1:1119"` to the client's
`WTF\Config.wtf` (a copy of the previous file is kept as `Config.wtf.bak`) and verifies that no
earlier proxy instance holds the ports. On subsequent runs, the same double-click starts the
session.

The proxy's console output for the most recent session is saved to `Hermes\play-console.log`.

### Manually

The following performs the same sequence without the scripts.

1. **Direct the client to the proxy.** Open
   `C:\Games\Kronos\World of Warcraft\_classic_era_\WTF\Config.wtf` in Notepad. Set the
   `SET portal` line to the following, adding the line if it is absent:

   ```
   SET portal "127.0.0.1:1119"
   ```

   If the `WTF` folder does not exist, create it and create `Config.wtf` containing that line.
   `1119` is the proxy's `BNetPort`; the two values must match.
2. **Start the proxy.** Double-click `C:\Games\Kronos\Hermes\JimsProxy.exe`. A console window
   opens. Wait for the line:

   ```
   Starting WorldSocket service
   ```

   This is the last of the proxy's four listeners. The client cannot connect before it appears.
3. **Start the game.** Double-click `WowClassic_ForCustomServers.exe` (or start the Arctium
   launcher as described under [Client executable options](#client-executable-options)).
4. **Log in** with the account name and password.
5. **End the session.** Exit the game, then close the proxy's console window. Closing the
   window, `Ctrl+C`, and `taskkill` without `/F` all allow the proxy to finish writing its log;
   `taskkill /F` does not.

---

## Installing the JimsPlus addon (optional)

JimsPlus is an in-game addon distributed with the proxy (the `Addons\JimsPlus` folder from
Step 2). It provides client-side fixes for cast bars, mail and name display, taxi and pet
behaviour, and an options panel (`/jp` in game) for proxy features. The addon and the proxy
communicate over a versioned channel; the proxy disables the channel when the versions do not
match, so the addon must be updated together with the proxy.

**PowerShell**

```powershell
Copy-Item -Recurse -Force "C:\Games\Kronos\Hermes\Addons\JimsPlus" "C:\Games\Kronos\World of Warcraft\_classic_era_\Interface\AddOns\JimsPlus"
```

**File Explorer:** copy the `JimsPlus` folder from `Hermes\Addons` into
`_classic_era_\Interface\AddOns`.

**Expected result:** `_classic_era_\Interface\AddOns\JimsPlus\JimsPlus.toc` exists.

---

## Updating

A manual installation is not updated automatically. Releases are listed on the
[releases page](https://github.com/jameopotato/jimsproxy/releases).

1. Exit the game and confirm that no `JimsProxy.exe` process is running (Task Manager,
   `Ctrl+Shift+Esc`).
2. Download and extract the new archive over the existing folder. The archive does not contain
   `HermesProxy.config` or `AccountData`, so both are left unchanged.

   ```powershell
   Invoke-WebRequest -Uri "https://jimothy.cc/proxy/stable/latest" -OutFile "$env:TEMP\jimsproxy-bundle.zip"
   Expand-Archive -Path "$env:TEMP\jimsproxy-bundle.zip" -DestinationPath "C:\Games\Kronos\Hermes" -Force
   ```

   For the beta channel, use `https://jimothy.cc/proxy/beta/latest`.
3. If JimsPlus is installed, copy the updated addon again as described in
   [Installing the JimsPlus addon](#installing-the-jimsplus-addon-optional).

The installed version is recorded in `Hermes\manifest.json` and printed on the `Version` line
at proxy startup. New versions may introduce configuration keys; every key has a built-in
default, so an existing configuration file remains valid. The
[configuration reference](configuration.md) lists all keys.

## Uninstalling

1. Exit the game and stop the proxy.
2. Delete the `Hermes` folder. `AccountData` inside it holds per-account state; keep a copy if a
   later reinstall should retain it.
3. Optionally delete `_classic_era_\Interface\AddOns\JimsPlus`.
4. Optionally remove the `SET portal` line from `_classic_era_\WTF\Config.wtf`. With the line
   present and no proxy running, the client cannot connect to any server.

---

## Troubleshooting

**"World Server is Down", or the realm list does not appear.**
The client is not connected to the proxy. Verify that `WTF\Config.wtf` contains
`SET portal "127.0.0.1:1119"`, that the port matches `BNetPort` in `HermesProxy.config`, and
that the proxy printed `Starting WorldSocket service` (or the script printed
`proxy is ready`) before the game started.

**The proxy exits immediately, or reports that a port is in use.**
The proxy requires four free TCP ports on the local machine: **1119, 8081, 8084, 8086**. A
common cause is an earlier proxy instance that is still running. In Task Manager
(`Ctrl+Shift+Esc`), end `JimsProxy.exe` (or `HermesProxy.exe` from an earlier installation)
and start again. `play.bat` reports the name of the process holding a port. A Windows
`WSAEACCES (10013)` error indicates that the port is owned by another program, reserved by
Windows, or held by a proxy running with higher privileges.

**`Config loading failed`.**
`HermesProxy.config` is not in the same folder as `JimsProxy.exe`, or was saved as
`HermesProxy.config.txt`. Repeat [Step 3](#step-3-download-the-configuration-file). If the
message is `The verification of the config failed`, the preceding line names the invalid
value (a malformed `ClientSeed`, an unsupported build, or a port outside 1–65535).

**Login fails, or `Unsupported ClientBuild` is printed.**
The client build is not 42597, or the unmodified `WowClassic.exe` was started without the
Arctium launcher. See [Verifying the client build](#verifying-the-client-build) and
[Client executable options](#client-executable-options).

**A yellow "update available" message appears at proxy startup.**
The message comes from a version check against the archived upstream project and can be
ignored. The play scripts start the proxy with `--no-version-check`, which disables it.

**Quest progress or settings are wrong after an update.**
The `Hermes` folder was deleted and re-created, which removed `AccountData`. Restore the folder
from the previous installation. The procedure in [Updating](#updating) preserves it.

**Antivirus or SmartScreen blocks the proxy.**
See the note in [Step 2](#step-2-download-and-extract-the-proxy).

> **Note:** unlike upstream HermesProxy, JimsProxy ignores the client's *Optimize Network for
> Speed* setting on the local connection (it has no effect over loopback). The setting does not
> need to be enabled to avoid disconnects.

---

## Appendix: Linux and macOS (community-supported)

Windows is the tested platform. On Linux and macOS, the proxy builds and runs natively; the
game client does not run natively on either. Neither platform is tested by the project.
Linux-specific proxy fixes have been made from user reports. No prebuilt Linux or macOS proxy
binaries are published, so both procedures build the proxy from source.

### Linux (community-supported)

**Proxy.** Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0),
then build and publish:

```bash
git clone https://github.com/jameopotato/jimsproxy.git
cd jimsproxy
dotnet publish HermesProxy --configuration Release --use-current-runtime -p:UsePublishBuildSettings=true -o ~/kronos/Hermes
chmod +x ~/kronos/Hermes/JimsProxy
```

The output is a self-contained `JimsProxy` executable with `CSV/` and `HermesProxy.config`
beside it. The source build includes the configuration file, so Step 3 does not apply. Set the
server address as in Step 4:

```bash
sed -i 's|<add key="ServerAddress" value="[^"]*"|<add key="ServerAddress" value="login.twinstar-wow.com"|' ~/kronos/Hermes/HermesProxy.config
```

To verify the build, run `cd ~/kronos/Hermes && ./JimsProxy --no-version-check`; the output
should include `Starting WorldSocket service`. Stop it with `Ctrl+C`.

**Game client.** The client runs under Wine or Proton. Configurations reported to work: Wine 9
or later, or GE-Proton, with DXVK; started through Lutris, Bottles, or Steam (as a non-Steam
game); in a dedicated prefix; without the Battle.net application. The proxy listens on
`127.0.0.1`, which Wine shares with the host, so no network configuration is required. If the
game window remains black or the client crashes at start, set `SET gxApi "D3D11"` in the
client's `WTF/Config.wtf`. Two Linux-specific client crashes (on flight-path landing and at world
entry) were caused by packet translation and are fixed in current proxy versions.

Stone Tavern's experimental [Better Client](https://stonetavern.app/betterclient) is a
reference implementation of this setup: a Linux package (about 8 GB, client included) that
bundles JimsProxy built natively for Linux, configured for their realm, behind a one-click play
script. It targets their own realm; the operating-system setup applies, the server settings do
not.

**Play.** `scripts/play.sh` performs the same session sequence as `play.bat`:

```bash
curl -L -o ~/kronos/Hermes/play.sh https://raw.githubusercontent.com/jameopotato/jimsproxy/master/scripts/play.sh
chmod +x ~/kronos/Hermes/play.sh
~/kronos/Hermes/play.sh --game-exe "$HOME/kronos/World of Warcraft/_classic_era_/WowClassic_ForCustomServers.exe"
```

By default the script starts the game with `wine "<game-exe>"`. When Lutris, Bottles, or Steam
starts the game, pass that command instead; the script then waits for the WoW process to exit:
`play.sh --game-cmd 'lutris lutris:rungameid/12'`. `play.sh --help` lists all options. Without
the script: run `./JimsProxy` in a terminal, wait for `Starting WorldSocket service`, start the
client, and stop the proxy with `Ctrl+C` after the game exits.

### macOS (community-supported)

**Proxy.** The proxy builds and runs natively on Apple Silicon and Intel with the same
`dotnet publish` command as on Linux. `play.sh` runs on macOS for the proxy side; pass the
command that starts the game with `--game-cmd`. If the proxy exits at startup with
`AesGcm is not supported on your platform`, it prints the remedy on the following lines
(`brew install openssl@3`, then start it with
`DYLD_LIBRARY_PATH=/opt/homebrew/opt/openssl@3/lib`).

**Game client.** The project has not tested a macOS configuration. Stone Tavern's experimental
[Better Client](https://stonetavern.app/betterclient) includes a macOS package (about 8 GB)
that runs the Windows 1.14.2 client through Apple's Game Porting Toolkit with JimsProxy built
natively for macOS. Reproducing that configuration for Kronos requires the Game Porting Toolkit
(or a Wine-based tool such as CrossOver or Whisky), the game client, and the proxy configured as
in Step 4. Reports of working configurations are welcome as issues.

---

## Reference

### Command line flags

Flags override `HermesProxy.config` for the current run only.

```
JimsProxy --config MyServer.config
JimsProxy --set ServerAddress=logon.example.com --set ServerPort=3724
JimsProxy --no-version-check
JimsProxy --metrics
```

| Flag | Description |
|---|---|
| `--config <path>` | Use a different configuration file (default: `HermesProxy.config` beside the executable) |
| `--set Key=Value` | Override a single configuration value; repeatable |
| `--no-version-check` | Skip the update check at startup (up to 15 s when offline); the play scripts pass it |
| `--metrics` | Print per-opcode latency metrics every 60 s (diagnostics) |

To use more than one server, keep one configuration file per server and select it with
`--config`.

### Chat commands

Entered in any chat box in game:

| Command | Effect |
|---|---|
| `!qcomplete <questId>` | Mark a quest complete in the proxy's tracking |
| `!quncomplete <questId>` | Reverse `!qcomplete` |

### Files and ports

Files written by the proxy, all in the folder containing the executable:

| Path | Contents |
|---|---|
| `Logs\jimsproxy-<date>-<time>.jsonl` | Structured diagnostic log, one per session (`StructuredLog`, enabled by default) |
| `AccountData\<account>\` | Per-account state that the 1.14 client expects the server to persist |
| `PacketsLog\` | Full packet captures, written only when `PacketsLog` is `true` |
| `play-console.log` | Console output of the most recent session, written by the play scripts |

Listeners, all bound to `127.0.0.1`:

| Key | Default | Purpose |
|---|---|---|
| `BNetPort` | `1119` | Login. The port referenced by `SET portal` in `Config.wtf` |
| `RestPort` | `8081` | Login REST calls |
| `RealmPort` | `8084` | Realm list and character selection |
| `InstancePort` | `8086` | Game world. Its `Starting WorldSocket service` line is the ready signal |

To run two proxy instances at once (multiboxing), use a second copy of the `Hermes` folder with
a different port set (the launcher's second instance uses `1120 / 8082 / 8085 / 8087`) and set
that client's portal to the second `BNetPort`.

### Configuration keys

[configuration.md](configuration.md) documents all 40 keys the proxy reads, their defaults, and
their intended use.

---

## Reporting a bug

Open an issue at [github.com/jameopotato/jimsproxy/issues](https://github.com/jameopotato/jimsproxy/issues)
and include:

- The proxy version: the `Version` line printed at startup (also in `play-console.log`), or
  the `version` field in `manifest.json`; and whether the proxy was built from source.
- The `Logs\jimsproxy-*.jsonl` file for the session. If more detail is requested, set
  `DebugOutput` to `true` in `HermesProxy.config` and reproduce the problem. If a packet
  capture is requested, set `PacketsLog` to `true` for that session and reset it afterwards.
- The operating system and, on Linux or macOS, the tool used to run the client (Wine, Proton,
  CrossOver) and its version.

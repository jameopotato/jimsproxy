# Running JimsProxy without the launcher

This is the manual path: you run the proxy yourself and start the game yourself. It is the
only path on Linux and macOS, and the right one for custom setups on Windows.

If you are on Windows and just want to play, use the **Classic WoW Launcher** instead. It
downloads or repairs the game client, installs and updates the proxy, and manages addons.
Get it from [jimothy.cc/install](https://jimothy.cc/install); the full guide is
[classic-114-launcher/docs/INSTALL.md](https://github.com/jameopotato/classic-114-launcher/blob/master/docs/INSTALL.md).

| | Launcher (Windows) | Manual (this page) |
|---|---|---|
| Setup | Guided wizard | Edit one config value and one `Config.wtf` line |
| Proxy updates | Automatic, stable or beta | You re-download |
| Game client | Downloaded, copied or repaired for you | **You supply it** |
| Addons, keybind import, repair, multibox | Included | Not included (JimsPlus can be installed by hand) |
| Linux / macOS | Not supported | Community-supported |

**Support status.** Windows is the tested platform. Linux and macOS are community-supported:
the proxy builds and runs natively on both, the game client does not, and the project does
not test either. Reports from Linux players have led to real fixes (see the changelog), so
they are welcome, but expect to do some of the work yourself.

Contents

- [What you need](#what-you-need)
- [Windows](#windows)
- [Linux (community-supported)](#linux-community-supported)
- [macOS (community-supported)](#macos-community-supported)
- [The launch scripts](#the-launch-scripts)
- [Keeping it updated](#keeping-it-updated)
- [The JimsPlus addon](#the-jimsplus-addon)
- [Command line flags](#command-line-flags)
- [Chat commands](#chat-commands)
- [Files and ports](#files-and-ports)
- [Troubleshooting](#troubleshooting)
- [Reporting a bug](#reporting-a-bug)

---

## What you need

### 1. The proxy

A folder containing:

```
JimsProxy.exe          (Windows)  /  JimsProxy  (Linux, macOS)
HermesProxy.config
CSV/                   game data tables, required
```

All three must sit **in the same folder**. The proxy switches its working directory to
wherever its executable lives, so it always reads the config and `CSV/` from beside itself,
however you start it.

> The config file is named **`HermesProxy.config`**, not `JimsProxy.config`, even though the
> binary is `JimsProxy`. The project kept upstream's filename for compatibility.

Where to get it:

- **Windows, direct download.** The same bundle the launcher installs, straight from
  jimothy.cc:

  | Channel | Link |
  |---|---|
  | Latest stable | <https://jimothy.cc/proxy/stable/latest> |
  | Latest beta | <https://jimothy.cc/proxy/beta/latest> |

  The bundle contains `JimsProxy.exe`, `CSV/`, the JimsPlus addon under `Addons/JimsPlus/`,
  and a `manifest.json` with the version. **It does not contain `HermesProxy.config`**: the
  launcher writes its own, so the bundle ships without one and the proxy stops with
  `Config loading failed` if it is missing. Download the config from the repository,
  [`HermesProxy/HermesProxy.config`](https://raw.githubusercontent.com/jameopotato/jimsproxy/master/HermesProxy/HermesProxy.config)
  (right-click, *Save as*), and put it beside `JimsProxy.exe`.
- **Windows, GitHub release.** [The latest release](https://github.com/jameopotato/jimsproxy/releases/latest)
  has `JimsProxy-v<version>-win-x64.zip` plus `checksums-sha256.txt`; verify the download with
  `scripts/verify-checksums.ps1` or `Get-FileHash`. If the zip has no `HermesProxy.config`,
  fetch it as above.
- **Linux and macOS:** [build from source](../README.md#building-from-source). Releases do
  not ship Linux or macOS binaries yet. CI does build them on every push (the *Build Proxy*
  workflow produces `HermesProxy-ubuntu-*` and `HermesProxy-macos-universal-*` artifacts),
  but workflow artifacts need a GitHub login and expire, so treat them as a convenience,
  not a distribution channel.

### 2. A game client, which you supply

JimsProxy does not distribute game files, and this guide does not link to any.

| | |
|---|---|
| Version | **WoW Classic Era 1.14.2** |
| Build | **42597** |
| Executable | **`WowClassic_ForCustomServers.exe`**, or the stock `WowClassic.exe` started through the Arctium Launcher |

On its own, the stock `WowClassic.exe` from Battle.net only talks to Blizzard's servers, and
no proxy changes that. Two things make it accept a custom server:

- **`WowClassic_ForCustomServers.exe`**, a patched build of the client. This is what the
  launcher installs and what the rest of this guide assumes.
- **The [Arctium WoW Launcher](https://github.com/Arctium/WoW-Launcher)** with the unpatched
  `WowClassic.exe`. It patches the client in memory at start. Put it in the main game folder
  (the one that contains `_classic_era_`) and start it with
  `--staticseed --version=ClassicEra`. `--staticseed` makes the client use the fixed auth seed
  that the proxy's shipped `ClientSeed` value matches, so leave that key alone.

Either way the client's build must match `ClientBuild` in the proxy config exactly (`42597`),
or login fails. Other 1.14 builds are listed in the config's comments but are not tested.

The examples below use the launcher's folder layout, which the scripts auto-detect. Any
layout works if you pass the paths explicitly.

```
<root>/
├── Hermes/                              the proxy folder from step 1
└── World of Warcraft/
    └── _classic_era_/
        ├── WowClassic_ForCustomServers.exe
        ├── Data/
        ├── Interface/AddOns/
        └── WTF/Config.wtf
```

### 3. A Kronos account

Create one at [kronos-wow.com](https://www.kronos-wow.com). You log in with the account name
and password at the normal WoW login screen.

---

## Windows

### Step 1: Point the proxy at the server

Open `HermesProxy.config` in a text editor and set `ServerAddress`:

```xml
<add key="ServerAddress" value="login.twinstar-wow.com" />
```

Kronos runs more than one realm cluster. Use the host of the one you play on, the same
thing the launcher's *Server* dropdown picks:

| Launcher name | `ServerAddress` |
|---|---|
| Kronos | `login.twinstar-wow.com` |
| Kronos 2 | `login2.twinstar-wow.com` |
| Kronos 3 | `login3.twinstar-wow.com` |

For any other vanilla 1.12 server, use its logon address, the same thing you would put in
`SET REALMLIST`.

Everything else ships ready to go. In particular, do **not** touch `ClientBuild` (already
`42597`), `ServerBuild` (`auto` picks 1.12.1 for a 1.14 client; the launcher pins `5875`,
which is the same thing), or `ClientSeed` (a fallback; the real per-build seeds load from
`CSV/BuildAuthSeeds.csv`). Leave `ServerType` on `Kronos`. Every key is explained in
[configuration.md](configuration.md).

### Step 2: Point the client at the proxy

Edit `World of Warcraft\_classic_era_\WTF\Config.wtf` (create the `WTF` folder and the file
if they do not exist yet) so it contains:

```
SET portal "127.0.0.1:1119"
```

`1119` is the proxy's `BNetPort`. If you change one, change both; a mismatch is the single
most common "it just won't connect". Classic Era has no `realmlist.wtf`; the portal line is
the whole story. The launch script in step 3 checks and fixes this line for you.

### Step 3: Start the proxy, then the game

**With the script (recommended).** Copy `scripts\play.ps1` and `scripts\play.bat` from this
repository into your `Hermes\` folder and double-click `play.bat`. It checks that the
proxy's ports are free, fixes the portal line, starts the proxy, waits for it to be ready,
starts the game, and shuts the proxy down cleanly when you close the game. Details in
[The launch scripts](#the-launch-scripts).

**By hand.**

1. Run `JimsProxy.exe`. A console window opens. Wait for this line:

   ```
   Starting WorldSocket service
   ```

   It is the last of the four listeners to come up, so once you see it everything is bound.
   Do not start the game before it appears.
2. Start `WowClassic_ForCustomServers.exe` (or the Arctium Launcher with
   `--staticseed --version=ClassicEra`).
3. Log in with your Kronos account name and password, pick your realm and character.
4. When you are done playing, close the game first, then close the proxy's console window.
   The proxy handles the window close, `Ctrl+C` and a normal `taskkill` gracefully and
   flushes its diagnostic log. Only `taskkill /F` skips that.

**SmartScreen and antivirus.** `JimsProxy.exe` is a large, unsigned, self-contained .NET
program that opens listening sockets, a shape that draws false positives. If SmartScreen
blocks it, click *More info*, then *Run anyway*. If an antivirus quarantines it, add the
`Hermes\` folder as an exception rather than restoring the one file; a restore on its own
often does not hold. Build from source if you would rather not take anyone's word for it.

---

## Linux (community-supported)

The proxy runs natively. The game client runs under Wine or Proton. Nothing on the network
side needs configuring: the proxy listens on `127.0.0.1`, and Wine shares the host's
loopback interface, so the client reaches it exactly as on Windows.

### Step 1: Build the proxy

Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) for your
distribution, then:

```bash
git clone https://github.com/jameopotato/jimsproxy.git
cd jimsproxy
dotnet publish HermesProxy --configuration Release --use-current-runtime -p:UsePublishBuildSettings=true -o ~/kronos/Hermes
chmod +x ~/kronos/Hermes/JimsProxy
```

That produces a self-contained, single-file `JimsProxy` with `CSV/` and `HermesProxy.config`
beside it. No .NET runtime is needed on the machine that runs it. Check that it starts:

```bash
cd ~/kronos/Hermes && ./JimsProxy --no-version-check
```

You should see `Starting WorldSocket service`. Stop it with `Ctrl+C`.

### Step 2: Configure

Same two edits as on Windows: `ServerAddress` in `HermesProxy.config`
([step 1](#step-1-point-the-proxy-at-the-server)) and `SET portal "127.0.0.1:1119"` in the
client's `WTF/Config.wtf` ([step 2](#step-2-point-the-client-at-the-proxy)). The launch
script fixes the portal line for you.

### Step 3: The game client under Wine or Proton

This is the part the project does not test. What Linux players report working:

- A recent Wine (9 or newer) or GE-Proton, with DXVK. Lutris, Bottles, or Steam with
  `WowClassic_ForCustomServers.exe` added as a non-Steam game all work; use whichever you
  already know.
- Give the client its own prefix. Run `WowClassic_ForCustomServers.exe` directly; there is no
  Battle.net app involved.
- If the game window stays black or the client crashes at start, force DirectX 11 in
  `WTF/Config.wtf`:

  ```
  SET gxApi "D3D11"
  ```

- Keep the proxy current. Two Linux-specific client crashes (on flight-path landing and at
  world entry) were caused by packet translation and fixed in the proxy, not in Wine.

For a worked example of a 1.14 client packaged for Linux and macOS, look at Stone Tavern's
"better client" (their ready-to-play 1.14.2 package with launch scripts) at
[stonetavern.app/betterclient](https://stonetavern.app/betterclient). It is built for their
own realm through HermesProxy, so borrow the operating-system setup, not the server settings.

### Step 4: Start

Copy `scripts/play.sh` next to `JimsProxy` (or run it from the repository) and:

```bash
chmod +x play.sh
./play.sh --proxy-dir ~/kronos/Hermes --game-exe "~/kronos/World of Warcraft/_classic_era_/WowClassic_ForCustomServers.exe"
```

By default it starts the game with `wine "<game-exe>"`. If Lutris, Bottles or Steam starts
your game, hand the script that command instead and it will wait for the `WowClassic` process
to exit:

```bash
./play.sh --proxy-dir ~/kronos/Hermes --game-cmd 'lutris lutris:rungameid/12'
```

Or by hand: start `./JimsProxy` in one terminal, wait for `Starting WorldSocket service`,
start the client from your Wine setup, and `Ctrl+C` the proxy after you quit the game. `kill`
(SIGTERM) and closing the terminal (SIGHUP) are handled gracefully too.

---

## macOS (community-supported)

**Proxy:** builds and runs natively on Apple Silicon and Intel with the same
`dotnet publish ... --use-current-runtime` command as on Linux. If it exits at startup with
`AesGcm is not supported on your platform`, it prints the fix on the next lines
(`brew install openssl@3`, then start it with `DYLD_LIBRARY_PATH=/opt/homebrew/opt/openssl@3/lib`).
`scripts/play.sh` works on macOS for the proxy side; pass the command that starts your game
with `--game-cmd`.

**Game client:** there is no confirmed way to run the custom-servers client on macOS at the
time of writing. People either run the Windows client through CrossOver or Wine on the Mac,
or play from a Windows machine with the launcher. Stone Tavern's "better client" page at
[stonetavern.app/betterclient](https://stonetavern.app/betterclient), with its macOS launcher, is
the closest reference for running a 1.14 client on a Mac. If you get it working against Kronos, open an issue with
your steps and this section will be updated.

---

## The launch scripts

`scripts/play.ps1` + `scripts/play.bat` (Windows) and `scripts/play.sh` (Linux, macOS) are
the manual-install equivalent of the launcher's Play button. Copy them next to the proxy
binary, or point them at it. Both do the same things in the same order:

1. Find the proxy and the game executable (launcher layout auto-detected, or pass paths).
2. Refuse to start if another process already listens on one of the proxy's four ports,
   naming it, which catches the "a previous proxy is still running" case.
3. Set `SET portal "127.0.0.1:<BNetPort>"` in `WTF/Config.wtf`, keeping a `Config.wtf.bak`
   (skip with `-NoPortalFix` / `--no-portal-fix`).
4. Start the proxy with its console output shown and saved to `play-console.log`, and
   wait for `Starting WorldSocket service` (60 s by default).
5. Start the game and wait for it to close. If what you pointed the script at is a launcher
   (Arctium, or any wrapper that returns as soon as WoW is up), it waits for the WoW process
   that launcher started instead.
6. Ask the proxy to shut down cleanly (so `Logs/jimsproxy-*.jsonl` is flushed), and only
   force-close it if it has not exited after 5 seconds. `-KeepProxy` / `--keep-proxy` leaves
   it running instead.

| | `play.ps1` (Windows) | `play.sh` (Linux, macOS) |
|---|---|---|
| Proxy folder | `-ProxyDir D:\Kronos\Hermes` | `--proxy-dir ~/kronos/Hermes` |
| Game executable | `-GameExe <path>` | `--game-exe <path>` |
| Game arguments (Arctium route) | `-GameArgs '--staticseed --version=ClassicEra'` | part of `--game-cmd` |
| Custom game command | n/a | `--game-cmd 'lutris lutris:rungameid/12'` |
| Ready timeout | `-TimeoutSeconds 90` | `--timeout 90` |
| Leave the proxy running | `-KeepProxy` | `--keep-proxy` |
| Do not touch Config.wtf | `-NoPortalFix` | `--no-portal-fix` |

`play.bat` runs `play.ps1` with the execution policy bypassed for that one script, so a
double-click works on a stock Windows install. To run `play.ps1` from a PowerShell prompt
without the wrapper: `powershell -ExecutionPolicy Bypass -File .\play.ps1`.

The scripts are new. `play.sh` has been exercised against a stand-in proxy on Linux;
`play.ps1` follows the same design but has had less real-world use. If either misbehaves,
the by-hand steps above always work; please open an issue with `play-console.log` attached.

---

## Keeping it updated

Manual installs do not update themselves. When a new release appears:

1. Quit the game and stop the proxy.
2. Download the new bundle from the [stable or beta link](#1-the-proxy) (or the GitHub
   release, and verify its checksum) and replace `JimsProxy.exe`, the `CSV/` folder and
   `Addons/`. Keep your edited `HermesProxy.config`; compare it against the repository's
   current one for keys that were added (the [configuration reference](configuration.md)
   lists every key and its default, so a missing key is never fatal).
3. Keep `AccountData/`. It holds per-account state (quest tracking and similar); losing it
   is what makes "my quest log looks wrong after updating" happen.
4. Update the [JimsPlus addon](#the-jimsplus-addon) to the same version.

Release notes are on the [releases page](https://github.com/jameopotato/jimsproxy/releases)
and in [CHANGES.md](../CHANGES.md).

---

## The JimsPlus addon

JimsPlus is a small in-game addon that ships with the launcher and pairs with the proxy:
client-side fixes (cast bars, mail and name display, taxi and pet quirks) plus an options
panel for proxy features. It is optional but recommended, and it must match the proxy
version: the proxy and the addon talk over a versioned in-game channel, and the proxy
disables that channel if the addon does not answer correctly.

The direct download bundle includes it as `Addons/JimsPlus/`: copy that folder into
`World of Warcraft\_classic_era_\Interface\AddOns\` so you end up with
`Interface\AddOns\JimsPlus\JimsPlus.toc`. From source, it is `Addons/JimsPlus` at the same tag
as the proxy you run. In game, `/jp` opens its options.

---

## Command line flags

All flags override `HermesProxy.config` for that run only.

```
JimsProxy --config MyServer.config
JimsProxy --set ServerAddress=logon.example.com --set ServerPort=3724
JimsProxy --no-version-check
JimsProxy --metrics
```

| Flag | Description |
|---|---|
| `--config <path>` | Use a different config file (default: `HermesProxy.config` beside the executable) |
| `--set Key=Value` | Override a single config value; repeatable |
| `--no-version-check` | Skip the update check at startup. Saves up to 15 s when offline; the launch scripts pass it |
| `--metrics` | Print per-opcode latency metrics every 60 s (diagnostics only) |

Running against more than one server? Keep a config per server and pick with `--config`.

## Chat commands

Typed into any chat box while playing:

| Command | Effect |
|---|---|
| `!qcomplete <questId>` | Mark a quest complete in the proxy's tracking |
| `!quncomplete <questId>` | Undo the above |

---

## Files and ports

Everything the proxy writes goes beside its executable:

| Path | Contents |
|---|---|
| `Logs/jimsproxy-<date>-<time>.jsonl` | Structured diagnostic log, one per session (`StructuredLog`, on by default). Attach to bug reports. |
| `AccountData/<account>/` | Per-account state the 1.14 client expects the server to remember. Keep it across updates. |
| `PacketsLog/` | Full packet captures, only when `PacketsLog=true`. Large. |
| `play-console.log` | Console output of the last run, written by the launch scripts. |

All four listeners bind to `127.0.0.1` and all four ports must be free:

| Key | Default | Used for |
|---|---|---|
| `BNetPort` | `1119` | Battle.net login. **What `SET portal` in `Config.wtf` points at.** |
| `RestPort` | `8081` | Login REST calls |
| `RealmPort` | `8084` | Realm list and character select |
| `InstancePort` | `8086` | The game world. Its `Starting WorldSocket service` line is the ready signal |

Running two proxies at once (multiboxing)? Give the second one its own copy of the folder
with a different set, for example `1120 / 8082 / 8085 / 8087` (what the launcher uses for
its Alt client), and point that client's portal at the second `BNetPort`.

---

## Troubleshooting

**"World Server is Down", or the client never reaches the realm list.**
The portal and `BNetPort` disagree. `SET portal "127.0.0.1:1119"` in `WTF/Config.wtf` must
match `BNetPort` in `HermesProxy.config`. Also confirm the proxy printed
`Starting WorldSocket service` before you started the game.

**The proxy exits immediately, or reports a bind or port error.**
It needs four free ports on `127.0.0.1`: **1119, 8081, 8084, 8086**. Almost always this is a
previous proxy still running: check Task Manager for `JimsProxy.exe` (or `HermesProxy.exe`
from an old install) and end it, or `pgrep -fl JimsProxy` on Linux and macOS. The launch
scripts name the process holding the port. A leftover proxy is worth taking seriously: it
will keep serving your session quite happily, and you will think you are running a build you
are not. A Windows `WSAEACCES (10013)` means something else owns the port: another program, a
Windows port reservation, or a proxy running with higher privileges than you.

**`Unsupported ClientBuild`, login fails, or the client complains about the version.**
`ClientBuild` does not match your client. 1.14.2 is `42597`. Confirm the executable you are
launching is really `WowClassic_ForCustomServers.exe` and really build 42597, or that you
started `WowClassic.exe` through the Arctium Launcher with `--staticseed`; a plain
`WowClassic.exe` never reaches the proxy at all.

**`Config loading failed`.**
`HermesProxy.config` is not beside the executable (the direct download bundle ships without
one; see [What you need](#1-the-proxy)), or `--config` points at a file that does not exist. `The verification of the config failed` is printed right after the reason: a
malformed `ClientSeed`, an unsupported build, or a port outside 1-65535.

**A yellow "update available" banner at startup.**
Harmless. The startup check compares against the archived upstream project and can be
skipped with `--no-version-check`.

**Quest progress or settings look wrong after updating.**
You replaced the whole folder and lost `AccountData/`. Copy it over from the old folder.

**Linux: the client crashes at world entry or when landing from a flight path.**
Update the proxy; both crashes were fixed proxy-side (5.1.4 and 5.2.1-beta.4).

**Linux: black window or crash at start under Wine or Proton.**
Force DirectX 11 with `SET gxApi "D3D11"` in `WTF/Config.wtf`, and make sure DXVK is in
the prefix (Proton and Lutris include it).

**macOS: `AesGcm is not supported on your platform`.**
Follow the two lines the proxy prints right after that message.

**Antivirus flags the executable.**
See [SmartScreen and antivirus](#step-3-start-the-proxy-then-the-game) above.

> Unlike upstream HermesProxy, JimsProxy ignores the client's *Optimize Network for Speed*
> setting on the local connection (it is a no-op over loopback), so you do not need to keep
> it enabled to avoid disconnects.

---

## Reporting a bug

Open an issue on [github.com/jameopotato/jimsproxy](https://github.com/jameopotato/jimsproxy/issues)
with:

- The proxy version, printed on the second line at startup (`Version 2026-…`), and whether
  you built it yourself.
- The `Logs/jimsproxy-*.jsonl` file for the session. If a developer asks for more detail,
  set `DebugOutput` to `true` and reproduce; if they ask for a packet capture, set
  `PacketsLog` to `true` for that one session and turn it back off afterwards.
- Your operating system and, on Linux or macOS, how you run the client (Wine, Proton,
  CrossOver, and the version).

Launcher users have a *Send Bug Report* button in the launcher's Help tab that attaches all
of this automatically.

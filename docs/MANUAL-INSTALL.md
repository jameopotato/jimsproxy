# Manual install: run JimsProxy without the launcher

This guide takes you from the download link to the login screen, one step at a time, with the
exact things to click and the exact commands to paste. It assumes you have never done this
before. It is written for Windows; Linux and macOS are in the
[appendix](#appendix-linux-and-macos-community-supported).

> **The easy way.** On Windows, the **Classic WoW Launcher** at
> [jimothy.cc/install](https://jimothy.cc/install) does everything on this page for you, and
> also downloads or repairs the game client, keeps the proxy updated and manages addons. If you
> just want to play, use that and stop reading here.

Contents

- [Before you start](#before-you-start)
- [Step 1: Set up the folders](#step-1-set-up-the-folders)
- [Step 2: Download the proxy](#step-2-download-the-proxy)
- [Step 3: Get the config file](#step-3-get-the-config-file)
- [Step 4: Point the proxy at Kronos](#step-4-point-the-proxy-at-kronos)
- [Step 5: Get the play scripts](#step-5-get-the-play-scripts)
- [Step 6: Play](#step-6-play)
- [Running it by hand (without the scripts)](#running-it-by-hand-without-the-scripts)
- [The JimsPlus addon (optional)](#the-jimsplus-addon-optional)
- [Updating](#updating)
- [Troubleshooting](#troubleshooting)
- [Appendix: Linux and macOS (community-supported)](#appendix-linux-and-macos-community-supported)
- [Reference](#reference)
- [Reporting a bug](#reporting-a-bug)

---

## Before you start

You need three things.

**1. A WoW Classic Era 1.14.2 client, build 42597. You supply this.** JimsProxy does not
distribute game files and this guide does not link to any. The build must be exactly 42597 or
login fails. To check: right-click the game's `.exe`, choose *Properties*, open the *Details*
tab and read *File version*; it should say `1.14.2.42597`.

The client also has to be willing to talk to a custom server. On its own, the stock
`WowClassic.exe` from Battle.net only talks to Blizzard. You need one of:

- **`WowClassic_ForCustomServers.exe`**, a patched build of the client. This guide assumes it.
- **The [Arctium WoW Launcher](https://github.com/Arctium/WoW-Launcher)** with the unpatched
  `WowClassic.exe`. It patches the client in memory each time it starts it. Put it in the game
  folder that contains `_classic_era_` and start it with `--staticseed --version=ClassicEra`.
  Where this guide says "start `WowClassic_ForCustomServers.exe`", start Arctium instead.

**2. A Kronos account.** Create one at [kronos-wow.com](https://www.kronos-wow.com). You will
type its name and password into the normal WoW login screen.

**3. Windows 10 or 11, 64-bit**, with a few hundred MB free next to the game.

**About the commands in this guide.** Some steps offer a PowerShell block as an alternative to
clicking around. To open PowerShell: press the Windows key, type `powershell`, press Enter.
Paste a whole block at once (right-click pastes) and press Enter. Nothing here needs
administrator rights.

**About the paths.** The examples use `C:\Games\Kronos` as the folder that holds everything,
with the game client inside it at `C:\Games\Kronos\World of Warcraft\_classic_era_\`. Wherever
you see those paths, use your own.

---

## Step 1: Set up the folders

The proxy lives in a folder called `Hermes`, next to your `World of Warcraft` folder. This is
the layout the launcher uses too, and the play scripts find the game by it:

```
C:\Games\Kronos\
├── Hermes\                                  ← you create this; the proxy goes here
└── World of Warcraft\                       ← your game client
    └── _classic_era_\
        ├── WowClassic_ForCustomServers.exe
        ├── Data\
        ├── Interface\
        └── WTF\
```

1. Open File Explorer and go to the folder that contains your `World of Warcraft` folder
   (`C:\Games\Kronos` in the example).
2. Right-click an empty spot, choose *New → Folder*, and name it `Hermes`.

Or in PowerShell:

```powershell
New-Item -ItemType Directory -Force "C:\Games\Kronos\Hermes"
```

If your client folder is not called `World of Warcraft` but contains `_classic_era_` directly,
that works too: put `Hermes` next to `_classic_era_`. Any other layout also works; you will
just tell the play script where the game is (see [Step 5](#step-5-get-the-play-scripts)).

**Check:** `C:\Games\Kronos\Hermes` exists and is empty, and
`C:\Games\Kronos\World of Warcraft\_classic_era_\WowClassic_ForCustomServers.exe` exists.

---

## Step 2: Download the proxy

The proxy comes as one zip file, the same bundle the launcher installs:

| Channel | Link |
|---|---|
| **Latest stable** (use this) | <https://jimothy.cc/proxy/stable/latest> |
| Latest beta (newest fixes, less tested) | <https://jimothy.cc/proxy/beta/latest> |

**With the browser**

1. Click the stable link. Your browser saves a zip file (named like `hermes-bundle-5.2.0.zip`)
   in your *Downloads* folder.
2. Right-click the zip and choose *Extract All…*.
3. In the *Files will be extracted to this folder* box, delete what is there and type
   `C:\Games\Kronos\Hermes`. Click *Extract*.

**Or with PowerShell** (downloads and extracts in one go):

```powershell
Invoke-WebRequest -Uri "https://jimothy.cc/proxy/stable/latest" -OutFile "$env:TEMP\jimsproxy-bundle.zip"
Expand-Archive -Path "$env:TEMP\jimsproxy-bundle.zip" -DestinationPath "C:\Games\Kronos\Hermes" -Force
```

**Check:** `C:\Games\Kronos\Hermes` now contains `JimsProxy.exe`, a `CSV` folder, an `Addons`
folder and `manifest.json`. If instead you see a single sub-folder in there (for example
`Hermes\hermes-bundle-5.2.0\JimsProxy.exe`), *Extract All* added a folder level: move
everything from that sub-folder up into `Hermes` and delete the empty sub-folder.

> `JimsProxy.exe` is a large, unsigned program that opens network ports, which is exactly the
> shape antivirus tools dislike. If yours quarantines it, restore it and add the whole `Hermes`
> folder as an exception; restoring the one file usually does not hold. If Windows SmartScreen
> shows "Windows protected your PC" when it first runs, click *More info*, then *Run anyway*.

---

## Step 3: Get the config file

The proxy reads its settings from a file called **`HermesProxy.config`** (the name comes from
the project JimsProxy was forked from). The bundle does not include one, because the launcher
writes its own. Without it the proxy stops with `Config loading failed`. Get it from the
repository:

**With PowerShell** (recommended; it puts the file in the right place with the right name):

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jameopotato/jimsproxy/master/HermesProxy/HermesProxy.config" -OutFile "C:\Games\Kronos\Hermes\HermesProxy.config"
```

**Or with the browser:** open
[HermesProxy.config](https://raw.githubusercontent.com/jameopotato/jimsproxy/master/HermesProxy/HermesProxy.config),
press `Ctrl+S`, pick `C:\Games\Kronos\Hermes` as the folder, set *Save as type* to *All files*,
make sure the file name is exactly `HermesProxy.config` (not `HermesProxy.config.txt`), and save.

**Check:** `C:\Games\Kronos\Hermes\HermesProxy.config` exists, next to `JimsProxy.exe`.

---

## Step 4: Point the proxy at Kronos

Out of the box the config points at `127.0.0.1` (your own PC). Change one line so it points at
Kronos.

**With Notepad**

1. Right-click `C:\Games\Kronos\Hermes\HermesProxy.config`, choose *Open with → Notepad*.
2. Find this line (it is near the top, after a comment about *ServerAddress*):

   ```xml
   <add key="ServerAddress" value="127.0.0.1" />
   ```

3. Change it to:

   ```xml
   <add key="ServerAddress" value="login.twinstar-wow.com" />
   ```

4. Save with `Ctrl+S` and close Notepad.

**Or with PowerShell:**

```powershell
$cfg = "C:\Games\Kronos\Hermes\HermesProxy.config"
(Get-Content $cfg) -replace '(<add key="ServerAddress" value=")[^"]*(")', '${1}login.twinstar-wow.com$2' | Set-Content $cfg
```

`login.twinstar-wow.com` is the Kronos realm cluster the launcher calls *Kronos*. If you play
on another one, use its address instead:

| Launcher name | `ServerAddress` |
|---|---|
| Kronos | `login.twinstar-wow.com` |
| Kronos 2 | `login2.twinstar-wow.com` |
| Kronos 3 | `login3.twinstar-wow.com` |

Change nothing else. `ClientBuild` is already `42597`, `ServerBuild` is `auto` (it picks 1.12.1
for a 1.14 client), `ClientSeed` is a fallback the proxy overrides, and `ServerType` is already
`Kronos`. If you are curious what the other keys do, every one of them is explained in the
[configuration reference](configuration.md).

**Check:** open the file again and confirm the `ServerAddress` line now says
`login.twinstar-wow.com`.

---

## Step 5: Get the play scripts

Two small files turn the whole start-and-stop routine into one double-click: they check that
nothing else is using the proxy's ports, point the game at the proxy, start the proxy, wait
until it is ready, start the game, and shut the proxy down cleanly when you quit the game.
Put them next to `JimsProxy.exe`:

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jameopotato/jimsproxy/master/scripts/play.bat" -OutFile "C:\Games\Kronos\Hermes\play.bat"
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/jameopotato/jimsproxy/master/scripts/play.ps1" -OutFile "C:\Games\Kronos\Hermes\play.ps1"
```

(Without PowerShell: open each link in the browser, `Ctrl+S`, save into `Hermes` with *Save as
type* set to *All files* and the names exactly `play.bat` and `play.ps1`.)

The scripts find the game by the folder layout from Step 1. **If your game is somewhere else**,
tell them where: open `play.bat` in Notepad and add the path to the end of the `powershell`
line, so it reads

```
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0play.ps1" %* -GameExe "D:\Somewhere\_classic_era_\WowClassic_ForCustomServers.exe"
```

For the Arctium route, point it at the Arctium executable and add its arguments:
`-GameExe "C:\Games\Kronos\World of Warcraft\Arctium WoW Launcher.exe" -GameArgs "--staticseed --version=ClassicEra"`.

**Check:** `C:\Games\Kronos\Hermes` contains `play.bat` and `play.ps1`.

---

## Step 6: Play

1. Double-click `C:\Games\Kronos\Hermes\play.bat`.
2. A black window opens and prints what it is doing. The first time, Windows may show
   SmartScreen for the proxy (*More info → Run anyway*) or your antivirus may object (see the
   note in Step 2). Wait for this line:

   ```
   [play] proxy is ready on 127.0.0.1:1119
   ```

   The game starts by itself right after it. Leave the black window open; it is holding the
   proxy.
3. At the WoW login screen, type your Kronos account name and password and press Enter. Pick
   your realm, pick a character, play.
4. When you are done, quit the game normally (*Exit Game* from the menu). The black window
   prints `[play] stopping the proxy...` and closes itself.

The first run also did two things you would otherwise do by hand: it wrote
`SET portal "127.0.0.1:1119"` into the game's `WTF\Config.wtf` (keeping a `Config.wtf.bak`),
which is what tells the game to connect to the proxy instead of Blizzard, and it checked that no
old proxy was still running. From now on, playing is just: double-click `play.bat`.

The proxy's console output is also saved to `Hermes\play-console.log`; attach it if you ever
need to report a problem.

---

## Running it by hand (without the scripts)

The scripts do nothing you cannot do yourself.

1. **Point the game at the proxy.** Open
   `C:\Games\Kronos\World of Warcraft\_classic_era_\WTF\Config.wtf` in Notepad. If there is a
   line starting with `SET portal`, change it; otherwise add a line. It must read:

   ```
   SET portal "127.0.0.1:1119"
   ```

   If there is no `WTF` folder yet, create it and create `Config.wtf` inside it with just that
   line. (`1119` is the proxy's `BNetPort`; if you ever change one, change both.)
2. **Start the proxy.** Double-click `C:\Games\Kronos\Hermes\JimsProxy.exe`. A console window
   opens. Wait for this line, which is the last of its four listeners coming up:

   ```
   Starting WorldSocket service
   ```

   Do not start the game before it appears.
3. **Start the game.** Double-click `WowClassic_ForCustomServers.exe` (or the Arctium Launcher).
4. **Log in** with your Kronos account name and password.
5. **When done,** quit the game first, then close the proxy's console window. A normal close,
   `Ctrl+C`, or `taskkill` without `/F` all let the proxy finish writing its log; only
   `taskkill /F` does not.

---

## The JimsPlus addon (optional)

JimsPlus is a small in-game addon that ships in the bundle (the `Addons\JimsPlus` folder from
Step 2). It pairs with the proxy: client-side fixes for cast bars, mail and name display, taxi
and pet quirks, plus an options panel (`/jp` in game) for proxy features. The launcher installs
it automatically; by hand, copy the folder into the game's addon folder:

```powershell
Copy-Item -Recurse -Force "C:\Games\Kronos\Hermes\Addons\JimsPlus" "C:\Games\Kronos\World of Warcraft\_classic_era_\Interface\AddOns\JimsPlus"
```

(Or drag the `JimsPlus` folder from `Hermes\Addons` into `_classic_era_\Interface\AddOns`.)

It must match the proxy version: re-copy it every time you update the proxy. The proxy switches
the addon channel off if the versions disagree, so nothing breaks, you just lose its fixes.

---

## Updating

A manual install does not update itself. When a new version is out (see the
[releases page](https://github.com/jameopotato/jimsproxy/releases) or the launcher's patch
notes on Discord):

1. Quit the game and make sure the proxy is stopped (no `JimsProxy.exe` in Task Manager).
2. Download the new bundle over the old one. Your `HermesProxy.config` is not in the zip, so it
   is not touched, and neither is `AccountData` (per-account state the game expects the proxy
   to remember; keep it):

   ```powershell
   Invoke-WebRequest -Uri "https://jimothy.cc/proxy/stable/latest" -OutFile "$env:TEMP\jimsproxy-bundle.zip"
   Expand-Archive -Path "$env:TEMP\jimsproxy-bundle.zip" -DestinationPath "C:\Games\Kronos\Hermes" -Force
   Copy-Item -Recurse -Force "C:\Games\Kronos\Hermes\Addons\JimsPlus" "C:\Games\Kronos\World of Warcraft\_classic_era_\Interface\AddOns\JimsPlus"
   ```

   (For the beta channel, use the beta link from Step 2 instead.)
3. Double-click `play.bat` as usual.

To see which version you have: open `Hermes\manifest.json` in Notepad, or read the `Version`
line the proxy prints when it starts. New versions occasionally add config keys; every key has a
built-in default, so an older config keeps working. The
[configuration reference](configuration.md) lists them if you want to add one.

---

## Troubleshooting

**"World Server is Down", or the game never shows the realm list.**
The game is not talking to the proxy. `WTF\Config.wtf` must contain
`SET portal "127.0.0.1:1119"` and the number must match `BNetPort` in `HermesProxy.config`
(`play.bat` fixes this for you). Also confirm the proxy printed `Starting WorldSocket service`
(or the script printed `proxy is ready`) before the game started.

**The proxy window closes at once, or says a port is in use.**
It needs four free ports on your PC: **1119, 8081, 8084, 8086**. Almost always an old proxy is
still running. Open Task Manager (`Ctrl+Shift+Esc`), find `JimsProxy.exe` (or `HermesProxy.exe`
from an old install), end it, try again. `play.bat` names the program holding the port. A
Windows `WSAEACCES (10013)` means something else owns the port: another program, a Windows port
reservation, or a proxy running with higher privileges than you.

**`Config loading failed`.**
`HermesProxy.config` is not next to `JimsProxy.exe`, or it was saved as
`HermesProxy.config.txt`. Redo [Step 3](#step-3-get-the-config-file). If it says
`The verification of the config failed` instead, the line just above names the problem
(a malformed `ClientSeed`, an unsupported build, a port outside 1-65535).

**Login fails, or `Unsupported ClientBuild`.**
Your client is not build 42597, or you started the plain `WowClassic.exe`. Check the build as in
[Before you start](#before-you-start), and start `WowClassic_ForCustomServers.exe` (or Arctium
with `--staticseed`).

**A yellow "update available" banner in the proxy window.**
Harmless; it compares against the archived upstream project. `play.bat` already starts the
proxy with `--no-version-check`, which skips it.

**Quest progress or settings look wrong after updating.**
You deleted the `Hermes` folder and lost `AccountData`. Restore it from wherever the old folder
went; next time update as in [Updating](#updating), which keeps it.

**Antivirus or SmartScreen.** See the note in [Step 2](#step-2-download-the-proxy).

> Unlike upstream HermesProxy, JimsProxy ignores the game's *Optimize Network for Speed*
> setting on the local connection (it is a no-op over loopback), so you do not need to keep it
> enabled to avoid disconnects.

---

## Appendix: Linux and macOS (community-supported)

The project tests Windows only. The proxy builds and runs natively on Linux and macOS; the
game client does not, and this project does not test either platform. Reports from Linux
players have led to real fixes, so they are welcome, but expect to do some of the work
yourself. There are no prebuilt Linux or macOS proxy binaries yet, so both start with a build
from source.

### Linux (community-supported)

**Proxy.** Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
for your distribution, then:

```bash
git clone https://github.com/jameopotato/jimsproxy.git
cd jimsproxy
dotnet publish HermesProxy --configuration Release --use-current-runtime -p:UsePublishBuildSettings=true -o ~/kronos/Hermes
chmod +x ~/kronos/Hermes/JimsProxy
```

That produces a self-contained `JimsProxy` with `CSV/` and `HermesProxy.config` beside it (the
source build includes the config, so Step 3 is not needed). Set `ServerAddress` as in Step 4:

```bash
sed -i 's|<add key="ServerAddress" value="[^"]*"|<add key="ServerAddress" value="login.twinstar-wow.com"|' ~/kronos/Hermes/HermesProxy.config
```

Test it: `cd ~/kronos/Hermes && ./JimsProxy --no-version-check` should print
`Starting WorldSocket service`; stop it with `Ctrl+C`.

**Game client.** Runs under Wine or Proton. What Linux players report working: a recent Wine
(9+) or GE-Proton with DXVK, through Lutris, Bottles, or Steam with
`WowClassic_ForCustomServers.exe` added as a non-Steam game, in a prefix of its own, no
Battle.net app involved. The proxy listens on `127.0.0.1`, which Wine shares with the host, so
nothing on the network side needs setting up. If the window stays black or the client crashes at
start, force DirectX 11 with `SET gxApi "D3D11"` in the client's `WTF/Config.wtf`. Keep the
proxy current: two Linux-specific client crashes (flight-path landing, world entry) were fixed
proxy-side.

Stone Tavern's experimental [Better Client](https://stonetavern.app/betterclient) is a worked
example of exactly this setup: their Linux package (about 8 GB, client included) bundles
JimsProxy built natively for Linux, pre-configured for their realm, behind a one-click play
script. It targets their own realm, so borrow the operating-system setup, not the server
settings.

**Play.** `scripts/play.sh` is the Linux/macOS version of `play.bat`:

```bash
curl -L -o ~/kronos/Hermes/play.sh https://raw.githubusercontent.com/jameopotato/jimsproxy/master/scripts/play.sh
chmod +x ~/kronos/Hermes/play.sh
~/kronos/Hermes/play.sh --game-exe "$HOME/kronos/World of Warcraft/_classic_era_/WowClassic_ForCustomServers.exe"
```

By default it starts the game with `wine "<game-exe>"`. If Lutris, Bottles or Steam starts your
game, hand it that command instead and it will wait for the WoW process to exit:
`play.sh --game-cmd 'lutris lutris:rungameid/12'`. `--help` lists the other options. By hand:
`./JimsProxy` in one terminal, wait for `Starting WorldSocket service`, start the client from
your Wine setup, `Ctrl+C` the proxy after you quit.

### macOS (community-supported)

**Proxy.** Builds and runs natively on Apple Silicon and Intel with the same `dotnet publish`
command as on Linux, and `play.sh` works for the proxy side (pass the command that starts your
game with `--game-cmd`). If it exits at startup with `AesGcm is not supported on your platform`,
it prints the fix on the next lines (`brew install openssl@3`, then start it with
`DYLD_LIBRARY_PATH=/opt/homebrew/opt/openssl@3/lib`).

**Game client.** This project has not tested a macOS route, but one exists: Stone Tavern's
experimental [Better Client](https://stonetavern.app/betterclient) ships a macOS package (about
8 GB) that runs the same Windows 1.14.2 client through Apple's free Game Porting Toolkit, with
JimsProxy built natively for macOS behind it. Reproducing that for Kronos means the Game Porting
Toolkit (or a Wine-based tool such as CrossOver or Whisky), your own client, and this proxy
pointed at Kronos. The alternative is to play from a Windows machine. If you get the Mac route
working against Kronos, open an issue with your steps and this section will be updated.

---

## Reference

### Command line flags

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
| `--no-version-check` | Skip the update check at startup. Saves up to 15 s when offline; the play scripts pass it |
| `--metrics` | Print per-opcode latency metrics every 60 s (diagnostics only) |

Playing on more than one server? Keep a config per server and pick with `--config`.

### Chat commands

Typed into any chat box while playing:

| Command | Effect |
|---|---|
| `!qcomplete <questId>` | Mark a quest complete in the proxy's tracking |
| `!quncomplete <questId>` | Undo the above |

### Files and ports

Everything the proxy writes goes next to its executable:

| Path | Contents |
|---|---|
| `Logs\jimsproxy-<date>-<time>.jsonl` | Structured diagnostic log, one per session (`StructuredLog`, on by default). Attach it to bug reports. |
| `AccountData\<account>\` | Per-account state the 1.14 client expects the server to remember. Keep it across updates. |
| `PacketsLog\` | Full packet captures, only when `PacketsLog=true`. Large. |
| `play-console.log` | Console output of the last run, written by the play scripts. |

All four listeners bind to `127.0.0.1` and all four ports must be free:

| Key | Default | Used for |
|---|---|---|
| `BNetPort` | `1119` | Login. **What `SET portal` in `Config.wtf` points at.** |
| `RestPort` | `8081` | Login REST calls |
| `RealmPort` | `8084` | Realm list and character select |
| `InstancePort` | `8086` | The game world. Its `Starting WorldSocket service` line is the ready signal |

Running two proxies at once (multiboxing)? Give the second one its own copy of the `Hermes`
folder with a different set, for example `1120 / 8082 / 8085 / 8087` (what the launcher uses for
its Alt client), and point that client's portal at the second `BNetPort`.

### Every config key

[configuration.md](configuration.md) documents all 40 keys the proxy reads, their defaults, and
which ones you should leave alone.

---

## Reporting a bug

Open an issue on [github.com/jameopotato/jimsproxy](https://github.com/jameopotato/jimsproxy/issues)
with:

- The proxy version: the `Version` line the proxy prints at startup (also in `play-console.log`),
  or `manifest.json`; and whether you built it yourself.
- The `Logs\jimsproxy-*.jsonl` file for the session. If a developer asks for more detail, set
  `DebugOutput` to `true` in `HermesProxy.config` and reproduce; if they ask for a packet
  capture, set `PacketsLog` to `true` for that one session and turn it back off afterwards.
- Your operating system and, on Linux or macOS, how you run the client (Wine, Proton,
  CrossOver, and the version).

Launcher users have a *Send Bug Report* button in the launcher's Help tab that attaches all of
this automatically.

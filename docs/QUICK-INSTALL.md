# JimsProxy quick-start installation guide

This guide describes installing JimsProxy with the **quick-start bundle**: a zip archive whose
installer locates an existing WoW Classic Era 1.14.2 client, installs the current proxy next to
it, configures the proxy and the client, and creates a Play command. The bundle performs the
Installation section of the [manual installation guide](MANUAL-INSTALL.md) automatically; the
manual guide remains the reference for what each step does and for Linux and macOS.

The quick-start bundle runs on Windows only and does not include or obtain the game client.

Contents

- [Prerequisites](#prerequisites)
- [Step 1: Download and extract the bundle](#step-1-download-and-extract-the-bundle)
- [Step 2: Run the installer](#step-2-run-the-installer)
- [Step 3: Play](#step-3-play)
- [Updating](#updating)
- [Reconfiguring](#reconfiguring)
- [Uninstalling](#uninstalling)
- [Unattended installation](#unattended-installation)
- [What the installer changes](#what-the-installer-changes)
- [Troubleshooting](#troubleshooting)

---

## Prerequisites

| Requirement | Details |
|---|---|
| Operating system | Windows 10 or Windows 11, 64-bit, with Windows PowerShell 5.1 (included). |
| Game client | WoW Classic Era **1.14.2, build 42597**, either installed (a `_classic_era_` folder) or present as a client archive (a zip that contains `.build.info` and the `_classic_era_` folder, such as `pkg_base.zip`), which the installer extracts. The installer accepts this build only; any other build is listed as not supported. JimsProxy does not include or distribute the game client. |
| Custom-server capable executable | `WowClassic_ForCustomServers.exe` in the client's `_classic_era_` folder, or the unmodified `WowClassic.exe` together with the [Arctium WoW Launcher](https://github.com/Arctium/WoW-Launcher) placed in the folder above `_classic_era_`. See [Client executable options](MANUAL-INSTALL.md#client-executable-options). |
| Server account | An account for the server. For Kronos, create one at [kronos-wow.com](https://www.kronos-wow.com). |
| Network | Access to `jimothy.cc` (proxy archive) and `raw.githubusercontent.com` (configuration file and play scripts). |
| Disk space | Approximately 500 MB on the drive that holds the client. Extracting a client archive additionally requires the archive's uncompressed size on the destination drive. |
| Privileges | None. The installer does not request administrator rights. |

## Step 1: Download and extract the bundle

1. Download `JimsProxy-QuickStart.zip`:
   <https://github.com/jameopotato/jimsproxy/releases/latest/download/JimsProxy-QuickStart.zip>
2. In File Explorer, right-click the downloaded file and select **Extract All…**, then
   **Extract**. The default destination (a folder named `JimsProxy-QuickStart` next to the zip)
   is suitable.

**Expected result:** the extracted folder contains `Install JimsProxy.cmd`, `install.ps1`,
`README.txt`, and `VERSION.txt`.

> **Note:** run the installer from the extracted folder. Started from inside the zip (the
> Explorer preview of the archive), it stops with the message "Extract the zip first".

## Step 2: Run the installer

Double-click `Install JimsProxy.cmd`. A console window opens and works through six steps. Each
step shows a numbered menu with a default in brackets; press Enter to accept the default, or
type the number of another option. `Q` quits at any menu. Everything shown is also written to
`Hermes\install.log`.

> **Note:** `install.ps1` is not code-signed. Windows SmartScreen may display "Windows
> protected your PC" the first time; select **More info**, then **Run anyway**. The same applies
> to `JimsProxy.exe` when it first starts. If antivirus software quarantines `JimsProxy.exe`,
> restore it and add the `Hermes` folder to the exclusion list.

### Step 1 of 6: Check this PC

The installer verifies the operating system and PowerShell version, enables TLS 1.2 for its
downloads, checks that `jimothy.cc` and `raw.githubusercontent.com` are reachable, and checks
free disk space. A failed check is reported with its reason, and the installer exits.

### Step 2 of 6: Find the client

The installer scans the usual installation locations and the root of each fixed drive (three
folder levels deep, at most 20 seconds) for a `_classic_era_` folder that contains a game
executable, and the Downloads, Desktop, Documents, and drive-root folders for a client archive
named `pkg_base.zip`. It lists what it found:

```
[1] D:\Games\Kronos\World of Warcraft\_classic_era_    build 1.14.2.42597    WowClassic_ForCustomServers.exe
[2] C:\Program Files (x86)\World of Warcraft\_classic_era_    not supported: build 1.15.7.60000
[3] C:\Users\Name\Downloads\pkg_base.zip    client archive    build 1.14.2.42597    (will be extracted)
[B] Browse for the _classic_era_ folder
[A] Use a client archive (.zip)
[T] Type the path
[Q] Quit
```

- The build is read from the client's `.build.info` file (inside the archive, for an archive)
  or from the executable's version. Only build 1.14.2.42597 can be selected.
- A client that has only `WowClassic.exe` is usable when the Arctium launcher is present in the
  folder above `_classic_era_`; the installer then configures the Play command to start Arctium
  with `--staticseed --version=ClassicEra`. Without Arctium, the entry states what is missing.

Select the client by number, or use `B`, `A`, or `T` to point the installer at a folder or
archive it did not find. If no usable client is selected, the installer exits with a message
stating the requirement.

**Client archives.** When an archive is selected, the installer asks for a destination folder
(default `<drive>:\Games\Kronos`; the folder must be empty or absent, and the drive must have
room for the archive's uncompressed contents plus 500 MB), then extracts the archive into
`<destination>\World of Warcraft\`. Extraction of a full client takes several minutes; a
progress line is printed as it proceeds. The archive is not modified or deleted. The
installation then continues with `<destination>\World of Warcraft\_classic_era_` as the
client. If the extraction is interrupted, the destination folder holds an incomplete client and
must be deleted before the installer is run again.

### Step 3 of 6: Server and channel

```
Server:   [1] Kronos (login.twinstar-wow.com)  [2] Kronos 2  [3] Kronos 3  [4] Other address    [1]
Channel:  [1] Stable  [2] Beta (newer changes, less testing)                                    [1]
```

### Step 4 of 6: Install the proxy

No input is required. The installer creates a `Hermes` folder next to the `World of Warcraft`
folder (or next to `_classic_era_` when there is no `World of Warcraft` level), downloads the
proxy archive for the chosen channel from `jimothy.cc`, extracts it, downloads
`HermesProxy.config` and the play scripts from the repository, sets `ServerAddress`, and writes
`Play Kronos.cmd`. Each action prints one line as it completes.

### Step 5 of 6: Connect the game

The installer sets `SET portal "127.0.0.1:1119"` in the client's `WTF\Config.wtf` (an existing
file is backed up as `Config.wtf.bak`), then asks:

```
Install the JimsPlus addon?             [Y/n]
Create a desktop shortcut "Play Kronos"? [Y/n]
```

JimsPlus is the in-game addon that pairs with the proxy (see
[Installing the JimsPlus addon](MANUAL-INSTALL.md#installing-the-jimsplus-addon-optional)).

### Step 6 of 6: Done

The installer prints a summary (installation folder, proxy version, client folder, executable
route, server, channel, addon, shortcut, log path) and asks:

```
Start the game now? [Y/n]
```

**Expected result:** the `Hermes` folder contains `JimsProxy.exe`, `CSV`, `Addons`,
`manifest.json`, `HermesProxy.config`, `play.bat`, `play.ps1`, `Play Kronos.cmd`,
`quickstart.json`, and `install.log`.

## Step 3: Play

1. Double-click the **Play Kronos** shortcut on the desktop, or `Hermes\Play Kronos.cmd`.
2. A console window opens. Wait for the line `[play] proxy is ready on 127.0.0.1:1119`; the
   game starts immediately after it. The console window must stay open during the session.
3. At the login screen, enter the account name and password, then select a realm and a
   character.
4. To end the session, exit the game. The console prints `[play] stopping the proxy...` and
   closes.

`Play Kronos.cmd` runs the same `play.ps1` described in
[Running the proxy and the game](MANUAL-INSTALL.md#running-the-proxy-and-the-game) with the
paths resolved by the installer.

## Updating

1. Exit the game and confirm that no `JimsProxy.exe` process is running.
2. Run `Install JimsProxy.cmd` again and select the same client. Because an installation exists,
   the installer shows:

   ```
   [1] Update the proxy
   [2] Reconfigure (server, channel)
   [3] Uninstall
   [4] Quit
   ```

3. Select `1`. The installer downloads the current archive for the installed channel, replaces
   `JimsProxy.exe`, `CSV`, `Addons`, and `manifest.json`, re-copies the JimsPlus addon if it was
   installed, and refreshes the play scripts. `HermesProxy.config` and `AccountData` are not
   modified.

The installer refuses to update while the proxy is running.

## Reconfiguring

Run `Install JimsProxy.cmd`, select the client, and choose `2`. The server and channel menus
from Step 3 are shown again; `ServerAddress` in `HermesProxy.config` and the stored channel are
updated. A channel change takes effect at the next update.

## Uninstalling

Run `Install JimsProxy.cmd`, select the client, and choose `3`. The installer:

1. Refuses to continue while the proxy is running.
2. Offers to move `AccountData` (per-account state) to `<root>\JimsProxy-AccountData-backup`
   before deleting the `Hermes` folder.
3. Deletes the `Hermes` folder.
4. Deletes `_classic_era_\Interface\AddOns\JimsPlus` if the installer installed it.
5. Removes the `SET portal` line from `WTF\Config.wtf` and the desktop shortcut.

The game client itself is not modified beyond items 4 and 5.

## Unattended installation

`install.ps1` accepts parameters that replace the corresponding prompts. Any prompt whose value
is supplied is skipped; `-Yes` accepts every remaining default.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -ClientDir "D:\Games\Kronos\World of Warcraft\_classic_era_" -Server kronos -Channel stable -Yes
```

| Parameter | Values |
|---|---|
| `-ClientDir <path>` | The `_classic_era_` folder |
| `-ClientArchive <path>` | A client archive to extract; requires `-ExtractTo` |
| `-ExtractTo <path>` | Destination folder for `-ClientArchive` (must be empty or absent) |
| `-Server` | `kronos`, `kronos2`, `kronos3`, or a login address |
| `-Channel` | `stable` or `beta` |
| `-NoAddon`, `-NoShortcut` | Skip the addon or the shortcut |
| `-Yes` | Accept all defaults; no prompts |
| `-Update`, `-Reconfigure`, `-Uninstall` | Run that action on an existing installation |
| `-Root <path>` | The folder containing `Hermes`, for `-Update` and `-Uninstall` |

Exit codes: `0` success; `1` unexpected error; `2` a Step 1 check failed; `3` no usable client
selected; `4` a download failed; `5` a downloaded file failed verification; `6` cancelled.

## What the installer changes

| Location | Change |
|---|---|
| `<root>\World of Warcraft\` | Created only when a client archive is extracted; holds the extracted client. |
| `<root>\Hermes\` | Created. Holds the proxy, its configuration, the play scripts, `Play Kronos.cmd`, `quickstart.json` (installer state), and `install.log`. |
| `_classic_era_\WTF\Config.wtf` | The `SET portal` line is set; the previous file is kept as `Config.wtf.bak`. |
| `_classic_era_\Interface\AddOns\JimsPlus\` | Created or replaced, if the addon option is accepted. |
| Desktop | `Play Kronos.lnk`, if the shortcut option is accepted. |

No other file in the game client is read for any purpose other than detection, and none is
deleted.

## Troubleshooting

**"No usable client found" or the only entries are "not supported".**
The installer accepts build 1.14.2.42597 only. Check the client's build as described in
[Verifying the client build](MANUAL-INSTALL.md#verifying-the-client-build). A client in an
unusual location can be selected with `B` (browse) or `T` (type the path).

**An archive is listed as "not supported" or "not a client archive".**
The archive's `.build.info` reports a build other than 1.14.2.42597, or the archive does not
contain `.build.info` and a `_classic_era_` folder with a game executable. Only archives with
that layout and build are extracted.

**"Destination is not empty" or "not enough free space" when extracting an archive.**
Choose an empty or non-existent folder on a drive with room for the extracted client; the
required amount is shown in the message.

**The entry says "needs WowClassic_ForCustomServers.exe or the Arctium launcher".**
The folder contains only the unmodified `WowClassic.exe`. See
[Client executable options](MANUAL-INSTALL.md#client-executable-options).

**"Download failed" (exit code 4) or "verification failed" (exit code 5).**
The proxy archive or a repository file could not be fetched, or the download was not a valid
archive. Check the network, then run the installer again; a partial installation is completed
on the next run.

**Step 1 fails on connectivity.**
`jimothy.cc` or `raw.githubusercontent.com` is not reachable from this PC (firewall, proxy,
or DNS). Both are required.

**"Update" or "Uninstall" refuses to run.**
`JimsProxy.exe` is still running. End the session with the Play window, or end the process in
Task Manager (`Ctrl+Shift+Esc`), and retry.

**Problems after installation** (connection, ports, login, "World Server is Down") are covered
in the manual guide's [Troubleshooting](MANUAL-INSTALL.md#troubleshooting) section; the
installed files are identical to a manual installation.

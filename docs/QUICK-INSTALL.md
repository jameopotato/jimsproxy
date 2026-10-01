# JimsProxy quick-start installation

The quick-start bundle is a zip whose installer locates an existing WoW Classic Era 1.14.2
client, installs the current proxy next to it, configures the proxy and the client, and creates
a Play command. It performs the Install section of the [manual guide](MANUAL-INSTALL.md); that
guide remains the reference for what each step does. Windows only. The game client is not
included.

Contents

- [Requirements](#requirements)
- [Install](#install)
- [Run](#run)
- [Update, reconfigure, uninstall](#update-reconfigure-uninstall)
- [Unattended installation](#unattended-installation)
- [What the installer changes](#what-the-installer-changes)
- [Troubleshooting](#troubleshooting)

---

## Requirements

- Windows 10 or 11, 64-bit, with Windows PowerShell 5.1 (included). No administrator rights.
- WoW Classic Era **1.14.2, build 42597**, with `WowClassic_ForCustomServers.exe`: either
  installed (a `_classic_era_` folder) or as a client archive (a zip containing `.build.info`
  and `_classic_era_\WowClassic_ForCustomServers.exe`), which the installer extracts. Any
  other build is listed as not supported and cannot be selected. The game client is not
  included.
- An account on the server. For Kronos: [kronos-wow.com](https://www.kronos-wow.com).
- Network access to `jimothy.cc` and `raw.githubusercontent.com`.
- About 500 MB free next to the client, plus the archive's uncompressed size if one is extracted.

---

## Install

1. Download <https://github.com/jameopotato/jimsproxy/releases/latest/download/JimsProxy-QuickStart.zip>
   and extract it (**Extract All**). The folder contains `Install JimsProxy.cmd`, `install.ps1`,
   `README.txt`, and `VERSION.txt`. Run the installer from the extracted folder, not from
   inside the zip.
2. Double-click `Install JimsProxy.cmd`. A console window describes what the installer does,
   links this guide, and waits for Enter. It then works through six steps. In each menu, the
   arrow keys move the highlight and Enter selects; `Q` quits. Everything shown is also written
   to `Hermes\install.log`.

`install.ps1` and `JimsProxy.exe` are not code-signed. If SmartScreen shows "Windows protected
your PC", select **More info → Run anyway**. If antivirus software quarantines `JimsProxy.exe`,
restore it and add the `Hermes` folder to the exclusion list.

**Step 1 of 6: Check this PC.** Operating system, PowerShell version, TLS 1.2, reachability of
both hosts, free disk space. A failed check is reported and the installer exits (code 2).

**Step 2 of 6: Find the client.** The installer scans the usual installation locations and the
fixed drives (four folder levels deep) for `_classic_era_` folders, and the Downloads, Desktop,
Documents, drive-root and drive `Games` folders for client archives: each zip larger than 1 GB
is opened and listed if its contents are a client archive, whatever its file name.

```
Select a client:
 > √ Kronos                 D:\Games\Kronos
   √ kronoswow              C:\...\AppData\Roaming\kronoswow  (has a launcher or manual Hermes)
   √ client.zip             C:\Users\Name\Downloads  (client archive, extracted to a new folder)
     2 other clients found, not usable: build 1.15.8.67156 (x2)

   Browse for a client folder
   Extract a client archive (.zip)
   Keep searching (every folder on every drive)
   Quit
```

`√` marks a usable client: build 1.14.2.42597 with `WowClassic_ForCustomServers.exe`. Clients
that cannot be used are counted on one line with the reason. If only WoW Classic 1.15 clients
are found, the installer recommends the Classic WoW Launcher
([jimothy.cc/install](https://jimothy.cc/install)), which can copy a 1.15 client and patch the
copy into 1.14.2. **Keep searching** walks every folder on every fixed drive (it can take a few
minutes; Esc stops it) and adds what it finds to the list.

**A client that already has a `Hermes` folder** (from the launcher or a manual installation)
offers two routes:

- **Copy the client to a new folder and install there** (recommended): a clean install that
  leaves the current client and its `Hermes` untouched. It needs the client's size in
  additional disk space; the installer shows the amount.
- **Install here, keeping the current `Hermes` as a backup**: after a warning and a confirmation,
  the existing `Hermes` is renamed to `Hermes-backup-<date>` at Step 4 and its `AccountData` is
  carried into the new one. A launcher or other installation that uses that folder stops working
  until the backup is renamed back.

For an archive or a copy, the installer asks for a destination (default `<drive>:\Games\Kronos`,
or **Browse**; it must be empty or absent, with room for the client) and writes the client to
`<destination>\World of Warcraft\`. An archive's build is checked before extraction; the archive
and the original client are not modified. A full client takes a few minutes.

**Step 3 of 6: Server and channel.**

```
Server:
 > Kronos (login.twinstar-wow.com)
   Kronos 2 (login2.twinstar-wow.com)
   Kronos 3 (login3.twinstar-wow.com)
   Other address

Channel:
 > Stable: the current release
   Beta: newer changes, less testing
```

**Step 4 of 6: Install the proxy.** No input. The installer reads the channel manifest,
downloads the archive it names, verifies its SHA-256 and contents, extracts it into a new
`Hermes` folder next to the `World of Warcraft` folder, downloads `HermesProxy.config` and the
play scripts, sets `ServerAddress`, and writes `Play Kronos.cmd`.

**Step 5 of 6: Connect the game.** Sets `SET portal "127.0.0.1:1119"` in the client's
`WTF\Config.wtf` (previous file kept as `Config.wtf.bak`), then asks:

```
Install the JimsPlus addon? [Y/n]
Create a desktop shortcut "Play Kronos"? [Y/n]
```

**Step 6 of 6: Done.** Prints a summary (installation folder, proxy version, client folder, game
executable, client archive if one was extracted, server, channel, addon, shortcut, Play command,
log path) and asks `Start the game now? [Y/n]`.

---

## Run

Double-click the **Play Kronos** desktop shortcut or `Hermes\Play Kronos.cmd`. The game starts
after the console prints `[play] proxy is ready on 127.0.0.1:1119`; the console must stay open
during the session. Exit the game to stop the proxy. `Play Kronos.cmd` runs the same `play.ps1`
described in the [manual guide](MANUAL-INSTALL.md#run) with the paths resolved by the installer.

---

## Update, reconfigure, uninstall

Run `Install JimsProxy.cmd` again and select the same client. Because an installation exists,
the installer shows:

```
Select:
 > Update the proxy
   Reconfigure (server, channel)
   Uninstall
   Quit
```

- **Update** downloads the current archive for the installed channel and replaces
  `JimsProxy.exe`, `CSV`, `Addons` and `manifest.json`; re-copies JimsPlus if it was installed;
  refreshes the play scripts. `HermesProxy.config` and `AccountData` are not modified.
- **Reconfigure** shows the server and channel menus again. A channel change takes effect at the
  next update.
- **Uninstall** asks for confirmation (from this menu), offers to move `AccountData` to
  `<root>\JimsProxy-AccountData-backup`, deletes `Hermes`, deletes `Interface\AddOns\JimsPlus`
  if the installer created it, removes the `SET portal` line only if it points at `127.0.0.1`,
  and deletes the shortcut.

Update and Uninstall refuse to run while `JimsProxy.exe` is running (exit code 2). Reconfigure
and Uninstall skip the Step 1 network check.

---

## Unattended installation

`install.ps1` accepts parameters in place of the prompts. A supplied value skips its prompt;
`-Yes` accepts every remaining default and does not start the game.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -ClientDir "D:\Games\Kronos\World of Warcraft\_classic_era_" -Server kronos -Channel stable -Yes
```

| Parameter | Values |
|---|---|
| `-ClientDir <path>` | The `_classic_era_` folder |
| `-ClientArchive <path>` | A client archive to extract |
| `-ExtractTo <path>` | Destination for `-ClientArchive` (empty or absent); without it the destination is asked, or the default is used with `-Yes` |
| `-CopyTo <path>` | When the client already has a `Hermes` the installer did not create: copy the client to this empty or new folder and install there |
| `-Server` | `kronos`, `kronos2`, `kronos3`, or a login address |
| `-Channel` | `stable` or `beta` |
| `-NoAddon`, `-NoShortcut` | Skip the addon or the shortcut |
| `-Yes` | Accept all defaults; no prompts (the welcome screen is skipped) |
| `-Update`, `-Reconfigure`, `-Uninstall` | Run that action on an existing installation, without confirmation |
| `-Root <path>` | The folder containing `Hermes`, for `-Update`, `-Reconfigure` and `-Uninstall` |

Exit codes: `0` success; `1` unexpected error or invalid parameter; `2` a Step 1 check failed,
or the proxy is running; `3` no usable client selected (including a refused destination, an
existing `Hermes` the installer did not create without `-CopyTo`, or no installation at
`-Root`); `4` a download
failed or the channel is paused; `5` a downloaded or extracted file failed verification; `6`
cancelled (`Q` or `Ctrl+C`).

---

## What the installer changes

| Location | Change |
|---|---|
| `<root>\Hermes\` | Created: the proxy, `HermesProxy.config`, play scripts, `Play Kronos.cmd`, `quickstart.json` (installer state), `install.log` |
| `<root>\World of Warcraft\` | Created only when a client archive is extracted or a client is copied |
| `<root>\Hermes-backup-<date>\` | The previous `Hermes`, renamed, only when installing in place over a launcher or manual installation |
| `_classic_era_\WTF\Config.wtf` | `SET portal` line set; previous file kept as `Config.wtf.bak`; created if absent |
| `_classic_era_\Interface\AddOns\JimsPlus\` | Created or updated, if accepted |
| Desktop | `Play Kronos.lnk`, if accepted |

Nothing else in the client is modified, and nothing is deleted.

---

## Troubleshooting

**No client has a `√`, or none is found.** Only build 1.14.2.42597 is accepted; see
[Verifying the client build](MANUAL-INSTALL.md#verifying-the-client-build). **Keep searching**
looks everywhere, and **Browse** selects a client folder directly. For a 1.15 client, use the
[Classic WoW Launcher](https://jimothy.cc/install).

**An archive is rejected or not listed.** Its `.build.info` reports another build, or it lacks
`.build.info` and `_classic_era_\WowClassic_ForCustomServers.exe`. The scan lists only zips
larger than 1 GB; **Extract a client archive** selects a smaller one.

**The destination is refused.** It must be an empty or non-existent folder on a drive with room
for the client. An interrupted extraction or copy leaves an incomplete folder that must be
deleted before running again.

**An in-place install broke the launcher.** Delete the new `Hermes` and rename
`Hermes-backup-<date>` back to `Hermes`.

**Download failed (exit code 4).** A host was unreachable, or the channel is paused while a
release is published; retry later.

**Verification failed (exit code 5), or the warning "SHA-256 not verified".** The download did
not match the channel manifest, or the manifest could not be read and the archive was fetched
without a checksum (its contents are still checked). Run the installer again.

**Update or Uninstall refuses to run.** `JimsProxy.exe` is still running. Exit the game or end
the process in Task Manager.

Problems after installation (connection, ports, login) are covered in the manual guide's
[Troubleshooting](MANUAL-INSTALL.md#troubleshooting); the installed files are identical to a
manual installation.

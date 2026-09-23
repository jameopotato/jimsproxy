#Requires -Version 5.1
<#
.SYNOPSIS
  JimsProxy quick-start installer. Finds a WoW Classic Era 1.14.2 client, installs JimsProxy
  next to it, configures the proxy and the client, and creates a Play command.

.DESCRIPTION
  User documentation: docs/QUICK-INSTALL.md in the jameopotato/jimsproxy repository.
  Start it with "Install JimsProxy.cmd"; running this file directly works the same way.

  The installer changes nothing outside the chosen root folder (the folder that holds
  "World of Warcraft", or the folder that holds "_classic_era_"), the client's WTF and
  Interface\AddOns\JimsPlus folders, the optional desktop shortcut, and its own temporary
  files in %TEMP%. It never downloads, copies, patches, or deletes game files.

  Exit codes: 0 success, 1 unexpected error, 2 preflight failed, 3 no usable client selected,
  4 download failed, 5 verification failed, 6 cancelled.
#>
[CmdletBinding()]
param(
    [string]$ClientDir,
    [string]$ClientArchive,
    [string]$ExtractTo,
    [string]$Server,
    [string]$Channel,
    [switch]$NoAddon,
    [switch]$NoShortcut,
    [switch]$Yes,
    [switch]$Update,
    [switch]$Reconfigure,
    [switch]$Uninstall,
    [string]$Root
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with the progress bar on 5.1

# ================================================================== constants
$InstallerVersion = '1.0.0'
$RequiredBuild    = '1.14.2.42597'
$ReadyLine        = 'Starting WorldSocket service'
$ProxyHost        = 'https://jimothy.cc'
$RawBase          = 'https://raw.githubusercontent.com/jameopotato/jimsproxy/master'
$Servers = @(
    [pscustomobject]@{ Key = 'kronos';  Name = 'Kronos';   Address = 'login.twinstar-wow.com' },
    [pscustomobject]@{ Key = 'kronos2'; Name = 'Kronos 2'; Address = 'login2.twinstar-wow.com' },
    [pscustomobject]@{ Key = 'kronos3'; Name = 'Kronos 3'; Address = 'login3.twinstar-wow.com' }
)
$MinFreeBytes     = 500MB
$ScanSeconds      = 20
$ClientDepth      = 4       # folder levels below each scan root searched for _classic_era_
$MinArchiveBytes  = 1GB     # zips at or below this size are not opened during the scan
$PlayCmdName      = 'Play Kronos.cmd'
$ShortcutName     = 'Play Kronos.lnk'

# Test hook, not documented for users: serve a local folder in place of both hosts, laid out as
# <base>/proxy/<channel>/latest and <base>/raw/<repo path>.
if ($env:JIMSPROXY_QS_BASEURL) {
    $ProxyHost = $env:JIMSPROXY_QS_BASEURL.TrimEnd('/')
    $RawBase   = "$ProxyHost/raw"
}
# latest.json names the channel's current archive and its SHA-256 (written by the site's
# publish step); /latest redirects to the same archive and is used only when latest.json
# cannot be read.
$ProxyUrls         = @{ stable = "$ProxyHost/proxy/stable/latest"; beta = "$ProxyHost/proxy/beta/latest" }
$ProxyManifestUrls = @{ stable = "$ProxyHost/proxy/stable/latest.json"; beta = "$ProxyHost/proxy/beta/latest.json" }
$ConfigUrl  = "$RawBase/HermesProxy/HermesProxy.config"
$PlayBatUrl = "$RawBase/scripts/play.bat"
$PlayPs1Url = "$RawBase/scripts/play.ps1"

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ================================================================== state
$script:LogPath   = Join-Path $env:TEMP 'jimsproxy-quickstart.log'
$script:TempPaths = New-Object System.Collections.Generic.List[string]
$script:Step      = 'Start'
$script:Extracting = $null      # root folder while a client archive is being extracted
$script:Finished  = $false      # false in the final block only when Ctrl+C stopped the script

# ================================================================== output and input
function Out-Log {
    param([string]$Text = '')
    Write-Host $Text
    try { [System.IO.File]::AppendAllText($script:LogPath, $Text + "`r`n", $Utf8NoBom) } catch { }
}

function Set-LogFile {
    # Moves logging into the installation's Hermes folder, carrying over what was logged so far.
    param([string]$Path)
    if ($script:LogPath -eq $Path) { return }
    try {
        if ([System.IO.File]::Exists($script:LogPath)) {
            [System.IO.File]::AppendAllText($Path, [System.IO.File]::ReadAllText($script:LogPath), $Utf8NoBom)
            Remove-Item -LiteralPath $script:LogPath -Force -ErrorAction SilentlyContinue
        }
    } catch { }
    $script:LogPath = $Path
}

function Write-Heading {
    param([string]$Title)
    $script:Step = $Title
    Out-Log ''
    Out-Log $Title
    Out-Log ('-' * $Title.Length)
}

function Stop-Install {
    # Every failure names the step, the reason, the log, and one next action.
    param([int]$Code, [string]$Reason, [string]$Next)
    Out-Log ''
    Out-Log "Stopped at: $($script:Step)"
    Out-Log "Reason:     $Reason"
    if ($Next) { Out-Log "Next:       $Next" }
    Out-Log "Log:        $($script:LogPath)"
    $script:Finished = $true
    exit $Code
}

function Read-Line {
    param([string]$Prompt)
    Write-Host -NoNewline "$Prompt "
    $answer = [Console]::In.ReadLine()
    if ($null -eq $answer) {
        Out-Log ''
        Stop-Install 6 'Input ended before a choice was made.' 'Run the installer again from a console window.'
    }
    try { [System.IO.File]::AppendAllText($script:LogPath, "$Prompt $answer`r`n", $Utf8NoBom) } catch { }
    return $answer.Trim()
}

function Read-Choice {
    # Returns the chosen key (upper case). Q always cancels.
    param([string]$Prompt, [string[]]$Keys, [string]$Default)
    if ($Yes) { Out-Log "$Prompt [$Default] $Default (accepted by -Yes)"; return $Default }
    while ($true) {
        $a = Read-Line "$Prompt [$Default]"
        if ($a -eq '') { $a = $Default }
        $a = $a.ToUpperInvariant()
        if ($a -eq 'Q') { Stop-Install 6 'Cancelled.' 'Run the installer again to continue.' }
        if ($Keys -contains $a) { return $a }
        Out-Log "Enter one of: $(($Keys + 'Q') -join ', ')."
    }
}

function Read-YesNo {
    param([string]$Prompt, [bool]$Default = $true)
    $hint = if ($Default) { '[Y/n]' } else { '[y/N]' }
    if ($Yes) { Out-Log "$Prompt $hint $(if ($Default) { 'Y' } else { 'N' }) (accepted by -Yes)"; return $Default }
    while ($true) {
        $a = (Read-Line "$Prompt $hint").ToUpperInvariant()
        if ($a -eq '') { return $Default }
        if ($a -eq 'Y' -or $a -eq 'YES') { return $true }
        if ($a -eq 'N' -or $a -eq 'NO') { return $false }
        if ($a -eq 'Q') { Stop-Install 6 'Cancelled.' 'Run the installer again to continue.' }
        Out-Log 'Enter Y or N.'
    }
}

# ================================================================== file helpers
function New-TempPath {
    param([string]$Suffix)
    $p = Join-Path $env:TEMP ("jimsproxy-quickstart-{0}{1}" -f [guid]::NewGuid().ToString('N').Substring(0, 12), $Suffix)
    $script:TempPaths.Add($p)
    return $p
}

function Remove-Tree {
    # Deletes a folder without following junctions or symbolic links inside it: links are
    # removed as links first, so their targets are never touched.
    param([string]$Path)
    if (-not [System.IO.Directory]::Exists($Path)) { return }
    $links = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue)
    foreach ($l in $links) {
        if ($l.PSIsContainer) { [System.IO.Directory]::Delete($l.FullName, $false) } else { [System.IO.File]::Delete($l.FullName) }
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Read-TextFile {
    # Returns @{ Text; Bom } so a rewrite keeps the file's encoding.
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = if ($bom) { [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3) } else { [System.Text.Encoding]::UTF8.GetString($bytes) }
    return @{ Text = $text; Bom = $bom }
}

function Write-TextFile {
    param([string]$Path, [string]$Text, [bool]$Bom = $false)
    $enc = if ($Bom) { New-Object System.Text.UTF8Encoding($true) } else { $Utf8NoBom }
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

function Get-FreeBytes {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $drive = New-Object System.IO.DriveInfo([System.IO.Path]::GetPathRoot($full))
    return $drive.AvailableFreeSpace
}

function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N1} GB' -f ($Bytes / 1GB)) }
    return ('{0:N0} MB' -f ($Bytes / 1MB))
}

function Get-ConfigValue {
    param([string]$Text, [string]$Key)
    $m = [regex]::Match($Text, '<add\s+key="' + [regex]::Escape($Key) + '"\s+value="([^"]*)"')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

# ================================================================== client detection
function Read-BuildInfoVersion {
    # .build.info is a pipe-separated table; the header names each column as "Name!TYPE:n".
    # Takes the Version of the Active row, preferring the wow_classic_era product when a
    # folder holds more than one product.
    param([string[]]$Lines)
    $Lines = @($Lines | Where-Object { $_ -and $_.Trim() -ne '' })
    if ($Lines.Count -lt 2) { return $null }
    $cols = @($Lines[0].Split('|') | ForEach-Object { ($_ -split '!')[0].Trim() })
    $iActive = [array]::IndexOf($cols, 'Active')
    $iVer    = [array]::IndexOf($cols, 'Version')
    $iProd   = [array]::IndexOf($cols, 'Product')
    if ($iVer -lt 0) { return $null }
    $first = $null
    for ($i = 1; $i -lt $Lines.Count; $i++) {
        $f = $Lines[$i].Split('|')
        if ($f.Count -le $iVer) { continue }
        if ($iActive -ge 0 -and ($f.Count -le $iActive -or $f[$iActive].Trim() -ne '1')) { continue }
        $v = $f[$iVer].Trim()
        if ($iProd -ge 0 -and $f.Count -gt $iProd -and $f[$iProd].Trim() -eq 'wow_classic_era') { return $v }
        if (-not $first) { $first = $v }
    }
    return $first
}

function New-Candidate {
    param([string]$Kind, [string]$Path)
    return [pscustomobject]@{
        Kind = $Kind; Path = $Path; Build = $null; GameExe = $null; Usable = $false; Status = ''
        Prefix = $null; UncompressedBytes = [long]0; EntryCount = 0; IsClientArchive = $false
    }
}

function Get-FolderCandidate {
    param([string]$EraDir)
    $c = New-Candidate 'folder' $EraDir
    $custom = Join-Path $EraDir 'WowClassic_ForCustomServers.exe'
    $stock  = Join-Path $EraDir 'WowClassic.exe'
    $hasCustom = [System.IO.File]::Exists($custom)
    if (-not $hasCustom -and -not [System.IO.File]::Exists($stock)) { $c.Status = 'not usable: no game executable'; return $c }
    $buildInfo = Join-Path (Split-Path -Parent $EraDir) '.build.info'
    if ([System.IO.File]::Exists($buildInfo)) {
        try { $c.Build = Read-BuildInfoVersion ([System.IO.File]::ReadAllLines($buildInfo)) } catch { }
    }
    if (-not $c.Build) {
        $exe = if ($hasCustom) { $custom } else { $stock }
        $vi = (Get-Item -LiteralPath $exe).VersionInfo
        $c.Build = '{0}.{1}.{2}.{3}' -f $vi.FileMajorPart, $vi.FileMinorPart, $vi.FileBuildPart, $vi.FilePrivatePart
    }
    if ($c.Build -ne $RequiredBuild) { $c.Status = "not supported: build $($c.Build)"; return $c }
    if (-not $hasCustom) { $c.Status = 'not usable: needs WowClassic_ForCustomServers.exe'; return $c }
    $c.GameExe = $custom
    $c.Usable = $true
    $c.Status = "build $($c.Build)    WowClassic_ForCustomServers.exe"
    return $c
}

function Get-ArchiveCandidate {
    param([string]$ZipPath)
    $c = New-Candidate 'archive' $ZipPath
    try { $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath) }
    catch { $c.Status = 'not a readable zip archive'; return $c }
    try {
        # A client archive is identified by its contents: .build.info and
        # _classic_era_/WowClassic_ForCustomServers.exe under the same prefix (empty, or one or
        # more folders). Entry names are compared with forward slashes; some tools write
        # backslashes.
        $names = @{}
        foreach ($e in $zip.Entries) { $names[$e.FullName.Replace('\', '/').ToLowerInvariant()] = $e }
        $prefix = $null
        foreach ($n in @($names.Keys | Sort-Object Length)) {
            $m = [regex]::Match($n, '^(.*?)_classic_era_/wowclassic_forcustomservers\.exe$')
            if (-not $m.Success) { continue }
            $p = $m.Groups[1].Value
            if ($p -ne '' -and -not $p.EndsWith('/')) { continue }
            if ($names.ContainsKey("$p.build.info")) { $prefix = $p; break }
        }
        if ($null -eq $prefix) { $c.Status = 'not a client archive: no .build.info and _classic_era_/WowClassic_ForCustomServers.exe'; return $c }
        $c.Prefix = $names["$prefix.build.info"].FullName.Replace('\', '/').Substring(0, $prefix.Length)
        $c.IsClientArchive = $true
        $bi = $names["$prefix.build.info"]
        $reader = New-Object System.IO.StreamReader($bi.Open())
        try { $lines = $reader.ReadToEnd() -split "`r?`n" } finally { $reader.Dispose() }
        $c.Build = Read-BuildInfoVersion $lines
        if (-not $c.Build) { $c.Status = 'not a client archive: .build.info has no active version'; return $c }

        foreach ($e in $zip.Entries) {
            if ($e.FullName.Replace('\', '/').StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $c.UncompressedBytes += $e.Length
                $c.EntryCount++
            }
        }
        if ($c.Build -ne $RequiredBuild) { $c.Status = "not supported: build $($c.Build)"; return $c }
        $c.Usable = $true
        $c.Status = "client archive    build $($c.Build)    (will be extracted)"
        return $c
    } finally { $zip.Dispose() }
}

function Resolve-EraFolder {
    # Accepts the _classic_era_ folder itself, or a folder that contains it directly or
    # through a "World of Warcraft" level.
    param([string]$Path)
    $Path = $Path.Trim().Trim('"').TrimEnd('\')
    if ($Path -match '^[A-Za-z]:$') { $Path += '\' }
    if (-not [System.IO.Directory]::Exists($Path)) { return $null }
    $full = [System.IO.Path]::GetFullPath($Path)
    if ((Split-Path -Leaf $full) -ieq '_classic_era_') { return $full.TrimEnd('\') }
    foreach ($sub in '_classic_era_', 'World of Warcraft\_classic_era_') {
        $p = Join-Path $full $sub
        if ([System.IO.Directory]::Exists($p)) { return $p }
    }
    return $null
}

function Get-InstallRoot {
    # The folder that contains "World of Warcraft"; without that level, the folder that
    # contains _classic_era_.
    param([string]$EraDir)
    $game = Split-Path -Parent $EraDir
    if ((Split-Path -Leaf $game) -ieq 'World of Warcraft') {
        $parent = Split-Path -Parent $game
        if ($parent) { return $parent }
    }
    return $game
}

$SkipNames = @('Windows', 'System Volume Information', 'WinSxS', 'node_modules', '.git')

function Get-ScanPlan {
    # Roots in priority order with how deep to look for a _classic_era_ folder (FolderDepth)
    # and for client archives (ZipDepth); -1 means not at all. Drive roots come last because
    # they are the slowest; the 20-second cap then cuts off the least likely places first.
    $homeDir = $env:USERPROFILE
    $desktop = [Environment]::GetFolderPath('Desktop')
    $plan = New-Object System.Collections.Generic.List[object]
    $add = { param($p, $f, $z) if ($p -and [System.IO.Directory]::Exists($p)) { $plan.Add([pscustomobject]@{ Path = $p.TrimEnd('\') + '\'; FolderDepth = $f; ZipDepth = $z }) } }
    & $add ${env:ProgramFiles(x86)} $ClientDepth -1
    & $add $env:ProgramFiles $ClientDepth -1
    & $add $env:APPDATA $ClientDepth -1
    & $add $env:LOCALAPPDATA $ClientDepth -1
    & $add (Join-Path $homeDir 'Games') $ClientDepth -1
    & $add (Join-Path $homeDir 'Desktop') $ClientDepth 2
    & $add $desktop $ClientDepth 2
    & $add (Join-Path $homeDir 'Downloads') $ClientDepth 2
    & $add (Join-Path $homeDir 'Documents') $ClientDepth 2
    & $add ([Environment]::GetFolderPath('MyDocuments')) $ClientDepth 2
    $drives = @([System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady })
    foreach ($d in $drives) { & $add (Join-Path $d.RootDirectory.FullName 'Games') $ClientDepth 2 }
    foreach ($d in $drives) { & $add $d.RootDirectory.FullName $ClientDepth 2 }
    return , $plan
}

function Find-Candidates {
    # One breadth-first walk per root. A folder already walked from an earlier root is
    # skipped with its subtree (an earlier root always covers it at least as deep). Does
    # not descend into junctions or symbolic links.
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $eraDirs = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $zips    = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $visited = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $completed = $true
    foreach ($root in (Get-ScanPlan)) {
        if ($visited.Contains($root.Path)) { continue }
        $maxDepth = [Math]::Max($root.FolderDepth, $root.ZipDepth)
        $queue = New-Object System.Collections.Generic.Queue[object]
        $queue.Enqueue(@($root.Path, 0))
        while ($queue.Count -gt 0) {
            if ($clock.Elapsed.TotalSeconds -ge $ScanSeconds) { $completed = $false; break }
            $item = $queue.Dequeue()
            $dir = $item[0]; $level = $item[1]
            if (-not $visited.Add($dir)) { continue }
            if ($level -le $root.FolderDepth -and [System.IO.Path]::GetFileName($dir.TrimEnd('\')) -ieq '_classic_era_') {
                if ([System.IO.File]::Exists((Join-Path $dir 'WowClassic_ForCustomServers.exe')) -or [System.IO.File]::Exists((Join-Path $dir 'WowClassic.exe'))) {
                    [void]$eraDirs.Add($dir.TrimEnd('\'))
                }
            }
            if ($level -le $root.ZipDepth) {
                try {
                    foreach ($f in ([System.IO.DirectoryInfo]$dir).GetFiles('*.zip')) {
                        if ($f.Length -gt $MinArchiveBytes) { [void]$zips.Add($f.FullName) }
                    }
                } catch { }
            }
            if ($level -ge $maxDepth) { continue }
            try { $subs = [System.IO.Directory]::GetDirectories($dir) } catch { continue }
            foreach ($s in $subs) {
                $name = [System.IO.Path]::GetFileName($s)
                if ($name.StartsWith('$') -or $SkipNames -contains $name) { continue }
                try { if ([System.IO.File]::GetAttributes($s) -band [System.IO.FileAttributes]::ReparsePoint) { continue } } catch { continue }
                $queue.Enqueue(@(($s + '\'), ($level + 1)))
            }
        }
        if (-not $completed) { break }
    }
    if (-not $completed) {
        Out-Log "The scan stopped after $ScanSeconds seconds; folders not reached are not listed. Use B, A, or T to select a client."
    }
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($d in ($eraDirs | Sort-Object)) { $result.Add((Get-FolderCandidate $d)) }
    # Large zips that are not client archives are left out of the list.
    foreach ($z in ($zips | Sort-Object)) {
        $a = Get-ArchiveCandidate $z
        if ($a.IsClientArchive) { $result.Add($a) }
    }
    return , $result
}

# ================================================================== pickers
function Show-FolderPicker {
    param([string]$Description)
    Add-Type -AssemblyName System.Windows.Forms
    $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false }
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = $Description
    $dlg.ShowNewFolderButton = $true
    try {
        if ($dlg.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { return $dlg.SelectedPath }
        return $null
    } finally { $dlg.Dispose(); $owner.Dispose() }
}

function Show-ZipPicker {
    Add-Type -AssemblyName System.Windows.Forms
    $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false }
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = 'Zip archives (*.zip)|*.zip'
    $dlg.Title = 'Select the client archive'
    try {
        if ($dlg.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { return $dlg.FileName }
        return $null
    } finally { $dlg.Dispose(); $owner.Dispose() }
}

# ================================================================== client archive extraction
function Test-ExtractDestination {
    # Returns $null when the destination is acceptable, otherwise the reason.
    param([string]$Dest, [pscustomobject]$Archive)
    if (-not [System.IO.Path]::IsPathRooted($Dest) -or $Dest -notmatch '^[A-Za-z]:\\') { return "Enter a full path that starts with a drive letter, such as D:\Games\Kronos." }
    if ([System.IO.File]::Exists($Dest)) { return "Destination is a file: $Dest" }
    if ([System.IO.Directory]::Exists($Dest) -and @([System.IO.Directory]::EnumerateFileSystemEntries($Dest)).Count -gt 0) {
        return "Destination is not empty: $Dest. Choose an empty or new folder."
    }
    $need = $Archive.UncompressedBytes + $MinFreeBytes
    try { $free = Get-FreeBytes $Dest } catch { return "Drive not available for: $Dest" }
    if ($free -lt $need) { return "Not enough free space on $([System.IO.Path]::GetPathRoot($Dest)): $(Format-Size $need) required, $(Format-Size $free) free." }
    return $null
}

function Select-ExtractDestination {
    param([pscustomobject]$Archive)
    $default = Join-Path ([System.IO.Path]::GetPathRoot($Archive.Path)) 'Games\Kronos'
    if ($ExtractTo) {
        $dest = [System.IO.Path]::GetFullPath($ExtractTo.Trim().Trim('"'))
        $why = Test-ExtractDestination $dest $Archive
        if ($why) { Stop-Install 3 $why 'Choose an empty or new folder on a drive with enough free space, and run the installer again.' }
        return $dest
    }
    while ($true) {
        Out-Log ''
        Out-Log "The client will be extracted to <destination>\World of Warcraft. Extracted size: $(Format-Size $Archive.UncompressedBytes)."
        Out-Log "[1] $default"
        Out-Log '[B] Browse for a folder'
        Out-Log '[T] Type the path'
        Out-Log '[Q] Quit'
        $k = Read-Choice 'Destination' @('1', 'B', 'T') '1'
        $dest = $null
        if ($k -eq '1') { $dest = $default }
        elseif ($k -eq 'B') { $dest = Show-FolderPicker 'Select an empty folder for the extracted client' }
        else { $dest = Read-Line 'Destination folder:' }
        if (-not $dest) { continue }
        try { $dest = [System.IO.Path]::GetFullPath($dest.Trim().Trim('"')) } catch { Out-Log "Not a valid path: $dest"; continue }
        $why = Test-ExtractDestination $dest $Archive
        if (-not $why) { return $dest }
        Out-Log $why
        if ($Yes) { Stop-Install 3 $why 'Choose an empty or new folder on a drive with enough free space, and run the installer again.' }
    }
}

function Expand-ClientArchive {
    param([pscustomobject]$Archive, [string]$Dest)
    $target = Join-Path $Dest 'World of Warcraft'
    $targetFull = [System.IO.Path]::GetFullPath($target).TrimEnd('\') + '\'
    Out-Log "Extracting $($Archive.Path)"
    Out-Log "        to $target"
    Out-Log "$($Archive.EntryCount) entries, $(Format-Size $Archive.UncompressedBytes). This takes several minutes."
    [void][System.IO.Directory]::CreateDirectory($target)
    $script:Extracting = $Dest
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive.Path)
    try {
        $count = 0; $bytes = [long]0; $lastCount = 0; $lastBytes = [long]0; $skipped = 0
        foreach ($e in $zip.Entries) {
            $n = $e.FullName.Replace('\', '/')
            if (-not $n.StartsWith($Archive.Prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            $rel = $n.Substring($Archive.Prefix.Length)
            if ($rel -eq '') { continue }
            $segments = $rel.TrimEnd('/').Split('/')
            if ($rel.StartsWith('/') -or $rel.Contains(':') -or ($segments -contains '..')) {
                Out-Log "Skipped unsafe entry: $($e.FullName)"; $skipped++; continue
            }
            $path = [System.IO.Path]::GetFullPath((Join-Path $target ($rel.Replace('/', '\'))))
            if (-not $path.StartsWith($targetFull, [System.StringComparison]::OrdinalIgnoreCase) -and $path.TrimEnd('\') + '\' -ne $targetFull) {
                Out-Log "Skipped unsafe entry: $($e.FullName)"; $skipped++; continue
            }
            if ($n.EndsWith('/')) { [void][System.IO.Directory]::CreateDirectory($path); continue }
            [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path))
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $path, $false)
            $count++; $bytes += $e.Length
            if ($count - $lastCount -ge 200 -or $bytes - $lastBytes -ge 100MB) {
                Out-Log ('  {0} files, {1} of {2}, {3:mm\:ss} elapsed' -f $count, (Format-Size $bytes), (Format-Size $Archive.UncompressedBytes), $clock.Elapsed)
                $lastCount = $count; $lastBytes = $bytes
            }
        }
        Out-Log ('  {0} files, {1}, finished in {2:mm\:ss}' -f $count, (Format-Size $bytes), $clock.Elapsed)
        if ($skipped -gt 0) { Out-Log "$skipped unsafe entries were not extracted." }
    } finally { $zip.Dispose() }
    $script:Extracting = $null

    $era = Join-Path $target '_classic_era_'
    if (-not [System.IO.File]::Exists((Join-Path $target '.build.info')) -or -not [System.IO.File]::Exists((Join-Path $era 'WowClassic_ForCustomServers.exe'))) {
        Stop-Install 5 "The extracted client in $target is missing .build.info or WowClassic_ForCustomServers.exe." "Delete $Dest and run the installer again."
    }
    $c = Get-FolderCandidate $era
    if (-not $c.Usable) { Stop-Install 5 "The extracted client does not pass the client check: $($c.Status)." "Delete $Dest and run the installer again." }
    return $c
}

# ================================================================== installation state
function Get-State {
    param([string]$RootDir)
    $p = Join-Path $RootDir 'Hermes\quickstart.json'
    if (-not [System.IO.File]::Exists($p)) { return $null }
    try { return (Read-TextFile $p).Text | ConvertFrom-Json } catch { return $null }
}

function Save-State {
    param([pscustomobject]$State)
    $p = Join-Path $State.root 'Hermes\quickstart.json'
    $State.installerVersion = $InstallerVersion
    $State.updatedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    Write-TextFile $p (($State | ConvertTo-Json -Depth 4) + "`r`n")
}

function New-State {
    param([string]$RootDir, [pscustomobject]$Client, $ArchivePath)
    $now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    return [pscustomobject]@{
        installerVersion = $InstallerVersion
        status           = 'installing'
        installedAt      = $now
        updatedAt        = $now
        root             = $RootDir
        clientDir        = $Client.Path
        gameExe          = $Client.GameExe
        clientArchive    = $ArchivePath
        server           = $null
        serverAddress    = $null
        channel          = $null
        addonInstalled   = $false
        addonCreated     = $false
        shortcut         = $null
    }
}

function Get-ProxyProcesses {
    # JimsProxy processes running from this Hermes folder. A process whose path cannot be read
    # is counted too, so the installer never replaces files under a running proxy.
    param([string]$HermesDir)
    $exe = Join-Path $HermesDir 'JimsProxy.exe'
    return @(Get-Process -Name 'JimsProxy' -ErrorAction SilentlyContinue | Where-Object {
        $p = $null
        try { $p = $_.Path } catch { }
        (-not $p) -or ($p -ieq $exe)
    })
}

function Assert-ProxyStopped {
    param([string]$HermesDir, [string]$Action)
    $running = @(Get-ProxyProcesses $HermesDir)
    if ($running.Count -gt 0) {
        Stop-Install 2 "JimsProxy.exe is running (PID $(($running | ForEach-Object { $_.Id }) -join ', ')); $Action is not possible while it runs." 'Exit the game, wait for the Play window to close (or end JimsProxy.exe in Task Manager), and run the installer again.'
    }
}

# ================================================================== steps
function Invoke-Preflight {
    param([bool]$Network, [string]$TargetPath)
    Write-Heading 'Step 1 of 6: Check this PC'
    $os = [Environment]::OSVersion.Version
    if (-not [Environment]::Is64BitOperatingSystem -or $os.Major -lt 10) {
        Stop-Install 2 "This installer requires 64-bit Windows 10 or 11 (found Windows $os, $(if ([Environment]::Is64BitOperatingSystem) { '64' } else { '32' })-bit)." 'Use a 64-bit Windows 10 or 11 PC, or follow docs/MANUAL-INSTALL.md.'
    }
    Out-Log "Windows $os, 64-bit: OK"
    if ($PSVersionTable.PSVersion -lt [version]'5.1') {
        Stop-Install 2 "Windows PowerShell 5.1 or newer is required (found $($PSVersionTable.PSVersion))." 'Install Windows Management Framework 5.1 and run the installer again.'
    }
    Out-Log "PowerShell $($PSVersionTable.PSVersion): OK"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Out-Log 'TLS 1.2: enabled'
    if ($Network) {
        foreach ($url in @($ProxyHost, ($RawBase -replace '^(https?://[^/]+).*$', '$1'))) {
            $reached = $false; $detail = ''
            try {
                Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 10 | Out-Null
                $reached = $true
            } catch [System.Net.WebException] {
                # Any HTTP response, including an error status, proves the host is reachable.
                if ($null -ne $_.Exception.Response) { $reached = $true } else { $detail = $_.Exception.Message }
            } catch { $detail = $_.Exception.Message }
            if (-not $reached) {
                Stop-Install 2 "$url is not reachable: $detail" 'Check the internet connection, firewall, or proxy settings, and run the installer again.'
            }
            Out-Log "$url reachable: OK"
        }
    }
    $probe = if ($TargetPath) { $TargetPath } else { $env:TEMP }
    $free = Get-FreeBytes $probe
    if ($free -lt $MinFreeBytes) {
        Stop-Install 2 "Only $(Format-Size $free) free on $([System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($probe))); at least $(Format-Size $MinFreeBytes) is required." 'Free disk space and run the installer again.'
    }
    Out-Log "Free space on $([System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($probe))): $(Format-Size $free): OK"
}

function Write-CandidateMenu {
    param($Candidates)
    $n = 0
    foreach ($c in $Candidates) {
        if ($c.Usable) { $n++; Out-Log "[$n] $($c.Path)    $($c.Status)" }
        else { Out-Log "[-] $($c.Path)    $($c.Status)" }
    }
    if ($Candidates.Count -eq 0) { Out-Log 'No client was found in the scanned locations.' }
    Out-Log '[B] Browse for the _classic_era_ folder'
    Out-Log '[A] Use a client archive (.zip)'
    Out-Log '[T] Type the path (folder or .zip)'
    Out-Log '[Q] Quit'
}

$RequirementText = "A WoW Classic Era 1.14.2 client, build 42597, with WowClassic_ForCustomServers.exe, as a folder or as a client archive, is required."

function Resolve-UserChoice {
    # Turns a typed or picked path into a candidate; folders and .zip files both work.
    param([string]$Path)
    if (-not $Path) { return $null }
    $Path = $Path.Trim().Trim('"')
    if ($Path -match '\.zip$') {
        if (-not [System.IO.File]::Exists($Path)) { Out-Log "File not found: $Path"; return $null }
        return Get-ArchiveCandidate ([System.IO.Path]::GetFullPath($Path))
    }
    $era = Resolve-EraFolder $Path
    if (-not $era) { Out-Log "No _classic_era_ folder found at: $Path"; return $null }
    return Get-FolderCandidate $era
}

function Select-Client {
    # Returns @{ Client; Archive } where Client is a usable folder candidate. An archive is
    # only extracted after the whole selection (including the destination) is settled.
    Write-Heading 'Step 2 of 6: Find the client'
    if ($ClientDir) {
        $era = Resolve-EraFolder $ClientDir
        if (-not $era) { Stop-Install 3 "No _classic_era_ folder found at: $ClientDir" $RequirementText }
        $c = Get-FolderCandidate $era
        Out-Log "$($c.Path)    $($c.Status)"
        if (-not $c.Usable) { Stop-Install 3 "The client at $($c.Path) is $($c.Status)." $RequirementText }
        return @{ Client = $c; Archive = $null }
    }
    if ($ClientArchive) {
        if (-not [System.IO.File]::Exists($ClientArchive)) { Stop-Install 3 "File not found: $ClientArchive" $RequirementText }
        $a = Get-ArchiveCandidate ([System.IO.Path]::GetFullPath($ClientArchive))
        Out-Log "$($a.Path)    $($a.Status)"
        if (-not $a.Usable) { Stop-Install 3 "The archive $($a.Path) is $($a.Status)." $RequirementText }
        return @{ Client = $null; Archive = $a }
    }

    Out-Log "Scanning for clients (at most $ScanSeconds seconds)..."
    $candidates = Find-Candidates
    while ($true) {
        Out-Log ''
        Write-CandidateMenu $candidates
        $usable = @($candidates | Where-Object { $_.Usable })
        $keys = @()
        for ($i = 1; $i -le $usable.Count; $i++) { $keys += "$i" }
        $keys += 'B', 'A', 'T'
        $default = if ($usable.Count -gt 0) { '1' } else { 'B' }
        if ($Yes -and $usable.Count -eq 0) { Stop-Install 3 'No usable client was found.' $RequirementText }
        $k = Read-Choice 'Select' $keys $default
        $picked = $null
        switch ($k) {
            'B' { $picked = Resolve-UserChoice (Show-FolderPicker 'Select the _classic_era_ folder (or the folder that contains it)') }
            'A' { $picked = Resolve-UserChoice (Show-ZipPicker) }
            'T' { $picked = Resolve-UserChoice (Read-Line 'Path to the _classic_era_ folder or the client archive:') }
            default { $picked = $usable[[int]$k - 1] }
        }
        if ($null -eq $picked) { continue }
        if (-not $picked.Usable) { Out-Log "Cannot use $($picked.Path): $($picked.Status). $RequirementText"; continue }
        if ($picked.Kind -eq 'archive') { return @{ Client = $null; Archive = $picked } }
        return @{ Client = $picked; Archive = $null }
    }
}

function Select-ServerAndChannel {
    param([string]$CurrentServer, [string]$CurrentChannel)
    Write-Heading 'Step 3 of 6: Server and channel'
    $address = $null; $key = $null
    if ($Server) {
        $match = @($Servers | Where-Object { $_.Key -ieq $Server })
        if ($match.Count -gt 0) { $key = $match[0].Key; $address = $match[0].Address }
        elseif (Test-ServerAddress $Server) { $key = 'other'; $address = $Server }
        else { Stop-Install 1 "-Server must be kronos, kronos2, kronos3, or a hostname or IP address (got '$Server')." 'Correct the parameter and run the installer again.' }
        Out-Log "Server: $address"
    } else {
        $i = 0
        foreach ($s in $Servers) { $i++; Out-Log "[$i] $($s.Name) ($($s.Address))" }
        Out-Log '[4] Other address'
        Out-Log '[Q] Quit'
        $default = '1'
        if ($CurrentServer) {
            $idx = [array]::IndexOf(@($Servers | ForEach-Object { $_.Address }), $CurrentServer)
            $default = if ($idx -ge 0) { "$($idx + 1)" } else { '4' }
        }
        $k = Read-Choice 'Server' @('1', '2', '3', '4') $default
        if ($k -eq '4') {
            if ($Yes -and $CurrentServer) { $address = $CurrentServer }
            else {
                while ($true) {
                    $a = Read-Line 'Login address (hostname or IP):'
                    if (Test-ServerAddress $a) { $address = $a; break }
                    Out-Log 'Enter a hostname such as login.example.com or an IPv4 address such as 192.0.2.10.'
                }
            }
            $key = 'other'
        } else {
            $s = $Servers[[int]$k - 1]; $key = $s.Key; $address = $s.Address
        }
    }

    $ch = $null
    if ($Channel) {
        if ($Channel -notin @('stable', 'beta')) { Stop-Install 1 "-Channel must be stable or beta (got '$Channel')." 'Correct the parameter and run the installer again.' }
        $ch = $Channel.ToLowerInvariant()
        Out-Log "Channel: $ch"
    } else {
        Out-Log ''
        Out-Log '[1] Stable: the current release'
        Out-Log '[2] Beta: newer changes, less testing'
        Out-Log '[Q] Quit'
        $default = if ($CurrentChannel -eq 'beta') { '2' } else { '1' }
        $ch = if ((Read-Choice 'Channel' @('1', '2') $default) -eq '2') { 'beta' } else { 'stable' }
    }
    return @{ Server = $key; Address = $address; Channel = $ch }
}

function Test-ServerAddress {
    param([string]$Address)
    if (-not $Address) { return $false }
    if ($Address -match '^\d{1,3}(\.\d{1,3}){3}$') {
        foreach ($o in $Address.Split('.')) { if ([int]$o -gt 255) { return $false } }
        return $true
    }
    return ($Address.Length -le 253 -and $Address -match '^(?=.*[A-Za-z])[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$')
}

function Get-ProxyManifest {
    # Returns latest.json as { version, url, sha256 }, or $null when it cannot be read (the
    # download then uses /latest, without a checksum). A paused channel stops the install.
    param([string]$Chan)
    $src = $ProxyManifestUrls[$Chan]
    try { $r = Invoke-WebRequest -Uri $src -UseBasicParsing -TimeoutSec 30 }
    catch { Out-Log "  WARNING: $src could not be read ($($_.Exception.Message))"; return $null }
    if ($r.StatusCode -eq 204) { Stop-Install 4 "The $Chan channel is paused on the download server." 'Run the installer again later, or choose the other channel.' }
    try { $m = $r.Content | ConvertFrom-Json } catch { Out-Log "  WARNING: $src did not return JSON"; return $null }
    $prefix = "$ProxyHost/proxy/$Chan/"
    $fields = @($m.PSObject.Properties.Name)
    if (-not ($fields -contains 'url' -and $fields -contains 'sha256' -and $fields -contains 'version') -or
        -not "$($m.url)".StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -or "$($m.url)" -notmatch '\.zip$' -or
        "$($m.sha256)" -notmatch '^[0-9a-fA-F]{64}$') {
        Out-Log "  WARNING: $src has an unexpected format"
        return $null
    }
    return [pscustomobject]@{ version = "$($m.version)"; url = "$($m.url)"; sha256 = "$($m.sha256)" }
}

function Get-ProxyArchive {
    # Downloads and checks the proxy archive; returns the folder holding its contents
    # (JimsProxy.exe at the top). Nothing in Hermes is touched here.
    param([string]$Chan)
    $manifest = Get-ProxyManifest $Chan
    $url = if ($manifest) { $manifest.url } else { $ProxyUrls[$Chan] }
    $zipPath = New-TempPath '.zip'
    Out-Log "Downloading the $Chan proxy archive$(if ($manifest) { " $($manifest.version)" }) from $url"
    try { Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing -TimeoutSec 120 }
    catch {
        $status = $null
        if ($_.Exception -is [System.Net.WebException] -and $null -ne $_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($status -eq 503) { Stop-Install 4 "The $Chan channel is paused on the download server (HTTP 503)." 'Run the installer again later, or choose the other channel.' }
        Stop-Install 4 "Download failed: $($_.Exception.Message)" 'Check the internet connection and run the installer again.'
    }
    $size = (Get-Item -LiteralPath $zipPath).Length
    Out-Log "  downloaded $(Format-Size $size)"

    if ($manifest) {
        $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        if ($actual -ne $manifest.sha256.ToUpperInvariant()) {
            Stop-Install 5 "SHA-256 mismatch for the proxy archive (published $($manifest.sha256.ToUpperInvariant()), downloaded $actual)." 'Run the installer again; if it repeats, report it.'
        }
        Out-Log '  SHA-256 matches the published checksum'
    } else {
        Out-Log '  WARNING: SHA-256 not verified (the published checksum could not be read); the archive contents are still checked'
    }

    $prefix = $null
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            foreach ($n in $names) {
                $m = [regex]::Match($n, '^([^/]+/)?JimsProxy\.exe$', 'IgnoreCase')
                if ($m.Success) { $prefix = $m.Groups[1].Value; break }
            }
            if ($null -eq $prefix) { throw 'JimsProxy.exe is not in the archive' }
            if (-not ($names -icontains "${prefix}manifest.json")) { throw 'manifest.json is not in the archive' }
            if (-not @($names | Where-Object { $_.StartsWith("${prefix}CSV/", [System.StringComparison]::OrdinalIgnoreCase) }).Count) { throw 'the CSV folder is not in the archive' }
        } finally { $zip.Dispose() }
    } catch {
        Stop-Install 5 "The downloaded file is not a valid proxy archive ($($_.Exception.Message))." 'Run the installer again later; the download address may have returned an error page.'
    }
    $stage = New-TempPath ''
    Expand-Archive -LiteralPath $zipPath -DestinationPath $stage -Force
    $source = if ($prefix) { Join-Path $stage $prefix.TrimEnd('/') } else { $stage }
    if ($prefix) { Out-Log "  the archive holds its files in the sub-folder $($prefix.TrimEnd('/')); using its contents" }
    Out-Log '  archive contents verified'
    return $source
}

function Install-ProxyFiles {
    # Copies the archive contents into Hermes. HermesProxy.config and AccountData are never
    # overwritten; CSV, Addons, and any other folder from the archive replace the old copy.
    param([string]$Source, [string]$HermesDir)
    foreach ($item in @(Get-ChildItem -LiteralPath $Source -Force)) {
        if ($item.Name -ieq 'HermesProxy.config' -or $item.Name -ieq 'AccountData') { continue }
        $dest = Join-Path $HermesDir $item.Name
        if ($item.PSIsContainer) {
            if ([System.IO.Directory]::Exists($dest)) { Remove-Tree $dest }
            Copy-Item -LiteralPath $item.FullName -Destination $dest -Recurse -Force
        } else {
            Copy-Item -LiteralPath $item.FullName -Destination $dest -Force
        }
    }
    foreach ($required in 'JimsProxy.exe', 'CSV', 'manifest.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $HermesDir $required))) {
            Stop-Install 5 "$required is missing from $HermesDir after extraction." 'Run the installer again.'
        }
    }
    Out-Log "  installed JimsProxy.exe, CSV, manifest.json$(if ([System.IO.Directory]::Exists((Join-Path $HermesDir 'Addons'))) { ', Addons' })"
    if (-not [System.IO.File]::Exists((Join-Path $HermesDir 'Addons\JimsPlus\JimsPlus.toc'))) {
        Out-Log '  WARNING: the archive has no Addons\JimsPlus; the addon cannot be installed from it.'
    }
}

function Get-ProxyVersion {
    param([string]$HermesDir)
    try { return ((Read-TextFile (Join-Path $HermesDir 'manifest.json')).Text | ConvertFrom-Json).version } catch { return 'unknown' }
}

function Save-RemoteFile {
    # Downloads to a temporary file, checks it contains $MustContain, then moves it into place.
    param([string]$Url, [string]$Dest, [string]$MustContain)
    $tmp = New-TempPath ([System.IO.Path]::GetExtension($Dest))
    try { Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing -TimeoutSec 60 }
    catch { Stop-Install 4 "Download of $([System.IO.Path]::GetFileName($Dest)) failed: $($_.Exception.Message)" 'Check the internet connection and run the installer again.' }
    if (-not (Read-TextFile $tmp).Text.Contains($MustContain)) {
        Stop-Install 5 "The downloaded $([System.IO.Path]::GetFileName($Dest)) is not the expected file." 'Run the installer again later; the download address may have returned an error page.'
    }
    Move-Item -LiteralPath $tmp -Destination $Dest -Force
    Out-Log "  downloaded $([System.IO.Path]::GetFileName($Dest))"
}

function Set-ServerAddress {
    param([string]$HermesDir, [string]$Address)
    $cfg = Join-Path $HermesDir 'HermesProxy.config'
    $f = Read-TextFile $cfg
    $re = '(<add\s+key="ServerAddress"\s+value=")[^"]*(")'
    if (-not [regex]::IsMatch($f.Text, $re)) { Stop-Install 5 "HermesProxy.config has no ServerAddress entry." "Delete $cfg and run the installer again to download a fresh copy." }
    $new = [regex]::Replace($f.Text, $re, { param($m) $m.Groups[1].Value + $Address + $m.Groups[2].Value })
    if ($new -ne $f.Text) { Write-TextFile $cfg $new $f.Bom }
    Out-Log "  ServerAddress = $Address"
}

function Install-PlayFiles {
    param([pscustomobject]$State)
    $hermes = Join-Path $State.root 'Hermes'
    Save-RemoteFile $PlayBatUrl (Join-Path $hermes 'play.bat') 'play.ps1'
    Save-RemoteFile $PlayPs1Url (Join-Path $hermes 'play.ps1') $ReadyLine
    # The game path is written relative to this file, so the command keeps working if the
    # whole root folder is moved. It is ASCII by construction (see Get-InstallRoot).
    $rel = $State.gameExe.Substring($State.root.TrimEnd('\').Length).TrimStart('\')
    $cmd = @(
        '@echo off',
        'rem Starts JimsProxy, then the game; stops the proxy when the game exits. Written by the JimsProxy quick-start installer.',
        "powershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0play.ps1`" -GameExe `"%~dp0..\$rel`" %*",
        'if errorlevel 1 pause'
    ) -join "`r`n"
    [System.IO.File]::WriteAllText((Join-Path $hermes $PlayCmdName), $cmd + "`r`n", [System.Text.Encoding]::ASCII)
    Out-Log "  wrote $PlayCmdName"
}

function Set-PortalLine {
    param([string]$EraDir, [int]$Port)
    $wtfDir = Join-Path $EraDir 'WTF'
    $wtf = Join-Path $wtfDir 'Config.wtf'
    $line = "SET portal `"127.0.0.1:$Port`""
    if (-not [System.IO.File]::Exists($wtf)) {
        [void][System.IO.Directory]::CreateDirectory($wtfDir)
        Write-TextFile $wtf ($line + "`r`n")
        Out-Log "  created $wtf with $line"
        return
    }
    $f = Read-TextFile $wtf
    $nl = if ($f.Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.AddRange([string[]]($f.Text -split "`r?`n"))
    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*SET\s+portal\s') {
            if (-not $found) { $lines[$i] = $line; $found = $true } else { $lines.RemoveAt($i); $i-- }
        }
    }
    if (-not $found) {
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') { $lines.Insert($lines.Count - 1, $line) } else { $lines.Add($line) }
    }
    $new = $lines -join $nl
    if ($new -eq $f.Text) { Out-Log "  Config.wtf already has $line"; return }
    Copy-Item -LiteralPath $wtf -Destination "$wtf.bak" -Force
    Write-TextFile $wtf $new $f.Bom
    Out-Log "  Config.wtf: $line (previous file kept as Config.wtf.bak)"
}

function Remove-PortalLine {
    # Removes only a portal line that points at a local proxy.
    param([string]$EraDir)
    $wtf = Join-Path $EraDir 'WTF\Config.wtf'
    if (-not [System.IO.File]::Exists($wtf)) { return }
    $f = Read-TextFile $wtf
    $nl = if ($f.Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $kept = @(($f.Text -split "`r?`n") | Where-Object { $_ -notmatch '^\s*SET\s+portal\s+"127\.0\.0\.1:\d+"\s*$' })
    $new = $kept -join $nl
    if ($new -eq $f.Text) { return }
    Copy-Item -LiteralPath $wtf -Destination "$wtf.bak" -Force
    Write-TextFile $wtf $new $f.Bom
    Out-Log '  removed the SET portal line from Config.wtf (previous file kept as Config.wtf.bak)'
}

function Install-Addon {
    param([pscustomobject]$State)
    $src = Join-Path $State.root 'Hermes\Addons\JimsPlus'
    if (-not [System.IO.File]::Exists((Join-Path $src 'JimsPlus.toc'))) { Out-Log '  JimsPlus is not in the proxy archive; addon not installed'; return $false }
    $dest = Join-Path $State.clientDir 'Interface\AddOns\JimsPlus'
    $created = -not [System.IO.Directory]::Exists($dest)
    [void][System.IO.Directory]::CreateDirectory($dest)
    Copy-Item -Path (Join-Path ([WildcardPattern]::Escape($src)) '*') -Destination $dest -Recurse -Force
    if ($created) { $State.addonCreated = $true }
    Out-Log "  JimsPlus copied to $dest"
    return $true
}

function New-DesktopShortcut {
    param([pscustomobject]$State)
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnk = Join-Path $desktop $ShortcutName
    $hermes = Join-Path $State.root 'Hermes'
    $shell = New-Object -ComObject WScript.Shell
    try {
        $s = $shell.CreateShortcut($lnk)
        $s.TargetPath = Join-Path $hermes $PlayCmdName
        $s.WorkingDirectory = $hermes
        $s.IconLocation = "$($State.gameExe),0"
        $s.Description = 'Start JimsProxy and the game'
        $s.Save()
    } finally { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
    Out-Log "  shortcut: $lnk"
    return $lnk
}

function Write-Summary {
    param([pscustomobject]$State)
    $hermes = Join-Path $State.root 'Hermes'
    Out-Log ''
    Out-Log "Installation folder  $hermes"
    Out-Log "Proxy version        $(Get-ProxyVersion $hermes)"
    Out-Log "Client folder        $($State.clientDir)"
    Out-Log "Game executable      $($State.gameExe)"
    if ($State.clientArchive) { Out-Log "Client archive       $($State.clientArchive) (not modified)" }
    Out-Log "Server               $($State.serverAddress)"
    Out-Log "Channel              $($State.channel)"
    Out-Log "JimsPlus addon       $(if ($State.addonInstalled) { 'installed' } else { 'not installed' })"
    Out-Log "Desktop shortcut     $(if ($State.shortcut) { $State.shortcut } else { 'not created' })"
    Out-Log "Play command         $(Join-Path $hermes $PlayCmdName)"
    Out-Log "Log                  $($script:LogPath)"
}

# ================================================================== flows
function Invoke-Install {
    param([hashtable]$Selection)
    $client = $Selection.Client
    $archive = $Selection.Archive
    if ($archive) { $client = Expand-ClientArchive $archive (Select-ExtractDestination $archive) }
    $settings = Select-ServerAndChannel $null $null
    $rootDir = Get-InstallRoot $client.Path
    $hermes = Join-Path $rootDir 'Hermes'

    Write-Heading 'Step 4 of 6: Install the proxy'
    $free = Get-FreeBytes $rootDir
    if ($free -lt $MinFreeBytes) { Stop-Install 2 "Only $(Format-Size $free) free on $([System.IO.Path]::GetPathRoot($rootDir)); $(Format-Size $MinFreeBytes) is required." 'Free disk space and run the installer again.' }
    $source = Get-ProxyArchive $settings.Channel

    $state = Get-State $rootDir
    if ($null -eq $state) { $state = New-State $rootDir $client $(if ($archive) { $archive.Path } else { $null }) }
    $state.clientDir = $client.Path; $state.gameExe = $client.GameExe
    $state.server = $settings.Server; $state.serverAddress = $settings.Address; $state.channel = $settings.Channel
    [void][System.IO.Directory]::CreateDirectory($hermes)
    Set-LogFile (Join-Path $hermes 'install.log')
    Save-State $state
    Out-Log "  installing into $hermes"

    Install-ProxyFiles $source $hermes
    $cfg = Join-Path $hermes 'HermesProxy.config'
    if ([System.IO.File]::Exists($cfg)) { Out-Log '  HermesProxy.config exists; kept' }
    else { Save-RemoteFile $ConfigUrl $cfg '<add key="ServerAddress"' }
    Set-ServerAddress $hermes $settings.Address
    Install-PlayFiles $state
    Save-State $state

    Write-Heading 'Step 5 of 6: Connect the game'
    $bnetPort = Get-ConfigValue (Read-TextFile $cfg).Text 'BNetPort'
    if (-not $bnetPort) { $bnetPort = '1119' }
    Set-PortalLine $state.clientDir ([int]$bnetPort)
    if ($NoAddon) { Out-Log 'JimsPlus addon: skipped (-NoAddon)' }
    elseif (Read-YesNo 'Install the JimsPlus addon?' $true) { $state.addonInstalled = Install-Addon $state }
    if ($NoShortcut) { Out-Log 'Desktop shortcut: skipped (-NoShortcut)' }
    elseif (Read-YesNo 'Create a desktop shortcut "Play Kronos"?' $true) { $state.shortcut = New-DesktopShortcut $state }
    $state.status = 'installed'
    Save-State $state

    Write-Heading 'Step 6 of 6: Done'
    Write-Summary $state
    Out-Log ''
    if ($Yes) { Out-Log 'The game is not started in unattended mode (-Yes).'; return }
    if (Read-YesNo 'Start the game now?' $true) {
        Start-Process -FilePath (Join-Path $hermes $PlayCmdName) -WorkingDirectory $hermes
        Out-Log 'Started the Play command in a new window.'
    }
}

function Invoke-Update {
    param([pscustomobject]$State)
    $hermes = Join-Path $State.root 'Hermes'
    Write-Heading 'Update the proxy'
    Assert-ProxyStopped $hermes 'updating'
    $before = Get-ProxyVersion $hermes
    $source = Get-ProxyArchive $State.channel
    Install-ProxyFiles $source $hermes
    $cfg = Join-Path $hermes 'HermesProxy.config'
    if (-not [System.IO.File]::Exists($cfg)) {
        Save-RemoteFile $ConfigUrl $cfg '<add key="ServerAddress"'
        Set-ServerAddress $hermes $State.serverAddress
    }
    Install-PlayFiles $State
    if ($State.addonInstalled) { [void](Install-Addon $State) }
    Save-State $State
    Out-Log ''
    Out-Log "Proxy updated: $before -> $(Get-ProxyVersion $hermes) ($($State.channel) channel). HermesProxy.config and AccountData were not changed."
}

function Invoke-Reconfigure {
    param([pscustomobject]$State)
    $hermes = Join-Path $State.root 'Hermes'
    $settings = Select-ServerAndChannel $State.serverAddress $State.channel
    Write-Heading 'Reconfigure'
    $cfg = Join-Path $hermes 'HermesProxy.config'
    if (-not [System.IO.File]::Exists($cfg)) { Save-RemoteFile $ConfigUrl $cfg '<add key="ServerAddress"' }
    Set-ServerAddress $hermes $settings.Address
    $changedChannel = ($settings.Channel -ne $State.channel)
    $State.server = $settings.Server; $State.serverAddress = $settings.Address; $State.channel = $settings.Channel
    Save-State $State
    Out-Log "  channel = $($settings.Channel)$(if ($changedChannel) { ' (takes effect at the next update)' })"
    Out-Log 'Restart the proxy (end the Play session and start it again) for the change to take effect.'
}

function Invoke-Uninstall {
    param([pscustomobject]$State, [bool]$Confirm)
    $hermes = Join-Path $State.root 'Hermes'
    Write-Heading 'Uninstall'
    Assert-ProxyStopped $hermes 'uninstalling'
    if ($Confirm -and -not (Read-YesNo "Remove JimsProxy from $($State.root)?" $false)) { Stop-Install 6 'Cancelled; nothing was removed.' $null }

    $accountData = Join-Path $hermes 'AccountData'
    if ([System.IO.Directory]::Exists($accountData) -and @([System.IO.Directory]::EnumerateFileSystemEntries($accountData)).Count -gt 0) {
        if (Read-YesNo "Move AccountData to $($State.root)\JimsProxy-AccountData-backup before deleting?" $true) {
            $backup = Join-Path $State.root 'JimsProxy-AccountData-backup'
            if (Test-Path -LiteralPath $backup) { $backup = "$backup-$((Get-Date).ToString('yyyyMMdd-HHmmss'))" }
            Move-Item -LiteralPath $accountData -Destination $backup
            Out-Log "  AccountData moved to $backup"
        }
    }
    Set-LogFile (Join-Path $env:TEMP 'jimsproxy-quickstart.log')
    Remove-Tree $hermes
    Out-Log "  deleted $hermes"
    if ($State.addonCreated) {
        $addon = Join-Path $State.clientDir 'Interface\AddOns\JimsPlus'
        if ([System.IO.Directory]::Exists($addon)) { Remove-Tree $addon; Out-Log "  deleted $addon" }
    } elseif ($State.addonInstalled) {
        Out-Log '  JimsPlus was present before the installation; left in place'
    }
    Remove-PortalLine $State.clientDir
    if ($State.shortcut -and [System.IO.File]::Exists($State.shortcut)) { Remove-Item -LiteralPath $State.shortcut -Force; Out-Log "  deleted $($State.shortcut)" }
    Out-Log ''
    Out-Log 'JimsProxy is uninstalled. The game client was not modified beyond the items listed above.'
}

# ================================================================== main
$exitCode = 0
try {
    try { [System.IO.File]::WriteAllText($script:LogPath, '', $Utf8NoBom) } catch { }
    Out-Log "JimsProxy quick-start installer $InstallerVersion"
    Out-Log "Started $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')), log: $($script:LogPath)"

    $actions = @($Update, $Reconfigure, $Uninstall | Where-Object { $_ })
    if ($actions.Count -gt 1) { Stop-Install 1 'Use only one of -Update, -Reconfigure, -Uninstall.' 'Correct the parameters and run the installer again.' }
    if ($ClientDir -and $ClientArchive) { Stop-Install 1 'Use either -ClientDir or -ClientArchive, not both.' 'Correct the parameters and run the installer again.' }
    if ($Channel -and $Channel -notin @('stable', 'beta')) { Stop-Install 1 "-Channel must be stable or beta (got '$Channel')." 'Correct the parameters and run the installer again.' }

    $state = $null
    if ($Root) {
        $state = Get-State ([System.IO.Path]::GetFullPath($Root.Trim().Trim('"')))
        if ($null -eq $state) { Stop-Install 3 "No quick-start installation found in $Root (Hermes\quickstart.json is missing)." 'Pass the folder that contains Hermes, or run the installer without -Root.' }
    }
    $needsNetwork = -not ($Uninstall -or $Reconfigure)
    $probe = if ($Root) { $Root } elseif ($ClientDir) { $ClientDir } else { $null }
    if ($probe -and -not (Test-Path -LiteralPath $probe)) { $probe = $null }
    Invoke-Preflight $needsNetwork $probe

    $selection = $null
    while ($null -eq $state) {
        $selection = Select-Client
        if ($selection.Archive) { break }
        $rootDir = Get-InstallRoot $selection.Client.Path
        $existing = Get-State $rootDir
        $hermes = Join-Path $rootDir 'Hermes'
        if ($existing -and $existing.status -eq 'installed') { $state = $existing; break }
        if ($existing) { Out-Log "Resuming the unfinished installation in $hermes."; break }
        if ([System.IO.Directory]::Exists($hermes)) {
            $why = "$hermes already exists and was not created by this installer (a launcher or manual installation). The installer does not modify it."
            if ($ClientDir -or $Yes) { Stop-Install 3 $why 'Select a client whose root folder has no Hermes folder, or remove that installation first.' }
            Out-Log $why
            Out-Log 'Select another client.'
            continue
        }
        if ($Update -or $Reconfigure -or $Uninstall) { Stop-Install 3 "No quick-start installation found for $($selection.Client.Path)." 'Run the installer without -Update, -Reconfigure, or -Uninstall to install.' }
        break
    }

    if ($null -ne $state) {
        # Maintenance of an existing installation. Log into its Hermes folder from here on.
        $hermesLog = Join-Path $state.root 'Hermes\install.log'
        if ([System.IO.Directory]::Exists((Split-Path -Parent $hermesLog))) { Set-LogFile $hermesLog }
        if ($Update) { Invoke-Update $state }
        elseif ($Reconfigure) { Invoke-Reconfigure $state }
        elseif ($Uninstall) { Invoke-Uninstall $state $false }
        else {
            Write-Heading "Existing installation in $($state.root)\Hermes"
            Out-Log "Proxy $(Get-ProxyVersion (Join-Path $state.root 'Hermes')), $($state.channel) channel, server $($state.serverAddress)"
            Out-Log '[1] Update the proxy'
            Out-Log '[2] Reconfigure (server, channel)'
            Out-Log '[3] Uninstall'
            Out-Log '[4] Quit'
            $k = Read-Choice 'Select' @('1', '2', '3', '4') '1'
            switch ($k) {
                '1' { Invoke-Update $state }
                '2' { Invoke-Reconfigure $state }
                '3' { Invoke-Uninstall $state $true }
                '4' { Stop-Install 6 'Cancelled; nothing was changed.' $null }
            }
        }
    } else {
        Invoke-Install $selection
    }
    $script:Finished = $true
}
catch {
    $script:Finished = $true
    Out-Log ''
    Out-Log "Stopped at: $($script:Step)"
    Out-Log "Unexpected error: $($_.Exception.Message)"
    Out-Log "  at $($_.InvocationInfo.PositionMessage -replace "`r?`n", ' ')"
    Out-Log "Log:        $($script:LogPath)"
    Out-Log 'Next:       Run the installer again; a partial installation is completed on the next run. If it repeats, report it with the log.'
    $exitCode = 1
}
finally {
    if ($script:Extracting) {
        Out-Log ''
        Out-Log "The extraction did not finish. $($script:Extracting) holds an incomplete client and must be deleted before the installer is run again."
    }
    foreach ($p in $script:TempPaths) {
        try {
            if ([System.IO.Directory]::Exists($p)) { Remove-Item -LiteralPath $p -Recurse -Force }
            elseif ([System.IO.File]::Exists($p)) { Remove-Item -LiteralPath $p -Force }
        } catch { }
    }
    if (-not $script:Finished) {
        # Ctrl+C: PowerShell would report exit code 0. Report "cancelled" instead, but only
        # when this script is the process (started with -File), never in an interactive shell.
        Out-Log 'Cancelled (Ctrl+C).'
        if ([Environment]::CommandLine -match [regex]::Escape($PSCommandPath)) { [Environment]::Exit(6) }
    }
}
exit $exitCode

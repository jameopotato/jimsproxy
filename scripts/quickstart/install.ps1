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
  files in %TEMP%. It never downloads, patches, or deletes game files; it copies or extracts a
  client only when that is selected.

  Exit codes: 0 success, 1 unexpected error, 2 preflight failed, 3 no usable client selected,
  4 download failed, 5 verification failed, 6 cancelled.
#>
[CmdletBinding()]
param(
    [string]$ClientDir,
    [string]$ClientArchive,
    [string]$ExtractTo,
    [string]$CopyTo,
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
$InstallerVersion = '1.1.2'
$GuideUrl         = 'https://jimothy.cc/install/guide/quick-start'
$LauncherUrl      = 'https://jimothy.cc/install'
$RequiredBuild    = '1.14.2.42597'
$ReadyLine        = 'Starting WorldSocket service'
$ProxyHost        = 'https://jimothy.cc'
$RawBase          = 'https://raw.githubusercontent.com/jameopotato/jimsproxy/master'
$Servers = @(
    [pscustomobject]@{ Key = 'kronos';  Name = 'Kronos';   Address = 'login.twinstar-wow.com' },
    [pscustomobject]@{ Key = 'kronos2'; Name = 'Kronos mirror 2'; Address = 'login2.twinstar-wow.com' },
    [pscustomobject]@{ Key = 'kronos3'; Name = 'Kronos mirror 3'; Address = 'login3.twinstar-wow.com' }
)
$MinFreeBytes     = 500MB
$ScanSeconds      = 20
$ClientDepth      = 4       # folder levels below each scan root searched for _classic_era_
$MinArchiveBytes  = 1GB     # zips at or below this size are not opened during the scan
$CloudOnlyAttributes = 0x1000 -bor 0x40000 -bor 0x400000   # Offline, RecallOnOpen, RecallOnDataAccess
$PlayCmdName      = 'Play Kronos.cmd'

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
$script:Extracting = $null      # destination folder while a client is being extracted or copied
$script:ExtractVerb = 'extraction'
$script:Finished  = $false      # false in the final block only when Ctrl+C stopped the script

# ================================================================== output and input
$Glyph = @{
    Check = [string][char]0x221A; Arrow = [string][char]0x25BA; Rule = [string][char]0x2500
    TL = [string][char]0x250C; TR = [string][char]0x2510; BL = [string][char]0x2514; BR = [string][char]0x2518; V = [string][char]0x2502
    Full = [string][char]0x2588; Light = [string][char]0x2591
}   # all present in Consolas and the console's default code page

function Write-LogOnly {
    param([string]$Text = '')
    try { [System.IO.File]::AppendAllText($script:LogPath, $Text + "`r`n", $Utf8NoBom) } catch { }
}

function Out-Log {
    # Console and log. Colors are for the console only; the log stays plain text. A line that
    # starts with WARNING is yellow unless a color is given.
    param([string]$Text = '', [string]$Color)
    End-ProgressBar
    if (-not $Color -and $Text.TrimStart().StartsWith('WARNING')) { $Color = 'Yellow' }
    if ($Color) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text }
    Write-LogOnly $Text
}

function Write-Check {
    # A passed check: green checkmark, then the text.
    param([string]$Text)
    End-ProgressBar
    Write-Host "   $($Glyph.Check) " -NoNewline -ForegroundColor Green
    Write-Host $Text
    Write-LogOnly "   $($Glyph.Check) $Text"
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
    # "Step 2 of 6: Find the client" is shown as a dim step counter and a bright name over a
    # thin rule; other headings are bright text over the rule.
    param([string]$Title)
    $script:Step = $Title
    End-ProgressBar
    Out-Log ''
    $m = [regex]::Match($Title, '^(Step \d of \d): (.+)$')
    if ($m.Success) {
        $shown = "$($m.Groups[1].Value)  $($m.Groups[2].Value)"
        Write-Host "$($m.Groups[1].Value)  " -NoNewline -ForegroundColor DarkCyan
        Write-Host $m.Groups[2].Value -ForegroundColor Cyan
    } else {
        $shown = $Title
        Write-Host $Title -ForegroundColor Cyan
    }
    Write-Host ($Glyph.Rule * [Math]::Max(28, $shown.Length)) -ForegroundColor DarkCyan
    Write-LogOnly $Title
    Write-LogOnly ('-' * $Title.Length)
}

function Write-Failure {
    # The failure block: red where it stopped and why, white for what to do next.
    param([string]$Reason, [string]$Next)
    End-ProgressBar
    Out-Log ''
    Out-Log "Stopped at: $($script:Step)" -Color Red
    Out-Log "Reason:     $Reason" -Color Red
    if ($Next) { Out-Log "Next:       $Next" -Color White }
    Out-Log "Log:        $($script:LogPath)" -Color DarkGray
}

function Stop-Install {
    # Every failure names the step, the reason, the log, and one next action.
    param([int]$Code, [string]$Reason, [string]$Next)
    Write-Failure $Reason $Next
    $script:Finished = $true
    exit $Code
}

$script:BarShown = $false
$script:BarDrawnAt = [long]0
function Update-ProgressBar {
    # One console line rewritten in place while a client is copied or extracted. Throttled so a
    # client of many small files does not spend its time redrawing.
    param([long]$Done, [long]$Total, [System.Diagnostics.Stopwatch]$Clock)
    if ($script:BarShown -and $Clock.ElapsedMilliseconds - $script:BarDrawnAt -lt 150 -and $Done -lt $Total) { return }
    $script:BarDrawnAt = $Clock.ElapsedMilliseconds
    $pct = if ($Total -gt 0) { [Math]::Min(100, [int](100 * $Done / $Total)) } else { 100 }
    $filled = [int]($pct * 30 / 100)
    $bar = ($Glyph.Full * $filled) + ($Glyph.Light * (30 - $filled))
    Write-Host ("`r  [{0}] {1,3}%  {2} / {3}  {4:mm\:ss}   " -f $bar, $pct, (Format-Size $Done), (Format-Size $Total), $Clock.Elapsed) -NoNewline -ForegroundColor Cyan
    $script:BarShown = $true
}

function End-ProgressBar {
    if ($script:BarShown) { Write-Host ''; $script:BarShown = $false }
}

function Test-ProgressBarFits {
    # The bar line is about 70 columns; in a narrower window every redraw would wrap onto a new
    # line, so the milestone lines are shown instead.
    return ((Test-InteractiveConsole) -and [Console]::WindowWidth -ge 72)
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
    if (Test-InteractiveConsole) {
        # Same arrow-key menu as every other choice, with the default highlighted.
        $items = @((New-MenuItem -Key 'yes' -Text 'Yes'), (New-MenuItem -Key 'no' -Text 'No'))
        return ((Show-Menu $Prompt $items $(if ($Default) { 'yes' } else { 'no' })) -eq 'yes')
    }
    # Redirected input (scripts, unattended runs) keeps the typed Y/N answer.
    while ($true) {
        $a = (Read-Line "$Prompt $hint").ToUpperInvariant()
        if ($a -eq '') { return $Default }
        if ($a -eq 'Y' -or $a -eq 'YES') { return $true }
        if ($a -eq 'N' -or $a -eq 'NO') { return $false }
        if ($a -eq 'Q') { Stop-Install 6 'Cancelled.' 'Run the installer again to continue.' }
        Out-Log 'Enter Y or N.'
    }
}

# ================================================================== menus
$CheckMark = [string][char]0x221A   # renders in every console font and code page

function Test-InteractiveConsole {
    try { return (-not [Console]::IsInputRedirected -and -not [Console]::IsOutputRedirected -and $Host.Name -eq 'ConsoleHost') }
    catch { return $false }
}

function New-MenuItem {
    # A selectable row has a Key; a row without one is information (Dim) or a blank spacer.
    param([string]$Key, [string]$Text, [string]$Mark = '', [switch]$Dim)
    return [pscustomobject]@{ Key = $Key; Text = $Text; Mark = $Mark; Dim = [bool]$Dim; Selectable = [bool]$Key }
}

function Format-ShortPath {
    # Shortens a path from the middle, keeping the drive and the last folders.
    param([string]$Path, [int]$Max)
    if ($Path.Length -le $Max -or $Max -lt 12) { return $Path }
    $root = [System.IO.Path]::GetPathRoot($Path)
    $parts = $Path.Substring($root.Length).Split('\')
    $tail = ''
    for ($i = $parts.Count - 1; $i -ge 0; $i--) {
        $candidate = if ($tail) { $parts[$i] + '\' + $tail } else { $parts[$i] }
        if (($root + '...\' + $candidate).Length -gt $Max) { break }
        $tail = $candidate
    }
    if (-not $tail) { return $Path.Substring(0, $Max - 3) + '...' }
    return $root + '...\' + $tail
}

function Show-Menu {
    # Arrow keys and Enter in a console; numbered choices when input or output is redirected.
    # Returns the chosen item's Key. Q or Esc cancels the installer.
    param([string]$Title, [object[]]$Items, [string]$DefaultKey)
    $selectable = @(for ($i = 0; $i -lt $Items.Count; $i++) { if ($Items[$i].Selectable) { $i } })
    $idx = $selectable[0]
    foreach ($i in $selectable) { if ($Items[$i].Key -eq $DefaultKey) { $idx = $i } }
    $logText = @($Title) + @($Items | ForEach-Object { if ($_.Selectable) { "  $($_.Mark) $($_.Text)" } else { "    $($_.Text)" } })
    try { [System.IO.File]::AppendAllText($script:LogPath, ($logText -join "`r`n") + "`r`n", $Utf8NoBom) } catch { }

    if ($Yes) { Write-Host $Title; Out-Log "  -> $($Items[$idx].Text) (accepted by -Yes)"; return $Items[$idx].Key }

    # Numbered choices when input or output is redirected, or when the menu cannot fit the
    # window (a console buffer can be exactly as tall as the window; the key hint needs 47
    # columns).
    if (-not (Test-InteractiveConsole) -or ($Items.Count + 3) -gt [Console]::WindowHeight -or [Console]::WindowWidth -lt 50) {
        return Show-NumberedMenu $Title $Items $idx
    }

    Clear-KeyBuffer
    Write-Host $Title
    $width = [Math]::Max(40, [Console]::WindowWidth - 1)
    foreach ($it in $Items) { Write-Host '' }
    Write-Host '  Up/Down to move, Enter to select, Q to quit' -ForegroundColor DarkGray
    # Measure from the line after the hint: at the bottom of the window every line written
    # scrolls the screen, so a position taken before the hint would be one line too low.
    $bottom = [Console]::CursorTop
    $top = $bottom - 1 - $Items.Count
    $drawRow = {
        param([int]$i, [bool]$on)
        $it = $Items[$i]
        [Console]::SetCursorPosition(0, $top + $i)
        if (-not $it.Selectable) {
            $line = ('    ' + $it.Text)
            if ($line.Length -gt $width) { $line = $line.Substring(0, $width - 3) + '...' }
            Write-Host $line.PadRight($width) -NoNewline -ForegroundColor $(if ($it.Dim) { 'DarkGray' } else { 'Gray' })
            return
        }
        $body = $it.Text
        if ($body.Length -gt $width - 6) { $body = $body.Substring(0, $width - 9) + '...' }
        $fg = if ($on) { 'Black' } else { 'Gray' }
        $bg = if ($on) { 'Gray' } else { [Console]::BackgroundColor }
        Write-Host $(if ($on) { " $($Glyph.Arrow) " } else { '   ' }) -NoNewline -ForegroundColor Cyan
        Write-Host $(if ($it.Mark) { $it.Mark } else { ' ' }) -NoNewline -ForegroundColor Green
        Write-Host ' ' -NoNewline
        Write-Host $body -NoNewline -ForegroundColor $fg -BackgroundColor $bg
        Write-Host ''.PadRight([Math]::Max(0, $width - 5 - $body.Length)) -NoNewline
    }
    $cursorWas = $true
    try { $cursorWas = [Console]::CursorVisible; [Console]::CursorVisible = $false } catch { }
    $outcome = 'choose'
    try {
        for ($i = 0; $i -lt $Items.Count; $i++) { & $drawRow $i ($i -eq $idx) }
        while ($true) {
            $key = [Console]::ReadKey($true)
            $pos = [array]::IndexOf($selectable, $idx)
            $next = $idx
            switch ($key.Key) {
                'UpArrow'   { if ($pos -gt 0) { $next = $selectable[$pos - 1] } else { $next = $selectable[$selectable.Count - 1] } }
                'DownArrow' { if ($pos -lt $selectable.Count - 1) { $next = $selectable[$pos + 1] } else { $next = $selectable[0] } }
                'Home'      { $next = $selectable[0] }
                'End'       { $next = $selectable[$selectable.Count - 1] }
                'Enter'     { $next = -1 }
                'Escape'    { $next = -2 }
                'Q'         { $next = -2 }
            }
            if ($next -eq -1) { break }
            if ($next -eq -2) { $outcome = 'cancel'; break }
            if ($next -ne $idx) { & $drawRow $idx $false; & $drawRow $next $true; $idx = $next }
        }
    } catch {
        # The window was resized so the menu's rows no longer exist; ask by number instead.
        $outcome = 'resized'
    } finally {
        try { [Console]::CursorVisible = $cursorWas } catch { }
        try { [Console]::SetCursorPosition(0, $bottom) } catch { Write-Host '' }
    }
    if ($outcome -eq 'cancel') { Stop-Install 6 'Cancelled.' 'Run the installer again to continue.' }
    if ($outcome -eq 'resized') {
        Write-Host ''
        Write-Host 'The window size changed; choose by number instead.' -ForegroundColor DarkGray
        return Show-NumberedMenu $Title $Items $idx
    }
    try { [System.IO.File]::AppendAllText($script:LogPath, "  -> $($Items[$idx].Text)`r`n", $Utf8NoBom) } catch { }
    Write-Host "  -> $($Items[$idx].Text)"
    return $Items[$idx].Key
}

function Show-NumberedMenu {
    # The same menu as numbered choices: used when input or output is redirected, or the menu
    # does not fit the console window.
    param([string]$Title, [object[]]$Items, [int]$DefaultIndex)
    Clear-KeyBuffer
    Write-Host $Title
    $map = @{}; $n = 0; $defaultNumber = '1'
    for ($i = 0; $i -lt $Items.Count; $i++) {
        $it = $Items[$i]
        if ($it.Selectable) {
            $n++; $map["$n"] = $i
            if ($i -eq $DefaultIndex) { $defaultNumber = "$n" }
            Write-Host ("[{0}] {1}{2}" -f $n, $(if ($it.Mark) { "$($it.Mark) " } else { '' }), $it.Text)
        } else { Write-Host ("    " + $it.Text) }
    }
    $keys = @(1..$n | ForEach-Object { "$_" })
    $k = Read-Choice 'Select' $keys $defaultNumber
    $chosen = $Items[$map[$k]]
    try { [System.IO.File]::AppendAllText($script:LogPath, "  -> $($chosen.Text)`r`n", $Utf8NoBom) } catch { }
    return $chosen.Key
}

function Clear-KeyBuffer {
    # Discards keys pressed while the installer was busy, so an impatient Enter or a second Esc
    # does not answer the next question.
    try { while ([Console]::KeyAvailable) { [void][Console]::ReadKey($true) } } catch { }
}

# ================================================================== file helpers
function New-TempPath {
    param([string]$Suffix)
    $p = Join-Path $env:TEMP ("jimsproxy-quickstart-{0}{1}" -f [guid]::NewGuid().ToString('N').Substring(0, 12), $Suffix)
    $script:TempPaths.Add($p)
    return $p
}

function Get-NormalPath {
    # Full path without a trailing backslash, except a drive root, which stays "X:\". ("X:"
    # alone means the current folder on that drive, so it is read as the root too.)
    param([string]$Path)
    if ($Path -match '^[A-Za-z]:$') { $Path += '\' }
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
    return $full
}

function Test-IsLink {
    # True for a junction or symbolic link. The ReparsePoint attribute alone is not enough:
    # OneDrive marks every synced file and folder with it.
    param([string]$Path)
    try { $t = (Get-Item -LiteralPath $Path -Force -ErrorAction Stop).LinkType } catch { return $false }
    return ($t -eq 'Junction' -or $t -eq 'SymbolicLink')
}

function Remove-Tree {
    # Deletes a folder bottom-up. A junction or symbolic link inside it is removed as a link,
    # so its target is never touched; OneDrive folders are deleted like any other folder.
    param([string]$Path)
    if (-not [System.IO.Directory]::Exists($Path)) { return }
    if (Test-IsLink $Path) { [System.IO.Directory]::Delete($Path, $false); return }
    foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) {
        if ($child.PSIsContainer) {
            if (Test-IsLink $child.FullName) { [System.IO.Directory]::Delete($child.FullName, $false) }
            else { Remove-Tree $child.FullName }
        } else {
            if ($child.Attributes -band [System.IO.FileAttributes]::ReadOnly) { $child.Attributes = [System.IO.FileAttributes]::Normal }
            [System.IO.File]::Delete($child.FullName)
        }
    }
    $dir = New-Object System.IO.DirectoryInfo($Path)
    if ($dir.Attributes -band [System.IO.FileAttributes]::ReadOnly) { $dir.Attributes = $dir.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly) }
    [System.IO.Directory]::Delete($Path, $false)
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
    # -Deep ("Keep searching") walks every fixed drive with no practical depth limit.
    param([switch]$Deep)
    $homeDir = $env:USERPROFILE
    $desktop = [Environment]::GetFolderPath('Desktop')
    $plan = New-Object System.Collections.Generic.List[object]
    $add = { param($p, $f, $z) if ($p -and [System.IO.Directory]::Exists($p)) { $plan.Add([pscustomobject]@{ Path = $p.TrimEnd('\') + '\'; FolderDepth = $f; ZipDepth = $z }) } }
    if ($Deep) {
        foreach ($d in @([System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady })) { & $add $d.RootDirectory.FullName 64 4 }
        return , $plan
    }
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
    # not descend into junctions or symbolic links. -Deep has no time cap; it shows a folder
    # counter and stops early on Esc.
    param([switch]$Deep)
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $eraDirs = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $zips    = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $visited = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $completed = $true
    $interactive = Test-InteractiveConsole
    $counter = $interactive -and [Console]::WindowWidth -ge 60   # the counter line is about 50 columns
    # Temporary folders never hold a real installation; other programs' test copies there only
    # add noise to the list.
    foreach ($t in @($env:TEMP, $env:TMP, (Join-Path $env:LOCALAPPDATA 'Temp'))) {
        if ($t) { try { [void]$visited.Add([System.IO.Path]::GetFullPath($t).TrimEnd('\') + '\') } catch { } }
    }
    if ($Deep) { Out-Log "Searching every folder on every fixed drive.$(if ($interactive) { ' Press Esc to stop.' })" }
    foreach ($root in (Get-ScanPlan -Deep:$Deep)) {
        if ($visited.Contains($root.Path)) { continue }
        $maxDepth = [Math]::Max($root.FolderDepth, $root.ZipDepth)
        $queue = New-Object System.Collections.Generic.Queue[object]
        $queue.Enqueue(@($root.Path, 0))
        while ($queue.Count -gt 0) {
            if (-not $Deep -and $clock.Elapsed.TotalSeconds -ge $ScanSeconds) { $completed = $false; break }
            if ($Deep -and ($visited.Count % 500) -eq 0) {
                if ($interactive) {
                    if ($counter) { Write-Host -NoNewline ("`r  searched {0:N0} folders, found {1} client(s)   " -f $visited.Count, ($eraDirs.Count + $zips.Count)) }
                    if ([Console]::KeyAvailable -and [Console]::ReadKey($true).Key -eq 'Escape') { $completed = $false; break }
                }
            }
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
                        # An online-only cloud file (OneDrive and similar) is not listed: opening
                        # it to read its build would download it.
                        if ($f.Length -gt $MinArchiveBytes -and -not ([int]$f.Attributes -band $CloudOnlyAttributes)) { [void]$zips.Add($f.FullName) }
                    }
                } catch { }
            }
            if ($level -ge $maxDepth) { continue }
            try { $subs = [System.IO.Directory]::GetDirectories($dir) } catch { continue }
            foreach ($s in $subs) {
                $name = [System.IO.Path]::GetFileName($s)
                if ($name.StartsWith('$') -or $SkipNames -contains $name) { continue }
                # Junctions and symbolic links are not followed; OneDrive folders carry the same
                # attribute but are ordinary folders, so only flagged folders get the link check.
                try { if (([System.IO.File]::GetAttributes($s) -band [System.IO.FileAttributes]::ReparsePoint) -and (Test-IsLink $s)) { continue } } catch { continue }
                $queue.Enqueue(@(($s + '\'), ($level + 1)))
            }
        }
        if (-not $completed) { break }
    }
    if ($Deep -and $counter) { Write-Host '' }
    if ($Deep) { Out-Log ("Searched {0:N0} folders in {1:mm\:ss}{2}." -f $visited.Count, $clock.Elapsed, $(if ($completed) { '' } else { ' (stopped with Esc)' })) }
    elseif (-not $completed) {
        Out-Log "The scan stopped after $ScanSeconds seconds; folders not reached are not listed. Use Keep searching or Browse."
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
function Test-Destination {
    # Returns $null when the destination is acceptable, otherwise the reason. $Inside is the
    # client folder being copied: the copy may not go inside it.
    param([string]$Dest, [long]$RequiredBytes, [string]$Inside = '')
    if (-not [System.IO.Path]::IsPathRooted($Dest) -or $Dest -notmatch '^[A-Za-z]:\\') { return "Use a full path that starts with a drive letter, such as D:\Games\Kronos." }
    if (Test-PathInside $Dest $Inside) { return "$Dest is inside the client being copied ($Inside). Choose a folder outside it." }
    if ([System.IO.File]::Exists($Dest)) { return "Destination is a file: $Dest" }
    if ([System.IO.Directory]::Exists($Dest) -and @([System.IO.Directory]::EnumerateFileSystemEntries($Dest)).Count -gt 0) {
        return "Destination is not empty: $Dest. Choose an empty or new folder."
    }
    if (-not [System.IO.Directory]::Exists([System.IO.Path]::GetPathRoot($Dest))) { return "Drive not available for: $Dest" }
    if (-not (Test-Writable $Dest)) { return "$Dest cannot be written without administrator rights. Choose a folder you can write to, such as $(Join-Path $env:USERPROFILE 'Games\Kronos')." }
    $need = $RequiredBytes + $MinFreeBytes
    try { $free = Get-FreeBytes $Dest } catch { return "Drive not available for: $Dest" }
    if ($free -lt $need) { return "Not enough free space on $([System.IO.Path]::GetPathRoot($Dest)): $(Format-Size $need) required, $(Format-Size $free) free." }
    return $null
}

function Select-Destination {
    # Picks an empty or new folder for an extracted or copied client. $Given is the value of
    # -ExtractTo or -CopyTo, which skips the menu.
    param([string]$Purpose, [long]$RequiredBytes, [string]$NearPath, [string]$Given, [string]$Inside = '')
    if ($Given) {
        $dest = Get-NormalPath ($Given.Trim().Trim('"'))
        $why = Test-Destination $dest $RequiredBytes $Inside
        if ($why) { Stop-Install 3 $why 'Choose an empty or new folder on a drive with enough free space, and run the installer again.' }
        return $dest
    }
    $base = Join-Path ([System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($NearPath))) 'Games\Kronos'
    $default = $base; $n = 2
    while (([System.IO.Directory]::Exists($default) -and @([System.IO.Directory]::EnumerateFileSystemEntries($default)).Count -gt 0) -or (Test-PathInside $default $Inside)) { $default = "$base $n"; $n++ }
    while ($true) {
        Out-Log ''
        Out-Log "The client will be $Purpose to <folder>\World of Warcraft ($(Format-Size $RequiredBytes)). The folder must be empty or new."
        $items = @(
            (New-MenuItem -Key 'default' -Text $default),
            (New-MenuItem -Key 'browse' -Text 'Browse for another folder')
        )
        $k = Show-Menu 'Destination folder:' $items 'default'
        $dest = if ($k -eq 'default') { $default } else { Show-FolderPicker 'Select an empty folder, or create a new one' }
        if (-not $dest) { continue }
        try { $dest = [System.IO.Path]::GetFullPath($dest) } catch { Out-Log "Not a valid path: $dest"; continue }
        $why = Test-Destination $dest $RequiredBytes $Inside
        if (-not $why) { return $dest }
        Out-Log $why
        if ($Yes) { Stop-Install 3 $why 'Choose an empty or new folder on a drive with enough free space, and run the installer again.' }
    }
}

function Select-ExtractDestination {
    param([pscustomobject]$Archive)
    return Select-Destination 'extracted' $Archive.UncompressedBytes $Archive.Path $ExtractTo
}

# ================================================================== copying a client
$CopySkipAtRoot = @('Hermes', 'JimsProxy-AccountData-backup')   # proxy folders, not client files
$CopySkipInEra  = @('Cache', 'Logs', 'Errors')                  # regenerated by the client

function Get-CopyPlan {
    # Lists the files to copy from a client's game folder (the folder above _classic_era_),
    # following junctions so the copy is independent of the original. Returns
    # @{ Files = list of relative paths; Bytes = total size }.
    param([string]$GameDir)
    $files = New-Object System.Collections.Generic.List[string]
    $bytes = [long]0
    $stack = New-Object System.Collections.Generic.Stack[object]
    $stack.Push(@('', 0))
    while ($stack.Count -gt 0) {
        $item = $stack.Pop(); $rel = $item[0]; $depth = $item[1]
        if ($depth -gt 32) { continue }
        $full = if ($rel) { Join-Path $GameDir $rel } else { $GameDir }
        try {
            foreach ($f in ([System.IO.DirectoryInfo]$full).GetFiles()) { $files.Add($(if ($rel) { "$rel\$($f.Name)" } else { $f.Name })); $bytes += $f.Length }
            foreach ($d in ([System.IO.DirectoryInfo]$full).GetDirectories()) {
                if ($depth -eq 0 -and ($CopySkipAtRoot -contains $d.Name -or $d.Name -like 'Hermes-backup-*')) { continue }
                if ($rel -ieq '_classic_era_' -and $CopySkipInEra -contains $d.Name) { continue }
                $stack.Push(@($(if ($rel) { "$rel\$($d.Name)" } else { $d.Name }), ($depth + 1)))
            }
        } catch { Stop-Install 1 "Cannot read $full ($($_.Exception.Message))." 'Close programs that use the client and run the installer again.' }
    }
    return @{ Files = $files; Bytes = $bytes }
}

function Copy-Client {
    # Copies the client into <Dest>\World of Warcraft and returns the copy as a candidate.
    param([pscustomobject]$Client, [string]$Dest, [hashtable]$Plan)
    $game = Split-Path -Parent $Client.Path
    $target = Join-Path $Dest 'World of Warcraft'
    Out-Log "Copying $game"
    Out-Log "     to $target"
    Out-Log "$($Plan.Files.Count) files, $(Format-Size $Plan.Bytes). The original is not changed."
    [void][System.IO.Directory]::CreateDirectory($target)
    $script:Extracting = $Dest; $script:ExtractVerb = 'copy'
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $count = 0; $bytes = [long]0; $lastCount = 0; $lastBytes = [long]0
    $bar = Test-ProgressBarFits
    foreach ($rel in $Plan.Files) {
        $src = Join-Path $game $rel
        $dst = Join-Path $target $rel
        [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($dst))
        try { [System.IO.File]::Copy($src, $dst, $false) }
        catch {
            $why = $(if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }).TrimEnd('.')
            Stop-Install 2 "Could not copy $rel ($why)." "Close the game and any program using the client, delete $Dest, and run the installer again."
        }
        $count++; $bytes += (New-Object System.IO.FileInfo($dst)).Length
        if ($bar) { Update-ProgressBar $bytes $Plan.Bytes $clock }
        if ($count - $lastCount -ge 1000 -or $bytes - $lastBytes -ge 1GB) {
            $line = '  {0} files, {1} of {2}, {3:mm\:ss} elapsed' -f $count, (Format-Size $bytes), (Format-Size $Plan.Bytes), $clock.Elapsed
            if ($bar) { Write-LogOnly $line } else { Out-Log $line }
            $lastCount = $count; $lastBytes = $bytes
        }
    }
    if ($bar) { Update-ProgressBar $Plan.Bytes $Plan.Bytes $clock; End-ProgressBar }
    Out-Log ('  {0} files, {1}, finished in {2:mm\:ss}' -f $count, (Format-Size $bytes), $clock.Elapsed) -Color DarkGray
    $script:Extracting = $null
    $era = Join-Path $target '_classic_era_'
    $c = Get-FolderCandidate $era
    if (-not $c.Usable) { Stop-Install 5 "The copied client does not pass the client check: $($c.Status)." "Delete $Dest and run the installer again." }
    Save-PendingState $c $null
    return $c
}

function Save-PendingState {
    # Marks a freshly extracted or copied client as an unfinished quick-start installation, so a
    # later run (or the same command run again) continues there instead of refusing the folder
    # it just filled.
    param([pscustomobject]$Client, $ArchivePath)
    $root = Get-InstallRoot $Client.Path
    [void][System.IO.Directory]::CreateDirectory((Join-Path $root 'Hermes'))
    Save-State (New-State $root $Client $ArchivePath)
}

function Get-PendingClient {
    # The client in Folder when an earlier run extracted or copied it there and did not finish;
    # otherwise $null.
    param([string]$Folder)
    if (-not $Folder) { return $null }
    try { $full = Get-NormalPath ($Folder.Trim().Trim('"')) } catch { return $null }
    $prev = Get-State $full
    if ($null -eq $prev -or $prev.status -ne 'installing') { return $null }
    $c = Get-FolderCandidate "$($prev.clientDir)"
    if (-not $c.Usable) { return $null }
    Out-Log "Continuing in $full, where an earlier run already put the client."
    return $c
}

function Invoke-CopyRoute {
    # Copies the client into a new folder (from -CopyTo, or chosen) and returns the copy.
    # Running the same -CopyTo again after a failure continues in that folder.
    param([pscustomobject]$Client, [string]$Given, $Plan, [string]$Announce)
    $pending = Get-PendingClient $Given
    if ($pending) { return $pending }
    if ($Announce) { Out-Log $Announce }
    # The installation needs the download server: find out before spending minutes on a copy.
    Assert-Network
    $game = Split-Path -Parent $Client.Path
    if ($null -eq $Plan) { $Plan = Get-CopyPlan $game }
    $dest = Select-Destination 'copied' $Plan.Bytes (Get-InstallRoot $Client.Path) $Given $game
    Wait-GameClosed $game
    return Copy-Client $Client $dest $Plan
}

function Test-PathInside {
    # True when Path is Folder itself or anywhere below it.
    param([string]$Path, [string]$Folder)
    if (-not $Path -or -not $Folder) { return $false }
    $p = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $f = [System.IO.Path]::GetFullPath($Folder).TrimEnd('\')
    return ($p -ieq $f -or $p.StartsWith($f + '\', [System.StringComparison]::OrdinalIgnoreCase))
}

function Get-GameProcesses {
    # Game processes started from GameDir (the folder above _classic_era_).
    param([string]$GameDir)
    return @(Get-Process -Name 'WowClassic', 'WowClassic_ForCustomServers' -ErrorAction SilentlyContinue | Where-Object {
        $exe = $null; try { $exe = $_.Path } catch { }
        $exe -and (Test-PathInside $exe $GameDir)
    })
}

function Wait-GameClosed {
    # A running game locks client files; the copy waits until it is closed.
    param([string]$GameDir)
    while ($true) {
        $running = @(Get-GameProcesses $GameDir)
        if ($running.Count -eq 0) { return }
        $why = "The game is running from $GameDir (PID $(($running | ForEach-Object { $_.Id }) -join ', ')); it must be closed before the client is copied."
        if ($Yes) { Stop-Install 2 $why 'Close the game and run the installer again.' }
        Out-Log ''
        Out-Log "WARNING: $why"
        $k = Show-Menu 'Close the game, then:' @((New-MenuItem -Key 'retry' -Text 'Retry'), (New-MenuItem -Key 'quit' -Text 'Quit')) 'retry'
        if ($k -eq 'quit') { Stop-Install 6 'Cancelled; nothing was copied.' $null }
    }
}

function Test-Writable {
    # True when the installer can write at Folder. It always creates a folder there (Hermes, or
    # World of Warcraft) and then files inside it, so the probe does the same in the nearest
    # existing parent: a standard user may create folders in C:\ but not files.
    param([string]$Folder)
    $d = $Folder
    while ($d -and -not [System.IO.Directory]::Exists($d)) { $d = Split-Path -Parent $d }
    if (-not $d) { return $false }
    $probe = Join-Path $d (".jimsproxy-write-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        [void][System.IO.Directory]::CreateDirectory($probe)
        [System.IO.File]::WriteAllText((Join-Path $probe 'test'), '')
        return $true
    } catch { return $false }
    finally { try { if ([System.IO.Directory]::Exists($probe)) { [System.IO.Directory]::Delete($probe, $true) } } catch { } }
}

function Select-ExistingHermesAction {
    # The chosen client's root cannot take a new Hermes as it is: it already has one the
    # installer did not create ($Reason 'foreign'), or it cannot be written without
    # administrator rights ($Reason 'readonly', e.g. under Program Files). Offers a copy of the
    # client into a new folder and, for 'foreign', an in-place install that keeps the old
    # Hermes as a backup. Returns @{ Action = 'copy' | 'inplace' | 'back'; Client }.
    param([pscustomobject]$Client, [string]$Reason = 'foreign')
    $root = Get-InstallRoot $Client.Path
    $hermes = Join-Path $root 'Hermes'
    $game = Split-Path -Parent $Client.Path
    Out-Log ''
    if ($Reason -eq 'readonly') { Out-Log "$root cannot be written without administrator rights (for example, a folder under Program Files)." }
    else { Out-Log "$hermes already exists: this client has a Classic WoW Launcher or manual JimsProxy/HermesProxy installation." }
    Out-Log 'Measuring the client...'
    $plan = Get-CopyPlan $game
    while ($true) {
        $items = @(
            (New-MenuItem -Key 'copy' -Text 'Copy the client to a new folder and install there (recommended)'),
            (New-MenuItem -Dim -Text "Clean install. The current client is not touched. Needs $(Format-Size $plan.Bytes) of additional disk space.")
        )
        if ($Reason -ne 'readonly') {
            $items += (New-MenuItem -Key 'inplace' -Text 'Install here, keeping the current Hermes as a backup')
            $items += (New-MenuItem -Dim -Text 'Renames Hermes to Hermes-backup-<date>. A launcher using this folder stops working until it is renamed back.')
        }
        $items += (New-MenuItem -Text '')
        # -ClientDir names the client, so there is no other one to choose.
        if ($ClientDir) { $items += (New-MenuItem -Key 'quit' -Text 'Quit') }
        else { $items += (New-MenuItem -Key 'back' -Text 'Choose a different client') }
        $k = Show-Menu 'How should the installer proceed?' $items 'copy'
        if ($k -eq 'quit') { Stop-Install 6 'Cancelled; nothing was changed.' $null }
        if ($k -eq 'back') { return @{ Action = 'back'; Client = $null } }
        if ($k -eq 'inplace') {
            Out-Log ''
            Out-Log "WARNING: $hermes will be renamed to Hermes-backup-<date> and a new Hermes installed."
            Out-Log '         If the Classic WoW Launcher or another installation uses this folder, it stops working' -Color Yellow
            Out-Log '         until you delete the new Hermes and rename the backup to Hermes. If you are not sure' -Color Yellow
            Out-Log '         whether anything else uses this folder, choose the copy instead.' -Color Yellow
            if (-not (Read-YesNo 'Install here anyway?' $false)) { Out-Log ''; continue }
            return @{ Action = 'inplace'; Client = $Client }
        }
        return @{ Action = 'copy'; Client = (Invoke-CopyRoute $Client $null $plan) }
    }
}

function Backup-ExistingHermes {
    # In-place route, run at Step 4 once the proxy archive has passed its checks: renames the
    # existing Hermes and returns the backup's path. The caller records it in quickstart.json
    # before anything else is written, so an interrupted run resumes instead of treating its
    # own new Hermes as foreign.
    param([string]$HermesDir)
    $running = @(Get-Process -Name 'JimsProxy', 'HermesProxy' -ErrorAction SilentlyContinue | Where-Object {
        $p = $null; try { $p = $_.Path } catch { }
        (-not $p) -or $p.StartsWith($HermesDir + '\', [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($running.Count -gt 0) { Stop-Install 2 "A proxy is running from $HermesDir (PID $(($running | ForEach-Object { $_.Id }) -join ', '))." 'Close the game and the launcher (or end the proxy in Task Manager), then run the installer again.' }
    $backup = "$HermesDir-backup-$((Get-Date).ToString('yyyyMMdd-HHmmss'))"
    try { Move-Item -LiteralPath $HermesDir -Destination $backup }
    catch { Stop-Install 2 "Could not rename $HermesDir ($($_.Exception.Message))." 'Close programs that use that folder and run the installer again.' }
    Out-Log "  renamed the existing Hermes to $backup"
    return $backup
}

function Copy-BackupAccountData {
    # Carries AccountData from the renamed Hermes into the new one, once. A run interrupted
    # during the copy repeats it on resume; a finished copy is recorded and never repeated, so
    # keybindings or macros changed since are not overwritten by the backup's older files.
    param([pscustomobject]$State, [string]$HermesDir)
    $backup = Get-StateValue $State 'hermesBackup'
    if (-not $backup -or (Get-StateValue $State 'accountDataCopied')) { return }
    $src = Join-Path $backup 'AccountData'
    if ([System.IO.Directory]::Exists($src)) {
        $dst = Join-Path $HermesDir 'AccountData'
        [void][System.IO.Directory]::CreateDirectory($dst)
        Copy-Item -Path (Join-Path ([WildcardPattern]::Escape($src)) '*') -Destination $dst -Recurse -Force
        Out-Log "  copied AccountData from $backup"
    }
    Set-StateValue $State 'accountDataCopied' $true
    Save-State $State
}

function Set-StateValue {
    # Sets a field on a state object, adding it when an older quickstart.json lacks it.
    param($State, [string]$Name, $Value)
    if ($State.PSObject.Properties[$Name]) { $State.$Name = $Value }
    else { $State | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
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
        $bar = Test-ProgressBarFits
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
            if ($bar) { Update-ProgressBar $bytes $Archive.UncompressedBytes $clock }
            if ($count - $lastCount -ge 200 -or $bytes - $lastBytes -ge 100MB) {
                $line = '  {0} files, {1} of {2}, {3:mm\:ss} elapsed' -f $count, (Format-Size $bytes), (Format-Size $Archive.UncompressedBytes), $clock.Elapsed
                if ($bar) { Write-LogOnly $line } else { Out-Log $line }
                $lastCount = $count; $lastBytes = $bytes
            }
        }
        if ($bar) { Update-ProgressBar $Archive.UncompressedBytes $Archive.UncompressedBytes $clock; End-ProgressBar }
        Out-Log ('  {0} files, {1}, finished in {2:mm\:ss}' -f $count, (Format-Size $bytes), $clock.Elapsed) -Color DarkGray
        if ($skipped -gt 0) { Out-Log "$skipped unsafe entries were not extracted." }
    } finally { $zip.Dispose() }
    $script:Extracting = $null

    $era = Join-Path $target '_classic_era_'
    if (-not [System.IO.File]::Exists((Join-Path $target '.build.info')) -or -not [System.IO.File]::Exists((Join-Path $era 'WowClassic_ForCustomServers.exe'))) {
        Stop-Install 5 "The extracted client in $target is missing .build.info or WowClassic_ForCustomServers.exe." "Delete $Dest and run the installer again."
    }
    $c = Get-FolderCandidate $era
    if (-not $c.Usable) { Stop-Install 5 "The extracted client does not pass the client check: $($c.Status)." "Delete $Dest and run the installer again." }
    Save-PendingState $c $Archive.Path
    return $c
}

# ================================================================== installation state
$script:MovedFrom = @{}   # root -> the root recorded in a quickstart.json that was copied or moved there

function Get-State {
    # Reads <RootDir>\Hermes\quickstart.json. The state always describes the folder it was read
    # from: if the root folder was copied or moved (e.g. a copy made to multibox), the recorded
    # paths are re-based onto RootDir, so Update, Reconfigure and Uninstall can never act on the
    # original installation. The original's desktop shortcut is not this installation's.
    param([string]$RootDir)
    try { $here = Get-NormalPath $RootDir } catch { return $null }
    # Path.Combine, not Join-Path: Join-Path fails for a drive that does not exist (a typed
    # -CopyTo or -ExtractTo), which must reach Test-Destination's message instead.
    $p = [System.IO.Path]::Combine($here, 'Hermes', 'quickstart.json')
    if (-not [System.IO.File]::Exists($p)) { return $null }
    try { $s = (Read-TextFile $p).Text | ConvertFrom-Json } catch { return $null }
    if ($null -eq $s -or $s -isnot [System.Management.Automation.PSCustomObject]) { return $null }
    try {
        $was = "$(Get-StateValue $s 'root')"
        if ($was) { $was = Get-NormalPath $was }
    } catch { return $null }
    $fields = 'clientDir', 'gameExe', 'hermesBackup'
    if ($was -and $was -ine $here) {
        $wasPrefix = $was.TrimEnd('\') + '\'
        foreach ($field in $fields) {
            $v = "$(Get-StateValue $s $field)"
            if (-not $v) { continue }
            # Normalised like the root, so a short (8.3) or differently written path still matches.
            try { $v = Get-NormalPath $v } catch { return $null }
            if ($v.StartsWith($wasPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { Set-StateValue $s $field ($here.TrimEnd('\') + '\' + $v.Substring($wasPrefix.Length)) }
        }
        if (Get-StateValue $s 'shortcut') { $s.shortcut = $null }
        $s.root = $here
        $script:MovedFrom[$here] = $was
    }
    # Every recorded path lies inside the installation's folder; a state that points anywhere
    # else is not trusted (nothing outside the selected folder is ever changed).
    foreach ($field in $fields) {
        $v = "$(Get-StateValue $s $field)"
        if (-not $v) { continue }
        try { if (-not (Test-PathInside $v $here)) { return $null } } catch { return $null }
    }
    return $s
}

function Get-StateValue {
    # A field of a state object, or $null when an older quickstart.json does not have it.
    param($State, [string]$Name)
    $prop = $State.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    return $null
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
        hermesBackup     = $null
        accountDataCopied = $false
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
$script:NetworkOk = $false

function Get-NetworkHosts {
    return @($ProxyHost, ($RawBase -replace '^(https?://[^/]+).*$', '$1'))
}

function Test-Reachable {
    # $null when the host answers (any HTTP status counts), otherwise the reason.
    param([string]$Url)
    try { Invoke-WebRequest -Uri $Url -Method Head -UseBasicParsing -TimeoutSec 10 | Out-Null; return $null }
    catch [System.Net.WebException] { if ($null -ne $_.Exception.Response) { return $null } else { return $_.Exception.Message } }
    catch { return $_.Exception.Message }
}

function Assert-Network {
    # Called before the first download: both hosts must answer.
    if ($script:NetworkOk) { return }
    foreach ($url in (Get-NetworkHosts)) {
        $why = Test-Reachable $url
        if ($why) { Stop-Install 2 "$url is not reachable: $why" 'Check the internet connection, firewall, or proxy settings, and run the installer again.' }
        Write-Check "$(([uri]$url).Authority) reachable"
    }
    $script:NetworkOk = $true
}
function Invoke-Preflight {
    param([bool]$Network, [string]$TargetPath)
    Write-Heading 'Step 1 of 6: Check this PC'
    $os = [Environment]::OSVersion.Version
    if (-not [Environment]::Is64BitOperatingSystem -or $os.Major -lt 10) {
        Stop-Install 2 "This installer requires 64-bit Windows 10 or 11 (found Windows $os, $(if ([Environment]::Is64BitOperatingSystem) { '64' } else { '32' })-bit)." 'Use a 64-bit Windows 10 or 11 PC, or follow docs/MANUAL-INSTALL.md.'
    }
    Write-Check "Windows $os, 64-bit"
    if ($PSVersionTable.PSVersion -lt [version]'5.1') {
        Stop-Install 2 "Windows PowerShell 5.1 or newer is required (found $($PSVersionTable.PSVersion))." 'Install Windows Management Framework 5.1 and run the installer again.'
    }
    Write-Check "PowerShell $($PSVersionTable.PSVersion)"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Write-Check 'TLS 1.2 enabled'
    if ($Network) {
        # Only a warning here: an Uninstall or Reconfigure chosen from the menu works offline.
        # Assert-Network stops the run before the first download if a host is still unreachable.
        $allReached = $true
        foreach ($url in (Get-NetworkHosts)) {
            $why = Test-Reachable $url
            if ($why) {
                Out-Log "   WARNING: $(([uri]$url).Authority) is not reachable ($why). Installing and updating need it."
                $allReached = $false
            } else { Write-Check "$(([uri]$url).Authority) reachable" }
        }
        if ($allReached) { $script:NetworkOk = $true }
    }
    $probe = if ($TargetPath) { $TargetPath } else { $env:TEMP }
    $free = Get-FreeBytes $probe
    if ($free -lt $MinFreeBytes) {
        Stop-Install 2 "Only $(Format-Size $free) free on $([System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($probe))); at least $(Format-Size $MinFreeBytes) is required." 'Free disk space and run the installer again.'
    }
    Write-Check "$(Format-Size $free) free on $([System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($probe)))"
}

function Get-ClientMenuItems {
    # One row per usable client (checkmark, name, shortened path, install note), one dim line
    # summarising the unusable ones, then the manual choices after a blank line.
    param($Candidates)
    $usable = @($Candidates | Where-Object { $_.Usable })
    $unusable = @($Candidates | Where-Object { -not $_.Usable })
    $width = 100
    if (Test-InteractiveConsole) { $width = [Math]::Max(60, [Console]::WindowWidth - 7) }
    $rows = @()
    foreach ($c in $usable) {
        if ($c.Kind -eq 'archive') {
            $name = [System.IO.Path]::GetFileName($c.Path)
            $where = Split-Path -Parent $c.Path
            $note = 'client archive, extracted to a new folder'
        } else {
            $root = Get-InstallRoot $c.Path
            $name = Split-Path -Leaf $root
            if (-not $name) { $name = $root }
            $where = $root
            $hermes = Join-Path $root 'Hermes'
            $state = Get-State $root
            $note = if ($state -and $state.status -eq 'installed') { 'JimsProxy quick-start installed' }
                    elseif ($state) { 'unfinished quick-start installation' }
                    elseif ([System.IO.Directory]::Exists($hermes)) { 'has a launcher or manual Hermes' }
                    else { '' }
        }
        $rows += [pscustomobject]@{ Name = $name; Where = $where; Note = $note; Candidate = $c }
    }
    $nameWidth = [Math]::Min(26, [Math]::Max(8, (@($rows | ForEach-Object { $_.Name.Length }) + 0 | Measure-Object -Maximum).Maximum))
    $items = @()
    $i = 0
    foreach ($r in $rows) {
        $i++
        $name = if ($r.Name.Length -gt $nameWidth) { $r.Name.Substring(0, $nameWidth - 3) + '...' } else { $r.Name.PadRight($nameWidth) }
        $note = if ($r.Note) { "  ($($r.Note))" } else { '' }
        $pathRoom = $width - $nameWidth - 2 - $note.Length
        $items += New-MenuItem -Key "client:$i" -Mark $CheckMark -Text ("{0}  {1}{2}" -f $name, (Format-ShortPath $r.Where $pathRoom), $note)
    }
    if ($unusable.Count -gt 0) {
        $reasons = @($unusable | Group-Object Status | Sort-Object Count -Descending | ForEach-Object {
            $s = $_.Name -replace '^not supported: ', '' -replace '^not usable: ', ''
            if ($_.Count -gt 1) { "$s (x$($_.Count))" } else { $s }
        })
        $other = if ($usable.Count -gt 0) { 'other ' } else { '' }
        $noun = if ($unusable.Count -eq 1) { "${other}client found, not usable" } else { "${other}clients found, not usable" }
        $items += New-MenuItem -Dim -Text "$($unusable.Count) $noun`: $($reasons -join '; ')"
    }
    if ($usable.Count -eq 0 -and $unusable.Count -eq 0) { $items += New-MenuItem -Dim -Text 'No client found in the usual locations.' }
    $items += New-MenuItem -Text ''
    $items += New-MenuItem -Key 'browse' -Text 'Browse for a client folder'
    $items += New-MenuItem -Key 'archive' -Text 'Extract a client archive (.zip)'
    $items += New-MenuItem -Key 'deep' -Text 'Keep searching (every folder on every drive)'
    $items += New-MenuItem -Key 'quit' -Text 'Quit'
    return @{ Items = $items; Rows = $rows }
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
        $pending = Get-PendingClient $ExtractTo
        if ($pending) { return @{ Client = $pending; Archive = $null } }
        if (-not [System.IO.File]::Exists($ClientArchive)) { Stop-Install 3 "File not found: $ClientArchive" $RequirementText }
        $a = Get-ArchiveCandidate ([System.IO.Path]::GetFullPath($ClientArchive))
        Out-Log "$($a.Path)    $($a.Status)"
        if (-not $a.Usable) { Stop-Install 3 "The archive $($a.Path) is $($a.Status)." $RequirementText }
        return @{ Client = $null; Archive = $a }
    }

    Out-Log "Scanning for clients (at most $ScanSeconds seconds)..."
    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($c in (Find-Candidates)) { $candidates.Add($c) }
    while ($true) {
        Out-Log ''
        $usable = @($candidates | Where-Object { $_.Usable })
        if ($Yes -and $usable.Count -eq 0) { Stop-Install 3 'No usable client was found.' $RequirementText }
        $only115 = ($usable.Count -eq 0 -and @($candidates | Where-Object { $_.Build -like '1.15.*' }).Count -gt 0)
        if ($only115) {
            Out-Log 'Only WoW Classic 1.15 clients were found; this installer needs 1.14.2 (build 42597).'
            Out-Log "The Classic WoW Launcher can copy a 1.15 client and patch the copy into 1.14.2: $LauncherUrl"
            Out-Log ''
        }
        $menu = Get-ClientMenuItems $candidates
        $default = if ($usable.Count -gt 0) { 'client:1' } elseif ($only115) { 'quit' } else { 'browse' }
        $k = Show-Menu 'Select a client:' $menu.Items $default
        $picked = $null
        switch -Regex ($k) {
            '^client:(\d+)$' { $picked = $menu.Rows[[int]$Matches[1] - 1].Candidate }
            '^browse$'  { $picked = Resolve-UserChoice (Show-FolderPicker 'Select the _classic_era_ folder, or the folder that contains it') }
            '^archive$' { $picked = Resolve-UserChoice (Show-ZipPicker) }
            '^deep$' {
                $known = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
                foreach ($c in $candidates) { [void]$known.Add($c.Path) }
                $added = 0
                foreach ($c in (Find-Candidates -Deep)) { if ($known.Add($c.Path)) { $candidates.Add($c); $added++ } }
                Out-Log "Keep searching found $added more client(s)."
            }
            '^quit$' { Stop-Install 6 'Cancelled.' 'Run the installer again to continue.' }
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
        else { Stop-Install 1 "-Server must be kronos, kronos2 or kronos3 (mirrors), or a hostname or IP address (got '$Server')." 'Correct the parameter and run the installer again.' }
        Out-Log "Server: $address"
    } else {
        $items = @($Servers | ForEach-Object { New-MenuItem -Key $_.Key -Text "$($_.Name) ($($_.Address))" }) + @(New-MenuItem -Key 'other' -Text 'Other address')
        $default = 'kronos'
        if ($CurrentServer) {
            $match = @($Servers | Where-Object { $_.Address -eq $CurrentServer })
            $default = if ($match.Count -gt 0) { $match[0].Key } else { 'other' }
        }
        $k = Show-Menu 'Server:' $items $default
        if ($k -eq 'other') {
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
            $s = @($Servers | Where-Object { $_.Key -eq $k })[0]; $key = $s.Key; $address = $s.Address
        }
    }

    $ch = $null
    if ($Channel) {
        if ($Channel -notin @('stable', 'beta')) { Stop-Install 1 "-Channel must be stable or beta (got '$Channel')." 'Correct the parameter and run the installer again.' }
        $ch = $Channel.ToLowerInvariant()
        Out-Log "Channel: $ch"
    } else {
        Out-Log ''
        $items = @(
            (New-MenuItem -Key 'stable' -Text 'Stable: the current release'),
            (New-MenuItem -Key 'beta' -Text 'Beta: newer changes, less testing')
        )
        $ch = Show-Menu 'Channel:' $items $(if ($CurrentChannel -eq 'beta') { 'beta' } else { 'stable' })
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
    # An empty body or "null" parses to $null without an error, and "{}" to an object with no
    # properties; both mean the manifest cannot be used.
    if ($null -eq $m -or $m -isnot [System.Management.Automation.PSCustomObject]) { Out-Log "  WARNING: $src did not return a JSON object"; return $null }
    $prefix = "$ProxyHost/proxy/$Chan/"
    $fields = @($m.PSObject.Properties | ForEach-Object { $_.Name })
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
    Assert-Network
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
        Write-Check 'SHA-256 matches the published checksum'
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
        # A .NET call wraps the real reason ("End of Central Directory record could not be found")
        # in "Exception calling ... with N argument(s)"; show the reason.
        $why = $(if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }).TrimEnd('.')
        Stop-Install 5 "The downloaded file is not a valid proxy archive ($why)." 'Run the installer again later; the download address may have returned an error page.'
    }
    $stage = New-TempPath ''
    Expand-Archive -LiteralPath $zipPath -DestinationPath $stage -Force
    $source = if ($prefix) { Join-Path $stage $prefix.TrimEnd('/') } else { $stage }
    if ($prefix) { Out-Log "  the archive holds its files in the sub-folder $($prefix.TrimEnd('/')); using its contents" }
    Write-Check 'archive contents verified'
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
    Assert-Network
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

function Get-ShortcutTarget {
    param([string]$Lnk)
    try {
        $shell = New-Object -ComObject WScript.Shell
        try { return $shell.CreateShortcut($Lnk).TargetPath } finally { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
    } catch { return $null }
}

function Get-ShortcutPath {
    # "Play Kronos.lnk", unless that name already belongs to another installation (or anything
    # else): then "Play Kronos (<installation folder>).lnk", numbered if that is taken too. A
    # shortcut that already points at this installation keeps its name.
    param([pscustomobject]$State)
    $desktop = [Environment]::GetFolderPath('Desktop')
    $mine = Join-Path (Join-Path $State.root 'Hermes') $PlayCmdName
    # The installation folder's name; for a drive root ("D:\") the drive letter.
    $leaf = [System.IO.Path]::GetFileName($State.root.TrimEnd('\'))
    if (-not $leaf -or $leaf.Contains(':')) { $leaf = $State.root.Substring(0, 1) }
    foreach ($ch in [System.IO.Path]::GetInvalidFileNameChars()) { $leaf = $leaf.Replace([string]$ch, '') }
    if (-not $leaf) { $leaf = 'Kronos' }
    $names = @('Play Kronos', "Play Kronos ($leaf)") + @(2..20 | ForEach-Object { "Play Kronos ($leaf $_)" })
    foreach ($n in $names) {
        $path = Join-Path $desktop "$n.lnk"
        if (-not [System.IO.File]::Exists($path)) { return $path }
        if ((Get-ShortcutTarget $path) -ieq $mine) { return $path }
    }
    return Join-Path $desktop "Play Kronos ($leaf $([guid]::NewGuid().ToString('N').Substring(0, 6))).lnk"
}

function New-DesktopShortcut {
    param([pscustomobject]$State, [string]$Lnk)
    $lnk = $Lnk
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
    $rows = New-Object System.Collections.Generic.List[object]
    $add = { param($label, $value) $rows.Add(@($label, [string]$value)) }
    & $add 'Installation folder' $hermes
    & $add 'Proxy version' (Get-ProxyVersion $hermes)
    & $add 'Client folder' $State.clientDir
    & $add 'Game executable' $State.gameExe
    if ($State.clientArchive) { & $add 'Client archive' "$($State.clientArchive) (not modified)" }
    & $add 'Server' $State.serverAddress
    & $add 'Channel' $State.channel
    & $add 'JimsPlus addon' $(if ($State.addonInstalled) { 'installed' } else { 'not installed' })
    & $add 'Desktop shortcut' $(if ($State.shortcut) { $State.shortcut } else { 'not created' })
    & $add 'Play command' (Join-Path $hermes $PlayCmdName)
    & $add 'Log' $script:LogPath
    Out-Log ''
    foreach ($r in $rows) { Write-LogOnly ('{0,-20} {1}' -f $r[0], $r[1]) }
    if (-not (Test-InteractiveConsole) -or [Console]::WindowWidth -lt 60) {
        foreach ($r in $rows) { Write-Host ('{0,-20} {1}' -f $r[0], $r[1]) }
        return
    }
    # In a console: a green frame, with long paths shortened from the middle so it never wraps.
    $room = [Math]::Max(30, [Console]::WindowWidth - 30)
    $lines = @($rows | ForEach-Object {
        $v = $_[1]
        if ($v.Length -gt $room) { $v = if ($v.Contains('\')) { Format-ShortPath $v $room } else { $v.Substring(0, $room - 3) + '...' } }
        '{0,-20} {1}' -f $_[0], $v
    })
    $inner = ($lines | Measure-Object -Property Length -Maximum).Maximum + 4
    Write-Host ($Glyph.TL + ($Glyph.Rule * $inner) + $Glyph.TR) -ForegroundColor Green
    foreach ($l in $lines) {
        Write-Host $Glyph.V -NoNewline -ForegroundColor Green
        Write-Host ('  ' + $l.PadRight($inner - 2)) -NoNewline
        Write-Host $Glyph.V -ForegroundColor Green
    }
    Write-Host ($Glyph.BL + ($Glyph.Rule * $inner) + $Glyph.BR) -ForegroundColor Green
}

# ================================================================== flows
function Invoke-Install {
    param([hashtable]$Selection)
    $client = $Selection.Client
    $archive = $Selection.Archive
    if ($archive) {
        # The installation needs the download server: find out before a long extraction.
        Assert-Network
        $client = Expand-ClientArchive $archive (Select-ExtractDestination $archive)
    }
    $settings = Select-ServerAndChannel $null $null
    $rootDir = Get-InstallRoot $client.Path
    $hermes = Join-Path $rootDir 'Hermes'

    Write-Heading 'Step 4 of 6: Install the proxy'
    $free = Get-FreeBytes $rootDir
    if ($free -lt $MinFreeBytes) { Stop-Install 2 "Only $(Format-Size $free) free on $([System.IO.Path]::GetPathRoot($rootDir)); $(Format-Size $MinFreeBytes) is required." 'Free disk space and run the installer again.' }
    $source = Get-ProxyArchive $settings.Channel

    $backup = $null
    if ($Selection.ContainsKey('InPlace') -and $Selection.InPlace) { $backup = Backup-ExistingHermes $hermes }
    $state = Get-State $rootDir
    if ($null -eq $state) { $state = New-State $rootDir $client $(if ($archive) { $archive.Path } else { $null }) }
    $state.clientDir = $client.Path; $state.gameExe = $client.GameExe
    $state.server = $settings.Server; $state.serverAddress = $settings.Address; $state.channel = $settings.Channel
    if ($backup) { Set-StateValue $state 'hermesBackup' $backup }
    [void][System.IO.Directory]::CreateDirectory($hermes)
    Set-LogFile (Join-Path $hermes 'install.log')
    Save-State $state
    Out-Log "  installing into $hermes"
    Copy-BackupAccountData $state $hermes

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
    else {
        $lnkPath = Get-ShortcutPath $state
        if (Read-YesNo "Create a desktop shortcut `"$([System.IO.Path]::GetFileNameWithoutExtension($lnkPath))`"?" $true) { $state.shortcut = New-DesktopShortcut $state $lnkPath }
    }
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
        $backup = Join-Path $State.root 'JimsProxy-AccountData-backup'
        if (Read-YesNo "Move AccountData to $backup before deleting?" $true) {
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
    if ($State.shortcut -and [System.IO.File]::Exists($State.shortcut)) {
        # Only a shortcut that still starts this installation is deleted; another installation
        # may have taken the name since.
        if ((Get-ShortcutTarget $State.shortcut) -ieq (Join-Path $hermes $PlayCmdName)) { Remove-Item -LiteralPath $State.shortcut -Force; Out-Log "  deleted $($State.shortcut)" }
        else { Out-Log "  $($State.shortcut) now starts another installation; left in place" }
    }
    $earlier = "$(Get-StateValue $State 'hermesBackup')"
    if ($earlier -and [System.IO.Directory]::Exists($earlier)) {
        Out-Log "  kept $earlier, the Hermes that was here before this installation; rename it to Hermes to use it again"
    }
    Out-Log ''
    Out-Log 'JimsProxy is uninstalled. The game client was not modified beyond the items listed above.'
}

# ================================================================== main
$exitCode = 0
try {
    try { [System.IO.File]::WriteAllText($script:LogPath, '', $Utf8NoBom) } catch { }
    Out-Log ''
    Out-Log "JimsProxy quick-start installer $InstallerVersion" -Color White
    Out-Log 'WoW Classic Era 1.14.2 on Kronos' -Color DarkGray
    Out-Log "Started $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')), log: $($script:LogPath)" -Color DarkGray

    $actions = @($Update, $Reconfigure, $Uninstall | Where-Object { $_ })
    if ($actions.Count -gt 1) { Stop-Install 1 'Use only one of -Update, -Reconfigure, -Uninstall.' 'Correct the parameters and run the installer again.' }
    if ($ClientDir -and $ClientArchive) { Stop-Install 1 'Use either -ClientDir or -ClientArchive, not both.' 'Correct the parameters and run the installer again.' }
    if ($Channel -and $Channel -notin @('stable', 'beta')) { Stop-Install 1 "-Channel must be stable or beta (got '$Channel')." 'Correct the parameters and run the installer again.' }
    $action = $Update -or $Reconfigure -or $Uninstall
    if ($action -and ($ClientArchive -or $ExtractTo -or $CopyTo)) { Stop-Install 1 '-Update, -Reconfigure and -Uninstall act on an existing installation; they cannot be combined with -ClientArchive, -ExtractTo or -CopyTo.' 'Correct the parameters and run the installer again.' }

    if (-not $Yes) {
        Out-Log ''
        Out-Log 'This installer sets up JimsProxy for WoW Classic Era 1.14.2 (build 42597) on Kronos:'
        Out-Log '  1. Checks this PC and finds your 1.14.2 client (or extracts or copies one for you).'
        Out-Log '  2. Downloads the current JimsProxy into a Hermes folder next to the client.'
        Out-Log '  3. Points the client at the proxy and, if you want, installs the JimsPlus addon.'
        Out-Log '  4. Creates a "Play Kronos" command that starts the proxy and the game together.'
        Out-Log 'It never downloads game files and needs no administrator rights. Run it again later'
        Out-Log 'to update, reconfigure, or uninstall.'
        Out-Log ''
        Write-Host 'Guide: ' -NoNewline -ForegroundColor DarkGray
        Write-Host $GuideUrl -ForegroundColor Cyan
        Write-LogOnly "Guide: $GuideUrl"
        Out-Log ''
        if (Test-InteractiveConsole) {
            Write-Host 'Press Enter to continue, or Q to quit.' -ForegroundColor Cyan
            Clear-KeyBuffer
            while ($true) {
                $key = [Console]::ReadKey($true).Key
                if ($key -eq 'Enter') { break }
                if ($key -eq 'Q' -or $key -eq 'Escape') { Stop-Install 6 'Cancelled before any change.' $null }
            }
        } else {
            if ((Read-Line 'Press Enter to continue, or Q to quit:') -ieq 'Q') { Stop-Install 6 'Cancelled before any change.' $null }
        }
    }

    $state = $null
    $selection = $null
    if ($Root) {
        $rootFull = Get-NormalPath ($Root.Trim().Trim('"'))
        $state = Get-State $rootFull
        if ($null -eq $state) { Stop-Install 3 "No quick-start installation found in $Root (Hermes\quickstart.json is missing, or does not describe this folder)." 'Pass the folder that contains Hermes, or run the installer without -Root.' }
        if ($script:MovedFrom.ContainsKey($rootFull)) { Out-Log "This installation was copied or moved from $($script:MovedFrom[$rootFull]); only $rootFull is changed." }
        if ($state.status -ne 'installed' -and -not $Uninstall) {
            if ($Update -or $Reconfigure) { Stop-Install 3 "The installation in $(Join-Path $rootFull 'Hermes') is unfinished." 'Run the installer without -Update or -Reconfigure to finish it, or with -Uninstall to remove it.' }
            # -Root alone on an unfinished installation: finish it.
            $c = Get-FolderCandidate "$($state.clientDir)"
            if (-not $c.Usable) { Stop-Install 3 "The client recorded for $rootFull is $($c.Status)." $RequirementText }
            Out-Log "Resuming the unfinished installation in $(Join-Path $rootFull 'Hermes')."
            $selection = @{ Client = $c; Archive = $null }
            $state = $null
        }
    }
    $needsNetwork = -not ($Uninstall -or $Reconfigure)
    $probe = if ($Root) { $Root } elseif ($ClientDir) { $ClientDir } else { $null }
    if ($probe -and -not (Test-Path -LiteralPath $probe)) { $probe = $null }
    Invoke-Preflight $needsNetwork $probe

    while ($null -eq $state -and $null -eq $selection) {
        $selection = Select-Client
        if ($selection.Archive) {
            if ($action) { Stop-Install 3 'A client archive is not an installation; -Update, -Reconfigure and -Uninstall need an installed client.' 'Select the installed client instead.' }
            break
        }
        $rootDir = Get-InstallRoot $selection.Client.Path
        $existing = Get-State $rootDir
        $hermes = Join-Path $rootDir 'Hermes'
        if ($existing -and $script:MovedFrom.ContainsKey($rootDir)) { Out-Log "This installation was copied or moved from $($script:MovedFrom[$rootDir]); only $rootDir is changed." }
        if ($existing -and $existing.status -eq 'installed') { $state = $existing; break }
        if ($existing) {
            # An unfinished quick-start installation: finish it, or remove it with -Uninstall.
            if ($Update -or $Reconfigure) { Stop-Install 3 "The installation in $hermes is unfinished." 'Run the installer without -Update or -Reconfigure to finish it, or with -Uninstall to remove it.' }
            if ($Uninstall) { $state = $existing; break }
            Out-Log "Resuming the unfinished installation in $hermes."
            break
        }
        if ($action) {
            $note = if ([System.IO.Directory]::Exists($hermes)) { " ($hermes was not created by this installer)" } else { '' }
            Stop-Install 3 "No quick-start installation found for $($selection.Client.Path)$note." 'Run the installer without -Update, -Reconfigure, or -Uninstall to install.'
        }
        $reason = $null
        if ([System.IO.Directory]::Exists($hermes)) { $reason = 'foreign' }
        elseif (-not (Test-Writable $rootDir)) { $reason = 'readonly' }
        if ($reason) {
            if ($CopyTo) {
                $what = if ($reason -eq 'foreign') { "$hermes already exists" } else { "$rootDir cannot be written" }
                $selection.Client = Invoke-CopyRoute $selection.Client $CopyTo $null "$what; copying the client to $CopyTo (-CopyTo)."
                break
            }
            if ($Yes) {
                $why = if ($reason -eq 'foreign') { "$hermes already exists and was not created by this installer (a launcher or manual installation)." } else { "$rootDir cannot be written without administrator rights (for example, under Program Files)." }
                Stop-Install 3 $why 'Add -CopyTo <empty folder> to copy the client and install there, or run the installer without -Yes to choose.'
            }
            $choice = Select-ExistingHermesAction $selection.Client $reason
            if ($choice.Action -eq 'back') { $selection = $null; continue }
            if ($choice.Action -eq 'inplace') { $selection.InPlace = $true; break }
            $selection.Client = $choice.Client
            break
        }
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
            Write-Heading "Existing installation in $(Join-Path $state.root 'Hermes')"
            Out-Log "Proxy $(Get-ProxyVersion (Join-Path $state.root 'Hermes')), $($state.channel) channel, server $($state.serverAddress)"
            $items = @(
                (New-MenuItem -Key 'update' -Text 'Update the proxy'),
                (New-MenuItem -Key 'reconfigure' -Text 'Reconfigure (server, channel)'),
                (New-MenuItem -Key 'uninstall' -Text 'Uninstall'),
                (New-MenuItem -Key 'quit' -Text 'Quit')
            )
            switch (Show-Menu 'Select:' $items 'update') {
                'update'      { Invoke-Update $state }
                'reconfigure' { Invoke-Reconfigure $state }
                'uninstall'   { Invoke-Uninstall $state $true }
                'quit'        { Stop-Install 6 'Cancelled; nothing was changed.' $null }
            }
        }
    } else {
        Invoke-Install $selection
    }
    $script:Finished = $true
}
catch {
    $script:Finished = $true
    End-ProgressBar
    Out-Log ''
    Out-Log "Stopped at: $($script:Step)" -Color Red
    Out-Log "Unexpected error: $($_.Exception.Message)" -Color Red
    Out-Log "  at $($_.InvocationInfo.PositionMessage -replace "`r?`n", ' ')" -Color DarkGray
    Out-Log "Log:        $($script:LogPath)" -Color DarkGray
    Out-Log 'Next:       Run the installer again; a partial installation is completed on the next run. If it repeats, report it with the log.' -Color White
    $exitCode = 1
}
finally {
    End-ProgressBar
    if ($script:Extracting) {
        Out-Log ''
        Out-Log "The $($script:ExtractVerb) did not finish. $($script:Extracting) holds an incomplete client and must be deleted before the installer is run again." -Color Red
    }
    foreach ($p in $script:TempPaths) {
        try {
            if ([System.IO.Directory]::Exists($p)) { Remove-Item -LiteralPath $p -Recurse -Force }
            elseif ([System.IO.File]::Exists($p)) { Remove-Item -LiteralPath $p -Force }
        } catch { }
    }
    if (-not $script:Finished) { Out-Log 'Cancelled (Ctrl+C).' }
    # Started by double-clicking Install JimsProxy.cmd: keep the window open until the result
    # has been read, whatever the outcome (success, failure, Q, or Ctrl+C). The marker file
    # tells the .cmd that the script ran and took care of the window (waited, or not because of
    # -Yes or redirection); without it, PowerShell never got here and the .cmd waits itself.
    if ($env:JIMSPROXY_QS_PAUSE -eq '1') { try { [System.IO.File]::WriteAllText((Join-Path $env:TEMP 'jimsproxy-quickstart.paused'), '') } catch { } }
    if ($env:JIMSPROXY_QS_PAUSE -eq '1' -and -not $Yes -and (Test-InteractiveConsole)) {
        Clear-KeyBuffer
        Write-Host ''
        Write-Host 'Press any key to close this window.' -ForegroundColor DarkGray
        try { [void][Console]::ReadKey($true) } catch { }
    }
    if (-not $script:Finished) {
        # Ctrl+C: PowerShell would report exit code 0. Report "cancelled" instead, but only
        # when this script is the process (started with -File), never in an interactive shell.
        if ([Environment]::CommandLine -match [regex]::Escape($PSCommandPath)) { [Environment]::Exit(6) }
    }
}
exit $exitCode

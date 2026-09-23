<#
.SYNOPSIS
  Start JimsProxy, wait until it is ready, start the game, and stop the proxy when the game
  exits. Sets SET portal in WTF\Config.wtf (previous file kept as Config.wtf.bak). See
  docs/MANUAL-INSTALL.md; play.bat runs this script without changing the execution policy.

.PARAMETER ProxyDir
  Folder containing JimsProxy.exe. Default: this script's folder, then .\Hermes, then ..\Hermes.

.PARAMETER GameExe
  Path to WowClassic_ForCustomServers.exe. Default: <root>\World of Warcraft\_classic_era_\WowClassic_ForCustomServers.exe
  where <root> is the folder that contains Hermes\.

.EXAMPLE
  .\play.ps1 -GameExe "D:\Kronos\World of Warcraft\_classic_era_\WowClassic_ForCustomServers.exe"
#>
[CmdletBinding()]
param(
    [string]$ProxyDir,
    [string]$GameExe
)

$ErrorActionPreference = 'Stop'
$TimeoutSeconds   = 60
$ReadyLine        = 'Starting WorldSocket service'
$ShutdownSentinel = '__LAUNCHER_SHUTDOWN__'
$ShutdownAck      = '__PROXY_SHUTDOWN_ACK__'
$FatalPatterns    = 'Config loading failed|verification of the config failed|Failed to start|AesGcm is not supported'

function Say($msg)  { Write-Host "[play] $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "[play] ERROR: $msg" -ForegroundColor Red; exit 1 }

# ------------------------------------------------------------------ locate files
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$candidates = @()
if ($ProxyDir) { $candidates += $ProxyDir }
$candidates += $scriptDir, (Join-Path $scriptDir 'Hermes'), (Join-Path (Split-Path -Parent $scriptDir) 'Hermes'), (Get-Location).Path, (Join-Path (Get-Location).Path 'Hermes')
$ProxyDir = $null
foreach ($c in $candidates) {
    if ($c -and (Test-Path (Join-Path $c 'JimsProxy.exe'))) { $ProxyDir = (Resolve-Path $c).Path; break }
}
if (-not $ProxyDir) { Fail "JimsProxy.exe not found. Pass -ProxyDir <folder containing JimsProxy.exe>." }
$proxyExe = Join-Path $ProxyDir 'JimsProxy.exe'
$configPath = Join-Path $ProxyDir 'HermesProxy.config'
if (-not (Test-Path $configPath)) {
    Fail "HermesProxy.config is missing next to JimsProxy.exe ($ProxyDir). The direct download bundle does not include one: get it from https://raw.githubusercontent.com/jameopotato/jimsproxy/master/HermesProxy/HermesProxy.config, put it beside JimsProxy.exe, and set ServerAddress (see docs\MANUAL-INSTALL.md)."
}
if (-not (Test-Path (Join-Path $ProxyDir 'CSV'))) { Fail "CSV\ folder is missing next to JimsProxy.exe. The proxy cannot start without it." }

$rootDir = Split-Path -Parent $ProxyDir
if (-not $GameExe) {
    foreach ($c in @(
        (Join-Path $rootDir 'World of Warcraft\_classic_era_\WowClassic_ForCustomServers.exe'),
        (Join-Path $rootDir '_classic_era_\WowClassic_ForCustomServers.exe'),
        (Join-Path $rootDir 'WowClassic_ForCustomServers.exe'))) {
        if (Test-Path $c) { $GameExe = $c; break }
    }
}
if (-not $GameExe) { Fail "Game client not found. Pass -GameExe <path to WowClassic_ForCustomServers.exe>." }
if (-not (Test-Path $GameExe)) { Fail "Game executable does not exist: $GameExe" }
$GameExe = (Resolve-Path $GameExe).Path
if ((Split-Path -Leaf $GameExe) -ieq 'WowClassic.exe') {
    Say "WARNING: WowClassic.exe connects only to Blizzard's servers. Use WowClassic_ForCustomServers.exe."
}

# ------------------------------------------------------------------ config values
$configText = Get-Content -Raw $configPath
function ConfigValue($key, $default) {
    $m = [regex]::Match($configText, "<add\s+key=`"$key`"\s+value=`"([^`"]*)`"")
    if ($m.Success -and $m.Groups[1].Value) { return $m.Groups[1].Value } else { return $default }
}
$bnetPort     = [int](ConfigValue 'BNetPort' 1119)
$realmPort    = [int](ConfigValue 'RealmPort' 8084)
$instancePort = [int](ConfigValue 'InstancePort' 8086)
$restPort     = [int](ConfigValue 'RestPort' 8081)
$serverAddr   = ConfigValue 'ServerAddress' '127.0.0.1'
if ($serverAddr -eq '127.0.0.1') {
    Say "WARNING: ServerAddress in HermesProxy.config is still 127.0.0.1 (localhost)."
    Say "         For Kronos set it to login.twinstar-wow.com - see docs\MANUAL-INSTALL.md."
}

# ------------------------------------------------------------------ port check
$busy = @()
foreach ($p in @($bnetPort, $realmPort, $instancePort, $restPort)) {
    try {
        $conn = Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction Stop | Select-Object -First 1
        if ($conn) {
            $owner = ''
            try { $owner = (Get-Process -Id $conn.OwningProcess -ErrorAction Stop).ProcessName } catch {}
            $busy += "$p ($owner, PID $($conn.OwningProcess))"
        }
    } catch {
        # Get-NetTCPConnection throws when nothing listens on that port; that means it is free.
    }
}
if ($busy.Count -gt 0) {
    Fail ("port(s) already in use: " + ($busy -join ', ') + ". A previous JimsProxy is probably still running - end it in Task Manager, or change the ports in HermesProxy.config.")
}

# ------------------------------------------------------------------ portal fix
$wtfDir   = Join-Path (Split-Path -Parent $GameExe) 'WTF'
$wtfPath  = Join-Path $wtfDir 'Config.wtf'
$expected = "SET portal `"127.0.0.1:$bnetPort`""
if (Test-Path $wtfPath) {
    $lines = @(Get-Content $wtfPath)
    $current = $lines | Where-Object { $_ -match '^SET portal ' } | Select-Object -First 1
    if ($current -ne $expected) {
        Copy-Item $wtfPath "$wtfPath.bak" -Force
        if ($current) {
            $done = $false
            $lines = $lines | ForEach-Object { if (-not $done -and $_ -match '^SET portal ') { $done = $true; $expected } else { $_ } }
        } else {
            $lines += $expected
        }
        Set-Content -Path $wtfPath -Value $lines
        Say "Config.wtf: set portal to 127.0.0.1:$bnetPort (backup in Config.wtf.bak)"
    }
} else {
    New-Item -ItemType Directory -Force -Path $wtfDir | Out-Null
    Set-Content -Path $wtfPath -Value @($expected, 'SET textLocale "enUS"', 'SET audioLocale "enUS"')
    Say "Config.wtf: created with portal 127.0.0.1:$bnetPort"
}

# ------------------------------------------------------------------ start proxy
$logPath = Join-Path $ProxyDir 'play-console.log'
$log = New-Object System.IO.StreamWriter($logPath, $false)
$log.AutoFlush = $true

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $proxyExe
$psi.Arguments = '--no-version-check'
$psi.WorkingDirectory = $ProxyDir
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true    # lets us ask for a clean shutdown
$psi.RedirectStandardOutput = $true   # ready-line detection + log
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$proxy = $null
$outTask = $null
$errTask = $null
$script:sawAck = $false

# Pump-Proxy drains whatever the proxy printed since the last call. Both pipes MUST
# be drained continuously: a full pipe buffer stalls the proxy.
function Pump-Proxy {
    $sawReady = $false
    $fatal = $false
    foreach ($which in 'out', 'err') {
        while ($true) {
            $t = if ($which -eq 'out') { $script:outTask } else { $script:errTask }
            if ($null -eq $t -or -not $t.IsCompleted) { break }
            $line = $null
            try { $line = $t.Result } catch { $line = $null }
            if ($null -eq $line) {
                if ($which -eq 'out') { $script:outTask = $null } else { $script:errTask = $null }
                break
            }
            $log.WriteLine($line)
            Write-Host $line
            if ($line -like "*$ReadyLine*")       { $sawReady = $true }
            if ($line -like "*$ShutdownAck*")     { $script:sawAck = $true }
            if ($line -match $FatalPatterns)      { $fatal = $true }
            if ($which -eq 'out') { $script:outTask = $proxy.StandardOutput.ReadLineAsync() }
            else                  { $script:errTask = $proxy.StandardError.ReadLineAsync() }
        }
    }
    return @{ Ready = $sawReady; Fatal = $fatal }
}

function Stop-Proxy {
    if ($null -eq $proxy -or $proxy.HasExited) { return }
    Say "stopping the proxy..."
    try { $proxy.StandardInput.WriteLine($ShutdownSentinel); $proxy.StandardInput.Flush() } catch {}
    $deadline = (Get-Date).AddSeconds(5)
    while (-not $proxy.HasExited -and (Get-Date) -lt $deadline) {
        Pump-Proxy | Out-Null
        Start-Sleep -Milliseconds 100
    }
    if ($proxy.HasExited) { Say "proxy exited cleanly"; return }
    Say "proxy did not answer, closing it forcibly"
    try { $proxy.Kill() } catch {}
    try { $proxy.WaitForExit(3000) | Out-Null } catch {}
}

$exitCode = 0
try {
    Say "starting $proxyExe"
    $proxy = [System.Diagnostics.Process]::Start($psi)
    $script:outTask = $proxy.StandardOutput.ReadLineAsync()
    $script:errTask = $proxy.StandardError.ReadLineAsync()

    # ---- wait for ready
    Say "waiting for `"$ReadyLine`" (up to $TimeoutSeconds s)..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $ready = $false
    while (-not $ready) {
        $r = Pump-Proxy
        if ($r.Ready) { $ready = $true; break }
        if ($r.Fatal) { Start-Sleep -Milliseconds 300; Pump-Proxy | Out-Null; Fail "the proxy reported a startup error - read the lines above (full log: $logPath)" }
        if ($proxy.HasExited) { Start-Sleep -Milliseconds 300; Pump-Proxy | Out-Null; Fail "the proxy exited before it was ready - read the lines above (full log: $logPath)" }
        if ((Get-Date) -gt $deadline) { Fail "timed out after $TimeoutSeconds s waiting for the proxy to become ready (log: $logPath)" }
        Start-Sleep -Milliseconds 100
    }
    Say "proxy is ready on 127.0.0.1:$bnetPort"

    # ---- start game
    Say "starting the game: $GameExe"
    $game = Start-Process -FilePath $GameExe -WorkingDirectory (Split-Path -Parent $GameExe) -PassThru
    while (-not $game.HasExited) {
        Pump-Proxy | Out-Null
        if ($proxy.HasExited) { Say "WARNING: the proxy exited while the game was running (log: $logPath)" ; break }
        Start-Sleep -Milliseconds 250
    }
    if ($game.HasExited) { Say "game process exited (code $($game.ExitCode))" }
}
catch {
    Write-Host "[play] ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    Stop-Proxy
    if ($log) { $log.Dispose() }
}
exit $exitCode

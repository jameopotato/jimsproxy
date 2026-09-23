#Requires -Version 5.1
<#
.SYNOPSIS
  Packs scripts/quickstart into build/JimsProxy-QuickStart.zip (files at the zip root).

.DESCRIPTION
  Adds a generated VERSION.txt (installer version and UTC build date) and prints the zip's
  SHA-256. The version defaults to $InstallerVersion in scripts/quickstart/install.ps1.
  Refuses to pack a .ps1 or .cmd file that contains non-ASCII bytes: Windows PowerShell 5.1
  reads a BOM-less script in the ANSI code page, and cmd.exe reads batch files in the OEM
  code page, so any non-ASCII character can break parsing.

.PARAMETER Version
  Installer version (semver). Default: read from install.ps1.

.EXAMPLE
  powershell -NoProfile -File scripts\build-quickstart.ps1
#>
[CmdletBinding()]
param([string]$Version)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo   = Split-Path -Parent $PSScriptRoot
$src    = Join-Path $PSScriptRoot 'quickstart'
$outDir = Join-Path $repo 'build'
$zipPath = Join-Path $outDir 'JimsProxy-QuickStart.zip'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$installer = Join-Path $src 'install.ps1'
$declared = [regex]::Match([System.IO.File]::ReadAllText($installer), "(?m)^\`$InstallerVersion\s*=\s*'([^']+)'").Groups[1].Value
if (-not $declared) { throw "`$InstallerVersion not found in $installer" }
if (-not $Version) { $Version = $declared }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$Version' is not semver (for example 1.0.0 or 1.1.0-beta.1)." }
if ($Version -ne $declared) { Write-Host "Note: -Version $Version differs from `$InstallerVersion $declared in install.ps1." }

$files = @('Install JimsProxy.cmd', 'install.ps1', 'README.txt')
foreach ($f in $files) {
    $p = Join-Path $src $f
    if (-not [System.IO.File]::Exists($p)) { throw "Missing: $p" }
    if ($f -match '\.(ps1|cmd)$') {
        $bytes = [System.IO.File]::ReadAllBytes($p)
        for ($i = 0; $i -lt $bytes.Length; $i++) {
            if ($bytes[$i] -gt 0x7F) {
                $line = 1 + @($bytes[0..$i] | Where-Object { $_ -eq 0x0A }).Count
                throw "$f contains a non-ASCII byte at line $line; replace it with ASCII."
            }
        }
    }
}

$built = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + ' UTC'
$versionText = "JimsProxy quick-start installer $Version`r`nBuilt $built`r`n"

[void][System.IO.Directory]::CreateDirectory($outDir)
if ([System.IO.File]::Exists($zipPath)) { [System.IO.File]::Delete($zipPath) }
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $src $f), $f, [System.IO.Compression.CompressionLevel]::Optimal)
    }
    $entry = $zip.CreateEntry('VERSION.txt', [System.IO.Compression.CompressionLevel]::Optimal)
    $w = New-Object System.IO.StreamWriter($entry.Open(), $utf8NoBom)
    try { $w.Write($versionText) } finally { $w.Dispose() }
} finally { $zip.Dispose() }

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Built   $zipPath"
Write-Host "Version $Version ($built)"
Write-Host "Size    $((Get-Item -LiteralPath $zipPath).Length) bytes"
Write-Host "SHA-256 $hash"

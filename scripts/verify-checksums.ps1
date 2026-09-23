# Verify JimsProxy release checksums
# Usage: .\verify-checksums.ps1 [-Directory <path>]
#
# Downloads checksums-sha256.txt from the latest release and verifies
# all JimsProxy release archives in the specified directory (default: current dir).

param(
    [string]$Directory = "."
)

$Repo = "jameopotato/jimsproxy"
$ChecksumsFile = "checksums-sha256.txt"
$ChecksumsPath = Join-Path $Directory $ChecksumsFile

Write-Host "=== JimsProxy Release Checksum Verifier ===" -ForegroundColor Cyan
Write-Host ""

# Download checksums file if not present
if (-not (Test-Path $ChecksumsPath)) {
    Write-Host "Downloading $ChecksumsFile from latest release..."
    try {
        gh release download --repo $Repo --pattern $ChecksumsFile --dir $Directory 2>$null
    } catch {
        Write-Host "ERROR: Could not download $ChecksumsFile" -ForegroundColor Red
        Write-Host "Make sure 'gh' CLI is installed or download the file manually from:"
        Write-Host "  https://github.com/$Repo/releases/latest"
        exit 1
    }
    if (-not (Test-Path $ChecksumsPath)) {
        Write-Host "ERROR: Could not download $ChecksumsFile" -ForegroundColor Red
        exit 1
    }
}

Write-Host "Verifying checksums in: $Directory"
Write-Host ""

$Fail = $false

Get-Content $ChecksumsPath | ForEach-Object {
    $line = $_.Trim()
    if ([string]::IsNullOrWhiteSpace($line)) { return }

    # Format: <hash>  <filename> (two spaces between)
    $parts = $line -split '  ', 2
    if ($parts.Count -ne 2) {
        $parts = $line -split ' ', 2
    }
    $expectedHash = $parts[0].Trim()
    $filename = $parts[1].Trim()

    $filepath = Join-Path $Directory $filename

    if (-not (Test-Path $filepath)) {
        Write-Host "  SKIP: $filename (not found)" -ForegroundColor Yellow
        return
    }

    $actualHash = (Get-FileHash -Path $filepath -Algorithm SHA256).Hash.ToLower()

    if ($actualHash -eq $expectedHash) {
        Write-Host "    OK: $filename" -ForegroundColor Green
    } else {
        Write-Host "  FAIL: $filename" -ForegroundColor Red
        Write-Host "    Expected: $expectedHash"
        Write-Host "    Actual:   $actualHash"
        $script:Fail = $true
    }
}

Write-Host ""
if ($Fail) {
    Write-Host "WARNING: One or more checksums did not match!" -ForegroundColor Red
    Write-Host "The files may have been tampered with or corrupted during download."
    exit 1
} else {
    Write-Host "All checksums verified successfully." -ForegroundColor Green
}

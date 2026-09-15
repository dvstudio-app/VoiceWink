# Fetches the Microsoft.WindowsAppRuntime 1.6 bootstrap installer
# (WindowsAppRuntimeInstall-x64.exe, ~61 MB) into installer/runtime/ so that
# the publish step copies it into the build output. Helpers/PrereqInstaller.cs
# spawns this file on clean machines without WinAppRuntime 1.6 pre-installed;
# without it, the very first AudioTranscribe click crashes
# Microsoft.UI.Xaml.dll with a stowed exception.
#
# Usage:
#   # First-time fetch (computes + prints SHA256, no verification):
#   pwsh scripts/fetch-winapp-runtime.ps1
#
#   # Verified fetch (CI / release builds — fails if SHA256 mismatches):
#   pwsh scripts/fetch-winapp-runtime.ps1 -ExpectedSha256 <hash>
#
#   # Force re-download even when the file is present:
#   pwsh scripts/fetch-winapp-runtime.ps1 -Force
#
#   # Version-bump repin (EXPECTED_SHA256 procedure): fresh forced
#   # download that IGNORES the stale pin file (the auto-load below would
#   # otherwise fail the new version against the old hash); prints the new
#   # SHA256 to write into installer/runtime/EXPECTED_SHA256:
#   pwsh scripts/fetch-winapp-runtime.ps1 -Repin
#
# Every download is Authenticode-gated (Valid + Microsoft Corporation signer)
# before its SHA256 is printed or verified. This is PUBLISHER authentication:
# a version bump mints a new pin from the downloaded bytes, and the gate
# guarantees those bytes are a genuine Microsoft-signed binary — it cannot
# prove WHICH servicing build the CDN served (the installer's version
# resource carries only "1.6"). Version identity rests on the versioned
# aka.ms URL, the paired csproj restore of the same build, and the
# release-time checks in scripts/check-publish-payload.ps1.
#
# CI integration:
#   - Run as a pre-build step on Release publish workflows.
#   - Pin -ExpectedSha256 from a GitHub Actions secret or a checked-in
#     manifest so a CDN tampering / mirror confusion can't slip past.
#
# Why this lives outside the csproj as a script rather than as an MSBuild
# Download target:
#   - 61 MB shouldn't slow every dotnet restore.
#   - The hash verification is policy that benefits from explicit invocation.
#   - The file is binary and gitignored; an MSBuild download would have to
#     deal with concurrent-build locks, partial downloads, etc.

[CmdletBinding()]
param(
    [string]$Version = "1.6.250602001",
    [string]$ExpectedSha256 = "",
    [switch]$Force,
    [switch]$Repin
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outDir = Join-Path $repoRoot "installer\runtime"
$outFile = Join-Path $outDir "WindowsAppRuntimeInstall-x64.exe"

# Repin mode: the pin on disk belongs to the PREVIOUS version, so skip the
# auto-load below (which would fail the new download against the old hash)
# and force a fresh download (verifying or hashing a stale on-disk file would
# mint a pin for the wrong bytes). Identity is covered by the Authenticode
# gate; the printed SHA256 becomes the new pin.
if ($Repin) {
    if ($ExpectedSha256) {
        throw "-Repin and -ExpectedSha256 are mutually exclusive: repin exists to MINT a new pin, not to verify against one."
    }
    $Force = $true
    Write-Host "Repin mode: ignoring installer/runtime/EXPECTED_SHA256; forcing a fresh download."
}

# Default `-ExpectedSha256` from the tracked `installer/runtime/EXPECTED_SHA256`
# pin file when the caller didn't supply one explicitly. The file's first
# non-empty, non-`#` line is the SHA256 hex; comments above it document the
# version + provenance. Lets every fetch verify identity automatically without
# anyone needing to remember the hex.
if (-not $ExpectedSha256 -and -not $Repin) {
    $shaFile = Join-Path $outDir "EXPECTED_SHA256"
    if (Test-Path -LiteralPath $shaFile) {
        # @(...) forces array context — without it, `Where-Object` unwraps a
        # single matching line to a scalar string and `.Count` would throw
        # under Set-StrictMode.
        $shaLines = @(Get-Content $shaFile | Where-Object { $_ -and ($_ -notmatch '^\s*#') -and ($_ -match '\S') })
        if ($shaLines.Count -gt 0) {
            $candidate = $shaLines[0].Trim()
            # Codex 2026-05-20 release-pipeline review: fail-closed if the
            # pin file exists but yields a non-hex / wrong-length value.
            # Otherwise an empty / comment-only / typo'd file would silently
            # leave the download unverified — defeating the pin's purpose.
            if ($candidate -notmatch '^[0-9A-Fa-f]{64}$') {
                throw "SHA pin file exists but contains no valid 64-character hex SHA256 (got '$candidate'). Fix or delete $shaFile."
            }
            $ExpectedSha256 = $candidate
            Write-Host "Using pinned SHA256 from installer/runtime/EXPECTED_SHA256"
        }
        else {
            throw "SHA pin file $shaFile exists but is empty / comment-only. Add the 64-character hex SHA256 (or delete the file to fall back to unverified fetch)."
        }
    }
}

if ((Test-Path -LiteralPath $outFile) -and -not $Force) {
    $sizeMb = "{0:N2}" -f ((Get-Item -LiteralPath $outFile).Length / 1MB)
    Write-Host "Already present: $outFile ($sizeMb MB)"

    if ($ExpectedSha256) {
        $actual = (Get-FileHash -LiteralPath $outFile -Algorithm SHA256).Hash
        if ($actual -ne $ExpectedSha256) {
            throw "SHA256 mismatch on existing file: expected $ExpectedSha256, got $actual. Delete the file and re-run with -Force to refetch."
        }
        Write-Host "SHA256 verified: $actual"
    }
    else {
        $actual = (Get-FileHash -LiteralPath $outFile -Algorithm SHA256).Hash
        Write-Host "SHA256 (unverified): $actual"
        Write-Host "Use -Force to refetch, or pass -ExpectedSha256 to verify."
    }
    exit 0
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Microsoft documents the per-version download URL pattern at
# https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads.
# The aka.ms/windowsappsdk redirector resolves to the actual CDN URL.
$url = "https://aka.ms/windowsappsdk/1.6/$Version/windowsappruntimeinstall-x64.exe"
Write-Host "Downloading WindowsAppRuntime $Version from $url ..."

$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing
}
catch {
    if (Test-Path -LiteralPath $outFile) { Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue }
    throw "Download failed: $($_.Exception.Message)"
}
$sw.Stop()

$sizeMb = "{0:N2}" -f ((Get-Item -LiteralPath $outFile).Length / 1MB)
$elapsedSec = "{0:N1}" -f $sw.Elapsed.TotalSeconds
Write-Host "Downloaded $sizeMb MB in $elapsedSec s -> $outFile"

# Authenticode gate — BEFORE the SHA256 is printed or verified. A version bump
# (-Repin) mints the new pin from this very download, so without this check an
# unsigned / non-Microsoft substitution could simply mint its own pin. This
# authenticates the PUBLISHER only — it cannot prove which servicing build the
# CDN served (see the header note). Same intent as Check 2 in
# scripts/check-publish-payload.ps1, with the subject match anchored to the
# exact `O=Microsoft Corporation` DN component (a bare substring match would
# also accept e.g. "O=Microsoft Corporation Ltd").
$sig = Get-AuthenticodeSignature -LiteralPath $outFile
if ($sig.Status -ne 'Valid') {
    Remove-Item -LiteralPath $outFile -Force
    throw "Authenticode status on downloaded file: $($sig.Status). Expected 'Valid'. Deleted downloaded file."
}
if ($null -eq $sig.SignerCertificate) {
    Remove-Item -LiteralPath $outFile -Force
    throw "Downloaded file has no signer certificate. Deleted downloaded file."
}
if ($sig.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation\s*(,|$)') {
    $signerSubject = $sig.SignerCertificate.Subject
    Remove-Item -LiteralPath $outFile -Force
    throw "Downloaded file's signer is not Microsoft Corporation (subject: $signerSubject). Deleted downloaded file."
}
Write-Host "Authenticode verified: signed by Microsoft Corporation."

$actual = (Get-FileHash -LiteralPath $outFile -Algorithm SHA256).Hash
Write-Host "SHA256: $actual"

if ($ExpectedSha256) {
    if ($actual -ne $ExpectedSha256) {
        Remove-Item $outFile -Force
        throw "SHA256 mismatch: expected $ExpectedSha256, got $actual. Deleted downloaded file."
    }
    Write-Host "SHA256 verified against pinned hash."
}
elseif ($Repin) {
    Write-Host ""
    Write-Host "Repin: write the SHA256 above into installer/runtime/EXPECTED_SHA256"
    Write-Host "(hex line + comment block), then rerun this script normally to prove"
    Write-Host "the new pin matches."
}
else {
    Write-Warning "No -ExpectedSha256 supplied; download not verified."
    Write-Host ""
    Write-Host "For CI/production, re-invoke with verification:"
    Write-Host "  pwsh scripts/fetch-winapp-runtime.ps1 -ExpectedSha256 $actual"
}

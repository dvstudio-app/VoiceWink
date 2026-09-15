# fetch-vcredist.ps1 — MAINTAINER refresh tool for the four app-local Microsoft
# Visual C++ runtime DLLs (vcruntime140 / vcruntime140_1 / msvcp140 / vcomp140)
# that VoiceWink ships next to whisper.dll (runtimes\win-x64\).
#
# IMPORTANT: this is NOT a per-build step. The four DLLs are tiny (~920 KB) and
# COMMITTED to installer/runtime/vcredist/ (verified by the manifest), so normal
# builds/CI need none of the tooling below. Run this only to:
#   - check whether Microsoft has shipped a newer redist  (-CheckOnly), or
#   - re-extract / refresh the committed DLLs from the pinned redist (default), or
#   - bump to a NEW pinned redist version and print fresh manifest values
#     (-Regenerate; see the refresh checklist in MANIFEST.psd1).
#
# WHY EXTRACTION NEEDS WiX (no built-in works — empirically confirmed 2026-06-02)
# -----------------------------------------------------------------------------
# vc_redist.x64.exe is a WiX "Burn" bundle: its payload MSIs live in a custom
# attached container glued onto the PE. `/layout` just copies the bundle exe,
# `/extract:` is not a Burn switch, and `expand.exe` cannot read the container.
# `wix burn extract` (WiX v5 — v6/v7 require the paid OSMF EULA, so pin v5) is
# the one tool that cleanly yields the MSIs with original names; `msiexec /a`
# (administrative image, NO elevation, NO machine install) then unpacks the DLLs.
# See docs/plans/2026-06-02-1939-vcredist-applocal-bundle/50-decision.md.
#
# Usage:
#   pwsh scripts/fetch-vcredist.ps1 -CheckOnly      # drift check: pinned vs upstream SHA
#   pwsh scripts/fetch-vcredist.ps1                 # re-extract + verify vs MANIFEST.psd1
#   pwsh scripts/fetch-vcredist.ps1 -Regenerate     # re-extract + PRINT manifest values
#
# Prereq for extraction modes (NOT -CheckOnly): WiX v5 on PATH —
#   dotnet tool install --global wix --version 5.0.2

[CmdletBinding()]
param(
    # Bundle SHA256 pin. Defaults to installer/runtime/vcredist/EXPECTED_SHA256.
    [string]$ExpectedSha256 = "",
    # Drift check only: download the CURRENT upstream redist, compare its SHA256
    # to the pin, report up-to-date / newer-available, then exit. No extraction.
    [switch]$CheckOnly,
    # Re-extract and PRINT computed manifest values instead of verifying against
    # the (possibly being-bumped) manifest. Used when refreshing the version.
    [switch]$Regenerate,
    # Force re-download even if a cached bundle is present in the scratch dir.
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outDir = Join-Path $repoRoot "installer\runtime\vcredist"
$shaFile = Join-Path $outDir "EXPECTED_SHA256"
$manifestPath = Join-Path $outDir "MANIFEST.psd1"
$url = "https://aka.ms/vs/17/release/vc_redist.x64.exe"

# The four DLLs we ship, and which MSI each comes from (informational; both MSIs
# are unpacked into one administrative image, so we just collect by name).
$TargetDlls = @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'vcomp140.dll')

# --- default the SHA pin from EXPECTED_SHA256 (same convention as fetch-winapp-runtime.ps1) ---
if (-not $ExpectedSha256) {
    if (Test-Path -LiteralPath $shaFile) {
        $shaLines = @(Get-Content $shaFile | Where-Object { $_ -and ($_ -notmatch '^\s*#') -and ($_ -match '\S') })
        if ($shaLines.Count -gt 0) {
            $candidate = $shaLines[0].Trim()
            if ($candidate -notmatch '^[0-9A-Fa-f]{64}$') {
                throw "SHA pin file exists but contains no valid 64-character hex SHA256 (got '$candidate'). Fix or delete $shaFile."
            }
            $ExpectedSha256 = $candidate
            Write-Host "Using pinned bundle SHA256 from installer/runtime/vcredist/EXPECTED_SHA256"
        }
        elseif (-not $Regenerate) {
            throw "SHA pin file $shaFile exists but is empty / comment-only. Add the bundle SHA256 (or run -Regenerate to compute fresh values)."
        }
    }
    elseif (-not $Regenerate) {
        throw "SHA pin file $shaFile not found. Run -Regenerate to bootstrap, or create the pin."
    }
}

# --- download the bundle into a scratch dir (kept OUT of the tracked outDir) ---
$scratch = Join-Path ([IO.Path]::GetTempPath()) "vw-vcredist-fetch"
if ($Force -and (Test-Path -LiteralPath $scratch)) { Remove-Item -Recurse -Force -LiteralPath $scratch }
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$bundle = Join-Path $scratch "vc_redist.x64.exe"

# -CheckOnly is the servicing/CVE drift probe, so it must NEVER trust a cached
# bundle (a stale cache would falsely report "up to date" while a newer redist is
# live). Force a fresh download in that mode regardless of -Force.
if ((Test-Path -LiteralPath $bundle) -and -not $Force -and -not $CheckOnly) {
    Write-Host "Using cached bundle: $bundle (pass -Force to re-download)"
}
else {
    if ($CheckOnly -and (Test-Path -LiteralPath $bundle)) {
        Remove-Item -LiteralPath $bundle -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Downloading vc_redist.x64.exe from $url ..."
    try {
        Invoke-WebRequest -Uri $url -OutFile $bundle -UseBasicParsing
    }
    catch {
        if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Force -ErrorAction SilentlyContinue }
        throw "Download failed: $($_.Exception.Message)"
    }
}

$bundleSha = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
$bundleVer = (Get-Item -LiteralPath $bundle).VersionInfo.ProductVersion
Write-Host ("Bundle: version $bundleVer, SHA256 $bundleSha")

# --- -CheckOnly: drift report, then exit (no extraction) ---
if ($CheckOnly) {
    Write-Host ""
    if (-not $ExpectedSha256) {
        Write-Host "No pin recorded yet — upstream is $bundleVer ($bundleSha)." -ForegroundColor Yellow
    }
    elseif ($bundleSha -eq $ExpectedSha256) {
        Write-Host "UP TO DATE: pinned redist matches upstream ($bundleVer)." -ForegroundColor Green
    }
    else {
        Write-Host "NEWER REDIST AVAILABLE." -ForegroundColor Yellow
        Write-Host ("  pinned   : $ExpectedSha256")
        Write-Host ("  upstream : $bundleSha  (version $bundleVer)")
        Write-Host "  To adopt it, follow the refresh checklist in installer/runtime/vcredist/MANIFEST.psd1:"
        Write-Host "    1. update EXPECTED_SHA256 to the upstream hash + bump the comment,"
        Write-Host "    2. run: pwsh scripts/fetch-vcredist.ps1 -Regenerate,"
        Write-Host "    3. paste the printed values into MANIFEST.psd1, commit DLLs + pins together."
    }
    exit 0
}

# --- verify the bundle SHA before cracking it open (skip in -Regenerate bootstrap) ---
if ($ExpectedSha256 -and ($bundleSha -ne $ExpectedSha256)) {
    if ($Regenerate) {
        Write-Warning "Bundle SHA ($bundleSha) != pinned ($ExpectedSha256). -Regenerate: proceeding with the downloaded bundle. Update EXPECTED_SHA256 to match."
    }
    else {
        throw "Bundle SHA256 mismatch: expected $ExpectedSha256, got $bundleSha. Upstream moved — run -CheckOnly, then -Regenerate to adopt the new version. Refusing to extract an unpinned bundle."
    }
}

# --- Authenticode-verify the bundle before cracking it open ---
# The SHA pin is the strong identity anchor when it matches; this is defense-in-
# depth for the -Regenerate path (SHA mismatch allowed) and against a tampered
# cached/downloaded bundle. Fail-closed: never extract an unsigned / non-Microsoft
# bundle.
$bundleSig = Get-AuthenticodeSignature -LiteralPath $bundle
if ($bundleSig.Status -ne 'Valid') {
    throw "vc_redist.x64.exe Authenticode status is '$($bundleSig.Status)', expected 'Valid'. Refusing to extract."
}
if ($null -eq $bundleSig.SignerCertificate -or $bundleSig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    $sigSubj = if ($bundleSig.SignerCertificate) { $bundleSig.SignerCertificate.Subject } else { '<none>' }
    throw "vc_redist.x64.exe signer is not Microsoft Corporation (got: $sigSubj). Refusing to extract."
}
Write-Host "Bundle Authenticode: Valid, signed by Microsoft Corporation."

# --- require a pre-OSMF WiX (extraction prereq) ---
# Accepts WiX major v4 OR v5: both ship the free `wix burn extract` subcommand we
# rely on. v6/v7 are rejected because they require accepting the paid Open Source
# Maintenance Fee (OSMF) EULA. The install hint pins 5.0.2 (the latest free major)
# as the recommended version; v4 also works if already present. The exact WiX
# version doesn't affect output identity - the extracted DLLs are verified by
# SHA256 against MANIFEST.psd1 either way.
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) {
    throw "WiX not found on PATH. The redist is a Burn bundle; 'wix burn extract' is required. Install the recommended version: dotnet tool install --global wix --version 5.0.2"
}
$wixVerRaw = (& wix --version 2>&1 | Out-String).Trim()
$wixMajor = if ($wixVerRaw -match '^(\d+)\.') { [int]$matches[1] } else { 0 }
if ($wixMajor -lt 4) {
    throw "WiX version '$wixVerRaw' too old (need v4 or v5 'wix burn extract'). Install: dotnet tool install --global wix --version 5.0.2"
}
if ($wixMajor -ge 6) {
    throw "WiX v$wixMajor requires the paid Open Source Maintenance Fee (OSMF) EULA. Pin a free major (v4 or v5): dotnet tool uninstall --global wix; dotnet tool install --global wix --version 5.0.2"
}
Write-Host "Using WiX $wixVerRaw"

# --- step 1: wix burn extract (yields the payload MSIs with original names) ---
$extractDir = Join-Path $scratch "burn-extract"
if (Test-Path -LiteralPath $extractDir) { Remove-Item -Recurse -Force -LiteralPath $extractDir }
New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
Write-Host "Extracting Burn bundle (wix burn extract)..."
& wix burn extract $bundle -o $extractDir -oba (Join-Path $scratch "burn-ba") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "wix burn extract failed (exit $LASTEXITCODE)." }

$msiMin = Get-ChildItem -LiteralPath $extractDir -Recurse -Filter 'vc_runtimeMinimum_x64.msi' -File | Select-Object -First 1
$msiAdd = Get-ChildItem -LiteralPath $extractDir -Recurse -Filter 'vc_runtimeAdditional_x64.msi' -File | Select-Object -First 1
if (-not $msiMin) { throw "vc_runtimeMinimum_x64.msi not found after extract — redist bundle shape may have changed." }
if (-not $msiAdd) { throw "vc_runtimeAdditional_x64.msi not found after extract — redist bundle shape may have changed." }

# --- step 2: msiexec /a (administrative image unpack — no elevation, no install) ---
$adminImg = Join-Path $scratch "admin-install"
if (Test-Path -LiteralPath $adminImg) { Remove-Item -Recurse -Force -LiteralPath $adminImg }
New-Item -ItemType Directory -Force -Path $adminImg | Out-Null
foreach ($msi in @($msiMin.FullName, $msiAdd.FullName)) {
    $msiLog = Join-Path $scratch ("msi-" + (Split-Path $msi -Leaf) + ".log")
    Write-Host ("msiexec administrative-image unpack: " + (Split-Path $msi -Leaf))
    $p = Start-Process msiexec -ArgumentList @('/a', $msi, '/qn', "TARGETDIR=$adminImg", '/L*v', $msiLog) -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "msiexec /a failed for $msi (exit $($p.ExitCode)). See $msiLog." }
}

# --- step 3: collect the four DLLs into the tracked outDir ---
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$collected = foreach ($dll in $TargetDlls) {
    $found = Get-ChildItem -LiteralPath $adminImg -Recurse -Filter $dll -File | Select-Object -First 1
    if (-not $found) { throw "$dll not found in the administrative image — redist contents may have changed." }
    Copy-Item -LiteralPath $found.FullName -Destination (Join-Path $outDir $dll) -Force
    $dest = Join-Path $outDir $dll
    [PSCustomObject]@{
        Name        = $dll
        FileVersion = (Get-Item -LiteralPath $dest).VersionInfo.FileVersion
        Sha256      = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}
Write-Host ""
Write-Host "Collected into $outDir :"
$collected | ForEach-Object { "   {0,-22} v{1}  {2}" -f $_.Name, $_.FileVersion, $_.Sha256 }

# --- step 4: verify vs manifest, OR (in -Regenerate) print fresh manifest values ---
if ($Regenerate) {
    Write-Host ""
    Write-Host "=== -Regenerate: paste these into installer/runtime/vcredist/MANIFEST.psd1 ===" -ForegroundColor Cyan
    Write-Host ("    RedistVersion = '{0}'" -f $bundleVer)
    Write-Host "    Dlls = @("
    foreach ($c in $collected) {
        Write-Host ("        @{{ Name = '{0}'; FileVersion = '{1}'; Sha256 = '{2}' }}" -f $c.Name, $c.FileVersion, $c.Sha256)
    }
    Write-Host "    )"
    Write-Host ""
    Write-Host "Also confirm EXPECTED_SHA256 = $bundleSha (version $bundleVer)." -ForegroundColor Cyan
    exit 0
}

# Non-regenerate: hard-verify the freshly extracted DLLs reproduce the committed
# pin. (check-vcredist-payload.ps1 verifies a SHIPPED runtimes\win-x64 layout; the
# tracked outDir is flat, so we do a direct per-DLL manifest SHA comparison here.)
$manifest = Import-PowerShellDataFile -LiteralPath $manifestPath
$mismatch = $false
foreach ($entry in $manifest.Dlls) {
    $match = $collected | Where-Object { $_.Name -eq $entry.Name } | Select-Object -First 1
    $expected = ([string]$entry.Sha256).ToUpperInvariant()
    if (-not $match) { Write-Host "[FAIL] $($entry.Name) not extracted." -ForegroundColor Red; $mismatch = $true }
    elseif ($match.Sha256 -ne $expected) {
        Write-Host ("[FAIL] {0} SHA256 {1} != manifest {2}" -f $entry.Name, $match.Sha256, $expected) -ForegroundColor Red
        $mismatch = $true
    }
    else { Write-Host "[OK]  $($entry.Name) reproduces manifest pin" -ForegroundColor Green }
}
if ($mismatch) {
    throw "Extracted DLLs do not match MANIFEST.psd1. If you intend to bump the redist version, run -Regenerate and update the manifest + EXPECTED_SHA256."
}
Write-Host ""
Write-Host "Refresh complete: committed DLLs reproduce from the pinned redist ($bundleVer)." -ForegroundColor Green

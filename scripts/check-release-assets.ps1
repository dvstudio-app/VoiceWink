# Pre-release validation gate for VoiceWink — fails the build if shipped legal
# assets still contain DRAFT markers or address placeholders, the bin output is
# missing the GPL v3 LICENSE file, the privacy policy has dropped the
# custom-endpoint disclosure, or (when invoked with -PortableZip) the portable
# ZIP is missing any of its required GPL §6 source-release artefacts.
#
# Wired into the release flow so a Release that contradicts the EULA refuses to
# ship.
#
# This script implements the validation gate documented in the DV Studio plan
# (.claude/CLAUDE.md, "Execution gates" → Gate 1) and addresses Codex' R3-1
# correction: PowerShell `Select-String -SimpleMatch` treats `|` as literal,
# not regex alternation, so the earlier draft of these checks would have
# silently passed even with placeholders present. This file uses real regex.
#
# Usage:
#   pwsh -File scripts/check-release-assets.ps1
#   pwsh -File scripts/check-release-assets.ps1 -Configuration Release
#   pwsh -File scripts/check-release-assets.ps1 -SkipBinCheck   # legal-asset-only mode
#   pwsh -File scripts/check-release-assets.ps1 -PortableZip installer/Output/VoiceWink-1.2.3-Portable.zip

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipBinCheck,

    [string]$PortableZip,

    [string]$RepoRoot,

    # Release channel of the build being gated (release-update.ps1 passes its -Channel).
    # The PUBLIC channel (win-x64-stable) carries extra fail-closed requirements that
    # tester-ring channels deliberately do not — see the stable-channel gate below.
    [string]$Channel = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ONE definition of "is this a public release", shared with the UAT-coverage gate. Two gates
# disagreeing about that is the failure nobody notices until something ships past a check.
. (Join-Path $PSScriptRoot 'lib/release-channel.ps1')
# Versioned-legal-file resolution + the auto-install disclosure tripwire. Table-driven by
# scripts/test-legal-disclosure.ps1, which CI runs.
. (Join-Path $PSScriptRoot 'lib/legal-disclosure.ps1')

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

$failures = New-Object System.Collections.Generic.List[string]

Write-Host "=== VoiceWink release-asset validation ===" -ForegroundColor Cyan
Write-Host "Repo root:     $RepoRoot"
Write-Host "Configuration: $Configuration"
if (-not [string]::IsNullOrWhiteSpace($PortableZip)) {
    Write-Host "Portable ZIP:  $PortableZip"
}
Write-Host ""

# --- Check 1: legal-asset placeholders --------------------------------------
# Hard-fail on any DRAFT marker or address placeholder in shipped legal assets.
# The pattern is a real regex (no -SimpleMatch) so `|` works as alternation.
$legalDir = Join-Path $RepoRoot "src/VoiceWink/Assets/Legal"
$placeholderPattern = 'DRAFT|postal address to be added'

Write-Host "[1/4] Legal-asset placeholder scan ($legalDir)" -ForegroundColor Yellow
if (-not (Test-Path -LiteralPath $legalDir)) {
    $failures.Add("Legal-asset directory not found: $legalDir")
} else {
    $hits = Get-ChildItem -LiteralPath $legalDir -Filter '*.md' -File |
        Select-String -Pattern $placeholderPattern
    if ($hits) {
        foreach ($hit in $hits) {
            $rel = (Resolve-Path -LiteralPath $hit.Path -Relative).TrimStart('.', '\', '/')
            $failures.Add("Legal placeholder still present: $rel`:$($hit.LineNumber) — $($hit.Line.Trim())")
        }
    } else {
        Write-Host "      OK — no DRAFT markers or address placeholders." -ForegroundColor Green
    }
}

# --- Check 2: LICENSE + notices + VAD model bundled in bin output ------------
if ($SkipBinCheck) {
    Write-Host "[2/4] Bin-output LICENSE/notices/VAD-model check ... SKIPPED (-SkipBinCheck)" -ForegroundColor DarkGray
} else {
    $binDir = Join-Path $RepoRoot "src/VoiceWink/bin/x64/$Configuration/net8.0-windows10.0.22621.0"
    Write-Host "[2/4] Bin-output LICENSE/notices/VAD model ($binDir)" -ForegroundColor Yellow
    $binLicense = Join-Path $binDir "LICENSE"
    if (-not (Test-Path -LiteralPath $binLicense)) {
        $failures.Add("LICENSE missing from bin output: $binLicense — confirm <Content Include='..\..\LICENSE'> is in VoiceWink.csproj and that a build has run for the requested configuration.")
    } else {
        Write-Host "      OK — LICENSE present in bin output." -ForegroundColor Green
    }
    # The bundled MIT Silero VAD model must travel with its attribution file; the
    # deep size/SHA pin runs on the shippable artefacts (publish payload + portable
    # ZIP) — here presence suffices to catch a broken Content include early.
    $binNotices = Join-Path $binDir "THIRD-PARTY-NOTICES.md"
    if (-not (Test-Path -LiteralPath $binNotices)) {
        $failures.Add("THIRD-PARTY-NOTICES.md missing from bin output: $binNotices — confirm <Content Include='..\..\THIRD-PARTY-NOTICES.md'> is in VoiceWink.csproj.")
    } else {
        Write-Host "      OK — THIRD-PARTY-NOTICES.md present in bin output." -ForegroundColor Green
    }
    # TWO bundled Silero models: the ggml one for the no-speech gate, and
    # silero_vad.onnx for Parakeet's decode segmentation. Both degrade SILENTLY when
    # absent, which is why presence is gated rather than assumed.
    foreach ($modelName in @('ggml-silero-v6.2.0.bin', 'silero_vad.onnx')) {
        $binModel = Join-Path $binDir "Assets/Models/$modelName"
        if (-not (Test-Path -LiteralPath $binModel)) {
            $failures.Add("Bundled model missing from bin output: $binModel — confirm src/VoiceWink/Assets/Models/$modelName exists (the Assets\** Content glob ships it).")
        } else {
            Write-Host "      OK — bundled model $modelName present in bin output." -ForegroundColor Green
        }
    }
}

# --- Check 3: legal-asset structural sanity ---------------------------------
# The bundled privacy policy must disclose category C (custom base URLs / Ollama).
# Resolve the highest-numbered privacy-v*.md rather than hardcoding a version, so
# this survives version bumps (v1 -> v3 -> v4 ...) — the plan's S4 sequencing trap:
# a hardcoded privacy-v1.md would fail "not found" the instant v1 is promoted out.
$privacyFile = Resolve-HighestLegalVersionFile -Directory $legalDir -Stem 'privacy'
Write-Host "[3/4] Privacy-policy custom-endpoint disclosure" -ForegroundColor Yellow
if ($privacyFile -and (Test-Path -LiteralPath $privacyFile)) {
    $privacyContent = Get-Content -LiteralPath $privacyFile -Raw
    $hasCustomEndpoint = $privacyContent -match '(custom (base )?URL)|self-hosted|Ollama|user-supplied endpoint'
    if (-not $hasCustomEndpoint) {
        $failures.Add("Privacy policy does not disclose custom-endpoint / category-C path: $privacyFile — required per the GDPR processor classification matrix.")
    } else {
        Write-Host "      OK — privacy policy mentions a custom-endpoint / category-C path." -ForegroundColor Green
    }
} else {
    $failures.Add("Privacy policy not found: no privacy-v*.md in $legalDir")
}

# --- Check 4: portable-ZIP contents -----------------------------------------
# Only runs when -PortableZip is provided, so dev runs and bin-only invocations
# are unaffected. Required entries derive from build-portable.sh's staging step
# (R3-2: VoiceWink.vbs is generated at install time, NOT bundled).
if ([string]::IsNullOrWhiteSpace($PortableZip)) {
    Write-Host "[4/4] Portable ZIP contents ... SKIPPED (no -PortableZip provided)" -ForegroundColor DarkGray
} else {
    if (-not [System.IO.Path]::IsPathRooted($PortableZip)) {
        $PortableZip = Join-Path $RepoRoot $PortableZip
    }
    Write-Host "[4/4] Portable ZIP contents ($PortableZip)" -ForegroundColor Yellow
    if (-not (Test-Path -LiteralPath $PortableZip)) {
        $failures.Add("Portable ZIP not found: $PortableZip")
    } else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $required = @(
            'LICENSE',
            'README.md',
            'VoiceWinkSetup.ps1',
            'Install.bat',
            'app/VoiceWink.dll',
            'app/THIRD-PARTY-NOTICES.md',
            'app/Assets/Models/ggml-silero-v6.2.0.bin',
            # Staged candidate infrastructure: the shipped decode plans whole-buffer
            # chunks and does not read this today, but SherpaVadSegmenter SHA-pins the
            # artifact for the registered batch-2 rescue candidate — absent or altered,
            # that candidate strands silently. Mandatory like the Parakeet natives below.
            'app/Assets/Models/silero_vad.onnx',
            # The Parakeet natives are MANDATORY entries. Without them a portable
            # package ships a Models page offering a 670 MB download for an engine that then
            # fails inside native code — and, worse, the ONNX notices check below keys off
            # onnxruntime.dll, so dropping BOTH the DLL and its notices would have bypassed the
            # legal gate entirely. Listing them here is what makes that impossible (Codex).
            'app/onnxruntime.dll',
            'app/sherpa-onnx-c-api.dll'
        )
        # ONNX Runtime's own third-party notices. onnxruntime.dll ships in the
        # portable ZIP too, and its ~67 statically-linked components' licences require their
        # notices to travel with the binary — the sideload channel is a redistribution like any
        # other.
        #
        # NOT in $required as a hardcoded filename. That is what this first shipped as, with a
        # comment claiming it was "version-pinned so a package bump that leaves the old file
        # behind fails here" — it does no such thing: a bumped DLL plus a stale 1.27.0 notice
        # satisfies a name check perfectly (Codex). The version is DERIVED from the DLL in the
        # ZIP, exactly as the publish-payload gate derives it, and the content is hashed.
        $ortNoticesSha256 = '0E07B95F3A8D6230037707C5C4A2B554D12C4CB67369669AC255635528FFCEE2'
        $ortDllEntryName = 'app/onnxruntime.dll'
        # Integrity pins for the bundled Silero models (both MIT) — each must match the
        # constant the consuming code checks (VoiceActivityDetectionService.ModelSha256 and
        # SherpaVadSegmenter.ModelSha256). Both refuse a mismatching file at runtime, so a
        # corrupt ZIP entry would silently degrade every portable install rather than fail:
        # the ggml one to the RMS silence gate, the ONNX one by stranding the staged
        # batch-2 rescue candidate (the shipped default decode never reads it).
        # Hashes computed FROM THE ZIP ENTRY STREAM — the final artefact, not an
        # intermediate staging dir.
        $bundledModelPins = @(
            @{
                Entry  = 'app/Assets/Models/ggml-silero-v6.2.0.bin'
                Sha256 = '2AA269B785EEB53A82983A20501DDF7C1D9C48E33AB63A41391AC6C9F7FB6987'
                Size   = 885098
            },
            @{
                Entry  = 'app/Assets/Models/silero_vad.onnx'
                Sha256 = '9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6'
                Size   = 643854
            }
        )
        $zip = [System.IO.Compression.ZipFile]::OpenRead($PortableZip)
        try {
            # Normalize entry separators — Compress to forward slashes so an
            # `app\VoiceWink.dll` ZIP layout still matches `app/VoiceWink.dll`.
            $entryNames = $zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
            $missing = @($required | Where-Object { $entryNames -notcontains $_ })
            if ($missing.Count -gt 0) {
                foreach ($m in $missing) {
                    $failures.Add("Portable ZIP missing required entry: $m (expected at ZIP root or under app/)")
                }
            } else {
                Write-Host "      OK — all required entries present ($($required -join ', '))." -ForegroundColor Green
            }

            foreach ($pin in $bundledModelPins) {
                $modelEntry = $zip.Entries | Where-Object { ($_.FullName -replace '\\', '/') -eq $pin.Entry } | Select-Object -First 1
                if ($null -eq $modelEntry) {
                    # The required-entry list above already fails for a missing model; this
                    # stays quiet rather than double-reporting the same absence.
                    continue
                }

                if ($modelEntry.Length -ne $pin.Size) {
                    $failures.Add("Portable ZIP entry $($pin.Entry) size $($modelEntry.Length) != pinned $($pin.Size) bytes.")
                    continue
                }

                $sha = [System.Security.Cryptography.SHA256]::Create()
                $entryStream = $modelEntry.Open()
                try {
                    $hashHex = [System.BitConverter]::ToString($sha.ComputeHash($entryStream)) -replace '-', ''
                } finally {
                    $entryStream.Dispose()
                    $sha.Dispose()
                }

                if ($hashHex -ne $pin.Sha256) {
                    $failures.Add("Portable ZIP entry $($pin.Entry) SHA256 mismatch. Expected $($pin.Sha256), got $hashHex.")
                } else {
                    Write-Host "      OK — $($pin.Entry) ZIP entry size + SHA256 pin verified." -ForegroundColor Green
                }
            }

            # ONNX Runtime notices, version-coupled to the DLL in this very ZIP.
            #
            # The version cannot be read from a ZIP entry's metadata, so the DLL is extracted to a
            # temp file to read its ProductVersion — the same source of truth the publish gate uses.
            # Deriving it from the artefact rather than hardcoding it is the whole point: a package
            # bump that forgets the notices then FAILS instead of passing a stale-name check.
            $ortDllEntry = $zip.Entries | Where-Object { ($_.FullName -replace '\\', '/') -eq $ortDllEntryName } | Select-Object -First 1
            if ($null -eq $ortDllEntry) {
                # FAILS, does not skip. A skip here was fail-OPEN in the worst possible shape: this
                # whole legal check keys off the DLL, so a ZIP missing both the DLL and its notices
                # would have printed a friendly grey "skipping" line and passed (Codex). The
                # required-entry list above already fails for the same file; this is the second
                # lock, because the two can drift.
                $failures.Add("Portable ZIP has no $ortDllEntryName — the Parakeet engine cannot run, and its third-party notices cannot be verified. A missing native must FAIL this gate, never skip it.")
            } else {
                $ortTemp = Join-Path ([System.IO.Path]::GetTempPath()) "vw-ort-$([guid]::NewGuid().ToString('N')).dll"
                try {
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($ortDllEntry, $ortTemp, $true)
                    $ortVer = (Get-Item -LiteralPath $ortTemp).VersionInfo.ProductVersion
                    if ([string]::IsNullOrWhiteSpace($ortVer) -or $ortVer -notmatch '^[0-9A-Za-z.\-+]{1,64}$') {
                        $failures.Add("Portable ZIP onnxruntime.dll reports an unusable ProductVersion ('$ortVer') — cannot verify its third-party notices.")
                    } else {
                        # Same normalization as check-publish-payload.ps1: build metadata after '+'
                        # names the same upstream release, so it is dropped before composing the
                        # filename. The two scripts must agree or one of them starts failing on a
                        # build the other accepts.
                        $ortVer = ($ortVer -split '\+', 2)[0]
                        $ortEntryName = "app/licenses/onnxruntime-$ortVer-ThirdPartyNotices.txt"
                        $ortEntry = $zip.Entries | Where-Object { ($_.FullName -replace '\\', '/') -eq $ortEntryName } | Select-Object -First 1
                        if ($null -eq $ortEntry) {
                            $failures.Add("Portable ZIP missing ONNX Runtime third-party notices: $ortEntryName. The bundled onnxruntime.dll is version $ortVer; its ~67 statically-linked components' licences require their notices to accompany the binary.")
                        } else {
                            $ortSha = [System.Security.Cryptography.SHA256]::Create()
                            $ortStream = $ortEntry.Open()
                            try {
                                $ortHash = [System.BitConverter]::ToString($ortSha.ComputeHash($ortStream)) -replace '-', ''
                            } finally {
                                $ortStream.Dispose()
                                $ortSha.Dispose()
                            }
                            if ($ortHash -ne $ortNoticesSha256) {
                                $failures.Add("Portable ZIP ONNX notices SHA256 mismatch for $ortEntryName. Expected $ortNoticesSha256, got $ortHash. If deliberate, re-pin it here AND in check-publish-payload.ps1.")
                            } else {
                                Write-Host "      OK — ONNX Runtime $ortVer notices present in ZIP, content verified." -ForegroundColor Green
                            }
                        }
                    }
                } finally {
                    if (Test-Path -LiteralPath $ortTemp) { Remove-Item -LiteralPath $ortTemp -Force -ErrorAction SilentlyContinue }
                }
            }
        } finally {
            $zip.Dispose()
        }
    }
}

# --- Stable-channel privacy-version gate --------------------------------------
# Fail-closed enforcement of the retention-disclosure public-launch gates (Codex diff
# review 2026-07-23). Originally written while the bundled privacy was the deliberately
# hash-frozen v4, which predated two retention behaviors (failed-transcription and
# no-speech-blocked retention); the lawyer-reviewed v5 carrying those disclosures was
# promoted 2026-08-19, so this gate passes today and its role going forward is a
# REGRESSION guard: a PUBLIC (win-x64-stable) release must never ship with a bundled
# privacy policy below v5 — tester-ring channels are unaffected.
if (Test-IsPublicReleaseChannel -Channel $Channel) {
    Write-Host "[stable-gate] Public-channel privacy-version check" -ForegroundColor Yellow
    $privacyVersion = 0
    if ($privacyFile -and (Test-Path -LiteralPath $privacyFile)) {
        $m = [regex]::Match((Split-Path $privacyFile -Leaf), 'privacy-v(\d+)\.md')
        if ($m.Success) { $privacyVersion = [int]$m.Groups[1].Value }
    }
    if ($privacyVersion -lt 5) {
        # Message kept free of private-tree paths, policy-content specifics and internal
        # tracker ids: this script ships in the public source snapshot, so its failure
        # text is world-readable. The maintainer detail lives in the legal addenda's
        # retention items.
        $failures.Add("PUBLIC RELEASE BLOCKED: bundled privacy policy is v$privacyVersion, but the $Channel channel requires v5+. The lawyer-reviewed v5 was promoted 2026-08-19 — a lower version here means the bundle has REGRESSED; restore the v5+ assets before shipping.")
    } else {
        Write-Host "      OK — privacy v$privacyVersion meets the public-channel minimum (v5)." -ForegroundColor Green
    }

    # --- Auto-install disclosure ---------------------------------------------
    # The version check above CANNOT see this. The queued addenda item folds the automatic-install
    # wording INTO the same v5 the retention items require, so "v5 exists" is true whether or not anyone
    # wrote the sentence — and the app now installs updates by itself, ON BY DEFAULT. Both
    # documents are gated independently because the queued wording lands in BOTH (privacy §4/§4.4 and
    # EULA §12.3), and a fold that touched only one is precisely the plausible mistake.
    #
    # A tripwire against forgetting, not a legal adequacy review — see Test-AutoUpdateDisclosure
    # for its stated limits. It fails LOUDLY if counsel rewords past the anchors, which is the
    # correct direction: a human re-reads the text and updates the anchor.
    Write-Host "[stable-gate] Public-channel auto-install disclosure check" -ForegroundColor Yellow
    $eulaFile = Resolve-HighestLegalVersionFile -Directory $legalDir -Stem 'eula'
    foreach ($doc in @(
        @{ Label = 'privacy policy'; Path = $privacyFile; Section = 'privacy §4 / §4.4' },
        @{ Label = 'EULA';           Path = $eulaFile;    Section = 'EULA §12.3' }
    )) {
        if (-not $doc.Path -or -not (Test-Path -LiteralPath $doc.Path)) {
            # Unreadable is a finding, never a pass: "cannot tell" must not be spelled the same
            # as "yes" on a gate that authorises shipping.
            $failures.Add("PUBLIC RELEASE BLOCKED: could not read the bundled $($doc.Label) to verify the automatic-install disclosure.")
            continue
        }
        $verdict = Test-AutoUpdateDisclosure -Text (Get-Content -LiteralPath $doc.Path -Raw)
        if (-not $verdict.Ok) {
            $missing = @()
            if (-not $verdict.HasDisclosure) { $missing += 'that updates can be installed automatically' }
            if (-not $verdict.HasOptOut)     { $missing += 'that automatic installation can be turned off' }
            $failures.Add("PUBLIC RELEASE BLOCKED: the bundled $($doc.Label) ($(Split-Path $doc.Path -Leaf)) does not state $($missing -join ', and '). VoiceWink installs updates automatically by default, so this must be disclosed before a public release — fold the queued auto-install wording for $($doc.Section) from the legal addenda. If the text DOES cover it in different words, update the anchors in scripts/lib/legal-disclosure.ps1 and its case table.")
        } else {
            Write-Host "      OK — $($doc.Label) discloses automatic installation and its opt-out." -ForegroundColor Green
        }
    }
}

# --- Verdict ----------------------------------------------------------------
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "All release-asset checks passed." -ForegroundColor Green
    exit 0
}

Write-Host "Release-asset validation FAILED:" -ForegroundColor Red
foreach ($msg in $failures) {
    Write-Host "  - $msg" -ForegroundColor Red
}
exit 1

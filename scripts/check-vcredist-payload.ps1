# check-vcredist-payload.ps1 - fail-closed gate proving the four app-local
# Microsoft Visual C++ runtime DLLs ship beside the x64 whisper.dll.
#
# WHY THIS EXISTS
# ---------------
# The native Whisper DLLs import the MSVC runtime (vcruntime140 / vcruntime140_1
# / msvcp140 / vcomp140). Without those four next to whisper.dll, a clean Win 11
# VM (no VC++ Redistributable) fails the native load with PInvokeError 0 and the
# hotkey silently does nothing. The DLLs are committed (installer/runtime/vcredist/)
# and copied into the publish output by VoiceWink.csproj <Content><Link>. This
# gate is the fail-closed proof that the SHIPPED artifact actually contains them
# - defends against someone breaking the csproj copy rule or relocating the DLLs.
#
# WHY A STANDALONE SCRIPT (not folded into check-publish-payload.ps1)
# ------------------------------------------------------------------
# Two ship paths need this exact check but NOT the rest of check-publish-payload:
#   - the self-contained Velopack publish (check-publish-payload.ps1 calls this),
#   - the framework-dependent portable ZIP (build-portable.sh calls this against
#     the staged app dir). The portable build has neither the self-contained
#     payload (hostfxr/coreclr) nor WindowsAppRuntimeInstall-x64.exe, so it can't
#     run the full check-publish-payload gate. Sharing this one script keeps the
#     VC++ verification identical for both without duplicating logic.
#
# CHECK (deterministic x64 selection, per 50-decision.md; two-runtime split since the
# GPU (Vulkan) whisper runtime shipped, 2026-09-01):
#   1. Locate every whisper.dll whose containing directory is named 'win-x64' and
#      PARTITION on the parent directory: parent 'vulkan' = the GPU runtime
#      (runtimes\vulkan\win-x64\), anything else = the CPU runtime
#      (runtimes\win-x64\). Require EXACTLY ONE of EACH (fail on zero /
#      ambiguous - the output tree also carries win-x86 / win-arm64 copies).
#      Verify each is PE machine x64.
#   2. Require the four VC++ DLLs as siblings in BOTH directories (a spawned/
#      loaded native resolves imports from its own directory first, and the
#      Vulkan copy is the FIRST one the [Vulkan, Cpu] pin loads).
#   3. Verify each against installer/runtime/vcredist/MANIFEST.psd1 (parsed
#      data-only via Import-PowerShellDataFile): SHA256 (strong identity anchor),
#      PE machine, and Authenticode signer Organization.
#   4. parakeet-server.exe beside the CPU whisper.dll (CPU dir only); 4b the Vulkan loader there.
#   4c. runtimes\win-x64\llama\: EXACTLY the llama-server manifest's files + the loader
#      copy + its VC++ copies (no extra PE, none missing), each file's SHA256 pin, x64, unsigned.
#   4d. runtimes\win-arm64\llama\: the same exact-set rule for the native ARM64 llama-server
#      (installer/runtime/llama-arm64/MANIFEST.psd1: ARM64, unsigned, no loader copy) and its
#      two ARM64 VC++ copies (installer/runtime/vcredist-arm64/MANIFEST.psd1).
#   5. ggml-vulkan-whisper.dll beside the Vulkan whisper.dll (the backend itself).
#
# Exit codes: 0 = pass - 1 = at least one check failed - 2 = invalid arguments.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Dir,

    # Path to the tracked manifest. Defaults to the repo's committed pin.
    [string]$ManifestPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Argument / manifest-integrity failures exit 2. NOTE: Write-Error throws under
# ErrorActionPreference=Stop, so a bare `Write-Error; exit 2` never reaches the
# exit (the process dies with code 1 + an ugly terminating-error blob). Emit the
# message to host + stderr and exit 2 explicitly instead.
function Fail-Arg([string]$msg) {
    Write-Host "[ERROR] $msg" -ForegroundColor Red
    [Console]::Error.WriteLine($msg)
    exit 2
}

# All path tests use -LiteralPath so a $Dir containing PS wildcard metachars
# ([ ] * ?) is treated literally (mirrors check-publish-payload.ps1).
if (-not (Test-Path -LiteralPath $Dir -PathType Container)) {
    Fail-Arg "Dir not found: $Dir"
}
$root = (Resolve-Path -LiteralPath $Dir).Path

if (-not $ManifestPath) {
    $ManifestPath = Join-Path (Split-Path $PSScriptRoot -Parent) "installer\runtime\vcredist\MANIFEST.psd1"
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    Fail-Arg "VC++ manifest not found: $ManifestPath"
}

# Parse the manifest DATA-ONLY via the AST + HashtableAst.SafeGetValue(), NOT
# Import-PowerShellDataFile. Two reasons: (1) it lets us enforce that the .psd1 is
# EXACTLY one top-level hashtable expression and nothing else (rejecting a tampered
# manifest with trailing statements - Import-PowerShellDataFile can't express that);
# (2) it drops a dependency on Import-PowerShellDataFile, a module-autoload cmdlet.
# SafeGetValue evaluates ONLY constant literals/arrays/hashtables and THROWS on any
# command/variable/expression, so the manifest is never executed as code.
# NOTE: this gate is still invoked via pwsh (by check-publish-payload.ps1 and
# build-portable.sh), NOT the bare Windows PowerShell 5.1 that Git Bash resolves -
# it also calls Get-FileHash / Get-AuthenticodeSignature, which that host's mangled
# PSModulePath can't auto-load. ASCII-only source keeps it parseable everywhere.
# The AST-only manifest reader is SHARED with the post-pack signing gate since 2026-09-02
# (scripts/lib/Import-Psd1Safe.ps1): both gates read the same vendored-payload manifests and must
# parse them identically.
. (Join-Path $PSScriptRoot 'lib\Import-Psd1Safe.ps1')

try {
    $manifest = Import-Psd1Safe $ManifestPath
}
catch {
    Fail-Arg "Failed to parse VC++ manifest ${ManifestPath}: $($_.Exception.Message)"
}
foreach ($key in 'SignerOrg', 'Machine', 'Dlls') {
    if (-not $manifest.ContainsKey($key)) {
        Fail-Arg "VC++ manifest is missing required key '$key': $ManifestPath"
    }
}
$expectedMachine = [string]$manifest.Machine
$expectedSigner = [string]$manifest.SignerOrg

# Independently require the EXACT four-DLL set, not whatever the manifest happens to
# list. Without this, a future manifest edit that dropped (say) vcomp140.dll would
# silently stop the gate requiring it, and a clean-VM build could ship missing the
# CPU-OpenMP runtime. The manifest's Dlls names must match this set exactly - no
# missing, no extras, count 4 - before we trust its per-DLL SHA256 pins.
$RequiredDlls = @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'vcomp140.dll')
$manifestNames = @($manifest.Dlls | ForEach-Object { [string]$_.Name })
$missingFromManifest = @($RequiredDlls | Where-Object { $_ -notin $manifestNames })
$extraInManifest = @($manifestNames | Where-Object { $_ -notin $RequiredDlls })
if ($manifestNames.Count -ne $RequiredDlls.Count -or $missingFromManifest.Count -gt 0 -or $extraInManifest.Count -gt 0) {
    $missDisp = if ($missingFromManifest.Count -gt 0) { $missingFromManifest -join ', ' } else { '<none>' }
    $extraDisp = if ($extraInManifest.Count -gt 0) { $extraInManifest -join ', ' } else { '<none>' }
    Fail-Arg ("VC++ manifest DLL set mismatch. Required exactly: " + ($RequiredDlls -join ', ') +
        ". Manifest lists: " + ($manifestNames -join ', ') +
        ". (missing: $missDisp; extra: $extraDisp)")
}

$failures = New-Object System.Collections.Generic.List[string]
function Add-Failure([string]$msg) { $failures.Add($msg) }

# Read the PE COFF machine field, formatted '0x{0:X4}' (e.g. 0x8664 for x64).
function Get-PeMachine([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Seek(0x3C, 'Begin') | Out-Null
        $peOff = $br.ReadInt32()
        # Validate the PE signature ("PE\0\0") before trusting the offset.
        $fs.Seek($peOff, 'Begin') | Out-Null
        $sig = $br.ReadUInt32()
        if ($sig -ne 0x00004550) { return $null }
        $machine = $br.ReadUInt16()
        return ('0x{0:X4}' -f $machine)
    }
    finally { $fs.Close() }
}

# ---------------------------------------------------------------------------
# Steps 2 + 3, as a function - the four VC++ DLLs present as siblings + verified
# ---------------------------------------------------------------------------
# The GPU (Vulkan) runtime made this a function because TWO directories now need the identical
# verification (the CPU and Vulkan whisper.dll homes). Iterates the hardcoded
# $RequiredDlls (not $manifest.Dlls) so all four are ALWAYS required regardless
# of manifest contents; the set-equality check above already guaranteed the
# manifest pins exactly these four. $label only decorates messages.
# $names narrows the set for a directory whose natives import fewer of the four (the
# llama-server directory imports three - libomp replaces vcomp140). It may only NARROW:
# a name outside $RequiredDlls has no manifest pin to verify against, so it is refused.
# $vcManifest selects WHICH vcredist manifest the directory is verified against: the x64 one
# (the default) or the ARM64 one for the native ARM64 llama-server directory - same-named
# DLLs, different bytes and PE machine, so a directory is only ever checked against its own.
function Test-VcSiblingSet([string]$dirPath, [string]$label, [string[]]$names = $RequiredDlls, $vcManifest = $manifest, [string]$sourceDir = 'vcredist') {
    $pinned = @($vcManifest.Dlls | ForEach-Object { [string]$_.Name })
    $expectedMachine = [string]$vcManifest.Machine
    $expectedSigner = [string]$vcManifest.SignerOrg
    $unknown = @($names | Where-Object { $_ -notin $pinned })
    if ($names.Count -eq 0 -or $unknown.Count -gt 0) {
        Add-Failure "VC++ sibling set for the $label directory names DLLs the vcredist manifest does not pin: $($unknown -join ', ') (or is empty)."
        return
    }
    foreach ($name in $names) {
        $entry = $vcManifest.Dlls | Where-Object { [string]$_.Name -eq $name } | Select-Object -First 1
        $sib = Join-Path $dirPath $name
        if (-not (Test-Path -LiteralPath $sib -PathType Leaf)) {
            Add-Failure "$name NOT FOUND in the $label native directory at $sib. The app-local VC++ runtime is missing - on a clean VM the natives there fail to load (PInvokeError 0 for Whisper; a spawned child dies before main). Confirm the csproj <Content Include='..\..\installer\runtime\$sourceDir\$name'> rule targeting this directory fires and the file exists in installer/runtime/$sourceDir/."
            continue
        }

        # SHA256 - the strong identity anchor.
        $actualSha = (Get-FileHash -LiteralPath $sib -Algorithm SHA256).Hash
        $expectedSha = ([string]$entry.Sha256).ToUpperInvariant()
        if ($actualSha.ToUpperInvariant() -ne $expectedSha) {
            Add-Failure "$name ($label) SHA256 mismatch. Expected $expectedSha, got $actualSha. The shipped DLL is not the manifest-pinned VC++ runtime ($($vcManifest.RedistVersion))."
            continue
        }

        # PE machine.
        $sibMachine = Get-PeMachine $sib
        if ($sibMachine -ne $expectedMachine) {
            Add-Failure "$name ($label) PE machine is '$sibMachine', expected '$expectedMachine'."
            continue
        }

        # Authenticode signer Organization (per-DLL CN differs; O= is stable).
        $sig = Get-AuthenticodeSignature -LiteralPath $sib
        if ($sig.Status -ne 'Valid') {
            Add-Failure "$name ($label) Authenticode status is '$($sig.Status)', expected 'Valid'."
            continue
        }
        if ($null -eq $sig.SignerCertificate) {
            Add-Failure "$name ($label) has no signer certificate."
            continue
        }
        if ($sig.SignerCertificate.Subject -notmatch [regex]::Escape($expectedSigner)) {
            Add-Failure "$name ($label) signer subject '$($sig.SignerCertificate.Subject)' does not contain '$expectedSigner'."
            continue
        }

        Write-Host "[OK]  $name ($label) verified (SHA256 + $expectedMachine + $expectedSigner)" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# The bundled Khronos Vulkan loader in one native directory (step 4b's check, a function
# since the llama-server payload because the llama-server directory carries a second copy of the same pinned
# bytes). Verified against installer/runtime/vulkan-loader/MANIFEST.psd1: SHA256, PE machine,
# and the LunarG Authenticode signer.
#
# PRE-PACK, like step 4 - but for a DIFFERENT reason than the server row (measured on the
# 2026-09-02 release run 33588250383): vpk signs an UNSIGNED PE (the server's post-pack SHA differs),
# and leaves a VALIDLY SIGNED PE byte-identical - so for this loader the pre- and post-pack
# identity coincide, and the post-pack signing gate (check-pack-signatures) asserts the SAME
# manifest SignerCommonName AND the SAME Sha256 pin against the extracted pack. One manifest
# feeds both gates. $missingConsequence finishes the failure sentence for the directory's owner.
# ---------------------------------------------------------------------------
function Test-VulkanLoaderIn([string]$dirPath, [string]$label, [string]$missingConsequence) {
    $loaderManifestPath = Join-Path (Split-Path $PSScriptRoot -Parent) "installer\runtime\vulkan-loader\MANIFEST.psd1"
    if (-not (Test-Path -LiteralPath $loaderManifestPath -PathType Leaf)) {
        Add-Failure "Vulkan loader manifest not found: $loaderManifestPath. Cannot verify the bundled vulkan-1.dll ($label)."
        return
    }
    try {
        $loaderManifest = Import-Psd1Safe $loaderManifestPath
        # Required keys fail CLOSED (the vcredist manifest's loop at the top of this
        # script): without this, a refreshed manifest that dropped SignerOrg would make
        # the signer regex '' - and `-notmatch ''` is always false, passing ANY validly
        # signed DLL (self-review, payload lens).
        foreach ($loaderKey in 'LoaderVersion', 'SignerOrg', 'Machine', 'Dlls') {
            if (-not $loaderManifest.ContainsKey($loaderKey) -or
                ([string]::IsNullOrWhiteSpace([string]$loaderManifest[$loaderKey]) -and $loaderKey -ne 'Dlls')) {
                throw "manifest is missing required key '$loaderKey' (or it is empty)"
            }
        }
        $loaderEntry = $loaderManifest.Dlls | Where-Object { [string]$_.Name -eq 'vulkan-1.dll' } | Select-Object -First 1
        if ($null -eq $loaderEntry) { throw "manifest lists no vulkan-1.dll entry" }
    }
    catch {
        Add-Failure "Failed to parse Vulkan loader manifest ${loaderManifestPath}: $($_.Exception.Message)"
        return
    }
    $loaderPath = Join-Path $dirPath 'vulkan-1.dll'
    if (-not (Test-Path -LiteralPath $loaderPath -PathType Leaf)) {
        Add-Failure "vulkan-1.dll NOT FOUND in the $label directory at $loaderPath - $missingConsequence. Confirm the csproj <Content Include='..\..\installer\runtime\vulkan-loader\vulkan-1.dll'> row targeting this directory fires."
        return
    }
    $loaderSha = (Get-FileHash -LiteralPath $loaderPath -Algorithm SHA256).Hash
    if ($loaderSha.ToUpperInvariant() -ne ([string]$loaderEntry.Sha256).ToUpperInvariant()) {
        Add-Failure "vulkan-1.dll ($label) SHA256 mismatch. Expected $($loaderEntry.Sha256), got $loaderSha. The shipped loader is not the manifest-pinned LunarG runtime ($($loaderManifest.LoaderVersion))."
        return
    }
    if ((Get-PeMachine $loaderPath) -ne [string]$loaderManifest.Machine) {
        Add-Failure "vulkan-1.dll ($label) PE machine is not $($loaderManifest.Machine) (x64)."
        return
    }
    $loaderSig = Get-AuthenticodeSignature -LiteralPath $loaderPath
    if ($loaderSig.Status -ne 'Valid' -or $null -eq $loaderSig.SignerCertificate -or
        $loaderSig.SignerCertificate.Subject -notmatch [regex]::Escape([string]$loaderManifest.SignerOrg)) {
        Add-Failure "vulkan-1.dll ($label) Authenticode check failed (status '$($loaderSig.Status)'; expected signer org '$($loaderManifest.SignerOrg)')."
        return
    }
    Write-Host "[OK]  vulkan-1.dll ($label) verified (SHA256 + x64 + $($loaderManifest.SignerOrg))" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Step 4c as a function - the llama-server directory. See the call site for why.
# The manifest is validated fail-CLOSED before anything is compared against it: an empty or
# malformed Files list would otherwise make "every listed file matched" vacuously true.
# ---------------------------------------------------------------------------
# $sourceName is the directory under installer\runtime\ that holds the payload and its manifest
# ('llama' = x64, 'llama-arm64' = the native ARM64 build); $vcSourceName the vcredist directory
# its VC++ copies come from. The manifest says whether the directory carries a Vulkan loader
# copy (HasVulkanLoader; an absent key means it does - the x64 manifest predates the key).
function Test-LlamaServerPayload([string]$llamaDir, [string]$sourceName = 'llama', [string]$vcSourceName = 'vcredist', [string]$label = 'llama-server') {
    $llamaManifestPath = Join-Path (Split-Path $PSScriptRoot -Parent) "installer\runtime\$sourceName\MANIFEST.psd1"
    $vcManifest = $manifest
    try {
        if (-not (Test-Path -LiteralPath $llamaManifestPath -PathType Leaf)) { throw "not found" }
        $llama = Import-Psd1Safe $llamaManifestPath
        foreach ($key in 'Build', 'Machine', 'ExeName', 'Files', 'VcRuntimeDlls') {
            if (-not $llama.ContainsKey($key)) { throw "missing required key '$key'" }
        }
        foreach ($key in 'Build', 'Machine', 'ExeName') {
            if ([string]::IsNullOrWhiteSpace([string]$llama[$key])) { throw "required key '$key' is empty" }
        }
        $files = @($llama.Files)
        if ($files.Count -eq 0) { throw "Files is empty" }
        $fileNames = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
        foreach ($f in $files) {
            $n = [string]$f.Name
            if ([string]::IsNullOrWhiteSpace($n) -or $n -notmatch '^[A-Za-z0-9._-]+\.(dll|exe)$') { throw "a Files entry has an invalid Name '$n'" }
            if ([string]$f.Sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Files entry '$n' has no 64-hex Sha256" }
            if (-not $fileNames.Add($n)) { throw "Files lists '$n' twice" }
        }
        if (-not $fileNames.Contains([string]$llama.ExeName)) { throw "ExeName '$($llama.ExeName)' is not in Files" }
        $vcNames = @($llama.VcRuntimeDlls | ForEach-Object { [string]$_ })
        if ($vcNames.Count -eq 0) { throw "VcRuntimeDlls is empty" }
        $hasLoader = $true
        if ($llama.ContainsKey('HasVulkanLoader')) {
            if ($llama.HasVulkanLoader -isnot [bool]) { throw "HasVulkanLoader must be `$true or `$false" }
            $hasLoader = [bool]$llama.HasVulkanLoader
        }
        if ($vcSourceName -ne 'vcredist') {
            # A payload with its own vcredist manifest: validated as strictly as the x64 one is at
            # the top of this script, and its DLL set must be EXACTLY what the payload names.
            $vcManifestPath = Join-Path (Split-Path $PSScriptRoot -Parent) "installer\runtime\$vcSourceName\MANIFEST.psd1"
            if (-not (Test-Path -LiteralPath $vcManifestPath -PathType Leaf)) { throw "its VC++ manifest $vcManifestPath was not found" }
            $vcManifest = Import-Psd1Safe $vcManifestPath
            foreach ($key in 'SignerOrg', 'Machine', 'Dlls') {
                if (-not $vcManifest.ContainsKey($key)) { throw "its VC++ manifest is missing required key '$key'" }
            }
            $vcPinned = @($vcManifest.Dlls | ForEach-Object { [string]$_.Name })
            if ($vcPinned.Count -ne $vcNames.Count -or @($vcNames | Where-Object { $_ -notin $vcPinned }).Count -gt 0) {
                throw "its VC++ manifest pins [$($vcPinned -join ', ')] but VcRuntimeDlls names [$($vcNames -join ', ')]"
            }
            foreach ($d in $vcManifest.Dlls) {
                if ([string]$d.Sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "its VC++ manifest entry '$($d.Name)' has no 64-hex Sha256" }
            }
            if ([string]$vcManifest.Machine -ne [string]$llama.Machine) {
                throw "its VC++ manifest is for machine $($vcManifest.Machine) but the payload is $($llama.Machine)"
            }
        }
    }
    catch {
        Add-Failure "$label manifest ${llamaManifestPath} is unusable: $($_.Exception.Message). Cannot verify the bundled $label payload."
        return
    }

    if (-not (Test-Path -LiteralPath $llamaDir -PathType Container)) {
        Add-Failure "$label directory NOT FOUND at $llamaDir - the local AI engine would be missing from every install that needs it. Confirm the csproj <Content Include='..\..\installer\runtime\$sourceName\*.dll;...'> rows fire."
        return
    }

    # The exact PE set: the manifest's files + the loader copy + the VC++ copies.
    $expected = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
    foreach ($n in $fileNames) { [void]$expected.Add($n) }
    if ($hasLoader) { [void]$expected.Add('vulkan-1.dll') }
    foreach ($n in $vcNames) { [void]$expected.Add($n) }
    $actual = @(Get-ChildItem -LiteralPath $llamaDir -File -Force |
        Where-Object { $_.Extension -in '.dll', '.exe' } | ForEach-Object { $_.Name })
    $extra = @($actual | Where-Object { -not $expected.Contains($_) })
    $missing = @($expected | Where-Object { $_ -notin $actual })
    if ($extra.Count -gt 0) {
        Add-Failure "$label directory carries PE files the manifest does not pin: $($extra -join ', '). ggml's backend loader maps any ggml-<name>*.dll in the exe directory, so an unpinned PE there is unreviewed code in every install."
    }
    if ($missing.Count -gt 0) {
        Add-Failure "$label directory is missing: $($missing -join ', ') (at $llamaDir)."
    }

    $expectedMachine = [string]$llama.Machine
    $ok = 0
    foreach ($f in $files) {
        $path = Join-Path $llamaDir ([string]$f.Name)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue } # reported as missing above
        $sha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($sha.ToUpperInvariant() -ne ([string]$f.Sha256).ToUpperInvariant()) {
            Add-Failure "$($f.Name) ($label) SHA256 mismatch. Expected $($f.Sha256), got $sha. The shipped file is not the manifest-pinned llama.cpp $($llama.Build) binary."
            continue
        }
        if ((Get-PeMachine $path) -ne $expectedMachine) {
            Add-Failure "$($f.Name) ($label) PE machine is not $expectedMachine."
            continue
        }
        $sig = Get-AuthenticodeSignature -LiteralPath $path
        if ($sig.Status -ne 'NotSigned') {
            Add-Failure "$($f.Name) ($label) Authenticode status is '$($sig.Status)', expected 'NotSigned' pre-pack: the vendored upstream bytes are unsigned and vpk signs them at pack time."
            continue
        }
        $ok++
    }
    if ($ok -eq $files.Count) {
        Write-Host "[OK]  $label payload verified ($ok files: SHA256 pins + $expectedMachine + unsigned upstream, llama.cpp $($llama.Build))" -ForegroundColor Green
    }

    Test-VcSiblingSet -dirPath $llamaDir -label $label -names $vcNames -vcManifest $vcManifest -sourceDir $vcSourceName
    if ($hasLoader) {
        Test-VulkanLoaderIn -dirPath $llamaDir -label $label -missingConsequence "ggml-vulkan.dll imports it, so on a machine without a driver-installed loader the GPU backend cannot load and the engine silently runs on the CPU"
    }
}

# ---------------------------------------------------------------------------
# Step 1 - locate the whisper.dll ship directories
# ---------------------------------------------------------------------------
# Since the GPU (Vulkan) runtime shipped: TWO native whisper.cpp runtimes, in two directories that BOTH end
# in 'win-x64' - the CPU package at runtimes\win-x64\ and the Vulkan package at
# runtimes\vulkan\win-x64\ (same-named DLLs, different builds; the separation is
# load-bearing). Partition on the PARENT directory name and require EXACTLY ONE
# of each: the CPU rule keeps its original guarantee (the output tree also
# carries win-x86 / win-arm64 copies), and the Vulkan rule is fail-closed the
# same way - a payload without it means the [Vulkan, Cpu] runtime pin falls
# through to CPU on every machine and GPU acceleration silently never engages.
$allWinX64 = @(
    Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'whisper.dll' -ErrorAction SilentlyContinue |
        Where-Object { (Split-Path $_.DirectoryName -Leaf) -eq 'win-x64' }
)
$whisperMatches = @($allWinX64 | Where-Object { (Split-Path (Split-Path $_.DirectoryName -Parent) -Leaf) -ne 'vulkan' })
$vulkanMatches = @($allWinX64 | Where-Object { (Split-Path (Split-Path $_.DirectoryName -Parent) -Leaf) -eq 'vulkan' })

if ($whisperMatches.Count -eq 0) {
    Add-Failure "No CPU 'win-x64\whisper.dll' found under $root. Expected the published Whisper x64 native lib at runtimes\win-x64\whisper.dll. Cannot verify VC++ siblings without it."
}
elseif ($whisperMatches.Count -gt 1) {
    $dirs = ($whisperMatches | ForEach-Object { $_.DirectoryName }) -join '; '
    Add-Failure "Ambiguous: $($whisperMatches.Count) CPU 'win-x64\whisper.dll' found ($dirs). Expected exactly one. Refusing to guess the ship directory."
}
else {
    $whisper = $whisperMatches[0]
    $x64Dir = $whisper.DirectoryName

    $whisperMachine = Get-PeMachine $whisper.FullName
    if ($whisperMachine -ne $expectedMachine) {
        Add-Failure "win-x64\whisper.dll PE machine is '$whisperMachine', expected '$expectedMachine' (x64). The dir is named win-x64 but the binary is not x64 - wrong publish layout."
    }
    else {
        Write-Host "[OK]  x64 whisper.dll located: $($whisper.FullName)" -ForegroundColor Green

        Test-VcSiblingSet -dirPath $x64Dir -label 'CPU'

        # -------------------------------------------------------------------
        # Step 4 - parakeet-server.exe beside the same DLLs
        # -------------------------------------------------------------------
        # The resident parakeet.cpp server ships INSIDE runtimes\win-x64\ because a spawned
        # child resolves its PE imports (MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1 / VCOMP140)
        # from ITS OWN directory - the exact DLLs this gate just verified. The csproj Content
        # row is Condition="Exists(...)" (fail-SOFT), and in a PCPP_ENABLED build a missing exe
        # degrades to sherpa-forever: a silent no-flip release. This is the fail-CLOSED proof.
        # Hash only, no Authenticode: PRE-pack the exe is the tracked upstream binary verbatim
        # (unsigned); vpk signs every payload PE at pack time, so the signed identity is
        # asserted post-pack by the signing gate (check-pack-signatures) instead - a post-sign
        # hash pin is impossible (timestamped signatures are non-deterministic).
        $pcppPinFile = Join-Path (Split-Path $PSScriptRoot -Parent) "installer\runtime\parakeet\EXPECTED_SHA256"
        if (-not (Test-Path -LiteralPath $pcppPinFile -PathType Leaf)) {
            Add-Failure "parakeet-server pin file not found: $pcppPinFile. Cannot verify the parakeet.cpp server payload."
        }
        else {
            $pcppPin = (Get-Content -LiteralPath $pcppPinFile |
                Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') } |
                Select-Object -First 1)
            $pcppPin = if ($null -ne $pcppPin) { $pcppPin.Trim() } else { '' }
            if ($pcppPin -notmatch '^[0-9a-fA-F]{64}$') {
                Add-Failure "parakeet-server pin file holds no 64-hex SHA256: $pcppPinFile"
            }
            else {
                $serverExe = Join-Path $x64Dir 'parakeet-server.exe'
                if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
                    Add-Failure "parakeet-server.exe NOT FOUND beside whisper.dll at $serverExe. The parakeet.cpp backend would silently fall back to the legacy engine in every install. Confirm the csproj <Content Include='..\..\installer\runtime\parakeet\parakeet-server.exe'> rule fires and the file exists in installer/runtime/parakeet/."
                }
                else {
                    $serverSha = (Get-FileHash -LiteralPath $serverExe -Algorithm SHA256).Hash
                    if ($serverSha.ToUpperInvariant() -ne $pcppPin.ToUpperInvariant()) {
                        Add-Failure "parakeet-server.exe SHA256 mismatch. Expected $pcppPin, got $serverSha. The staged server is not the G-gate-validated binary (evidence/artifact-pins.md)."
                    }
                    else {
                        $serverMachine = Get-PeMachine $serverExe
                        if ($serverMachine -ne $expectedMachine) {
                            Add-Failure "parakeet-server.exe PE machine is '$serverMachine', expected '$expectedMachine' (x64)."
                        }
                        else {
                            Write-Host "[OK]  parakeet-server.exe verified (SHA256 pin + x64)" -ForegroundColor Green
                        }
                    }
                }
            }
        }

        # -------------------------------------------------------------------
        # Step 4b - the Khronos Vulkan loader beside parakeet-server.exe (GPU acceleration, 2026-09-01)
        # -------------------------------------------------------------------
        # The server statically imports vulkan-1.dll (first import-table entry, delay-load
        # directory empty). Without this bundled copy, every machine with no GPU driver kills
        # the child BEFORE main on every spawn, the storm fuse trips, and Parakeet - the
        # DEFAULT engine - is unrunnable for that user class. The checks and the pre-pack
        # rationale are on Test-VulkanLoaderIn.
        Test-VulkanLoaderIn -dirPath $x64Dir -label 'parakeet-server' -missingConsequence "the server statically imports it, so every GPU-less machine (no driver-installed loader) kills the child before main and the storm fuse marks Parakeet unrunnable"

        # -------------------------------------------------------------------
        # Step 4c - the llama-server directory (2026-09-29)
        # -------------------------------------------------------------------
        # llama.cpp's llama-server ships in its OWN directory, runtimes\win-x64\llama\, pinned
        # file by file by installer/runtime/llama/MANIFEST.psd1. The csproj copies it with a
        # WILDCARD, so this step is what makes the set exact: ggml's backend loader maps every
        # ggml-<name>*.dll it finds in the exe directory, so an EXTRA PE there is loaded code
        # nobody pinned, and a MISSING one is a child that dies before main (or a silent CPU-only
        # fallback when it is ggml-vulkan.dll). Every manifest file must match its SHA256, be x64
        # and be UNSIGNED - pre-pack these are the upstream bytes verbatim; vpk signs them at pack
        # time and the post-pack signing gate checks them there (Kind 'Ours'). A signed source
        # would mean the vendored bytes are not what the manifest describes.
        Test-LlamaServerPayload -llamaDir (Join-Path $x64Dir 'llama')

        # -------------------------------------------------------------------
        # Step 4d - the native ARM64 llama-server directory (2026-09-30)
        # -------------------------------------------------------------------
        # On an ARM64 PC the x64 app starts the NATIVE ARM64 build of the same llama.cpp release
        # from runtimes\win-arm64\llama\ (installer/runtime/llama-arm64/MANIFEST.psd1). The same
        # exact-set rule as 4c, against that payload's own manifest and the ARM64 vcredist
        # manifest: a directory is only ever verified against its OWN pins, so an x64 file under
        # an ARM64 name (or the reverse) fails on hash and on PE machine. Required in every
        # publish - without it an ARM64 PC has no local AI engine at all (the app never falls
        # back to the emulated x64 one).
        Test-LlamaServerPayload -llamaDir (Join-Path (Split-Path $x64Dir -Parent) 'win-arm64\llama') `
            -sourceName 'llama-arm64' -vcSourceName 'vcredist-arm64' -label 'llama-server (ARM64)'
    }
}

# ---------------------------------------------------------------------------
# Step 5 - the Vulkan whisper.dll directory, same VC++ verification
# ---------------------------------------------------------------------------
if ($vulkanMatches.Count -eq 0) {
    Add-Failure "No 'runtimes\vulkan\win-x64\whisper.dll' found under $root. The GPU (Vulkan) whisper runtime is missing from the payload - the [Vulkan, Cpu] load-order pin falls through to CPU on every machine and GPU acceleration silently never engages. Verify the Whisper.net.Runtime.Vulkan package reference and its copy items fired."
}
elseif ($vulkanMatches.Count -gt 1) {
    $vDirs = ($vulkanMatches | ForEach-Object { $_.DirectoryName }) -join '; '
    Add-Failure "Ambiguous: $($vulkanMatches.Count) 'vulkan\win-x64\whisper.dll' found ($vDirs). Expected exactly one. Refusing to guess the Vulkan ship directory."
}
else {
    $vulkanWhisper = $vulkanMatches[0]
    $vulkanDir = $vulkanWhisper.DirectoryName

    $vulkanMachine = Get-PeMachine $vulkanWhisper.FullName
    if ($vulkanMachine -ne $expectedMachine) {
        Add-Failure "vulkan\win-x64\whisper.dll PE machine is '$vulkanMachine', expected '$expectedMachine' (x64). The dir is named win-x64 but the binary is not x64 - wrong publish layout."
    }
    else {
        Write-Host "[OK]  Vulkan whisper.dll located: $($vulkanWhisper.FullName)" -ForegroundColor Green

        # The Vulkan copy is the FIRST whisper.dll the process loads (the [Vulkan, Cpu]
        # runtime pin), and Win32 resolves a loaded DLL's imports from ITS OWN directory
        # first - so the four VC++ DLLs must sit HERE too, or a clean VM fails the
        # Vulkan load (PInvokeError 0) and falls through to CPU: acceleration silently
        # missing for exactly the population this gate exists for. parakeet-server.exe
        # stays CPU-dir-only (step 4) - nothing spawns from this directory.
        Test-VcSiblingSet -dirPath $vulkanDir -label 'Vulkan'

        # The app's pre-flight (WhisperBackendLog.MissingVulkanFiles) requires the COMPLETE
        # five-DLL whisper.cpp chain before it will pin the GPU order - so the payload gate
        # must require the same five, or a package missing any of the other three passes both
        # gates and ships 58 MB of GPU payload the app then declines at startup: CPU-only for
        # every user, silently, despite this gate's fail-closed contract (codex diff review,
        # owner-requested round, 2026-09-01).
        foreach ($native in 'ggml-base-whisper.dll', 'ggml-cpu-whisper.dll', 'ggml-whisper.dll') {
            $nativePath = Join-Path $vulkanDir $native
            if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
                Add-Failure "$native NOT FOUND at $nativePath - the Vulkan runtime chain is incomplete, so the app's pre-flight will decline it and GPU acceleration silently never engages."
                continue
            }

            $nativeMachine = Get-PeMachine $nativePath
            if ($nativeMachine -ne $expectedMachine) {
                Add-Failure "$native PE machine is '$nativeMachine', expected '$expectedMachine' (x64)."
            }
            else {
                Write-Host "[OK]  $native present (x64)" -ForegroundColor Green
            }
        }

        # ggml-vulkan-whisper.dll is the backend itself (~58 MB, the payload's whole
        # point) - whisper.dll alone would load and then have no GPU backend to bind.
        $ggmlVulkan = Join-Path $vulkanDir 'ggml-vulkan-whisper.dll'
        if (-not (Test-Path -LiteralPath $ggmlVulkan -PathType Leaf)) {
            Add-Failure "ggml-vulkan-whisper.dll NOT FOUND at $ggmlVulkan - the Vulkan directory ships without its backend, so the GPU runtime cannot engage anywhere."
        }
        else {
            $ggmlMachine = Get-PeMachine $ggmlVulkan
            if ($ggmlMachine -ne $expectedMachine) {
                Add-Failure "ggml-vulkan-whisper.dll PE machine is '$ggmlMachine', expected '$expectedMachine' (x64)."
            }
            else {
                Write-Host "[OK]  ggml-vulkan-whisper.dll present (x64)" -ForegroundColor Green
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Final report
# ---------------------------------------------------------------------------
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host " VC++ RUNTIME PAYLOAD CHECK FAILED" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host ""
        Write-Host "[FAIL] $failure" -ForegroundColor Red
    }
    Write-Host ""
    exit 1
}

Write-Host "All VC++ runtime payload checks passed." -ForegroundColor Green
exit 0

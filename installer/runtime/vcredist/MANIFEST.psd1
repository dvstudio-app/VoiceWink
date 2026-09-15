# MANIFEST.psd1 - identity pin for the four app-local Microsoft Visual C++
# runtime DLLs that VoiceWink ships next to whisper.dll (runtimes\win-x64\).
#
# WHY THESE FOUR DLLS EXIST IN THE REPO
# -------------------------------------
# The four native Whisper DLLs (whisper.dll, ggml-*.dll) import the MSVC runtime
# (vcruntime140 / vcruntime140_1 / msvcp140, plus vcomp140 for the CPU OpenMP
# backend). A clean Windows 11 VM with no Visual C++ Redistributable installed
# fails the native load with PInvokeError 0 ("operation completed successfully"
# = dependency-missing, not file-missing). Velopack installs per-user and
# non-elevated, so running vc_redist.x64.exe (machine-wide, admin-only) is not
# an option. Shipping the DLLs APP-LOCAL is Microsoft-permitted (these four are
# on the VC++ "Redistributable Files" list) and needs no install / admin /
# network. See docs/plans/2026-06-02-1939-vcredist-applocal-bundle/50-decision.md
# and installer/signing/README.md.
#
# WHY THE DLLS ARE COMMITTED (not fetched per build)
# --------------------------------------------------
# They are tiny (~920 KB total) and change at most a few times a year. Committing
# them keeps every build/CI machine free of a VC++-extraction toolchain.
# scripts/fetch-vcredist.ps1 is a maintainer-only REFRESH tool (it needs WiX v5
# + msiexec to crack open the redist bundle) - run it with -CheckOnly to detect
# an upstream version bump, or without args to regenerate these DLLs + this pin.
#
# WHAT VERIFIES AGAINST THIS FILE
# -------------------------------
# Parsed data-only via Import-PowerShellDataFile (NEVER dot-sourced / executed):
#   - scripts/fetch-vcredist.ps1        verifies freshly-extracted DLLs match.
#   - scripts/check-vcredist-payload.ps1 (the fail-closed release gate) verifies
#     the DLLs shipped in the publish/portable output match (SHA256 is the
#     strong identity anchor; signer Org + PE machine are belt-and-suspenders).
#
# REFRESH CHECKLIST (when -CheckOnly reports a newer runtime, or a VC++ CVE lands)
#   1. Update RedistVersion + the comment date below.
#   2. Re-pin installer/runtime/vcredist/EXPECTED_SHA256 to the new bundle SHA256.
#   3. Run scripts/fetch-vcredist.ps1 -Regenerate to re-extract + reprint values.
#   4. Replace the Dlls SHA256 + FileVersion entries below with the printed values.
#   5. Commit the refreshed DLLs + this manifest + EXPECTED_SHA256 together.
# Mirrors the WindowsAppRuntime version-bump procedure (installer/runtime/EXPECTED_SHA256).
#
# Source bundle: https://aka.ms/vs/17/release/vc_redist.x64.exe
# Redist version: 14.44.35211.0  (extracted 2026-06-02)
# Bundle SHA256 pin lives in: installer/runtime/vcredist/EXPECTED_SHA256

@{
    RedistVersion = '14.44.35211.0'

    # Authenticode signer Organization shared by all four DLLs. The per-DLL CN
    # differs (vcruntime*/msvcp140 = "CN=Microsoft Windows"; vcomp140 =
    # "CN=Microsoft Windows Software Compatibility Publisher"), so the gate
    # asserts the stable O= field - same convention as the WindowsAppRuntime
    # signer check in scripts/check-publish-payload.ps1.
    SignerOrg = 'O=Microsoft Corporation'

    # PE COFF machine for x64 (IMAGE_FILE_MACHINE_AMD64). The gate reads the
    # DLL's PE header and formats it as '0x{0:X4}' for a string compare.
    Machine = '0x8664'

    Dlls = @(
        @{ Name = 'vcruntime140.dll';   FileVersion = '14.44.35211.0'; Sha256 = 'D5E4D9A3E835FA679450145D6A7D94E36573A509317111904D9B3712C30D9066' }
        @{ Name = 'vcruntime140_1.dll'; FileVersion = '14.44.35211.0'; Sha256 = '1F2D41C4AA5DB0BC33EBF7B66D72943A817D7CE6CBE880502A9403823633093F' }
        @{ Name = 'msvcp140.dll';       FileVersion = '14.44.35211.0'; Sha256 = '0F885B509A685D2BBFA652FED26B5FB31D88FBDAB0A978C641D1C7B8AA460AA9' }
        @{ Name = 'vcomp140.dll';       FileVersion = '14.44.35211.0'; Sha256 = '55ABA23CDCD6484FBB06F4155B8CA75ADFCE7A881F10AFD0C49457165E677164' }
    )
}

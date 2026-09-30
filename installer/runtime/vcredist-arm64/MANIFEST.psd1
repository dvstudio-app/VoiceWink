# MANIFEST.psd1 - identity pin for the two app-local ARM64 Microsoft Visual C++ runtime DLLs
# that VoiceWink ships beside the native ARM64 llama-server (runtimes\win-arm64\llama\).
#
# WHY: the ARM64 llama-server closure imports MSVCP140.dll and VCRUNTIME140.dll (read from the PE
# import tables; ARM64 has no vcruntime140_1, and libomp replaces vcomp140). A clean ARM64 Windows
# carries the UCRT but not these two - the x64 manifest (installer/runtime/vcredist/MANIFEST.psd1)
# records the same fact for x64 and the reasons for shipping them app-local and committed.
#
# WHAT VERIFIES AGAINST THIS FILE (parsed data-only, never executed):
#   - scripts/fetch-vcredist.ps1 -Arch arm64   verifies freshly extracted DLLs match.
#   - scripts/check-vcredist-payload.ps1        step 4d: the copies in the publish output.
#   - scripts/check-pack-signatures.ps1         the copies inside the signed package.
#   - src/VoiceWink/Services/AIEnhancement/LocalEngine/LlamaServerPayload.cs pins both hashes
#     for the spawn gate; a parity test fails when they differ from this file.
#
# REFRESH: the x64 manifest's checklist with -Arch arm64. Keep RedistVersion equal to the x64
# manifest's - one Microsoft release, two architectures.
#
# Source bundle: https://aka.ms/vs/17/release/vc_redist.arm64.exe
# Redist version: 14.44.35211.0  (extracted 2026-09-30)
# Bundle SHA256 pin lives in: installer/runtime/vcredist-arm64/EXPECTED_SHA256

@{
    RedistVersion = '14.44.35211.0'

    SignerOrg = 'O=Microsoft Corporation'

    # PE COFF machine for ARM64 (IMAGE_FILE_MACHINE_ARM64).
    Machine = '0xAA64'

    Dlls = @(
        @{ Name = 'vcruntime140.dll'; FileVersion = '14.44.35211.0'; Sha256 = 'D8A8513921544569837E400D37CC71302819967AE6DEFA932266A7ECB41DBAF9' }
        @{ Name = 'msvcp140.dll';     FileVersion = '14.44.35211.0'; Sha256 = '045FAEAB0B5710816AE160EA20DAE8495FFC11BB51FECA15D22AAF7FF3752CB3' }
    )
}

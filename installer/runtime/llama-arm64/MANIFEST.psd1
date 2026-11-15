@{
    # llama.cpp's `llama-server` for ARM64 Windows: the NATIVE build of the same release the x64
    # payload pins (installer/runtime/llama/MANIFEST.psd1), started as a child by the x64 app when
    # the OS is ARM64. An x64 process may create an ARM64 child; the emulated x64 payload is
    # correct there but 3-5x slower (measured on a Snapdragon X Elite laptop, 2026-09-30).
    #
    # The archive is upstream's OpenCL build: the nine files of the CPU-only ARM64 archive
    # (byte-identical, compared file by file) plus ggml-opencl.dll, so the child has a CPU backend
    # AND a GPU backend and the app's per-PC first-use check decides between them. Import closure
    # (read from the PE import tables, 2026-09-30): llama-server.exe -> llama-server-impl ->
    # llama-common, mtmd, llama, ggml, ggml-base; ggml-base -> libomp. ggml-cpu.dll and
    # ggml-opencl.dll are loaded DYNAMICALLY by ggml from the exe directory; ggml-opencl.dll
    # imports the system's OpenCL.dll (the graphics driver's loader) and ggml falls back to the
    # CPU backend when it cannot load. One ggml-cpu.dll: no per-CPU variants on ARM64. The zip's
    # other tools are deliberately NOT shipped.
    #
    # Its OWN directory - runtimes\win-arm64\llama\ in the app output - for the x64 payload's
    # reason (ggml maps every ggml-*.dll in the exe directory), and with ARM64 copies of the two
    # VC++ runtime DLLs the closure imports (msvcp140, vcruntime140; ARM64 has no vcruntime140_1).
    # No Vulkan loader: this build has no Vulkan backend.
    #
    # Every file below is upstream-UNSIGNED; vpk signs them at pack time, so the pre-pack gate
    # requires NotSigned sources and the runtime spawn gate accepts the pinned hash OR a DV Studio
    # signature per file - the x64 payload's hash-or-signature rule.
    #
    # Consumers: the x64 manifest's list, each reading this file for the ARM64 directory -
    #   scripts/check-vcredist-payload.ps1 step 4d, scripts/check-pack-signatures.ps1,
    #   scripts/check-llama-server-spawn.ps1, scripts/check-publish-payload.ps1 (Build equality),
    #   src/VoiceWink/Services/AIEnhancement/LocalEngine/LlamaServerPayload.cs (the Arm64 set).
    #
    # BUMP PROCEDURE: bump TOGETHER with the x64 manifest (Build and Commit must be equal - one
    # notices entry covers both). Download the release's win-opencl-adreno-arm64 zip, verify its
    # SHA-256 against GitHub's asset digest, extract exactly the files listed here, re-hash,
    # update SourceArchive* and every Sha256 below plus LlamaServerPayload.cs, then run
    # tools/llama-server-probe on an ARM64 PC in both modes - no hosted CI runner executes ARM64.
    Build = 'b12110'
    Commit = 'cd959bae9'
    SourceArchive = 'https://github.com/ggml-org/llama.cpp/releases/download/b12110/llama-b12110-bin-win-opencl-adreno-arm64.zip'
    SourceArchiveSha256 = 'fac3826972eeb5f0efa30dbda0bad62c559f91e3c69916d2c1bf599621bd5659'
    Machine = '0xAA64'
    ExeName = 'llama-server.exe'
    HasVulkanLoader = $false
    Files = @(
        @{ Name = 'ggml-base.dll'; Sha256 = 'da51a68ad763cb16c9f4f9cd76ee90918b3d1e6a898b21e44947b2a6a18fb5be' }
        @{ Name = 'ggml-cpu.dll'; Sha256 = '7dda802d308c4c05b4b0654e74dbeb0942947da386924c5d5f298a6bd0f6b67b' }
        @{ Name = 'ggml-opencl.dll'; Sha256 = '340a96c9d7ce6e2f78a09c40110ec70d2bc70bb21733c952ac08273810443918' }
        @{ Name = 'ggml.dll'; Sha256 = '84f5f975819a60cd15a0033f4361fdac8fea111301addbe0081b356dad3b22e3' }
        @{ Name = 'libomp.dll'; Sha256 = '26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a' }
        @{ Name = 'llama-common.dll'; Sha256 = 'eaa4888f9b9685ce8b652fbaa0b3c1b63e6e051648086b9a2fad613074cde406' }
        @{ Name = 'llama-server-impl.dll'; Sha256 = '64d4b18e467d49279b885b0ec3f16d8963d709200e8078cf666c68521fa72071' }
        @{ Name = 'llama-server.exe'; Sha256 = 'd6908c01d6b7767a1122a157e9bdf24a1412f270ef23cd9af2d7c3a07a74e753' }
        @{ Name = 'llama.dll'; Sha256 = '9def90ce6d5de42fc97d89cd8dc47a7694b02dbbe617196d2d8e5681dc25e47a' }
        @{ Name = 'mtmd.dll'; Sha256 = '4c1239fdc92de05ef2332365f1211a1ca77f08a922e2c901e2f09dec314c5a1e' }
    )
    # The two ARM64 VC++ runtime DLLs copied into the same directory, by name - their bytes and
    # hashes are governed by installer/runtime/vcredist-arm64/MANIFEST.psd1, not restated here.
    VcRuntimeDlls = @('msvcp140.dll', 'vcruntime140.dll')
}

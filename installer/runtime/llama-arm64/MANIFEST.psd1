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
    Build = 'b12359'
    Commit = 'b1b770d90'
    SourceArchive = 'https://github.com/ggml-org/llama.cpp/releases/download/b12359/llama-b12359-bin-win-opencl-adreno-arm64.zip'
    SourceArchiveSha256 = '0bb97f5716d24b5c4257594f1a8f1d75b26beb967eac0d3a9b66caae5fe0bb13'
    Machine = '0xAA64'
    ExeName = 'llama-server.exe'
    HasVulkanLoader = $false
    Files = @(
        @{ Name = 'ggml-base.dll'; Sha256 = '7e793b53b14449ac19b0461a63c0ea22d02114c6c7f20811d7e7bad2bcf1efae' }
        @{ Name = 'ggml-cpu.dll'; Sha256 = '1a63d6dba3f9ffd3a636d41e2ab51aab413225e12dcaec2eb80b0608337bfbde' }
        @{ Name = 'ggml-opencl.dll'; Sha256 = 'fb831667b31de70b578b2a186272590546a70755769dda205c8c9e5f33675986' }
        @{ Name = 'ggml.dll'; Sha256 = '53f924c2934455532cb24afbfd5686ece90f767fa1aacb6eae202c97377c9eb1' }
        @{ Name = 'libomp.dll'; Sha256 = '26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a' }
        @{ Name = 'llama-common.dll'; Sha256 = '2803620da257ab50c8d76ebf59be055ed21bb91ef8a1bad0df8aff2a7061747d' }
        @{ Name = 'llama-server-impl.dll'; Sha256 = '8388dc1a838223e6fefd7b3d6e70d86e03b2274b2573cdf5ac8f33b602b0877d' }
        @{ Name = 'llama-server.exe'; Sha256 = 'c8ea3fa202d22cffff0f1c01320d38d9efba63d67ff078ca04613e07265a9f60' }
        @{ Name = 'llama.dll'; Sha256 = 'cfc3a793e43a8e946dbfdb0d6c290604ad4eedf06f42d6462f1a8083a35d975f' }
        @{ Name = 'mtmd.dll'; Sha256 = '04e3fb881eebbc6ef301596ff5160a28e0ee41b9d4f5ccda01450fbb09177328' }
    )
    # The two ARM64 VC++ runtime DLLs copied into the same directory, by name - their bytes and
    # hashes are governed by installer/runtime/vcredist-arm64/MANIFEST.psd1, not restated here.
    VcRuntimeDlls = @('msvcp140.dll', 'vcruntime140.dll')
}

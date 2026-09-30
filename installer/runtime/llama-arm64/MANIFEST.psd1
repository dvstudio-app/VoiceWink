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
    Build = 'b11147'
    Commit = 'fee39dd92'
    SourceArchive = 'https://github.com/ggml-org/llama.cpp/releases/download/b11147/llama-b11147-bin-win-opencl-adreno-arm64.zip'
    SourceArchiveSha256 = '40435b65cd59bc73031c7809d407a0eaec97a13ede0e4bf608007e247e67e445'
    Machine = '0xAA64'
    ExeName = 'llama-server.exe'
    HasVulkanLoader = $false
    Files = @(
        @{ Name = 'ggml-base.dll'; Sha256 = '2d73e9f5a8009c8745318a1f2c3a8d50d903b256a58e0fdea184cc28cfb968f7' }
        @{ Name = 'ggml-cpu.dll'; Sha256 = 'b0226196aab68a5106507d7c9002537e81f94b5062d8639948019ed70aafa81d' }
        @{ Name = 'ggml-opencl.dll'; Sha256 = '840c9bd737633479a880a5ee43200ca648f3a3e315a6174612127405605b81f3' }
        @{ Name = 'ggml.dll'; Sha256 = '0040d1da745e7182bae173084aeb85c3a806f25dfe3af207c79efb2c4e5101a0' }
        @{ Name = 'libomp.dll'; Sha256 = '26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a' }
        @{ Name = 'llama-common.dll'; Sha256 = 'bd8aaea3f9a7c7e19aaa1e0b6c812ea3ae2e2e5f36fb3f23366cf9520ee1c4e2' }
        @{ Name = 'llama-server-impl.dll'; Sha256 = 'd006a8a295c7f125f8d5722219f0a2128a98a274027adfea130273c1a3b78175' }
        @{ Name = 'llama-server.exe'; Sha256 = 'd8f87f3843ac45ff5a1eba107e19b0d9e525d8e1885bdfacee8faad0eaeb2203' }
        @{ Name = 'llama.dll'; Sha256 = '9e01fb1a675ce8102b2f573c54a5386551973a4f822dde86a67628ae2c983b81' }
        @{ Name = 'mtmd.dll'; Sha256 = '44057cd1a0a04accfa4a807dceb2631c367bd908fb500245adc02381a02bd124' }
    )
    # The two ARM64 VC++ runtime DLLs copied into the same directory, by name - their bytes and
    # hashes are governed by installer/runtime/vcredist-arm64/MANIFEST.psd1, not restated here.
    VcRuntimeDlls = @('msvcp140.dll', 'vcruntime140.dll')
}

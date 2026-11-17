@{
    # llama.cpp's `llama-server`, the local LLM engine behind AI enhancement "On this PC".
    # Shipped in every install (owner decision, 2026-09-24).
    #
    # NOT ONE BINARY. llama-server.exe is a 9 KB stub; the code is in the DLLs below. Import
    # closure (read from the PE import tables, 2026-09-29): llama-server.exe -> llama-server-impl
    # -> llama-common, mtmd, llama, ggml, ggml-base; ggml-base -> libomp. ggml-vulkan.dll (imports
    # vulkan-1.dll) and the fourteen ggml-cpu-<arch>.dll variants are loaded DYNAMICALLY: ggml
    # scores every ggml-cpu-*.dll in the exe directory and loads the best one for this CPU, and
    # falls back to the CPU backend when ggml-vulkan.dll cannot load. The zip's other tools
    # (llama-cli, llama-bench, llama-quantize, ggml-rpc, ...) are deliberately NOT shipped.
    #
    # ggml's backend loader searches the EXE DIRECTORY, the CURRENT DIRECTORY and
    # GGML_BACKEND_PATH for ggml-<name>*.dll (ggml/src/ggml-backend-reg.cpp:486-501 at this
    # tag), so the payload has its OWN directory - runtimes\win-x64\llama\ in the app output -
    # and never sits beside Whisper's ggml-*-whisper.dll natives in runtimes\win-x64\. The
    # directory also carries a copy of the pinned Vulkan loader and three VC++ runtime DLLs
    # (msvcp140, vcruntime140, vcruntime140_1: the closure's imports; libomp replaces vcomp140),
    # because a child resolves its imports from ITS OWN directory first.
    #
    # Every file below is upstream-UNSIGNED; vpk signs them at pack time (Kind 'Ours' in
    # scripts/check-pack-signatures.ps1), so the pre-pack gate requires NotSigned sources and
    # the runtime spawn gate accepts the pinned hash (dev/CI) OR a DV Studio signature (release)
    # per file - the hash-or-signature rule.
    #
    # The native ARM64 build of this same release is a second payload with its own manifest,
    # installer/runtime/llama-arm64/MANIFEST.psd1 - Build and Commit there must equal these
    # (scripts/check-publish-payload.ps1 fails a release where they differ), so bump both together.
    #
    # Consumers (update ALL together when bumping):
    #   scripts/check-vcredist-payload.ps1 step 4c   (pre-pack: exact file set, hashes, NotSigned)
    #   scripts/check-pack-signatures.ps1            (post-pack: one Ours row per file; the runtime-provenance
    #                                                leg runs the app's spawn gate over the signed directory)
    #   scripts/check-llama-server-spawn.ps1         (CI: the staged set runs, build number)
    #   scripts/check-publish-payload.ps1            (THIRD-PARTY-NOTICES names this Build)
    #   src/VoiceWink/Services/AIEnhancement/LocalEngine/LlamaServerPayload.cs
    #                                                (the runtime spawn gate's pins; parity-tested against this file)
    #
    # BUMP PROCEDURE: download the new release's win-vulkan-x64 zip from the upstream release
    # page, verify its SHA-256 against GitHub's own asset digest, extract exactly the files
    # listed here (re-run the import-closure read if upstream adds a dependency), copy LICENSE
    # from the tag and LICENSE-LLVM-OpenMP from the zip (saved as LICENSE-LLVM-OpenMP.txt: the public-snapshot publisher classifies files by extension), re-hash, update Build/Commit/SourceArchive*
    # and every Sha256 below, update the consumers above, and re-validate the GPU self-test
    # threshold and the local-AI bench on the
    # new build - numerics change between builds.
    Build = 'b12359'
    Commit = 'b1b770d90'
    SourceArchive = 'https://github.com/ggml-org/llama.cpp/releases/download/b12359/llama-b12359-bin-win-vulkan-x64.zip'
    SourceArchiveSha256 = 'e8d18c6e4210cca87e7307c2ea78363ac8d158e4e1397358ec96ab70b53a0d5e'
    Machine = '0x8664'
    ExeName = 'llama-server.exe'
    Files = @(
        @{ Name = 'ggml-base.dll'; Sha256 = '20e4986ec6fd88eb61630433ff172e08885f8fecfbd0c1960a5d55c8854019e3' }
        @{ Name = 'ggml-cpu-alderlake.dll'; Sha256 = 'f4bd2a5bd4c99669a5efcbc0edf5326c6f51cf556c3301ae5c8fc2994b913440' }
        @{ Name = 'ggml-cpu-cannonlake.dll'; Sha256 = 'b6a69981682c6e9dfb5fefcfc3677b9e17c112c3c33fc9d59405daa618e63304' }
        @{ Name = 'ggml-cpu-cascadelake.dll'; Sha256 = '488337fb896ac54ec8a74a5e8163b48d8ec4c8e082da4bbb62ebe52139d3b3d8' }
        @{ Name = 'ggml-cpu-cooperlake.dll'; Sha256 = '7d30660f15d288d6da7c1d99ae8418c46f61d1d5036e0082aad6fbf4e461d2bb' }
        @{ Name = 'ggml-cpu-haswell.dll'; Sha256 = '26dbcf80225fe3c56aa750501247cebdc99b791f323c33d0d8fcd2ed7c4bafcb' }
        @{ Name = 'ggml-cpu-icelake.dll'; Sha256 = '0a35f7a655bb74ce2d64cbb64e6817b17f62964b060c6a503d3267b2133199bc' }
        @{ Name = 'ggml-cpu-ivybridge.dll'; Sha256 = '2e83b2aa5ad108445a599fcdd46fafcb227a3d2d66743994434b0bcaacb5a714' }
        @{ Name = 'ggml-cpu-piledriver.dll'; Sha256 = '851474816a574847b264e018de02410f5905359934c2fa2411f7ff3d7706620c' }
        @{ Name = 'ggml-cpu-sandybridge.dll'; Sha256 = '86b1c9114249492b8d39a1a3d9f9717c8969334a4bd12c279b91248a553cb254' }
        @{ Name = 'ggml-cpu-sapphirerapids.dll'; Sha256 = 'b049b5672c3c246bb320ec011c735981628a9369687cfd64a4628c5bad238b3b' }
        @{ Name = 'ggml-cpu-skylakex.dll'; Sha256 = '183413744426f596c67fb206daf2cab17d666e22076e4a6b9a25d301de871d85' }
        @{ Name = 'ggml-cpu-sse42.dll'; Sha256 = '5b2f04b3ab1e76b09d887c496eb61b09a0ee4fc77b28c0602e45411906946ccd' }
        @{ Name = 'ggml-cpu-x64.dll'; Sha256 = '375434294cb7d22de0bf5da68fe67569fb5f8ca2c11e925850c8be3f52d052fe' }
        @{ Name = 'ggml-cpu-zen4.dll'; Sha256 = 'dae5d12772f0213b6d70a3c4db032146b504dea32b1b35bbcc9dff769edf57f7' }
        @{ Name = 'ggml-vulkan.dll'; Sha256 = '9cc0d9416bf032caa58af66fc7fe700e35a476b74cab94fcb45e08f4eca8213c' }
        @{ Name = 'ggml.dll'; Sha256 = '92b081cfb031e2efe5cd2a8c71c872e8a92cb1d4a43619ad6614dc30ad357b80' }
        @{ Name = 'libomp.dll'; Sha256 = 'a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c' }
        @{ Name = 'llama-common.dll'; Sha256 = 'a0e0b993dede7c95eb1ca79cdcc2a003f2f79ff59fc129cb46ec5daac80fd057' }
        @{ Name = 'llama-server-impl.dll'; Sha256 = 'c81c23738405831cacda737e64ce8f08870d4f44beb63e3aa9515b3ad2e4286e' }
        @{ Name = 'llama-server.exe'; Sha256 = '38c5affb81c9a8f88f5538c7d11bc445e13bc46a2c6eda05694b8b31a8d45995' }
        @{ Name = 'llama.dll'; Sha256 = '17b0063b06c4561f46929a809a60bf60c857783bb22f593528145cc1e1108d7d' }
        @{ Name = 'mtmd.dll'; Sha256 = '558d7c41475cc6f54d6da41eea23876cd35cfd2e9d8f2b3e637b8c49c9928ed5' }
    )
    # The three VC++ runtime DLLs copied into the same directory, by name - their bytes and
    # hashes are governed by installer/runtime/vcredist/MANIFEST.psd1, not restated here.
    VcRuntimeDlls = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
}

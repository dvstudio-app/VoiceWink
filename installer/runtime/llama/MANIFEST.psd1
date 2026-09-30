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
    Build = 'b11147'
    Commit = 'fee39dd92'
    SourceArchive = 'https://github.com/ggml-org/llama.cpp/releases/download/b11147/llama-b11147-bin-win-vulkan-x64.zip'
    SourceArchiveSha256 = '55058706491ecc2a33c0bb3d24400ebda341965d89bfa4b975dc9c68e7f37d35'
    Machine = '0x8664'
    ExeName = 'llama-server.exe'
    Files = @(
        @{ Name = 'ggml-base.dll'; Sha256 = '7ee3380f8d4758b518caefbe7b1cdcbf2c752d1bcb3a5823e837f24b55a7f99d' }
        @{ Name = 'ggml-cpu-alderlake.dll'; Sha256 = '832dfb28df9a5a4a15c982a4f286cfdcc2fb0220c57af102fb6cf0823f58df69' }
        @{ Name = 'ggml-cpu-cannonlake.dll'; Sha256 = '842aa91963d4259b0e6a406f9e55ce5c2fc43fff9c90885b01478d30cf77602d' }
        @{ Name = 'ggml-cpu-cascadelake.dll'; Sha256 = '042b9e375846a2e840c836a55ff2e60068398d4521d92ea231b4153d6fb9ad37' }
        @{ Name = 'ggml-cpu-cooperlake.dll'; Sha256 = '452bf574e28e003ea02786513efeebd3af049bbf3d332fb1f2f67f5f2527ea9f' }
        @{ Name = 'ggml-cpu-haswell.dll'; Sha256 = 'b6ddbee3c5de7da4c844eeb7df74a4279db7eecff94786d81b833bae3415f5d5' }
        @{ Name = 'ggml-cpu-icelake.dll'; Sha256 = '3c9de8002008369f994a1048540187991ac83fc3daf29ddcbc97fa01808f44dd' }
        @{ Name = 'ggml-cpu-ivybridge.dll'; Sha256 = '7a32ff2606a652197ee76931d5c97b02d02e90e09d6c479e4d12a0561cfd8880' }
        @{ Name = 'ggml-cpu-piledriver.dll'; Sha256 = '375df17509f3a8ccbb8165e8d48ebe7086e13d10b7bba3f763fd30f01de04b26' }
        @{ Name = 'ggml-cpu-sandybridge.dll'; Sha256 = 'ccd500e1639e931f93fa8b51f6b43f6a2ca95dd3c14243ee39d88cc5ac48df50' }
        @{ Name = 'ggml-cpu-sapphirerapids.dll'; Sha256 = '263c3fee6b7a4add9cc8fb9876c818cf0721c72dd8f9f8ecfbe58f64f837e5bd' }
        @{ Name = 'ggml-cpu-skylakex.dll'; Sha256 = 'e7b742a3c4a5445f385882eaadb14abb6da28912c652a2530a5a4598152ea5d7' }
        @{ Name = 'ggml-cpu-sse42.dll'; Sha256 = '808c4c8abb9f99f496ac3ee69d72529b156663ce1c9c6789c463d68193f97c06' }
        @{ Name = 'ggml-cpu-x64.dll'; Sha256 = '095549b90f3562ef4690542f977bb3e62e25e531f1285e9d5c80a1958bf0c70b' }
        @{ Name = 'ggml-cpu-zen4.dll'; Sha256 = '8cd60877ec783e59ec84e851f8f6914f52445400d7014e2b67e7de954a44f8f6' }
        @{ Name = 'ggml-vulkan.dll'; Sha256 = 'a1fa1db850faedc7b450a1d3b306bab4443c2c380839e3994be1bd390bc41613' }
        @{ Name = 'ggml.dll'; Sha256 = 'b76c45a3e2493ab7e22487d9ab5217508fb452d983e2cc8b6a9dfd882a724a27' }
        @{ Name = 'libomp.dll'; Sha256 = 'a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c' }
        @{ Name = 'llama-common.dll'; Sha256 = 'bd24c02aabffbcb71bdc8870660631e6d27bcc0332972a7d4c023884b2223f4b' }
        @{ Name = 'llama-server-impl.dll'; Sha256 = 'f3cebd952136b01171b3ab92d90064b3fa1ed2e00ba178f2ecd37828cd65c547' }
        @{ Name = 'llama-server.exe'; Sha256 = 'a9b905492642e253f85347fa7166c614dedf44fcabe82550fd769425ef224a03' }
        @{ Name = 'llama.dll'; Sha256 = '8def2e0053d68d85c747492d12ac70e25ea673cd05e32f92a90e9834ffb95409' }
        @{ Name = 'mtmd.dll'; Sha256 = '227521be2d121edea8460ba650963e213847443ae6a0eccc6988d6bfaa9742ee' }
    )
    # The three VC++ runtime DLLs copied into the same directory, by name - their bytes and
    # hashes are governed by installer/runtime/vcredist/MANIFEST.psd1, not restated here.
    VcRuntimeDlls = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
}

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
    Build = 'b11960'
    Commit = '8642541b2'
    SourceArchive = 'https://github.com/ggml-org/llama.cpp/releases/download/b11960/llama-b11960-bin-win-vulkan-x64.zip'
    SourceArchiveSha256 = 'bac6914b5e83523eb008c6789eaa12669f93fbc196c3b9e8237b6672323b33e9'
    Machine = '0x8664'
    ExeName = 'llama-server.exe'
    Files = @(
        @{ Name = 'ggml-base.dll'; Sha256 = '200cd9b6f0a7934c2e1c85e55289d5498332713321b03e83eb2ae3da5eeb6137' }
        @{ Name = 'ggml-cpu-alderlake.dll'; Sha256 = '8ae4c14de9eeb5528e42cff69ed8485e6cd0e3f8601837add873a9d12199ca95' }
        @{ Name = 'ggml-cpu-cannonlake.dll'; Sha256 = '779a9d0ede6215fba8a00fb680c1295dd62836f9d8a124437d48f9c086fc1f07' }
        @{ Name = 'ggml-cpu-cascadelake.dll'; Sha256 = '71f6275d8aa7df86545975e02510e10280abfcc274ae419dee898746a5b2ce1b' }
        @{ Name = 'ggml-cpu-cooperlake.dll'; Sha256 = 'c0b3f9445cbd4490504c13d06e89b25bdbe9dea73784fc62c0c10ae7069de6cb' }
        @{ Name = 'ggml-cpu-haswell.dll'; Sha256 = '0bc8209ebe520b43d8549db7d45ca87382df88c3a8b238f3dda42d96e2af178e' }
        @{ Name = 'ggml-cpu-icelake.dll'; Sha256 = '81f86f655c82dfe330f9ee269b97a2b21352e5e876bd385d48bc61d9a6ca230b' }
        @{ Name = 'ggml-cpu-ivybridge.dll'; Sha256 = '52f262a610ac189d0375472c66c46b7b366d618916ed67a2b119e69023c3f943' }
        @{ Name = 'ggml-cpu-piledriver.dll'; Sha256 = '340c9dc4c5618fa4f89e6311c31f2f472d3277a87858c79b3c44890b0e92d1e7' }
        @{ Name = 'ggml-cpu-sandybridge.dll'; Sha256 = '6251d2dc50745bad1d0f16ea95a4484ef5d8c7cb49b5a4a33af85d199c08be61' }
        @{ Name = 'ggml-cpu-sapphirerapids.dll'; Sha256 = 'cdfdc20cb7f953f87e61ab90e234c864df4e2c2615f5651bd83a4cf27fdc4e3d' }
        @{ Name = 'ggml-cpu-skylakex.dll'; Sha256 = 'da5c7c86df8409c08fb748ed5596eba65285ead7747581daa124a7fff2f65704' }
        @{ Name = 'ggml-cpu-sse42.dll'; Sha256 = '00efeb321bb728616cde5719bab06ea575679f7ca4967367faa70fd5ca1c8ba6' }
        @{ Name = 'ggml-cpu-x64.dll'; Sha256 = 'af7233344eec5587d531b4e875ec5bf2b1aa62c2be57b657f69cca503f0db918' }
        @{ Name = 'ggml-cpu-zen4.dll'; Sha256 = '4ebbee0eb0c5a4e9e3c3b437d7a2876ede8f1d3e00526dad33db50f39f46e831' }
        @{ Name = 'ggml-vulkan.dll'; Sha256 = '429b5dc6931cfc51121f05a43b4fb0b7dbf49d8c2ce38c0f72f848b308c592c8' }
        @{ Name = 'ggml.dll'; Sha256 = 'd2e63157b90c0fd15f010fb2952d032d9f3296b8406b78ff0950211cc144705b' }
        @{ Name = 'libomp.dll'; Sha256 = 'a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c' }
        @{ Name = 'llama-common.dll'; Sha256 = '34370fd48c31112b2ebe4e7bb234747c1a9b39325a29e61302659c525a6049d9' }
        @{ Name = 'llama-server-impl.dll'; Sha256 = '0b329dcfdece6a43f34d0554dec5ca0cc2efe6ee23d5aabd8141c51487668564' }
        @{ Name = 'llama-server.exe'; Sha256 = '81f65c6681d66099e5d48757aa2c050ba958dc43816e584f0bb4181eb7c910d8' }
        @{ Name = 'llama.dll'; Sha256 = 'af13ed8cb1454a80689dbd390310995b6c4b23333e096152453fe6d0fef17356' }
        @{ Name = 'mtmd.dll'; Sha256 = 'e82d563ae9dad225a3d15d0f189e046909713e26759ae9a046f89430bc845bd3' }
    )
    # The three VC++ runtime DLLs copied into the same directory, by name - their bytes and
    # hashes are governed by installer/runtime/vcredist/MANIFEST.psd1, not restated here.
    VcRuntimeDlls = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
}

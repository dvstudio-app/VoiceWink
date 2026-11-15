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
    Build = 'b12110'
    Commit = 'cd959bae9'
    SourceArchive = 'https://github.com/ggml-org/llama.cpp/releases/download/b12110/llama-b12110-bin-win-vulkan-x64.zip'
    SourceArchiveSha256 = '9732a5e48887ca54d0bfe4af4324dc85fd56cd8cda19b52df3542fa6ebe6874f'
    Machine = '0x8664'
    ExeName = 'llama-server.exe'
    Files = @(
        @{ Name = 'ggml-base.dll'; Sha256 = '3a0399fa51102a86824255beda9d021ea7e8cd2bd8ca448a43a9a80aa4829290' }
        @{ Name = 'ggml-cpu-alderlake.dll'; Sha256 = '1de7a0d14d70bd741f2e4cdfa959176fee26680d6fbc78c717c0bd19e20b6233' }
        @{ Name = 'ggml-cpu-cannonlake.dll'; Sha256 = '1451909b84c223739247088a0e03db0f89bcd83461f2af43f53d794727afddcd' }
        @{ Name = 'ggml-cpu-cascadelake.dll'; Sha256 = '74d79278bd20143d3e7deeb2b5c033555f9d621016a7152b5190a2fcbf30265c' }
        @{ Name = 'ggml-cpu-cooperlake.dll'; Sha256 = '737f0e96f23ca7d66c8dc55eace36ebbfd47770a9a228c804ffff5bf66f933a8' }
        @{ Name = 'ggml-cpu-haswell.dll'; Sha256 = '85d9ffd6f84fd0ec01586420b935ac4d5a5c9a1ab76b533eb3ddc060c1fe2869' }
        @{ Name = 'ggml-cpu-icelake.dll'; Sha256 = '41ca727e74b171983ea9afe0250553706c09225c10a8c6ad72a619f9189279a8' }
        @{ Name = 'ggml-cpu-ivybridge.dll'; Sha256 = '39752b7667cb34b3bbd06178fac83803f6089771d43873a9c81015d2a98c280b' }
        @{ Name = 'ggml-cpu-piledriver.dll'; Sha256 = '65bef2ae4f69256d9aff053a6fff1abdbd33acdbc51ede863d8bc5bd43c53ee8' }
        @{ Name = 'ggml-cpu-sandybridge.dll'; Sha256 = '5359624dee5670cb299c08f4a49347f7a6f4d0df35ed4c0a91164e4d771b3e23' }
        @{ Name = 'ggml-cpu-sapphirerapids.dll'; Sha256 = 'a7a395d0e49f182240c9a2ed1d623398911506bdae418baf161354541aa06a89' }
        @{ Name = 'ggml-cpu-skylakex.dll'; Sha256 = '6bd53dc5731c5b9e4d3fbf98a9dc5b3ce76d357be9a579bc6c8cedac035462e8' }
        @{ Name = 'ggml-cpu-sse42.dll'; Sha256 = '1e1700e6b6eef26b6abb0bc68669b6ee9fdad2b8d00058e491cb5160eb54afc2' }
        @{ Name = 'ggml-cpu-x64.dll'; Sha256 = '81877c8787f9d5680b990f4cf5f8b89ba5b02ee5064b36abfd53d9d2ffb50916' }
        @{ Name = 'ggml-cpu-zen4.dll'; Sha256 = 'b5c1eec79e83878303acfa774f1275d9e025493b2c23f2a01af09fe985cdc21a' }
        @{ Name = 'ggml-vulkan.dll'; Sha256 = 'e863e7556e10eeea6888894755552a917ab919004882bdf89d2d684d2b54fc8c' }
        @{ Name = 'ggml.dll'; Sha256 = '06dfdff8b7b834c757ec5b5411abfc0f732604fb595eaaffbdc6da563523e8a1' }
        @{ Name = 'libomp.dll'; Sha256 = 'a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c' }
        @{ Name = 'llama-common.dll'; Sha256 = '0898e517843e4478b5178621dc878552b4d40a4d47d7b6f1775e7897f4e2344e' }
        @{ Name = 'llama-server-impl.dll'; Sha256 = 'c5d306bdcad1c4011f18a1c01ae2fcdca48a197af0d421c2931ace2cfb487fc6' }
        @{ Name = 'llama-server.exe'; Sha256 = '00d50686815af38249d0218c91558fbd0e9093ac8c074bac86196907f4305e31' }
        @{ Name = 'llama.dll'; Sha256 = '211b91d86e67c49f12eb97992b45ae4c2fc797d6edab48637835a877c808890e' }
        @{ Name = 'mtmd.dll'; Sha256 = '74a3cace50f6c545cc66e4dc05e2aecf465f146ea1b4000376cd9834598a3e14' }
    )
    # The three VC++ runtime DLLs copied into the same directory, by name - their bytes and
    # hashes are governed by installer/runtime/vcredist/MANIFEST.psd1, not restated here.
    VcRuntimeDlls = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
}

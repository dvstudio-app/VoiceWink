@{
    # The Khronos Vulkan LOADER, vendored because the GPU-build parakeet-server.exe STATICALLY
    # imports vulkan-1.dll (first import-table entry, delay-load directory empty — demonstrated
    # from the PE, 2026-09-01): without a loader the child dies before main, every spawn fails,
    # and the storm fuse marks Parakeet — the DEFAULT engine — unrunnable on every GPU-less
    # machine (no GPU driver = no system loader: VMs, stripped installs). Bundling is the right
    # call ONLY because that static import forecloses relying on the driver-installed system
    # loader; Khronos guidance otherwise favours the system copy. Measured before vendoring:
    # beside the exe WITH a real driver the child maps THIS copy and still enumerates the GPU
    # through the driver's ICD; with a loader but no ICD it serves on CPU, byte-identical to
    # PARAKEET_DEVICE=cpu (0/8 clips, two machines, two vendors).
    #
    # AGEING: this pin does not refresh with driver installs, and the child's own-directory copy
    # wins forever — a driver whose ICD someday requires a newer loader interface is the failure
    # direction. Treat like the vcredist pins: refresh occasionally with a note (vcredist-drift
    # pattern; the follow-up card tracks a drift check). Source: LunarG Vulkan Runtime
    # components (upstream archive below); the loader project is Apache-2.0 and explicitly
    # redistributable. VulkanRT-License.txt beside this manifest is the upstream attribution
    # document verbatim (the archive ships no separate NOTICE file — audited 2026-09-01);
    # THIRD-PARTY-NOTICES.md carries the shipped attribution.
    LoaderVersion = '1.4.357.0'
    SourceArchive = 'https://sdk.lunarg.com/sdk/download/latest/windows/vulkan-runtime-components.zip'
    SourceArchiveSha256 = 'a14672efed15aafc7f5a16572d35cd3a3416eadf670aeee3cdf50ee32d5fbf83'
    SignerOrg = 'LunarG, Inc.'
    # The certificate's COMMON NAME, decoded from DER and compared whole-value by the post-pack
    # signing gate (check-pack-signatures, since 2026-09-02) - vpk leaves this validly signed
    # file intact, so the pack must carry exactly this signer and exactly the Sha256 below.
    # SignerOrg above is what the pre-pack gate matches against the display subject; the two are
    # equal for LunarG today and the gates must not silently depend on that, hence both keys.
    SignerCommonName = 'LunarG, Inc.'
    Machine = '0x8664'
    Dlls = @(
        @{
            Name = 'vulkan-1.dll'
            Sha256 = 'cd862090370454630b31b174e3d4eb474fda38ea034998d1fe1767b0c99a8696'
        }
    )
}

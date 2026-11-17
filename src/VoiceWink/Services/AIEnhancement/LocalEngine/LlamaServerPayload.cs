using System.Runtime.InteropServices;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// One bundled llama-server payload: where it sits under the app, and every PE in it with its
/// SHA-256, split by how each file is trusted. Two exist (<see cref="LlamaServerPayload.X64"/>,
/// <see cref="LlamaServerPayload.Arm64"/>); nothing is shared between them, so a directory is only
/// ever checked against its own pins.
/// </summary>
/// <param name="Name">For logs and the probe: <c>x64</c> / <c>arm64</c>.</param>
/// <param name="RelativeDirectory">Relative to the app's base directory. Its OWN directory: ggml's
/// backend loader maps every <c>ggml-*.dll</c> in the exe directory and the current directory.</param>
/// <param name="PeMachine">The PE machine every file in this set must carry (0x8664 / 0xAA64).
/// Checked by the gate independently of hash and signature: our signature says who shipped a
/// file, not that it is the right architecture - a signed x64 DLL under an ARM64 name would pass
/// the signature branch and the ARM64 child could not load it.</param>
/// <param name="SignedAtPack">Upstream-unsigned PEs that vpk signs at pack time: trusted by the
/// pinned hash (dev and CI builds) OR a DV Studio signature (a signed release).</param>
/// <param name="PinnedCopies">Vendor-signed copies vpk leaves byte-identical: trusted by the
/// pinned hash ONLY, in every build.</param>
internal sealed record LlamaPayloadSet(
    string Name,
    string RelativeDirectory,
    ushort PeMachine,
    IReadOnlyDictionary<string, string> SignedAtPack,
    IReadOnlyDictionary<string, string> PinnedCopies)
{
    internal string DirectoryFor(string appBaseDirectory) => Path.Combine(appBaseDirectory, RelativeDirectory);

    /// <summary>
    /// What a stored first-use GPU verdict is keyed on, beside the display driver and the model. A
    /// verdict is about ONE payload's GPU backend: the x64 set computes through Vulkan, the ARM64
    /// set through OpenCL, and the same PC can fail one and pass the other (measured: an Adreno
    /// X1-85 returns wrong text through the emulated Vulkan build and correct text through the
    /// native OpenCL one). So a verdict recorded by one set is never read by the other. The x64
    /// value is the bare build, unchanged from before the second set existed.
    /// </summary>
    internal string VerdictBuild => ReferenceEquals(this, LlamaServerPayload.X64)
        ? LlamaServerPayload.Build
        : LlamaServerPayload.Build + "-" + Name;
}

/// <summary>
/// LAI-2: the bundled llama-server payload as compile-time data — the directory, the pinned build,
/// and every PE in it with its SHA-256. The spawn gate reads this; the pack gates read
/// <c>installer/runtime/llama/MANIFEST.psd1</c>. <c>LlamaServerPayloadTests</c> reads that manifest
/// (and the vcredist and Vulkan-loader manifests for the copies) and asserts the same names and
/// hashes, so the runtime gate and the release gates can never disagree about the file set — the
/// disagreement TRN-34 and TRN-56 each shipped once.
///
/// <para><b>LAI-10: two payloads, chosen by the OS architecture and the processor.</b> The app is x64 and runs
/// emulated on an ARM64 PC; an x64 process may start an ARM64 child, and the native ARM64 build of
/// the same llama.cpp release does a long cleanup 3–5× faster there than the emulated x64 one
/// (owner measurement, Snapdragon X Elite: 8.3 s against 31 s, 17 s against 81 s cold).
/// <see cref="Current"/> is the ONE selection, made once per process: the ARM64 set where the OS
/// is ARM64 AND the processor has the instructions that build requires (<see cref="LlamaArm64Cpu"/>
/// - an older ARM64 PC keeps the emulated x64 set, which works there). There is no fallback from
/// one set to the other afterwards: a missing or damaged ARM64 directory on a PC that selected it
/// is refused by the spawn gate like any other damaged payload, never silently replaced by the
/// slow one.</para>
/// </summary>
internal static class LlamaServerPayload
{
    /// <summary>The upstream build tag; also what <c>--version</c> prints as <c>build 12359</c>.</summary>
    internal const string Build = "b12359";

    internal const string ExeName = "llama-server.exe";

    private static readonly Lazy<LlamaPayloadSet> CurrentSet = new(() =>
    {
        var os = RuntimeInformation.OSArchitecture;
        return ForOs(os, os == Architecture.Arm64 && LlamaArm64Cpu.SupportsNativePayload(LlamaArm64Cpu.ReadIsar1()));
    });

    /// <summary>The payload this PC runs, decided once per process so the directory, the gate's
    /// pins and the verdict key can never come from two different answers.</summary>
    internal static LlamaPayloadSet Current => CurrentSet.Value;

    /// <summary>The ARM64 set only on an ARM64 OS whose processor can run it; the x64 set
    /// everywhere else. An emulated x64 process is told the OS's true architecture (.NET 7+;
    /// measured on the owner's Snapdragon laptop for the MAPI guard, which reads the same
    /// property).</summary>
    internal static LlamaPayloadSet ForOs(Architecture osArchitecture, bool nativeArm64Supported)
        => osArchitecture == Architecture.Arm64 && nativeArm64Supported ? Arm64 : X64;

    /// <summary>The x64 payload (upstream <c>win-vulkan-x64</c>): CPU variants plus the Vulkan
    /// backend, the Vulkan loader copy and three VC++ DLLs. Pinned in
    /// <c>installer/runtime/llama/MANIFEST.psd1</c>.</summary>
    internal static readonly LlamaPayloadSet X64 = new(
        "x64",
        Path.Combine("runtimes", "win-x64", "llama"),
        0x8664,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ggml-base.dll"] = "20e4986ec6fd88eb61630433ff172e08885f8fecfbd0c1960a5d55c8854019e3",
            ["ggml-cpu-alderlake.dll"] = "f4bd2a5bd4c99669a5efcbc0edf5326c6f51cf556c3301ae5c8fc2994b913440",
            ["ggml-cpu-cannonlake.dll"] = "b6a69981682c6e9dfb5fefcfc3677b9e17c112c3c33fc9d59405daa618e63304",
            ["ggml-cpu-cascadelake.dll"] = "488337fb896ac54ec8a74a5e8163b48d8ec4c8e082da4bbb62ebe52139d3b3d8",
            ["ggml-cpu-cooperlake.dll"] = "7d30660f15d288d6da7c1d99ae8418c46f61d1d5036e0082aad6fbf4e461d2bb",
            ["ggml-cpu-haswell.dll"] = "26dbcf80225fe3c56aa750501247cebdc99b791f323c33d0d8fcd2ed7c4bafcb",
            ["ggml-cpu-icelake.dll"] = "0a35f7a655bb74ce2d64cbb64e6817b17f62964b060c6a503d3267b2133199bc",
            ["ggml-cpu-ivybridge.dll"] = "2e83b2aa5ad108445a599fcdd46fafcb227a3d2d66743994434b0bcaacb5a714",
            ["ggml-cpu-piledriver.dll"] = "851474816a574847b264e018de02410f5905359934c2fa2411f7ff3d7706620c",
            ["ggml-cpu-sandybridge.dll"] = "86b1c9114249492b8d39a1a3d9f9717c8969334a4bd12c279b91248a553cb254",
            ["ggml-cpu-sapphirerapids.dll"] = "b049b5672c3c246bb320ec011c735981628a9369687cfd64a4628c5bad238b3b",
            ["ggml-cpu-skylakex.dll"] = "183413744426f596c67fb206daf2cab17d666e22076e4a6b9a25d301de871d85",
            ["ggml-cpu-sse42.dll"] = "5b2f04b3ab1e76b09d887c496eb61b09a0ee4fc77b28c0602e45411906946ccd",
            ["ggml-cpu-x64.dll"] = "375434294cb7d22de0bf5da68fe67569fb5f8ca2c11e925850c8be3f52d052fe",
            ["ggml-cpu-zen4.dll"] = "dae5d12772f0213b6d70a3c4db032146b504dea32b1b35bbcc9dff769edf57f7",
            ["ggml-vulkan.dll"] = "9cc0d9416bf032caa58af66fc7fe700e35a476b74cab94fcb45e08f4eca8213c",
            ["ggml.dll"] = "92b081cfb031e2efe5cd2a8c71c872e8a92cb1d4a43619ad6614dc30ad357b80",
            ["libomp.dll"] = "a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c",
            ["llama-common.dll"] = "a0e0b993dede7c95eb1ca79cdcc2a003f2f79ff59fc129cb46ec5daac80fd057",
            ["llama-server-impl.dll"] = "c81c23738405831cacda737e64ce8f08870d4f44beb63e3aa9515b3ad2e4286e",
            ["llama-server.exe"] = "38c5affb81c9a8f88f5538c7d11bc445e13bc46a2c6eda05694b8b31a8d45995",
            ["llama.dll"] = "17b0063b06c4561f46929a809a60bf60c857783bb22f593528145cc1e1108d7d",
            ["mtmd.dll"] = "558d7c41475cc6f54d6da41eea23876cd35cfd2e9d8f2b3e637b8c49c9928ed5",
        },
        // Vendor-signed copies vpk leaves byte-identical (measured, release run 33588250383): the
        // Vulkan loader (installer/runtime/vulkan-loader/MANIFEST.psd1) and the three VC++ DLLs
        // the closure imports (installer/runtime/vcredist/MANIFEST.psd1).
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vulkan-1.dll"] = "cd862090370454630b31b174e3d4eb474fda38ea034998d1fe1767b0c99a8696",
            ["msvcp140.dll"] = "0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9",
            ["vcruntime140.dll"] = "d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066",
            ["vcruntime140_1.dll"] = "1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f",
        });

    /// <summary>The native ARM64 payload (upstream <c>win-opencl-adreno-arm64</c>, the same
    /// build): one CPU backend plus the OpenCL GPU backend, and ARM64 copies of the two VC++ DLLs
    /// its closure imports. No Vulkan loader — this build has no Vulkan backend. Pinned in
    /// <c>installer/runtime/llama-arm64/MANIFEST.psd1</c> and
    /// <c>installer/runtime/vcredist-arm64/MANIFEST.psd1</c>.</summary>
    internal static readonly LlamaPayloadSet Arm64 = new(
        "arm64",
        Path.Combine("runtimes", "win-arm64", "llama"),
        0xAA64,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ggml-base.dll"] = "7e793b53b14449ac19b0461a63c0ea22d02114c6c7f20811d7e7bad2bcf1efae",
            ["ggml-cpu.dll"] = "1a63d6dba3f9ffd3a636d41e2ab51aab413225e12dcaec2eb80b0608337bfbde",
            ["ggml-opencl.dll"] = "fb831667b31de70b578b2a186272590546a70755769dda205c8c9e5f33675986",
            ["ggml.dll"] = "53f924c2934455532cb24afbfd5686ece90f767fa1aacb6eae202c97377c9eb1",
            ["libomp.dll"] = "26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a",
            ["llama-common.dll"] = "2803620da257ab50c8d76ebf59be055ed21bb91ef8a1bad0df8aff2a7061747d",
            ["llama-server-impl.dll"] = "8388dc1a838223e6fefd7b3d6e70d86e03b2274b2573cdf5ac8f33b602b0877d",
            ["llama-server.exe"] = "c8ea3fa202d22cffff0f1c01320d38d9efba63d67ff078ca04613e07265a9f60",
            ["llama.dll"] = "cfc3a793e43a8e946dbfdb0d6c290604ad4eedf06f42d6462f1a8083a35d975f",
            ["mtmd.dll"] = "04e3fb881eebbc6ef301596ff5160a28e0ee41b9d4f5ccda01450fbb09177328",
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["msvcp140.dll"] = "045faeab0b5710816ae160ea20dae8495ffc11bb51feca15d22aaf7ff3752cb3",
            ["vcruntime140.dll"] = "d8a8513921544569837e400d37cc71302819967ae6defa932266a7ecb41dbaf9",
        });
}

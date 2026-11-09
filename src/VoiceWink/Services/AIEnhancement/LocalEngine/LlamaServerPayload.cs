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
    /// <summary>The upstream build tag; also what <c>--version</c> prints as <c>build 11960</c>.</summary>
    internal const string Build = "b11960";

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
            ["ggml-base.dll"] = "200cd9b6f0a7934c2e1c85e55289d5498332713321b03e83eb2ae3da5eeb6137",
            ["ggml-cpu-alderlake.dll"] = "8ae4c14de9eeb5528e42cff69ed8485e6cd0e3f8601837add873a9d12199ca95",
            ["ggml-cpu-cannonlake.dll"] = "779a9d0ede6215fba8a00fb680c1295dd62836f9d8a124437d48f9c086fc1f07",
            ["ggml-cpu-cascadelake.dll"] = "71f6275d8aa7df86545975e02510e10280abfcc274ae419dee898746a5b2ce1b",
            ["ggml-cpu-cooperlake.dll"] = "c0b3f9445cbd4490504c13d06e89b25bdbe9dea73784fc62c0c10ae7069de6cb",
            ["ggml-cpu-haswell.dll"] = "0bc8209ebe520b43d8549db7d45ca87382df88c3a8b238f3dda42d96e2af178e",
            ["ggml-cpu-icelake.dll"] = "81f86f655c82dfe330f9ee269b97a2b21352e5e876bd385d48bc61d9a6ca230b",
            ["ggml-cpu-ivybridge.dll"] = "52f262a610ac189d0375472c66c46b7b366d618916ed67a2b119e69023c3f943",
            ["ggml-cpu-piledriver.dll"] = "340c9dc4c5618fa4f89e6311c31f2f472d3277a87858c79b3c44890b0e92d1e7",
            ["ggml-cpu-sandybridge.dll"] = "6251d2dc50745bad1d0f16ea95a4484ef5d8c7cb49b5a4a33af85d199c08be61",
            ["ggml-cpu-sapphirerapids.dll"] = "cdfdc20cb7f953f87e61ab90e234c864df4e2c2615f5651bd83a4cf27fdc4e3d",
            ["ggml-cpu-skylakex.dll"] = "da5c7c86df8409c08fb748ed5596eba65285ead7747581daa124a7fff2f65704",
            ["ggml-cpu-sse42.dll"] = "00efeb321bb728616cde5719bab06ea575679f7ca4967367faa70fd5ca1c8ba6",
            ["ggml-cpu-x64.dll"] = "af7233344eec5587d531b4e875ec5bf2b1aa62c2be57b657f69cca503f0db918",
            ["ggml-cpu-zen4.dll"] = "4ebbee0eb0c5a4e9e3c3b437d7a2876ede8f1d3e00526dad33db50f39f46e831",
            ["ggml-vulkan.dll"] = "429b5dc6931cfc51121f05a43b4fb0b7dbf49d8c2ce38c0f72f848b308c592c8",
            ["ggml.dll"] = "d2e63157b90c0fd15f010fb2952d032d9f3296b8406b78ff0950211cc144705b",
            ["libomp.dll"] = "a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c",
            ["llama-common.dll"] = "34370fd48c31112b2ebe4e7bb234747c1a9b39325a29e61302659c525a6049d9",
            ["llama-server-impl.dll"] = "0b329dcfdece6a43f34d0554dec5ca0cc2efe6ee23d5aabd8141c51487668564",
            ["llama-server.exe"] = "81f65c6681d66099e5d48757aa2c050ba958dc43816e584f0bb4181eb7c910d8",
            ["llama.dll"] = "af13ed8cb1454a80689dbd390310995b6c4b23333e096152453fe6d0fef17356",
            ["mtmd.dll"] = "e82d563ae9dad225a3d15d0f189e046909713e26759ae9a046f89430bc845bd3",
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
            ["ggml-base.dll"] = "283b58d4c596ea42b5c98c8d1a0297ec6e8e66e84dbc750d0a0871e9544b2306",
            ["ggml-cpu.dll"] = "f4b6787aa170dd1c40731280f35a48c6e05d8ef51f4d3d6e05e698c55af7be72",
            ["ggml-opencl.dll"] = "11bce5320dec129cac2bcc1e326f8381811f0ca375da94a701340cd91a7044cb",
            ["ggml.dll"] = "735ab34555dd9684ed3dece1d8b5df2a7a42daa670467c20bf2028e833c40475",
            ["libomp.dll"] = "26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a",
            ["llama-common.dll"] = "b525708cc22578f0a34786de3cc7a72f0c9176d56308d3467e291d5dc78ef8fe",
            ["llama-server-impl.dll"] = "9a900b285330cd4cc1dc00d951c70e5a7c4ae26947284469fdda634c7e4f3cd0",
            ["llama-server.exe"] = "cf47d64d289d8a7696bdadd0177110f02c86e75e33a663254c456f642b523664",
            ["llama.dll"] = "6d1462b457e30f7abfccaf4aa32c83fbf314b0ddbef8e395d0d0bfbe1fd1179f",
            ["mtmd.dll"] = "5ea20332ac359887cad1a1e4bf7f0c9dc5a321aab055958a50f822750485d2d7",
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["msvcp140.dll"] = "045faeab0b5710816ae160ea20dae8495ffc11bb51feca15d22aaf7ff3752cb3",
            ["vcruntime140.dll"] = "d8a8513921544569837e400d37cc71302819967ae6defa932266a7ecb41dbaf9",
        });
}

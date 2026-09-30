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
    /// <summary>The upstream build tag; also what <c>--version</c> prints as <c>build 11147</c>.</summary>
    internal const string Build = "b11147";

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
            ["ggml-base.dll"] = "7ee3380f8d4758b518caefbe7b1cdcbf2c752d1bcb3a5823e837f24b55a7f99d",
            ["ggml-cpu-alderlake.dll"] = "832dfb28df9a5a4a15c982a4f286cfdcc2fb0220c57af102fb6cf0823f58df69",
            ["ggml-cpu-cannonlake.dll"] = "842aa91963d4259b0e6a406f9e55ce5c2fc43fff9c90885b01478d30cf77602d",
            ["ggml-cpu-cascadelake.dll"] = "042b9e375846a2e840c836a55ff2e60068398d4521d92ea231b4153d6fb9ad37",
            ["ggml-cpu-cooperlake.dll"] = "452bf574e28e003ea02786513efeebd3af049bbf3d332fb1f2f67f5f2527ea9f",
            ["ggml-cpu-haswell.dll"] = "b6ddbee3c5de7da4c844eeb7df74a4279db7eecff94786d81b833bae3415f5d5",
            ["ggml-cpu-icelake.dll"] = "3c9de8002008369f994a1048540187991ac83fc3daf29ddcbc97fa01808f44dd",
            ["ggml-cpu-ivybridge.dll"] = "7a32ff2606a652197ee76931d5c97b02d02e90e09d6c479e4d12a0561cfd8880",
            ["ggml-cpu-piledriver.dll"] = "375df17509f3a8ccbb8165e8d48ebe7086e13d10b7bba3f763fd30f01de04b26",
            ["ggml-cpu-sandybridge.dll"] = "ccd500e1639e931f93fa8b51f6b43f6a2ca95dd3c14243ee39d88cc5ac48df50",
            ["ggml-cpu-sapphirerapids.dll"] = "263c3fee6b7a4add9cc8fb9876c818cf0721c72dd8f9f8ecfbe58f64f837e5bd",
            ["ggml-cpu-skylakex.dll"] = "e7b742a3c4a5445f385882eaadb14abb6da28912c652a2530a5a4598152ea5d7",
            ["ggml-cpu-sse42.dll"] = "808c4c8abb9f99f496ac3ee69d72529b156663ce1c9c6789c463d68193f97c06",
            ["ggml-cpu-x64.dll"] = "095549b90f3562ef4690542f977bb3e62e25e531f1285e9d5c80a1958bf0c70b",
            ["ggml-cpu-zen4.dll"] = "8cd60877ec783e59ec84e851f8f6914f52445400d7014e2b67e7de954a44f8f6",
            ["ggml-vulkan.dll"] = "a1fa1db850faedc7b450a1d3b306bab4443c2c380839e3994be1bd390bc41613",
            ["ggml.dll"] = "b76c45a3e2493ab7e22487d9ab5217508fb452d983e2cc8b6a9dfd882a724a27",
            ["libomp.dll"] = "a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c",
            ["llama-common.dll"] = "bd24c02aabffbcb71bdc8870660631e6d27bcc0332972a7d4c023884b2223f4b",
            ["llama-server-impl.dll"] = "f3cebd952136b01171b3ab92d90064b3fa1ed2e00ba178f2ecd37828cd65c547",
            ["llama-server.exe"] = "a9b905492642e253f85347fa7166c614dedf44fcabe82550fd769425ef224a03",
            ["llama.dll"] = "8def2e0053d68d85c747492d12ac70e25ea673cd05e32f92a90e9834ffb95409",
            ["mtmd.dll"] = "227521be2d121edea8460ba650963e213847443ae6a0eccc6988d6bfaa9742ee",
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
            ["ggml-base.dll"] = "2d73e9f5a8009c8745318a1f2c3a8d50d903b256a58e0fdea184cc28cfb968f7",
            ["ggml-cpu.dll"] = "b0226196aab68a5106507d7c9002537e81f94b5062d8639948019ed70aafa81d",
            ["ggml-opencl.dll"] = "840c9bd737633479a880a5ee43200ca648f3a3e315a6174612127405605b81f3",
            ["ggml.dll"] = "0040d1da745e7182bae173084aeb85c3a806f25dfe3af207c79efb2c4e5101a0",
            ["libomp.dll"] = "26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a",
            ["llama-common.dll"] = "bd8aaea3f9a7c7e19aaa1e0b6c812ea3ae2e2e5f36fb3f23366cf9520ee1c4e2",
            ["llama-server-impl.dll"] = "d006a8a295c7f125f8d5722219f0a2128a98a274027adfea130273c1a3b78175",
            ["llama-server.exe"] = "d8f87f3843ac45ff5a1eba107e19b0d9e525d8e1885bdfacee8faad0eaeb2203",
            ["llama.dll"] = "9e01fb1a675ce8102b2f573c54a5386551973a4f822dde86a67628ae2c983b81",
            ["mtmd.dll"] = "44057cd1a0a04accfa4a807dceb2631c367bd908fb500245adc02381a02bd124",
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["msvcp140.dll"] = "045faeab0b5710816ae160ea20dae8495ffc11bb51feca15d22aaf7ff3752cb3",
            ["vcruntime140.dll"] = "d8a8513921544569837e400d37cc71302819967ae6defa932266a7ecb41dbaf9",
        });
}

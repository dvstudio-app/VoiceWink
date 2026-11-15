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
    /// <summary>The upstream build tag; also what <c>--version</c> prints as <c>build 12110</c>.</summary>
    internal const string Build = "b12110";

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
            ["ggml-base.dll"] = "3a0399fa51102a86824255beda9d021ea7e8cd2bd8ca448a43a9a80aa4829290",
            ["ggml-cpu-alderlake.dll"] = "1de7a0d14d70bd741f2e4cdfa959176fee26680d6fbc78c717c0bd19e20b6233",
            ["ggml-cpu-cannonlake.dll"] = "1451909b84c223739247088a0e03db0f89bcd83461f2af43f53d794727afddcd",
            ["ggml-cpu-cascadelake.dll"] = "74d79278bd20143d3e7deeb2b5c033555f9d621016a7152b5190a2fcbf30265c",
            ["ggml-cpu-cooperlake.dll"] = "737f0e96f23ca7d66c8dc55eace36ebbfd47770a9a228c804ffff5bf66f933a8",
            ["ggml-cpu-haswell.dll"] = "85d9ffd6f84fd0ec01586420b935ac4d5a5c9a1ab76b533eb3ddc060c1fe2869",
            ["ggml-cpu-icelake.dll"] = "41ca727e74b171983ea9afe0250553706c09225c10a8c6ad72a619f9189279a8",
            ["ggml-cpu-ivybridge.dll"] = "39752b7667cb34b3bbd06178fac83803f6089771d43873a9c81015d2a98c280b",
            ["ggml-cpu-piledriver.dll"] = "65bef2ae4f69256d9aff053a6fff1abdbd33acdbc51ede863d8bc5bd43c53ee8",
            ["ggml-cpu-sandybridge.dll"] = "5359624dee5670cb299c08f4a49347f7a6f4d0df35ed4c0a91164e4d771b3e23",
            ["ggml-cpu-sapphirerapids.dll"] = "a7a395d0e49f182240c9a2ed1d623398911506bdae418baf161354541aa06a89",
            ["ggml-cpu-skylakex.dll"] = "6bd53dc5731c5b9e4d3fbf98a9dc5b3ce76d357be9a579bc6c8cedac035462e8",
            ["ggml-cpu-sse42.dll"] = "1e1700e6b6eef26b6abb0bc68669b6ee9fdad2b8d00058e491cb5160eb54afc2",
            ["ggml-cpu-x64.dll"] = "81877c8787f9d5680b990f4cf5f8b89ba5b02ee5064b36abfd53d9d2ffb50916",
            ["ggml-cpu-zen4.dll"] = "b5c1eec79e83878303acfa774f1275d9e025493b2c23f2a01af09fe985cdc21a",
            ["ggml-vulkan.dll"] = "e863e7556e10eeea6888894755552a917ab919004882bdf89d2d684d2b54fc8c",
            ["ggml.dll"] = "06dfdff8b7b834c757ec5b5411abfc0f732604fb595eaaffbdc6da563523e8a1",
            ["libomp.dll"] = "a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c",
            ["llama-common.dll"] = "0898e517843e4478b5178621dc878552b4d40a4d47d7b6f1775e7897f4e2344e",
            ["llama-server-impl.dll"] = "c5d306bdcad1c4011f18a1c01ae2fcdca48a197af0d421c2931ace2cfb487fc6",
            ["llama-server.exe"] = "00d50686815af38249d0218c91558fbd0e9093ac8c074bac86196907f4305e31",
            ["llama.dll"] = "211b91d86e67c49f12eb97992b45ae4c2fc797d6edab48637835a877c808890e",
            ["mtmd.dll"] = "74a3cace50f6c545cc66e4dc05e2aecf465f146ea1b4000376cd9834598a3e14",
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
            ["ggml-base.dll"] = "da51a68ad763cb16c9f4f9cd76ee90918b3d1e6a898b21e44947b2a6a18fb5be",
            ["ggml-cpu.dll"] = "7dda802d308c4c05b4b0654e74dbeb0942947da386924c5d5f298a6bd0f6b67b",
            ["ggml-opencl.dll"] = "340a96c9d7ce6e2f78a09c40110ec70d2bc70bb21733c952ac08273810443918",
            ["ggml.dll"] = "84f5f975819a60cd15a0033f4361fdac8fea111301addbe0081b356dad3b22e3",
            ["libomp.dll"] = "26caae17f29aaf2238f664375b663cd306d596bc9e36e780fa88356c51fe876a",
            ["llama-common.dll"] = "eaa4888f9b9685ce8b652fbaa0b3c1b63e6e051648086b9a2fad613074cde406",
            ["llama-server-impl.dll"] = "64d4b18e467d49279b885b0ec3f16d8963d709200e8078cf666c68521fa72071",
            ["llama-server.exe"] = "d6908c01d6b7767a1122a157e9bdf24a1412f270ef23cd9af2d7c3a07a74e753",
            ["llama.dll"] = "9def90ce6d5de42fc97d89cd8dc47a7694b02dbbe617196d2d8e5681dc25e47a",
            ["mtmd.dll"] = "4c1239fdc92de05ef2332365f1211a1ca77f08a922e2c901e2f09dec314c5a1e",
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["msvcp140.dll"] = "045faeab0b5710816ae160ea20dae8495ffc11bb51feca15d22aaf7ff3752cb3",
            ["vcruntime140.dll"] = "d8a8513921544569837e400d37cc71302819967ae6defa932266a7ecb41dbaf9",
        });
}

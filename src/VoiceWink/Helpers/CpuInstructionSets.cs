using System.Runtime.Intrinsics.X86;
using System.Text;

namespace VoiceWink.Helpers;

/// <summary>
/// Which x86 instruction-set extensions this processor has, and its brand string — the facts that
/// decide whether a prebuilt native engine can run here at all.
///
/// <para>Exists because a native engine built for a newer instruction set does not fail with an
/// exception on an older processor: it executes an instruction the CPU does not have and the
/// process dies (<c>0xC000001D</c>). VOICEWINK-J/K/M (2026-10-15) is the case that added this: the
/// parakeet-server child died a few seconds after launch in GPU mode and again in the CPU retry,
/// on a PC whose report said Windows, x64 and an AMD RX 580 — and nothing said which extensions the
/// processor had, so the likeliest cause could not be confirmed. The session banner and the engine's
/// failure report both carry <see cref="Render"/> now.</para>
///
/// <para>The F16C test is the CPUID(1).ECX bit 29 read <c>VoiceActivityDetectionService</c> makes
/// for the same reason; .NET exposes no <c>IsSupported</c> for it.</para>
///
/// <para>Since TRN-79 these facts also DECIDE: <see cref="EngineCpuRequirements"/> reads them to say
/// which local engines this processor can run. <c>Bmi2</c> was added then, last and defaulted so the
/// diagnostic call sites kept compiling: <c>parakeet-server</c> is built with it.</para>
/// </summary>
internal readonly record struct CpuInstructionSets(
    bool X86, bool Sse42, bool Avx, bool Avx2, bool Fma, bool F16c, bool Avx512F, string Brand, bool Bmi2 = false)
{
    /// <summary>The longest brand string kept. CPUID's is at most 48 characters.</summary>
    internal const int MaxBrandLength = 64;

    private static readonly Lazy<CpuInstructionSets> CurrentLazy = new(Read, isThreadSafe: true);

    /// <summary>This process's processor, read once (it cannot change within a process).</summary>
    public static CpuInstructionSets Current => CurrentLazy.Value;

    /// <summary>One compact token list: each extension present, or <c>-name</c> when absent, then
    /// the brand in quotes — <c>sse4.2 avx -avx2 -fma f16c -bmi2 -avx512f "Intel(R) Xeon(R) CPU E5-2650 v2"</c>.
    /// A non-x86 process (native arm64) renders <c>not-x86</c>.</summary>
    public string Render()
    {
        var extensions = RenderExtensions();
        return Brand.Length == 0 ? extensions : $"{extensions} \"{Brand}\"";
    }

    /// <summary>The extension tokens alone (<c>sse4.2 avx -avx2 …</c>, or <c>not-x86</c>) — the
    /// Sentry tag, where the brand rides its own tag.</summary>
    public string RenderExtensions()
    {
        if (!X86) return "not-x86";

        var b = new StringBuilder();
        Flag(b, "sse4.2", Sse42);
        Flag(b, "avx", Avx);
        Flag(b, "avx2", Avx2);
        Flag(b, "fma", Fma);
        Flag(b, "f16c", F16c);
        Flag(b, "bmi2", Bmi2);
        Flag(b, "avx512f", Avx512F);
        return b.ToString();

        static void Flag(StringBuilder b, string name, bool present)
        {
            if (b.Length > 0) b.Append(' ');
            if (!present) b.Append('-');
            b.Append(name);
        }
    }

    /// <summary>The brand string as it may be logged: one line, quotes removed, bounded, trimmed.
    /// CPUID pads it with spaces and NULs; the value is hardware model text, the same class as the
    /// GPU name the device lines already log.</summary>
    internal static string CleanBrand(string? raw)
    {
        // SingleLine turns NULs and every other control or space run into one space.
        if (string.IsNullOrWhiteSpace(raw?.Replace('\0', ' '))) return "";
        var cleaned = LogValueSanitizer.SingleLine(raw!.Replace('"', '\'')).Trim();
        return cleaned.Length <= MaxBrandLength ? cleaned : cleaned[..MaxBrandLength].TrimEnd();
    }

    private static CpuInstructionSets Read()
    {
        try
        {
            if (!X86Base.IsSupported)
                return new CpuInstructionSets(false, false, false, false, false, false, false, "");

            var (_, _, ecx, _) = X86Base.CpuId(1, 0);
            return new CpuInstructionSets(
                X86: true,
                Sse42: global::System.Runtime.Intrinsics.X86.Sse42.IsSupported,
                Avx: global::System.Runtime.Intrinsics.X86.Avx.IsSupported,
                Avx2: global::System.Runtime.Intrinsics.X86.Avx2.IsSupported,
                Fma: global::System.Runtime.Intrinsics.X86.Fma.IsSupported,
                F16c: (ecx & (1 << 29)) != 0,
                Avx512F: global::System.Runtime.Intrinsics.X86.Avx512F.IsSupported,
                Brand: CleanBrand(ReadBrand()),
                Bmi2: global::System.Runtime.Intrinsics.X86.Bmi2.IsSupported);
        }
        catch
        {
            // A diagnostic must never take a caller down.
            return new CpuInstructionSets(false, false, false, false, false, false, false, "");
        }
    }

    /// <summary>CPUID leaves 0x80000002–4: 48 bytes of ASCII, when the processor reports them.</summary>
    private static string ReadBrand()
    {
        var (maxExtended, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
        if (unchecked((uint)maxExtended) < 0x80000004) return "";

        var bytes = new byte[48];
        for (var i = 0; i < 3; i++)
        {
            var (a, b, c, d) = X86Base.CpuId(unchecked((int)(0x80000002 + (uint)i)), 0);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 16, 4), a);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 4, 4), b);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 8, 4), c);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 12, 4), d);
        }
        return Encoding.ASCII.GetString(bytes);
    }
}

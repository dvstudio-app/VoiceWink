using Microsoft.Win32;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// Can this ARM64 PC's processor run the native ARM64 llama-server payload at all?
///
/// <para>Upstream compiles its one ARM64 Windows build with <c>-march=armv8.7-a</c>
/// (<c>cmake/arm64-windows-llvm.cmake</c> at the pinned tag), and unlike the x64 payload it ships
/// ONE CPU backend with no per-CPU variants. The pinned <c>ggml-cpu.dll</c> carries the 8-bit
/// integer matrix-multiply instructions (260 SMMLA and 4 USDOT encodings in its code section,
/// counted 2026-09-30) with no runtime guard. A Snapdragon X processor has them; the ARM64 Windows
/// PCs before it (Snapdragon 8cx and 7c generations, the Microsoft SQ chips) do not, and there the
/// native child would die on an illegal instruction in its CPU backend - in CPU mode too. Those
/// PCs keep the emulated x64 payload, whose CPU backend picks a variant by what the processor
/// reports.</para>
///
/// <para>This is a capability the binary requires, read from the processor - the same kind of
/// decision ggml makes itself on x64 - not a rule about a brand or a model. It reads the
/// instruction-set register Windows publishes per processor in the registry, because a process
/// running under x64 emulation cannot ask the processor directly. <b>Unreadable means "not
/// supported"</b>: the emulated payload is slower and known to work, the native one on the wrong
/// processor does not work at all.</para>
/// </summary>
internal static class LlamaArm64Cpu
{
    /// <summary>Windows' name for ID_AA64ISAR1_EL1 under each <c>CentralProcessor\N</c> key.</summary>
    internal const string Isar1ValueName = "CP 4031";

    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    /// <summary>The pure decision over the register: its I8MM field (bits 55:52) is non-zero.
    /// <c>null</c> (no value, not an ARM64 PC, unreadable) is "not supported".</summary>
    internal static bool SupportsNativePayload(ulong? isar1) => isar1 is ulong value && ((value >> 52) & 0xF) != 0;

    /// <summary>Processor 0's register, or <c>null</c> when it cannot be read. Never throws.</summary>
    internal static ulong? ReadIsar1()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(ProcessorKey);
            return key?.GetValue(Isar1ValueName) switch
            {
                long q => unchecked((ulong)q),
                byte[] { Length: 8 } bytes => BitConverter.ToUInt64(bytes, 0),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is global::System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

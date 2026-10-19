using System.Globalization;

namespace VoiceWink.Helpers;

/// <summary>
/// A child process's exit code as a support case reads it: the code in hex plus a short name for
/// the handful of values that tell crash classes apart.
///
/// <para>Exists because a local engine that dies before it is healthy used to leave only "exited
/// before health" behind (VOICEWINK-J/K/M, 2026-10-15: the parakeet-server child died a few seconds
/// after launch in GPU mode AND in the CPU retry, and nothing said why). The exit code is what
/// separates a processor missing an instruction set from a missing DLL, an out-of-memory kill or a
/// native assertion — four different fixes. Names only; nothing the child printed is involved.</para>
/// </summary>
internal static class NtStatusName
{
    /// <summary>The short name for <paramref name="code"/>, or <c>unknown</c>.</summary>
    public static string Name(uint code) => code switch
    {
        0 => "success",
        1 => "error-exit",
        0xC000001D => "illegal-instruction",
        0xC0000005 => "access-violation",
        0xC0000135 => "dll-not-found",
        0xC0000139 => "entry-point-not-found",
        0xC0000142 => "dll-init-failed",
        0xC0000409 => "fail-fast",
        0xC0000017 => "no-memory",
        0xC000012D => "commit-limit",
        0xC00000FD => "stack-overflow",
        0xC000013A => "ctrl-c",
        0xE06D7363 => "cpp-exception",
        0x40010004 => "debugger-terminated",
        0xFFFFFFFF => "minus-one",
        _ => "unknown",
    };

    /// <summary><c>0xC000001D illegal-instruction</c> — the form every log line uses.</summary>
    public static string Describe(uint code)
        => "0x" + code.ToString("X8", CultureInfo.InvariantCulture) + " " + Name(code);

    /// <summary>As <see cref="Describe(uint)"/>, or <c>unknown</c> when Windows gave no code (the
    /// child still running, or its handle already closed).</summary>
    public static string Describe(uint? code) => code is { } c ? Describe(c) : "unknown";
}

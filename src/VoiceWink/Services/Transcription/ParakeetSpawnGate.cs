using System.Security.Cryptography;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// TRN-51: the ONE provenance decision for spawning a `parakeet-server.exe`, extracted from
/// `ParakeetServerProcess` so that EVERY spawn path is subject to it.
///
/// <para><b>Why this is its own type — the storm fuse and the provenance gate are unrelated
/// concerns that used to cohabit one class.</b> TRN-49's warm-up child must be invisible to the
/// FUSE (an optional latency optimisation must never degrade capability), and the first design
/// achieved that by bypassing `ParakeetServerProcess` — which ALSO bypassed the TRN-34
/// hash-or-Authenticode gate, whose only call site lived inside that class: a fail-open on a
/// user-writable exe (Velopack installs per-user under <c>%LOCALAPPDATA%</c>), caught by the
/// laptop session BEFORE the diff existed. Escaping the fuse must never escape the gate; this
/// extraction makes the gate a property of the SPAWN rather than of one call path. The next
/// person who routes around `ParakeetServerProcess` for a good reason inherits the gate by
/// calling this.</para>
///
/// <para><b>Re-verified per spawn, never cached</b> — a cached app-start verdict would widen the
/// check-to-spawn window the per-launch design deliberately keeps small. Hash FIRST, Authenticode
/// only on mismatch (Kimi diff r1 A3 on TRN-34: dev/CI builds match the pin and never pay a
/// native trust evaluation; the AIA chain-build stall documented on `AuthenticodeSignature` can
/// only arise for a signed file, which is exactly the case where the hash cannot match anyway).</para>
///
/// <para><b>Threat model, stated once so the two class docs stop disagreeing:</b> this gate is an
/// INTEGRITY check against a swapped, corrupted, or half-updated file — `AuthenticodeSignature`'s
/// framing, which governs. It does not and cannot defend against an adversary already running
/// code as the user: under a per-user install that adversary can replace any unverified DLL
/// beside the exe (or the app's own assemblies), so a check-to-spawn TOCTOU hold or a runtime
/// hash of every sibling DLL would harden against a party who has already won. "User-writable
/// exe" in the paragraph above explains why the check exists at all (files change legitimately —
/// updates, AV quarantine, partial writes), not who it is armored against.</para>
/// </summary>
public static class ParakeetSpawnGate
{
    /// <summary>The gate's verdict: spawnable, or the refusal reason for the caller's log line
    /// (reason text comes from <see cref="ParakeetServerProvenance.DescribeRefusal"/>, which
    /// names BOTH branches — hash mismatch AND signature verdict — because a single-branch
    /// message hid a systematic release defect for a week in TRN-34; or, for a file that could
    /// not be read, from <see cref="ParakeetServerProvenance.DescribeUnreadable"/>).</summary>
    public readonly record struct Verdict(bool Spawnable, string? RefusalReason, string? RefusalKind = null);

    /// <summary>The refusal kind of a file whose open was REFUSED, so neither its hash nor its
    /// bytes were judged (2026-10-21: a Microsoft Defender attack-surface-reduction rule refused
    /// every process access to the newly bundled exe, and the pill called the intact file damaged).</summary>
    internal const string UnreadableKind = "unreadable";

    /// <summary>What reading a file against its pin found.</summary>
    internal enum HashCheck
    {
        Match,
        Mismatch,
        /// <summary>The file is not there.</summary>
        Missing,
        /// <summary>Opening the file was refused — access denied, a sharing or lock violation, a
        /// policy or antivirus block (<see cref="RefusedOpenHResults"/>). Nothing is known about
        /// its bytes, and a reinstall writes the same bytes the refusal meets again.</summary>
        Unreadable,
        /// <summary>Any other I/O error while opening or reading (a disk read error, a dropped
        /// network path). The file may really be damaged, so it is refused like a mismatch.</summary>
        ReadFailed,
    }

    /// <summary>The <see cref="IOException"/> HResults that mean "the open was refused", not "the
    /// read failed": sharing violation, lock violation, virus infected, virus deleted, blocked by
    /// group policy. Access denied arrives as UnauthorizedAccessException — which is what Defender's
    /// ASR block raised, measured on the affected laptop 2026-10-21 (0x80070005; File.Exists still
    /// true). The refusal line logs the exact code either way.</summary>
    private static readonly int[] RefusedOpenHResults =
        [unchecked((int)0x80070020), unchecked((int)0x80070021), unchecked((int)0x800700E1),
         unchecked((int)0x800700E2), unchecked((int)0x800704EC)];

    /// <summary><paramref name="OpenError"/> names the failed open or read (exception type + HResult)
    /// for the local log; null unless <see cref="HashCheck.Unreadable"/> or <see cref="HashCheck.ReadFailed"/>.</summary>
    internal readonly record struct HashResult(HashCheck Check, string? OpenError = null);

    /// <summary>The refusal as a fixed token — <c>unsigned</c>, <c>signature-invalid</c> or
    /// <c>wrong-signer</c> (or <see cref="UnreadableKind"/>, decided in <see cref="Check"/>) — for the
    /// once-per-session Sentry report (2026-10-17): the full reason
    /// embeds the certificate SUBJECT, text whoever made the certificate controls.</summary>
    internal static string KindOf(AuthenticodeSignature.Verdict signature)
        => (signature.IsSignatureValid, signature.SignerSubject) switch
        {
            (false, null) => "unsigned",
            (false, _) => "signature-invalid",
            _ => "wrong-signer",
        };

    /// <summary>
    /// May this exe spawn? <paramref name="verifySignature"/> is the TRN-34 Authenticode seam
    /// (tests cannot produce a file signed by our certificate); production callers pass null and
    /// get the real verifier.
    /// </summary>
    public static Verdict Check(
        string exePath,
        string expectedSha256,
        Func<string, AuthenticodeSignature.Verdict>? verifySignature = null)
    {
        var hash = CheckHash(exePath, expectedSha256);
        var hashMatches = hash.Check == HashCheck.Match;
        var signature = hashMatches
            ? AuthenticodeSignature.Verdict.Unsigned // not consulted; kept out of the log line
            : (verifySignature ?? AuthenticodeSignature.Verify)(exePath);
        return Decide(hash, signature);
    }

    /// <summary>The verdict from the hash outcome and the signature (not consulted on a match).</summary>
    internal static Verdict Decide(HashResult hash, AuthenticodeSignature.Verdict signature)
    {
        if (ParakeetServerProvenance.IsTrusted(hash.Check == HashCheck.Match, signature)) return new Verdict(true, null);

        // Still fail closed — an unreadable file never spawns — but a REFUSED open gets its own
        // kind: "damaged — reinstall" repairs nothing when something is refusing the open. Any
        // other read error keeps the damaged path; the reason names the error either way.
        return hash.Check switch
        {
            HashCheck.Unreadable => new Verdict(false,
                ParakeetServerProvenance.DescribeUnreadable(hash.OpenError, signature), UnreadableKind),
            HashCheck.ReadFailed => new Verdict(false,
                ParakeetServerProvenance.DescribeUnreadable(hash.OpenError, signature), KindOf(signature)),
            _ => new Verdict(false, ParakeetServerProvenance.DescribeRefusal(signature), KindOf(signature)),
        };
    }

    /// <summary>Hashes the file against its pin (TRN-29 slice 2, moved from `ParakeetServerProcess`).
    /// Any outcome but <see cref="HashCheck.Match"/> is unverified; the caller fails closed.</summary>
    internal static HashResult CheckHash(string path, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return new(string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? HashCheck.Match
                : HashCheck.Mismatch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var check = ClassifyReadFailure(ex);
            return new(check, check == HashCheck.Missing ? null : $"{ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    /// <summary>Which outcome a failed open or read is: Missing, Unreadable (a refused open) or ReadFailed.</summary>
    internal static HashCheck ClassifyReadFailure(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => HashCheck.Missing,
        UnauthorizedAccessException => HashCheck.Unreadable,
        _ when RefusedOpenHResults.Contains(ex.HResult) => HashCheck.Unreadable,
        _ => HashCheck.ReadFailed,
    };
}

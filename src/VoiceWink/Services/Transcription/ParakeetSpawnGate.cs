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
    /// message hid a systematic release defect for a week in TRN-34).</summary>
    public readonly record struct Verdict(bool Spawnable, string? RefusalReason);

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
        var hashMatches = HashMatches(exePath, expectedSha256);
        var signature = hashMatches
            ? AuthenticodeSignature.Verdict.Unsigned // not consulted; kept out of the log line
            : (verifySignature ?? AuthenticodeSignature.Verify)(exePath);
        return ParakeetServerProvenance.IsTrusted(hashMatches, signature)
            ? new Verdict(true, null)
            : new Verdict(false, ParakeetServerProvenance.DescribeRefusal(signature));
    }

    /// <summary>Moved verbatim from `ParakeetServerProcess` (TRN-29 slice 2): unreadable =
    /// unverifiable = refuse, fail closed.</summary>
    internal static bool HashMatches(string exePath, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(exePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

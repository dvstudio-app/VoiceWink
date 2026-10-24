using VoiceWink.Helpers;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>
/// LAI-2: may the bundled llama-server spawn? The <see cref="ParakeetSpawnGate"/> rule, applied
/// to a payload that is NOT one binary: <c>llama-server.exe</c> is a 9 KB stub and the code lives
/// in 22 DLLs beside it, so checking the exe alone would verify nothing. Three conditions, all
/// required, re-verified per spawn and never cached (ParakeetSpawnGate's reason: a cached verdict
/// widens the check-to-spawn window):
///
/// <list type="number">
/// <item><b>Exactly the pinned PE set.</b> Every <c>*.dll</c> / <c>*.exe</c> in the directory is
/// one <see cref="LlamaServerPayload"/> names, and every name is present. An extra DLL is refused,
/// not ignored: ggml's backend loader maps every <c>ggml-*.dll</c> in the exe directory, so a
/// stray file is code the child would run.</item>
/// <item><b>Each upstream-unsigned file</b> matches its pinned hash (dev and CI builds) OR carries
/// a valid DV Studio signature (a signed release, where vpk's signing changed the bytes) —
/// <see cref="ParakeetServerProvenance.IsTrusted"/>, the ONE provenance rule, per file. Hash
/// first; the Authenticode evaluation runs only on a mismatch (the ParakeetSpawnGate order).</item>
/// <item><b>Each vendor-signed copy</b> (the Vulkan loader, the three VC++ DLLs) matches its
/// pinned hash in every build — vpk leaves validly signed files byte-identical.</item>
/// </list>
///
/// <para>Threat model: ParakeetSpawnGate's, which governs — an INTEGRITY check against a
/// half-updated, corrupted or AV-quarantined payload, not a defence against code already running
/// as the user. For this payload the realistic failure is a missing or partly written DLL, which
/// is exactly why every file is checked.</para>
/// </summary>
internal static class LlamaSpawnGate
{
    /// <summary>Spawnable, or why not. The reason names the FILE and, for a signed-at-pack file,
    /// both proofs (<see cref="ParakeetServerProvenance.DescribeRefusal"/>, or the read error for a file that cannot be read); app-authored text, a
    /// file name and a certificate subject only — never a path. <paramref name="RefusalKind"/> is the
    /// refusal as a fixed token (the constants below, or <see cref="ParakeetSpawnGate"/>'s for a
    /// signed-at-pack file) — what the once-per-run Sentry report is keyed and labelled by, since the
    /// reason is redacted there.</summary>
    internal readonly record struct Verdict(bool Spawnable, string? RefusalReason, string? RefusalKind = null);

    internal const string DirectoryUnreadableKind = "directory-unreadable";
    internal const string ExtraFileKind = "extra-file";
    internal const string MissingKind = "missing";
    internal const string HashMismatchKind = "hash-mismatch";
    internal const string ReadFailedKind = "read-failed";
    internal const string WrongArchitectureKind = "wrong-architecture";

    /// <summary>The rule over ONE payload set's own pins (LAI-10: x64 or ARM64 — a directory is
    /// never checked against the other set's tables).</summary>
    internal static Verdict Check(
        string payloadDirectory,
        LlamaPayloadSet payload,
        Func<string, AuthenticodeSignature.Verdict>? verifySignature = null)
    {
        var verdict = Check(payloadDirectory, payload.SignedAtPack, payload.PinnedCopies, verifySignature);
        if (!verdict.Spawnable) return verdict;

        // Every file is this set's architecture, whatever proved its provenance: a file that
        // passed on OUR signature (a signed release) has no hash binding it to this set.
        foreach (var name in payload.SignedAtPack.Keys.Concat(payload.PinnedCopies.Keys))
        {
            switch (HasPeMachine(Path.Combine(payloadDirectory, name), payload.PeMachine))
            {
                case null:
                    return new Verdict(false, $"{name}: the file cannot be read", ParakeetSpawnGate.UnreadableKind);
                case false:
                    return new Verdict(false, $"{name}: not a {payload.Name} file (PE machine is not 0x{payload.PeMachine:X4})", WrongArchitectureKind);
            }
        }
        return verdict;
    }

    /// <summary>Null when the file cannot be read — refused, but not a wrong-architecture file.</summary>
    private static bool? HasPeMachine(string path, ushort expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 64 || peOffset > stream.Length - 6) return false;
            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The rule over explicit pin tables — the test seam (a test directory of small files
    /// with their own hashes); production passes <see cref="LlamaServerPayload"/>'s.</summary>
    internal static Verdict Check(
        string payloadDirectory,
        IReadOnlyDictionary<string, string> signedAtPack,
        IReadOnlyDictionary<string, string> pinnedCopies,
        Func<string, AuthenticodeSignature.Verdict>? verifySignature)
    {
        string[] present;
        try
        {
            present = Directory.EnumerateFiles(payloadDirectory)
                .Where(IsPe)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Verdict(false, "the payload directory cannot be read", DirectoryUnreadableKind);
        }

        foreach (var name in present)
        {
            if (!signedAtPack.ContainsKey(name) && !pinnedCopies.ContainsKey(name))
            {
                return new Verdict(false, $"{name} is not part of the pinned payload", ExtraFileKind);
            }
        }

        var presentSet = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        foreach (var name in signedAtPack.Keys.Concat(pinnedCopies.Keys))
        {
            if (!presentSet.Contains(name))
            {
                return new Verdict(false, $"{name} is missing", MissingKind);
            }
        }

        foreach (var (name, sha) in pinnedCopies)
        {
            var hash = ParakeetSpawnGate.CheckHash(Path.Combine(payloadDirectory, name), sha);
            switch (hash.Check)
            {
                case ParakeetSpawnGate.HashCheck.Match:
                    continue;
                case ParakeetSpawnGate.HashCheck.Missing:
                    return new Verdict(false, $"{name} is missing", MissingKind);
                case ParakeetSpawnGate.HashCheck.Unreadable:
                    return new Verdict(false, $"{name}: the file cannot be read ({hash.OpenError})", ParakeetSpawnGate.UnreadableKind);
                case ParakeetSpawnGate.HashCheck.ReadFailed:
                    return new Verdict(false, $"{name}: the file cannot be read ({hash.OpenError})", ReadFailedKind);
                default:
                    return new Verdict(false, $"{name}: hash does not match the pinned copy", HashMismatchKind);
            }
        }

        // The Parakeet rule per file — hash first, signature on anything else, an unreadable file
        // named as such rather than as a hash mismatch.
        foreach (var (name, sha) in signedAtPack)
        {
            var file = ParakeetSpawnGate.Check(Path.Combine(payloadDirectory, name), sha, verifySignature);
            if (!file.Spawnable)
            {
                return new Verdict(false, $"{name}: {file.RefusalReason}", file.RefusalKind);
            }
        }

        return new Verdict(true, null);
    }

    private static bool IsPe(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }
}

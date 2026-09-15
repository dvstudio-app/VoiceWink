namespace VoiceWink.Helpers;

/// <summary>
/// TRN-34: may this <c>parakeet-server.exe</c> be spawned? The gate's purpose is unchanged from
/// TRN-29 — "a wrong binary never spawns" — but it now accepts the TWO proofs that actually occur,
/// instead of the one that only occurs before release.
///
/// <para><b>The defect this replaces.</b> The launch gate compared SHA-256 against a compile-time
/// pin taken from the UNSIGNED vendored binary, while the post-pack signing gate
/// (<c>check-pack-signatures</c>) requires the shipped copy to be SIGNED (SGN-2, after a Bitdefender detection). Signing changes
/// the bytes, so no signed release could ever satisfy the pin: 30 refusals and zero spawns in the
/// owner's logs, every dictation silently falling back to sherpa — and on a FRESH install, where
/// <c>PCPP_ENABLED</c> means the sherpa bundle is never downloaded, falling back to nothing at all
/// (<c>PcppPrepareOutcome.Unavailable</c>).</para>
///
/// <para><b>Why it hid.</b> It is present exactly where it is never tested and absent exactly where
/// it is: dev builds and CI are unsigned and pass; only a signed release fails. No local run and no
/// CI job could surface it.</para>
///
/// <para><b>Why not pin the signed hash instead.</b> Impossible, and the repo already recorded why
/// (the post-pack signing gate, <c>check-pack-signatures</c>): timestamped signatures are non-deterministic, so the shipped
/// bytes differ on every signing run. There is no signed value to pin.</para>
///
/// <para><b>Why not verify only the signature.</b> That inverts the defect rather than fixing it —
/// dev builds carry no signature, so the engine would die in development instead of production.
/// The two-branch form is what makes the check pass in BOTH environments, which is the actual
/// requirement.</para>
///
/// <para><b>The "either" does not weaken provenance.</b> The only thing the hash branch admits
/// beyond the signature branch is our own vendored binary at one pinned hash — the same code the
/// signed branch admits, minus the signature. It is not a general escape: an arbitrary unsigned
/// binary matches neither branch.</para>
/// </summary>
public static class ParakeetServerProvenance
{
    /// <summary>
    /// The certificate common name every shipped copy must carry, compared WHOLE — never as a
    /// prefix or substring, because <c>DV Studio Tools</c> is a different company that could obtain
    /// its own trusted certificate (Codex diff r1 Blocker).
    ///
    /// <para>Deliberately the SAME value as the post-pack signing gate's (<c>check-pack-signatures</c>)
    /// <c>$script:ExpectedSignerCommonName</c>, and <c>ParakeetServerProvenanceTests</c> asserts the
    /// two agree by reading that script. That test is the durable half of this fix: a signer change
    /// that updated the pack gate alone would produce a release that passes its own signing gate and
    /// then refuses every spawn — this defect exactly, one identity over. The pin file
    /// <c>installer/runtime/parakeet/EXPECTED_SHA256</c> is already tied to the hash constant the
    /// same way by <c>PcppFeatureGateTests</c>; this closes the matching hole on the signer.</para>
    /// </summary>
    public const string ExpectedSignerCommonName = "DV Studio";

    /// <summary>
    /// The decision, over verdicts the caller supplies. Both inputs are parameters so every
    /// combination is testable without a file signed by our certificate — which cannot exist on a
    /// dev machine or in CI, and is precisely why the original defect went unnoticed.
    /// </summary>
    public static bool IsTrusted(bool hashMatchesPin, AuthenticodeSignature.Verdict signature)
        => hashMatchesPin || signature.IsTrusted(ExpectedSignerCommonName);

    /// <summary>
    /// Why a spawn was refused, for the log. The old line said only "executable hash mismatch",
    /// which is what made this defect read as corruption for a week — it named the branch that
    /// failed while staying silent about the branch that was supposed to succeed. This names both.
    /// App-authored text plus a certificate subject: no file contents, no paths.
    /// </summary>
    public static string DescribeRefusal(AuthenticodeSignature.Verdict signature)
        => $"hash does not match the pinned build AND {signature.Describe()} "
           + $"(a shipped copy must be signed by CN={ExpectedSignerCommonName})";
}

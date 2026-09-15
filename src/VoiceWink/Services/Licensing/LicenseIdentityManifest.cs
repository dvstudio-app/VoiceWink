using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace VoiceWink.Services.Licensing;

/// <summary>
/// The tracked, reviewable license-identity manifest (LIC-4 product binding): which
/// LemonSqueezy store/product identities VoiceWink accepts as its own. Activate/validate
/// responses whose <c>meta.store_id</c>/<c>meta.product_id</c> fall outside these sets are
/// rejected as foreign once the manifest is armed. Variant ids are deliberately absent —
/// variant identity is ADVISORY (logged, never gating; owner decision 2026-07-28).
///
/// <para><b>Lifecycle:</b> shipped STAGED (both sets empty — binding not enforced) from 2026-07-29
/// until the live LS catalog existed; ARMED 2026-09-10 with that catalog's ids (Batch C step 3 —
/// the LNC-6 scaffold revert it was once paired with shipped earlier, in LIC-21 PR A). The id lifecycle is ADDITIVE — never remove
/// an id customers own; grow the sets instead. Any change to the values changes
/// <see cref="PolicyDigest"/>, which invalidates every install's persisted binding proof
/// and forces one online revalidation — deliberate.</para>
///
/// <para><b>Why constants, and why tracked:</b> these are public identifiers (they appear
/// in every checkout URL), so the file is tracked — a gitignored config would break
/// clean-clone GPL builds. Compile-time constants mean no runtime parse that could fail
/// open; the constructor rejects every invalid shape, so a malformed manifest cannot
/// exist at runtime. In a RELEASE build the type cannot represent a non-live manifest at
/// all; a DEBUG build additionally accepts the test-mode environment (see
/// <c>TestEnvironment</c> below), which no app code path supplies — it exists for the
/// LS-test-store harness and the test suite only. Numeric ids carry no environment marker,
/// so <see cref="Environment"/> is a reviewable declaration only — the release preflight
/// (<c>scripts/ls-release-preflight.ps1</c>) is what proves the ids are live-mode.
/// The preflight extracts the values below from this source file; its extraction pattern
/// is pinned against this file's shape by <c>LicenseIdentityManifestTests</c> — keep the
/// declaration lines machine-shaped (one id list per line).</para>
/// </summary>
public sealed class LicenseIdentityManifest
{
    /// <summary>The only environment a RELEASE build can declare — see the class doc.</summary>
    public const string LiveEnvironment = "live";

#if DEBUG
    /// <summary>
    /// DEBUG-only: the LemonSqueezy test-mode store environment. Compiled out of Release, and no
    /// app code path supplies it in ANY configuration — the <c>tools/ls-license-exercise</c>
    /// harness and the test suite are the only callers. Two mechanisms keep it out of a signed
    /// artifact: an unguarded reference to this symbol fails the Release compile outright
    /// (release-audit builds Release per PR, tests included), and the one thing the compiler
    /// cannot see — the raw literal below re-appearing outside this region — is pinned by
    /// <c>ManifestSource_ConfinesTestLiteralToDebugRegion</c> and the preflight's mirror check.
    /// Keep this file free of any preprocessor directive other than <c>#if DEBUG</c>/<c>#endif</c>;
    /// both pins assert that so their region tracking stays sound.
    /// </summary>
    public const string TestEnvironment = "test";
#endif

    // ── The manifest values (the ONLY lines the preflight extracts — keep each on one line) ──
    private static readonly long[] ProductionStoreIds = { 366247 };   // ARMED 2026-09-10 — the LS store "VoiceWink" (Batch C step 3; the same id the test-mode store carried)
    private static readonly long[] ProductionProductIds = { 1353161 }; // ARMED 2026-09-10 — the LIVE-mode product "VoiceWink for Windows" (Copy to Live minted it; the test-mode product was 1326728)

    /// <summary>The manifest compiled into this build — ARMED with the live catalog since 2026-09-10.</summary>
    public static LicenseIdentityManifest Production { get; } =
        new(LiveEnvironment, ProductionStoreIds, ProductionProductIds);

    public string Environment { get; }
    public ImmutableArray<long> ApprovedStoreIds { get; }
    public ImmutableArray<long> ApprovedProductIds { get; }

    /// <summary>Armed (both sets populated) vs Staged (both empty). Any other shape cannot be constructed.</summary>
    public bool IsEnforced { get; }

    /// <summary>
    /// Deterministic identity of this policy: SHA-256 (lowercase hex) over the environment
    /// and the sorted id sets. Persisted as the binding PROOF when a stored key matches an
    /// armed manifest online, and compared at every cache-trust point — so a manifest change
    /// self-invalidates old proofs. Also the value the preflight attestation pins.
    /// </summary>
    public string PolicyDigest { get; }

    /// <summary>
    /// Validates at construction so an invalid manifest fails the build's first test run
    /// (and any app launch) instead of failing open: the environment must be exactly
    /// <see cref="LiveEnvironment"/> (a DEBUG compilation additionally accepts the DEBUG-only
    /// test environment — ordinal both ways, so case variants stay rejected everywhere); the
    /// id sets must be BOTH empty (Staged) or BOTH populated (Armed) — a half-armed manifest
    /// would silently not enforce; ids must be positive and distinct.
    /// </summary>
    public LicenseIdentityManifest(string environment, IEnumerable<long> approvedStoreIds, IEnumerable<long> approvedProductIds)
    {
        if (environment != LiveEnvironment
#if DEBUG
            && environment != TestEnvironment
#endif
           )
        {
            throw new ArgumentException($"License identity manifest must declare environment '{LiveEnvironment}' (got '{environment}').", nameof(environment));
        }

        var stores = approvedStoreIds.ToImmutableArray();
        var products = approvedProductIds.ToImmutableArray();
        if (stores.IsEmpty != products.IsEmpty)
            throw new ArgumentException("License identity manifest is half-armed: store and product id sets must be both empty (staged) or both populated (armed).");
        ValidateIds(stores, "store");
        ValidateIds(products, "product");

        Environment = environment;
        ApprovedStoreIds = stores;
        ApprovedProductIds = products;
        IsEnforced = !stores.IsEmpty;
        PolicyDigest = ComputePolicyDigest(environment, stores, products);
    }

    private static void ValidateIds(ImmutableArray<long> ids, string kind)
    {
        foreach (var id in ids)
        {
            if (id <= 0)
                throw new ArgumentException($"License identity manifest {kind} id {id} is not a positive LemonSqueezy id.");
        }
        if (ids.Distinct().Count() != ids.Length)
            throw new ArgumentException($"License identity manifest {kind} ids contain duplicates.");
    }

    private static string ComputePolicyDigest(string environment, ImmutableArray<long> stores, ImmutableArray<long> products)
    {
        var canonical = $"{environment}|stores:{string.Join(",", stores.Sort())}|products:{string.Join(",", products.Sort())}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

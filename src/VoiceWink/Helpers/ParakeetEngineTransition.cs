namespace VoiceWink.Helpers;

/// <summary>Which engine backend serves the Parakeet model row right now.</summary>
public enum ParakeetBackend
{
    /// <summary>Nothing can serve — no artifact is usable (the row shows as not installed, or a
    /// real failure when a decode was requested anyway).</summary>
    None,

    /// <summary>The legacy sherpa-onnx bundle serves.</summary>
    Sherpa,

    /// <summary>The parakeet.cpp GGUF serves (via the resident server subprocess).</summary>
    Pcpp,
}

/// <summary>The GGUF artifact's state — TRN-29 Step 0's integrity tiers, refined by the slice-1
/// self-review so "hash still running", "hash failed", and "staging leftovers" are DISTINCT
/// inputs rather than one conflated value (each pairs with a different decision below).
/// <para><c>Installed</c> is the manager's cheap check (directory + manifest + exact sizes) with
/// the per-install SHA-256 still PENDING; <c>VerifyFailed</c> is that hash having FAILED (the
/// same-size-corruption case); <c>PcppEligible</c> is the hash having passed for this install.
/// The pcpp backend serves only at <c>PcppEligible</c>.</para></summary>
public enum GgufTier
{
    /// <summary>No GGUF bundle directory and no staging leftovers.</summary>
    Absent,

    /// <summary>No installed directory, but staging leftovers exist (a cancelled or failed
    /// download). Serves nothing; whether anything auto-resumes depends on the sherpa side —
    /// see <see cref="ParakeetEngineTransition"/>.</summary>
    Staging,

    /// <summary>Directory + manifest + sizes check out; the per-install hash has not finished.
    /// A healthy transitional state (the verify runs right after install), never a broken one.</summary>
    Installed,

    /// <summary>Installed AND the full SHA-256 verified for this install.</summary>
    PcppEligible,

    /// <summary>The cheap check passes but the per-install hash FAILED — the artifact is broken
    /// (same-size corruption). Never serves; the repair path is a re-download.</summary>
    VerifyFailed,
}

/// <summary>Everything the transition decision reads, derived fresh at each preparation —
/// TRN-29 Step 0's rule that there is no persisted "transitioned" flag whose staleness could
/// route a recording to a deleted artifact.</summary>
/// <param name="PcppFeatureOn">The rollout flag — the KILL SWITCH for the pcpp backend in THIS
/// build. (Downgrade safety — Step 0 row 8 — is a different mechanism: an OLDER binary never
/// executes this function at all.)</param>
/// <param name="SherpaInstalled">The legacy 4-file bundle passes the manager's installed check.</param>
/// <param name="Gguf">The GGUF artifact's state.</param>
/// <param name="PcppRunnable">The pcpp backend can actually run (server binary present, spawn
/// path healthy). Step 0 row 10: a valid GGUF with an unrunnable backend serves sherpa.</param>
/// <param name="PcppHasServed">pcpp has successfully served ≥1 transcription on this install —
/// the gate on offering legacy cleanup (row 7 / row 10 note).</param>
/// <param name="DiskAllowsGguf">The free-space precheck for the GGUF download passes (row 9).</param>
/// <param name="DeletePending">The coordinator's delete tombstone: a user delete of the Parakeet
/// row has begun and has not fully completed (row 11's partial-delete state). While set, nothing
/// auto-downloads and nothing is offered — the remaining artifacts still serve, but the next
/// move belongs to the delete retry, never to a fresh migration download of bytes the user just
/// asked to remove.</param>
/// <param name="SherpaBytesRemain">Legacy sherpa BYTES exist on disk, whether or not the bundle
/// still reads as installed. Feeds ONLY <see cref="ParakeetTransitionDecision.CleanupLegacyDue"/>,
/// and exists because the manager's delete is two-phase: a phase-2 failure (a file an AV scan
/// holds open) removes the manifest — so <see cref="SherpaInstalled"/> goes false — while
/// ~670 MB stays on disk. Keying cleanup on the installed flag alone made that debris
/// unreclaimable forever (post-flip no catalog row can re-install the sherpa bundle to sweep
/// it). The coordinator's own delete rule — judge completion on the BYTES, never the flag —
/// applies to cleanup too: the auto-cleanup keeps retrying on later serves precisely while
/// bytes remain. Serving decisions deliberately ignore this input: a half-deleted bundle is
/// unverifiable and must never serve.</param>
public readonly record struct ParakeetTransitionState(
    bool PcppFeatureOn,
    bool SherpaInstalled,
    GgufTier Gguf,
    bool PcppRunnable,
    bool PcppHasServed,
    bool DiskAllowsGguf,
    bool DeletePending,
    bool SherpaBytesRemain);

/// <summary>The decision for one preparation/UI refresh.</summary>
/// <param name="Serve">Which backend a decode uses right now.</param>
/// <param name="RowInstalled">Whether the single Models-page Parakeet row presents as installed.
/// True when a HEALTHY artifact exists (sherpa, or a GGUF at <see cref="GgufTier.Installed"/> /
/// <see cref="GgufTier.PcppEligible"/>); false for <see cref="GgufTier.VerifyFailed"/>-only and
/// staging-only states, so the Download action is visible as the repair path (the transactional
/// installer replaces the broken directory).</param>
/// <param name="AutoDownloadGguf">Start (or resume) the background GGUF migration download —
/// Step 0 row 2, extended to re-fetching a <see cref="GgufTier.VerifyFailed"/> artifact and
/// resuming stale staging. Only while sherpa still serves the user: a user-initiated download
/// cancelled on a fresh install is NOT auto-restarted (cancel means cancel; the Download button
/// is the re-trigger there).</param>
/// <param name="CleanupLegacyDue">Remove the sherpa bundle automatically (row 7) — only when
/// pcpp is serving AND has successfully served on this install, and never mid-delete. Named for
/// what happens since 2026-08-24 (owner: no button, no dialog — the coordinator deletes in the
/// background after a successful serve); it was <c>OfferLegacyCleanup</c> while a Models-page
/// affordance consumed it.</param>
public readonly record struct ParakeetTransitionDecision(
    ParakeetBackend Serve,
    bool RowInstalled,
    bool AutoDownloadGguf,
    bool CleanupLegacyDue);

/// <summary>
/// TRN-29 Step 0's transition-state table as one pure function — the single decision point the
/// transition coordinator, the runtime, and the Models-page surfaces all consult, so no caller
/// special-cases the second bundle on its own
/// (<c>docs/plans/2026-08-23-trn29-parakeetcpp-swap/12-step0-transition-states.md</c>).
///
/// <para><b>Pure and re-evaluated per preparation.</b> The inputs are derived from disk and flag
/// state at the moment of the call; nothing here persists.</para>
///
/// <para><b>Flag OFF is the kill switch, and its cost is stated rather than hidden:</b> a user
/// who completed legacy cleanup (sherpa deleted) and is then flag-flipped off has NO serving
/// backend — Parakeet reads as needing a download, exactly like today's <c>disable_parakeet</c>
/// lever costs the whole engine. The GGUF still counts toward <c>RowInstalled</c> there (the
/// bytes exist and delete must own them); it simply cannot serve while the flag is off.</para>
/// </summary>
public static class ParakeetEngineTransition
{
    public static ParakeetTransitionDecision Decide(ParakeetTransitionState s)
    {
        var ggufHealthy = s.Gguf is GgufTier.Installed or GgufTier.PcppEligible;
        var rowInstalled = s.SherpaInstalled || ggufHealthy;

        if (!s.PcppFeatureOn)
        {
            // Kill switch: the pcpp backend never serves, nothing downloads, nothing is offered.
            // Sherpa-era behavior for sherpa-era states; a healthy GGUF still presents as
            // installed (honest about the bytes on disk) but cannot serve.
            return new ParakeetTransitionDecision(
                Serve: s.SherpaInstalled ? ParakeetBackend.Sherpa : ParakeetBackend.None,
                RowInstalled: rowInstalled,
                AutoDownloadGguf: false,
                CleanupLegacyDue: false);
        }

        // The pcpp side serves only at PcppEligible AND with a runnable backend (rows 3/4/10).
        if (s.Gguf == GgufTier.PcppEligible && s.PcppRunnable)
        {
            return new ParakeetTransitionDecision(
                Serve: ParakeetBackend.Pcpp,
                RowInstalled: true,
                AutoDownloadGguf: false,
                // Row 7: cleanup only once pcpp has proven itself on this install, only while
                // legacy BYTES remain (installed OR phase-2-delete debris — see the state
                // param's doc), and never while a delete is mid-flight.
                CleanupLegacyDue: (s.SherpaInstalled || s.SherpaBytesRemain)
                                  && s.PcppHasServed && !s.DeletePending);
        }

        // Everything below serves the sherpa side (or nothing): Absent/Staging (rows 1/2/6),
        // Installed with the hash pending (healthy, transitional — defer, never serve unverified
        // bytes), VerifyFailed (broken), and PcppEligible-but-unrunnable (row 10).
        var serve = s.SherpaInstalled ? ParakeetBackend.Sherpa : ParakeetBackend.None;

        // Row 2 (+5/6/9/11): the background migration download runs only while sherpa still
        // serves the user, disk allows it, no delete is mid-flight, and the GGUF is genuinely
        // fetchable-from-scratch: absent, stale staging, or a verify-failed artifact being
        // replaced. Installed-with-hash-pending downloads nothing (verification, not repair),
        // and the fresh-install path (no sherpa) never auto-downloads — row 1 is user-initiated,
        // and a user-cancelled download stays cancelled until the user asks again.
        var autoDownload = s.SherpaInstalled
                           && !s.DeletePending
                           && s.DiskAllowsGguf
                           && s.Gguf is GgufTier.Absent or GgufTier.Staging or GgufTier.VerifyFailed;

        return new ParakeetTransitionDecision(
            Serve: serve,
            RowInstalled: rowInstalled,
            AutoDownloadGguf: autoDownload,
            CleanupLegacyDue: false);
    }
}

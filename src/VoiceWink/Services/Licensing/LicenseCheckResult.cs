namespace VoiceWink.Services.Licensing;

/// <summary>
/// Whether a licence check actually reached the server — the fact a UI needs before it may say
/// anything about WHY nothing changed (LIC-15).
///
/// <para>Three values, not five, and the boundary is deliberate: the UI needs to tell "we never
/// asked" from "we asked and could not get through" from "we asked and got an answer". What the
/// answer WAS is already carried by <see cref="LicenseCheckResult.Status"/>, so splitting
/// <see cref="Completed"/> into validated / refused / discarded would add states the caller cannot
/// use — and would mean threading an outcome through <c>ApplyValidateOutcome</c>, the method holding
/// the stale-response identity re-check and every response-derived write. That method is left
/// untouched on purpose.</para>
/// </summary>
internal enum LicenseCheckOutcome
{
    /// <summary>
    /// No request was sent: a local preflight answered. On a FORCED check this means the stored key
    /// could not be checked at all — no key on file, or the hardware fingerprint no longer matches
    /// the machine it was activated on (both short-circuit before any HTTP). The four persisted-verdict
    /// flags and the fresh-cache return are also here, but only an unforced check can reach them.
    /// </summary>
    NotAttempted,

    /// <summary>
    /// A response arrived and was processed — validated, refused, or deliberately discarded as stale
    /// or unreadable. In every case <see cref="LicenseCheckResult.Status"/> is the current verdict, so
    /// a caller can truthfully report "no change" when it matches what it had before.
    /// </summary>
    Completed,

    /// <summary>
    /// The request could not be completed: transport failure, timeout, an unreadable body, or a
    /// connection reset mid-response. The server's opinion is unknown and the local grace chain
    /// answered instead.
    /// </summary>
    Unreachable,
}

/// <summary>
/// A licence check's verdict plus whether the server was actually consulted (LIC-15).
///
/// <para>Exists because <c>Task&lt;LicenseStatus&gt;</c> alone cannot support an honest UI message.
/// The status is identical for "the fingerprint no longer matches, so we never asked" and for "we
/// asked and could not get through" — both return <c>Invalid</c>/unchanged — and a UI inferring the
/// difference from a last-validated timestamp gets it wrong in BOTH directions: a local
/// short-circuit never advances the stamp though the machine is online, and a server-confirmed
/// refusal need not advance it though the server answered. The License page shipped exactly that
/// inference and would have told an online user their connection had failed (Codex plan round,
/// 2026-09-04).</para>
/// </summary>
internal readonly record struct LicenseCheckResult(LicenseStatus Status, LicenseCheckOutcome Outcome);

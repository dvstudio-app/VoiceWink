namespace VoiceWink.Services.System;

/// <summary>
/// Outcome of <see cref="ApiKeyManager.SetApiKey"/>. Replaced a plain <c>bool</c> in ENH-15:
/// "malformed key" and "this device could not encrypt" are different facts and the UI must not
/// tell a user their device failed when they pasted something with a line break in it.
/// </summary>
public enum ApiKeySaveResult
{
    /// <summary>A key was persisted. <c>KeyChanged</c> raised.</summary>
    Saved,

    /// <summary>The stored key was cleared, because the input was empty or whitespace-only. <c>KeyChanged</c> raised.</summary>
    Cleared,

    /// <summary>The value cannot form an HTTP header. Nothing written, no event, previous key intact.</summary>
    InvalidFormat,

    /// <summary>DPAPI threw. Nothing written, no event, previous key intact (F28/F44).</summary>
    EncryptionFailed
}

public static class ApiKeySaveResultExtensions
{
    /// <summary>
    /// The single success predicate, shared by all twelve consuming sites.
    ///
    /// <para><b>Clearing counts as success</b>, and that is the whole reason this lives in one
    /// place. Clearing a slot is a legitimate write with a legitimate outcome, and a caller that
    /// spelled the test as <c>== Saved</c> would compile, run, and report a failure for a clear
    /// that worked perfectly.</para>
    ///
    /// <para>ENH-17 removed the rollback sites this predicate was originally written for — the
    /// save flows no longer write a candidate before validating it, so there is nothing to undo.
    /// The predicate stays because the distinction it draws is about WRITES, not about rollback:
    /// every commit path still has to tell "the slot changed as asked" from "the write was
    /// refused".</para>
    /// </summary>
    public static bool IsSuccess(this ApiKeySaveResult result)
        => result is ApiKeySaveResult.Saved or ApiKeySaveResult.Cleared;
}

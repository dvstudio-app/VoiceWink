using VoiceWink.Services.Audio;

namespace VoiceWink.Helpers;

/// <summary>
/// AUD-17 (2026-08-15): the line under the Settings Microphone dropdown naming the endpoint a
/// recording would actually use — the answer to "which microphone am I on?".
///
/// <para><b>Why this exists, from a live incident.</b> The owner spent an afternoon believing they
/// had switched microphones twice. They had not: the dropdown read "System default", Windows had
/// every default ROLE pointing at a connected Bluetooth headset, and the app followed it correctly
/// and silently. Nothing on any screen named the device. Answering the question took a log grep for
/// AUD-16's <c>dev=</c> tag plus a purpose-built COM probe — for a fact the app already had.
/// A microphone that is silently not the one you think you are using is not a diagnostics gap; it
/// wastes the session and every recording in it.</para>
///
/// <para><b>Two cases earn the line; everything else stays silent.</b> "System default" selected —
/// the combo names a POLICY, not a device, so the resolved device is genuinely absent from the
/// screen. And a pinned device that is MISSING — the combo says "(unavailable)" but never what runs
/// instead, which is the same gap one step along. When a present device is pinned the combo already
/// carries its name, and restating it would be noise: a second label saying what the control above
/// it says trains the reader to stop reading both.</para>
///
/// <para><b>It describes CONFIGURATION, not the last recording.</b> The wording is deliberately
/// "Currently:" and never "Recording from:" — AUD-1's fallback classification happens at ACTIVATION
/// time, so a pinned-and-present device can still fall back for one recording without this line
/// being wrong. Claiming otherwise would put a second, quieter lie on the same screen as the one
/// this fixes. The per-recording truth is the pipeline notice AUD-1 already publishes.</para>
///
/// <para><b>Every label requires POSITIVE evidence; there is no "no microphone" claim.</b> An
/// earlier revision said "No microphone found" for an empty snapshot, and that was a defect
/// (Codex diff review): <c>AudioDeviceManager.RefreshDevices</c> CATCHES every enumeration failure
/// and substitutes an empty list, so by the time a snapshot reaches here, "the machine has no
/// capture endpoint" and "the audio service just failed us" are the same value. A transient COM
/// fault would have told the user their microphone did not exist while recording still worked.
/// The distinction cannot be recovered at this layer, so the claim is not made at all — the same
/// rule as <c>ModelLanguageSupport.CanRecognise</c>, which requires membership rather than
/// negating absence. Restoring the claim needs a real enumeration OUTCOME plumbed from the
/// manager, not a re-reading of the list.</para>
///
/// <para><b>Pure, and reads only the snapshot it is handed.</b> No COM: <c>AudioDeviceManager</c>
/// already marks <see cref="AudioDeviceInfo.IsDefault"/> during the enumeration the dropdown
/// performs anyway, so this costs one list scan on a snapshot that exists. That matters beyond
/// tidiness — a <c>FriendlyName</c> read is a property-store open, and AUD-11 cost a recording to
/// one on a contended Bluetooth endpoint. Nothing here may ever grow a device call.</para>
/// </summary>
internal static class ResolvedMicrophoneLabel
{
    /// <summary>Shown when the system default resolves to a known device.</summary>
    internal const string CurrentlyPrefix = "Currently: ";

    /// <summary>Shown when the pinned device is gone and we can name the stand-in.</summary>
    internal const string FallbackPrefix = "Unavailable — currently using ";

    /// <summary>
    /// The label for the current snapshot + persisted pin, or <c>null</c> when the dropdown
    /// already says everything true.
    /// </summary>
    /// <param name="devices">
    /// The enumeration snapshot, or <c>null</c> when none has run yet. The two are treated
    /// IDENTICALLY and deliberately so — see the positive-evidence paragraph above: an empty list
    /// is what a FAILED enumeration also produces, so it is not evidence of anything and earns no
    /// label. Do not reintroduce a branch on <c>Count == 0</c> without a real outcome signal.
    /// </param>
    /// <param name="persistedId">The pinned endpoint id; null/blank = "System default".</param>
    internal static string? Describe(IReadOnlyList<AudioDeviceInfo>? devices, string? persistedId)
    {
        var snapshot = devices ?? Array.Empty<AudioDeviceInfo>();
        var pinned = string.IsNullOrWhiteSpace(persistedId) ? null : persistedId;

        if (pinned is null)
        {
            // "System default" names a policy, so resolve it — but only from positive evidence.
            // A snapshot with devices but no default mark is a real state too: AudioDeviceManager
            // CATCHES the default-endpoint lookup and logs a warning, leaving every row false.
            // Guessing "the first one" would be a fabrication, so say nothing.
            var def = FirstDefault(snapshot);
            return def is null ? null : CurrentlyPrefix + def.Name;
        }

        // Pinned AND present: the combo already renders its name.
        foreach (var d in snapshot)
        {
            if (string.Equals(d.Id, pinned, StringComparison.Ordinal)) return null;
        }

        // Pinned and gone. Name the stand-in only if we actually know it — "(unavailable)" is
        // already on the combo, so silence here loses nothing but a guess would mislead.
        var standIn = FirstDefault(snapshot);
        return standIn is null ? null : FallbackPrefix + standIn.Name;
    }

    private static AudioDeviceInfo? FirstDefault(IReadOnlyList<AudioDeviceInfo> devices)
    {
        foreach (var d in devices)
        {
            // A blank name would render "Currently: " — a label with nothing after the colon reads
            // as a rendering bug rather than as information, so treat it as unknown.
            if (d.IsDefault && !string.IsNullOrWhiteSpace(d.Name)) return d;
        }

        return null;
    }
}

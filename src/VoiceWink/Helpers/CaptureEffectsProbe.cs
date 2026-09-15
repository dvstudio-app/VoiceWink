namespace VoiceWink.Helpers;

/// <summary>
/// AUD-18: the pure half of capture-effects visibility — WinRT id composition and the log-line
/// format. The measured gap this closes: on 2026-08-15 the owner established by manual A/B that
/// Windows Voice Clarity was gating their capture to 34.56% digital zeros, and NOTHING in the app
/// or its logs could answer "was an effect active on this recording?" — it took a WAV scan and
/// most of a session. This helper is the formatting/composition core; the WinRT query itself lives
/// in <c>Services/Audio/CaptureEffectsReader</c> behind a seam.
///
/// <para><b>The id composition is MEASURED, not guessed</b> (2026-08-19 probe, live on this
/// machine): <c>Windows.Devices.Enumeration.DeviceInformation</c> reports capture devices with ids
/// of exactly this shape — <c>\\?\SWD#MMDEVAPI#{mmDeviceId}#{DEVINTERFACE_AUDIO_CAPTURE}</c> —
/// and <c>AudioEffectsManager.CreateAudioCaptureEffectsManager</c> accepts them. The same format
/// was independently documented by AUD-3's 2026-08-01 API probe (endpoint property store pids 1/9).
/// The GUID is <c>DEVINTERFACE_AUDIO_CAPTURE</c>; a render endpoint would need the render GUID,
/// which this deliberately does not support — the reader only ever describes capture.</para>
///
/// <para><b>What a line can honestly claim.</b> The 2026-08-19 probe also measured the LIMIT: the
/// owner's Intel Smart Sound array reports NO effects in ANY category while demonstrably gating
/// between-word audio to exact zeros (25.7% of samples, 2026-08-18) — driver DSP below the APO
/// reporting layer is invisible to this API. So the line reports what Windows REPORTS, and the
/// wording is "(none reported)", never "no processing". Empty is evidence about the APO layer
/// only.</para>
/// </summary>
internal static class CaptureEffectsProbe
{
    /// <summary>DEVINTERFACE_AUDIO_CAPTURE — the device-interface class GUID for capture
    /// endpoints, verbatim from the measured DeviceInformation ids.</summary>
    internal const string CaptureInterfaceGuid = "{2eef81be-33fa-4800-9670-1cd474972c3f}";

    /// <summary>Compose the WinRT device id <c>AudioEffectsManager</c> wants from the MMDevice
    /// endpoint id the app already holds. Returns null for a null/blank input — the caller turns
    /// that into the "unavailable" line rather than querying a malformed id.</summary>
    internal static string? ComposeWinRtId(string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return null;
        return $"\\\\?\\SWD#MMDEVAPI#{endpointId}#{CaptureInterfaceGuid}";
    }

    /// <summary>One category's rendering: <c>(none reported)</c> for an empty list, the enum
    /// names comma-joined otherwise. Names come from <c>AudioEffectType.ToString()</c> — enum
    /// identifiers or integers, never user-authored text, so the line is log-safe by
    /// construction (the SEC-3 concern class does not arise).</summary>
    internal static string FormatCategory(IReadOnlyList<string> effectNames)
        => effectNames.Count == 0 ? "(none reported)" : string.Join(",", effectNames);

    /// <summary>The whole line's payload: our stream's category first (NAudio opens a
    /// default-category stream, so <c>default=</c> is what OUR capture gets), then the
    /// Communications chain as the reference point — it answers "would a comms app (Teams) have
    /// received processing this recording did not", which is precisely the 2026-08-15 question.
    /// Per-category failures degrade to <c>unavailable(0xHRESULT)</c> for that category only.</summary>
    internal static string FormatPayload(string defaultChain, string commsChain)
        => $"default={defaultChain} comms={commsChain}";
}

namespace VoiceWink.Helpers;

/// <summary>What kind of capture endpoint a device is. Descriptive only — see the classifier's
/// doc for why nothing user-facing is derived from it any more.</summary>
public enum CaptureEndpointKind
{
    /// <summary>Not classifiable from the available signals.</summary>
    Unknown,
    /// <summary>An ordinary microphone or mic array.</summary>
    Microphone,
    /// <summary>A headset/handset endpoint that is NOT reachable over Bluetooth hands-free —
    /// typically wired USB/analog.</summary>
    WiredHeadset,
    /// <summary>A Bluetooth hands-free (HFP / LE Audio voice) endpoint. <b>The bandwidth is NOT
    /// knowable from this classification</b> — the service GUID proves the PROFILE, not the codec,
    /// and HFP spans narrowband CVSD (~3.4 kHz) through wideband mSBC (~8 kHz). One QC35 II was
    /// measured wideband on 2026-08-03; that is one headset on one link, not a property of the
    /// kind. Only a spectrum can tell you which you have.</summary>
    BluetoothHandsFree,
}

/// <summary>
/// AUD-4: classifies a capture endpoint. It once drove a user-facing warning that a Bluetooth
/// headset's mic transcribes worse than a wired one. <b>That warning was REMOVED on 2026-08-03,
/// and it must not be reinstated without new evidence</b> — the claim was measured and did not
/// survive. Owner decision: "we should not warn against anything we are not sure about."
///
/// <para><b>What the measurement found</b> (one speaker, one 218-word passage read once and
/// captured by two microphones SIMULTANEOUSLY, so the utterance is identical and only the
/// hardware differs — <c>tools/capture-band-energy record2</c>, <c>tools/transcript-wer</c>):</para>
/// <list type="bullet">
/// <item><b>Bandwidth costs nothing.</b> The Bluetooth link cliffs 41 dB above 8 kHz — but the
/// recorder writes 16 kHz, so 8 kHz IS its Nyquist. Everything the link discards is content the
/// app would have thrown away regardless.</item>
/// <item><b>Cloud model: no detectable difference.</b> The between-mic gap was smaller than
/// <c>gpt-transcribe</c>'s own run-to-run variance on a single unchanged file (the app logged
/// 1260 and 1262 chars for the same WAV), so the instrument could not resolve it.</item>
/// <item><b>Local small model: the Bluetooth headset was BETTER</b> — 5.96% WER against 7.80%,
/// each reproduced byte-identically across two runs (whisper.cpp decodes deterministically, so
/// there is no run noise to hide behind). The warning was not merely unsupported, it was
/// BACKWARDS in the one configuration where a difference was measurable at all.</item>
/// </list>
///
/// <para><b>What this does NOT establish</b>, and why the classification is kept rather than
/// deleted: a noisy environment was never tested against transcripts, and the 2026-07-31 incident
/// that motivated the warning (local models hallucinating on Bluetooth audio) happened on a
/// train. Noise remains the plausible cause there, not the link. Different mic POSITIONS also
/// confound this — clip-on versus on-head compares two capture setups, not two transducers.</para>
///
/// <para><b>Signals, measured on real hardware 2026-08-01</b> (Bose QC35 II vs an Intel Smart
/// Sound mic array, both active):</para>
/// <list type="bullet">
/// <item>Endpoint form factor: <c>5</c> (Headset) vs <c>4</c> (Microphone). NECESSARY but NOT
/// sufficient — a wired USB headset also reports 5, and warning about those would be wrong.</item>
/// <item>The Bluetooth endpoint carries a device-path property containing <c>BTHENUM</c> and the
/// Bluetooth Hands-Free Profile service GUID <c>0000111E</c>; the array carries nothing of the
/// kind. This is the discriminator.</item>
/// <item><b>What does NOT work:</b> <c>PKEY_Device_EnumeratorName</c> is <c>INTELAUDIO</c> for
/// BOTH devices — the endpoint is enumerated through the audio driver, not through the Bluetooth
/// stack — so the obvious signal would have misfired. Likewise the negotiated mix format is
/// 48 kHz stereo for both: WASAPI shared mode hides the hands-free band limit entirely.</item>
/// </list>
///
/// <para>Because the exact property id is undocumented and driver-dependent, the classifier
/// takes the endpoint's string-valued properties as an opaque collection and scans for the
/// markers rather than pinning one key — that survives a vendor exposing it elsewhere. Pure:
/// no COM, no IO. Advisory ONLY: nothing here gates or alters capture.</para>
/// </summary>
public static class CaptureEndpointClassifier
{
    // Windows EndpointFormFactor: 4 = Microphone, 5 = Headset, 6 = Handset.
    private const int FormFactorMicrophone = 4;
    private const int FormFactorHeadset = 5;
    private const int FormFactorHandset = 6;

    // Bluetooth enumerator prefixes (classic + LE) as they appear in endpoint device paths.
    private static readonly string[] BluetoothEnumerators = ["BTHENUM", "BTHLE", "BTHHFENUM", "BTHA2DP"];

    // Bluetooth Hands-Free Profile service class UUID (0x111E). Its presence on a capture
    // endpoint is the strongest available statement that this is the phone-call voice path.
    private const string HandsFreeServiceGuid = "0000111E";

    /// <summary>
    /// Convert a boxed <c>PKEY_AudioEndpoint_FormFactor</c> property value to its int form,
    /// or null when it is not an integral value at all.
    ///
    /// <para><b>This exists because of a shipped bug (2026-08-01).</b> Windows returns the form
    /// factor as a <see cref="uint"/>, and the original enumeration code pattern-matched
    /// <c>value is int</c>. C# pattern matching on a BOXED value requires an exact type match,
    /// so a boxed <c>uint</c> never matched: the form factor read as null, every endpoint
    /// classified <see cref="CaptureEndpointKind.Unknown"/>, and the Bluetooth advisory could
    /// never appear — the feature shipped dead. The classifier's own tests could not catch it
    /// because they pass a real <c>int</c> and never cross the boxing boundary; that is exactly
    /// why the conversion lives HERE, as a pure function with its own rows, instead of inline
    /// at the untestable COM-reading site.</para>
    /// </summary>
    public static int? ToFormFactor(object? propertyValue) => propertyValue switch
    {
        uint u => (int)u,     // what Windows actually returns — measured on real endpoints
        int i => i,
        ushort us => us,
        short s => s,
        byte b => b,
        _ => null,            // strings, blobs, null: not a form factor, say nothing
    };

    /// <summary>
    /// Classify an endpoint. <paramref name="formFactor"/> is the raw
    /// <c>PKEY_AudioEndpoint_FormFactor</c> value (null when unreadable — use
    /// <see cref="ToFormFactor"/> to obtain it from a boxed property value);
    /// <paramref name="propertyValues"/> is every string-valued endpoint property.
    /// </summary>
    public static CaptureEndpointKind Classify(int? formFactor, IEnumerable<string?>? propertyValues)
    {
        var bluetoothVoice = HasBluetoothVoiceMarker(propertyValues);

        // A hands-free endpoint is a headset-shaped device on the Bluetooth voice path. Require
        // BOTH: the marker alone could appear on a device whose form factor says it is something
        // else entirely, and the form factor alone cannot tell wired from wireless.
        if (formFactor is FormFactorHeadset or FormFactorHandset)
            return bluetoothVoice ? CaptureEndpointKind.BluetoothHandsFree : CaptureEndpointKind.WiredHeadset;

        if (formFactor == FormFactorMicrophone)
            return CaptureEndpointKind.Microphone;

        // Unreadable or an unmodelled form factor: say nothing rather than guess. Silence is the
        // correct output for advisory copy — a wrong hint is worse than no hint.
        return CaptureEndpointKind.Unknown;
    }

    /// <summary>True when a single property value carries a Bluetooth-voice marker: a Bluetooth
    /// enumerator prefix, or the Hands-Free Profile service GUID. Public so the enumeration side
    /// can filter as it reads and keep only what the classification acts on.</summary>
    public static bool IsBluetoothVoiceMarker(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Contains(HandsFreeServiceGuid, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var enumerator in BluetoothEnumerators)
            if (value.Contains(enumerator, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool HasBluetoothVoiceMarker(IEnumerable<string?>? propertyValues)
    {
        if (propertyValues == null) return false;
        foreach (var value in propertyValues)
            if (IsBluetoothVoiceMarker(value)) return true;
        return false;
    }

    // NO ADVISORY LIVES HERE ANY MORE — see the type doc. `AdviceFor` was removed 2026-08-03 after
    // its claim was measured and found to be, at best, unsupported. Classification stays: it is
    // correct, it was expensive to get right, and it is the honest half of this feature.
}

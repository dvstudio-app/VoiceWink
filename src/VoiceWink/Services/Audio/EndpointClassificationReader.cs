using NAudio.CoreAudioApi;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Audio;

/// <summary>
/// The COM-side half of endpoint classification: reads the two signals the pure
/// <see cref="CaptureEndpointClassifier"/> needs — the endpoint form factor and the
/// Bluetooth-voice marker strings — from an OPEN <see cref="MMDevice"/>'s property store.
/// Extracted verbatim from <c>AudioDeviceManager.ClassifyEndpoint</c> (AUD-6) so the standing
/// warm capture can classify its freshly RESOLVED device at start (never the enumeration cache,
/// which can be stale or miss the id) with the same read the enumeration pass uses.
///
/// <para>Best-effort by design: a property store that refuses to enumerate yields
/// <see cref="CaptureEndpointKind.Unknown"/>. The boxed-<c>UInt32</c> form-factor trap lives in
/// <see cref="CaptureEndpointClassifier.ToFormFactor"/> with its regression rows — this reader
/// deliberately owns no type conversion.</para>
/// </summary>
internal static class EndpointClassificationReader
{
    private static ILogger Logger => Log.ForContext(typeof(EndpointClassificationReader));

    public static CaptureEndpointKind Classify(MMDevice? device)
    {
        if (device == null) return CaptureEndpointKind.Unknown;

        try
        {
            var props = device.Properties;
            if (props == null) return CaptureEndpointKind.Unknown;

            int? formFactor = null;
            var markers = new List<string?>(4);
            for (var i = 0; i < props.Count; i++)
            {
                // Early exit: once BOTH answers are in hand, stop materializing values. A
                // property store carries large binary blobs (format descriptors, jack
                // descriptions) that cost real marshalling work and are never classification
                // input, so the common case reads a handful of entries instead of seventy.
                if (formFactor != null && markers.Count > 0) break;

                try
                {
                    var key = props.Get(i);
                    var value = props.GetValue(i).Value;
                    // Key FIRST, type second: Windows returns the form factor as a UInt32, and
                    // testing `value is int` here silently never matched — the feature shipped
                    // dead until a live re-verification caught it. ToFormFactor owns the
                    // widening and carries the regression rows.
                    if (key.formatId == PropertyKeys.PKEY_AudioEndpoint_FormFactor.formatId
                        && key.propertyId == PropertyKeys.PKEY_AudioEndpoint_FormFactor.propertyId)
                    {
                        formFactor = CaptureEndpointClassifier.ToFormFactor(value);
                    }
                    else if (value is string s && CaptureEndpointClassifier.IsBluetoothVoiceMarker(s))
                    {
                        // Keep only what the classifier acts on — device paths and names are
                        // identifying, and there is no reason to hold seventy of them in memory.
                        markers.Add(s);
                    }
                }
                catch
                {
                    // A single unreadable property must not cost the whole classification —
                    // property stores routinely carry entries that throw on read.
                }
            }

            return CaptureEndpointClassifier.Classify(formFactor, markers);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Endpoint classification failed (non-critical)");
            return CaptureEndpointKind.Unknown;
        }
    }
}

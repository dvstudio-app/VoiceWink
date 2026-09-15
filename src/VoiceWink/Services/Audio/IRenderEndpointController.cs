using NAudio.CoreAudioApi;
using Serilog;

namespace VoiceWink.Services.Audio;

/// <summary>What one <see cref="IRenderEndpointController.MuteDefaultIfUnmuted"/> pass did.</summary>
/// <param name="EndpointId">The default render endpoint, or <c>null</c> when the machine has none.</param>
/// <param name="MutedByUs">True only when THIS call applied the mute. False means it was already
/// muted — by us on an earlier recording, or by someone else — and the caller must not assume
/// ownership from this value alone.</param>
public readonly record struct RenderMuteAttempt(string? EndpointId, bool MutedByUs);

/// <summary>
/// The render-endpoint operations <see cref="SystemAudioMuteService"/> needs, addressed BY ENDPOINT
/// ID rather than by "whatever is default right now".
///
/// <para><b>Why the seam exists.</b> The service used to resolve
/// <c>GetDefaultAudioEndpoint(Render, Multimedia)</c> twice — once to mute, once to unmute — and
/// silently assumed the two resolutions named the same device. A Bluetooth headset breaks that on
/// EVERY recording: opening the microphone forces the link from A2DP to Hands-Free, which swaps the
/// default render endpoint (a Jabra Evolve2 65 exposes <c>Headphones (…)</c> on A2DP and
/// <c>Headset Earphone (…)</c> on HFP), and stopping swaps it back. Mute landed on one endpoint,
/// unmute on the other, and the first stayed muted with nothing left that ever unmuted it — the
/// user's headset earpiece silent for calls. Observed live 2026-08-29: muted 16:06:06.753, unmuted
/// 16:06:08.148, then 22 recordings during which <c>Mute()</c> took its already-muted branch.</para>
///
/// <para><b>Why mute is ONE call.</b> Resolve-then-read-then-set as three separate operations meant
/// three <see cref="MMDeviceEnumerator"/> instances, three device resolutions and two volume
/// activations per recording, all inside the service's lock and 300 ms into a recording — exactly
/// when a Bluetooth link is mid-A2DP→HFP switch. AUD-11 cost this repo a recording when a single
/// synchronous endpoint COM call stalled 4.6 s then 2.3 s on a contended Bluetooth endpoint while a
/// lock was held, so tripling that exposure on the very hardware this fix targets is not a trade
/// worth making. One pass also narrows (it cannot close) the window in which the default changes
/// between reading its id and muting it.</para>
///
/// <para><b>Why an interface.</b> The mute path could not be tested at all before this — the only
/// coverage that could exist drove the feature-disabled no-op branches, because everything else
/// reached a real audio endpoint and CI's hosted runner has none. Production wiring is
/// <see cref="NAudioRenderEndpointController"/>; the service defaults to it, so no DI registration
/// changes.</para>
/// </summary>
public interface IRenderEndpointController
{
    /// <summary>
    /// Resolve the default multimedia render endpoint and mute it if it is not already muted, in a
    /// single pass. Throws only when the OPERATION failed — never for a cleanup failure, because a
    /// caller that sees a throw will not record ownership, and an applied-but-unrecorded mute is
    /// the exact defect this class exists to prevent.
    /// </summary>
    RenderMuteAttempt MuteDefaultIfUnmuted();

    /// <summary>Unmute one specific endpoint by id.</summary>
    void Unmute(string endpointId);
}

/// <summary>
/// The production <see cref="IRenderEndpointController"/> — a thin NAudio/WASAPI wrapper holding no
/// state of its own.
/// </summary>
public sealed class NAudioRenderEndpointController : IRenderEndpointController
{
    private static ILogger Logger => Log.ForContext<NAudioRenderEndpointController>();

    public RenderMuteAttempt MuteDefaultIfUnmuted()
    {
        using var enumerator = new MMDeviceEnumerator();

        // Asking for a default that does not exist THROWS in NAudio rather than returning null,
        // so the probe has to come first — a machine with no output device is a supported state.
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            return new RenderMuteAttempt(null, false);

        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        try
        {
            var volume = device.AudioEndpointVolume;

            // Read the id BEFORE the set, so "throws iff the mute failed" holds by construction
            // rather than by GetId happening not to fail: a throw between applying the mute and
            // returning its id would be applied-but-unrecorded — the same shape DisposeQuietly
            // exists to close.
            var id = device.ID;
            if (volume.Mute) return new RenderMuteAttempt(id, false);

            volume.Mute = true;
            return new RenderMuteAttempt(id, true);
        }
        finally
        {
            DisposeQuietly(device);
        }
    }

    public void Unmute(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();

        // GetDevice THROWS for an unknown id; it never returns null. An endpoint that merely
        // disappeared usually still resolves (Windows keeps Unplugged/NotPresent entries) and
        // throws later, from the Activate inside AudioEndpointVolume. The caller treats both alike.
        var device = enumerator.GetDevice(endpointId);
        try
        {
            device.AudioEndpointVolume.Mute = false;
        }
        finally
        {
            DisposeQuietly(device);
        }
    }

    /// <summary>
    /// Release the device, swallowing a cleanup-only failure.
    ///
    /// <para>This is not defensive habit — it is required for correctness, and the mechanism is
    /// specific. NAudio 2.2.1's <c>MMDevice.Dispose()</c> cascades into
    /// <c>AudioEndpointVolume.Dispose()</c>, whose FIRST statement is
    /// <c>Marshal.ThrowExceptionForHR(audioEndPointVolume.UnregisterControlChangeNotify(callBack))</c>.
    /// So a plain <c>using</c> lets a teardown failure escape from a method whose real operation had
    /// already SUCCEEDED: the mute is applied, the caller sees an exception, records no ownership,
    /// and nothing ever unmutes it. Swallowing here keeps each method's contract honest — it throws
    /// if and only if the mute or unmute itself failed.</para>
    ///
    /// <para>Disposal is still worth doing: it releases the cached <c>AudioEndpointVolume</c> and its
    /// control-change registration on a thread that can catch the failure, rather than leaving it to
    /// the finalizer where the same throw would be unrecoverable. Note what it does NOT do — NAudio's
    /// <c>MMDevice.Dispose()</c> never releases the underlying <c>IMMDevice</c> RCW, so this is not
    /// "fixing a leaked COM object".</para>
    /// </summary>
    private static void DisposeQuietly(MMDevice device)
    {
        try
        {
            device.Dispose();
        }
        catch (Exception ex)
        {
            // Deliberately does NOT claim the operation succeeded: this runs from a finally, so
            // it is also reached when the mute or unmute itself threw (and that one propagates).
            Logger.Debug(ex, "Ignoring render-endpoint cleanup failure");
        }
    }
}

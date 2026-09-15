using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Audio;

/// <summary>
/// Mutes/unmutes system audio during recording, so speaker output cannot bleed into the capture.
///
/// <para><b>One piece of state, and it is an OWNERSHIP claim.</b>
/// <see cref="_mutedEndpointId"/> is non-null exactly while this service is holding a mute it has
/// not yet restored, and it names the endpoint to restore. There is deliberately no companion
/// boolean: the previous shape carried a <c>_didMuteAudio</c> flag beside the id, and every path
/// that could desynchronise the two — a re-entrant mute, a failed unmute, a throw between the two
/// assignments — stranded a mute that nothing could ever clear.</para>
///
/// <para><b>Two rules follow, and both are load-bearing.</b> (1) A mute is released ONLY by a
/// successful unmute — never by observing that the endpoint is already muted, because this service
/// cannot tell its own mute from a third party's by looking. (2) While a mute is held, a new
/// recording does NOT take a different endpoint: overwriting the id is precisely what left the
/// original muted forever. <see cref="IRenderEndpointController"/> carries the field evidence.</para>
/// </summary>
public sealed class SystemAudioMuteService : IDisposable
{
    private static ILogger Logger => Log.ForContext<SystemAudioMuteService>();

    private readonly SettingsService _settings;
    private readonly IRenderEndpointController _endpoints;
    private readonly object _lock = new();

    /// <summary>The endpoint this service muted and has not yet restored — the whole of its
    /// ownership state. Non-null means "we are holding a mute on this endpoint".</summary>
    private string? _mutedEndpointId;

    private CancellationTokenSource? _unmuteCts;

    public SystemAudioMuteService(SettingsService settings, IRenderEndpointController? endpoints = null)
    {
        _settings = settings;
        _endpoints = endpoints ?? new NAudioRenderEndpointController();
    }

    /// <summary>
    /// Mute system audio if the setting is enabled, we are not already holding a mute, and the
    /// endpoint isn't already muted by someone else.
    /// </summary>
    public void Mute()
    {
        if (!_settings.GetBool(AppDefaults.IsSystemMuteEnabled, true))
            return;

        lock (_lock)
        {
            // Cancel any pending delayed unmute
            _unmuteCts?.Cancel();
            _unmuteCts?.Dispose();
            _unmuteCts = null;

            // We are still holding a mute: either the line above just cancelled its delayed
            // unmute, or an earlier unmute failed and is awaiting retry. Keep that endpoint.
            // Replacing it here is the defect: the successor recording's unmute would restore
            // the NEW endpoint and leave the original muted with nothing tracking it.
            //
            // The residual, stated honestly: the held mute covers this recording only while the
            // default is still that endpoint. If the default has moved since (a Bluetooth link
            // changing profile), the successor records with the CURRENT endpoint unmuted, so
            // bleed is not suppressed for that take. That is the accepted trade — a stranded
            // mute is an incident the user cannot diagnose or clear, bleed on one overlapping
            // take is neither — and reaching it needs either a resumption delay > 0 with a
            // quick successor, or an unmute that already failed.
            if (_mutedEndpointId is not null)
            {
                Logger.Debug("System audio mute already held (dev={DeviceTag}); keeping it",
                    CaptureDeviceTag.For(_mutedEndpointId));
                return;
            }

            try
            {
                var attempt = _endpoints.MuteDefaultIfUnmuted();

                if (attempt.EndpointId is null)
                {
                    // No output device at all. Nothing to mute, and nothing to unmute later.
                    Logger.Debug("No default render endpoint; skipping system mute");
                }
                else if (attempt.MutedByUs)
                {
                    _mutedEndpointId = attempt.EndpointId;
                    Logger.Information("System audio muted (dev={DeviceTag})",
                        CaptureDeviceTag.For(attempt.EndpointId));
                }
                else
                {
                    // Muted before we got here, and we hold no claim — so it is somebody else's.
                    // Leave it, and take no ownership we would later wrongly release.
                    Logger.Debug("System audio was already muted, skipping (dev={DeviceTag})",
                        CaptureDeviceTag.For(attempt.EndpointId));
                }
            }
            catch (Exception ex)
            {
                // The controller throws only when the mute itself failed, so nothing is held.
                Logger.Error(ex, "Failed to mute system audio");
            }
        }
    }

    /// <summary>
    /// Unmute system audio after optional delay. Only unmutes if we are holding a mute.
    /// </summary>
    public async Task UnmuteAsync()
    {
        if (!_settings.GetBool(AppDefaults.IsSystemMuteEnabled, true))
            return;

        lock (_lock)
        {
            if (_mutedEndpointId is null)
            {
                Logger.Debug("Did not mute audio, skipping unmute");
                return;
            }
        }

        var delay = _settings.GetDouble(AppDefaults.AudioResumptionDelay, 0.0);

        if (delay > 0)
        {
            CancellationToken ct;
            lock (_lock)
            {
                _unmuteCts?.Cancel();
                _unmuteCts?.Dispose();
                _unmuteCts = new CancellationTokenSource();
                ct = _unmuteCts.Token;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // A new recording started and took over the held mute; it stays applied.
                Logger.Debug("Delayed unmute cancelled (new recording started)");
                return;
            }
        }

        DoUnmute();
    }

    private void DoUnmute()
    {
        lock (_lock)
        {
            var endpointId = _mutedEndpointId;
            if (endpointId is null) return;

            try
            {
                // By ID, NOT by current default: on a Bluetooth headset the default render
                // endpoint has very likely changed since Mute() (HFP during capture, A2DP after).
                _endpoints.Unmute(endpointId);
                _mutedEndpointId = null;
                Logger.Information("System audio unmuted (dev={DeviceTag})", CaptureDeviceTag.For(endpointId));
            }
            catch (Exception ex)
            {
                // Keep the claim. Mute() refuses to replace a held endpoint, so this id survives
                // until a later stop or Dispose restores it — the endpoint may simply be absent
                // for a moment (headset mid-reconnect), and a stranded mute is the worse outcome.
                Logger.Error(ex, "Failed to unmute system audio (dev={DeviceTag})",
                    CaptureDeviceTag.For(endpointId));
            }
        }
    }

    public void Dispose()
    {
        // Unconditional: DoUnmute re-checks ownership under the lock, and the previous outer
        // check read that state without one, from a different thread than the one writing it.
        DoUnmute();

        _unmuteCts?.Cancel();
        _unmuteCts?.Dispose();
    }
}

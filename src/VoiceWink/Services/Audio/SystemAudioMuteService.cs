using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Audio;

/// <summary>
/// Mutes/unmutes system audio during recording, so speaker output cannot bleed into the capture.
///
/// <para><b>The state is OWNERSHIP claims, in two places.</b>
/// <see cref="_mutedEndpointId"/> is the ACTIVE claim: non-null exactly while this service holds a
/// mute it has not restored on an endpoint it can still reach, and it names the endpoint to restore.
/// <see cref="_parkedEndpointIds"/> holds mutes on endpoints that went AWAY before they could be
/// restored (AUD-37) — an unplugged headset or dock. There is deliberately no companion boolean: the
/// previous shape carried a <c>_didMuteAudio</c> flag beside the id, and every path that could
/// desynchronise the two stranded a mute that nothing could ever clear.</para>
///
/// <para><b>Three rules follow, and all are load-bearing.</b> (1) A claim, active or parked, is
/// released ONLY by a successful unmute — never by observing that the endpoint is already muted,
/// because this service cannot tell its own mute from a third party's by looking. (The one move that
/// is not a release: a parked id becoming the active claim again.) (2) While an ACTIVE claim is held,
/// a new recording does NOT take a different endpoint: overwriting the id is precisely what left the
/// original muted forever. <see cref="IRenderEndpointController"/> carries the field evidence.
/// (3) A PARKED claim blocks nothing: an endpoint that is gone cannot bleed into a capture, and
/// letting it block (the pre-AUD-37 shape) left every later recording of the session unmuted while
/// each stop logged one Sentry Error against the removed device.</para>
///
/// <para><b>And one about the setting (AUD-38):</b> "Mute system audio while recording" gates
/// APPLYING a mute, never releasing one — a claim this service holds is restored through the same
/// release path whatever the setting says. The toggle itself does not release anything: a held claim
/// may belong to a recording still in progress, and unmuting on the switch would open the speakers
/// into a live capture. Idle claims wait for the next stop or for Dispose.</para>
/// </summary>
public sealed class SystemAudioMuteService : IDisposable
{
    private static ILogger Logger => Log.ForContext<SystemAudioMuteService>();

    private readonly SettingsService _settings;
    private readonly IRenderEndpointController _endpoints;
    private readonly object _lock = new();

    /// <summary>The ACTIVE claim: the endpoint this service muted and has not yet restored.
    /// Non-null means "we are holding a mute on this endpoint", and it blocks taking another.</summary>
    private string? _mutedEndpointId;

    /// <summary>AUD-37: mutes this service applied on endpoints that were gone when it tried to
    /// restore them. Retried at every stop and at Dispose; they block nothing.</summary>
    private readonly HashSet<string> _parkedEndpointIds = new(StringComparer.Ordinal);

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
            // quick successor, or an unmute that failed on an endpoint that is still present
            // (one that is GONE is parked instead, and blocks nothing — AUD-37).
            if (_mutedEndpointId is not null)
            {
                Logger.Debug("System audio mute already held (dev={DeviceTag}); keeping it",
                    CaptureDeviceTag.For(_mutedEndpointId));
                return;
            }

            // Parked claims are NOT retried here: that would add endpoint COM calls to the
            // recording-start path, inside this lock (AUD-11). The stop path retries them.
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
                    // A fresh claim supersedes a parked one on the same endpoint.
                    _parkedEndpointIds.Remove(attempt.EndpointId);
                    _mutedEndpointId = attempt.EndpointId;
                    Logger.Information("System audio muted (dev={DeviceTag})",
                        CaptureDeviceTag.For(attempt.EndpointId));
                }
                else if (_parkedEndpointIds.Remove(attempt.EndpointId))
                {
                    // AUD-37: a device we muted, that went away before we could restore it, is back
                    // as the default and still muted. That mute is ours — take it back as the active
                    // claim, so this stop restores it.
                    _mutedEndpointId = attempt.EndpointId;
                    Logger.Information("System audio mute held on a returned device (dev={DeviceTag})",
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
        // No setting check here (AUD-38): the setting gates APPLYING a mute (Mute()), never releasing
        // one. A check here stranded the mute of a recording during which the user turned the feature
        // off — the device stayed muted until quit. With nothing held the early-out below makes this a
        // no-op, exactly as the feature-off path was before. Do not read the setting after the delay
        // either: that would strand the same in-flight release.
        lock (_lock)
        {
            if (_mutedEndpointId is null && _parkedEndpointIds.Count == 0)
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
            UnmuteActiveClaim();
            RetryParkedClaims();
        }
    }

    private void UnmuteActiveClaim()
    {
        var endpointId = _mutedEndpointId;
        if (endpointId is null) return;

        try
        {
            // By ID, NOT by current default: on a Bluetooth headset the default render
            // endpoint has very likely changed since Mute() (HFP during capture, A2DP after).
            if (_endpoints.Unmute(endpointId) == RenderUnmuteOutcome.Unmuted)
            {
                _mutedEndpointId = null;
                Logger.Information("System audio unmuted (dev={DeviceTag})", CaptureDeviceTag.For(endpointId));
                return;
            }

            // AUD-37: the device is gone (unplugged headset or dock). Park the claim so it no
            // longer blocks muting the device that replaced it, and restore it if it returns.
            // Warning, not Error: the environment changed, nothing here is broken (Sentry
            // VOICEWINK-F was one Error per recording for a whole session).
            _mutedEndpointId = null;
            _parkedEndpointIds.Add(endpointId);
            Logger.Warning("Muted output device is no longer present; it will be unmuted if it returns (dev={DeviceTag})",
                CaptureDeviceTag.For(endpointId));
        }
        catch (Exception ex)
        {
            // Keep the claim. Mute() refuses to replace a held endpoint, so this id survives
            // until a later stop or Dispose restores it — the endpoint is still PRESENT (a gone
            // one is parked above), so this is a failure we do not understand and must see.
            Logger.Error(ex, "Failed to unmute system audio (dev={DeviceTag})",
                CaptureDeviceTag.For(endpointId));
        }
    }

    private void RetryParkedClaims()
    {
        if (_parkedEndpointIds.Count == 0) return;

        // A copy: the loop removes restored ids from the set it would otherwise be enumerating.
        foreach (var endpointId in _parkedEndpointIds.ToArray())
        {
            try
            {
                if (_endpoints.Unmute(endpointId) == RenderUnmuteOutcome.Unmuted)
                {
                    _parkedEndpointIds.Remove(endpointId);
                    Logger.Information("System audio unmuted on a returned device (dev={DeviceTag})",
                        CaptureDeviceTag.For(endpointId));
                }
                else
                {
                    // Still gone: the expected state of an unplugged device, checked every stop.
                    Logger.Debug("Muted output device still not present (dev={DeviceTag})",
                        CaptureDeviceTag.For(endpointId));
                }
            }
            catch (Exception ex)
            {
                // Keep the claim and try again at the next stop. Warning without the exception
                // object: this repeats every stop while it lasts, and a stack each time is noise.
                Logger.Warning("Could not unmute a returned output device: {ErrorType} (dev={DeviceTag})",
                    ex.GetType().Name, CaptureDeviceTag.For(endpointId));
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

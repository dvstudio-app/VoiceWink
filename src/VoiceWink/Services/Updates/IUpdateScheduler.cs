using System;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Owns the automatic, background "is there a newer version?" poll (UPD-1b): the missing
/// trigger that calls <see cref="IUpdateService.CheckForUpdatesAsync"/> with
/// <c>isManualCheck: false</c> on a schedule (~30s after launch, then every 24h — see
/// <see cref="UpdateScheduleDecision"/>). A found update raises TWO events with deliberately
/// different semantics: <see cref="UpdateAvailableDetected"/> (deduped) is the NOTIFY channel
/// behind the sidebar badge, and <see cref="UpdateReadyToInstall"/> (not deduped) is the
/// UPD-4b AUTO-INSTALL channel.
///
/// <para><b>Applying is no longer necessarily a user click (UPD-4b, 2026-08-08).</b> With
/// <see cref="VoiceWink.Helpers.AppDefaults.AutomaticUpdateInstallEnabled"/> on — the shipped
/// default — <c>AutoUpdateInstaller</c> drives
/// <see cref="IUpdateService.ApplyAndRestartAsync"/> off the second event. The scheduler itself
/// still only reports; it never applies, and it holds no policy about whether to.</para>
///
/// <para><b>Gating.</b> The scheduler is inert in builds where
/// <see cref="UpdateCheckFeature.IsEnabled"/> is <c>false</c> (dev/IDE/CI) — no loop, no
/// timer, no wake — mirroring <see cref="UpdateService"/>'s compile-time gate-1 so default
/// builds make zero network egress. It is started by <c>App.StartGatedRuntimeServices</c>
/// only after the LGL-1 legal/onboarding gate passes, so automatic checks never run ahead
/// of consent.</para>
/// </summary>
public interface IUpdateScheduler
{
    /// <summary>
    /// Raised (off the UI thread) when a background tick finds a newer version, carrying
    /// the available version string. De-duplicated: the same version is announced at most
    /// once per session. Subscribers must marshal to the UI thread before touching UI.
    /// </summary>
    event Action<string>? UpdateAvailableDetected;

    /// <summary>
    /// Raised (off the UI thread) on <b>every</b> tick that finds a newer version — carrying the
    /// available version string, and deliberately <b>NOT de-duplicated</b>, which is the whole
    /// reason it exists separately from <see cref="UpdateAvailableDetected"/> (UPD-4b).
    ///
    /// <para>The two channels want opposite things from a repeat. The badge must not be
    /// re-announced, so its channel dedupes. The auto-install must get a FRESH chance on every
    /// tick, because its first attempt can be refused for a purely transient reason (a recording
    /// in flight) — and on the deduped channel that refusal would be permanent for the process,
    /// which on a tray app launched at login can mean weeks. Sharing one deduped event was the
    /// original plan and is the defect this event was added to fix.</para>
    ///
    /// <para>Subscribers must marshal to the UI thread: <c>ApplyAndRestartAsync</c>'s synchronous
    /// prefix is UI-thread-affine (see its own invariant comment).</para>
    /// </summary>
    event Action<string>? UpdateReadyToInstall;

    /// <summary>
    /// Raised (off the UI thread) when a background tick finds the app up to date AFTER a
    /// prior tick had announced an update — i.e. the previously-advertised update was pulled
    /// or resolved externally. Fires only on the announced→cleared transition.
    /// <b>No longer wired to any UI since UPD-3b</b>: the sidebar dot and the Updates page are
    /// state-driven off <see cref="IUpdateService.PendingUpdateVersionChanged"/> (a blind
    /// event-ordered badge clear could race a newer pending version). Retained for the
    /// scheduler's announced→cleared bookkeeping and its tests.
    /// </summary>
    event Action? UpdateNoLongerAvailable;

    /// <summary>
    /// Start the background poll loop. Idempotent (a second call is a no-op while running)
    /// and inert when the build-flag gate is off. Call from behind the LGL-1 gate.
    /// </summary>
    void Start();

    /// <summary>
    /// Stop the loop and cancel any pending wait. Idempotent. Called from <c>App.Cleanup</c>
    /// before the DI container is disposed.
    /// </summary>
    void Stop();

    /// <summary>
    /// Notify the scheduler that the user toggled
    /// <see cref="VoiceWink.Helpers.AppDefaults.AutomaticUpdateCheckEnabled"/> (there is no
    /// <c>SettingsService</c> change event). Re-arms the loop so the change takes effect in
    /// the current session. A <b>no-op until <see cref="Start"/> has run</b>, so flipping
    /// the toggle during onboarding cannot start polling ahead of the legal gate, and it
    /// never creates a second loop.
    /// </summary>
    void OnAutomaticSettingChanged(bool enabled);
}

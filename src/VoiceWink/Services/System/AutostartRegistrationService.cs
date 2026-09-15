using global::System;
using Microsoft.Win32;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>
/// Idempotent HKCU\Run registry write/remove for the autostart-on-login feature.
///
/// <para>Why a service vs a direct registry call inside <c>SettingsViewModel</c>: the
/// CommunityToolkit.Mvvm <c>[ObservableProperty]</c> partial setter only fires when
/// the value <em>changes</em>. If the singleton VM already holds the default
/// <c>LaunchAtLogin=true</c>, the partial no-ops and the registry write is skipped — fresh
/// installs end up with the setting in <c>settings.json</c> but no HKCU\Run entry. The
/// service makes <see cref="Apply"/> the single source of truth: callers always invoke it
/// after writing the setting, and it idempotently rewrites the registry to match.</para>
///
/// <para>The reconciliation call from <c>App.OnLaunched</c> (after the legal/onboarding
/// gate) also uses this service — that's how existing portable users with stale VBS Run
/// entries get migrated to the right command for whatever channel they're now on.</para>
/// </summary>
public sealed class AutostartRegistrationService
{
    // Internal (not private) so VelopackUninstallCleanup and the two test
    // classes share this single source of truth for the registry coordinates
    // — same HKCU\Run path + value name the service writes, the uninstall
    // helper deletes, and both test suites pin against. Avoids the 4-site
    // duplication that previously existed and the typo-fork risk that comes
    // with copy-pasted constants.
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValueName = "VoiceWink";

    private static ILogger Logger => Log.ForContext<AutostartRegistrationService>();

    private readonly LauncherDiscovery _launcher;
    private readonly IRegistryKeyWriter _registry;

    public AutostartRegistrationService(LauncherDiscovery launcher)
        : this(launcher, new RealRegistryKeyWriter()) { }

    internal AutostartRegistrationService(LauncherDiscovery launcher, IRegistryKeyWriter registry)
    {
        _launcher = launcher;
        _registry = registry;
    }

    /// <summary>
    /// Idempotent: writes the channel-correct autostart command when <paramref name="enabled"/>
    /// is true, removes the entry when false. Safe to call repeatedly with the same value.
    /// </summary>
    /// <remarks>
    /// Logs the <see cref="LauncherKind"/> classifier rather than the full command — the raw
    /// command contains <c>C:\Users\&lt;name&gt;\...</c> paths which would land verbatim in
    /// Sentry breadcrumbs (Information sink) and conflict with the "no PII in crash reports"
    /// posture documented in README/privacy copy.
    /// </remarks>
    public void Apply(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var (command, kind) = _launcher.GetAutostart();
                if (kind == LauncherKind.Unavailable)
                {
                    // Discovery couldn't pick a confident launcher (no apphost in process,
                    // no portable VBS, no entry assembly). Writing the placeholder VBS path
                    // would clobber a previously-working entry with one that fails at next
                    // login. Preserve the existing value and surface a warning — startup
                    // reconciliation runs every launch, so a transient discovery failure
                    // self-heals on the next session.
                    Logger.Warning("Autostart enable requested but no launcher resolved; preserving existing HKCU\\Run entry");
                    return;
                }
                _registry.SetValue(RunKeyPath, RunValueName, command);
                Logger.Information("Autostart enabled (launcher: {LauncherKind})", kind);
            }
            else
            {
                _registry.DeleteValue(RunKeyPath, RunValueName);
                Logger.Information("Autostart disabled");
            }
        }
        catch (global::System.Exception ex)
        {
            Logger.Warning(ex, "Failed to {Action} autostart registry entry",
                enabled ? "set" : "remove");
        }
    }
}

internal interface IRegistryKeyWriter
{
    /// <summary>
    /// Read an HKCU value without acquiring write lock. Returns null when the
    /// key or value is missing. Added 2026-05-20 for
    /// <see cref="VelopackUninstallCleanup"/>'s ownership-guarded delete —
    /// the cleanup helper reads the current Run value, decides whether it's
    /// Velopack-owned, and only deletes when the path-segment check confirms
    /// ownership.
    /// </summary>
    string? GetValue(string keyPath, string valueName);
    void SetValue(string keyPath, string valueName, string value);
    void DeleteValue(string keyPath, string valueName);
}

internal sealed class RealRegistryKeyWriter : IRegistryKeyWriter
{
    public string? GetValue(string keyPath, string valueName)
    {
        // OpenSubKey (not CreateSubKey) — we're reading, not creating. Returns
        // null if the key doesn't exist; cast result to string? since Run
        // values are always REG_SZ.
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void SetValue(string keyPath, string valueName, string value)
    {
        // CreateSubKey (not OpenSubKey) so the Run key is created if missing on
        // exotic Windows installs.
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key?.SetValue(valueName, value);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

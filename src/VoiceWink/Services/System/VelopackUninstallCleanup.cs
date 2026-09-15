using global::System;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>
/// Pre-bootstrap-safe HKCU\Run cleanup helper called from Velopack's
/// <c>OnBeforeUninstallFastCallback</c> in <see cref="VoiceWink.Program.Main"/>.
///
/// <para><b>Problem this solves.</b> When a user uninstalls VoiceWink via
/// <c>Update.exe --uninstall</c>, Velopack removes
/// <c>%LOCALAPPDATA%\VoiceWinkApp\</c> entirely — the install root, the
/// binary, and (post-fix) <c>install.log</c>. Before this helper landed,
/// the HKCU\Run\VoiceWink registry value was left behind, still pointing
/// at the now-missing apphost. At the user's next login Windows tried to
/// launch the missing exe, which fails silently or with a brief shell
/// toast on some Windows builds. The orphaned value also remained visible
/// in Settings → Apps → Startup, Task Manager → Startup, and msconfig,
/// looking like a botched uninstall.</para>
///
/// <para><b>Why a separate helper, not <see cref="AutostartRegistrationService"/>
/// reused?</b> Two reasons. (1) <see cref="AutostartRegistrationService"/>
/// depends on <see cref="LauncherDiscovery"/> in its constructor and logs
/// via Serilog. The OnBeforeUninstall hook runs PRE-BOOTSTRAP — before
/// <c>Bootstrap.TryInitialize</c>, before WinUI is up, and before Serilog
/// is configured. Calling a DI-shaped service from there would either
/// fail or rely on uninitialised Serilog. (2) <see cref="AutostartRegistrationService.Apply"/>
/// catches and swallows registry exceptions internally — the outer
/// uninstall hook needs to see (and log to install.log) what happened
/// for operator diagnostics. A small dedicated helper with explicit
/// log-sink injection cleanly satisfies both constraints.</para>
///
/// <para><b>Ownership guard.</b> The helper only deletes the Run value
/// when it references the canonical Velopack path segment
/// <c>\VoiceWinkApp\current\</c>. This excludes legacy Inno-installed
/// users (run value points at <c>%LOCALAPPDATA%\VoiceWink\VoiceWink.exe</c>),
/// portable users (<c>wscript.exe "...\VoiceWink.vbs"</c>), siblings
/// like <c>VoiceWinkAppBackup</c> or <c>OldVoiceWinkApp</c>, and any
/// other non-Velopack writer. The path-segment check uses slashes on
/// both sides so a substring like <c>VoiceWinkApp</c> alone inside an
/// unrelated arg string doesn't false-positive.</para>
///
/// <para><b>Wrapper-command edge case.</b> A hypothetical Run value like
/// <c>SomeWrapper.exe --target "...\VoiceWinkApp\current\VoiceWink.exe"</c>
/// would also match. This is acceptable: (a) <see cref="AutostartRegistrationService.Apply"/>
/// is the only writer of our Run value AND it writes a bare quoted
/// apphost path with no wrapper prefix — a wrapper shape would have to
/// be set by some external process; (b) the wrapper would target our
/// about-to-be-deleted binary, so removing the Run entry IS the correct
/// cleanup. Do not "fix" this match into a stricter path parser without
/// understanding why the looser check is intentional.</para>
///
/// <para><b>Privacy.</b> Log lines describe the action ("removed",
/// "preserved — not Velopack-owned", "absent") WITHOUT quoting the raw
/// registry value, mirroring <see cref="AutostartRegistrationService.Apply"/>'s
/// posture (the raw value contains <c>C:\Users\&lt;name&gt;\...</c> PII).
/// Even though install.log is local-only, the same defensive habit applies.</para>
/// </summary>
internal static class VelopackUninstallCleanup
{
    // Registry coordinates come from AutostartRegistrationService (the writer
    // side) so writer + deleter cannot diverge on a typo. See the constants'
    // docstring on AutostartRegistrationService for why they're internal.
    private const string RunKeyPath   = AutostartRegistrationService.RunKeyPath;
    private const string RunValueName = AutostartRegistrationService.RunValueName;

    // Canonical Velopack path segment used as the ownership probe. Slashes
    // on both sides exclude sibling directories like VoiceWinkAppBackup or
    // OldVoiceWinkApp, and exclude a bare "VoiceWinkApp" substring
    // appearing inside an unrelated arg string.
    // INS-4 made this the SHARED definition rather than one of two identical copies: the stop
    // pass and this cleanup must agree on what "our install" means, and three doc comments
    // already asserted they could not disagree while nothing enforced it. Same pattern as
    // RunKeyPath above -- one owner, everyone else references it.
    internal const string VelopackPathSegment = @"\VoiceWinkApp\current\";

    /// <summary>
    /// Production entry — routes registry ops through <see cref="RealRegistryKeyWriter"/>
    /// and log lines to <see cref="PrereqInstaller.LogPublic"/> (the same
    /// <c>%LOCALAPPDATA%\VoiceWinkApp\install.log</c> the install hook
    /// writes to). Caller is <see cref="VoiceWink.Program.Main"/>'s
    /// Velopack <c>OnBeforeUninstallFastCallback</c>.
    /// </summary>
    public static void RemoveAutostartIfVelopackOwned() =>
        RemoveAutostartIfVelopackOwned(
            new RealRegistryKeyWriter(),
            PrereqInstaller.LogPublic);

    /// <summary>
    /// Testable entry. The injected log sink lets tests capture log lines
    /// without touching the real <c>install.log</c> file under
    /// <c>%LOCALAPPDATA%\VoiceWinkApp\</c> — important because the
    /// production log path is the directory Velopack is about to delete.
    /// </summary>
    internal static void RemoveAutostartIfVelopackOwned(
        IRegistryKeyWriter registry,
        Action<string> log)
    {
        try
        {
            var current = registry.GetValue(RunKeyPath, RunValueName);
            if (string.IsNullOrEmpty(current))
            {
                SafeLog(log, "OnBeforeUninstall autostart: HKCU\\Run\\VoiceWink absent (no cleanup needed)");
                return;
            }

            // Path-boundary ownership check. See class docstring for why
            // the slashes on both sides matter.
            if (current.IndexOf(VelopackPathSegment,
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                // Privacy: log the classification, NOT the raw value (which
                // contains user-profile PII).
                SafeLog(log, "OnBeforeUninstall autostart: HKCU\\Run\\VoiceWink does not reference Velopack path (preserved — channel migration / legacy Inno / portable / parallel install case)");
                return;
            }

            registry.DeleteValue(RunKeyPath, RunValueName);
            SafeLog(log, "OnBeforeUninstall autostart: removed HKCU\\Run\\VoiceWink (Velopack-owned)");
        }
        catch (Exception ex)
        {
            // Best-effort: never fail the uninstall over a registry op.
            // The outer try/catch covers GetValue/DeleteValue throws AND
            // any other unexpected failure inside the helper.
            SafeLog(log, $"OnBeforeUninstall autostart cleanup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Logging is best-effort; production PrereqInstaller.LogPublic already
    // swallows IO failures internally, but a test-supplied Action<string>
    // could throw if the test sink is strict. Wrap so an exception in the
    // log sink never escapes the helper.
    private static void SafeLog(Action<string> log, string line)
    {
        try { log(line); }
        catch { /* nothing else to do */ }
    }
}

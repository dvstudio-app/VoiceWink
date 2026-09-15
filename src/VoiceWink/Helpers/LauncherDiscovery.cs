using System.Diagnostics;
using System.Reflection;

namespace VoiceWink.Helpers;

/// <summary>
/// Returns the right HKCU\Run autostart command for whatever launcher is in effect today.
///
/// <para>Channel matrix (from the installer/signing/auto-update plan, decision D2):</para>
/// <list type="bullet">
///   <item>Inno-installed apphost at <see cref="AppPaths.RootDir"/>\VoiceWink.exe → that
///     path.</item>
///   <item>Velopack-installed apphost at <c>%LOCALAPPDATA%\VoiceWinkApp\current\VoiceWink.exe</c>
///     → that path. Handled by the same generic running-apphost branch as Inno; no
///     install-channel-specific code is needed because process introspection picks up
///     whichever apphost the user is actually running.</item>
///   <item>Portable install → <c>wscript.exe "&lt;root&gt;\VoiceWink.vbs"</c>. The portable
///     ZIP strips the unsigned .exe and ships a VBS that launches via <c>dotnet
///     VoiceWink.dll</c>; HKCU\Run must point at the VBS for hidden-window startup.</item>
///   <item>Dev tree launched via <c>dotnet VoiceWink.dll</c> → reconstruct as
///     <c>"&lt;dotnet&gt;" "&lt;VoiceWink.dll&gt;"</c>; using the running process exe alone
///     would store <c>dotnet.exe</c> and lose the assembly argument.</item>
///   <item>Dev tree launched via the published apphost exe → use the running process path.</item>
/// </list>
///
/// <para>Detection is process introspection first, then file existence — so that a stale
/// launcher file lying around in <see cref="AppPaths.RootDir"/> from a prior install can
/// never hijack startup reconciliation away from the apphost the user is currently running.
/// The <see cref="IFileSystemProbe"/> seam lets tests exercise each branch deterministically
/// without touching the real filesystem.</para>
/// </summary>
public sealed class LauncherDiscovery
{
    private readonly IFileSystemProbe _probe;

    public LauncherDiscovery() : this(new RealFileSystemProbe()) { }

    internal LauncherDiscovery(IFileSystemProbe probe) => _probe = probe;

    /// <summary>
    /// Returns the autostart command + a coarse <see cref="LauncherKind"/> classifier the
    /// caller can log without leaking <c>C:\Users\&lt;name&gt;\...</c> into breadcrumbs.
    ///
    /// <para>When discovery cannot resolve a confident command, returns <see
    /// cref="LauncherKind.Unavailable"/>. Callers should NOT write the returned string in
    /// that case — it's a placeholder that will fail at next login and would corrupt a
    /// working HKCU\Run entry left over from a previous successful reconciliation.</para>
    /// </summary>
    /// <remarks>
    /// Detection order is deliberate (Codex round-2 + round-3 findings on
    /// portable→Velopack and Inno→Velopack migration):
    /// <list type="number">
    ///   <item><b>Running apphost</b> (process is NOT <c>dotnet.exe</c>): always wins. The
    ///     user is currently launching VoiceWink some way; the process exe IS that launcher.
    ///     Picking it correctly handles Inno (apphost in <see cref="AppPaths.RootDir"/>),
    ///     Velopack (apphost in <c>VoiceWinkApp\current\</c>), and dev-tree apphost cases —
    ///     and never returns a stale launcher from a prior install lying around in the data
    ///     root.</item>
    ///   <item><b>Portable VBS</b>: only when running under <c>dotnet.exe</c>; the VBS gives
    ///     hidden-window startup that <c>dotnet</c> alone cannot.</item>
    ///   <item><b>Dotnet host + entry assembly</b>: dev tree without a portable VBS.</item>
    ///   <item><b>Unavailable</b>: nothing else worked; surface a warning at the call site.</item>
    /// </list>
    /// File-existence-only checks intentionally aren't first: a stale legacy
    /// <c>VoiceWink.exe</c> or <c>VoiceWink.vbs</c> in the data root would otherwise win
    /// over a Velopack apphost the user is actually running, locking startup reconciliation
    /// onto the wrong launcher.
    /// </remarks>
    public (string Command, LauncherKind Kind) GetAutostart()
    {
        var launch = GetLaunch();
        return (launch.CommandLine, launch.Kind);
    }

    /// <summary>
    /// TRN-59: the same detection as <see cref="GetAutostart"/>, returned in the shape a
    /// <c>ProcessStartInfo</c> needs (<see cref="LaunchCommand.FileName"/> + <see cref="LaunchCommand.Arguments"/>)
    /// beside the one-string HKCU\Run form. ONE detection pass produces both, so the self-restart
    /// and the autostart can never disagree about which launcher this process is. A caller that
    /// gets <see cref="LauncherKind.Unavailable"/> must not start the command — it is the same
    /// fail-loudly placeholder the autostart returns.
    /// </summary>
    public LaunchCommand GetLaunch()
    {
        var hostExe = _probe.GetCurrentProcessFileName();
        var entryAssembly = _probe.GetEntryAssemblyLocation();
        var hostIsDotnet = !string.IsNullOrEmpty(hostExe)
            && string.Equals(
                Path.GetFileNameWithoutExtension(hostExe),
                "dotnet",
                StringComparison.OrdinalIgnoreCase);

        // Running apphost wins. Whether it's at RootDir (Inno), under VoiceWinkApp\current\
        // (Velopack), or in a dev publish folder, the process exe IS the right autostart
        // target.
        if (!string.IsNullOrEmpty(hostExe) && !hostIsDotnet)
        {
            var kind = ClassifyApphost(hostExe);
            return new LaunchCommand(hostExe, "", $"\"{hostExe}\"", kind);
        }

        // Portable VBS only kicks in when hosted by dotnet — the VBS was the way
        // unsigned-exe-blocked enterprise users got hidden-window startup.
        //
        // TRN-59: the STARTABLE form names wscript.exe by its System32 path. CreateProcess resolves
        // a bare name through the CURRENT DIRECTORY before the system directory, and the portable
        // VBS sets that directory to the user-writable install root — so a planted wscript.exe
        // there would be what "Restart now" runs. The HKCU\Run string keeps the bare name it has
        // always had (the shell launches Run entries from a system directory, and changing the
        // value would churn every portable user's entry for nothing).
        var portableVbs = Path.Combine(AppPaths.RootDir, "VoiceWink.vbs");
        var wscript = Path.Combine(Environment.SystemDirectory, "wscript.exe");
        if (_probe.FileExists(portableVbs))
            return new LaunchCommand(wscript, $"\"{portableVbs}\"", $"wscript.exe \"{portableVbs}\"", LauncherKind.PortableVbs);

        // Dev tree under dotnet hosting without a portable VBS — reconstruct the full
        // command so HKCU\Run launches dotnet WITH the assembly argument, not bare.
        if (hostIsDotnet && !string.IsNullOrEmpty(entryAssembly))
            return new LaunchCommand(hostExe!, $"\"{entryAssembly}\"", $"\"{hostExe}\" \"{entryAssembly}\"", LauncherKind.DotnetHost);

        // Last resort: a VBS-shaped path that doesn't exist. Autostart will fail loudly
        // at next login rather than misfire silently.
        return new LaunchCommand(wscript, $"\"{portableVbs}\"", $"wscript.exe \"{portableVbs}\"", LauncherKind.Unavailable);
    }

    /// <summary>
    /// Diagnostic-only classifier for the running apphost path. The autostart
    /// command itself is always the bare apphost path — the kind is only used
    /// in privacy-safe log breadcrumbs to distinguish channels at a glance.
    ///
    /// <list type="bullet">
    ///   <item><c>InstalledExe</c> — Inno-era apphost at <c>%LOCALAPPDATA%\VoiceWink\VoiceWink.exe</c>
    ///     (the user-data root, where the legacy Inno installer put the apphost).</item>
    ///   <item><c>Velopack</c> — Velopack-installed apphost under
    ///     <c>%LOCALAPPDATA%\VoiceWinkApp\current\</c>. The path-boundary
    ///     check uses <c>\VoiceWinkApp\current\</c> with slashes on both
    ///     sides so sibling dirs like <c>VoiceWinkAppBackup\current\</c>
    ///     or <c>OldVoiceWinkApp\current\</c> do NOT false-positive.</item>
    ///   <item><c>Apphost</c> — anything else (typically a dev-tree publish
    ///     folder, e.g. <c>C:\GitHub\VoiceWink\src\VoiceWink\bin\…\VoiceWink.exe</c>).</item>
    /// </list>
    /// </summary>
    private LauncherKind ClassifyApphost(string hostExe)
    {
        if (string.Equals(hostExe, Path.Combine(AppPaths.RootDir, "VoiceWink.exe"),
                StringComparison.OrdinalIgnoreCase))
        {
            return LauncherKind.InstalledExe;
        }

        var localAppData = _probe.GetLocalAppDataDir();
        if (!string.IsNullOrEmpty(localAppData))
        {
            // Use slashes on both sides ("\VoiceWinkApp\current\") so a
            // sibling like VoiceWinkAppBackup\current\ doesn't false-positive.
            // Build the canonical Velopack-root prefix with the trailing
            // separator so a prefix check is strict on the directory boundary.
            var velopackRootWithSep = Path.Combine(localAppData, "VoiceWinkApp", "current")
                + Path.DirectorySeparatorChar;
            if (hostExe.StartsWith(velopackRootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return LauncherKind.Velopack;
            }
        }

        return LauncherKind.Apphost;
    }
}

/// <summary>
/// TRN-59: one launcher, in both shapes it is consumed — <see cref="FileName"/> + <see cref="Arguments"/>
/// for a <c>ProcessStartInfo</c>, <see cref="CommandLine"/> for HKCU\Run. Produced by
/// <see cref="LauncherDiscovery.GetLaunch"/> in one pass. The raw members carry
/// <c>C:\Users\&lt;name&gt;\…</c>; log <see cref="Kind"/> only.
/// </summary>
public sealed record LaunchCommand(string FileName, string Arguments, string CommandLine, LauncherKind Kind);

/// <summary>
/// Coarse classifier for which channel <see cref="LauncherDiscovery.GetAutostart"/> picked.
/// Safe to log — contains no PII unlike the raw command string.
/// </summary>
public enum LauncherKind
{
    InstalledExe,
    PortableVbs,
    DotnetHost,
    Apphost,
    /// <summary>
    /// Apphost living under <c>%LOCALAPPDATA%\VoiceWinkApp\current\</c> —
    /// the Velopack install layout. Diagnostic-only: the autostart command
    /// itself is identical to <see cref="Apphost"/>, but logs can now
    /// distinguish "Velopack-installed user" from "dev-tree apphost" at
    /// a glance without leaking the raw path into log breadcrumbs.
    /// </summary>
    Velopack,
    Unavailable,
}

internal interface IFileSystemProbe
{
    bool FileExists(string path);
    string? GetCurrentProcessFileName();
    string? GetEntryAssemblyLocation();
    /// <summary>
    /// Returns the user's <c>%LOCALAPPDATA%</c> path. Added 2026-05-20 to
    /// support the Velopack-root path-boundary check in
    /// <see cref="LauncherDiscovery.GetAutostart"/>'s apphost classifier.
    /// Seam exists so tests can pin a deterministic root without depending
    /// on the running user's actual profile path.
    /// </summary>
    string? GetLocalAppDataDir();
}

internal sealed class RealFileSystemProbe : IFileSystemProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public string? GetCurrentProcessFileName() =>
        Process.GetCurrentProcess().MainModule?.FileName;

    public string? GetEntryAssemblyLocation() =>
        Assembly.GetEntryAssembly()?.Location;

    public string? GetLocalAppDataDir() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}

using System;
using System.IO;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Updates;

/// <summary>
/// Where the running copy of VoiceWink is installed, and when that installation was made — the
/// IDENTITY the install-source signal is keyed on (dvstudio-metrics #85).
///
/// <para>A Velopack install runs from <c>%LOCALAPPDATA%\VoiceWinkApp\current\</c>; the root is its
/// parent. Velopack swaps <c>current\</c> on every update but never the root, and a reinstall recreates
/// it — so the root's creation time names ONE installation, which is what "report once per install"
/// needs. The app's data folder (<c>%LOCALAPPDATA%\VoiceWink</c>, where settings live) survives an
/// uninstall and so cannot name an installation. The segment is the one
/// <see cref="VelopackUninstallCleanup.VelopackPathSegment"/> already defines, so the two can never
/// disagree about what "the install" means. A dev tree, a portable copy or a <c>dotnet VoiceWink.dll</c>
/// host has no root, and the whole signal is then a no-op.</para>
/// </summary>
public static class InstallRoot
{
    /// <summary>The install root for a process whose base directory is <paramref name="baseDirectory"/>,
    /// or null when it is not a Velopack install.</summary>
    public static string? FromBaseDirectory(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
        var withSeparator = baseDirectory.EndsWith('\\') ? baseDirectory : baseDirectory + "\\";
        var segment = VelopackUninstallCleanup.VelopackPathSegment; // \VoiceWinkApp\current\
        if (!withSeparator.EndsWith(segment, StringComparison.OrdinalIgnoreCase)) return null;
        // Keep "\VoiceWinkApp", drop "current\".
        return withSeparator[..(withSeparator.Length - "current\\".Length - 1)];
    }

    /// <summary>The install root's creation time in UTC, or null when there is no root or it cannot be read.</summary>
    public static DateTime? CreationTimeUtc(string? baseDirectory)
    {
        var root = FromBaseDirectory(baseDirectory);
        if (root is null) return null;
        try
        {
            return Directory.Exists(root) ? Directory.GetCreationTimeUtc(root) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

namespace VoiceWink.Helpers;

/// <summary>
/// Workaround for a Velopack 0.0.1298 signed-install bug where
/// <c>vpk pack</c> with the <c>azureTrustedSignFile</c> arg sometimes fails
/// to extract <c>Squirrel.exe → Update.exe</c> at the install root. Without
/// <c>Update.exe</c> the <c>UpdateManager</c> constructor will reject the
/// app as "not a Velopack installation" and auto-update breaks silently.
///
/// Strategy: re-extract just the <c>lib/app/Squirrel.exe</c> entry from a
/// full nupkg in the <c>packages\</c> directory, stream it to a temp file in
/// the install root, then atomic File.Move to <c>Update.exe</c>. Pure
/// managed code, pure file IO — no WinUI dependency, no native runtime
/// dependency. Safe to call from any pre-bootstrap stage.
///
/// Called from two sites:
/// <list type="number">
/// <item>Velopack's <c>OnAfterInstallFastCallback</c> — install path.</item>
/// <item><c>Program.Main</c> stage 2 — normal-launch defense in depth in
///   case the hook didn't fire or didn't deploy (e.g. in-place upgrades
///   where Velopack doesn't re-run the after-install hook).</item>
/// </list>
///
/// Guards (all five must pass to attempt extraction; any failure short-
/// circuits silently or with a single log line):
/// <list type="number">
/// <item>Layout: running from <c>&lt;install-root&gt;\current\</c>. Dev
///   launches (<c>dotnet VoiceWink.dll</c>) have a different
///   <c>BaseDirectory</c> and skip silently — no log spam.</item>
/// <item><c>packages\</c> directory exists (Velopack installed-layout
///   invariant).</item>
/// <item><c>Update.exe</c> missing OR zero-size (treat zero-size as
///   broken). If it exists and is non-empty, idempotent no-op.</item>
/// <item>A full nupkg (no <c>-delta</c> suffix) is found in
///   <c>packages\</c>; pick the lexicographically-highest by file name so
///   we get the latest version.</item>
/// <item>The expected zip entry <c>lib/app/Squirrel.exe</c> is present in
///   that nupkg.</item>
/// </list>
///
/// See <c>docs/plans/2026-05-05-2103-installer-signing-autoupdate-plan/50-decision.md</c>
/// override item 10 for full context.
///
/// <para><b>Test seams (UPD-1 Phase 4):</b> the public <see cref="EnsureDeployed()"/>
/// resolves <c>AppContext.BaseDirectory</c> + the production filesystem / zip
/// implementations and forwards to the internal
/// <see cref="EnsureDeployed(string, IFileSystem, IZipReader, global::System.Action{string})"/>
/// overload, which the <c>VoiceWink.Tests</c> project drives with in-memory
/// fakes. Production behavior is unchanged — the overload is the same code path
/// with the IO indirected through interfaces.</para>
/// </summary>
internal static class UpdateExeSelfHeal
{
    private const string ExpectedZipEntry = "lib/app/Squirrel.exe";
    private const string UpdateExeName = "Update.exe";
    private const string InstallRootLeafName = "current";

    /// <summary>Production entry point. Resolves the real BaseDirectory + IO
    /// seams and logs through <see cref="PrereqInstaller.LogPublic"/>.</summary>
    public static void EnsureDeployed() =>
        EnsureDeployed(
            global::System.AppContext.BaseDirectory,
            ProductionFileSystem.Instance,
            ProductionZipReader.Instance,
            PrereqInstaller.LogPublic);

    /// <summary>
    /// Seam-injected core. Identical control flow to the original static body;
    /// every filesystem / zip touch is routed through <paramref name="fs"/> /
    /// <paramref name="zip"/> and every log line through <paramref name="log"/>.
    /// Internal so the test project can drive it with fakes
    /// (<c>InternalsVisibleTo VoiceWink.Tests</c>).
    /// </summary>
    internal static void EnsureDeployed(
        string baseDirectory,
        IFileSystem fs,
        IZipReader zip,
        global::System.Action<string> log)
    {
        try
        {
            // Guard 1: layout — running from <install-root>\current\.
            // Dev launches (dotnet VoiceWink.dll) or non-Velopack contexts
            // have a different BaseDirectory and fail this check silently.
            var baseDir = baseDirectory
                .TrimEnd(global::System.IO.Path.DirectorySeparatorChar,
                         global::System.IO.Path.AltDirectorySeparatorChar);
            var leafName = global::System.IO.Path.GetFileName(baseDir);
            if (!string.Equals(leafName, InstallRootLeafName,
                global::System.StringComparison.OrdinalIgnoreCase))
            {
                // Not in installed layout — skip silently (no log spam on dev launches).
                return;
            }
            var installRoot = global::System.IO.Path.GetDirectoryName(baseDir);
            if (string.IsNullOrEmpty(installRoot))
            {
                log("self-heal: could not determine install root from BaseDirectory");
                return;
            }

            // Guard 2: packages\ must exist (Velopack installed layout invariant).
            var packagesDir = global::System.IO.Path.Combine(installRoot, "packages");
            if (!fs.DirectoryExists(packagesDir))
            {
                log($"self-heal: packages\\ does not exist at {packagesDir}; skipping");
                return;
            }

            // Guard 3: Update.exe missing (or zero-size, treat as broken).
            var updateExePath = global::System.IO.Path.Combine(installRoot, UpdateExeName);
            if (fs.FileExists(updateExePath))
            {
                if (fs.GetFileLength(updateExePath) > 0)
                {
                    // Idempotent no-op: Update.exe already deployed (by
                    // Velopack upstream fix, or a prior self-heal run, or
                    // manual placement).
                    return;
                }
                log($"self-heal: Update.exe exists at {updateExePath} but is zero-size; redeploying");
            }

            // Guard 4: find a FULL nupkg (skip -delta) — prefer the latest.
            //
            // Codex review (2026-05-19 diff loop) caught a lexicographic sort bug
            // here: comparing `VoiceWinkApp-1.0.10-full.nupkg` vs `VoiceWinkApp-1.0.9
            // -full.nupkg` by ordinal string compare would pick `1.0.9` because `9`
            // sorts after `1` in ASCII. Parse the version out of the filename and
            // compare as System.Version so the numerically-highest one always wins.
            // Files that don't match the expected `<id>-<version>-full.nupkg` shape
            // (e.g. malformed manual additions) fall back to ordinal compare so we
            // never silently skip a candidate.
            string? fullNupkg = null;
            global::System.Version? fullNupkgVersion = null;
            foreach (var path in fs.EnumerateFiles(packagesDir, "*.nupkg"))
            {
                var name = global::System.IO.Path.GetFileNameWithoutExtension(path);
                if (name.IndexOf("-delta", global::System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue; // skip delta nupkgs
                }

                var candidateVersion = TryExtractFullNupkgVersion(name);

                if (fullNupkg == null)
                {
                    fullNupkg = path;
                    fullNupkgVersion = candidateVersion;
                    continue;
                }

                // Both parsed → numeric compare (handles 1.0.10 > 1.0.9 correctly).
                // Exactly one parsed → prefer the parsed candidate (more trustworthy
                // signal than ordinal). Neither parsed → fall back to ordinal.
                bool replace;
                if (candidateVersion != null && fullNupkgVersion != null)
                {
                    replace = candidateVersion > fullNupkgVersion;
                }
                else if (candidateVersion != null)
                {
                    replace = true;
                }
                else if (fullNupkgVersion != null)
                {
                    replace = false;
                }
                else
                {
                    replace = global::System.StringComparer.OrdinalIgnoreCase.Compare(path, fullNupkg) > 0;
                }

                if (replace)
                {
                    fullNupkg = path;
                    fullNupkgVersion = candidateVersion;
                }
            }
            if (fullNupkg == null)
            {
                log($"self-heal: no full .nupkg in {packagesDir} (only deltas?); skipping");
                return;
            }

            // Guard 5: the expected entry must be present in the nupkg.
            string tempPath = updateExePath + ".tmp-selfheal";
            try
            {
                // Best-effort cleanup of stale temp from prior interrupted run.
                if (fs.FileExists(tempPath))
                {
                    fs.DeleteFile(tempPath);
                }

                using (var handle = zip.OpenRead(fullNupkg))
                {
                    // OpenEntry folds GetEntry + Open: null means the entry is
                    // absent (Guard 5 fail). The output path is ALWAYS the fixed
                    // updateExePath derived from BaseDirectory — never the zip
                    // entry name — so a hostile nupkg with traversal-named
                    // entries cannot escape the install root.
                    using var srcStream = handle.OpenEntry(ExpectedZipEntry);
                    if (srcStream == null)
                    {
                        var nupkgName = global::System.IO.Path.GetFileName(fullNupkg);
                        log("self-heal: " + ExpectedZipEntry + " not in " + nupkgName + "; skipping");
                        return;
                    }
                    // Stream the single entry to a temp file in the install
                    // root. Same drive => atomic File.Move(overwrite: true)
                    // on NTFS is the closest we get to a guaranteed atomic
                    // replace.
                    using var dstStream = fs.CreateFile(tempPath);
                    srcStream.CopyTo(dstStream);
                }

                fs.MoveFile(tempPath, updateExePath, overwrite: true);

                var deployedSize = fs.GetFileLength(updateExePath);
                var nupkgFileName = global::System.IO.Path.GetFileName(fullNupkg);
                log(
                    "self-heal: extracted " + ExpectedZipEntry + " from " +
                    nupkgFileName + " → Update.exe (" + deployedSize + " bytes)");
            }
            finally
            {
                // Defensive: if the extract-to-temp threw between Create and
                // Move, the temp file might still be around. Clean it up.
                try
                {
                    if (fs.FileExists(tempPath))
                    {
                        fs.DeleteFile(tempPath);
                    }
                }
                catch { /* best-effort */ }
            }
        }
        catch (global::System.Exception ex)
        {
            log($"self-heal: FAIL: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract the SemVer-shaped version segment from a Velopack full-nupkg
    /// file name. Two real-world Velopack 0.0.1298 shapes need to be handled:
    /// <list type="bullet">
    /// <item><c>&lt;packId&gt;-&lt;version&gt;-full</c> — older / no-channel
    ///   pack form. e.g. <c>VoiceWinkApp-1.0.0-full</c></item>
    /// <item><c>&lt;packId&gt;-&lt;version&gt;-&lt;channel&gt;-full</c> —
    ///   what <c>vpk pack --channel win-x64-stable</c> produces. e.g.
    ///   <c>VoiceWinkApp-1.23.268-win-x64-stable-full</c></item>
    /// </list>
    /// The channel can itself contain dashes (e.g. <c>win-x64-stable</c>),
    /// so taking the segment between the last dash and <c>-full</c> doesn't
    /// work for the channel-suffixed shape.
    ///
    /// <para>Strategy: find the FIRST occurrence of a semver-shaped
    /// <c>digit+.digit+.digit+</c> substring in the name (after stripping
    /// <c>-full</c>). Velopack always puts the version immediately after
    /// the packId, so the first such substring is the version. Pre-release
    /// suffixes (e.g. <c>1.0.0-spike</c>) are intentionally truncated to
    /// the semver core for sort purposes — picking <c>1.0.0</c> from a
    /// <c>1.0.0-spike-full</c> name is still better than falling back to
    /// ordinal compare and reviving the 1.0.10 &lt; 1.0.9 bug.</para>
    ///
    /// <para>Codex 2026-05-20 release-pipeline review iteration 3 caught
    /// the channel-suffix bug: the prior implementation took the segment
    /// after the last dash, which yielded "stable" for the real production
    /// shape and returned null. That null then fell back to ordinal
    /// compare and re-armed the very bug the parser was added to prevent.</para>
    ///
    /// <para>Returns <see cref="global::System.Version"/> on success; null
    /// only when no semver substring is anywhere in the name (malformed
    /// names; missing <c>-full</c> suffix; <c>-delta</c> suffix; etc.).</para>
    ///
    /// <para>Internal so the test project can exercise it directly without
    /// needing a full filesystem seam.</para>
    /// </summary>
    internal static global::System.Version? TryExtractFullNupkgVersion(string nameWithoutExt)
    {
        const string FullSuffix = "-full";
        if (!nameWithoutExt.EndsWith(FullSuffix, global::System.StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var withoutSuffix = nameWithoutExt.Substring(0, nameWithoutExt.Length - FullSuffix.Length);

        // First semver-shaped substring wins (Velopack puts the version
        // immediately after the packId, before any channel suffix).
        var match = global::System.Text.RegularExpressions.Regex.Match(
            withoutSuffix, @"\d+\.\d+\.\d+");
        if (!match.Success)
        {
            return null;
        }

        return global::System.Version.TryParse(match.Value, out var parsed) ? parsed : null;
    }
}

/// <summary>
/// Read-only zip seam for <see cref="UpdateExeSelfHeal"/>. Abstracts the single
/// "open this nupkg, give me a read stream for one entry (or null if absent)"
/// operation so tests can supply an in-memory entry — including a stream that
/// throws mid-copy to exercise the temp-cleanup-on-failure path — without
/// building real zip files. Production is <see cref="ProductionZipReader"/>,
/// a verbatim <see cref="global::System.IO.Compression.ZipFile"/> wrapper.
/// </summary>
internal interface IZipReader
{
    /// <summary>Open a zip archive for reading. Mirrors
    /// <see cref="global::System.IO.Compression.ZipFile.OpenRead(string)"/> —
    /// throws on a missing/corrupt archive (the caller's outer try/catch
    /// handles it).</summary>
    IZipArchiveHandle OpenRead(string archivePath);
}

/// <summary>An opened zip archive. Dispose releases the archive; any entry
/// stream handed out by <see cref="OpenEntry"/> must be read/disposed before
/// this handle is disposed.</summary>
internal interface IZipArchiveHandle : global::System.IDisposable
{
    /// <summary>Open a read stream for <paramref name="entryFullName"/>, or
    /// return null if the entry is absent. Folds
    /// <c>ZipArchive.GetEntry(name)?.Open()</c>.</summary>
    global::System.IO.Stream? OpenEntry(string entryFullName);
}

/// <summary>Production <see cref="IZipReader"/> — wraps <c>System.IO.Compression</c>.</summary>
internal sealed class ProductionZipReader : IZipReader
{
    internal static readonly ProductionZipReader Instance = new();
    private ProductionZipReader() { }

    public IZipArchiveHandle OpenRead(string archivePath) =>
        new Handle(global::System.IO.Compression.ZipFile.OpenRead(archivePath));

    private sealed class Handle : IZipArchiveHandle
    {
        private readonly global::System.IO.Compression.ZipArchive _archive;
        public Handle(global::System.IO.Compression.ZipArchive archive) => _archive = archive;

        public global::System.IO.Stream? OpenEntry(string entryFullName) =>
            _archive.GetEntry(entryFullName)?.Open();

        public void Dispose() => _archive.Dispose();
    }
}

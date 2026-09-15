namespace VoiceWink.Helpers;

/// <summary>
/// The shell identity VoiceWink declares to Windows — the AppUserModelID (AUMID) the taskbar
/// groups windows by.
///
/// <para>Separate from <c>App.xaml.cs</c> for two reasons: AGENTS.md forbids adding new
/// responsibilities to that hub, and <c>scripts/check-app-identity.ps1</c> needs ONE anchor to
/// parse. The gate is what makes the coupling below enforceable — before UI-16 the only thing
/// linking these two values was a source comment, and the comment was wrong for months.</para>
/// </summary>
internal static class AppIdentity
{
    /// <summary>
    /// The AUMID this process declares, and the one Velopack stamps onto every shortcut it creates.
    ///
    /// <para><b>The value is <c>"velopack." + --packId</c>, NOT the pack id verbatim.</b> Velopack
    /// writes it into the generated nuspec as <c>&lt;shortcutAmuid&gt;</c> — read 2026-09-07 out of a
    /// locally-held pack (<c>VoiceWinkApp-1.23.284-win-x64-beta-full.nupkg</c>;
    /// <c>releases/</c> is gitignored, so that artifact is NOT in a fresh clone and this sentence is
    /// the record): <c>&lt;shortcutAmuid&gt;velopack.VoiceWinkApp&lt;/shortcutAmuid&gt;</c>.
    /// vpk 0.0.1298 (pinned by <c>.github/workflows/release.yml</c>) exposes no override for it —
    /// its <c>pack</c> command has no <c>--shortcutAmuid</c> flag — so the app is the side that has
    /// to match, not the installer. Pack mode re-reads this on every release rather than trusting
    /// the sentence.</para>
    ///
    /// <para><b>UI-16 (2026-09-07):</b> this was <c>"VoiceWinkApp"</c> — the pack id verbatim — so the
    /// two declared identities disagreed. A 2026-05-19 Codex round had correctly found the previous
    /// mismatch (<c>"DvStudio.VoiceWink"</c>) and changed it to the pack id; the mechanism it stated
    /// (Velopack derives the shortcut AUMID from <c>--packId</c>) was right, but it ASSUMED the
    /// derivation was verbatim and missed the prefix — still wrong, differently. Do not "simplify"
    /// this back to the pack id: <c>scripts/check-app-identity.ps1</c> fails the build if you do,
    /// and its pack mode fails the RELEASE if Velopack's own output ever stops matching.</para>
    ///
    /// <para><b>What was measured, and what was not.</b> MEASURED: the two AUMIDs disagreed, and the
    /// MainWindow carries no per-window <c>System.AppUserModel.ID</c> or <c>RelaunchCommand</c>, so
    /// the shell falls back to this process-level value. INFERRED: why the duplicate button only
    /// appeared after an update (Explorer keeps a launch-time association that masks the mismatch
    /// until the app is restarted by an update), and what identity a pin created from the running
    /// window carries. The fix does not rest on the inference — two declared identities disagreeing
    /// is a defect on its own terms.</para>
    ///
    /// <para><b>Do not "make the other VoiceWinkApp strings consistent with this one."</b> They are a
    /// DIFFERENT value that merely looks the same — the bare pack id — and every one of them is
    /// correct as it stands: the version subdirectory <c>%LOCALAPPDATA%\VoiceWinkApp\current\</c>
    /// matched by <see cref="LauncherDiscovery"/>, <c>VelopackUninstallCleanup</c> and
    /// <c>UninstallRunningInstanceStop</c>; the install ROOT <c>%LOCALAPPDATA%\VoiceWinkApp\</c>
    /// holding <c>PrereqInstaller</c>'s <c>install.pid</c> / <c>install.log</c> and redacted by
    /// <c>LogRedactionEnricher</c>; the nupkg filenames <c>UpdateExeSelfHeal</c> parses; and the
    /// <c>appId</c> Velopack's <c>GetReleaseFeed</c> takes
    /// (<c>CloudflareAccessFileDownloaderTests</c>, <c>VelopackManifestAuthProbeTests</c>) — that
    /// last one is the sharpest trap, because prefixing it would break
    /// <c>releases.&lt;channel&gt;.json</c> resolution and kill auto-update silently.
    /// Both values derive from <c>--packId</c>, which is why the gate pins the pack id LITERAL rather
    /// than merely requiring the release scripts to agree with each other.</para>
    /// </summary>
    internal const string AppUserModelId = "velopack.VoiceWinkApp";
}

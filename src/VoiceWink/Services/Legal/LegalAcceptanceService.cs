using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Legal;

/// <summary>
/// In-flight legal acceptance state — current bundled hashes vs. accepted hashes.
/// </summary>
public sealed record LegalAcceptanceState(
    string CurrentEulaHash,
    string CurrentPrivacyHash,
    string? AcceptedEulaHash,
    string? AcceptedPrivacyHash)
{
    public bool RequiresAcceptance =>
        !string.Equals(AcceptedEulaHash, CurrentEulaHash, StringComparison.Ordinal) ||
        !string.Equals(AcceptedPrivacyHash, CurrentPrivacyHash, StringComparison.Ordinal);

    public bool IsFirstAcceptance =>
        AcceptedEulaHash is null && AcceptedPrivacyHash is null;
}

/// <summary>Thrown by <see cref="LegalAcceptanceService.Accept"/> on persistence failure.</summary>
public sealed class LegalAcceptanceWriteException : Exception
{
    public LegalAcceptanceWriteException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Owns the legal acceptance lifecycle (LGL-1): reads the bundled EULA and Privacy
/// Policy at construction, hashes the bodies, exposes a predicate seam for the
/// startup gate, and persists acceptance hashes through <see cref="SettingsService"/>.
///
/// <para>The decision predicate <see cref="RequiresPromptAtStartup"/> is the
/// testable seam — analogous to <see cref="VoiceWink.Services.Licensing.LicenseService.RequiresStartupRedirect"/>.
/// Hash-based acceptance ensures any in-place body edit (DRAFT → final text)
/// re-prompts every user, even without a version-header bump.</para>
///
/// <para>Bundle corruption is fail-closed at the gate
/// (<see cref="RequiresPromptAtStartup"/> returns true when
/// <see cref="BundleHealthy"/> is false) so a broken release surfaces a hard
/// "reinstall" path rather than silently allowing access — Codex review
/// challenge from <c>40-codex-final-check.md</c>.</para>
/// </summary>
public sealed class LegalAcceptanceService
{
    private static ILogger Logger => Log.ForContext<LegalAcceptanceService>();

    public const string EulaAssetPath = "Assets/Legal/eula-v5.md";
    public const string PrivacyAssetPath = "Assets/Legal/privacy-v5.md";

    private readonly SettingsService _settings;

    public LegalDocumentMetadata? Eula { get; }
    public LegalDocumentMetadata? Privacy { get; }
    public bool BundleHealthy => Eula is not null && Privacy is not null;

    /// <summary>Production constructor: reads from <c>AppContext.BaseDirectory</c>.</summary>
    public LegalAcceptanceService(SettingsService settings)
        : this(settings,
            eulaAssetPath: Path.Combine(AppContext.BaseDirectory, EulaAssetPath),
            privacyAssetPath: Path.Combine(AppContext.BaseDirectory, PrivacyAssetPath),
            fileReader: File.ReadAllText)
    { }

    /// <summary>
    /// Test seam: explicit asset paths + injected file reader. Used by
    /// VoiceWink.Tests via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal LegalAcceptanceService(
        SettingsService settings,
        string eulaAssetPath,
        string privacyAssetPath,
        Func<string, string> fileReader)
    {
        _settings = settings;
        Eula = TryParse(eulaAssetPath, fileReader, "EULA");
        Privacy = TryParse(privacyAssetPath, fileReader, "Privacy Policy");

        if (BundleHealthy)
        {
            Logger.Information(
                "Legal bundle: EULA v{EulaVersion} hash {EulaHash16}, Privacy v{PrivacyVersion} hash {PrivacyHash16}",
                Eula!.Version, Eula!.ContentHash[..16],
                Privacy!.Version, Privacy!.ContentHash[..16]);
        }
        else
        {
            Logger.Warning("Legal bundle UNHEALTHY (Eula={EulaOk}, Privacy={PrivacyOk}); gate will fail-closed",
                Eula is not null, Privacy is not null);
        }
    }

    private static LegalDocumentMetadata? TryParse(string path, Func<string, string> reader, string label)
    {
        try
        {
            var raw = reader(path);
            return LegalDocumentParser.Parse(raw);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load {Label} from {Path}", label, path);
            return null;
        }
    }

    /// <summary>
    /// Read the current acceptance state. Throws when the bundle is unhealthy —
    /// callers should check <see cref="BundleHealthy"/> first, OR use
    /// <see cref="RequiresPromptAtStartup"/> which fail-closes correctly.
    /// </summary>
    public LegalAcceptanceState GetState()
    {
        if (!BundleHealthy)
            throw new InvalidOperationException("Cannot GetState when bundle is unhealthy.");

        var acceptedEula = _settings.GetString(AppDefaults.AcceptedEulaVersion, "");
        var acceptedPrivacy = _settings.GetString(AppDefaults.AcceptedPrivacyVersion, "");

        return new LegalAcceptanceState(
            CurrentEulaHash: Eula!.ContentHash,
            CurrentPrivacyHash: Privacy!.ContentHash,
            AcceptedEulaHash: string.IsNullOrEmpty(acceptedEula) ? null : acceptedEula,
            AcceptedPrivacyHash: string.IsNullOrEmpty(acceptedPrivacy) ? null : acceptedPrivacy);
    }

    /// <summary>
    /// Predicate seam used by <c>App.EnsureLegalAcceptanceAsync</c>. Returns
    /// <c>true</c> when the gate should fire — including the bundle-unhealthy
    /// case (fail-closed: surface a "reinstall" dialog rather than silently
    /// allowing access to an app whose terms can't be displayed).
    /// </summary>
    public bool RequiresPromptAtStartup()
    {
        if (!BundleHealthy) return true;
        return GetState().RequiresAcceptance;
    }

    /// <summary>
    /// Persist the current bundled hashes as accepted. Throws
    /// <see cref="LegalAcceptanceWriteException"/> on disk-write failure. Callers
    /// (onboarding step, dialog) MUST handle the throw and avoid advancing state.
    ///
    /// <para>Rollback restores prior accepted hashes (not empty strings) so a
    /// failed write of a v2 hash doesn't erase evidence of a prior v1 acceptance —
    /// Codex review challenge from <c>70-codex-final-check-v2.md</c>.</para>
    /// </summary>
    public void Accept()
    {
        if (!BundleHealthy)
            throw new InvalidOperationException("Cannot Accept against an unhealthy bundle.");

        var priorEula = _settings.GetString(AppDefaults.AcceptedEulaVersion, "");
        var priorPrivacy = _settings.GetString(AppDefaults.AcceptedPrivacyVersion, "");

        _settings.SetString(AppDefaults.AcceptedEulaVersion, Eula!.ContentHash);
        _settings.SetString(AppDefaults.AcceptedPrivacyVersion, Privacy!.ContentHash);

        try
        {
            _settings.FlushOrThrow();
        }
        catch (Exception ex)
        {
            _settings.SetString(AppDefaults.AcceptedEulaVersion, priorEula);
            _settings.SetString(AppDefaults.AcceptedPrivacyVersion, priorPrivacy);
            try { _settings.FlushOrThrow(); }
            catch (Exception rollbackEx)
            {
                Logger.Warning(rollbackEx, "Legal acceptance rollback flush failed");
            }
            throw new LegalAcceptanceWriteException("Failed to persist legal acceptance", ex);
        }

        Logger.Information(
            "User accepted EULA hash {EulaHash16} + Privacy hash {PrivacyHash16}",
            Eula.ContentHash[..16], Privacy.ContentHash[..16]);

        try { Sentry.SentrySdk.AddBreadcrumb(
            message: $"Legal accepted: eula={Eula.ContentHash[..16]}, privacy={Privacy.ContentHash[..16]}",
            category: "legal"); }
        catch { /* Sentry not initialised — ignore */ }
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.Updates;

namespace VoiceWink.Helpers;

/// <summary>
/// The environment facts a support case asks for first, as ONE typed value with ONE rendering
/// (launch defaults audit, 2026-09-13): app version, OS build, process/OS architecture (a
/// Windows-on-ARM install runs the x64 build emulated — the ARM64 support case of 2026-09-03 took
/// a day to establish that), .NET runtime, release channel and whether update checks are
/// compiled in, plus the handful of preferences that change what the pipeline does.
/// </summary>
/// <remarks>
/// <para>Two consumers, one renderer: <c>App</c> logs <see cref="RenderLine"/> as the first line
/// of every session (until this existed the log opened with "VoiceWink starting up" and never
/// said which version or Windows produced it), and the Report-a-problem bundle writes
/// <see cref="RenderBlock"/> into <c>report-info.txt</c> (which carried the app version and the
/// attachment counts, nothing about the machine or the configuration).</para>
///
/// <para><b>Allowlisted by construction, and SHAPE-GATED on top.</b> <see cref="Collect"/> reads
/// exactly the keys named here and nothing else: bools, enum names and catalog ids. Never an API
/// key, a licence key, a base URL (custom endpoints can carry credentials), a path (embeds the
/// Windows username), the Dictionary, a prompt or a hotkey PROMPT — the hotkey is the canonical
/// binding token only. And a value is rendered ONLY when it has the shape of what the key holds
/// (Codex diff r1): a flag must parse as a boolean, the provider must be an <see cref="AIProvider"/>
/// name, every other string must be an identifier (<see cref="LogValueSanitizer.IsIdentifier"/>,
/// the ONE rule shared with the root-logger <c>ModelIdEnricher</c> since 2026-09-13) — because the model box
/// on the Enhancement page is an EDITABLE combo, so the stored "model id" can be any text a user
/// typed, and a corrupt or hand-edited file can hold anything under any key. Text that fails the
/// gate renders as its shape ("(not an id, N chars)"), never its content. What passes the gate can
/// still be a mis-pasted key — those are the shapes <c>LogRedactionEnricher.RedactString</c>
/// scrubs at every boundary this line crosses (the Sentry breadcrumb, the support bundle, the
/// GDPR export). <c>DiagnosticSnapshotTests</c> pins the allowlist against the settings keys that
/// exist and the gate against prose, a line break and an oversized value; the identifier table
/// itself lives in <c>LogValueSanitizerTests</c>.</para>
///
/// <para>Reads <c>settings.json</c> directly (the <see cref="CrashOptInReader"/> shape), not a
/// <c>SettingsService</c>: the startup line is logged before any service exists, and the report
/// dialog must not add a service-locator call (AGENTS.md). The file can lag the in-memory cache by
/// the 250 ms debounce — irrelevant for a report. Every field fails soft to "unknown" so a
/// corrupt file still yields a line. Known limit, shared with <see cref="CrashOptInReader"/>: under
/// CR-1's corrupt-primary recovery the app runs on the <c>.bak</c> values while the primary stays
/// corrupt until the next save, so the preferences render as "(default)" for that session.</para>
/// </remarks>
internal sealed record DiagnosticFacts(
    string AppVersion,
    string OsDescription,
    string ProcessArchitecture,
    string OsArchitecture,
    string Runtime,
    string Channel,
    bool UpdateChecksCompiledIn,
    string UiCulture,
    string SelectedModel,
    string Language,
    string GpuAcceleration,
    string InstantRecording,
    string AiEnhancement,
    string AiProvider,
    string AiModel,
    string RecordingHotkey,
    string VerboseLogging,
    string HistoryCleanup,
    string RemoveFillerWords,
    string CrashReports)
{
    /// <summary>True when the process runs under emulation — the architectures differ.</summary>
    public bool Emulated => !string.Equals(ProcessArchitecture, OsArchitecture, StringComparison.OrdinalIgnoreCase);
}

internal static class DiagnosticSnapshot
{
    internal const string Unknown = "unknown";

    /// <summary>The settings keys the snapshot reads — the whole allowlist, in one place.</summary>
    internal static readonly string[] SettingsKeysRead =
    [
        AppDefaults.SelectedModelName,
        AppDefaults.SelectedLanguage,
        AppDefaults.GpuAccelerationEnabled,
        AppDefaults.InstantRecordingEnabled,
        AppDefaults.AiEnhancementEnabled,
        AppDefaults.AiProvider,
        AppDefaults.HotkeyModifier,
        AppDefaults.VerboseLoggingEnabled,
        AppDefaults.IsTranscriptionCleanupEnabled,
        AppDefaults.RemoveFillerWords,
        AppDefaults.CrashReportingOptIn,
    ];

    public static DiagnosticFacts Collect(string settingsFilePath)
    {
        var settings = ReadSettings(settingsFilePath);
        var providerStored = TryProvider(settings, out var provider);

        return new DiagnosticFacts(
            AppVersion: Safe(() => typeof(DiagnosticSnapshot).Assembly.GetName().Version?.ToString(3)),
            OsDescription: Safe(() => global::System.Runtime.InteropServices.RuntimeInformation.OSDescription),
            ProcessArchitecture: Safe(() => global::System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
            OsArchitecture: Safe(() => global::System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString()),
            Runtime: Safe(() => global::System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription),
            Channel: string.IsNullOrEmpty(UpdateFeedConfig.Channel) ? "dev (no channel baked)" : UpdateFeedConfig.Channel,
            UpdateChecksCompiledIn: UpdateCheckFeature.IsEnabled,
            UiCulture: Safe(() => CultureInfo.CurrentUICulture.Name),
            SelectedModel: TokenDefaulted(settings, AppDefaults.SelectedModelName),
            Language: TokenDefaulted(settings, AppDefaults.SelectedLanguage),
            GpuAcceleration: Flag(settings, AppDefaults.GpuAccelerationEnabled),
            InstantRecording: Flag(settings, AppDefaults.InstantRecordingEnabled),
            AiEnhancement: Flag(settings, AppDefaults.AiEnhancementEnabled),
            AiProvider: providerStored ? provider : provider + " (default)",
            AiModel: Token(settings, ModelKeyFor(provider), "(none selected)"),
            RecordingHotkey: Token(settings, AppDefaults.HotkeyModifier, "(default)"),
            VerboseLogging: Flag(settings, AppDefaults.VerboseLoggingEnabled),
            HistoryCleanup: Flag(settings, AppDefaults.IsTranscriptionCleanupEnabled),
            RemoveFillerWords: Flag(settings, AppDefaults.RemoveFillerWords),
            CrashReports: Flag(settings, AppDefaults.CrashReportingOptIn));
    }

    /// <summary>One log line, key=value, for the session's first Information entry.</summary>
    public static string RenderLine(DiagnosticFacts f)
        => $"app={f.AppVersion} os=\"{f.OsDescription}\" arch={f.ProcessArchitecture}/{f.OsArchitecture}"
         + (f.Emulated ? " (emulated)" : "")
         + $" runtime=\"{f.Runtime}\" channel={f.Channel} updateChecks={(f.UpdateChecksCompiledIn ? "on" : "off")}"
         + $" culture={f.UiCulture} model={f.SelectedModel} language={f.Language} gpu={f.GpuAcceleration}"
         + $" instantRecording={f.InstantRecording} aiEnhancement={f.AiEnhancement} aiProvider={f.AiProvider}"
         + $" aiModel={f.AiModel} hotkey={f.RecordingHotkey} verboseLogging={f.VerboseLogging}"
         + $" historyCleanup={f.HistoryCleanup} removeFillers={f.RemoveFillerWords} crashReports={f.CrashReports}";

    /// <summary>The <c>report-info.txt</c> block: one fact per line, human-readable.</summary>
    public static string RenderBlock(DiagnosticFacts f)
    {
        var b = new StringBuilder();
        b.AppendLine("environment:");
        b.AppendLine($"  app version:       {f.AppVersion}");
        b.AppendLine($"  os:                {f.OsDescription}");
        b.AppendLine($"  architecture:      process {f.ProcessArchitecture} on {f.OsArchitecture}{(f.Emulated ? " (emulated)" : "")}");
        b.AppendLine($"  runtime:           {f.Runtime}");
        b.AppendLine($"  channel:           {f.Channel} (update checks {(f.UpdateChecksCompiledIn ? "compiled in" : "off in this build")})");
        b.AppendLine($"  ui culture:        {f.UiCulture}");
        b.AppendLine("configuration:");
        b.AppendLine($"  speech model:      {f.SelectedModel}");
        b.AppendLine($"  language:          {f.Language}");
        b.AppendLine($"  gpu acceleration:  {f.GpuAcceleration}");
        b.AppendLine($"  instant recording: {f.InstantRecording}");
        b.AppendLine($"  ai enhancement:    {f.AiEnhancement} (provider {f.AiProvider}, model {f.AiModel})");
        b.AppendLine($"  recording hotkey:  {f.RecordingHotkey}");
        b.AppendLine($"  verbose logging:   {f.VerboseLogging}");
        b.AppendLine($"  history cleanup:   {f.HistoryCleanup}");
        b.AppendLine($"  remove fillers:    {f.RemoveFillerWords}");
        b.AppendLine($"  crash reports:     {f.CrashReports}");
        return b.ToString();
    }

    // ── readers ────────────────────────────────────────────────────────────────────────────

    private static string DefaultProvider => (string)AppDefaults.Defaults[AppDefaults.AiProvider];

    /// <summary>The stored provider rendered by its ENUM name (case-insensitive parse, defined
    /// members only); anything else reads as absent, so the text is never rendered.</summary>
    private static bool TryProvider(Dictionary<string, Stored> settings, out string provider)
    {
        if (settings.TryGetValue(AppDefaults.AiProvider, out var stored)
            && stored.Kind == JsonValueKind.String
            && Enum.TryParse<AIProvider>(stored.Text, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            provider = parsed.ToString();
            return true;
        }
        provider = DefaultProvider;
        return false;
    }

    /// <summary>The per-provider model key, derived the way <c>AppDefaults.AiModelKey</c> derives it
    /// (lower-cased provider name) — from the stored string, since no enum is parsed here.</summary>
    private static string ModelKeyFor(string provider) => AppDefaults.AiModelPrefix + provider.ToLowerInvariant();

    /// <summary>A read value with the JSON kind it was stored as — a string "true" where a boolean
    /// belongs must not read as one (the app's own readers accept JSON <c>true</c> only).</summary>
    private readonly record struct Stored(string Text, JsonValueKind Kind);

    private static Dictionary<string, Stored> ReadSettings(string settingsFilePath)
    {
        var result = new Dictionary<string, Stored>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(settingsFilePath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsFilePath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var key in SettingsKeysRead)
                TryRead(doc.RootElement, key, result);
            // The per-provider model key depends on the provider value (the default provider's
            // when none is stored or the stored one is not a provider), so it is read second.
            TryProvider(result, out var provider);
            TryRead(doc.RootElement, ModelKeyFor(provider), result);
        }
        catch
        {
            // A corrupt file yields whatever was read before the failure; every renderer copes.
        }
        return result;
    }

    private static void TryRead(JsonElement root, string key, Dictionary<string, Stored> into)
    {
        if (!root.TryGetProperty(key, out var el)) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.True: into[key] = new("true", el.ValueKind); break;
            case JsonValueKind.False: into[key] = new("false", el.ValueKind); break;
            case JsonValueKind.String: into[key] = new(el.GetString() ?? "", el.ValueKind); break;
            case JsonValueKind.Number: into[key] = new(el.GetRawText(), el.ValueKind); break;
            default: break; // null / object / array: absent
        }
    }

    /// <summary>A flag renders only when it parses as a boolean; a string where a boolean belongs
    /// reads as absent — the same answer <c>GetBoolDefaulted</c> and the pre-boot readers give.</summary>
    private static string Flag(Dictionary<string, Stored> settings, string key)
    {
        if (settings.TryGetValue(key, out var stored) && stored.Kind is JsonValueKind.True or JsonValueKind.False)
            return stored.Text;
        return AppDefaults.BoolDefault(key) ? "true (default)" : "false (default)";
    }

    /// <summary>A string setting rendered through the identifier gate: the value when it is a
    /// token, its SHAPE when it is not — "(not an id, N chars)" — so text never reaches a line.</summary>
    private static string Token(Dictionary<string, Stored> settings, string key, string whenAbsent)
    {
        if (!settings.TryGetValue(key, out var stored) || string.IsNullOrWhiteSpace(stored.Text)) return whenAbsent;
        return LogValueSanitizer.IdentifierOrShape(stored.Text);
    }

    /// <summary>A string setting whose absence renders the AppDefaults table's value, marked "(default)".</summary>
    private static string TokenDefaulted(Dictionary<string, Stored> settings, string key)
        => Token(settings, key, AppDefaults.Defaults[key] + " (default)");

    private static string Safe(Func<string?> read)
    {
        try { return read() ?? Unknown; }
        catch { return Unknown; }
    }
}

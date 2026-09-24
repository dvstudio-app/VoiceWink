using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// Default application settings keys and values.
/// </summary>
public static class AppDefaults
{
    // Onboarding & General
    public const string HasCompletedOnboarding = "hasCompletedOnboarding";

    // Appearance
    public const string ThemeMode = "themeMode";

    // Clipboard
    public const string RestoreClipboardAfterPaste = "restoreClipboardAfterPaste";
    public const string ClipboardRestoreDelay = "clipboardRestoreDelay";

    // Audio & Media
    public const string IsSystemMuteEnabled = "isSystemMuteEnabled";
    public const string AudioResumptionDelay = "audioResumptionDelay";
    public const string IsSoundFeedbackEnabled = "isSoundFeedbackEnabled";

    // Recording & Transcription
    public const string IsTextFormattingEnabled = "IsTextFormattingEnabled";
    public const string RemoveFillerWords = "RemoveFillerWords";
    public const string SelectedLanguage = "SelectedLanguage";
    public const string AppendTrailingSpace = "AppendTrailingSpace";

    // History
    public const string IsHistoryEnabled = "isHistoryEnabled";

    // Cleanup
    public const string IsTranscriptionCleanupEnabled = "IsTranscriptionCleanupEnabled";
    public const string TranscriptionRetentionMinutes = "TranscriptionRetentionMinutes";
    public const string LifetimeMetricsState = "LifetimeMetricsState";

    // UI & Behavior
    public const string SendEnterAfterPaste = "sendEnterAfterPaste";
    public const string LaunchAtLogin = "launchAtLogin";

    // MiniRecorder pill position (PILL-4) — app-managed, NOT in Defaults: absence means
    // "default placement" (top-center of the active monitor). Written when the user
    // drags the pill; cleared by the Settings reset button and ResetAllSettings.
    public const string MiniRecorderPosFractionX = "miniRecorderPosFractionX";
    public const string MiniRecorderPosFractionY = "miniRecorderPosFractionY";

    // AUD-1: pinned recording device — app-managed, NOT in Defaults: absence means "system
    // default" (today's behavior). The WASAPI endpoint ID is the AUTHORITATIVE selection;
    // the name is optional display metadata only (the Settings "(unavailable)" annotation) —
    // an ID-only record is a valid selection. Device IDs are machine-specific, so both keys
    // stay out of settings export/import (not in Defaults ⇒ ImportExportService excludes
    // them); cleared by the Settings reset flow and ResetAllSettings. A selected-but-absent
    // device falls back to the system default for that recording (RecordingDeviceSelectionService).
    public const string RecordingDeviceId = "recordingDeviceId";
    public const string RecordingDeviceName = "recordingDeviceName";

    /// <summary>
    /// AUD-6: keep a standing microphone capture running (RAM-only ring, nothing persisted
    /// before the hotkey) so recording starts the instant the hotkey is pressed. Default ON
    /// (owner instruction 2026-08-04 — "instant with zero delay"); the honest cost is the
    /// Windows mic-in-use indicator staying lit while VoiceWink runs. Ordinary preference —
    /// exported/imported and reset-to-default like any other. OFF (or any standing-capture
    /// refusal: Bluetooth hands-free endpoint, license block, capture failure) means every
    /// recording starts on the pre-AUD-6 cold path, byte-for-byte.
    /// </summary>
    public const string InstantRecordingEnabled = "instantRecordingEnabled";

    // ENH-6g: per-install HMAC key (base64, 32 bytes) for content-addressed reference-copy
    // filenames. App-managed, NOT in Defaults (created lazily on first reference retention);
    // classified SENSITIVE in ImportExportService.IsSensitiveKey — a raw content hash in a
    // filename would let a history.json export recipient test whether a KNOWN image was
    // used, and the key itself must never ride an export or be planted by an import
    // (device-local secret state). Key loss only stops dedup against pre-existing copies.
    public const string ReferenceDedupKey = "referenceDedupKey";

    // App Mode
    public const string AppModeConfigs = "appModeConfigs";

    /// <summary>
    /// Pre-rename settings key for App Mode configs (the feature was called "Power Mode" until
    /// 2026-07-08). Read-and-removed one-way by <c>AppModeSettingsMigration.Run</c>; deliberately
    /// NOT in <see cref="Defaults"/> so it is never independently importable or writable.
    /// Delete once the installed fleet has migrated (post-1.x).
    /// </summary>
    internal const string LegacyPowerModeConfigs = "powerModeConfigs";


    // Text Processing
    public const string FillerWords = "FillerWords";
    /// <summary>
    /// The shipped filler list (comma-separated; the ONE source — <c>FillerWordManager</c> splits
    /// this, it does not keep its own copy). Every entry must be a filler in EVERY language the app
    /// recognises and a word in NONE, because the removal runs on every transcript whose language
    /// is English or unknown (<c>FillerWordManager.AppliesTo</c>). Until 2026-09-13 the list carried
    /// <c>ah</c>, <c>eh</c> and <c>er</c>: whole-word, case-insensitive, on every language — which
    /// deleted Dutch and German "er" ("Er is een probleem" → "Is een probleem"), French "eh" and the
    /// interjection "ah" from every dictation in those languages. <c>um</c> stays because it is the
    /// most common English filler — and it is the ONE residual: the German preposition ("um acht")
    /// and the Portuguese article ("um carro"). That is why an EXPLICIT non-English recognition
    /// language skips the removal entirely and only Auto-detect keeps the residual (the wizard
    /// asks for the language; picking it is the fix). <c>uhm</c>/<c>umm</c>/<c>ehm</c>/<c>euh</c>
    /// were added the same day — the spellings the engines actually emit for the same sounds, and
    /// "euh" is the Dutch/French one. Deliberately NOT VoiceInk's <c>mm</c>/<c>hm</c>: "5 mm" is a
    /// measurement.
    /// </summary>
    public const string DefaultFillerWords = "uh,um,uhm,umm,hmm,huh,erm,ehm,euh";
    /// <summary>
    /// The list that shipped until 2026-09-13, kept ONLY so <c>FillerWordsMigration</c> can
    /// recognise an install that stored it verbatim (the Settings "Reset to defaults" button wrote
    /// this literal) and hand it the new default. Nothing else may read it.
    /// </summary>
    internal const string LegacyDefaultFillerWords = "uh,um,ah,eh,hmm,huh,er,erm";

    // Hotkey
    public const string HotkeyModifier = "hotkeyModifier";
    public const string PasteLastHotkeyModifier = "pasteLastHotkeyModifier";
    public const string RedoLastHotkeyModifier = "redoLastHotkeyModifier";
    public const string GenerateImageHotkeyModifier = "generateImageHotkeyModifier";

    // Model
    public const string SelectedModelName = "selectedModelName";
    /// <summary>Whisper model name used as the fallback when <see cref="SelectedModelName"/> is unset.
    /// Small since 2026-07-23 (owner decision) — Base is not accurate enough. The q8 build since
    /// TRN-6 (2026-08-03), when the catalogue became q8-only: same weights, 264 MB instead of 465.
    ///
    /// <para>This is the UNSET fallback, so it governs new installs. It is deliberately NOT what
    /// moves an existing user off a superseded name — <see cref="LocalModelMigration"/> does that,
    /// and it is a separate mechanism because <c>ResetAllSettings</c> does not reset
    /// <see cref="SelectedModelName"/> (verified at review; an earlier version of this change
    /// assumed it did).</para>
    ///
    /// <para><b>It deliberately DIFFERS from what onboarding recommends</b> since change C
    /// (2026-08-04) made that Parakeet. This is a compile-time const in a static dictionary, so it
    /// cannot ask whether Parakeet runs on this machine — and Parakeet genuinely does not, behind
    /// the <c>-p:ParakeetEnabled=false</c> lever, below the SSE2 floor, or in a NATIVE arm64 process
    /// (which we do not ship — see <see cref="DefaultLocalModel"/>; ARM64 machines run the x64 build
    /// under emulation and DO get Parakeet). Baking it in
    /// here would hand an unsupported machine a model it cannot use, in the one path that exists
    /// precisely because nothing else set a value. <see cref="DefaultLocalModel"/> is where the
    /// availability-aware answer lives.</para></summary>
    public const string DefaultWhisperModel = "ggml-small-q8_0";
    public const string IsDiarizationEnabled = "isDiarizationEnabled";
    /// <summary>
    /// PRM-3: "Send dictionary and trigger words" toggle (named "Enable Dictionary"
    /// until 2026-07-23) for sending Dictionary words + trigger words to ElevenLabs
    /// as keyterms. Default ON (owner decision 2026-07-18, reversing the launch
    /// default-OFF: Dictionary value outweighs the +20% keyterms surcharge, and the
    /// cost disclosure sits beside the toggle). An ordinary preference — exported AND
    /// imported like any other setting (owner decision 2026-07-22), reset to the
    /// default by Reset-all-settings.
    /// </summary>
    public const string ElevenLabsKeytermsEnabled = "elevenLabsKeytermsEnabled";
    /// <summary>
    /// "Send dictionary and trigger words" toggle (named "Enable Dictionary" until
    /// 2026-07-23) for sending Dictionary words + trigger words to Deepgram as
    /// keyterm/keywords query params (owner request 2026-07-18 — Deepgram was
    /// always-on at PRM-3 launch; the toggle exists because the words travel in the
    /// request URL, a small privacy consideration disclosed beside it). Default ON;
    /// an ordinary preference like the ElevenLabs toggle — exported + imported, reset
    /// to default.
    /// </summary>
    public const string DeepgramKeytermsEnabled = "deepgramKeytermsEnabled";
    /// <summary>
    /// "Send dictionary and trigger words" toggle for OpenAI's <c>gpt-transcribe</c>, which sends
    /// them as the structured <c>keywords[]</c> field.
    ///
    /// <para>Defaults to <b>true</b>, matching the Deepgram and ElevenLabs toggles (owner decision
    /// 2026-08-02). It shipped OFF on 2026-08-01 as the conservative default while two things were
    /// unmeasured: it opens a NEW egress path for the user's Dictionary, and gpt-transcribe is a
    /// generative model, which this project has seen echo hint text back into a transcript on
    /// near-silent audio. The owner's call is that the feature is worth having on and the echo
    /// behaviour gets checked in UAT (plan §71.11) rather than guarded by a default nobody flips.</para>
    ///
    /// <para>The echo gate stays engaged for this transport regardless — see
    /// <c>HintTransportKind.StructuredKeywords</c>. Turning the default on raises how often that
    /// gate matters; it does not change what it does.</para>
    ///
    /// <para>An ordinary preference otherwise: exported, imported, and reset to default.</para>
    /// </summary>
    public const string OpenAIKeywordsEnabled = "openAIKeywordsEnabled";

    /// <summary>
    /// The SHIPPED default for a bool preference, from <see cref="Defaults"/>. Unregistered keys
    /// answer false — the safe direction for something nobody declared.
    ///
    /// <para>Exists so that no call site has to restate a default as a literal. Restating one is
    /// how a preference ends up meaning two different things: <c>SettingsService.Get</c> consults
    /// this table only when a key is ABSENT, so a present-but-malformed value falls back to
    /// whatever literal the CALLER passed. Two callers, two literals, and the UI can show a toggle
    /// ON while the request path sends nothing — which is exactly what happened when the OpenAI
    /// keywords toggle shipped OFF while the toggle renderer hardcoded <c>true</c>.</para>
    /// </summary>
    public static bool BoolDefault(string key)
        => Defaults.TryGetValue(key, out var value) && value is bool b && b;
    /// <summary>
    /// Master "Enable Dictionary" toggle on the Dictionary page (owner request
    /// 2026-07-23). The Dictionary FEATURE gate: when off, vocabulary words are
    /// excluded from transcription hints for ALL providers and from the AI-enhancement
    /// context, and word replacements are not applied. Prompt trigger phrases are NOT
    /// Dictionary data and keep riding live-dictation hints (still subject to the
    /// per-provider transport toggles above, which gate the whole keyterm payload).
    /// Default ON; an ordinary preference — exported + imported, reset to the default
    /// by Reset-all-settings.
    /// </summary>
    public const string DictionaryEnabled = "dictionaryEnabled";

    /// <summary>
    /// DCT-1: the shipped Dictionary defaults this install has been OFFERED — a JSON string array
    /// of vocabulary words and <c>replace:</c>-prefixed rule TARGETS (never the variant list, so a
    /// later variant edit cannot re-offer a rule), written by <c>DefaultDictionarySeed</c> BEFORE
    /// it adds the ones not yet on record — record first, so a row never exists without its key:
    /// a key with no row is a deletion to respect, a missing key means no row was ever added. Offered, not
    /// present: a default the user deleted stays deleted because it is on this record. App-managed
    /// state, NOT in <see cref="Defaults"/> — it survives Reset All Settings (resetting it would
    /// resurrect deleted defaults into Dictionary data the reset never touches) and stays out of
    /// settings export/import (the target install seeds for itself).
    /// </summary>
    public const string DictionaryDefaultsOffered = "dictionaryDefaultsOffered";

    // Startup
    public const string StartMinimized = "startMinimized";
    /// <summary>
    /// When true (default), MINIMIZING the main window hides it to the system tray instead of the
    /// taskbar (<c>App.SetupMinimizeToTray</c>); when false, minimize behaves normally (taskbar).
    /// Ordinary preference — exported/imported and reset-to-default like any other. Default ON
    /// preserves the pre-2026-07-22 behavior; upgraded installs lack the key and inherit ON via
    /// <see cref="Defaults"/>. Distinct from <see cref="StartMinimized"/> (startup) and the
    /// close-to-tray behavior (both unchanged).
    /// </summary>
    public const string MinimizeToTray = "minimizeToTray";
    /// <summary>
    /// When true (default), the MiniRecorder pill is excluded from screen capture
    /// (<c>WDA_EXCLUDEFROMCAPTURE</c>): screenshots, screen recordings and screen shares omit it while
    /// it stays fully visible on the user's own screen. Applied per pill window
    /// (<c>MiniRecorderWindow.ApplyCaptureExclusion</c> → <see cref="CaptureAffinity"/>) at construction
    /// AND on every Show, so it survives the cross-DPI recreate and a toggle change takes effect at the
    /// pill's next appearance. Windows limits the guarantee to supported public capture mechanisms and
    /// DWM composition — it is not a promise about every capture stack — and a failed call degrades to
    /// capturable without affecting the pill. Ordinary preference: exported/imported and reset-to-default
    /// like any other. Default ON was the owner's choice (2026-07-30); the trade-off is that a user's own
    /// screenshot of a pill message won't contain the pill.
    /// </summary>
    public const string HidePillFromCapture = "hidePillFromCapture";
    /// <summary>
    /// UPD-3c one-shot: set by the apply path right before the quit-for-update so the NEXT
    /// launch shows the window (and the What's-new dialog) even when <see cref="StartMinimized"/>
    /// is on — an update restart is an attended action, not a cold boot. App-managed transient
    /// state (like <see cref="LastUpdateCheckUtc"/>), deliberately NOT in <see cref="Defaults"/>:
    /// call sites default to false and Reset-all-settings has nothing to restore. Read early at
    /// launch, consumed (cleared) only when the onboarded post-legal path proceeds — see
    /// <c>UpdateRestartVisibility</c>.
    /// </summary>
    public const string ShowWindowAfterUpdateRestart = "showWindowAfterUpdateRestart";

    // Debug & Logging
    public const string VerboseLoggingEnabled = "verboseLoggingEnabled";
    /// <summary>REL-17: the RAW prompt/output trace opt-in — when ON, every prompt actually
    /// sent to a provider and the raw text it returned are appended to
    /// <c>Logs/prompts-yyyyMMdd.log</c> via <see cref="PromptTraceLog"/> (ALL builds; was
    /// DEBUG-only). The redacted sidecar is NOT behind this key since 2026-09-13 — it is always
    /// written — and the onboarding wizard's "Help improve VoiceWink" answer sets this key
    /// together with <see cref="CrashReportingOptIn"/> (owner decision). Deliberately a NEW key:
    /// a stale Debug-era <see cref="LegacyPromptTraceLoggingEnabled"/> value must never
    /// silently activate Release tracing, so the legacy key is REMOVED at launch
    /// (<see cref="PromptTraceKeyMigration"/>) and both keys are import-protected —
    /// device-local consent, never portable (ImportExportService). Image prompts are gated
    /// separately on top of this — see <see cref="PromptTraceIncludeImagePrompts"/>.</summary>
    public const string PromptTraceLoggingOptIn = "promptTraceLoggingOptIn";
    /// <summary>REL-21 (owner request 2026-08-04): sub-option under
    /// <see cref="PromptTraceLoggingOptIn"/> — image-generation prompts reach the RAW file ONLY
    /// when this is also ON (the always-written redacted sidecar carries their masked line
    /// regardless, 2026-09-13). Default OFF, so image prompts stay out unless asked for: an image
    /// prompt describes a picture in the user's own words and is routinely the longest, most
    /// personal entry in the trace. Named "include" rather than "exclude" so that an absent
    /// key — every existing install on upgrade — reads as EXCLUDE rather than fail-open.
    /// Device-local consent like its parent: import-protected, reset by Reset-all. Toggle sits
    /// beside its parent in Settings -> Diagnostics; the wizard never sets it.</summary>
    public const string PromptTraceIncludeImagePrompts = "promptTraceIncludeImagePrompts";
    /// <summary>Pre-REL-17 DEBUG-only trace toggle key. Exists only for the launch
    /// migration + the import skip; nothing reads it as a preference.</summary>
    public const string LegacyPromptTraceLoggingEnabled = "promptTraceLoggingEnabled";
    /// <summary>REL-17: when ON, each recording's WAV is MOVED to
    /// <c>Recordings\Debug\</c> at the moment the pipeline would otherwise delete it and
    /// kept for up to 7 days (swept unconditionally — independent of this toggle and of
    /// history cleanup) for diagnosing transcription problems. Device-local consent:
    /// import-protected, reset by Reset-all. Toggle lives in Settings -> Diagnostics.</summary>
    public const string KeepRecordingsForDebug = "keepRecordingsForDebug";

    /// <summary>UPD-4: app-managed per-domain manifest of last-applied default-content hashes
    /// (prompts + App-Mode), JSON. NOT a user preference — deliberately absent from
    /// <see cref="Defaults"/> (reset-all leaves it), excluded from settings export, and skipped
    /// on import (an import that applies prompts/configs invalidates the affected domain
    /// instead). Read and rewritten only by <c>DefaultPromptsReseed</c>.</summary>
    public const string DefaultsManifest = "defaultsManifest";

    // Licensing
    public const string LicenseKey = "licenseKey";
    public const string LicenseInstanceId = "licenseInstanceId";
    /// <summary>
    /// Random, stable per-install label sent as the LemonSqueezy <c>instance_name</c> on
    /// activation (LIC-9). Replaces the former hostname-derived name so no personal
    /// identifier is transmitted to LemonSqueezy. Generated once by
    /// <c>LicenseService.BuildInstanceName</c> and persisted; identity on LS's side is the
    /// <c>instance_id</c>, so this name is purely a cosmetic dashboard label. Intentionally
    /// NOT cleared by <c>ClearLocalLicenseState</c> — the device keeps one stable label so a
    /// re-activation reuses it rather than littering the LS dashboard with orphans.
    /// </summary>
    public const string LicenseInstanceLabel = "licenseInstanceLabel";
    public const string LicenseLastValidatedUtc = "licenseLastValidatedUtc";
    public const string LicenseMachineId = "licenseMachineId";
    /// <summary>
    /// When the free trial started on this device (ISO 8601 "O"). Since LIC-21 PR A this is DEVICE
    /// state that licensing never writes: it is NOT in <c>LicenseStateStringKeys</c>, and neither the
    /// activation persist block nor <c>ClearLocalLicenseState</c> touches it — activating a key inside
    /// the window neither extends nor cancels the trial, deactivating returns the remaining days.
    /// Read and written only by <c>TrialWindow</c>, beside the durable registry stamp (LIC-11).
    /// Folder-wipe survival is test-pinned; reinstall survival is the design intent pending the
    /// VM uninstall + reinstall row. Earliest first-run wins between the two.
    /// </summary>
    public const string LicenseFirstRunGraceStartedUtc = "licenseFirstRunGraceStartedUtc";
    /// <summary>
    /// When this device was last seen running (ISO 8601 "O") — the clock-rollback guard's input
    /// (<c>EffectiveNow = max(now, lastSeen)</c>). Advances on every licence status read whether or
    /// not a key is stored, persisted at most hourly. Exists as its own key so the settings leg is a
    /// real two-field reading: a synthesised last-seen would re-seed the stores on every read.
    /// </summary>
    public const string LicenseFirstRunGraceLastSeenUtc = "licenseFirstRunGraceLastSeenUtc";
    /// <summary>
    /// When the one-time legacy trial decision was taken on this device (ISO 8601 "O"; LIC-21 PR A,
    /// policy (a)). Pre-PR-A releases cleared the trial stamp at activation, so at the first launch
    /// with no stamp anywhere, activation evidence seeds the window as USED; this marker records that
    /// the decision happened — whichever way — so a post-PR-A user who activates a paid key before
    /// ever starting the trial does not present the same evidence at the next launch and lose an
    /// untouched trial. Never cleared by licensing.
    /// </summary>
    public const string LicenseTrialMigrationDecidedUtc = "licenseTrialMigrationDecidedUtc";
    /// <summary>
    /// UTC expiry (ISO 8601 "O" format) of the stored license key, persisted from
    /// LemonSqueezy's <c>license_key.expires_at</c> on activation and validation (LIC-4).
    /// Empty for a perpetual key or when never observed. Only expiring keys carry one (LIC-4
    /// built it for the retired trial key; a refunded key arrives the same way — LIC-21), so a
    /// past value means "this key ran out" — enforced locally by <c>LicenseService.IsStoredKeyExpired</c> at every
    /// cache-trust point so an expired key can't ride the 24h validation cache or the
    /// 30-day offline grace. A value that doesn't parse is treated as "no expiry"
    /// (fail-open to perpetual behavior, never to a wrongly-expired key).
    /// </summary>
    public const string LicenseKeyExpiresAtUtc = "licenseKeyExpiresAtUtc";
    /// <summary>
    /// UTC timestamp (ISO 8601 "O" format) when LemonSqueezy first reported the license
    /// as disabled. Persisted by <c>LicenseService.CheckAsync</c> so the disabled state
    /// survives across sessions even without a network round-trip on startup (LIC-2a).
    /// Disabling a key is a manual merchant action (refund, fraud, chargeback, replacement,
    /// or error) — never presented as proof of a refund (LIC-8). Empty when the license has
    /// never been observed disabled. A non-empty value that does NOT round-trip through
    /// <c>DateTimeOffset.TryParse</c> is treated as "no signal" by
    /// <c>LicenseService.IsLicenseDisabled</c> — defense against settings corruption /
    /// hand-edit that would otherwise sticky-lock the user forever.
    /// </summary>
    public const string LicenseDisabledUtc = "licenseDisabledUtc";
    /// <summary>
    /// The <see cref="global::VoiceWink.Services.Licensing.LicenseIdentityManifest.PolicyDigest"/>
    /// the stored key last MATCHED online (LIC-4 product binding) — the positive proof that
    /// this activation belongs to VoiceWink's own LS store/product. Written under the license
    /// state lock on every armed matching activate/validate; required at every cache-trust
    /// point while the manifest is armed (no proof ⇒ no 24h-cache trust, no offline grace —
    /// the status degrades to GraceExpired until one online validate succeeds). Empty when
    /// never proven (incl. every key activated while the manifest was staged).
    /// </summary>
    public const string LicenseBindingProofDigest = "licenseBindingProofDigest";
    /// <summary>
    /// UTC timestamp (ISO 8601 "O" format) when an online activate/validate response
    /// conclusively reported a FOREIGN store/product identity for the stored key (LIC-4
    /// product binding). Same parse-guard discipline as <see cref="LicenseDisabledUtc"/>.
    /// Honored only while <see cref="LicenseBindingMismatchDigest"/> equals the CURRENT
    /// manifest digest — an additive manifest change retires stale verdicts silently.
    /// Cleared by a matching validate, any successful activation, and deactivation.
    /// </summary>
    public const string LicenseIdentityMismatchUtc = "licenseIdentityMismatchUtc";
    /// <summary>
    /// The manifest <c>PolicyDigest</c> that produced <see cref="LicenseIdentityMismatchUtc"/> —
    /// scopes the foreign-identity verdict to the policy it was judged against.
    /// </summary>
    public const string LicenseBindingMismatchDigest = "licenseBindingMismatchDigest";
    /// <summary>
    /// UTC timestamp (ISO 8601 "O" format) of the last RECOGNIZED refusal from the license
    /// server (a validate response with <c>valid:false</c> and a named key status other than
    /// "disabled" — inactive, revoked, expired…). Without it a revoked key kept recording:
    /// the refusal returned <c>Invalid</c> from <c>CheckAsync</c> but wrote nothing, so the
    /// network-free <c>GetCachedStatus</c> — which the recording gate uses — still trusted
    /// the last-validated stamp and answered Activated for up to 24 h (then OfflineGrace for
    /// up to 30 days). Persisted like the disabled flag so every cache-trust point sees the
    /// server's verdict. Parse-guarded (a corrupt value reads as "no signal"); cleared by a
    /// successful validate, any successful activation, and deactivation. A MALFORMED refusal
    /// (no recognizable status) deliberately does NOT set it — that is not a verdict.
    /// </summary>
    public const string LicenseValidationRefusedUtc = "licenseValidationRefusedUtc";
    /// <summary>
    /// Activation limit reported by LemonSqueezy for this license (LIC-6). Stored via
    /// <c>SettingsService.SetInt</c>. -1 is the canonical write sentinel for "never
    /// observed" (used by <c>ClearLocalLicenseState</c> and the <c>GetInt</c> default
    /// when the key is absent); the read side in <c>LicenseService.GetActivationCounts</c>
    /// maps any stored value <c>&lt; 0</c> back to <c>null</c> so future encoding drift
    /// doesn't mis-classify. A stored 0 is a real "LS said zero" contract violation,
    /// not a sentinel — it round-trips as <c>0</c> and surfaces as such in the UI.
    /// </summary>
    public const string LicenseActivationLimit = "licenseActivationLimit";
    /// <summary>
    /// Activation usage reported by LemonSqueezy (number of seats currently active, LIC-6).
    /// Same nullable-via-negative-sentinel semantics as <see cref="LicenseActivationLimit"/>.
    /// </summary>
    public const string LicenseActivationUsage = "licenseActivationUsage";
    public const string CrashReportingOptIn = "crashReportingOptIn";

    /// <summary>
    /// TRN-53: GPU acceleration for the local engines (Whisper's Vulkan load order, Parakeet's
    /// server device). Default ON. Read pre-boot by <c>GpuAccelerationReader</c> because the
    /// consumers run before any service exists; a flip takes effect at NEXT APP START — the
    /// native load order is process-wide and frozen at the first factory — and the Settings copy
    /// says so.
    /// </summary>
    public const string GpuAccelerationEnabled = "gpuAccelerationEnabled";

    // Legal acceptance (LGL-1). Stored values are SHA-256 hex (lowercase) of the
    // bundled document body, not version numbers. Empty string means "unset".
    // Hash-based so any body edit re-prompts even without a version-header bump,
    // which protects against an accidental in-place text change without versioning.
    public const string AcceptedEulaVersion = "acceptedEulaVersion";
    public const string AcceptedPrivacyVersion = "acceptedPrivacyVersion";

    // Auto-updates (UPD-1). Both keys are inert until a build enables UpdateCheckEnabled
    // (the release scripts set `-p:UpdateCheckEnabled=true` per signed build; the committed
    // csproj default stays false) — until then, IUpdateService short-circuits before reading them.
    /// <summary>
    /// User-facing toggle for the 30s-after-launch automatic update check (Plan 4D).
    /// `true` lets the scheduler arm (subject to the UPDATE_CHECK_ENABLED build flag + the bundled
    /// privacy-policy acceptance); `false` disables the scheduler entirely. Manual "Check for updates" button
    /// presses bypass this gate — user click is itself explicit consent. Defaults to `true`
    /// (seeded into <see cref="Defaults"/>; reset by <see cref="ViewModels.SettingsViewModel"/>).
    /// </summary>
    public const string AutomaticUpdateCheckEnabled = "automaticUpdateCheckEnabled";
    /// <summary>
    /// UPD-4b: when a BACKGROUND check finds an update, apply it automatically (download +
    /// maintenance-gated apply + restart) instead of only lighting the sidebar badge and waiting
    /// for an "Apply &amp; restart" click. Defaults to `true` — an opt-out, not an opt-in (owner
    /// decision 2026-08-08; default-on matches the CRA's direction for consumer products).
    ///
    /// <para>Three layers gate this, and only the third is this key: the
    /// <c>UPDATE_CHECK_ENABLED</c> build flag must be on (signed release builds only), the
    /// scheduler must be armed (<see cref="AutomaticUpdateCheckEnabled"/> — an install is only ever
    /// triggered from a scheduler tick, so checks off means installs off), and then this key
    /// decides. The manual "Apply &amp; restart" button is NOT gated by it, exactly as the manual
    /// "Check for updates" button is not gated by the check toggle.</para>
    /// </summary>
    public const string AutomaticUpdateInstallEnabled = "automaticUpdateInstallEnabled";
    /// <summary>
    /// UTC timestamp (ISO 8601 "O" format) of the last attempted update check, written by
    /// <c>UpdateService.CheckForUpdatesAsync</c>. Empty until the first check completes.
    /// Surfaced by the Settings → Updates page as "Last checked: &lt;timestamp&gt;".
    /// Deliberately NOT in <see cref="Defaults"/> — this is an app-managed timestamp,
    /// not a user-configurable default. Mirrors <see cref="LicenseLastValidatedUtc"/>'s
    /// shape. Reset-all-settings clears it explicitly via SetString("").
    /// </summary>
    public const string LastUpdateCheckUtc = "lastUpdateCheckUtc";

    /// <summary>
    /// 3-component version string (e.g. "1.23.284") of the newest changelog the user has seen,
    /// written when the What's-new dialog (REL-4) is dismissed. Seeded to the current version on
    /// first run so the dialog only appears AFTER an update. Empty until first seen.
    /// App-managed (like <see cref="LastUpdateCheckUtc"/>) — NOT in <see cref="Defaults"/>.
    /// </summary>
    public const string LastSeenChangelogVersion = "lastSeenChangelogVersion";

    /// <summary>
    /// The one-time install-source record (dvstudio-metrics #85; Privacy §4.5), written only by
    /// <c>InstallSourceReporter</c>. <see cref="InstallSourceInstallStamp"/> is the Velopack install
    /// root's creation time ("O") the record belongs to — a different stamp means a new installation;
    /// <see cref="InstallSource"/> is <c>msstore</c> | <c>not-detected</c>;
    /// <see cref="InstallSourceReport"/> is <c>pending</c> | <c>sent</c> | <c>gave-up</c> | <c>none</c>;
    /// <see cref="InstallSourceReportAttempts"/> counts attempts as a decimal string. App-managed — NOT in
    /// <see cref="Defaults"/>, and deliberately untouched by "Reset all settings": clearing them would
    /// let the same installation be counted twice.
    /// </summary>
    public const string InstallSourceInstallStamp = "installSourceInstallStamp";
    public const string InstallSource = "installSource";
    public const string InstallSourceReport = "installSourceReport";
    public const string InstallSourceReportAttempts = "installSourceReportAttempts";

    // AI Enhancement
    public const string AiEnhancementEnabled = "aiEnhancementEnabled";
    public const string AiProvider = "aiProvider";
    public const string AiImageProvider = "aiImageProvider";
    public const string AiModelPrefix = "aiModel_";
    public const string AiImageModelPrefix = "aiImageModel_";
    public const string AiBaseUrlPrefix = "aiBaseUrl_";
    public const string ApiKeyPrefix = "apikey_";
    public const string LastActivePromptId = "lastActivePromptId";
    public const string CustomPrompts = "customPrompts";

    /// <summary>
    /// LAI-1: which protocol the Local server provider speaks — <c>"ollama"</c> or
    /// <c>"openai"</c> (<see cref="Services.AIEnhancement.LocalServerEndpoints"/>). Its address is
    /// <c>aiBaseUrl_localserver</c>, the per-provider base-URL key every provider already has.
    /// App-managed, NOT in <see cref="Defaults"/>, on purpose: the address key is import-protected
    /// by its prefix, so if this one travelled on its own an imported settings file would pair
    /// another machine's server TYPE with this machine's ADDRESS — an Ollama address spoken to as
    /// LM Studio, failing every dictation. The two stay together or not at all. Absent = Ollama.
    /// </summary>
    public const string LocalServerApiSetting = "localServerApi";

    /// <summary>
    /// Builds the settings key used to store the selected model for an AI provider.
    /// Centralizes the <c>aiModel_{provider}</c> convention so callers don't hand-interpolate.
    /// </summary>
    public static string AiModelKey(AIProvider provider) =>
        AiModelPrefix + provider.ToString().ToLowerInvariant();

    /// <summary>
    /// Builds the settings key used to store the selected image-generation model for an AI provider.
    /// Centralizes the <c>aiImageModel_{provider}</c> convention so callers don't hand-interpolate.
    /// </summary>
    public static string AiImageModelKey(AIProvider provider) =>
        AiImageModelPrefix + provider.ToString().ToLowerInvariant();

    /// <summary>
    /// Builds the settings key used to store the API key for a provider identified by string.
    /// Centralizes the <c>apikey_{provider}</c> convention.
    /// </summary>
    public static string ApiKey(string providerKey) =>
        ApiKeyPrefix + providerKey.ToLowerInvariant();

    public static Dictionary<string, object> Defaults { get; } = new()
    {
        [HasCompletedOnboarding] = false,
        [RestoreClipboardAfterPaste] = false,
        [ClipboardRestoreDelay] = 2.0,
        [IsSystemMuteEnabled] = true,
        [AudioResumptionDelay] = 0.0,
        [IsSoundFeedbackEnabled] = true,
        [IsTextFormattingEnabled] = true,
        [RemoveFillerWords] = true,
        [SelectedLanguage] = "auto",
        [AppendTrailingSpace] = true,
        [IsHistoryEnabled] = true,
        // OFF since 2026-09-13 (owner decision, launch defaults audit): ON with a 7-day window
        // silently deleted every history entry a week old — and recycled the generated images
        // with them — while privacy-v5 §7 promises local history "remains on your device until
        // you delete it". The toggle and its 1 h … 30 d window are unchanged for anyone who wants
        // a short retention; the REL-17 diagnostics sweep runs regardless of this switch.
        [IsTranscriptionCleanupEnabled] = false,
        [TranscriptionRetentionMinutes] = 10080,
        [AppModeConfigs] = "",
        [SendEnterAfterPaste] = true,    // ON by default again since 2026-09-11 (owner decision, reversing the same-day OFF flip of PR 861): it fires only in push-to-talk, the mode chat-app dictation uses; switch it off in Settings to keep the caret on the line
        [HotkeyModifier] = "RightAlt",
        [PasteLastHotkeyModifier] = "",
        [RedoLastHotkeyModifier] = "",
        [GenerateImageHotkeyModifier] = "",
        [FillerWords] = DefaultFillerWords,
        [SelectedModelName] = DefaultWhisperModel,
        [IsDiarizationEnabled] = false,
        [ElevenLabsKeytermsEnabled] = true,
        [DeepgramKeytermsEnabled] = true,
        [OpenAIKeywordsEnabled] = true,
        [DictionaryEnabled] = true,
        [ThemeMode] = "System",
        [LaunchAtLogin] = true,
        [StartMinimized] = true,
        [MinimizeToTray] = true,
        [HidePillFromCapture] = true,
        [InstantRecordingEnabled] = true,
        [VerboseLoggingEnabled] = false,
        [PromptTraceLoggingOptIn] = false,
        [PromptTraceIncludeImagePrompts] = false,
        [KeepRecordingsForDebug] = false,
        [CrashReportingOptIn] = false,
        [GpuAccelerationEnabled] = true,
        [AiEnhancementEnabled] = false,
        [AiProvider] = "OpenAI",
        [AiImageProvider] = "OpenAI",
        [LastActivePromptId] = "",
        [CustomPrompts] = "",
        [AcceptedEulaVersion] = "",
        [AcceptedPrivacyVersion] = "",
        [AutomaticUpdateCheckEnabled] = true,
        [AutomaticUpdateInstallEnabled] = true,
        // LastUpdateCheckUtc deliberately omitted — app-managed timestamp,
        // not a user-configurable default (matches LicenseLastValidatedUtc
        // shape). SettingsService.GetString falls back to the caller's
        // explicit default ("") when absent.
    };
}

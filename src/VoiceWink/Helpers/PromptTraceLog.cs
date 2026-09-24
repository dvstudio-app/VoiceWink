using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// The operation a trace entry belongs to (REL-17). A FIXED app-authored identity — the
/// redacted sidecar's entry headers render from this enum (via <see cref="PromptTraceLog"/>'s
/// internal name map), never from the free-form display <c>kind</c> string, which may embed
/// user-authored prompt titles (Codex plan rounds 3/4: string identities cannot prove
/// app-authorship; enums can).
/// </summary>
public enum PromptTraceOp
{
    GroqTranscription,
    OpenAITranscription,
    DeepgramTranscription,
    ElevenLabsTranscription,
    LocalWhisperTranscription,
    DetectedLanguage,
    TextEnhancement,
    ImageGeneration,
    ImageGenerationRedo,
    TranscriptionOutput,
    /// <summary>
    /// The recognizer output for a dictation that turned out to be an IMAGE prompt (REL-21).
    /// Its own member rather than <see cref="TranscriptionOutput"/> because the text IS the
    /// image description — the thing the image sub-option exists to keep out of the trace —
    /// and rather than <see cref="ImageGeneration"/> because it is what the recognizer heard,
    /// not what was sent to an image provider, and the sidecar header must not claim otherwise.
    /// </summary>
    ImagePromptTranscriptionOutput,
    EnhancementOutput,
    FileTranscriptionOutput,
    /// <summary>
    /// REL-26 (owner UAT §28.3): which language a recording will be recognised with and
    /// which precedence source supplied it (retry → prompt override → App Mode → global),
    /// written at resolution time — the trace otherwise shows only the wire-level
    /// language on each request, so precedence could not be verified from the file.
    /// </summary>
    LanguageResolution,
}

/// <summary>
/// Typed scalar metadata riding a trace entry into the REDACTED sidecar (REL-17). Every
/// value is validated at write time by <see cref="PromptTraceLog"/> — nothing here is
/// trusted: <see cref="Provider"/> must match the app's known provider set;
/// <see cref="Model"/> renders verbatim only when it is a member of the app's model
/// catalogs (<see cref="KnownModelIds"/> — decided CENTRALLY, never asserted per call
/// site: local model names are arbitrary user filenames, and a per-site flag was exactly
/// what the diff review caught mis-asserted) AND it passes the model-id charset check —
/// otherwise it renders as <c>custom (sha:…)</c> (correlation without content);
/// <see cref="Language"/> must look like a language tag or <c>auto</c>;
/// <see cref="Confidence"/> is rendered from the numeric value only.
/// </summary>
public readonly record struct TraceMeta(
    string? Provider = null,
    string? Model = null,
    string? Language = null,
    double? Confidence = null);

/// <summary>
/// Trace of every prompt actually sent to a provider (owner request 2026-07-18) —
/// transcription bias prompts/keyterms, composed enhancement system+user messages, image
/// prompts — plus the raw text each request RETURNED (owner request 2026-07-26), appended
/// verbatim to <c>Logs/prompts-yyyyMMdd.log</c> so full payloads never ride the main log or
/// any Sentry-adjacent pipeline. Available in ALL builds since REL-17 (was
/// <c>[Conditional("DEBUG")]</c>). The RAW file is gated by the runtime opt-in
/// (<see cref="AppDefaults.PromptTraceLoggingOptIn"/>, default OFF, import-protected, and
/// deliberately a NEW key so a stale Debug-era opt-in can never activate Release tracing —
/// see <c>PromptTraceKeyMigration</c>); every write is fail-soft — diagnostics must never
/// break the pipeline.
///
/// <para><b>Two files per day (REL-17), and since 2026-09-13 only ONE of them is optional:</b>
/// the RAW file above (byte-format unchanged), and a REDACTED sidecar
/// <c>prompts-redacted-yyyyMMdd.log</c> rendered AT WRITE TIME from the typed
/// <see cref="PromptTraceOp"/> + <see cref="TraceMeta"/> + entry structure only — no parser
/// exists anywhere (a parse-time redactor is injectable by dictated content that mimics
/// metadata lines; Codex plan round 1). User-authored text — dictation, prompt titles,
/// keyterms, section bodies, unknown labels — reaches the sidecar only as
/// <c>&lt;masked N chars&gt;</c> byte counts. <b>The sidecar is written ALWAYS — every op, no
/// switch</b> (owner decision 2026-09-13, closing the launch defaults audit's item #10): until
/// then both files sat behind the same opt-in, so for every user who never touched it — the
/// default — the support bundle's "redacted sidecars" half was 0 files, and the one file
/// support could read safely was the one nobody had. The bundle ships the sidecar by default
/// and the raw file only on the user's explicit per-report raw opt-in (<c>SupportBundle</c>),
/// re-running secret redaction over both at bundle time. The privacy policy's sentence — the
/// dictated text and AI prompts are written to local log files only if you switch the optional
/// prompt-trace logging on — stays exactly true: the sidecar holds neither.</para>
///
/// <para><b>Image prompts are gated SEPARATELY in the RAW file and excluded by default
/// (REL-21, owner request 2026-08-04):</b> an image prompt describes a picture in the user's
/// own words and is routinely the longest, most personal entry in the whole trace, so
/// <see cref="AppDefaults.PromptTraceIncludeImagePrompts"/> must ALSO be on for
/// <see cref="PromptTraceOp.ImageGeneration"/> / <see cref="PromptTraceOp.ImageGenerationRedo"/>
/// / <see cref="PromptTraceOp.ImagePromptTranscriptionOutput"/> to reach the raw file. That
/// third op exists because the two provider-request ops alone were NOT enough (both diff
/// reviewers found this independently): a voice-dictated image prompt reaches the trace as
/// recognizer output before the pipeline knows it is an image prompt, so the description went
/// in verbatim under a transcription op — the app's primary way of creating an image prompt,
/// and precisely the content this option promises to withhold. <c>MainViewModel</c> therefore
/// defers that entry until image intent is resolved. <b>The sidecar line for an excluded image
/// op IS written</b> (op name, provenance-gated provider/model, the safe scalar options, the
/// description's character count) — REL-21's "neither file, not even a masked sidecar line"
/// is superseded for the sidecar by the always-written rule above; the main application log
/// already records provider/model/options/description-length per generation
/// (<c>AIEnhancementService</c>), so the masked line adds no fact the log does not hold, and
/// the option's promise is about the TEXT, which still never lands. Both raw gates live in
/// <see cref="ShouldWriteRaw"/>.</para>
///
/// <para>Privacy posture: GDPR erasure deletes the Logs directory wholesale; the GDPR
/// export's redacted-logs opt-in carries both files (the user's own data going to the
/// user); <see cref="SweepOldTraces"/> (startup + the hourly cleanup pass) deletes traces
/// older than 7 days — Serilog's rolling retention only manages its own
/// <c>voicewink-*</c> files.</para>
/// </summary>
public static class PromptTraceLog
{
    private static readonly object Gate = new();

    /// <summary>Runtime gate, wired once at startup (reads the settings opt-in).
    /// Null (tests / early boot) = disabled.</summary>
    public static Func<bool>? Enabled { get; set; }

    /// <summary>
    /// SECOND gate, consulted only for image-generation ops (REL-21): the sub-option under
    /// the trace opt-in. Null (tests / early boot / unwired) = EXCLUDE — the settings key is
    /// named <see cref="AppDefaults.PromptTraceIncludeImagePrompts"/> rather than
    /// "exclude…" precisely so every no-value state lands on "don't write", including the
    /// upgrade path where the key is absent for every existing user.
    /// </summary>
    public static Func<bool>? ImagePromptsEnabled { get; set; }

    /// <summary>Target directory, wired once at startup (<c>AppPaths.LogsDir</c>).
    /// Settable for tests.</summary>
    public static string? Directory { get; set; }

    internal const string RedactedFilePrefix = "prompts-redacted-";

    /// <summary>
    /// Is this op an image-generation PROMPT (REL-21)? An explicit POSITIVE switch — three members
    /// today (the two provider-request ops plus the deferred recognizer output) — so an
    /// unclassified op is never treated as an image op by accident — the direction that
    /// matters, since the alternative failure is a non-image op silently disappearing from
    /// the trace. Deliberately NOT derived from <see cref="OpName"/> containing "image":
    /// that would couple a behavioural gate to sidecar display copy.
    ///
    /// <para>Adding a <see cref="PromptTraceOp"/> member does not force the author here, so
    /// <c>PromptTraceLogTests</c> pins the verdict for EVERY member — a new op fails that
    /// test until it is classified.</para>
    /// </summary>
    private static bool IsImagePromptOp(PromptTraceOp op) => op switch
    {
        PromptTraceOp.ImageGeneration
            or PromptTraceOp.ImageGenerationRedo
            // The dictated description itself (Codex diff review): a voice-driven image
            // prompt reaches the trace as recognizer output BEFORE the pipeline knows it is
            // an image prompt, so gating only the two provider-request ops would have left
            // the description verbatim in the raw file — the exact content the option
            // promises to withhold, on the app's primary way of creating one.
            or PromptTraceOp.ImagePromptTranscriptionOutput => true,
        _ => false,
    };

    /// <summary>
    /// The ONE decision for the RAW file, shared by all four entry points (REL-21). Central
    /// rather than per-call-site for three reasons: a future image op inherits the exclusion
    /// the moment it is classified; the settings reads stay out of the middle of the image
    /// pipeline; and there is exactly one place to read to know what the raw file can hold.
    /// The redacted sidecar is NOT behind it (2026-09-13) — it is written by every entry point
    /// unconditionally, which is why "suppress the raw, leak the sidecar" is the intended
    /// shape now rather than a hazard.
    ///
    /// <para>Order is load-bearing: <see cref="Enabled"/> short-circuits, so with raw tracing
    /// off the image predicate — and its settings read — never runs at all.</para>
    /// </summary>
    private static bool ShouldWriteRaw(PromptTraceOp op)
        => Enabled?.Invoke() == true
           && (!IsImagePromptOp(op) || ImagePromptsEnabled?.Invoke() == true);

    /// <summary>
    /// <see cref="ShouldWriteRaw"/> with its two delegate reads contained: a throwing settings
    /// read means "no raw entry", never "no sidecar entry" — the sidecar's independence from
    /// the opt-in is the whole point of it being unconditional (Codex plan round).
    /// </summary>
    private static bool RawWanted(PromptTraceOp op)
    {
        try { return ShouldWriteRaw(op); }
        catch { return false; }
    }

    /// <summary>
    /// The raw leg of an entry, contained on its own: a locked or full raw file must not cost
    /// the sidecar line that follows it (Codex plan round). <see cref="WriteRedacted"/> is
    /// fail-soft by itself, so the two legs can never take each other down.
    /// </summary>
    private static void WriteRawContained(string kind, string content)
    {
        try { WriteBody(kind, content); }
        catch { /* Fail-soft: the raw file is the optional half. */ }
    }

    private static void WriteRawIfWanted(PromptTraceOp op, string kind, string content)
    {
        if (RawWanted(op))
            WriteRawContained(kind, content);
    }

    /// <summary>Fixed app-authored sidecar header names — the ONLY strings that can head a
    /// redacted entry.</summary>
    private static string OpName(PromptTraceOp op) => op switch
    {
        PromptTraceOp.GroqTranscription => "groq transcription",
        PromptTraceOp.OpenAITranscription => "openai transcription",
        PromptTraceOp.DeepgramTranscription => "deepgram transcription",
        PromptTraceOp.ElevenLabsTranscription => "elevenlabs transcription",
        PromptTraceOp.LocalWhisperTranscription => "whisper (local) transcription",
        PromptTraceOp.DetectedLanguage => "detected language",
        PromptTraceOp.TextEnhancement => "enhancement",
        PromptTraceOp.ImageGeneration => "image generation",
        PromptTraceOp.ImageGenerationRedo => "image generation (redo)",
        PromptTraceOp.TranscriptionOutput => "transcription output",
        PromptTraceOp.ImagePromptTranscriptionOutput => "transcription output (image prompt)",
        PromptTraceOp.EnhancementOutput => "enhancement output",
        PromptTraceOp.FileTranscriptionOutput => "file transcription output",
        PromptTraceOp.LanguageResolution => "language resolution",
        _ => "trace",
    };

    /// <summary>Providers the sidecar will name verbatim — anything else is omitted.</summary>
    private static readonly HashSet<string> KnownProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Groq", "Deepgram", "ElevenLabs", "OpenAI", "LocalWhisper",
        "Anthropic", "Gemini", "Mistral", "OpenRouter", "Cerebras",
        "LocalServer",
    };

    /// <summary>
    /// Field labels whose VALUES the sidecar may keep (after per-label validation).
    /// Deliberately excludes <c>model</c>/<c>model_id</c> (model identity flows ONLY through
    /// the provenance-aware <see cref="TraceMeta.Model"/>) and <c>endpoint</c>/<c>query</c>
    /// (URLs can embed user data — Deepgram's query carries Dictionary keyterms). Everything
    /// not listed here renders as a masked byte count.
    /// </summary>
    /// <remarks>
    /// <c>languages[]</c> earns its place for the same reason <c>language</c> has it, and its
    /// absence would have been a pointed omission: gpt-transcribe replaced the singular field
    /// with the array one, and getting that wrong produces a 200 with the user's pinned language
    /// silently ignored. A support trace that masked the field name would hide precisely the
    /// evidence needed to spot it. The VALUE is validated as a language tag exactly like the
    /// singular field.
    ///
    /// <para><c>keywords[]</c> is deliberately NOT here — those values are the user's Dictionary.
    /// <c>keywords sent</c> carries the count instead, which is what diagnosis actually needs.</para>
    /// </remarks>
    private static readonly HashSet<string> SafeFieldLabels = new(StringComparer.Ordinal)
    {
        "response_format", "language", "language_code", "languages[]", "keyterms enabled",
        "keywords sent", "diarize", "aspect", "size tier", "quality", "threads",
        "language source",
    };

    /// <summary>Section names the sidecar shows as markers (bodies always mask).</summary>
    private static readonly HashSet<string> SafeSectionNames = new(StringComparer.Ordinal)
    {
        "SYSTEM", "USER", "REASONING",
    };

    /// <summary>
    /// Delete trace files older than 7 days — RAW and redacted alike (one
    /// <c>prompts-*</c> glob covers both names). Called at startup AND from the hourly
    /// cleanup pass (REL-17: startup-only left week-long tray sessions unbounded);
    /// fail-soft per file.
    /// </summary>
    public static void SweepOldTraces()
    {
        try
        {
            var dir = Directory;
            if (string.IsNullOrEmpty(dir) || !global::System.IO.Directory.Exists(dir))
                return;
            // Destructive sweep — same reparse-root + final-path guards as the
            // recording/report sweeps (diff review round 2): a junctioned Logs dir must
            // not let the hourly all-builds sweep delete through it.
            if (VerifiedFileAccess.IsReparsePointOrUnreadable(dir))
                return;
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in global::System.IO.Directory.EnumerateFiles(dir, "prompts-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff &&
                        VerifiedFileAccess.IsVerifiedUnder(file, dir))
                    {
                        File.Delete(file);
                    }
                }
                catch { /* in use / permissions — retry next pass */ }
            }
        }
        catch { /* fail-soft */ }
    }

    /// <summary>Single-payload entry (detected-language reports, ad-hoc content). The raw
    /// file keeps <paramref name="content"/> verbatim; the sidecar renders header + meta
    /// only, with the content as a byte count.</summary>
    public static void Write(PromptTraceOp op, TraceMeta meta, string kind, string content)
    {
        try
        {
            WriteRawIfWanted(op, kind, content);
            WriteRedacted(op, meta, sb => sb.Append(MaskedMarker(content)));
        }
        catch
        {
            // Fail-soft by design: a full disk or locked file (or a throwing Enabled
            // gate) must never surface into the dictation pipeline.
        }
    }

    /// <summary>
    /// Structured overload: renders one <c>label: value</c> line per field (a null or
    /// empty value shows as <c>(none)</c>) and writes the block under
    /// <paramref name="kind"/>, UNCONDITIONALLY per request, so a request with no
    /// Dictionary hints still logs the full request with the bias field shown as
    /// <c>(none)</c>. Never pass an API key or auth header as a field — every client
    /// attaches its key via an HTTP header, never a traced body/query field.
    /// Sidecar: only <see cref="SafeFieldLabels"/> keep validated values; all other fields
    /// collapse into one masked-count line.
    /// </summary>
    public static void Write(PromptTraceOp op, TraceMeta meta, string kind, params (string Label, string? Value)[] fields)
    {
        // Whole path is fail-soft AND the raw gates are evaluated once (Codex diff r1): the
        // try wraps the gates + compose + write so a throwing Enabled — or, since REL-21,
        // ImagePromptsEnabled — delegate can never break the pipeline. ShouldWriteRaw
        // short-circuits on the parent, so the image predicate only runs for an image op with
        // raw tracing already on. The raw body is composed only when wanted; the sidecar leg
        // below runs whatever the gates said (2026-09-13).
        try
        {
            if (RawWanted(op))
            {
                var sb = new global::System.Text.StringBuilder();
                for (var i = 0; i < fields.Length; i++)
                {
                    if (i > 0)
                        sb.Append(Environment.NewLine);
                    sb.Append(fields[i].Label).Append(": ")
                      .Append(string.IsNullOrEmpty(fields[i].Value) ? "(none)" : fields[i].Value);
                }
                WriteRawContained(kind, sb.ToString());
            }

            WriteRedacted(op, meta, rsb =>
            {
                var maskedCount = 0;
                var maskedChars = 0;
                var first = true;
                foreach (var (label, value) in fields)
                {
                    if (SafeFieldLabels.Contains(label))
                    {
                        var rendered = string.IsNullOrEmpty(value) ? "(none)" : ValidateScalar(label, value);
                        if (!first) rsb.Append(Environment.NewLine);
                        rsb.Append(label).Append(": ").Append(rendered);
                        first = false;
                    }
                    else
                    {
                        maskedCount++;
                        maskedChars += value?.Length ?? 0;
                    }
                }
                if (maskedCount > 0)
                {
                    if (!first) rsb.Append(Environment.NewLine);
                    rsb.Append("(+").Append(maskedCount).Append(" field(s) masked, ")
                       .Append(maskedChars).Append(" chars)");
                }
            });
        }
        catch
        {
            // Fail-soft.
        }
    }

    /// <summary>
    /// Bracket-section overload: each section renders as a <c>[NAME]</c> marker line
    /// with a blank line BEFORE and AFTER every marker — including one right after the
    /// entry header — so multi-paragraph payloads (the enhancement system/user
    /// messages) don't run together (owner request 2026-07-26). A null/empty body
    /// renders as <c>(none)</c>, matching the fields overload, so the section is still
    /// visibly traced. Sidecar: known section markers survive; bodies always mask;
    /// unknown marker NAMES mask too (a name is a string a future call site could feed
    /// from user data).
    /// </summary>
    public static void WriteSections(PromptTraceOp op, TraceMeta meta, string kind, params (string Name, string? Content)[] sections)
    {
        try
        {
            if (RawWanted(op))
            {
                var sb = new global::System.Text.StringBuilder();
                for (var i = 0; i < sections.Length; i++)
                {
                    if (i > 0)
                        sb.Append(Environment.NewLine);
                    sb.Append(Environment.NewLine)
                      .Append('[').Append(sections[i].Name).Append(']')
                      .Append(Environment.NewLine).Append(Environment.NewLine)
                      .Append(string.IsNullOrEmpty(sections[i].Content) ? "(none)" : sections[i].Content);
                }
                WriteRawContained(kind, sb.ToString());
            }

            WriteRedacted(op, meta, rsb =>
            {
                var first = true;
                foreach (var (name, content) in sections)
                {
                    if (!first) rsb.Append(Environment.NewLine);
                    var marker = SafeSectionNames.Contains(name) ? name : "masked section";
                    rsb.Append('[').Append(marker).Append("] ")
                       .Append(string.IsNullOrEmpty(content) ? "(none)" : MaskedMarker(content));
                    first = false;
                }
            });
        }
        catch
        {
            // Fail-soft.
        }
    }

    /// <summary>
    /// Output-entry variant: traces the raw text a provider RETURNED (transcription /
    /// enhancement outputs). A null / empty / whitespace-only result renders as
    /// <c>(empty)</c> — a 200-with-no-words response is exactly the case worth seeing
    /// (REL-13-shaped debugging), and bare whitespace would look like a formatting
    /// accident. The sidecar PRESERVES <c>(empty)</c> verbatim (the diagnostic) and
    /// masks everything else to a byte count.
    /// </summary>
    public static void WriteOutput(PromptTraceOp op, TraceMeta meta, string kind, string? text)
    {
        try
        {
            var isEmpty = string.IsNullOrWhiteSpace(text);
            WriteRawIfWanted(op, kind, isEmpty ? "(empty)" : text!);
            WriteRedacted(op, meta, sb => sb.Append(isEmpty ? "(empty)" : MaskedMarker(text!)));
        }
        catch
        {
            // Fail-soft.
        }
    }

    private static string MaskedMarker(string content)
        => $"<masked {content.Length} chars>";

    /// <summary>
    /// Per-label scalar validation for whitelisted fields: language labels must look like
    /// language tags; <c>threads</c> must be an integer; everything else is bounded to a
    /// short single-line value. Anything failing masks — a whitelisted LABEL never
    /// guarantees its VALUE (a multiline prompt could ride any tuple; Codex rounds 2/3).
    /// </summary>
    private static string ValidateScalar(string label, string value)
    {
        if (label is "language" or "language_code" or "languages[]")
            return ValidateLanguage(value) ?? MaskedMarker(value);
        // REL-26: exact member-name set, deliberately NOT Enum.TryParse — that also accepts
        // numeric strings ("3"), and a whitelisted LABEL never guarantees its VALUE.
        if (label is "language source")
            return value is nameof(LanguageSource.Global) or nameof(LanguageSource.Retry)
                or nameof(LanguageSource.Prompt) or nameof(LanguageSource.AppMode)
                ? value
                : MaskedMarker(value);
        if (label is "keywords sent")
            return int.TryParse(value, global::System.Globalization.NumberStyles.Integer,
                global::System.Globalization.CultureInfo.InvariantCulture, out var count)
                ? count.ToString(global::System.Globalization.CultureInfo.InvariantCulture)
                : MaskedMarker(value);
        if (label is "threads")
            return int.TryParse(value, global::System.Globalization.NumberStyles.Integer,
                global::System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? n.ToString(global::System.Globalization.CultureInfo.InvariantCulture)
                : MaskedMarker(value);
        return IsCleanScalar(value, 32) ? LogRedactionEnricher.RedactString(value) : MaskedMarker(value);
    }

    /// <summary>Language tags: BCP-47-ish (<c>en</c>, <c>pt-BR</c>) or the app's literal
    /// <c>auto</c> / <c>auto (detect)</c> forms.</summary>
    private static string? ValidateLanguage(string value)
    {
        if (value is "auto" or "auto (detect)")
            return value;
        if (!IsCleanScalar(value, 24))
            return null;
        return global::System.Text.RegularExpressions.Regex.IsMatch(
            value, "^[a-z]{2,3}(-[A-Za-z0-9]{1,8})*$")
            ? value
            : null;
    }

    /// <summary>Model ids: conservative identifier charset, bounded length.</summary>
    private static bool IsModelIdShaped(string value)
        => value.Length is >= 1 and <= 64 &&
           global::System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9._:/\-]+$");

    /// <summary>Single line, bounded, no control/bidi/zero-width characters.</summary>
    private static bool IsCleanScalar(string value, int maxLength)
    {
        if (value.Length == 0 || value.Length > maxLength)
            return false;
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
                return false;
            if (ch is '​' or '‌' or '‍' or '⁠' or '﻿')
                return false; // zero-width
            if (ch is >= '‪' and <= '‮')
                return false; // bidi embedding/override
            if (ch is >= '⁦' and <= '⁩')
                return false; // bidi isolates
        }
        return true;
    }

    /// <summary>Renders the provenance-gated model part of a sidecar header. Membership
    /// in the app's catalogs (<see cref="KnownModelIds"/>) is the ONLY thing that lets an
    /// id through verbatim — regex shape is not provenance, and neither is "came from
    /// settings" (diff review: local model names are arbitrary user filenames).</summary>
    private static string? RenderModel(in TraceMeta meta)
    {
        if (string.IsNullOrEmpty(meta.Model))
            return null;
        if (KnownModelIds.IsKnown(meta.Model) && IsModelIdShaped(meta.Model))
            return LogRedactionEnricher.RedactString(meta.Model);
        // Downloaded/custom/unverifiable id: hash prefix — correlation without content.
        var hash = global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(meta.Model));
        return $"custom (sha:{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()})";
    }

    /// <summary>
    /// Append one sidecar entry: fixed-op header + validated meta lines + the body the
    /// caller composes (already masked/validated by construction). Takes
    /// <see cref="Gate"/> itself; fail-soft — the sidecar must never take the raw trace
    /// (or the pipeline) down.
    /// </summary>
    private static void WriteRedacted(PromptTraceOp op, in TraceMeta meta, Action<global::System.Text.StringBuilder> body)
    {
        try
        {
            var dir = Directory;
            if (string.IsNullOrEmpty(dir))
                return;

            var header = new global::System.Text.StringBuilder(OpName(op));
            if (!string.IsNullOrEmpty(meta.Provider) && KnownProviders.Contains(meta.Provider))
                header.Append(" · ").Append(meta.Provider);
            var model = RenderModel(meta);
            if (model != null)
                header.Append(" · ").Append(model);

            var sb = new global::System.Text.StringBuilder();
            var language = meta.Language == null ? null : ValidateLanguage(meta.Language);
            if (language != null)
                sb.Append("language: ").Append(language).Append(Environment.NewLine);
            if (meta.Confidence is double confidence)
                sb.Append("confidence: ")
                  .Append(confidence.ToString("0.###", global::System.Globalization.CultureInfo.InvariantCulture))
                  .Append(Environment.NewLine);
            body(sb);

            global::System.IO.Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{RedactedFilePrefix}{DateTime.Now:yyyyMMdd}.log");
            var entry =
                $"───── {DateTime.Now:HH:mm:ss.fff} · {header} ─────{Environment.NewLine}" +
                $"{sb}{Environment.NewLine}{Environment.NewLine}";
            lock (Gate)
                File.AppendAllText(path, entry);
        }
        catch
        {
            // Fail-soft.
        }
    }

    // Shared RAW-file IO body. Callers gate on ShouldWriteRaw (via WriteRawIfWanted) and wrap
    // the call in try/catch, so this stays lean. Byte-format unchanged since the DEBUG-only era
    // (test-pinned).
    private static void WriteBody(string kind, string content)
    {
        var dir = Directory;
        if (string.IsNullOrEmpty(dir))
            return;
        global::System.IO.Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"prompts-{DateTime.Now:yyyyMMdd}.log");
        var entry =
            $"───── {DateTime.Now:HH:mm:ss.fff} · {kind} ─────{Environment.NewLine}" +
            $"{content}{Environment.NewLine}{Environment.NewLine}";
        lock (Gate)
            File.AppendAllText(path, entry);
    }
}

using System.Text.Json.Serialization;

namespace VoiceWink.Models;

/// <summary>
/// Custom AI enhancement prompt.
/// </summary>
public class CustomPrompt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// UPD-4 provenance: the immutable <see cref="TemplatePrompt.Key"/> this prompt was
    /// seeded from (null for user-created prompts). How <c>DefaultPromptsReseed</c> matches a
    /// persisted default across versions to overwrite its content. The runtime <see cref="Id"/>
    /// stays a GUID — App-Mode <c>LinkedEnhancementId</c> references the Id, never the SeedKey —
    /// so a re-seed preserves the Id and links never dangle.
    /// </summary>
    public string? SeedKey { get; set; }

    public string Title { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;
    public string Icon { get; set; } = "E945"; // FontIcon glyph
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public bool IsPredefined { get; set; }

    private List<string>? _triggerWords;
    /// <summary>
    /// Trigger words that auto-activate this prompt when detected in transcribed text.
    /// Uses a backing field to handle null from JSON deserialization of legacy data.
    /// </summary>
    public List<string> TriggerWords
    {
        get => _triggerWords ??= new();
        set => _triggerWords = value ?? new();
    }

    /// <summary>
    /// Optional hotkey that activates this prompt for the next recording (e.g. "F2", "F5").
    /// Works like a trigger word but activated via keyboard instead of voice.
    /// </summary>
    public string? Hotkey { get; set; }

    /// <summary>
    /// When true, the transcription is sent to an image generation API instead of text enhancement.
    /// The generated image is placed on the clipboard and pasted.
    /// </summary>
    public bool IsImageGeneration { get; set; }

    /// <summary>
    /// Optional provider override for this prompt. When set, this provider is used instead of
    /// the main provider selection. Stored as string for JSON compatibility; parsed to AIProvider at use-time.
    /// Null or empty means use the default provider.
    /// </summary>
    public string? ProviderOverride { get; set; }

    /// <summary>
    /// Optional model override for this prompt. When set, this model is used instead of
    /// the main text/image model selection. Null or empty means use the default.
    /// </summary>
    public string? ModelOverride { get; set; }

    /// <summary>
    /// ENH-8 (phase A, DEBUG-only): per-prompt reasoning-effort choice for TEXT
    /// enhancement — <c>"minimal"</c> / <c>"thorough"</c>, null = Default, which since ENH-23
    /// means the model's dictation-default row (or nothing, for a model without one).
    /// Parsed fail-soft by <see cref="VoiceWink.Helpers.ReasoningEffortPolicy.Parse"/>;
    /// honoured only in Debug builds — a Release build never reads it and sends the default
    /// row regardless (pinned by ReasoningReleaseGateTests).
    /// WhenWritingNull keeps untouched prompts' serialized form byte-identical. Known
    /// downgrade note (recorded): a PRE-feature build that re-saves prompts drops this
    /// unknown property — loss scope is this debug-only preference.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningOverride { get; set; }

    /// <summary>
    /// PRM-5: per-prompt SPEECH-RECOGNITION language (ISO code, or <c>"auto"</c> to detect);
    /// null = inherit the App-Mode override if one applies, else the global Settings language.
    /// <para>Exists because the only reliable way to translate was to set the GLOBAL transcription
    /// language to the SOURCE language (UAT 14.7, 2026-07-25 — owner: "the language override was
    /// supposed to be on the AI enhancement… that was the whole point"). A "Translate to English"
    /// prompt can now carry its own source language.</para>
    /// <para>Applies ONLY when this prompt is the effective one BEFORE recognition runs — chosen by
    /// hotkey, by an App-Mode link, or globally active. A prompt selected by a SPOKEN TRIGGER WORD is
    /// matched in the transcript, i.e. after recognition, so it cannot retroactively change how the
    /// audio was recognised; the Configure dialog states this next to the control rather than hiding
    /// it (Codex plan review: the same prompt may be reachable by hotkey/App Mode too, where it
    /// DOES work).</para>
    /// WhenWritingNull keeps untouched prompts byte-identical on disk. Same recorded downgrade note
    /// as <see cref="ReasoningOverride"/>: a pre-feature build re-saving prompts drops the property.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LanguageOverride { get; set; }

    /// <summary>
    /// Aspect ratio chosen by the user (e.g. "1:1", "16:9"). Null means "Auto" — let the model decide.
    /// Only used when IsImageGeneration is true. See <see cref="VoiceWink.Helpers.ImageOptions"/>.
    /// </summary>
    public string? ImageAspect { get; set; }

    /// <summary>
    /// Pixel-resolution tier chosen by the user ("1K", "2K", "4K"). Null means "Auto".
    /// On gpt-image-2 and the two named gpt-image-2.5 models (<c>ImageOptions.HasFlexibleSize</c>)
    /// this combines with <see cref="ImageAspect"/> to synthesize the
    /// <c>size</c> API parameter; on Gemini-native it maps to <c>imageConfig.imageSize</c>.
    /// Only used when IsImageGeneration is true.
    /// </summary>
    public string? ImageSizeTier { get; set; }

    /// <summary>
    /// When true, the user is prompted to pick image options (aspect, size, quality)
    /// before each generation. Only used when IsImageGeneration is true.
    /// </summary>
    public bool AskImageSize { get; set; }

    /// <summary>
    /// Image quality (detail / token-count) tier for image generation. Values: the tags in
    /// <c>ImageOptions.AllQualities</c> — "standard", "enhanced", "maximum" (= wire <c>high</c>, shown
    /// as "High"), and since IMG-14 "xhigh" and "max" (statically gpt-image-2.5-only, clamped DOWN
    /// elsewhere; an OpenRouter capability snapshot may explicitly offer them on another model).
    /// Null = auto. Only meaningful for OpenAI gpt-image — Gemini has no separate detail knob
    /// (its imageSize covers that role).
    /// </summary>
    public string? ImageQuality { get; set; }

    public DateTime DateCreated { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Creates a shallow copy with all properties duplicated.
    /// Use when you need a temporary override (e.g. image size picker)
    /// without mutating the cached original.
    /// </summary>
    public CustomPrompt Clone()
    {
        var copy = (CustomPrompt)MemberwiseClone();
        copy.TriggerWords = new List<string>(TriggerWords);
        return copy;
    }
}

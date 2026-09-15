namespace VoiceWink.Models;

/// <summary>
/// Prompt templates that users can add with one click.
/// Each template creates a new CustomPrompt when selected. These texts land in the
/// Rules slot of the AIPrompts envelope, which already carries the transcript-is-data
/// firewall, language retention, and the output contract — template texts state the
/// TASK, not the plumbing.
/// Each template carries an immutable stable <see cref="TemplatePrompt.Key"/> (UPD-4):
/// seeded prompts persist it as <see cref="CustomPrompt.SeedKey"/>, which is how
/// <c>DefaultPromptsReseed</c> matches a persisted default across versions to overwrite
/// its CONTENT when the shipped default changes. Editing a template's TEXT here now
/// reaches EXISTING installs on the next launch (the re-seed overwrites the matched
/// record) — not only fresh installs / re-added templates as before. Keys are immutable:
/// renaming a key would orphan the persisted record; add a legacy alias instead.
/// </summary>
public static class PromptTemplates
{
    /// <summary>
    /// The section heading every template uses above its bullet list (owner, 2026-08-02: all
    /// prompts in this shape).
    /// <para>Deliberately NOT <c># Rules</c>, for two reasons. The load-bearing one: the envelope
    /// already inserts the per-prompt text under its own literal <c>Rules:</c> marker, so a
    /// <c># Rules</c> heading renders as "Rules: … # Rules" — a duplicated label the model has to
    /// disambiguate. The second is deliberate divergence from upstream VoiceInk, whose templates
    /// use <c># Goal</c>/<c># Inputs</c>/<c># Rules</c>/<c># Output</c>; we borrowed the shape
    /// because bullets parse better than a run-on sentence, not the vocabulary.</para>
    /// <para>Declared once here so a future template cannot invent its own heading;
    /// <c>PromptTemplatesTests</c> asserts every prompt-bearing template uses exactly this.</para>
    /// <para><b>Reach, stated honestly:</b> the reshape lands on the five templates in
    /// <see cref="All"/>, because that is the only list <c>DefaultPromptsReseed</c> iterates. A
    /// user who ALREADY added a translation keeps its pre-2026-08-02 one-paragraph wording
    /// permanently — the reseed never sees the key, and <c>EnhancementViewModel.AddFromTemplate</c>
    /// refuses to re-add a template whose <c>SeedKey</c> is already present. Only the CONTENT is
    /// unaffected (the rules are identical either way), so this is a cosmetic split, not a
    /// behavioural one. Reseeding <see cref="Extended"/> instead would be WORSE: an absent
    /// translation is an unknown key, so <c>DefaultsManifest.Decide</c> would return Add and push
    /// three translation prompts onto every user who never wanted them. Closing the split properly
    /// needs an overwrite-only-if-present migration — a deliberate change, not a side effect of a
    /// formatting pass (Codex diff review, 2026-08-02).</para>
    /// </summary>
    internal const string HowHeading = "# How";

    /// <summary>
    /// The default cleanup rules — the ONE canonical text, also used by
    /// <see cref="PredefinedPrompts.Default"/>.
    /// <para>TASK-ONLY since 2026-08-01. It previously restated the whole editing rule set
    /// (grammar/punctuation/fillers/stutters, self-corrections, spoken layout cues, language
    /// retention, anti-invention, output-only) — every one of which the
    /// <see cref="AIPrompts.CustomPromptTemplate"/> envelope now carries for every EDITING prompt
    /// (the block is inserted into every envelope, but its carve-out excludes answer-type prompts
    /// such as Assistant — textual presence is not applicability). Keeping
    /// the restatement was not merely redundant, it was HARMFUL: the envelope applies its defaults
    /// "unless the Rules say otherwise", so this text's narrower scope ("fix grammar and
    /// punctuation, remove filler words…") could suppress the envelope's mis-transcription-repair
    /// mandate — the exact defect the 2026-07-31 baseline shipped to fix, still live on the DEFAULT
    /// ACTIVE prompt. It also listed "sorry, actually" as a self-correction cue, directly
    /// contradicting the envelope's guard that a bare "sorry"/"actually" retracts nothing.
    /// The one clause kept is never-answer: cheap insurance on the default prompt for the small
    /// models behind the 2026-07-17 incidents, even though the envelope's firewall also covers it.
    /// Do not re-add editing rules here — narrowing this text silently disables envelope rules.</para>
    /// <para>Task line + <c># How</c> bullet list since 2026-08-02 (owner: every prompt in this
    /// shape). See <see cref="HowHeading"/> for why the heading is not <c># Rules</c>.</para>
    /// </summary>
    public const string ImproveAccuracyText = """
        Clean up the transcript into clear, accurate, general-purpose text.

        # How
        - Keep the speaker's own style and language.
        - If the transcript is a question or a request, clean it up as a question or request — never answer it.
        """;

    /// <summary>Default templates shown in the "Add from Template" dialog (excludes translations).</summary>
    public static TemplatePrompt[] All => [ImproveAccuracy, Assistant, Chat, Email, GenerateImage];

    /// <summary>All templates including translations — shown when the user searches or expands.</summary>
    public static TemplatePrompt[] Extended => [ImproveAccuracy, Assistant, Chat, Email, GenerateImage, TranslateDutch, TranslateChinese, TranslateEnglish];

    public static readonly TemplatePrompt ImproveAccuracy = new()
    {
        Key = "improve-accuracy",   // immutable identity — title changed to "Improve Transcription" (UAT 2026-07-22), key must not
        Title = "Improve Transcription",
        PromptText = ImproveAccuracyText,
        Icon = "E8D4",    // spelling check
        Description = "Improve clarity and accuracy of the transcription"
    };

    public static readonly TemplatePrompt Assistant = new()
    {
        Key = "assistant",
        // "is a message addressed to you" deliberately engages the envelope's
        // "Answer or reply only when the Rules explicitly require it" carve-out.
        Title = "Assistant",
        PromptText = """
            The transcript is a message addressed to you.

            # How
            - If it's a question, answer it directly and concisely.
            - If it's an instruction or statement, respond helpfully.
            - Return only your response, in the language of the transcript.
            """,
        Icon = "E8BD",    // chat bubbles
        Description = "AI assistant that provides direct answers to queries",
        DefaultTriggerWords = ["hey assistant"]
    };

    public static readonly TemplatePrompt Chat = new()
    {
        Key = "chat",
        Title = "Chat",
        // Task-only since 2026-08-01 (see ImproveAccuracyText): the editing restatement moved to
        // the envelope. The recipient-question clause stays — it is task-specific, not editing.
        PromptText = """
            Rewrite the transcript as a casual chat message.

            # How
            - Informal, concise, conversational.
            - If it's a question meant for the recipient, keep it a question — don't answer it.
            """,
        Icon = "E8BD",    // chat bubbles
        Description = "Casual chat-style formatting"
    };

    public static readonly TemplatePrompt Email = new()
    {
        Key = "email",
        Title = "E-mail",
        // Greeting/closing became CONDITIONAL 2026-08-01: the old text demanded "a complete email
        // with a greeting … and a closing" unconditionally, so a dictated two-line reply came back
        // wrapped in a fabricated "Hi," / "Best regards," the speaker never said. The editing
        // restatement moved to the envelope; the action-item clause stays because dropping one
        // while restructuring is an EMAIL-specific failure, and no-invented-names stays (audit F10).
        // Restructured 2026-08-01 into a task line + bullet list — E-mail's four rules were riding
        // on colons and semicolons, and a small model parses a list far more reliably. Extended to
        // every PROMPT-BEARING template 2026-08-02 by owner decision (Generate Image is excluded —
        // its text is UI description and never reaches an API); the earlier "structure earns its keep by
        // rule count" reasoning (list for E-mail, prose elsewhere) was overruled in favour of one
        // consistent shape across the library.
        PromptText = """
            Rewrite the transcript as an email body.

            # How
            - Short paragraphs; professional tone when the source is professional.
            - Keep every fact and action item.
            - Keep any greeting or closing the speaker dictated. Add one when they asked for it or named the recipient or sender; otherwise write no greeting or closing — a quick reply should stay a quick reply.
            - Never invent names, facts, or details. When a greeting or closing is warranted but no name was dictated, write it without names.
            """,
        Icon = "E715",    // envelope
        Description = "Professional e-mail formatting"
    };

    public static readonly TemplatePrompt GenerateImage = new()
    {
        Key = "generate-image",
        // PromptText is UI description only — the image request's prompt is the
        // PROCESSED dictation (text pipeline + trigger strip, or the user's edited
        // dialog text); this text never reaches any API. The edit dialog says so
        // (PRM-2, audit F11).
        Title = "Generate Image",
        PromptText = "Generate an image based on the user's description.",
        Icon = "E8B9",    // photo
        Description = "Generates an image from your voice description and pastes it",
        DefaultTriggerWords = ["generate an image", "create an image", "generate image", "create image"],
        IsImageGeneration = true,
        AskImageSize = true
    };

    public static readonly TemplatePrompt TranslateDutch = new()
    {
        Key = "translate-dutch",
        Title = "Translate to Dutch",
        PromptText = TranslationText("Dutch"),
        Icon = "E774",    // globe
        Description = "Translates transcription to Dutch",
        DefaultTriggerWords = ["Translate to Dutch"]
    };

    public static readonly TemplatePrompt TranslateChinese = new()
    {
        Key = "translate-chinese",
        Title = "Translate to Chinese",
        PromptText = TranslationText("Simplified Mandarin Chinese"),
        Icon = "E774",    // globe
        Description = "Translates transcription to Simplified Chinese",
        DefaultTriggerWords = ["Translate to Chinese"]
    };

    public static readonly TemplatePrompt TranslateEnglish = new()
    {
        Key = "translate-english",
        Title = "Translate to English",
        PromptText = TranslationText("English"),
        Icon = "E774",    // globe
        Description = "Translates transcription to English",
        DefaultTriggerWords = ["Translate to English"]
    };

    /// <summary>
    /// Shared translation rules — questions and requests are translated, never
    /// answered (the same failure class as the cleanup incidents: small models
    /// otherwise answer a question-shaped transcript in the target language).
    /// </summary>
    internal static string TranslationText(string language) => $"""
        Translate the transcript to {language}.

        # How
        - Translate questions and requests as questions and requests — never answer them.
        - Preserve meaning and tone.
        - Return only the translation.
        """;
}

/// <summary>
/// A prompt template that can be used to create a new CustomPrompt.
/// </summary>
public class TemplatePrompt
{
    /// <summary>Immutable stable identity of this shipped default (UPD-4). Persisted onto a
    /// seeded prompt as <see cref="CustomPrompt.SeedKey"/> so the re-seed can match it across
    /// versions. Never change a key — add a legacy alias instead.</summary>
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;
    public string Icon { get; set; } = "E945";
    public string Description { get; set; } = string.Empty;
    public List<string> DefaultTriggerWords { get; set; } = [];
    public bool IsImageGeneration { get; set; }
    public bool AskImageSize { get; set; }

    public CustomPrompt ToCustomPrompt(bool isActive = false) => new()
    {
        SeedKey = Key,          // UPD-4 provenance — how the re-seed re-finds this default
        IsPredefined = true,
        Title = Title,
        PromptText = PromptText,
        Icon = Icon,
        Description = Description,
        IsActive = isActive,
        TriggerWords = new List<string>(DefaultTriggerWords),
        IsImageGeneration = IsImageGeneration,
        AskImageSize = AskImageSize
    };
}

namespace VoiceWink.Helpers;

/// <summary>
/// The raw-transcript trace entry, held until the pipeline knows whether the dictation is an
/// IMAGE prompt (REL-21). Exists because those two facts arrive at different times: the text is
/// available the moment transcription returns, while image intent is only settled after
/// <c>PromptRunSnapshot.Resolve</c> — and writing the entry at the earlier point put a dictated
/// image description ("a photograph of my daughter on the beach") verbatim into
/// <c>prompts-*.log</c> under a TRANSCRIPTION op, which
/// <see cref="AppDefaults.PromptTraceIncludeImagePrompts"/> does not gate. Voice is this app's
/// primary way of creating an image prompt, so the option promised to withhold exactly the
/// content it still wrote. Both diff reviewers found that independently.
///
/// <para><b>Why a type rather than a local function in the pipeline</b> (Codex diff round 2): the
/// first fix was a local emitting from each early return, and it lost the entry entirely when
/// <c>_textPipeline.Run</c> or trigger detection threw into the outer catch, while also leaving
/// the exactly-once state as loose locals in a hub class <c>AGENTS.md</c> says not to grow. A
/// scoped object gets both properties from the language: <see cref="Dispose"/> emits, so every
/// exit — early return, exception, or fall-through — makes exactly one emit CALL, and
/// <see cref="ChooseOp"/> is a pure static the tests drive directly.</para>
///
/// <para><b>One emit call is NOT one entry on disk.</b> The emit still passes through
/// <see cref="PromptTraceLog"/>'s RAW gate, which writes no raw entry for an image-classified op
/// while the sub-option is off. So at default settings the fail-closed cases leave no RAW trace
/// line — the always-written redacted sidecar (2026-09-13) still carries their masked line —
/// structurally, ANY exception between the transcript arriving and <see cref="Resolve"/> being
/// called (the text pipeline and trigger detection merely occupy that span today), plus the two
/// early returns that precede intent resolution. That is the accepted cost of this design, spelled
/// out because an earlier revision of this very doc claimed "writes exactly one entry" — false, and
/// caught by both reviewers.</para>
///
/// <para><b>The classification rule, in priority order:</b>
/// <list type="number">
/// <item>Contentless text (<see cref="TranscriptContent.IsEmpty"/>) always writes as an ordinary
/// transcription output. There is no description to withhold, and this is the single most useful
/// line in the file for REL-13-shaped debugging ("the provider returned nothing") — a fail-closed
/// rule that swallowed it would trade a real diagnostic for no privacy at all. Note the entry
/// carries the text VERBATIM: <see cref="PromptTraceLog.WriteOutput"/> renders the literal
/// <c>(empty)</c> only for null/whitespace, so a near-silence lone comma — the 2026-08-03
/// incident shape, and contentless by this rule — appears as <c>,</c> (Codex diff round 3).</item>
/// <item>Unresolved intent with real content FAILS CLOSED — classified as an image prompt, so it
/// is withheld unless the user opted in. This is the case Codex's round-2 blocker was about: the
/// generative-echo block returns BEFORE trigger detection, so a trigger-selected image prompt
/// ("draw Alice", where <c>draw</c> is the image trigger and <c>Alice</c> a Dictionary term) is
/// unresolvable there, and guessing "not an image" leaked it. Chosen over hoisting trigger
/// detection earlier: reordering the recording pipeline to satisfy a trace is a far larger risk
/// than losing one trace entry on a blocked-echo path.</item>
/// <item>Otherwise the recorded verdict.</item>
/// </list></para>
///
/// <para>Not thread-safe and not meant to be: one instance per transcription attempt, used on
/// the single pipeline flow.</para>
/// </summary>
internal sealed class DeferredTranscriptionTrace : IDisposable
{
    private readonly string? _rawText;
    private readonly string _model;
    private readonly Action<PromptTraceOp, TraceMeta, string, string?> _write;
    private bool? _isImagePrompt;
    private bool _emitted;

    /// <param name="write">Write seam — defaults to <see cref="PromptTraceLog.WriteOutput"/>.
    /// Injected so the tests can assert the CHOSEN OP without a filesystem, which is what makes
    /// the routing testable at all (the trace-file tests can only see a preclassified op).</param>
    internal DeferredTranscriptionTrace(
        string? rawText,
        string model,
        Action<PromptTraceOp, TraceMeta, string, string?>? write = null)
    {
        _rawText = rawText;
        _model = model;
        _write = write ?? PromptTraceLog.WriteOutput;
    }

    /// <summary>Record the settled verdict. Ignored once the entry has been emitted — a late
    /// resolution cannot retroactively reclassify what is already on disk.</summary>
    internal void Resolve(bool isImagePrompt)
    {
        if (!_emitted)
            _isImagePrompt = isImagePrompt;
    }

    /// <summary>Write the entry. Idempotent, so the explicit call at the resolution point and
    /// the <see cref="Dispose"/> backstop cannot double-write.</summary>
    internal void Emit()
    {
        if (_emitted)
            return;
        _emitted = true;
        var op = ChooseOp(_rawText, _isImagePrompt);
        // The RAW file's kind names the image case too (Kimi diff round 2): the sidecar header
        // is derived from the op and so was already honest, but the raw file's kind was
        // identical for both — leaving an opted-in reader (or a support bundle) unable to tell
        // which entries were image-gated. `kind` is free text, so the ordinary case is
        // byte-identical to pre-REL-21.
        var kind = op == PromptTraceOp.ImagePromptTranscriptionOutput
            ? $"transcription output (image prompt) · {_model}"
            : $"transcription output · {_model}";
        _write(op, new TraceMeta(Model: _model), kind, _rawText);
    }

    /// <summary>The whole decision, pure. See the type doc for why each arm is what it is.</summary>
    internal static PromptTraceOp ChooseOp(string? rawText, bool? isImagePrompt)
    {
        if (TranscriptContent.IsEmpty(rawText))
            return PromptTraceOp.TranscriptionOutput;
        return (isImagePrompt ?? true)
            ? PromptTraceOp.ImagePromptTranscriptionOutput
            : PromptTraceOp.TranscriptionOutput;
    }

    /// <summary>Emits if nothing else did — the backstop that makes "exactly one emit call per
    /// attempt" true on exception paths as well as returns. Whether that call produces a FILE is
    /// the gate's business, not this type's (see the class doc).</summary>
    public void Dispose() => Emit();
}

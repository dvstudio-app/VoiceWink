using VoiceWink.Models;

namespace VoiceWink.Services.Transcription;

/// <summary>
/// How a transcription service consumes vocabulary hints (PRM-4). The echo gate engages for
/// every transport that can put hint text in front of a GENERATIVE model —
/// <see cref="GenerativePrompt"/> and <see cref="StructuredKeywords"/> — and only
/// <see cref="Keyterms"/> is exempt.
/// </summary>
public enum HintTransportKind
{
    /// <summary>No hints sent. Every OpenAI transcription model except <c>gpt-transcribe</c>.</summary>
    None,
    /// <summary>
    /// Hints ride an initial generative prompt — can be echoed.
    ///
    /// <para><b>NO SHIPPED SERVICE DECLARES THIS, and that is enforced by a test</b>
    /// (<c>HintTransportContractTests</c>). Local Whisper and Groq Whisper both did until
    /// TRN-11 (2026-08-11), when the echo was measured to DESTROY transcripts rather than
    /// merely pollute them — evidence on <c>GroqClient.HintTransport</c>. The member is
    /// kept rather than deleted because it is the honest name for what the wire field
    /// still IS: both endpoints continue to accept a bias prompt, and a future client
    /// could fill one. Keeping it makes that a deliberate, test-failing act instead of an
    /// invisible one, and keeps <c>TriggerEchoGate</c>'s reasoning legible.</para>
    ///
    /// <para>Anything re-declaring this needs the measurement, not the argument: three
    /// runs of one quiet recording moved a correct 124-character transcript to 10 chars,
    /// 442 chars of hallucination, and 2 chars.</para>
    /// </summary>
    GenerativePrompt,
    /// <summary>Hints ride discriminative keyterm biasing (Deepgram, ElevenLabs) — never echoed.</summary>
    Keyterms,
    /// <summary>
    /// Hints ride a STRUCTURED keyword field on a generative recognizer — OpenAI
    /// <c>gpt-transcribe</c>'s <c>keywords[]</c>.
    ///
    /// <para>Shaped like <see cref="Keyterms"/>, gated like <see cref="GenerativePrompt"/>, and
    /// that combination is the whole reason it is its own member. Classifying it as
    /// <c>Keyterms</c> would have been the small change — and would have silently bypassed the
    /// echo gate on the strength of an assumption, because that member's "never echoed" contract
    /// is earned by Deepgram and ElevenLabs being DISCRIMINATIVE recognizers. gpt-transcribe is an
    /// LLM, and this project has already observed an OpenAI transcription model echoing its prompt
    /// back on near-silent audio. The no-speech gate that would normally absorb that can be
    /// compiled out (REL-15's <c>-p:VadEnabled=false</c> lever), so it cannot be the only defence.</para>
    ///
    /// <para>The gate is engaged here as DEFENCE, not because it has been validated for this
    /// shape: <c>TriggerEchoGate.IsLikelyGenerativeEcho</c> was tuned for contiguous echoes of a
    /// comma-joined Whisper prompt, and a keyword-field echo may look different. Moving this to
    /// <c>Keyterms</c> later is a one-line change — but it needs a measurement, not an argument.</para>
    /// </summary>
    StructuredKeywords,
}

/// <summary>
/// Abstraction for local vs cloud transcription.
/// </summary>
public interface ITranscriptionService
{
    /// <summary>
    /// How this service transports vocabulary hints — read off the RESOLVED instance
    /// so the echo gate can't drift from the registry's routing (PRM-4). Defaults to
    /// <see cref="HintTransportKind.None"/>; generative/keyterm providers override.
    /// </summary>
    HintTransportKind HintTransport => HintTransportKind.None;

    /// <summary>
    /// The ACTUAL model identity behind a <see cref="HintTransportKind.GenerativePrompt"/>
    /// transport (loaded ggml path for local Whisper, model id for Groq), used to pick
    /// the tokenizer vocabulary for the hint budget (Codex round 5: deriving it from
    /// the settings string could diverge from what's really loaded — e.g. the
    /// missing-model fallback load). Null for non-generative transports.
    /// </summary>
    string? GenerativeModelId => null;

    /// <summary>
    /// Transcribe a WAV file. <paramref name="hints"/> carries the Dictionary
    /// vocabulary as STRUCTURED terms (PRM-3) — each implementation composes its own
    /// provider-correct format (Deepgram keyterm/keywords query params, ElevenLabs
    /// keyterms fields, OpenAI <c>gpt-transcribe</c>'s <c>keywords[]</c>) or
    /// deliberately ignores it. IGNORING IS NOW THE COMMON CASE: Parakeet, the legacy
    /// OpenAI models, and — since TRN-11 — both Whisper paths all accept this parameter
    /// and send nothing. Whether a given service uses it is answered by
    /// <see cref="HintTransport"/>, never by this parameter's presence.
    /// </summary>
    Task<string> TranscribeAsync(string audioFilePath, string? language = null, TranscriptionHints? hints = null, bool diarize = false, CancellationToken ct = default);
    bool IsReady { get; }

    /// <summary>
    /// Validate the API key by making a lightweight request.
    /// Returns true if key is valid, false if invalid (401/403).
    /// Throws HttpRequestException for network errors.
    /// </summary>
    Task<bool> ValidateKeyAsync(CancellationToken ct = default) => Task.FromResult(true);
}

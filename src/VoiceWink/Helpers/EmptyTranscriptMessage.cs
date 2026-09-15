using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// TRN-18 (2026-08-15): what the pill says when a transcription produced nothing usable.
///
/// <para><b>Why the generic copy was not enough.</b> "No usable transcription" is true of every
/// empty outcome, and for the one class that is ENGINE-specific it left the user with no signal
/// that the model was the variable. Measured on the 2026-08-15 incident: a Parakeet collapse at
/// 17:33:49, a Retry at 17:34:30 that returned the identical empty result because it replayed the
/// same model, and recovery only at 17:36 once the owner worked out for themselves to switch
/// engines. The app already knew — the TRN-10b tripwire had fired on both attempts — and said none
/// of it. Same disease as [[TRN-15]] and [[AUD-17]]: the information is held and not surfaced.</para>
///
/// <para><b>Scoped to the engine with the MEASURED limitation, via runtime kind rather than a name
/// match.</b> Parakeet's all-or-nothing empty decode is documented across TRN-10/10b/10c/10d with a
/// three-file corpus; nothing equivalent is measured for whisper.cpp or for any cloud provider, and
/// telling a Whisper user to switch models would be advice with no evidence behind it — and the
/// other engines fail DIFFERENTLY, so the same advice would misdirect: local Whisper's failure mode
/// is the opposite (it invents text rather than returning empty, which is why the VAD gate exists),
/// so an empty result there usually means the recording really was silent; and a cloud empty is
/// typically a provider event (the REL-13 Deepgram 200-with-nothing incident), where the amber Retry
/// already on the pill IS the right action and "switch models" would push the user off it.
/// <b>Scope, stated precisely:</b> this keys on <see cref="LocalRuntimeKind.Parakeet"/>, so a second
/// model on the SAME runtime (a future Parakeet build) inherits the copy with no code change, while
/// a genuinely different engine needs this condition widened — one line, but not automatic. An
/// earlier revision of this paragraph claimed any "future engine opts in by declaring its runtime",
/// which overstated it. The bar for widening is evidence, not symmetry: a reproducible
/// empty-on-real-speech pattern for that engine, not a single outage.</para>
///
/// <para><b>The engine is only named when the ENGINE returned nothing usable.</b> Both callers of
/// the ViewModel's empty-transcript handler land on the same pill, and they are not the same event:
/// one is the engine producing nothing usable, the other is OUR OWN processing pipeline emptying
/// text the engine did produce. That distinction is the reason the shipped copy says "No usable
/// transcription" rather than "…returned" (Codex diff review round 3, 2026-07-25), and naming the
/// engine on the second would be a false accusation printed in front of the user. Hence the
/// explicit flag rather than an inference from emptiness — at the pill, both cases look identical.
/// The flag is <c>…NothingUsable</c>, not <c>…Nothing</c>, and the copy says "no usable text"
/// rather than "no text", because the guard behind it is <c>TranscriptContent.IsEmpty</c> — which
/// fires for a lone comma, a real Parakeet output on near-silent audio and the documented reason
/// that guard exists. An earlier revision of this file claimed "produced no text" was "literally
/// true whatever the reason"; it was false for precisely that input, and both self-review lenses
/// caught it. "Usable" is also the vocabulary <see cref="Generic"/> already uses.</para>
///
/// <para><b>The ADVICE requires POSITIVE evidence of speech — and that is the load-bearing rule
/// here.</b> The cause half is safe to state unconditionally; "try another model" is not, because
/// the user executes it. The VAD gate does NOT always establish that a recording contains speech:
/// it returns <c>Unavailable</c> and defers to a whole-file RMS check whenever the REL-15
/// <c>-p:VadEnabled=false</c> lever is set, the bundled Silero model is missing or fails its hash,
/// or the host CPU lacks the required instruction set — and <c>MainViewModel</c> records at that
/// gate's own call site that RMS "passes 3 s of silence + one click". On such a recording Parakeet
/// returning nothing is the CORRECT answer, and advising a switch would send the user to an engine
/// whose documented failure mode on that exact input is hallucination — the app would then paste
/// fabricated words at their cursor. So the clause is gated on <c>SpeechDetected</c> specifically,
/// never on "not blocked": positive evidence, never the absence of a negative, the same rule
/// <c>ModelLanguageSupport</c> follows for its own recommendation clause.</para>
///
/// <para><b>The advice is also withheld when the model is PINNED by an override.</b> An App Mode
/// config can pin the transcription model per foreground app, and a pinned retry replays that
/// override rather than re-reading Settings — so "try another model" would send the user to the
/// Models page to change a setting the next Retry ignores. Without an override the advice is
/// followable exactly as written, because the retry path re-reads
/// <c>AppDefaults.SelectedModelName</c> live.</para>
///
/// <para><b>Not the tail the owner removed.</b> On 2026-08-01 a "— Retry to transcribe anyway"
/// tail was dropped from this pill for restating the Retry button sitting on it. This clause names
/// the action that button cannot perform, and follows the shipped precedent of
/// "No audio — check microphone".</para>
/// </summary>
internal static class EmptyTranscriptMessage
{
    /// <summary>The shipped copy for every empty outcome this type does not specialize. Deliberately
    /// a constant: it is asserted by name in tests, and the VAD gate's amber "No speech detected"
    /// must never converge with it (different causes, different pill tones).</summary>
    internal const string Generic = "No usable transcription";

    /// <summary>House style for one pill line at the default text scale, mirroring
    /// <c>MainViewModel.DefaultPillMaxCodeUnits</c>. Enforced by test rather than by truncation:
    /// this copy is composed from a catalogue display name, so a longer future name would overflow
    /// silently and <c>TextTrimming.CharacterEllipsis</c> would cut the ACTIONABLE half first.</summary>
    internal const int PillMaxCodeUnits = 55;

    /// <summary>
    /// Pill copy for an empty transcription.
    /// <paramref name="engineReturnedNothingUsable"/> must be false when the text was emptied by the
    /// processing pipeline downstream of the engine. <paramref name="speechConfirmed"/> must be true
    /// only on a positive VAD <c>SpeechDetected</c> verdict — never on "the gate did not block".
    /// <paramref name="modelIsPinnedByOverride"/> suppresses advice a retry would ignore.
    /// Total: any unknown, blank, or cloud <paramref name="modelName"/> yields <see cref="Generic"/>.
    /// </summary>
    internal static string For(
        bool engineReturnedNothingUsable,
        string? modelName,
        bool speechConfirmed,
        bool modelIsPinnedByOverride)
    {
        if (!engineReturnedNothingUsable) return Generic;

        // Either Parakeet bundle spelling resolves to the active row (TRN-29 flip) — an
        // upgrader's un-rewritten selection must keep earning the engine-named copy rather than
        // silently degrading to the generic line this type exists to improve on.
        modelName = ParakeetCatalog.CanonicalName(modelName);
        var row = PredefinedModels.Models
            .FirstOrDefault(m => ModelDiskReconciliation.IsSameModel(m.Name, modelName));
        if (row?.Runtime != LocalRuntimeKind.Parakeet) return Generic;

        // Through ModelDisplayName even though row.DisplayName is in hand: that type is the ONE
        // answer to what a model is called in front of a user (owner decision 2026-08-06, which
        // also forbids rendering a catalogue id). A five-row array scan is not worth a second
        // naming path that could drift from it.
        var cause = $"{ModelDisplayName.Resolve(modelName)} produced no usable text";

        return speechConfirmed && !modelIsPinnedByOverride
            ? $"{cause} — try another model"
            : cause;
    }
}

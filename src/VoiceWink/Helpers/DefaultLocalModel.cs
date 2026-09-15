using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// Which local model a fresh install should start on.
///
/// <para>Parakeet when the machine can actually run it, Whisper Small when it cannot — the fallback
/// is the whole reason this is a function rather than a constant. Parakeet is unusable on four
/// classes of install the app already models: the <c>-p:ParakeetEnabled=false</c> build lever, a CPU
/// below the SSE2 floor, a NATIVE arm64 process, and a native library that will not load. The first
/// three are answered by <c>ParakeetTranscriptionService.IsAvailable</c> BEFORE anything is
/// downloaded, which matters because the alternative is recommending a 670 MB download and then
/// failing to load it — <c>PrepareOutcome.Unavailable</c> only arrives after the bytes are on
/// disk.</para>
///
/// <para><b>The arm64 class is NOT "an ARM64 machine", and reading it that way inverts the
/// answer.</b> <c>ParakeetNativeProbe</c> tests <c>Sse2.IsSupported</c>, never the architecture, and
/// this project ships win-x64 ONLY (<c>&lt;Platforms&gt;x64&lt;/Platforms&gt;</c> +
/// <c>&lt;RuntimeIdentifiers&gt;win-x64&lt;/RuntimeIdentifiers&gt;</c>; a native arm64 build is
/// backlog TCH-2, unbuilt). On Windows-on-ARM the shipped x64 build therefore runs under Prism
/// emulation, which supplies x86-64 semantics including SSE2 — so the probe passes and Parakeet
/// loads. Owner-verified 2026-08-20 on a Snapdragon ARM64 PC, running Parakeet as the only installed
/// local model for live dictation. This class becomes reachable only if TCH-2 ever ships a native
/// arm64 target; until then it is a guard against a build that does not exist.</para>
///
/// <para>The fourth (native present but unloadable) is not knowable in advance and is deliberately
/// left where it already is: <c>ParakeetLocalRuntime</c> catches <c>DllNotFoundException</c> /
/// <c>BadImageFormatException</c> at prepare time.</para>
///
/// <para><b>Owner instruction 2026-08-03, and it overrides a recorded gate.</b> <c>backlog.md</c>
/// TRN-1 holds DEFAULT promotion "gated on WER (TRN-4)", and that gate is now SATISFIED. What was
/// measured first was SPEED — 9.0× real time against the previous default's 0.6× on this project's
/// own 30-clip dictation corpus (<c>docs/model-review/2026-08-02-parakeet-vs-local-whisper-cpu.md</c>),
/// plus silence where four Whisper models hallucinated. WER on this hardware WAS unmeasured when
/// this default was chosen and is not any more: <c>ModelRatings</c> measured Parakeet's accuracy on
/// 2026-08-30 at 15.3% WER over the owner's own 56-clip corpus — the best of any LOCAL model and
/// ahead of four paid cloud rows, which STRENGTHENS this default rather than merely dating it.
/// <b>That measurement is of the GGUF bundle that ships by default; under the
/// <c>PcppEnabled=false</c> kill switch the sherpa bundle ships instead and carries the star by the
/// same-weights anchoring rule, not by its own measurement.</b> Before that date the row was
/// unmeasured here because the circulating figures were measured on hosted endpoints, not
/// sherpa-onnx int8 on a laptop CPU.
///
/// <b>The old closing line here has been REMOVED and must not come back.</b> It read: this ships a
/// default that is far faster and <i>believed</i> comparable on accuracy — "a product call, not a
/// measurement". That was true when written and is false now, in the direction that matters: the
/// default is not merely comparable, it is the most accurate local row measured. The remaining
/// honest caveat is the CORPUS, not the confidence — one speaker, and permanently so, since the
/// owner retired TRN-4's second-speaker sitting on 2026-08-31.</para>
///
/// <para>Pure, because the unavailable branch is the one that cannot be exercised on the machine
/// this is developed on.</para>
/// </summary>
internal static class DefaultLocalModel
{
    /// <summary>
    /// The specific Parakeet build that is the default — by NAME, not by runtime kind.
    ///
    /// <para>An earlier version resolved it as "the row whose <c>Runtime</c> is Parakeet", which is
    /// a lookup by ENGINE and assumes there is exactly one such row: a second Parakeet build would
    /// make <c>Single()</c> throw during first-run onboarding, and a test repeating the same
    /// expression pins nothing about WHICH model was intended (Codex, diff review).</para>
    ///
    /// <para>Since the TRN-29 flip the name is the ACTIVE catalog row's — the GGUF bundle in a
    /// <c>PCPP_ENABLED</c> build, the sherpa bundle in a kill-switch rebuild — via the same
    /// single-source the catalog itself reads, so a fresh install always defaults to a row this
    /// build's registry can serve.</para>
    /// </summary>
    internal static string ParakeetName => ParakeetCatalog.ActiveRow.Name;

    /// <summary>
    /// The model a fresh install should start on, given what this machine can run AND what the user
    /// said they will speak.
    ///
    /// <para><b>Language is part of the question, not a detail.</b> Parakeet covers 25 European
    /// languages; the picker offers 99. Answering on machine capability alone made a fresh install
    /// that pinned Japanese download 670 MB of a model that cannot transcribe Japanese — where the
    /// previous Small default simply worked. That is a regression for roughly three quarters of the
    /// languages on offer, and both diff reviewers found it independently.</para>
    ///
    /// <para><c>ModelLanguageSupport.CannotRecognise</c> is the same sourced check the Models page
    /// and the onboarding note already use, so the recommendation, the warning and the transcription
    /// cannot disagree about what Parakeet covers.</para>
    ///
    /// <para><c>auto</c> resolves to Parakeet: auto-detect is what Parakeet does natively, and no
    /// claim has been made about a language the user did not name.</para>
    /// </summary>
    internal static string Resolve(bool parakeetAvailable, string? selectedLanguage)
    {
        if (!parakeetAvailable) return AppDefaults.DefaultWhisperModel;

        var parakeet = PredefinedModels.Models.FirstOrDefault(m => m.Name == ParakeetName);
        if (parakeet is null) return AppDefaults.DefaultWhisperModel;

        return ModelLanguageSupport.CannotRecognise(parakeet, selectedLanguage)
            ? AppDefaults.DefaultWhisperModel
            : ParakeetName;
    }
}

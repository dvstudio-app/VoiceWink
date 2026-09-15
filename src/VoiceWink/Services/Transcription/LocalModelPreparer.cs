namespace VoiceWink.Services.Transcription;

/// <summary>
/// A resolved local model: which runtime owns it, which service transcribes with it, and how to
/// name it safely.
///
/// <para>Carrying the <see cref="Service"/> alongside the runtime is the point. Preparation and
/// routing previously derived their answers independently — recording start prepared one model
/// while <c>GetService</c> re-read settings for another — and with one engine that divergence only
/// loaded a different <c>.bin</c>. With two engines it becomes "prepared Parakeet, transcribed with
/// whisper.cpp". One resolution, one answer.</para>
/// </summary>
public sealed record LocalModelBinding(
    string CanonicalName,
    string DisplayName,
    ILocalTranscriptionRuntime Runtime,
    ITranscriptionService Service);

/// <summary>
/// Chooses the local runtime for a model name and prepares it.
///
/// <para>The single place that answers "which engine serves this name". Both
/// <see cref="TranscriptionServiceRegistry"/> and the ViewModels consult it rather than keeping
/// their own view — two answers to that question is how the app ends up loading one engine and
/// transcribing with another.</para>
/// </summary>
public sealed class LocalModelPreparer
{
    private readonly IReadOnlyList<ILocalTranscriptionRuntime> _runtimes;

    public LocalModelPreparer(IEnumerable<ILocalTranscriptionRuntime> runtimes)
        => _runtimes = runtimes.ToArray();

    /// <summary>
    /// Resolve a model name to its runtime, or null when no runtime claims it.
    ///
    /// <para>Two runtimes claiming one name THROWS rather than picking the first. That is a
    /// composition defect — two catalogs overlapping — and silently choosing one is exactly the
    /// wrong-engine failure this type exists to prevent. Better a loud startup-time error than a
    /// transcription that quietly used the other stack.</para>
    /// </summary>
    public LocalModelBinding? TryResolve(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return null;

        ILocalTranscriptionRuntime? found = null;
        foreach (var runtime in _runtimes)
        {
            if (!runtime.CanServe(modelName))
                continue;
            if (found is not null)
            {
                throw new InvalidOperationException(
                    $"Local model '{modelName}' is claimed by more than one runtime " +
                    $"({found.GetType().Name}, {runtime.GetType().Name}). Model catalogs must not overlap.");
            }
            found = runtime;
        }

        if (found is null)
            return null;

        // Canonical/display come from the catalog, so a hand-edited "GGML-SMALL" resolves to the
        // real spelling and every downstream surface shows app-authored text.
        var canonical = found.Canonicalize(modelName) ?? modelName;
        var display = found.DisplayName(modelName) ?? canonical;
        return new LocalModelBinding(canonical, display, found, found.Service);
    }

    /// <summary>
    /// Prepare a local model. <see cref="PrepareOutcome.UnknownModel"/> when no runtime claims the
    /// name — never a substitution with some other model that happens to be available.
    /// </summary>
    public async Task<PrepareOutcome> PrepareAsync(string? modelName, string? language, CancellationToken ct)
    {
        var binding = TryResolve(modelName);
        if (binding is null)
            return PrepareOutcome.UnknownModel;

        return await binding.Runtime
            .PrepareAsync(binding.CanonicalName, language, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Thrown when a model name is neither a cloud catalog model nor claimed by any local runtime.
///
/// <para>A fail-closed safety net, not the primary path: callers are expected to
/// <see cref="LocalModelPreparer.PrepareAsync"/> first and handle
/// <see cref="PrepareOutcome.UnknownModel"/> with their own copy. This exists so that a caller which
/// forgets cannot silently fall back to Whisper — which is what the registry did before, and what
/// would have quietly transcribed a Parakeet selection with whisper.cpp.</para>
/// </summary>
public sealed class UnknownTranscriptionModelException : InvalidOperationException
{
    /// <remarks>
    /// The MESSAGE carries a sanitized, length-bounded rendering; the raw value stays on
    /// <see cref="ModelName"/> for a caller that genuinely needs it. The name originates in
    /// settings, which import validates only as a string — so it can hold control characters,
    /// bidi overrides or unbounded text, and an exception message ends up in logs, support
    /// bundles and (via a generic handler) potentially on screen.
    /// </remarks>
    public UnknownTranscriptionModelException(string modelName)
        : base($"No transcription runtime serves model '{Helpers.UntrustedTextSanitizer.Sanitize(modelName, 40) ?? "(unnamed)"}'.")
        => ModelName = modelName;

    /// <summary>The raw name as stored. Sanitize before displaying.</summary>
    public string ModelName { get; }
}

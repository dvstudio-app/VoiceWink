using VoiceWink.Models;

namespace VoiceWink.Helpers;

/// <summary>
/// REL-17 diff review (High): "app-controlled" model provenance means CATALOG MEMBERSHIP,
/// not "came from settings" — local model names are arbitrary user filenames (any
/// <c>Models\*.bin</c> is DISCOVERABLE by the disk scan, so <c>patient-jane-doe.bin</c>
/// would have surfaced verbatim in the redacted sidecar), and a hand-edited settings.json
/// can carry any string into a cloud client. *(Said "discoverable and selectable" until
/// 2026-08-03: the pickers filter through <c>InstalledCatalogModels</c> now, so an unknown
/// file is not selectable. Discoverability plus a hand-edited settings key is what this rule
/// actually rests on, and both still hold.)* ONE membership rule, consulted centrally by
/// <see cref="PromptTraceLog"/> (call sites no longer assert provenance at all — the
/// assertion was exactly what went wrong): a model id renders verbatim iff it names a
/// predefined Whisper model or a cloud transcription catalog model; everything else —
/// downloaded/custom/enhancement ids — renders as a hash.
/// </summary>
public static class KnownModelIds
{
    private static readonly HashSet<string> Known = new(
        PredefinedModels.Models.Select(m => m.Name)
            .Concat(CloudModels.Models.Select(m => m.Name)),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string? modelId)
        => !string.IsNullOrEmpty(modelId) && Known.Contains(modelId);
}

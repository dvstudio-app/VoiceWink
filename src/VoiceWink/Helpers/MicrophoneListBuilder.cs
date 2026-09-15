using VoiceWink.Services.Audio;

namespace VoiceWink.Helpers;

/// <summary>One entry of the Settings "Microphone" dropdown (AUD-1). <see cref="Id"/> is the
/// WASAPI endpoint ID (null = the "System default" entry) — typed identity, because friendly
/// names can collide and a string-only combo would then be ambiguous. <see cref="Display"/> is
/// what the combo shows (may carry a disambiguating suffix or the "(unavailable)" annotation);
/// <see cref="PersistName"/> is the CLEAN device name to persist as display metadata — never
/// the decorated Display text.</summary>
public sealed record MicComboItem(
    string? Id,
    string Display,
    string? PersistName,
    CaptureEndpointKind Kind = CaptureEndpointKind.Unknown)
{
    public override string ToString() => Display;
}

/// <summary>
/// AUD-1: pure builder for the Microphone dropdown — device snapshot + persisted selection in,
/// combo items + selected index out. No COM, no settings access; fully unit-testable.
///
/// Rules: "System default" is always first and is the default selection (persisted id null/absent
/// or matching nothing AND no pin). Duplicate friendly names get a numeric " (2)"/" (3)" suffix in
/// Display (typed identity still carries the exact endpoint id). A pinned-but-missing device gets
/// a synthetic trailing entry "&lt;name&gt; (unavailable)" — selected, so the user SEES their pin
/// is still in force and falling back per-recording rather than silently reverting.
/// </summary>
public static class MicrophoneListBuilder
{
    public const string SystemDefaultDisplay = "System default";

    public static (IReadOnlyList<MicComboItem> Items, int SelectedIndex) Build(
        IReadOnlyList<AudioDeviceInfo> devices,
        string? persistedId,
        string? persistedName)
    {
        var items = new List<MicComboItem> { new(null, SystemDefaultDisplay, null) };

        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            var count = nameCounts.TryGetValue(d.Name, out var c) ? c + 1 : 1;
            nameCounts[d.Name] = count;
            var display = count == 1 ? d.Name : $"{d.Name} ({count})";
            items.Add(new MicComboItem(d.Id, display, d.Name, d.Kind));
        }

        var hasPin = !string.IsNullOrWhiteSpace(persistedId);
        var selectedIndex = 0;
        if (hasPin)
        {
            var match = items.FindIndex(i => i.Id != null && string.Equals(i.Id, persistedId, StringComparison.Ordinal));
            if (match >= 0)
            {
                selectedIndex = match;
            }
            else
            {
                // Pinned device absent — synthetic entry keeps the pin visible + re-selectable.
                var label = string.IsNullOrWhiteSpace(persistedName) ? "Selected microphone" : persistedName;
                items.Add(new MicComboItem(persistedId, $"{label} (unavailable)", persistedName));
                selectedIndex = items.Count - 1;
            }
        }

        return (items, selectedIndex);
    }
}

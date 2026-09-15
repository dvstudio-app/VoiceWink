using Serilog.Core;
using Serilog.Events;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>
/// The identifier gate on every <c>{Model}</c> log property, on the ROOT logger (2026-09-13).
///
/// <para><b>Why.</b> The AI Enhancement page's model box is an EDITABLE combo, so the stored
/// "model id" can be any text a user typed — and 41 <c>Logger.*("… {Model} …", model)</c> sites
/// across the enhancement service, the provider clients, the transcription clients and the view
/// models rendered it verbatim into the local log: hence into the support bundle and the GDPR
/// export (whose line scrubs know key shapes and named tokens, not prose) and, at Information+
/// with crash reporting on, into Sentry breadcrumbs. PR #917 gated ONE line — the startup banner's
/// configuration facts (<c>DiagnosticSnapshot</c>); this enricher is that gate at the one place
/// that covers every present and future site, instead of 41 call-site edits the next site would
/// forget.</para>
///
/// <para><b>What.</b> A log event carrying a property named <see cref="PropertyName"/> whose value
/// is a string has it replaced by <see cref="LogValueSanitizer.IdentifierOrShape"/> — the value
/// when it is an identifier (printable ASCII, no whitespace, ≤ 64 chars: every catalog id, the
/// app's <c>(default)</c> / <c>default</c> placeholders), otherwise <c>(not an id, N chars)</c>.
/// Registered with <c>.Enrich.With</c> on the root configuration in
/// <c>App.BuildLoggerConfiguration</c>, ahead of every sink, so the file sink, the Debug sink and
/// the Sentry sub-logger all see the gated property — and because Serilog renders the message
/// from the properties, the rendered LINE changes too, in every sink. Non-string values, absent
/// properties and every other property name are untouched.</para>
///
/// <para><b>Keyed by NAME, deliberately.</b> <c>Model</c> is the app's own convention for "this
/// is a model id" — the same mechanism as <see cref="LogRedactionEnricher"/>'s name allowlist. A
/// site that logs a model id under another name is outside the gate, and a site that logs a
/// catalog DISPLAY name ("Whisper Small": a space — app-authored, never user-typed) must use
/// <c>{ModelDisplayName}</c> instead, as the three <c>ModelDownloadManager</c>
/// SHA-256 lines do; <c>ModelIdEnricherTests</c> pins that no <c>{Model}</c> site logs a
/// <c>.DisplayName</c>. The one channel a name-keyed gate cannot see is a model baked into a
/// STRING before the event exists: the image clients' request descriptions
/// (<c>$"model={model}, …"</c>, logged as <c>{Detail}</c> on a timeout or an oversized response)
/// gate the value at the interpolation with <see cref="LogValueSanitizer.IdentifierOrShape"/> —
/// both diff-round seats found that bypass; the same tests pin every such site.</para>
///
/// <para><b>This is NOT an exception to "redaction is Sentry-only".</b> It gates the SHAPE of one
/// user-typed field; it never touches provider content, and the local file keeps full bodies.
/// Residuals, accepted — three: a value with no whitespace but personal content passes (the
/// hazard is typed or dictated prose); the opt-in RAW prompt trace still carries the model
/// verbatim as the entry's <c>kind</c> (the user opted into the full prompt text there); and a
/// provider that rejects the bogus model quotes it in its error body, which the local file keeps
/// under <c>{Body}</c> by the local-fidelity rule (Sentry name-redacts that property, the bundle
/// and the export do not). So the claim is about VoiceWink's OWN log lines, and the changelog
/// says so.</para>
/// </summary>
public sealed class ModelIdEnricher : ILogEventEnricher
{
    /// <summary>The property name the gate keys on — the app's convention for a model id.</summary>
    internal const string PropertyName = "Model";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (!logEvent.Properties.TryGetValue(PropertyName, out var value)) return;
        if (value is not ScalarValue { Value: string text }) return;

        var gated = LogValueSanitizer.IdentifierOrShape(text);
        if (ReferenceEquals(gated, text)) return;

        logEvent.AddOrUpdateProperty(new LogEventProperty(PropertyName, new ScalarValue(gated)));
    }
}

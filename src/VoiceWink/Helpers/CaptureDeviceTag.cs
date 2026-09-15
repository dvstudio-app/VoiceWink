using System.Security.Cryptography;
using System.Text;

namespace VoiceWink.Helpers;

/// <summary>
/// AUD-16 (2026-08-14): the short, stable, log-safe stand-in for a capture endpoint's identity.
///
/// <para><b>The gap it closes.</b> Investigating two field transcription failures, the logs could not
/// answer <i>"which microphone made this recording?"</i> — nor even the weaker and more useful
/// <i>"did these two recordings use the SAME microphone?"</i>. The owner had switched mics mid-day; a
/// switch could be inferred from level shifts but never confirmed, and the level timeline did not
/// line up with it, so the inference was worthless. On a system-default setup — the common case —
/// nothing distinguished recording 1 from recording 100.</para>
///
/// <para><b>Why not just log the device name.</b> Three independent reasons, all pre-existing and all
/// still binding. (1) <b>AUD-11 cost a recording over exactly this</b>: <c>MMDevice.FriendlyName</c>
/// opens the endpoint property store — a synchronous COM call — and the line ran while the recorder's
/// lock was held; on a Bluetooth endpoint being grabbed by another device it stalled 4.6 s then 2.3 s,
/// serialising two attempts into the race that stopped a successor's capture 5 ms after it started.
/// (2) It cannot be bounded for redaction: arbitrary driver text that may contain quotes, so no
/// delimited scrub pattern is safe. (3) Users rename endpoints ("Dieter's AirPods"), and
/// <c>Information</c> is Sentry's breadcrumb threshold — these lines ride into support bundles and
/// the GDPR export.</para>
///
/// <para><b>What this is, stated precisely.</b> A <b>persistent pseudonymous identifier for a Windows
/// endpoint INSTALLATION</b> — not an anonymous value, and not an immutable physical-device identity.
/// The endpoint id is stable across ordinary restarts and unplug/replug, which is what makes
/// "recording 1 vs recording 100" answerable; it is NOT guaranteed across a driver upgrade or a
/// device uninstall/reinstall, so a tag changing after one of those is expected rather than a bug
/// (Codex diff review — the first draft claimed "the same microphone forever", which overstates what
/// Microsoft documents). What it is NOT is reversible: the tag cannot be turned back into a device
/// name, and it is not user-authored text. Longitudinal same-device correlation WITHIN one machine's
/// own diagnostics is the whole feature.</para>
///
/// <para><b>It does NOT go to Sentry.</b> The crash-reporting opt-in is presented to the user as
/// "anonymous crash reports", and a stable per-device pseudonym would let multiple reports be
/// correlated to one machine — pseudonymous is not anonymous (GDPR Recital 26 draws exactly that
/// line). <c>devicetag</c> is therefore on <c>LogRedactionEnricher</c>'s property-NAME allowlist,
/// which covers the Sentry sub-logger only; the local file, the REL-3 support zip and the GDPR export
/// keep it, because those are where the diagnostic value lives.</para>
///
/// <para><b>Redaction:</b> 8 lowercase hex characters match no value pattern in
/// <c>LogRedactionEnricher</c>, so no enricher change is needed — pinned by a test that renders a
/// tag-bearing line through <c>RedactString</c> and asserts it survives. One placement rule follows
/// from that file rather than from taste: several tokens there scrub to END OF LINE
/// (<c>micName=</c>, <c>capProcs=</c>, <c>userTerm=</c>, <c>appMode=</c>), so a tag must never be
/// appended after one of those on a shared line — it would be eaten. None of the current sites do.</para>
///
/// <para>Pure, total and allocation-light; no COM, no I/O, no clock. Pinned by
/// <c>CaptureDeviceTagTests</c>.</para>
/// </summary>
internal static class CaptureDeviceTag
{
    /// <summary>What a tag reads when the endpoint id could not be obtained — a device with no id, a
    /// null device, or a throwing read. Deliberately a WORD rather than an empty string: a blank
    /// token in a log line is indistinguishable from a formatting bug, and this one is greppable.</summary>
    internal const string Unknown = "unknown";

    /// <summary>Hex characters kept. 32 bits is ample to separate the handful of endpoints one machine
    /// ever sees, and a collision is a diagnostic annoyance rather than a defect — two devices sharing
    /// a tag would merely blur one comparison, never change behaviour. Readability in a log line wins
    /// over collision headroom nobody needs.</summary>
    internal const int HexLength = 8;

    /// <summary>
    /// The tag for an endpoint id. Null, empty or whitespace ⇒ <see cref="Unknown"/>.
    /// </summary>
    /// <remarks>
    /// SHA-256 is used for a stable, well-distributed truncation — NOT as a security control. The
    /// endpoint id is not a secret; the hash exists so the log carries something bounded and
    /// non-reversible-to-a-name instead of a device path.
    /// </remarks>
    internal static string For(string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return Unknown;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(endpointId));

        var sb = new StringBuilder(HexLength);
        for (var i = 0; i < HexLength / 2; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Support;

/// <summary>Terminal state of one report-send step.</summary>
public enum ReportSendOutcome
{
    /// <summary>The MAPI compose window was shown (attachment already handed over);
    /// the bundle file was deleted best-effort.</summary>
    SentViaMapi,
    /// <summary>REL-29 (tester report, 2026-09-02): the mail client was ASKED and did not take the
    /// message (any MAPI code but success/user-abort, or an exception). NOTHING else was launched:
    /// the bundle (if any) is KEPT on disk and the CALLER decides what happens next —
    /// <see cref="ReportSendFlow.RetryMapi"/> or <see cref="ReportSendFlow.FallbackToMailto"/>.
    /// Before REL-29 this state did not exist: a decline went straight to the mailto fallback,
    /// which on a corporate Outlook whose add-in vetoed three compose windows in a row
    /// (`MAPI_E_FAILURE`, 9.5 s / 0.7 s / 18 s) meant three extra compose windows and three
    /// Explorer windows before the fourth attempt went through. The failure was transient; the
    /// flow treated it as final. <b>Never returned on a host where Simple MAPI is refused
    /// outright</b> (the REL-27 emulated-host predicate): there a retry can never succeed, so
    /// <see cref="ReportSendFlow.Run"/> takes the fallback itself and returns
    /// <see cref="MailtoFallback"/>, exactly as before REL-29.</summary>
    MapiDeclined,
    /// <summary>The mailto path ran: a <c>mailto:</c> compose was launched and the bundle was KEPT
    /// (revealed in Explorer) for the user to attach manually — either because the user chose it
    /// after a decline, or because this host refuses Simple MAPI and no retry could help. The
    /// 7-day Reports sweep is its lifecycle bound. <b>"No MAPI client" was the old wording
    /// here and it was too narrow</b> (REL-25): it is one cause among many, and naming it led to
    /// UI copy that asserted a cause the log did not support.</summary>
    MailtoFallback,
    /// <summary>The bundle could not be written — or, on a retry, is no longer on disk —
    /// fail-closed, no email launched.</summary>
    BundleFailed,
}

/// <summary>
/// What one step of the send returned: the outcome plus the bundle path the caller must carry into
/// the next step. <see cref="BundlePath"/> is null when no attachment was requested, when the
/// bundle was never built, when it was found missing, and after a MAPI hand-off (the file is
/// deleted then) — so a non-null path always means "a kept bundle exists at this path and
/// something still has to happen to it".
/// </summary>
public readonly record struct ReportSendResult(ReportSendOutcome Outcome, string? BundlePath);

/// <summary>
/// The report-send lifecycle, extracted from <c>ReportProblemDialog</c> (REL-17, Codex plan
/// round 4: MAPI-delete vs mailto-keep must be automated-test coverage, not manual UAT).
/// Owns: staged bundle build in the app's Reports dir, the MAPI attempt, the
/// <c>mailto:</c> fallback whose BODY carries only the bundle's basename (an absolute
/// path embeds the Windows username — pre-REL-17 leak, fixed here), Explorer reveal, and
/// the delete-on-MAPI-success rule (Simple MAPI clients copy the attachment at compose
/// time, so the source file is disposable once <c>TrySend</c> returns). Every side effect
/// rides an injectable seam (the SettingsService <c>_readFile</c> pattern) so
/// <c>ReportSendFlowTests</c> pins the matrix without a mail client or real IO.
///
/// <para><b>Since REL-29 the flow is three steps, not one.</b> <see cref="Run"/> builds and makes
/// the first MAPI attempt and STOPS at the verdict; a decline hands the kept bundle back to the
/// caller, who asks the user and then calls <see cref="RetryMapi"/> (the MAPI leg again, same
/// zip — never a rebuild, so the file name and contents stay what the log describes) or
/// <see cref="FallbackToMailto"/> (the old automatic fallback, now a choice). No automatic retry
/// anywhere: the one decline this was written for was an Outlook add-in vetoing the compose with a
/// modal dialog, and a retry the user did not ask for would re-trigger that dialog.</para>
///
/// <para><b>One decline is NOT offered as a choice: a host where Simple MAPI is refused before
/// the mail client is ever asked</b> (REL-27's <see cref="MapiHostSupport"/> predicate — an
/// emulated process, deterministic for the life of the install). A "Try again" there would fail
/// on every press forever, and the copy would blame a mail client that was never consulted — the
/// exact defect REL-27 removed. So <see cref="Run"/> checks the same predicate after a decline
/// and, when it is the host refusing, takes the fallback itself: those machines keep the
/// pre-REL-29 behaviour byte for byte (self-review, both lenses).</para>
/// </summary>
public sealed class ReportSendFlow
{
    private static ILogger Logger => Log.ForContext<ReportSendFlow>();

    // Injectable seams — production defaults; tests substitute.
    internal Func<string, string, string, string?, bool> SendViaMapi { get; set; } = MapiMailService.TrySend;
    internal Action<string> LaunchMailto { get; set; } =
        uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    internal Action<string> RevealInExplorer { get; set; } = path => ShellFolder.Reveal(path);
    // REL-25 item 3 (owner UAT 2026-08-17, §32.5 Fail: "two zip files were still in the reports
    // folder"). The delete stays best-effort -- a MAPI client can still hold the file open when the
    // compose window closes, and throwing there would turn a delivered report into an error -- but
    // it no longer fails SILENTLY. Without this line a lingering bundle was indistinguishable from
    // the mailto path's deliberate keep, which is precisely the ambiguity the card asks to resolve
    // before assuming an independent leak. The 7-day Reports sweep remains the lifecycle bound.
    internal Action<string> DeleteBundle { get; set; } =
        path =>
        {
            try { File.Delete(path); }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Report bundle could not be deleted after hand-off; the 7-day "
                    + "Reports sweep will remove it");
            }
        };
    internal Func<string, string, SupportBundleOptions, bool> BuildBundle { get; set; } =
        (logsDir, zipPath, options) => SupportBundle.TryWriteReportZip(logsDir, zipPath, options);
    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;
    // REL-29: the SAME predicate MapiMailService.TrySend refuses on (REL-27), read here so the
    // flow can tell "the mail client declined" from "this process never asks a mail client". The
    // former earns a retry offer; the latter never can. Read AFTER the attempt rather than before
    // it so the REL-27 refusal keeps logging its own Warning with both architectures — the line a
    // support bundle needs to tell a deliberate refusal from a mail-client failure.
    internal Func<bool> MapiUsableOnThisHost { get; set; } =
        () => MapiHostSupport.IsSafeToLoadInProcess(
            RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);

    /// <summary>
    /// Run the first attempt: build the bundle (fail-closed), then hand it to MAPI. Ends at the
    /// MAPI verdict — a decline returns <see cref="ReportSendOutcome.MapiDeclined"/> with the
    /// bundle KEPT and launches nothing, UNLESS this host refuses Simple MAPI outright, in which
    /// case the mailto fallback runs here (no retry can help) and the outcome is
    /// <see cref="ReportSendOutcome.MailtoFallback"/>. <paramref name="bundleOptions"/> null = no
    /// attachment at all (the user unchecked everything). BLOCKS on the MAPI compose window —
    /// call off the UI thread.
    /// <para><paramref name="footer"/> (PR #938, Codex diff r1 Blocker) is text that must END the
    /// mail on every transport — the version footer. It travels as its own argument rather than
    /// inside <paramref name="body"/> because the mailto leg truncates the body's TAIL to its length
    /// budget: appended to the body, the one line that names the build on a no-logs report was
    /// exactly what a long description cut off. MAPI has no budget, so there it is simply appended;
    /// the mailto leg reserves it (<see cref="SupportBundle.BuildMailto(string, string, string, string?)"/>).</para>
    /// </summary>
    public ReportSendResult Run(
        string recipient,
        string subject,
        string body,
        SupportBundleOptions? bundleOptions,
        string logsDir,
        string reportsDir,
        string? footer = null)
    {
        footer ??= string.Empty;
        // REL-25 item 2. An ENTRY line is what makes the three states of "Open Email did nothing"
        // distinguishable at all: the click never reached here; MAPISendMail is blocked on a modal
        // compose window opened behind other windows (the card's leading hypothesis — it returns
        // nothing and logs nothing while it blocks); or MAPI succeeded and the user missed the
        // window. Without an entry line and a success line, "which path ran" is knowable only by
        // the ABSENCE of a later line, which is precisely the ambiguity item 2 suffers from.
        Logger.Information("Report send starting: attachment={AttachmentRequested}", bundleOptions != null);

        string? zipPath = null;
        if (bundleOptions != null)
        {
            Directory.CreateDirectory(reportsDir);
            // Write-side junction guard (diff round 5): a junctioned Reports dir would
            // land the raw bundle OUTSIDE the app root, where the retention sweep and
            // erasure deliberately refuse to follow. Report creation fails closed.
            if (VerifiedFileAccess.IsReparsePointOrUnreadable(reportsDir))
            {
                Logger.Warning("Report bundle refused: Reports directory could not be verified");
                return new ReportSendResult(ReportSendOutcome.BundleFailed, null);
            }
            zipPath = Path.Combine(reportsDir, $"voicewink-report-{Now():yyyyMMdd-HHmmss}.zip");
            // Fail-closed: don't open an email referencing a bundle we couldn't create.
            if (!BuildBundle(logsDir, zipPath, bundleOptions))
                return new ReportSendResult(ReportSendOutcome.BundleFailed, null);
        }

        var result = AttemptMapi(recipient, subject, body + footer, zipPath);
        if (result.Outcome == ReportSendOutcome.MapiDeclined && !MapiUsableOnThisHost())
        {
            // Not a decline the user can do anything about: the process never asks a mail client
            // here (REL-27), so a retry offer would be a promise that cannot be kept. Take the
            // fallback directly — the pre-REL-29 behaviour on exactly these machines.
            Logger.Information("Report send: Simple MAPI is refused on this host, so no retry is offered - taking the mailto fallback directly");
            return FallbackToMailto(recipient, subject, body, result.BundlePath, footer);
        }
        return result;
    }

    /// <summary>
    /// REL-29 "Try again": the MAPI leg ONLY, on the bundle a previous step kept. Never rebuilds —
    /// the zip on disk is the one the log lines describe, and a second build would double the
    /// bundles the 7-day sweep has to clear (the §32.5 multiple-zips symptom). A kept bundle that
    /// is no longer on disk is <see cref="ReportSendOutcome.BundleFailed"/>: without this check
    /// <c>MapiMailService</c> would quietly compose WITHOUT the attachment (it skips a missing
    /// file rather than failing), and the user would send a report believing the logs rode along.
    /// BLOCKS on the MAPI compose window — call off the UI thread.
    /// </summary>
    public ReportSendResult RetryMapi(
        string recipient, string subject, string body, string? bundlePath, string? footer = null)
    {
        Logger.Information("Report send retry starting: attachment={AttachmentRequested}", bundlePath != null);
        // The vanished-bundle refusal lives in AttemptMapi's pin (Codex diff r1 Blocker): a
        // separate File.Exists here would re-open the check-then-use window it exists to close.
        return AttemptMapi(recipient, subject, body + (footer ?? string.Empty), bundlePath);
    }

    /// <summary>
    /// REL-29 "Attach it myself": the mailto fallback that used to run automatically on every
    /// decline. A plain mailto can't attach, so the body points the user at the kept file BY NAME
    /// ONLY and the file is revealed in Explorer. Never the absolute path — it embeds the Windows
    /// username in the outgoing mail body. The bundle stays on disk (the 7-day sweep is its bound).
    /// A kept bundle that has VANISHED since the decline (AV quarantine, a hand delete, the sweep
    /// under a dialog left open for days) is treated as "no attachment": the note that tells the
    /// user to attach a named file and the Explorer reveal of a path that no longer exists are
    /// both wrong-output, so the email opens with the message alone and the returned
    /// <see cref="ReportSendResult.BundlePath"/> is null so the caller's copy can say so.
    /// </summary>
    public ReportSendResult FallbackToMailto(
        string recipient, string subject, string body, string? bundlePath, string? footer = null)
    {
        // REL-25 items 1+3: which of the paths ran is the single most useful fact when a user
        // reports "it asked me to attach it by hand" -- MapiMailService has already logged WHY at
        // Warning, and this records the consequence next to it.
        Logger.Warning("Report send fell back to mailto; the bundle is KEPT for manual attach");

        // Codex diff r1 Blocker: an EXISTS check followed by the use is a TOCTOU window — an AV
        // quarantine or a hand delete between the two would name a file in the mailto body and
        // reveal a path that is no longer there. Pinning a handle turns the check into a
        // guarantee for the whole hand-off (see PinBundle for the sharing mode); a pin that cannot
        // be taken is the vanished-bundle case.
        using var pin = PinBundle(bundlePath);
        if (bundlePath != null && pin is null)
        {
            Logger.Warning("Report send fallback: the kept bundle is no longer on disk; the email opens without the attach note");
            bundlePath = null;
        }

        var fallbackBody = bundlePath != null
            ? $"[Please attach the report file \"{Path.GetFileName(bundlePath)}\" — VoiceWink just opened its folder in Explorer.]\n\n{body}"
            : body;
        // The footer is the RESERVED tail (see Run): it lands after the attach note, the
        // description and any overflow note, whatever the description's length.
        LaunchMailto(SupportBundle.BuildMailto(recipient, subject, fallbackBody, footer));
        if (bundlePath != null)
            RevealInExplorer(bundlePath);
        return new ReportSendResult(ReportSendOutcome.MailtoFallback, bundlePath);
    }

    /// <summary>
    /// Hold the kept bundle open for the duration of a hand-off so nothing can delete or rename
    /// it between our check and the moment the mail client (or Explorer) reads it. Null when
    /// there is no bundle OR it cannot be opened — the caller treats the latter as "vanished".
    /// <b><see cref="FileShare.ReadWrite"/>, not <see cref="FileShare.Read"/>, on purpose:</b>
    /// the pin exists to refuse DELETE access only. A mail client that opens the attachment for
    /// read+write (some copy through a writable handle) would hit a sharing violation under a
    /// read-only share and report <c>MAPI_E_ATTACHMENT_OPEN_FAILURE</c> — turning a guard against
    /// a millisecond race into a hard failure of the whole feature on that client. Without
    /// <see cref="FileShare.Delete"/> in the share, every delete and rename is refused for as long
    /// as the handle is open, which is exactly the guarantee wanted.
    /// </summary>
    private static FileStream? PinBundle(string? bundlePath)
    {
        if (bundlePath == null) return null;
        try
        {
            return new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The one MAPI leg both <see cref="Run"/> and <see cref="RetryMapi"/> share: hand
    /// the default mail client a compose window with the bundle already attached (Simple MAPI).
    /// Blocks until the compose window closes. Success deletes the bundle (the client copied it
    /// at compose time); a decline keeps it and reports back — nothing else is launched here.</summary>
    private ReportSendResult AttemptMapi(string recipient, string subject, string body, string? zipPath)
    {
        // Codex diff r1 Blocker: MapiMailService skips a MISSING attachment rather than failing,
        // so a bundle that vanishes between a check and the MAPISendMail call would be sent
        // WITHOUT its logs while the dialog closes as success. The pin holds the file through the
        // whole (possibly minutes-long) compose so nothing can delete it underneath the client,
        // and a pin that cannot be taken is the fail-closed refusal — the only File.Exists this
        // flow has. Released BEFORE the success-path delete, which needs the file unpinned.
        bool handedToMapi;
        using (var pin = PinBundle(zipPath))
        {
            if (zipPath != null && pin is null)
            {
                Logger.Warning("Report send refused: the kept bundle is no longer on disk");
                return new ReportSendResult(ReportSendOutcome.BundleFailed, null);
            }
            handedToMapi = SendViaMapi(recipient, subject, body, zipPath);
        }

        if (handedToMapi)
        {
            Logger.Information("Report handed to MAPI; the compose window was shown");
            if (zipPath != null)
                DeleteBundle(zipPath);
            return new ReportSendResult(ReportSendOutcome.SentViaMapi, null);
        }

        // MapiMailService logged WHY at Warning (the named code, or the REL-27 refusal); this is
        // the consequence beside it. The bundle is deliberately NOT touched: the user may retry.
        Logger.Warning("Report send declined by the mail client; the bundle is KEPT and the user is offered a retry");
        return new ReportSendResult(ReportSendOutcome.MapiDeclined, zipPath);
    }
}

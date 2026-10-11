using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Updates;

/// <summary>Decides and sends the one-time first-check install report (Privacy Policy §4.5).</summary>
public interface IInstallSourceReporter
{
    /// <summary>
    /// Called by the update scheduler on every automatic tick. Records this installation once when the
    /// build is the Microsoft Store's own channel, and — when a report is pending and
    /// <paramref name="networkProven"/> — makes one bounded attempt at it. Never throws.
    /// </summary>
    Task RunAsync(bool networkProven, CancellationToken ct);
}

/// <summary>
/// The once-per-installation first-check report (Privacy Policy §4.5; addenda A36, A39).
///
/// <para><b>Decided by the build, not by the machine (store funnel Part 2).</b> A copy built for the
/// Microsoft Store's own update channel (<c>UpdateFeedConfig.IsStoreChannel</c>) records
/// <c>msstore</c>/<c>pending</c> for its installation and sends the report; nothing on the device is read
/// to decide it. Every other build decides nothing new: it only completes a report that an EARLIER
/// version of the same installation had decided to send (the A36 event-log copies, 1.100.395+) — the
/// saved record for the current install stamp wins, whoever wrote it. The report goes to the build's own
/// channel, so a Store-channel copy reports on its channel and a legacy record finishes on
/// <c>win-x64-stable</c>.</para>
///
/// <para><b>Gates it rides.</b> It is called only from the update scheduler's automatic tick, so the
/// <c>UPDATE_CHECK_ENABLED</c> build flag, the automatic-check toggle and the LGL-1 legal gate all apply
/// before this type runs at all; the manual "Check for updates" button never reaches it. On top of
/// those: only a PUBLIC, configured feed (a tester build must not put its token on a new code path).</para>
///
/// <para><b>Once, keyed on the installation.</b> The record is saved against the Velopack install
/// root's creation time (<see cref="InstallRoot"/>); a new stamp (a reinstall) starts again. A pending
/// report's attempts are counted and persisted BEFORE the request, so <see cref="MaxAttempts"/> holds
/// across crashes; any HTTP response of any status is <c>sent</c>; an exhausted budget is
/// <c>gave-up</c>. Every write is read back, because <see cref="SettingsService.SetManyAndFlushOrThrow"/>
/// returns silently while persistence is suppressed (data erasure) — an unverified write aborts the tick
/// rather than sending on state that may not survive.</para>
///
/// <para><b>Stated limits</b> (also in the policy): a lost response makes a retry a duplicate; deleting
/// the settings (a GDPR erasure included) deletes the record, so a further report is possible; the
/// figure the metrics side reads is reports received per day, not installations completed that day.
/// The installer's own report at install time is <see cref="InstallerReport"/>, a separate path.</para>
/// </summary>
public sealed class InstallSourceReporter : IInstallSourceReporter
{
    private static ILogger Logger => Log.ForContext<InstallSourceReporter>();

    /// <summary>The named HttpClient (see <c>VoiceWinkHttpClients</c>).</summary>
    public const string HttpClientName = "install-source";

    /// <summary>The path under the channel folder the metrics side counts. FROZEN once shipped.</summary>
    public const string ReportPath = "install-source/msstore";

    /// <summary>Fixed, version-free User-Agent, so the metrics side can drop browser and bot traffic.</summary>
    public const string UserAgent = "VoiceWink";

    public const int MaxAttempts = 3;

    internal const string SourceMsStore = "msstore";
    /// <summary>Written only by the earlier event-log versions; a record carrying it is read, never written.</summary>
    internal const string SourceNotDetected = "not-detected";
    internal const string ReportPending = "pending";
    internal const string ReportSent = "sent";
    internal const string ReportGaveUp = "gave-up";
    internal const string ReportNone = "none";

    private readonly SettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Func<DateTime?> _installRootCreatedUtc;
    private readonly bool _feedIsPublic;
    private readonly string _channel;
    private readonly bool _isStoreChannel;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InstallSourceReporter(SettingsService settings, IHttpClientFactory httpFactory)
        : this(
            settings,
            httpFactory,
            static () => InstallRoot.CreationTimeUtc(AppContext.BaseDirectory),
            UpdateFeedConfig.IsConfigured && !UpdateFeedConfig.FeedIsGated,
            UpdateFeedConfig.Channel,
            UpdateFeedConfig.IsStoreChannel)
    {
    }

    internal InstallSourceReporter(
        SettingsService settings,
        IHttpClientFactory httpFactory,
        Func<DateTime?> installRootCreatedUtc,
        bool feedIsPublic,
        string channel,
        bool isStoreChannel)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _installRootCreatedUtc = installRootCreatedUtc;
        _feedIsPublic = feedIsPublic;
        _channel = channel ?? string.Empty;
        _isStoreChannel = isStoreChannel;
    }

    /// <summary>The report's address for <paramref name="channel"/>:
    /// <c>https://updates.voicewink.app/&lt;channel&gt;/install-source/msstore</c>.</summary>
    public static Uri BuildUri(string channel) =>
        new($"https://updates.voicewink.app/{channel}/{ReportPath}", UriKind.Absolute);

    /// <summary>The installation identity a record is saved against: the install root's creation time,
    /// UTC, round-trip format. One formatter for the writer below and <see cref="IsStoreInstallation(SettingsService, DateTime?)"/>.</summary>
    internal static string StampFor(DateTime installRootCreated)
    {
        var utc = installRootCreated.Kind == DateTimeKind.Utc ? installRootCreated : installRootCreated.ToUniversalTime();
        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether THIS installation is a Microsoft Store one (LNC-14: the one-time Store rating line). A
    /// Store-channel build with an install root is; so is an installation whose saved record says
    /// <c>msstore</c> under the CURRENT install root's stamp (the A36 record an earlier version wrote).
    /// Settings survive an uninstall, so an earlier Store installation's record can sit beside a later
    /// website install; the stamp is what tells the two apart. No install root (a dev or portable copy):
    /// false. Reads only; never records or sends.
    /// </summary>
    public static bool IsStoreInstallation(SettingsService settings, DateTime? installRootCreatedUtc) =>
        IsStoreInstallation(settings, installRootCreatedUtc, UpdateFeedConfig.IsStoreChannel);

    internal static bool IsStoreInstallation(SettingsService settings, DateTime? installRootCreatedUtc, bool isStoreChannel) =>
        installRootCreatedUtc is { } created
        && (isStoreChannel
            || (string.Equals(settings.GetString(AppDefaults.InstallSourceInstallStamp), StampFor(created), StringComparison.Ordinal)
                && string.Equals(settings.GetString(AppDefaults.InstallSource), SourceMsStore, StringComparison.Ordinal)));

    public async Task RunAsync(bool networkProven, CancellationToken ct)
    {
        if (!_feedIsPublic || _channel.Length == 0) return;
        // An overlapping tick (a re-armed loop racing the old one) skips rather than queues.
        if (!_gate.Wait(0)) return;
        try
        {
            // The tick checked the toggle before its update check; the user may have turned
            // automatic checks off while that check ran. Off means nothing is recorded either (§4.5).
            if (!AutomaticChecksOn()) return;

            var record = EnsureRecord();
            if (record is null || record.Value.Report != ReportPending) return;

            if (record.Value.Attempts >= MaxAttempts)
            {
                // Crashed after reserving the last attempt: the budget is spent, do not send again.
                if (Persist(record.Value with { Report = ReportGaveUp }))
                    Logger.Information("Install-source report: gave up after {Attempts} attempts", record.Value.Attempts);
                return;
            }

            if (!networkProven || ct.IsCancellationRequested) return;
            // And once more right before the send.
            if (!AutomaticChecksOn()) return;

            var attempt = record.Value.Attempts + 1;
            var reserved = record.Value with { Attempts = attempt };
            if (!Persist(reserved)) return;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(_channel));
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using var client = _httpFactory.CreateClient(HttpClientName);
                // The reservation write can block behind an opt-out's own flush (the settings IO
                // lock), so the toggle may have gone off, or an erasure begun, while it waited.
                if (ct.IsCancellationRequested || !AutomaticChecksOn() || _settings.IsPersistenceSuppressed) return;
                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                // Any status reached the endpoint and was counted there.
                if (!Persist(reserved with { Report = ReportSent }))
                    Logger.Information("Install-source report: the delivery could not be saved; a later check may report again");
                Logger.Information("Install-source report sent (HTTP {Status}, attempt {Attempt}/{Max})",
                    (int)response.StatusCode, attempt, MaxAttempts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Logger.Information("Install-source report cancelled (attempt {Attempt}/{Max})", attempt, MaxAttempts);
            }
            catch (Exception ex)
            {
                if (attempt >= MaxAttempts)
                {
                    Persist(reserved with { Report = ReportGaveUp });
                    Logger.Information("Install-source report failed ({Error:l}); gave up after {Attempt} attempts",
                        ex.GetType().Name, attempt);
                }
                else
                {
                    Logger.Information("Install-source report failed ({Error:l}), attempt {Attempt}/{Max}",
                        ex.GetType().Name, attempt, MaxAttempts);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Install-source report step failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool AutomaticChecksOn() => _settings.GetBoolDefaulted(AppDefaults.AutomaticUpdateCheckEnabled);

    internal readonly record struct Record(string Stamp, string Source, string Report, int Attempts);

    /// <summary>The saved record for THIS installation; for a Store-channel build with none yet, a new
    /// pending record. Null when this is not a Velopack install, when a non-Store build has no record
    /// for this installation (it decides nothing), or when a new record could not be saved.</summary>
    private Record? EnsureRecord()
    {
        if (_installRootCreatedUtc() is not { } created) return null;
        var stamp = StampFor(created);

        if (string.Equals(_settings.GetString(AppDefaults.InstallSourceInstallStamp), stamp, StringComparison.Ordinal))
            return ReadRecord(stamp);

        if (!_isStoreChannel) return null;

        var record = new Record(stamp, SourceMsStore, ReportPending, 0);
        if (!Persist(record)) return null;
        Logger.Information("Install source for this installation: {Source:l} (the Store channel's build)", record.Source);
        return record;
    }

    private Record ReadRecord(string stamp)
    {
        var source = _settings.GetString(AppDefaults.InstallSource);
        var report = _settings.GetString(AppDefaults.InstallSourceReport);
        // Fail closed: an unreadable counter reads as a spent budget, an unknown state as "nothing to send".
        var attempts = int.TryParse(_settings.GetString(AppDefaults.InstallSourceReportAttempts),
            NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : MaxAttempts;
        if (report is not (ReportPending or ReportSent or ReportGaveUp)) report = ReportNone;
        if (source != SourceMsStore && report == ReportPending) report = ReportNone;
        return new Record(stamp, source, report, attempts);
    }

    /// <summary>Durable write of the whole record, read back. False when it did not land.</summary>
    private bool Persist(Record record)
    {
        try
        {
            _settings.SetManyAndFlushOrThrow(new Dictionary<string, string?>
            {
                [AppDefaults.InstallSourceInstallStamp] = record.Stamp,
                [AppDefaults.InstallSource] = record.Source,
                [AppDefaults.InstallSourceReport] = record.Report,
                [AppDefaults.InstallSourceReportAttempts] = record.Attempts.ToString(CultureInfo.InvariantCulture),
            });
        }
        catch (Exception ex)
        {
            Logger.Information("Install-source record not saved ({Error:l})", ex.GetType().Name);
            return false;
        }

        return !_settings.IsPersistenceSuppressed
            && string.Equals(_settings.GetString(AppDefaults.InstallSourceInstallStamp), record.Stamp, StringComparison.Ordinal)
            && string.Equals(_settings.GetString(AppDefaults.InstallSource), record.Source, StringComparison.Ordinal)
            && string.Equals(_settings.GetString(AppDefaults.InstallSourceReport), record.Report, StringComparison.Ordinal)
            && string.Equals(_settings.GetString(AppDefaults.InstallSourceReportAttempts),
                record.Attempts.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}

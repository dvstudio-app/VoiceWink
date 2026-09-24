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

/// <summary>Decides and sends the one-time install-source report (dvstudio-metrics #85).</summary>
public interface IInstallSourceReporter
{
    /// <summary>
    /// Called by the update scheduler on every automatic tick. Classifies this installation once
    /// (reading the Store's event log), and — when a matching Store event was found and
    /// <paramref name="networkProven"/> — makes one bounded attempt at the report. Never throws.
    /// </summary>
    Task RunAsync(bool networkProven, CancellationToken ct);
}

/// <summary>
/// The once-per-installation install-source report (dvstudio-metrics #85; Privacy Policy §4.5;
/// addenda A36).
///
/// <para><b>Gates it rides.</b> It is called only from the update scheduler's automatic tick, so the
/// <c>UPDATE_CHECK_ENABLED</c> build flag, the automatic-check toggle and the LGL-1 legal gate all apply
/// before this type runs at all; the manual "Check for updates" button never reaches it. On top of
/// those: only a PUBLIC, configured feed (the Store carries stable only, and a tester build must not put
/// its token on a new code path) — otherwise neither the log is read nor anything sent.</para>
///
/// <para><b>Once, keyed on the installation.</b> The result is saved against the Velopack install
/// root's creation time (<see cref="InstallRoot"/>); the same stamp is never re-classified, a new stamp
/// (a reinstall) is. A Store result starts <c>pending</c>; each attempt is counted and persisted BEFORE
/// the request, so <see cref="MaxAttempts"/> holds across crashes; any HTTP response of any status is
/// <c>sent</c>; an exhausted budget is <c>gave-up</c>. Every write is read back, because
/// <see cref="SettingsService.SetManyAndFlushOrThrow"/> returns silently while persistence is
/// suppressed (data erasure) — an unverified write aborts the tick rather than sending on state that
/// may not survive.</para>
///
/// <para><b>Stated limits</b> (also in the policy): a lost response makes a retry a duplicate; a GDPR
/// erasure deletes the record, so a second report is possible; the figure the metrics repo reads is
/// reports received per day, not installations completed that day.</para>
/// </summary>
public sealed class InstallSourceReporter : IInstallSourceReporter
{
    private static ILogger Logger => Log.ForContext<InstallSourceReporter>();

    /// <summary>The named HttpClient (see <c>VoiceWinkHttpClients</c>).</summary>
    public const string HttpClientName = "install-source";

    /// <summary>The path under the channel folder the metrics repo counts. FROZEN once shipped.</summary>
    public const string ReportPath = "install-source/msstore";

    /// <summary>Fixed, version-free User-Agent, so the metrics side can drop browser and bot traffic.</summary>
    public const string UserAgent = "VoiceWink";

    public const int MaxAttempts = 3;

    internal const string SourceMsStore = "msstore";
    internal const string SourceNotDetected = "not-detected";
    internal const string ReportPending = "pending";
    internal const string ReportSent = "sent";
    internal const string ReportGaveUp = "gave-up";
    internal const string ReportNone = "none";

    private readonly SettingsService _settings;
    private readonly IStoreInstallEventSource _events;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Func<DateTime?> _installRootCreatedUtc;
    private readonly bool _feedIsPublic;
    private readonly string _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InstallSourceReporter(SettingsService settings, IHttpClientFactory httpFactory)
        : this(
            settings,
            new StoreInstallEventLogSource(),
            httpFactory,
            static () => InstallRoot.CreationTimeUtc(AppContext.BaseDirectory),
            UpdateFeedConfig.IsConfigured && !UpdateFeedConfig.FeedIsGated,
            UpdateFeedConfig.Channel)
    {
    }

    internal InstallSourceReporter(
        SettingsService settings,
        IStoreInstallEventSource events,
        IHttpClientFactory httpFactory,
        Func<DateTime?> installRootCreatedUtc,
        bool feedIsPublic,
        string channel)
    {
        _settings = settings;
        _events = events;
        _httpFactory = httpFactory;
        _installRootCreatedUtc = installRootCreatedUtc;
        _feedIsPublic = feedIsPublic;
        _channel = channel ?? string.Empty;
    }

    /// <summary>The report's address for <paramref name="channel"/>, e.g.
    /// <c>https://updates.voicewink.app/win-x64-stable/install-source/msstore</c>.</summary>
    public static Uri BuildUri(string channel) =>
        new($"https://updates.voicewink.app/{channel}/{ReportPath}", UriKind.Absolute);

    public async Task RunAsync(bool networkProven, CancellationToken ct)
    {
        if (!_feedIsPublic || _channel.Length == 0) return;
        // An overlapping tick (a re-armed loop racing the old one) skips rather than queues.
        if (!_gate.Wait(0)) return;
        try
        {
            // The tick checked the toggle before its update check; the user may have turned
            // automatic checks off while that check ran. Off means the log is not read either (§4.5).
            if (!AutomaticChecksOn()) return;

            var record = EnsureClassified();
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
                // Any status reached the endpoint and was counted there — 404 is the expected answer.
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

    /// <summary>The saved record for THIS installation, classifying it first when there is none. Null
    /// when this is not a Velopack install or the classification could not be saved.</summary>
    private Record? EnsureClassified()
    {
        if (_installRootCreatedUtc() is not { } created) return null;
        var createdUtc = created.Kind == DateTimeKind.Utc ? created : created.ToUniversalTime();
        var stamp = createdUtc.ToString("O", CultureInfo.InvariantCulture);

        if (string.Equals(_settings.GetString(AppDefaults.InstallSourceInstallStamp), stamp, StringComparison.Ordinal))
            return ReadRecord(stamp);

        InstallSource source;
        try
        {
            var (from, to) = InstallSourceClassifier.Window(createdUtc);
            source = InstallSourceClassifier.Classify(createdUtc, _events.Read(from, to));
        }
        catch (global::System.Diagnostics.Eventing.Reader.EventLogNotFoundException)
        {
            // No Store log on this machine at all (e.g. a Windows edition without the Store): a
            // permanent answer, so it is saved like any other.
            source = InstallSource.NotDetected;
            Logger.Information("Install source: this machine has no Microsoft Store event log");
        }
        catch (Exception ex)
        {
            // Anything else may be transient (a circular log overwriting the read position, a busy
            // event service): save nothing, so the next automatic check reads again. The window is
            // anchored to the install folder, so a later read judges the same installation.
            Logger.Information("Install source: Store event log not readable ({Error:l}); will retry at the next automatic check",
                ex.GetType().Name);
            return null;
        }

        var record = source == InstallSource.MsStore
            ? new Record(stamp, SourceMsStore, ReportPending, 0)
            : new Record(stamp, SourceNotDetected, ReportNone, 0);
        if (!Persist(record)) return null;
        Logger.Information("Install source for this installation: {Source:l}", record.Source);
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

using System;
using System.Collections.Generic;
using System.Threading;

namespace VoiceWink.Services.Maintenance;

/// <summary>
/// Default <see cref="IMaintenanceGate"/> implementation — a thread-safe
/// registry of <see cref="IMaintenanceStatusSource"/> instances that
/// aggregates their reports on demand.
///
/// <para>Registered as a singleton in DI (<c>App.xaml.cs</c>). Subsystems
/// that want to contribute blocker state inject <see cref="IMaintenanceGate"/>
/// and call <see cref="Register"/> from their constructor, typically
/// holding the returned handle for the app lifetime.</para>
/// </summary>
internal sealed class MaintenanceGate : IMaintenanceGate
{
    // Locks _sources for Register / Unregister / snapshot-in-Check. Held only
    // for the duration of mutating the list or copying it to a local array;
    // source.IsBlocking() is called WITHOUT the lock so a slow source can't
    // stall other gate callers.
    private readonly object _lock = new();
    private readonly List<IMaintenanceStatusSource> _sources = new();

    // All four counters/flags are guarded by _lock (same as _sources) so the check-AND-acquire
    // in the TryBegin* methods is atomic — a bare check-then-set would be a TOCTOU (Codex plan
    // review). Update-apply and erasure are MUTUALLY EXCLUSIVE (F19); cleanup passes are refused
    // while either exclusive op holds, and refcounted so overlapping passes all drain; an
    // automatic licence check (LIC-23) follows the cleanup-pass rule exactly.
    private int _updateApplyCount;   // > 0 ⇒ an update is being applied
    private bool _erasing;           // single-holder data-erasure lease
    private int _cleanupPassCount;   // > 0 ⇒ ≥1 history-cleanup / orphan-sweep pass in flight
    private int _licenseCheckCount;  // > 0 ⇒ an automatic licence re-validation is in flight (LIC-23)

    public IDisposable Register(IMaintenanceStatusSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_lock)
        {
            _sources.Add(source);
        }

        return new Registration(this, source);
    }

    public MaintenanceStatus Check()
    {
        // Snapshot under lock so the iteration below is stable even if
        // another thread Registers / Disposes a registration concurrently.
        IMaintenanceStatusSource[] snapshot;
        lock (_lock)
        {
            snapshot = _sources.ToArray();
        }

        List<string>? blockers = null;
        foreach (var source in snapshot)
        {
            if (!source.IsBlocking(out var detail)) continue;

            blockers ??= new List<string>();
            blockers.Add(string.IsNullOrEmpty(detail)
                ? source.Name
                : $"{source.Name}: {detail}");
        }

        // Gate-internal blocker (no IMaintenanceStatusSource): a background cleanup/sweep pass
        // in flight blocks an erasure's poll until it drains, without exposing the pass as a
        // pull-model source. Read under the same lock as the counter's writers.
        bool cleanupActive, licenseCheckActive;
        lock (_lock)
        {
            cleanupActive = _cleanupPassCount > 0;
            licenseCheckActive = _licenseCheckCount > 0;
        }
        if (cleanupActive)
        {
            blockers ??= new List<string>();
            // Deliberately generic. This counter covers EVERY background pass, and since the
            // retired-model sweep joined history cleanup and the reference orphan sweep, naming one
            // of them made the other two report the wrong work to the user (Codex, diff review).
            blockers.Add("Background cleanup");
        }
        if (licenseCheckActive)
        {
            blockers ??= new List<string>();
            // LIC-23: its own counter and its own name — folding it into "Background cleanup" would
            // repeat the mislabelling that counter's comment records. Every Check() consumer sees
            // it: an erasure's poll waits on an admitted check within its own gate budget (10 s —
            // a validate slower than that makes the erasure refuse pre-commit, retryable, nothing
            // deleted), and an update apply's pre-check or a TRN-59 restart's busy check is turned
            // away for the length of one validate (typically about a second; at most the licensing
            // client's bound).
            blockers.Add("License check");
        }

        return blockers is null
            ? MaintenanceStatus.Clear
            : new MaintenanceStatus
            {
                CanProceed = false,
                ActiveBlockers = blockers,
            };
    }

    public bool TryBeginUpdateApply(out IDisposable? lease)
    {
        lock (_lock)
        {
            if (_erasing) { lease = null; return false; }
            _updateApplyCount++;
        }
        lease = new ReleaseScope(EndUpdateApply);
        return true;
    }

    public bool TryBeginErasure(out IDisposable? lease)
    {
        lock (_lock)
        {
            if (_updateApplyCount > 0 || _erasing) { lease = null; return false; }
            _erasing = true;
        }
        lease = new ReleaseScope(EndErasure);
        return true;
    }

    public bool TryBeginCleanupPass(out IDisposable? lease)
    {
        lock (_lock)
        {
            if (_updateApplyCount > 0 || _erasing) { lease = null; return false; }
            _cleanupPassCount++;
        }
        lease = new ReleaseScope(EndCleanupPass);
        return true;
    }

    public bool TryBeginLicenseRevalidation(out IDisposable? lease)
    {
        lock (_lock)
        {
            // Refused while either exclusive op holds — the cleanup-pass rule, for the same two
            // reasons: once the user has begun erasing, no automatic request may carry the stored
            // key out; and a tick admitted during an update DOWNLOAD would make the apply's
            // post-download re-check see "License check" and discard the download (self-review,
            // correctness lens — the plan had admitted it during an apply).
            if (_updateApplyCount > 0 || _erasing) { lease = null; return false; }
            _licenseCheckCount++;
        }
        lease = new ReleaseScope(EndLicenseRevalidation);
        return true;
    }

    public bool IsApplyingUpdate
    {
        get
        {
            lock (_lock)
            {
                return _updateApplyCount > 0;
            }
        }
    }

    public bool IsErasing
    {
        get
        {
            lock (_lock)
            {
                return _erasing;
            }
        }
    }

    private void EndUpdateApply()
    {
        lock (_lock)
        {
            if (_updateApplyCount > 0) _updateApplyCount--;
        }
    }

    private void EndErasure()
    {
        lock (_lock)
        {
            _erasing = false;
        }
    }

    private void EndCleanupPass()
    {
        lock (_lock)
        {
            if (_cleanupPassCount > 0) _cleanupPassCount--;
        }
    }

    private void EndLicenseRevalidation()
    {
        lock (_lock)
        {
            if (_licenseCheckCount > 0) _licenseCheckCount--;
        }
    }

    private void Unregister(IMaintenanceStatusSource source)
    {
        lock (_lock)
        {
            _sources.Remove(source);
        }
    }

    private sealed class Registration : IDisposable
    {
        private MaintenanceGate? _gate;
        private readonly IMaintenanceStatusSource _source;

        public Registration(MaintenanceGate gate, IMaintenanceStatusSource source)
        {
            _gate = gate;
            _source = source;
        }

        public void Dispose()
        {
            // Idempotent — multiple Dispose() calls don't double-Unregister.
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Unregister(_source);
        }
    }

    // One release-once scope for all four lease kinds — the release delegate carries the
    // specific End* action. Idempotent: only the first Dispose runs it.
    private sealed class ReleaseScope : IDisposable
    {
        private Action? _release;

        public ReleaseScope(Action release) => _release = release;

        public void Dispose()
        {
            var release = Interlocked.Exchange(ref _release, null);
            release?.Invoke();
        }
    }
}

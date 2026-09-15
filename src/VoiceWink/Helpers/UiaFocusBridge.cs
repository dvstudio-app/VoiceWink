using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Minimal UI Automation client for capturing and restoring DOM-element focus
/// inside Chromium-based targets (Electron apps like Claude Desktop). Win32-only
/// focus diagnostics cannot see DOM-element focus inside a Chromium renderer;
/// UIA can. This bridge exposes only the three operations VoiceWink needs:
/// <see cref="CaptureFocusedElement"/> at recording-start,
/// <see cref="TryRestoreFocus"/> immediately before <c>SendCtrlV</c>, and
/// <see cref="EnqueueRelease"/> to dispose a captured RCW without blocking
/// the caller.
///
/// All public surface is exception-safe — methods log diagnostic information
/// at <c>Information</c> level on failure and return null/false rather than
/// throwing.
///
/// Privacy: only HRESULT codes, exception type names, and timing in
/// milliseconds are logged. Element names, control values, classnames, and
/// window titles are NEVER logged from this file because UIA element properties
/// may surface document text or chat content from the target app.
///
/// <para><b>Hang isolation — why this code exists.</b> Every UIA call is a
/// synchronous cross-process COM call into the target app's UI thread. If that
/// thread is wedged (Excel during a heavy recalc, a frozen Electron renderer,
/// a modal-dialog-waiting app, etc.) the call would block indefinitely. We
/// defend with three layers:</para>
/// <list type="number">
/// <item><b>A single long-lived MTA worker thread (<see cref="UiaWorker"/>)
/// owns the <see cref="IUIAutomation"/> RCW and executes every UIA call on
/// that same thread.</b> VoiceWink's UI thread is STA; an STA-affinitized RCW
/// would force COM to marshal every call back to the UI thread before going
/// cross-process — the UI thread would then block making the cross-process
/// call itself, which the timeout below cannot unblock. Creating the RCW on
/// an MTA thread that immediately exits is NOT equivalent to a long-lived
/// MTA executor (per Microsoft's UIA threading guidance the apartment must
/// remain alive for the lifetime of the RCW), so we keep one MTA thread
/// running for the life of the process.</item>
/// <item><b>Every synchronous call is bounded by <see cref="UiaCallTimeout"/>,
/// and since PST-10 a timed-out item carries a LIFECYCLE
/// (<see cref="BoundedCallGate"/>).</b> The caller waits on the item's task with
/// a timeout; on expiry it classifies rather than walking away: an item still
/// QUEUED is abandoned (its body provably never runs — previously it executed
/// whenever the wedged target answered, moving focus seconds after the paste;
/// PST-8 measured a 500 ms bound answering ~17 s later on WhatsApp); a MUTATING
/// item already executing its pre-SetFocus reads gets its mutation BARRED (the
/// commit gate is the last instruction before SetFocus); an item whose mutation
/// is committed/executing classifies as still-pending — undetectable focus risk,
/// surfaced by a one-line straggler-settled log when it lands. Late RCWs still
/// route through the worker queue for release. The async
/// <see cref="BeginCaptureFocusedElement"/> path has NO bridge-side timeout —
/// its caller bounds its own wait (<c>CapturedFocusSlot.PendingCaptureResolveTimeout</c>,
/// 1 s, paid at paste time rather than ahead of the recording pill) and routes
/// give-ups through <see cref="EnqueueReleaseWhenComplete"/>; it is deliberately
/// OUTSIDE the abandon machinery (read-only straggler, lifecycle already owned
/// by the slot).</item>
/// <item><b>An <see cref="Interlocked"/> busy flag refuses overlapping
/// Capture/Restore calls.</b> Once one is in flight, further presses get an
/// immediate null/false instead of piling up behind a wedged call. The flag
/// clears when the worker finishes its current item. Releases (via
/// <see cref="EnqueueRelease"/>) deliberately do NOT participate in this flag
/// because releases must always queue eventually — so a wedged release can
/// cause the FIRST subsequent Capture to queue behind it and pay its full
/// 500ms timeout; from the second Capture onward this flag protects them.
/// That's an acceptable corner case for a sustained-wedge scenario.</item>
/// </list>
///
/// <para>The single shared <c>_busy</c> flag means Capture and Restore cannot
/// be in flight simultaneously. That's fine for VoiceWink's flow — Capture
/// runs at recording-start, Restore runs pre-paste seconds (or minutes) later.
/// If a future caller needs concurrent UIA ops, split the flag.</para>
///
/// <para><b>Two workers since PST-2b:</b> the ControlType probe
/// (<see cref="TryGetFocusedControlType"/>) runs on its own dedicated
/// <see cref="UiaWorker"/> instance (own MTA thread, own IUIAutomation RCW, own
/// busy flag) because it executes BEFORE the focus restore on the paste path —
/// a timed-out probe's straggler on the shared worker would hold the busy flag
/// and make the immediately following restore fast-fail, silently converting a
/// fail-open probe into a skipped restore. Capture/Restore/Release stay on the
/// main worker; the probe never touches captured RCWs, so no cross-apartment
/// concern arises.</para>
/// </summary>
internal static class UiaFocusBridge
{
    private static ILogger Logger => Log.ForContext(typeof(UiaFocusBridge));

    // CUIAutomation coclass — sufficient for IUIAutomation::GetFocusedElement.
    // CUIAutomation8 (e22ad333-...) is only needed for IUIAutomation2+ APIs we don't use.
    private static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    // Upper bound on how long a single UIA call may stall the caller. Healthy
    // calls finish in 10-100ms; Chromium renderers can push to ~300ms. 500ms
    // gives a comfortable margin while keeping recording-start responsive
    // (≤0.5s freeze) if the target's UI thread is wedged.
    private static readonly TimeSpan UiaCallTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly object _initLock = new();
    private static UiaWorker? _worker;
    private static UiaWorker? _probeWorker;

    private static UiaWorker? GetWorker()
    {
        var w = _worker;
        if (w != null) return w;

        lock (_initLock)
        {
            if (_worker == null)
            {
                try
                {
                    _worker = new UiaWorker();
                }
                catch (Exception ex)
                {
                    Logger.Information("UIA worker init failed: {ExType}", ex.GetType().Name);
                }
            }
            return _worker;
        }
    }

    /// <summary>
    /// Dedicated worker for the PST-2 ControlType probe (PST-2b). The probe now runs
    /// BEFORE the focus restore on the paste path, so it must never contend with it:
    /// on the shared worker a timed-out probe leaves its straggler holding the busy
    /// interlock, and the immediately following <see cref="TryRestoreFocus"/> /
    /// <see cref="TryRefocusCurrentElement"/> would fast-fail — turning a fail-OPEN
    /// probe into a silently skipped restore. A separate thread + IUIAutomation RCW
    /// + busy flag makes the isolation structural. Warmed at startup alongside the
    /// main worker (<see cref="WarmUp"/>) — lazy creation at the first suspicious
    /// paste would race async activation and fail open, losing exactly that paste.
    /// Probes during a sustained wedge fast-fail on THIS worker's busy flag exactly
    /// like the main worker's ops do on theirs.
    /// </summary>
    private static UiaWorker? GetProbeWorker()
    {
        var w = _probeWorker;
        if (w != null) return w;

        lock (_initLock)
        {
            if (_probeWorker == null)
            {
                try
                {
                    _probeWorker = new UiaWorker();
                }
                catch (Exception ex)
                {
                    Logger.Information("UIA probe worker init failed: {ExType}", ex.GetType().Name);
                }
            }
            return _probeWorker;
        }
    }

    /// <summary>
    /// Capture the currently focused UIA element. The caller owns the returned
    /// RCW; release it via <see cref="EnqueueRelease"/> (preferred — won't block
    /// the caller's thread) or <see cref="Marshal.ReleaseComObject"/>. Returns
    /// null on any failure path, including timeout
    /// (<see cref="UiaCallTimeout"/>) when the target is unresponsive.
    /// </summary>
    internal static IUIAutomationElement? CaptureFocusedElement()
    {
        return GetWorker()?.CaptureFocusedElement(UiaCallTimeout);
    }

    /// <summary>
    /// Non-blocking variant of <see cref="CaptureFocusedElement"/> for the
    /// recording-start hot path: enqueues the capture on the MTA worker
    /// IMMEDIATELY (a queue add — microseconds) and returns the pending task
    /// instead of waiting on it, so the caller's UI thread never eats the
    /// 10–500 ms cross-process wait ahead of the MiniRecorder pill. Returns
    /// null under exactly the same refusal conditions as the sync path
    /// (worker unavailable, activation incomplete, <c>_busy</c> interlock).
    /// The returned task completes whenever the worker finishes — it has NO
    /// caller-side timeout; callers bound their own wait (see
    /// <c>CapturedFocusSlot.PendingCaptureResolveTimeout</c>) and route
    /// give-ups through <see cref="EnqueueReleaseWhenComplete"/> so a
    /// late-arriving RCW is never leaked.
    /// </summary>
    internal static Task<IUIAutomationElement?>? BeginCaptureFocusedElement()
    {
        return GetWorker()?.BeginCaptureFocusedElement()?.Task;
    }

    /// <summary>
    /// Fire-and-forget release of the element a still-pending (or abandoned)
    /// capture task will eventually produce. The continuation only enqueues
    /// the actual <see cref="Marshal.ReleaseComObject"/> onto the MTA worker,
    /// so it is safe to run synchronously on whatever thread completes the
    /// task. Canceled/faulted/null results are no-ops.
    /// </summary>
    internal static void EnqueueReleaseWhenComplete(Task<IUIAutomationElement?> task)
    {
        task.ContinueWith(
            static t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                    GetWorker()?.EnqueueRelease(t.Result);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Attempt to restore focus to the given element. Returns true on success
    /// (HRESULT 0). Caller retains ownership of the RCW. Never throws. Returns
    /// false on timeout — the paste path falls through to plain Ctrl+V rather
    /// than leaving the user staring at a frozen UI.
    ///
    /// <para><b>Contract:</b> <paramref name="element"/> must have originated
    /// from <see cref="CaptureFocusedElement"/>. The worker uses the RCW
    /// directly under the assumption that it's MTA-affinitized to this
    /// bridge's worker apartment. Passing an RCW obtained elsewhere may cause
    /// COM cross-apartment marshaling that defeats the hang-isolation
    /// guarantee.</para>
    /// </summary>
    internal static bool TryRestoreFocus(IUIAutomationElement element)
    {
        return TryRestoreFocusTyped(element) == RestoreFocusResult.Success;
    }

    /// <summary>
    /// PST-4 typed twin of <see cref="TryRestoreFocus"/>: surfaces the stale-RCW
    /// signature (<see cref="RestoreFocusResult.ElementUnavailable"/>) distinctly
    /// from generic failure — the identity-verified restore legalizes the
    /// fresh-recapture fallback ONLY on that dead signal.
    /// </summary>
    internal static RestoreFocusResult TryRestoreFocusTyped(IUIAutomationElement element)
    {
        return GetWorker()?.TryRestoreFocusTyped(element, UiaCallTimeout) ?? RestoreFocusResult.Unavailable;
    }

    /// <summary>
    /// Fresh-recapture fallback for the paste path: capture whatever element currently has
    /// UIA focus in the (now-foreground) target and <c>SetFocus</c> it. Used when the element
    /// captured at recording-start has gone stale (SetFocus returned UIA_E_ELEMENTNOTAVAILABLE,
    /// 0x80040201) — re-focusing a LIVE element is strictly better than re-focusing a dead
    /// reference, and recovers the common Chromium case where the editable regained focus on
    /// window activation but the original RCW was invalidated by a DOM re-render. Bounded by
    /// <see cref="UiaCallTimeout"/>; never throws; returns true only on a successful SetFocus.
    /// </summary>
    internal static bool TryRefocusCurrentElement()
    {
        return GetWorker()?.RefocusCurrentElement(UiaCallTimeout) ?? false;
    }

    /// <summary>
    /// Read the LIVE focused element's UIA ControlType (PST-2 Stage-2 probe:
    /// distinguishes browser-chrome text fields — ControlType.Edit, which must
    /// paste — from the blank-frame Pane/Document state that swallows Ctrl+V).
    /// Runs on the DEDICATED probe worker (see <see cref="GetProbeWorker"/>) with
    /// the standard 500 ms bound and busy interlock; returns null on ANY failure
    /// path so callers fail OPEN. PST-2b: never share this call with the main
    /// worker — the probe precedes the focus restore, and a probe straggler on a
    /// shared worker would starve the restore's busy check.
    /// </summary>
    internal static int? TryGetFocusedControlType()
    {
        return GetProbeWorker()?.GetFocusedControlType(UiaCallTimeout);
    }

    /// <summary>
    /// PST-3: full editability shape + runtime-ID identity of the LIVE focused
    /// element — used by the captured-element rescue's fail-closed verification
    /// and the identity-bound post-restore drift net. Probe worker (same
    /// isolation rationale as <see cref="TryGetFocusedControlType"/>).
    /// </summary>
    internal static UiaShapeWithIdentity? TryGetFocusedElementShapeWithIdentity()
    {
        return GetProbeWorker()?.GetFocusedElementShape(UiaCallTimeout);
    }

    /// <summary>
    /// PST-3: editability shape of a CAPTURED recording-start element. Runs on
    /// the MAIN worker — the captured RCW's home apartment (probing it from the
    /// probe worker would be a cross-apartment use of a worker-affine RCW).
    /// The caller keeps ownership of the element's lifetime. Since PST-4 this
    /// delegates to the typed probe (null unless the element is alive).
    /// </summary>
    internal static UiaElementShape? TryGetCapturedElementShape(IUIAutomationElement element)
    {
        return GetWorker()?.GetElementShape(element, UiaCallTimeout);
    }

    /// <summary>
    /// PST-4: typed captured-element probe (shape + runtime ID + alive/dead/unknown
    /// status, one bounded main-worker item). See <see cref="CapturedProbeKind"/> —
    /// only <see cref="CapturedProbeKind.ElementUnavailable"/> legalizes the
    /// fresh-recapture fallback downstream.
    /// </summary>
    internal static CapturedElementProbe ProbeCapturedElement(IUIAutomationElement element)
    {
        return GetWorker()?.ProbeElement(element, UiaCallTimeout)
               ?? new CapturedElementProbe(CapturedProbeKind.ProbeUnavailable, null, null);
    }

    /// <summary>
    /// PST-6: bounded text readback of the LIVE focused element for post-paste
    /// insertion verification. Probe worker (same isolation rationale as the
    /// shape probes — this runs on the paste path around the restore/send).
    /// Null on every failure path, on password fields (never read), and when no
    /// trusted content source exists — verification then reads Unknown and
    /// fails open. Content strings are returned to the caller and NEVER logged
    /// here (file-level privacy contract).
    /// </summary>
    internal static PasteTextReadback? TryGetFocusedElementTextReadback()
    {
        return GetProbeWorker()?.GetFocusedElementTextReadback(UiaCallTimeout);
    }

    /// <summary>
    /// Fire-and-forget release of an RCW captured via
    /// <see cref="CaptureFocusedElement"/>. The actual <see cref="Marshal.ReleaseComObject"/>
    /// call runs on the MTA worker thread, so the caller's thread does not
    /// block on the underlying cross-process <c>Release()</c> when the target
    /// app is wedged (e.g. <c>MainViewModel.ReleaseCapturedFocusedElement</c>
    /// runs on the UI thread; sync release of an Excel-owned RCW during a
    /// heavy recalc would otherwise freeze the UI).
    /// </summary>
    internal static void EnqueueRelease(IUIAutomationElement element)
    {
        GetWorker()?.EnqueueRelease(element);
    }

    /// <summary>
    /// Eagerly kick off worker-thread construction + COM activation for EVERY
    /// worker paste-time gating relies on. Intended to be called during app
    /// startup (e.g. <c>MainViewModel</c>'s ctor) so the UIA workers are ready
    /// well before the user's first hotkey press.
    /// Without this, the first hotkey after launch would race against
    /// activation: <see cref="CaptureFocusedElement"/> returns null if
    /// activation hasn't completed, which means the first recording silently
    /// loses DOM-focus capture and falls back to plain Ctrl+V at paste time
    /// (no captured element → no <see cref="TryRestoreFocus"/> call possible).
    /// PST-2b: the probe worker MUST warm here too — created lazily at the
    /// first suspicious-shape paste instead, <see cref="TryGetFocusedControlType"/>
    /// would hit its not-yet-ready check, fail open, and let that first paste
    /// vanish (the exact loss the pre-restore gate exists to block).
    /// Activation typically completes in &lt;100ms, so any startup call site
    /// gives both workers more than enough time.
    /// </summary>
    internal static void WarmUp()
    {
        GetWorker();
        GetProbeWorker();
    }

    /// <summary>
    /// Dedicated MTA worker thread + work queue. Owns the
    /// <see cref="IUIAutomation"/> RCW for the process lifetime so all UIA
    /// calls execute in a single unambiguous MTA apartment.
    /// </summary>
    private sealed class UiaWorker
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly ManualResetEventSlim _ready = new(initialState: false);
        private IUIAutomation? _automation;
        private int _busy; // 0 = idle, 1 = call in flight
        private long _busyHeldSinceTicks; // set on every busy CAS win — wedge age for skip logs

        private static DateTime UtcNow() => DateTime.UtcNow;

        /// <summary>
        /// PST-10 admission. On refusal the log line carries the incumbent's age so a
        /// sustained wedge is diagnosable from one line. Busy is released ONLY by the
        /// worker's dequeue path (the item's <see cref="BoundedCallItem{T}.Complete"/> —
        /// after running the body or skipping an abandoned husk), NEVER by the timeout
        /// path: releasing at abandonment would reopen admission behind a wedged release
        /// and turn today's "first caller waits, later callers fast-fail" backpressure
        /// into an unbounded husk-enqueue loop (Codex PST-10 round 2, blocking).
        /// </summary>
        private bool TryAcquireBusy(string op)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                var heldMs = (int)TimeSpan.FromTicks(UtcNow().Ticks - Volatile.Read(ref _busyHeldSinceTicks)).TotalMilliseconds;
                Logger.Information(
                    "UIA {Op} skipped: previous call still in flight for {Ms}ms (target likely wedged)",
                    op, heldMs);
                return false;
            }
            Volatile.Write(ref _busyHeldSinceTicks, UtcNow().Ticks);
            return true;
        }

        private void ReleaseBusy() => Interlocked.Exchange(ref _busy, 0);

        private BoundedCallItem<T> NewItem<T>(string op) => new(op, ReleaseBusy, UtcNow());

        public UiaWorker()
        {
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "VoiceWink.UiaWorker"
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();

            // Deliberately do NOT block on _ready here. Activation typically
            // completes in <100ms and isn't target-bound, but blocking the
            // caller for any duration weakens the "mini-recorder visible within
            // 500ms" guarantee on the very first hotkey press after app launch.
            // Capture/TryRestoreFocus check _ready.IsSet before using
            // _automation; the worst case is that the first call returns null
            // and the recording proceeds without DOM-level focus capture (a
            // non-essential feature — paste falls through to plain Ctrl+V).
            // Activation completes asynchronously and subsequent calls work
            // normally.
        }

        private void Run()
        {
            try
            {
                var t = Type.GetTypeFromCLSID(CLSID_CUIAutomation);
                if (t != null)
                {
                    var instance = Activator.CreateInstance(t) as IUIAutomation;
                    if (instance == null)
                    {
                        Logger.Information("UIA activation returned null");
                    }
                    // Volatile.Write pairs with Volatile.Read in
                    // CaptureFocusedElement so readers across threads see a
                    // fully-published instance after _ready.Set().
                    Volatile.Write(ref _automation, instance);
                }
                else
                {
                    Logger.Information("UIA CLSID type lookup returned null");
                }
            }
            catch (Exception ex)
            {
                Logger.Information("UIA activation failed: {ExType}", ex.GetType().Name);
            }
            finally
            {
                _ready.Set();
            }

            // Process items serially; the worker is the only thread that ever
            // touches _automation or the RCWs returned from it.
            try
            {
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    try { item(); }
                    catch (Exception ex)
                    {
                        Logger.Information("UIA worker item threw: {ExType}", ex.GetType().Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Information("UIA worker loop exited: {ExType}", ex.GetType().Name);
            }
        }

        /// <summary>
        /// Shared enqueue path for the sync and async capture entry points:
        /// refuses (null) when activation is incomplete or a call is already in
        /// flight, otherwise enqueues the GetFocusedElement work item and
        /// returns its <see cref="BoundedCallItem{T}"/>. The sync wrapper uses
        /// the item's timeout classification (PST-10 — nothing is cancelled
        /// anymore; a still-pending capture's late RCW is routed to
        /// <see cref="QueueRelease"/> by the wrapper's continuation); the async
        /// path exposes only the task and stays outside the abandon machinery.
        /// </summary>
        public BoundedCallItem<IUIAutomationElement?>? BeginCaptureFocusedElement()
        {
            // Snapshot _automation with Volatile.Read to make the cross-thread
            // visibility explicit (the worker writes via Volatile.Write in Run).
            // ManualResetEventSlim.IsSet probably also establishes ordering on
            // current .NET runtimes, but its public contract doesn't promise
            // that, so we use Volatile.Read on the field itself.
            if (!_ready.IsSet) return null;
            var auto = Volatile.Read(ref _automation);
            if (auto == null) return null;

            if (!TryAcquireBusy("capture")) return null;

            var item = NewItem<IUIAutomationElement?>("capture");
            _queue.Add(() => item.RunAtDequeue(i =>
            {
                IUIAutomationElement? captured = null;
                try
                {
                    // Use the local snapshot rather than the field — both are
                    // safe (worker is sole writer) but using the snapshot makes
                    // the read-side visibility model explicit.
                    var hr = auto.GetFocusedElement(out var element);
                    if (hr == 0)
                    {
                        captured = element;
                    }
                    else
                    {
                        if (element != null)
                        {
                            try { Marshal.ReleaseComObject(element); } catch { /* swallow */ }
                        }
                        Logger.Information("UIA capture: hresult=0x{HR:X8}", (uint)hr);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Information("UIA capture exception: {ExType}", ex.GetType().Name);
                }

                // PST-10: publication always lands (no cancellation exists anymore), so
                // the result — including a late RCW — rides the task. Timed-out sync
                // callers route it to QueueRelease via their pending-classification
                // continuation; the async (Begin) path drains abandoned captures via
                // EnqueueReleaseWhenComplete exactly as before.
                i.Complete(captured);
            }, fallback: null, UtcNow));

            return item;
        }

        public IUIAutomationElement? CaptureFocusedElement(TimeSpan timeout)
        {
            var item = BeginCaptureFocusedElement();
            if (item == null) return null;

            if (item.Task.Wait(timeout))
            {
                return item.Task.Result;
            }

            var t = item.ClassifyTimeout(
                hasMutationGate: false, (int)timeout.TotalMilliseconds,
                describe: r => r != null ? "captured" : "null", UtcNow);
            switch (t.Class)
            {
                case BoundedTimeoutClass.CompletedLate:
                    // Completed in the race window between the wait expiring and
                    // classification — the caller gets (and owns) the real RCW.
                    return t.LateResult;
                case BoundedTimeoutClass.StillPending:
                    // The straggler's RCW must route through the worker queue for
                    // release — releasing inline on the caller's thread (typically the
                    // UI thread) would re-introduce the exact "cross-process Release
                    // blocks on wedged target" path EnqueueRelease exists to avoid.
                    item.Task.ContinueWith(
                        late =>
                        {
                            if (late.Status == TaskStatus.RanToCompletion && late.Result != null)
                                QueueRelease(late.Result);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return null;
                default:
                    // AbandonedBeforeStart: the capture provably never ran; the skipped
                    // husk completes with null, so there is no RCW to route.
                    return null;
            }
        }

        public bool TryRestoreFocus(IUIAutomationElement element, TimeSpan timeout)
            => TryRestoreFocusTyped(element, timeout) == RestoreFocusResult.Success;

        /// <summary>
        /// PST-4 typed SetFocus. Classification is deliberately NARROW
        /// (Codex PST-4 round 2): only the stale-RCW signature
        /// (UIA_E_ELEMENTNOTAVAILABLE / InvalidComObjectException /
        /// disconnected-proxy COMException — <see cref="UiaComClassification"/>)
        /// maps to <see cref="RestoreFocusResult.ElementUnavailable"/>; every
        /// other completed failure is <see cref="RestoreFocusResult.Failed"/>.
        /// Since PST-10 the non-completed states are SPLIT (see the enum docs):
        /// not-ready / abandoned-before-start / mutation-barred →
        /// <see cref="RestoreFocusResult.Unavailable"/> (provably no mutation ran
        /// or is pending); lost admission → <see cref="RestoreFocusResult.BusyPending"/>;
        /// mutation committed at timeout → <see cref="RestoreFocusResult.TimedOutPending"/>.
        /// None of these legalizes the fresh-recapture fallback.
        /// </summary>
        public RestoreFocusResult TryRestoreFocusTyped(IUIAutomationElement element, TimeSpan timeout)
        {
            // Mirror CaptureFocusedElement: bail if the worker hasn't finished
            // activating yet. _ready.IsSet pairs with Volatile.Write/_ready.Set
            // in Run() to ensure the worker is ready to process queue items.
            // Not-ready ⇒ nothing was ever queued on this worker ⇒ Unavailable
            // (the bind-safe class); a lost admission CAS ⇒ BusyPending — the
            // incumbent is of UNKNOWN kind and may itself be a pending SetFocus
            // from a previous paste attempt, so it must never read as safe
            // (Codex PST-10 plan round 1, blocking).
            if (!_ready.IsSet) return RestoreFocusResult.Unavailable;
            if (!TryAcquireBusy("SetFocus")) return RestoreFocusResult.BusyPending;

            // The caller's RCW is used directly on the worker. Per the public
            // method's contract it must have come from CaptureFocusedElement,
            // so it's already MTA-affinitized to this worker — no cross-
            // apartment marshaling. The queue serializes any subsequent
            // EnqueueRelease after this SetFocus, so there's no concurrent-
            // access lifetime race either.
            var item = NewItem<RestoreFocusResult>("SetFocus");
            _queue.Add(() => item.RunAtDequeue(
                i => i.Complete(MutatingCallBodies.DirectRestore(i.Gate, () => ClassifiedSetFocus(element))),
                fallback: RestoreFocusResult.Unavailable, UtcNow));

            if (item.Task.Wait(timeout))
            {
                return item.Task.Result;
            }

            var t = item.ClassifyTimeout(
                hasMutationGate: true, (int)timeout.TotalMilliseconds,
                describe: r => r.ToString(), UtcNow);
            return t.Class switch
            {
                // Abandoned or barred: the SetFocus provably never runs — OUR restore
                // changed nothing and (having won admission) nothing prior is pending.
                BoundedTimeoutClass.AbandonedBeforeStart => RestoreFocusResult.Unavailable,
                BoundedTimeoutClass.MutationSuppressed => RestoreFocusResult.Unavailable,
                BoundedTimeoutClass.CompletedLate => t.LateResult,
                _ => RestoreFocusResult.TimedOutPending
            };
        }

        /// <summary>The PST-4 SetFocus classification, unchanged — extracted so the PST-10
        /// commit gate (inside <see cref="MutatingCallBodies.DirectRestore"/>) is the last
        /// instruction before this runs.</summary>
        private static RestoreFocusResult ClassifiedSetFocus(IUIAutomationElement element)
        {
            try
            {
                var hr = element.SetFocus();
                if (hr == 0)
                    return RestoreFocusResult.Success;
                Logger.Information("UIA SetFocus: hresult=0x{HR:X8}", (uint)hr);
                return UiaComClassification.IsStaleHr(hr)
                    ? RestoreFocusResult.ElementUnavailable
                    : RestoreFocusResult.Failed;
            }
            catch (Exception ex)
            {
                Logger.Information("UIA SetFocus exception: {ExType}", ex.GetType().Name);
                return UiaComClassification.IsStaleException(ex)
                    ? RestoreFocusResult.ElementUnavailable
                    : RestoreFocusResult.Failed;
            }
        }

        public bool RefocusCurrentElement(TimeSpan timeout)
        {
            // Mirror TryRestoreFocus's guards. Capture (recording-start) and refocus
            // (paste-time) are seconds apart, so the shared _busy flag never overlaps them;
            // the first TryRestoreFocus attempt has already cleared _busy before we get here.
            if (!_ready.IsSet) return false;
            var auto = Volatile.Read(ref _automation);
            if (auto == null) return false;

            if (!TryAcquireBusy("refocus")) return false;

            var item = NewItem<bool>("refocus");
            _queue.Add(() => item.RunAtDequeue(i =>
            {
                var ok = false;
                IUIAutomationElement? element = null;
                try
                {
                    // Re-read the LIVE focused element on the worker (MTA) thread, then
                    // focus it — with the PST-10 commit gate between the two: the acquire
                    // read is exactly the stall that used to carry the mutation past the
                    // timeout (Codex plan round 1, blocking).
                    ok = MutatingCallBodies.CurrentRefocus(
                        i.Gate,
                        acquireTarget: () =>
                        {
                            var hr = auto.GetFocusedElement(out element);
                            if (hr == 0 && element != null)
                                return true;
                            Logger.Information("UIA refocus GetFocusedElement: hresult=0x{HR:X8}", (uint)hr);
                            return false;
                        },
                        setFocus: () =>
                        {
                            var fhr = element!.SetFocus();
                            if (fhr != 0)
                                Logger.Information("UIA refocus SetFocus: hresult=0x{HR:X8}", (uint)fhr);
                            return fhr == 0;
                        });
                }
                catch (Exception ex)
                {
                    Logger.Information("UIA refocus exception: {ExType}", ex.GetType().Name);
                }
                finally
                {
                    // Publish the SetFocus outcome and free the busy gate BEFORE the
                    // (potentially slow/wedged) cross-process Release, so a slow Release can't
                    // turn a successful SetFocus into a caller-side timeout or a false
                    // "fallback failed" diagnostic. The release still runs on this MTA worker;
                    // the serial queue keeps the next UIA op ordered behind it regardless of
                    // the now-cleared _busy flag.
                    i.Complete(ok);
                    if (element != null)
                    {
                        try { Marshal.ReleaseComObject(element); } catch { /* swallow */ }
                    }
                }
            }, fallback: false, UtcNow));

            if (item.Task.Wait(timeout))
            {
                return item.Task.Result;
            }

            var t = item.ClassifyTimeout(
                hasMutationGate: true, (int)timeout.TotalMilliseconds,
                describe: r => r ? "focused" : "not-focused", UtcNow);
            // Every timeout class maps to false here (the caller-visible contract is
            // unchanged); CompletedLate is the one honest improvement — the real result.
            return t.Class == BoundedTimeoutClass.CompletedLate && t.LateResult;
        }

        /// <summary>
        /// PST-2 Stage-2 probe: get the live focused element and read its
        /// ControlType property. Null on every failure path (activation
        /// incomplete, busy, COM error, property read failure, timeout) —
        /// callers treat null as "not confirmed" and fail open.
        /// </summary>
        public int? GetFocusedControlType(TimeSpan timeout)
        {
            if (!_ready.IsSet) return null;
            var auto = Volatile.Read(ref _automation);
            if (auto == null) return null;

            if (!TryAcquireBusy("control-type probe")) return null;

            var item = NewItem<int?>("control-type probe");
            _queue.Add(() => item.RunAtDequeue(i =>
            {
                int? result = null;
                IUIAutomationElement? element = null;
                try
                {
                    var hr = auto.GetFocusedElement(out element);
                    if (hr == 0 && element != null)
                    {
                        // Same-IID cast: QI against the identical interface GUID,
                        // just a wider managed vtable view (see
                        // IUIAutomationElementProperties below).
                        if (element is IUIAutomationElementProperties props)
                        {
                            var phr = props.GetCurrentPropertyValue(
                                NoEditableFocusGate.UiaControlTypePropertyId, out var value);
                            if (phr == 0 && value is int controlType)
                                result = controlType;
                            else
                                Logger.Information("UIA control-type read: hresult=0x{HR:X8}", (uint)phr);
                        }
                    }
                    else
                    {
                        Logger.Information("UIA control-type probe GetFocusedElement: hresult=0x{HR:X8}", (uint)hr);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Information("UIA control-type probe exception: {ExType}", ex.GetType().Name);
                }
                finally
                {
                    i.Complete(result);
                    if (element != null)
                    {
                        try { Marshal.ReleaseComObject(element); } catch { /* swallow */ }
                    }
                }
            }, fallback: null, UtcNow));

            if (item.Task.Wait(timeout))
            {
                return item.Task.Result;
            }

            var t = item.ClassifyTimeout(
                hasMutationGate: false, (int)timeout.TotalMilliseconds,
                describe: r => r?.ToString() ?? "null", UtcNow); // a ControlType id is not content
            return t.Class == BoundedTimeoutClass.CompletedLate ? t.LateResult : null;
        }

        /// <summary>
        /// PST-3: get the LIVE focused element and read its full editability
        /// shape (ControlType + pattern flags) plus its runtime ID (identity
        /// token for the rescue-aware drift net). Null on every failure path —
        /// the rescue's live verification treats null as FAIL CLOSED (blocked),
        /// unlike the fail-open ControlType probe above. A missing runtime ID
        /// yields a shape with null identity (drift net falls back to Edit-only).
        /// </summary>
        public UiaShapeWithIdentity? GetFocusedElementShape(TimeSpan timeout)
        {
            if (!_ready.IsSet) return null;
            var auto = Volatile.Read(ref _automation);
            if (auto == null) return null;

            if (!TryAcquireBusy("shape probe")) return null;

            var item = NewItem<UiaShapeWithIdentity?>("shape probe");
            _queue.Add(() => item.RunAtDequeue(i =>
            {
                UiaShapeWithIdentity? result = null;
                IUIAutomationElement? element = null;
                try
                {
                    var hr = auto.GetFocusedElement(out element);
                    if (hr == 0 && element is IUIAutomationElementProperties props)
                    {
                        if (ReadShape(props, "live") is { } shape)
                            result = new UiaShapeWithIdentity(shape, ReadRuntimeId(props));
                    }
                    else
                    {
                        Logger.Information("UIA shape probe GetFocusedElement: hresult=0x{HR:X8}", (uint)hr);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Information("UIA shape probe exception: {ExType}", ex.GetType().Name);
                }
                finally
                {
                    i.Complete(result);
                    if (element != null)
                    {
                        try { Marshal.ReleaseComObject(element); } catch { /* swallow */ }
                    }
                }
            }, fallback: null, UtcNow));

            if (item.Task.Wait(timeout))
            {
                return item.Task.Result;
            }

            var t = item.ClassifyTimeout(
                hasMutationGate: false, (int)timeout.TotalMilliseconds,
                describe: r => r != null ? "shape" : "null", UtcNow);
            return t.Class == BoundedTimeoutClass.CompletedLate ? t.LateResult : null;
        }

        /// <summary>
        /// PST-6: text readback of the LIVE focused element (probe worker).
        /// Source selection is availability-gated — never a raw property read
        /// whose unsupported default (empty string) would masquerade as empty
        /// content (Codex plan round 2): WritableValue only when the
        /// ValuePattern is available AND writable; else TextPattern's document
        /// range, only when the pattern is available. Password fields return
        /// null without any content read. Content strings ride the RESULT only;
        /// nothing content-bearing is logged (file privacy contract).
        /// </summary>
        public PasteTextReadback? GetFocusedElementTextReadback(TimeSpan timeout)
        {
            if (!_ready.IsSet) return null;
            var auto = Volatile.Read(ref _automation);
            if (auto == null) return null;

            if (!TryAcquireBusy("text readback")) return null;

            var item = NewItem<PasteTextReadback?>("text readback");
            _queue.Add(() => item.RunAtDequeue(i =>
            {
                PasteTextReadback? result = null;
                IUIAutomationElement? element = null;
                try
                {
                    var hr = auto.GetFocusedElement(out element);
                    if (hr == 0 && element is IUIAutomationElementProperties props)
                    {
                        result = ReadTextReadback(element, props);
                    }
                    else
                    {
                        Logger.Information("UIA text readback GetFocusedElement: hresult=0x{HR:X8}", (uint)hr);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Information("UIA text readback exception: {ExType}", ex.GetType().Name);
                }
                finally
                {
                    i.Complete(result);
                    if (element != null)
                    {
                        try { Marshal.ReleaseComObject(element); } catch { /* swallow */ }
                    }
                }
            }, fallback: null, UtcNow));

            if (item.Task.Wait(timeout))
            {
                return item.Task.Result;
            }

            // The describe projection must NEVER surface the readback's content — it
            // carries raw focused-field text (file privacy contract; Codex round 2).
            var t = item.ClassifyTimeout(
                hasMutationGate: false, (int)timeout.TotalMilliseconds,
                describe: r => r != null ? "read" : "null", UtcNow);
            return t.Class == BoundedTimeoutClass.CompletedLate ? t.LateResult : null;
        }

        private static PasteTextReadback? ReadTextReadback(IUIAutomationElement element, IUIAutomationElementProperties props)
        {
            // Password fields: bail before ANY content read. FAIL CLOSED (Codex diff
            // review P1) — content is only read when the element AFFIRMATIVELY reports
            // it is not a password. An unreadable property used to fall through and
            // read anyway, which is exactly backwards for the one check whose whole
            // purpose is protecting credentials.
            //
            // Support-aware ONLY (ignoreDefaultValue: true): a provider that never
            // implements the property must not be read, because the plain read would
            // supply UIA core's default FALSE and turn "never said" into "confirmed
            // not a password". See IsConfirmedNotPasswordFromSupportAwareRead.
            if (!IsConfirmedNotPassword(element))
                return null;

            var runtimeId = ReadRuntimeId(props);

            var hasValue = ReadBoolProperty(props, NoEditableFocusGate.UiaIsValuePatternAvailablePropertyId, "readback");
            var readOnly = ReadBoolProperty(props, NoEditableFocusGate.UiaValueIsReadOnlyPropertyId, "readback");
            if (hasValue == true && readOnly == false)
            {
                var vhr = props.GetCurrentPropertyValue(NoEditableFocusGate.UiaValueValuePropertyId, out var value);
                if (vhr == 0 && value is string valueText)
                    return new PasteTextReadback(PasteReadbackSource.WritableValue, valueText, runtimeId, CapHit: false);
                Logger.Information("UIA readback value read: hresult=0x{HR:X8}", (uint)vhr);
                return null;
            }

            if (ReadBoolProperty(props, NoEditableFocusGate.UiaIsTextPatternAvailablePropertyId, "readback") != true)
                return null;
            if (element is not IUIAutomationElementPatterns patterns)
                return null;

            object? patternObj = null;
            IUIAutomationTextRange? range = null;
            try
            {
                var phr = patterns.GetCurrentPattern(NoEditableFocusGate.UiaTextPatternId, out patternObj);
                if (phr != 0 || patternObj is not IUIAutomationTextPattern textPattern)
                {
                    Logger.Information("UIA readback GetCurrentPattern(Text): hresult=0x{HR:X8}", (uint)phr);
                    return null;
                }
                var rhr = textPattern.get_DocumentRange(out range);
                if (rhr != 0 || range == null)
                {
                    Logger.Information("UIA readback DocumentRange: hresult=0x{HR:X8}", (uint)rhr);
                    return null;
                }
                var ghr = range.GetText(PasteInsertionVerification.ReadbackCapCodeUnits, out var text);
                if (ghr != 0 || text == null)
                {
                    Logger.Information("UIA readback GetText: hresult=0x{HR:X8}", (uint)ghr);
                    return null;
                }
                return new PasteTextReadback(
                    PasteReadbackSource.TextPattern, text, runtimeId,
                    CapHit: text.Length >= PasteInsertionVerification.ReadbackCapCodeUnits);
            }
            finally
            {
                if (range != null)
                {
                    try { Marshal.ReleaseComObject(range); } catch { /* swallow */ }
                }
                if (patternObj != null)
                {
                    try { Marshal.ReleaseComObject(patternObj); } catch { /* swallow */ }
                }
            }
        }

        /// <summary>
        /// PST-6 password gate. True ONLY when the element affirmatively reports it is
        /// not a password; every other state (explicit true, unsupported-and-unreadable,
        /// HRESULT failure, wrong type) returns false so no content is ever read.
        /// </summary>
        private static bool IsConfirmedNotPassword(IUIAutomationElement element)
        {
            if (element is not IUIAutomationElementPatterns ex)
                return false; // cannot ask support-aware — never read content

            var hr = ex.GetCurrentPropertyValueEx(
                NoEditableFocusGate.UiaIsPasswordPropertyId, ignoreDefaultValue: true, out var value);
            var confirmed = NoEditableFocusGate.IsConfirmedNotPasswordFromSupportAwareRead(hr == 0, value);
            if (!confirmed)
            {
                // One line so a broadly-unsupported property is VISIBLE in UAT as
                // "verification never ran" rather than silently doing nothing.
                Logger.Information(
                    "UIA readback skipped: IsPassword not explicitly false (hresult=0x{HR:X8}, explicitBool={HasBool})",
                    (uint)hr, value is bool);
            }
            return confirmed;
        }

        /// <summary>
        /// PST-3: read the editability shape of a CAPTURED element (the
        /// recording-start RCW, whose home apartment is THIS worker — never the
        /// probe worker). The element is NOT released here; the caller owns its
        /// lifetime. A normally-completing call clears the busy gate in the
        /// work item's finally BEFORE the TCS completes, so the rescue's
        /// immediately following direct restore is not starved; a timed-out
        /// straggler holds the flag and the restore fast-fails → the rescue
        /// degrades to the block (status quo, safe).
        /// </summary>
        public UiaElementShape? GetElementShape(IUIAutomationElement element, TimeSpan timeout)
        {
            var probe = ProbeElement(element, timeout);
            return probe.IsAlive ? probe.Shape : null;
        }

        /// <summary>
        /// PST-4 typed captured-element probe — shape + runtime ID + status in
        /// ONE bounded worker item. Classification (Codex PST-4 rounds 1–2):
        /// stale-RCW signature (hr or exception, <see cref="UiaComClassification"/>)
        /// → ElementUnavailable (the ONLY status that legalizes fresh recapture);
        /// ControlType read failing NON-stale → ProbeUnavailable (says nothing
        /// about the element — a busy worker must not count as death); shape ok →
        /// AliveWithIdentity/AliveIdentityUnknown by the runtime-ID read.
        /// The element is NOT released here; the caller owns its lifetime. A
        /// normally-completing call clears the busy gate in the work item's
        /// finally BEFORE the TCS completes, so an immediately following direct
        /// restore is not starved; a timed-out straggler makes it fast-fail
        /// (Unavailable) — degrades safely, never recaptures.
        /// </summary>
        public CapturedElementProbe ProbeElement(IUIAutomationElement element, TimeSpan timeout)
        {
            var unavailable = new CapturedElementProbe(CapturedProbeKind.ProbeUnavailable, null, null);
            if (!_ready.IsSet) return unavailable;

            if (!TryAcquireBusy("captured probe")) return unavailable;

            var item = NewItem<CapturedElementProbe>("captured probe");
            _queue.Add(() => item.RunAtDequeue(i =>
            {
                var result = unavailable;
                try
                {
                    if (element is IUIAutomationElementProperties props)
                    {
                        // The ControlType read doubles as the liveness probe: a
                        // stale RCW answers it with the dead signature.
                        var controlHr = props.GetCurrentPropertyValue(
                            NoEditableFocusGate.UiaControlTypePropertyId, out var controlValue);
                        if (UiaComClassification.IsStaleHr(controlHr))
                        {
                            // PST-6: this was the ONE silent classification — a dead capture
                            // refused the rescue with no trace, which cost the 2026-07-29
                            // Claude Desktop diagnosis. Kept at Information like its siblings.
                            Logger.Information("UIA captured probe: element unavailable (stale RCW, hr=0x{HR:X8})", (uint)controlHr);
                            result = new CapturedElementProbe(CapturedProbeKind.ElementUnavailable, null, null);
                        }
                        else if (controlHr == 0 && controlValue is int
                                 && ReadShape(props, "captured") is { } shape)
                        {
                            var runtimeId = ReadRuntimeId(props);
                            result = new CapturedElementProbe(
                                runtimeId is { Length: > 0 }
                                    ? CapturedProbeKind.AliveWithIdentity
                                    : CapturedProbeKind.AliveIdentityUnknown,
                                shape, runtimeId);
                        }
                        else
                        {
                            Logger.Information("UIA captured probe control-type read: hresult=0x{HR:X8}", (uint)controlHr);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result = UiaComClassification.IsStaleException(ex)
                        ? new CapturedElementProbe(CapturedProbeKind.ElementUnavailable, null, null)
                        : unavailable;
                    Logger.Information("UIA captured probe exception: {ExType}", ex.GetType().Name);
                }
                finally
                {
                    i.Complete(result);
                }
            }, fallback: unavailable, UtcNow));

            if (item.Task.Wait(timeout))
            {
                return item.Task.Result;
            }

            // This is the site PST-8 measured at ~17 s on WhatsApp — the late-settled log
            // the classification arms is what makes that straggler visible at last.
            var t = item.ClassifyTimeout(
                hasMutationGate: false, (int)timeout.TotalMilliseconds,
                describe: r => r.Kind.ToString(), UtcNow);
            return t.Class == BoundedTimeoutClass.CompletedLate && t.LateResult is { } late ? late : unavailable;
        }

        /// <summary>
        /// Read the five shape properties. Folding contract lives in
        /// <see cref="UiaElementShape.FromReads"/>: a failed ControlType read →
        /// null; ANY failed secondary read → maximally-non-editable secondary
        /// fields (only ControlType==Edit can then pass the rescue predicate).
        /// </summary>
        private static UiaElementShape? ReadShape(IUIAutomationElementProperties props, string context)
        {
            var shape = UiaElementShape.FromReads(
                ReadIntProperty(props, NoEditableFocusGate.UiaControlTypePropertyId, context),
                ReadBoolProperty(props, NoEditableFocusGate.UiaIsTextPatternAvailablePropertyId, context),
                ReadBoolProperty(props, NoEditableFocusGate.UiaIsValuePatternAvailablePropertyId, context),
                ReadBoolProperty(props, NoEditableFocusGate.UiaValueIsReadOnlyPropertyId, context),
                ReadBoolProperty(props, NoEditableFocusGate.UiaIsKeyboardFocusablePropertyId, context));
            if (shape is { } s)
                Logger.Information(
                    "UIA {Context} shape: controlType={ControlType} text={Text} value={Value} readOnly={ReadOnly} focusable={Focusable}",
                    context, s.ControlTypeId, s.HasTextPattern, s.HasValuePattern, s.ValueIsReadOnly, s.IsKeyboardFocusable);
            return shape;
        }

        private static int? ReadIntProperty(IUIAutomationElementProperties props, int propertyId, string context)
        {
            var hr = props.GetCurrentPropertyValue(propertyId, out var value);
            if (hr == 0 && value is int result)
                return result;
            Logger.Information("UIA {Context} shape int read {PropertyId}: hresult=0x{HR:X8}", context, propertyId, (uint)hr);
            return null;
        }

        private static bool? ReadBoolProperty(IUIAutomationElementProperties props, int propertyId, string context)
        {
            var hr = props.GetCurrentPropertyValue(propertyId, out var value);
            if (hr == 0 && value is bool result)
                return result;
            Logger.Information("UIA {Context} shape bool read {PropertyId}: hresult=0x{HR:X8}", context, propertyId, (uint)hr);
            return null;
        }

        /// <summary>Runtime ID (VT_I4 array) — null when unavailable; identity
        /// unknown then falls back to the Edit-only rule downstream, never a match.</summary>
        private static int[]? ReadRuntimeId(IUIAutomationElementProperties props)
        {
            try
            {
                var hr = props.GetCurrentPropertyValue(NoEditableFocusGate.UiaRuntimeIdPropertyId, out var value);
                if (hr == 0 && value is int[] runtimeId && runtimeId.Length > 0)
                    return runtimeId;
                Logger.Information("UIA live shape runtime-id read: hresult=0x{HR:X8} kind={Kind}",
                    (uint)hr, value?.GetType().Name ?? "null");
            }
            catch (Exception ex)
            {
                Logger.Information("UIA live shape runtime-id exception: {ExType}", ex.GetType().Name);
            }
            return null;
        }

        public void EnqueueRelease(IUIAutomationElement element)
        {
            // Fire-and-forget. The worker (MTA) runs Marshal.ReleaseComObject
            // so the underlying cross-process Release() executes there rather
            // than on the caller's UI thread. If the target is wedged, the
            // worker blocks — but the caller is unaffected. Note: releases
            // do not participate in _busy, so the FIRST Capture queued behind
            // a wedged release will queue up and pay its 500ms timeout before
            // subsequent Captures start short-circuiting via the flag. See
            // the class-level doc for the full _busy contract.
            QueueRelease(element);
        }

        /// <summary>
        /// Internal helper used by <see cref="EnqueueRelease"/> AND by the
        /// timeout-race cleanup in <see cref="CaptureFocusedElement"/>, so both
        /// routes share one queueing implementation and the late-RCW release
        /// never executes on the caller's thread.
        /// </summary>
        private void QueueRelease(IUIAutomationElement element)
        {
            try
            {
                _queue.Add(() =>
                {
                    try { Marshal.ReleaseComObject(element); }
                    catch (Exception ex) { Logger.Information("UIA release exception: {ExType}", ex.GetType().Name); }
                });
            }
            catch (InvalidOperationException)
            {
                // Queue was completed/disposed (process is shutting down). Drop
                // the release; the OS reclaims the proxy when the process exits.
            }
        }
    }

    // ─── COM interop ─────────────────────────────────────────────────────────
    //
    // V-table layout for IUIAutomation (from uiautomationclient.h / .idl):
    //   slot 0–2: IUnknown (QueryInterface, AddRef, Release) — implicit
    //   slot 3:   CompareElements
    //   slot 4:   CompareRuntimeIds
    //   slot 5:   GetRootElement
    //   slot 6:   ElementFromHandle
    //   slot 7:   ElementFromPoint
    //   slot 8:   GetFocusedElement              ← we call this
    //   slot 9+: many more (unused)
    //
    // Reserved methods below are slot padding only. They are never called, so
    // their void signature is safe; the runtime only needs the method count
    // to lay out the v-table correctly up to the last method we declare.
    //
    // For IUIAutomationElement, SetFocus is the FIRST method past IUnknown,
    // so no padding is needed.

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomation
    {
        void Reserved01_CompareElements();
        void Reserved02_CompareRuntimeIds();
        void Reserved03_GetRootElement();
        void Reserved04_ElementFromHandle();
        void Reserved05_ElementFromPoint();
        [PreserveSig] int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
    }

    // Wider managed view of the SAME COM interface (identical IID) — used only
    // inside the worker for the PST-2 ControlType probe, so the minimal
    // IUIAutomationElement surface (and its managed test fakes) stays untouched.
    //
    // V-table layout past IUnknown (from uiautomationclient.h):
    //   slot 3:  SetFocus
    //   slot 4:  GetRuntimeId            (reserved padding)
    //   slot 5:  FindFirst               (reserved padding)
    //   slot 6:  FindAll                 (reserved padding)
    //   slot 7:  FindFirstBuildCache     (reserved padding)
    //   slot 8:  FindAllBuildCache       (reserved padding)
    //   slot 9:  BuildUpdatedCache       (reserved padding)
    //   slot 10: GetCurrentPropertyValue ← we call this (VARIANT out, marshaled
    //            as object; UIA_ControlTypePropertyId yields a VT_I4 → boxed int)
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElementProperties
    {
        [PreserveSig] int SetFocus();
        void Reserved04_GetRuntimeId();
        void Reserved05_FindFirst();
        void Reserved06_FindAll();
        void Reserved07_FindFirstBuildCache();
        void Reserved08_FindAllBuildCache();
        void Reserved09_BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    }

    // PST-6: third managed view of the SAME element interface (identical IID) —
    // padded through to GetCurrentPattern for the TextPattern readback. V-table
    // layout past IUnknown continues from the Properties view above
    // (uiautomationclient.h):
    //   slot 10: GetCurrentPropertyValue    (declared in the Properties view)
    //   slot 11: GetCurrentPropertyValueEx  (reserved padding)
    //   slot 12: GetCachedPropertyValue     (reserved padding)
    //   slot 13: GetCachedPropertyValueEx   (reserved padding)
    //   slot 14: GetCurrentPatternAs        (reserved padding)
    //   slot 15: GetCachedPatternAs         (reserved padding)
    //   slot 16: GetCurrentPattern          ← we call this (IUnknown* out,
    //            marshaled as object; QI to IUIAutomationTextPattern follows)
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElementPatterns
    {
        [PreserveSig] int SetFocus();
        void Reserved04_GetRuntimeId();
        void Reserved05_FindFirst();
        void Reserved06_FindAll();
        void Reserved07_FindFirstBuildCache();
        void Reserved08_FindAllBuildCache();
        void Reserved09_BuildUpdatedCache();
        void Reserved10_GetCurrentPropertyValue();
        // Slot 11, declared for real (PST-6 password gate): ignoreDefaultValue=TRUE
        // distinguishes a provider's explicit answer from UIA core's default, which a
        // plain GetCurrentPropertyValue cannot.
        [PreserveSig] int GetCurrentPropertyValueEx(
            int propertyId,
            [MarshalAs(UnmanagedType.Bool)] bool ignoreDefaultValue,
            [MarshalAs(UnmanagedType.Struct)] out object value);
        void Reserved12_GetCachedPropertyValue();
        void Reserved13_GetCachedPropertyValueEx();
        void Reserved14_GetCurrentPatternAs();
        void Reserved15_GetCachedPatternAs();
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.Interface)] out object? patternObject);
    }

    // PST-6: minimal IUIAutomationTextPattern — only get_DocumentRange. V-table
    // past IUnknown (uiautomationclient.h):
    //   slot 3: RangeFromPoint            (reserved padding)
    //   slot 4: RangeFromChild            (reserved padding)
    //   slot 5: GetSelection              (reserved padding)
    //   slot 6: GetVisibleRanges          (reserved padding)
    //   slot 7: get_DocumentRange         ← we call this
    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTextPattern
    {
        void Reserved03_RangeFromPoint();
        void Reserved04_RangeFromChild();
        void Reserved05_GetSelection();
        void Reserved06_GetVisibleRanges();
        [PreserveSig] int get_DocumentRange([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange? range);
    }

    // PST-6: minimal IUIAutomationTextRange — only GetText. V-table past
    // IUnknown (uiautomationclient.h):
    //   slot 3:  Clone                    (reserved padding)
    //   slot 4:  Compare                  (reserved padding)
    //   slot 5:  CompareEndpoints         (reserved padding)
    //   slot 6:  ExpandToEnclosingUnit    (reserved padding)
    //   slot 7:  FindAttribute            (reserved padding)
    //   slot 8:  FindText                 (reserved padding)
    //   slot 9:  GetAttributeValue        (reserved padding)
    //   slot 10: GetBoundingRectangles    (reserved padding)
    //   slot 11: GetEnclosingElement      (reserved padding)
    //   slot 12: GetText                  ← we call this (BSTR out; maxLength
    //            bounds the read — -1 would mean unbounded, never used here)
    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationTextRange
    {
        void Reserved03_Clone();
        void Reserved04_Compare();
        void Reserved05_CompareEndpoints();
        void Reserved06_ExpandToEnclosingUnit();
        void Reserved07_FindAttribute();
        void Reserved08_FindText();
        void Reserved09_GetAttributeValue();
        void Reserved10_GetBoundingRectangles();
        void Reserved11_GetEnclosingElement();
        [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string? text);
    }
}

using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.System;

/// <summary>
/// Clipboard operations and Ctrl+V paste.
/// Uses Win32 clipboard API and SendInput for paste simulation.
/// Implements clipboard backup → set text → Ctrl+V → restore clipboard after delay.
/// </summary>
public sealed class ClipboardService
{
    private static ILogger Logger => Log.ForContext<ClipboardService>();

    private readonly SettingsService _settings;
    private readonly Services.Input.HotkeyService _hotkeyService;
    private readonly PasteDeliveryVerifier _deliveryVerifier;

    // Count of deferred clipboard-restore tasks still pending (paste → wait → restore). Read by
    // PasteRestoreMaintenanceSource so the update/reset gate waits for an in-flight restore.
    private int _pendingRestores;

    // IMG-BG (2026-07-16): serializes every in-process clipboard MUTATION flow. Until image
    // generation was backgrounded exactly one paste/copy could be in flight at a time (the
    // recording state machine serialized them); now a background job's image copy can overlap a
    // live dictation's text paste, and an unserialized interleaving could stomp the other
    // writer's payload mid-sequence (set → gates → Ctrl+V spans ~1.5 s). Acquired ONLY in the
    // public entry points (SetClipboardAsync / SetClipboardImageAsync / PasteAtCursorAsync /
    // PasteImageAtCursorAsync / the deferred restore / AcquireWriteLeaseAsync) — the
    // *CoreUnderLease members are lock-free by name so nested calls can never re-enter the
    // non-reentrant semaphore. Always WaitAsync, never Wait: the UI thread must not block on a
    // holder that may itself need the UI dispatcher to finish.
    private readonly global::System.Threading.SemaphoreSlim _writeLease = new(1, 1);

    /// <summary>True while a deferred clipboard restore is pending after a paste. Thread-safe.</summary>
    public bool IsRestorePending => global::System.Threading.Volatile.Read(ref _pendingRestores) > 0;

    public ClipboardService(SettingsService settings, Services.Input.HotkeyService hotkeyService)
        : this(settings, hotkeyService, null)
    {
    }

    /// <summary>
    /// PST-6 seam. Internal (not an overload of the public ctor) because
    /// <see cref="PasteDeliveryVerifier"/> is internal and a public signature may not
    /// expose it. Production DI registers through this via a factory; null builds the
    /// production verifier so the public ctor keeps working unchanged.
    /// </summary>
    internal ClipboardService(
        SettingsService settings,
        Services.Input.HotkeyService hotkeyService,
        PasteDeliveryVerifier? deliveryVerifier)
    {
        _settings = settings;
        _hotkeyService = hotkeyService;
        _deliveryVerifier = deliveryVerifier ?? new PasteDeliveryVerifier();
    }

    /// <summary>
    /// Acquire the clipboard write lease for a multi-step transaction that must not interleave
    /// with any other in-process clipboard mutation (the selected-text capture's
    /// capture → Ctrl+C → read → restore sequence). The returned lease exposes the lock-free
    /// snapshot/read/restore cores — callers must use THOSE, never the public locked entry
    /// points, while holding the lease (the semaphore is not reentrant). Dispose releases;
    /// disposing twice releases once.
    /// </summary>
    internal async Task<WriteLease> AcquireWriteLeaseAsync()
    {
        await _writeLease.WaitAsync().ConfigureAwait(false);
        return new WriteLease(this);
    }

    /// <summary>Lease handle over <see cref="_writeLease"/> exposing the lock-free cores.</summary>
    internal sealed class WriteLease : IDisposable
    {
        private ClipboardService? _owner;

        internal WriteLease(ClipboardService owner) => _owner = owner;

        private ClipboardService Owner => _owner
            ?? throw new ObjectDisposedException(nameof(WriteLease));

        /// <summary>Value-based snapshot of the current clipboard (lock-free core).</summary>
        public Task<ClipboardSnapshot?> CaptureSnapshotAsync() => Owner.CaptureSnapshotAsync();

        /// <summary>Current clipboard text, null when none (lock-free core).</summary>
        public string? GetText() => Owner.GetClipboard();

        /// <summary>Restore a snapshot captured earlier in this transaction (lock-free core).</summary>
        public Task<bool> RestoreSnapshotAsync(ClipboardSnapshot snapshot) => Owner.RestoreSnapshotAsync(snapshot);

        public void Dispose()
        {
            var owner = global::System.Threading.Interlocked.Exchange(ref _owner, null);
            owner?._writeLease.Release();
        }
    }

    /// <summary>
    /// Try to open the clipboard with retries and exponential backoff.
    /// OpenClipboard fails when another process holds it (e.g. clipboard managers, RDP).
    /// </summary>
    private static bool TryOpenClipboard(int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            if (NativeInterop.OpenClipboard(IntPtr.Zero)) return true;
            global::System.Threading.Thread.Sleep(50 * (i + 1));
        }

        // Name the contender so clipboard contention is attributable (RDP bridges,
        // clipboard managers, Office apps) instead of reading as an API flake.
        try
        {
            var owner = NativeInterop.GetClipboardOwner();
            NativeInterop.GetWindowThreadProcessId(owner, out var ownerPid);
            string ownerName = "";
            if (ownerPid != 0)
            {
                try
                {
                    using var p = global::System.Diagnostics.Process.GetProcessById((int)ownerPid);
                    ownerName = p.ProcessName;
                }
                catch { /* process exited / access denied */ }
            }
            // SEC-3: this is the clipboard OWNER's process name — the exact value {OwnerProcess}
            // and the `ownerProc="…"` rendered token already exist for, on the allowlist since
            // REL-16. It was logged under the generic {Name} with a `process="…"` token, so BOTH
            // redaction layers missed it. Reuse the established contract rather than adding a
            // third spelling; a quoted shape is safe here because a Windows process name cannot
            // contain a quote (unlike the device names above, which is why those use EOL).
            Logger.Warning(
                "OpenClipboard failed after {Retries} retries; clipboard owner=0x{Owner:X} pid={Pid} ownerProc=\"{OwnerProcess}\"",
                maxRetries, owner, ownerPid, global::VoiceWink.Helpers.LogValueSanitizer.SingleLine(ownerName));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Clipboard-owner diagnostic failed");
        }
        return false;
    }

    /// <summary>
    /// Value-based clipboard snapshot. Captures raw bytes for each clipboard format
    /// so the data survives clipboard ownership changes (unlike OLE IDataObject references
    /// which are invalidated when another process sets the clipboard).
    /// </summary>
    internal sealed class ClipboardSnapshot : IDisposable
    {
        private readonly List<(uint format, byte[] data)>? _formats;

        private ClipboardSnapshot(List<(uint format, byte[] data)>? formats, bool isEmpty)
        {
            _formats = formats;
            IsEmpty = isEmpty;
        }

        public bool IsEmpty { get; }

        public static ClipboardSnapshot Empty { get; } = new(null, isEmpty: true);

        public static ClipboardSnapshot FromFormats(List<(uint format, byte[] data)> formats)
            => new(formats, isEmpty: false);

        public bool Restore()
        {
            if (IsEmpty)
            {
                return ClearClipboardContents();
            }

            if (_formats == null || _formats.Count == 0)
            {
                return false;
            }

            if (!TryOpenClipboard())
            {
                return false;
            }

            try
            {
                return RestoreUnderOpenClipboard();
            }
            finally
            {
                NativeInterop.CloseClipboard();
            }
        }

        /// <summary>
        /// Restore while the caller already holds the clipboard open — the deferred restore's
        /// single-ownership-window compare-and-restore (IMG-BG). Empties the clipboard first;
        /// an <see cref="IsEmpty"/> snapshot restores to "empty" by stopping there.
        /// </summary>
        public bool RestoreUnderOpenClipboard()
        {
            NativeInterop.EmptyClipboard();

            if (IsEmpty)
            {
                return true;
            }

            if (_formats == null || _formats.Count == 0)
            {
                return false;
            }

            var restoredCount = 0;
            foreach (var (format, data) in _formats)
            {
                var hGlobal = NativeInterop.GlobalAlloc(NativeInterop.GMEM_MOVEABLE, (UIntPtr)data.Length);
                if (hGlobal == IntPtr.Zero) continue;

                var ptr = NativeInterop.GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero)
                {
                    NativeInterop.GlobalFree(hGlobal);
                    continue;
                }

                Marshal.Copy(data, 0, ptr, data.Length);
                NativeInterop.GlobalUnlock(hGlobal);

                // SetClipboardData takes ownership of hGlobal — do NOT free it
                if (NativeInterop.SetClipboardData(format, hGlobal) == IntPtr.Zero)
                {
                    // Failed to set this format — free the memory and skip
                    NativeInterop.GlobalFree(hGlobal);
                    Log.ForContext<ClipboardService>().Debug(
                        "Clipboard restore: skipped format {Format}", format);
                }
                else
                {
                    restoredCount++;
                }
            }

            return restoredCount > 0;
        }

        public void Dispose()
        {
            // Value-based — nothing to release
        }
    }

    /// <summary>
    /// Set clipboard text under the write lease. The one public text-write entry point —
    /// callers that used the old synchronous <c>SetClipboard</c> now await this.
    /// </summary>
    public async Task<bool> SetClipboardAsync(string text)
    {
        await _writeLease.WaitAsync().ConfigureAwait(false);
        try
        {
            return SetClipboardCoreUnderLease(text);
        }
        finally
        {
            _writeLease.Release();
        }
    }

    /// <summary>
    /// Set clipboard text. Lock-free core — call only while holding <see cref="_writeLease"/>
    /// (public callers go through <see cref="SetClipboardAsync"/>).
    /// </summary>
    private bool SetClipboardCoreUnderLease(string text)
    {
        try
        {
            if (!TryOpenClipboard())
            {
                Logger.Warning("Failed to open clipboard after retries");
                return false;
            }

            try
            {
                NativeInterop.EmptyClipboard();

                var bytes = global::System.Text.Encoding.Unicode.GetBytes(text + "\0");
                var hGlobal = NativeInterop.GlobalAlloc(NativeInterop.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (hGlobal == IntPtr.Zero)
                {
                    Logger.Warning("GlobalAlloc failed");
                    return false;
                }

                var ptr = NativeInterop.GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero)
                {
                    NativeInterop.GlobalFree(hGlobal);
                    return false;
                }

                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                NativeInterop.GlobalUnlock(hGlobal);
                return SetClipboardHandle(NativeInterop.CF_UNICODETEXT, hGlobal, "text");
            }
            finally
            {
                NativeInterop.CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to set clipboard text");
            return false;
        }
    }

    /// <summary>
    /// Get clipboard text.
    /// </summary>
    public string? GetClipboard()
    {
        try
        {
            if (!TryOpenClipboard())
                return null;

            try
            {
                return ReadUnicodeTextUnderOpenClipboard();
            }
            finally
            {
                NativeInterop.CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to get clipboard text");
            return null;
        }
    }

    /// <summary>
    /// Read CF_UNICODETEXT while the caller already holds the clipboard open. Extracted so the
    /// deferred restore's compare-and-restore can run inside ONE clipboard ownership window
    /// (read + compare + restore atomically vs other processes — IMG-BG).
    /// </summary>
    private static string? ReadUnicodeTextUnderOpenClipboard()
    {
        if (!NativeInterop.IsClipboardFormatAvailable(NativeInterop.CF_UNICODETEXT))
            return null;

        var hGlobal = NativeInterop.GetClipboardData(NativeInterop.CF_UNICODETEXT);
        if (hGlobal == IntPtr.Zero) return null;

        var ptr = NativeInterop.GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero) return null;

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            NativeInterop.GlobalUnlock(hGlobal);
        }
    }

    /// <summary>
    /// Set clipboard to a DIB image from encoded image bytes (any format WIC can decode). under the write lease.
    /// </summary>
    public async Task<bool> SetClipboardImageAsync(byte[] imageBytes)
    {
        await _writeLease.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SetClipboardImageCoreUnderLeaseAsync(imageBytes).ConfigureAwait(false);
        }
        finally
        {
            _writeLease.Release();
        }
    }

    /// <summary>
    /// Set clipboard to a DIB image from encoded image bytes (any format WIC can decode).
    /// Decodes via WIC → BITMAPINFOHEADER + pixel data → CF_DIB.
    /// Heavy work (decode, DIB conversion) runs on a background thread
    /// to avoid blocking the UI thread.
    /// Lock-free core — call only while holding <see cref="_writeLease"/>.
    /// </summary>
    private async Task<bool> SetClipboardImageCoreUnderLeaseAsync(byte[] imageBytes)
    {
        try
        {
            // Decode and convert to DIB off the UI thread — the work is CPU-bound and a large
            // image would otherwise stall the dispatcher.
            var dib = await Task.Run(
                () => Helpers.ClipboardDibConverter.ConvertAsync(imageBytes)).ConfigureAwait(false);
            var (dibBytes, width, height) = (dib.Bytes, dib.Width, dib.Height);

            // Clipboard operations must happen on the calling thread
            if (!TryOpenClipboard())
            {
                Logger.Warning("Failed to open clipboard for image after retries");
                return false;
            }

            try
            {
                NativeInterop.EmptyClipboard();

                var hGlobal = NativeInterop.GlobalAlloc(NativeInterop.GMEM_MOVEABLE, (UIntPtr)dibBytes.Length);
                if (hGlobal == IntPtr.Zero)
                {
                    Logger.Warning("GlobalAlloc failed for image");
                    return false;
                }

                var ptr = NativeInterop.GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero)
                {
                    NativeInterop.GlobalFree(hGlobal);
                    return false;
                }

                Marshal.Copy(dibBytes, 0, ptr, dibBytes.Length);
                NativeInterop.GlobalUnlock(hGlobal);
                if (!SetClipboardHandle(NativeInterop.CF_DIB, hGlobal, "image"))
                {
                    return false;
                }

                // seq= best-effort-tags this write for REL-16 probe correlation (manual —
                // a text attempt's verdict line is matched against this by hand in the log).
                Logger.Information("Image set on clipboard ({Width}x{Height}, {Size} bytes DIB, seq={Seq})",
                    width, height, dibBytes.Length, SafeClipboardSequence());
                return true;
            }
            finally
            {
                NativeInterop.CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to set clipboard image");
            return false;
        }
    }

    /// <summary>
    /// Paste an image at cursor. Sets clipboard to the image and sends Ctrl+V.
    /// Unlike text paste, the clipboard is NOT restored afterward — the generated image
    /// remains on the clipboard so the user can paste it again (Ctrl+V).
    /// </summary>
    /// <param name="focusedElement">
    /// Optional UIA-element reference borrowed from <c>MainViewModel</c>. When present,
    /// DOM-element focus is restored immediately before <c>SendCtrlV</c>, mirroring
    /// <see cref="PasteAtCursorAsync(string, IntPtr, object?)"/>.
    /// </param>
    /// <param name="proceedGate">
    /// IMG-BG: optional caller fence re-checked mid-sequence (after the clipboard write and
    /// immediately before Ctrl+V). Returns false to abort — the background job's paste spans
    /// ~1.5 s of gates, long enough for the user to start a recording; injecting Ctrl+V then
    /// would paste into their live dictation target. The image stays on the clipboard.
    /// </param>
    internal async Task<PasteResult> PasteImageAtCursorAsync(byte[] imageBytes, IntPtr targetWindow = default, object? focusedElement = null, PasteTargetSnapshot? targetSnapshot = null, Func<bool>? proceedGate = null)
    {
        Logger.Information("Pasting image at cursor ({Size} bytes), target=0x{Target:X}", imageBytes.Length, targetWindow);

        // Write lease held across set → gates → Ctrl+V: a concurrent text paste replacing the
        // clipboard mid-sequence would make this Ctrl+V paste the WRONG payload (IMG-BG).
        await _writeLease.WaitAsync().ConfigureAwait(false);
        try
        {
            return await PasteImageCoreUnderLeaseAsync(imageBytes, targetWindow, focusedElement, targetSnapshot, proceedGate).ConfigureAwait(false);
        }
        finally
        {
            _writeLease.Release();
        }
    }

    private async Task<PasteResult> PasteImageCoreUnderLeaseAsync(byte[] imageBytes, IntPtr targetWindow, object? focusedElement, PasteTargetSnapshot? targetSnapshot, Func<bool>? proceedGate)
    {
        if (!await SetClipboardImageCoreUnderLeaseAsync(imageBytes).ConfigureAwait(false))
        {
            Logger.Error("Failed to set clipboard image for paste");
            return PasteResult.Fail(PasteAttemptOutcome.ClipboardSetFailed);
        }

        if (proceedGate is { } gateAfterSet && !gateAfterSet())
        {
            Logger.Information("Image paste aborted before target work — recording started; image left on clipboard");
            return PasteResult.Fail(PasteAttemptOutcome.AbortedByRecordingStart, PasteResultPresentation.RecordingStartedMessage);
        }

        // Same liveness/identity + UIPI gates as the text path (the caller's freshness
        // gate — EvaluateImagePasteFreshness — has already run and stays authoritative;
        // these only protect the activation below). Failures degrade to the image
        // flow's clipboard-only posture — the image is ALREADY on the clipboard here.
        var validity = ValidateTargetSnapshot(targetSnapshot, targetWindow);
        if (validity is PasteTargetValidity.Gone or PasteTargetValidity.Recycled)
        {
            Logger.Warning("Image paste target 0x{Target:X} is {Validity}; image left on clipboard for manual retry", targetWindow, validity);
            return PasteResult.Fail(PasteAttemptOutcome.TargetGone, PasteResultPresentation.WindowClosedMessage);
        }
        if (targetWindow != IntPtr.Zero && targetSnapshot != null && targetSnapshot.Hwnd == targetWindow
            && ElevationGate.ShouldSkipElevatedTarget(NativeInterop.TryGetProcessElevation(targetSnapshot.Pid), SelfElevated.Value))
        {
            Logger.Warning("Image paste target 0x{Target:X} is elevated (UIPI); image left on clipboard for manual retry", targetWindow);
            return PasteResult.Fail(PasteAttemptOutcome.TargetElevated, PasteResultPresentation.ElevatedMessage);
        }

        var (acquired, _) = await EnsureTargetForegroundAsync(targetWindow, targetSnapshot, "image paste (initial)").ConfigureAwait(false);
        if (!acquired)
        {
            Logger.Warning(
                "Target window 0x{Target:X} did not regain focus before image paste; image left on clipboard for manual retry",
                targetWindow);
            return PasteResult.Fail(PasteAttemptOutcome.ForegroundAcquireFailed, PasteResultPresentation.NotInFrontMessage);
        }

        // Route resolved ONCE — reused by the UIA step and the NoEditableFocused gate.
        // Out-of-process WebView2 targets (new Teams): skip the UIA focus step — same rationale
        // as PasteAtCursorAsync (UIA SetFocus there only touches the host tree and can blur the box).
        var route = ResolvePasteRoute(targetWindow);
        if (route.Route == Helpers.PasteFocusRoute.Route.SkipUiaOopWebView2)
            Logger.Information("Image paste route: skipping UIA focus restore (out-of-process WebView2 target)");

        // PST-2b: authoritative gate BEFORE the restore — same evidence-destruction
        // rationale as the text path (no pre-paste diagnostic capture exists on the image
        // path, so this overload performs its own bounded capture). A block skips the
        // restore and maps to clipboard-only in the caller.
        //
        // PST-8: image BEHAVIOUR is deliberately unchanged — ONE shared memoized probe (so
        // the probe count matches the pre-PST-8 path exactly) and NO opaque-hybrid
        // suppression. The measured evidence is text-specific, and this path has no reusable
        // pre-paste native-focus snapshot to fingerprint from.
        var imageCapturedProbe = MemoizeCapturedProbe(focusedElement);
        var preRestoreStage = await NoEditableFocusGate.RunPreRestoreStageAsync(
            route.Route == Helpers.PasteFocusRoute.Route.SkipUiaOopWebView2,
            () => ShouldBlockNoEditableFocusedAsync(targetWindow, route, "image paste (pre-restore)"),
            () => TryRestoreUiaFocus(focusedElement),
            BuildCapturedElementRescue(focusedElement, imageCapturedProbe),
            BuildIdentityVerifiedRestore(focusedElement, imageCapturedProbe)).ConfigureAwait(false);
        if (preRestoreStage.Blocked)
        {
            if (preRestoreStage.Block == Helpers.PreRestoreBlock.FocusMovedInTarget)
                Logger.Warning(
                    "image paste: focus moved to a different element inside target 0x{Target:X} (same window, e.g. another tab) and could not be restored — blocking Ctrl+V, image kept on clipboard",
                    targetWindow);
            return MapPreRestoreBlock(preRestoreStage.Block);
        }
        if (preRestoreStage.UsedRescue)
            Logger.Information("image paste: captured-element rescue succeeded — restored recording-start editable, identity-bound drift net armed");

        // Re-verify foreground after the UIA work (it can take up to ~1s with the recapture
        // fallback) so a focus drift during it doesn't redirect Ctrl+V into the wrong app.
        if (targetWindow != IntPtr.Zero && NativeInterop.GetForegroundWindow() != targetWindow
            && !await RecoverTargetForegroundOnceAsync(targetWindow, targetSnapshot, "image paste (post-UIA)").ConfigureAwait(false))
        {
            Logger.Warning(
                "Target window 0x{Target:X} lost foreground during focus restore before image paste; image left on clipboard for manual retry",
                targetWindow);
            return PasteResult.Fail(PasteAttemptOutcome.LostForegroundPostUia, PasteResultPresentation.NotInFrontMessage);
        }

        // PST-2b drift net (see the text path): catches only a suspicious shape that
        // appeared during the restore/drift-recovery window. MUST run when the pre-restore
        // gate passed/failed open, and MUST stay before the final safety checks below (its
        // bounded waits must not open a gap between validation and SendInput). The caller
        // maps this to clipboard-only WITHOUT re-setting the clipboard (the image is
        // already there).
        if (targetWindow != IntPtr.Zero
            && await ShouldBlockNoEditableFocusedAsync(targetWindow, route, "image paste (post-restore)", rescueStage: preRestoreStage).ConfigureAwait(false))
        {
            return PasteResult.Fail(
                PasteAttemptOutcome.NoEditableFocused,
                PasteResultPresentation.NoTextBoxMessage);
        }

        // Final liveness/identity re-check immediately before the send — same recycled-HWND
        // protection as the text path (the UIA work, drift recovery, and gate above span
        // long enough for a destroy+recycle that hwnd-equality foreground checks cannot see).
        if (ValidateTargetSnapshot(targetSnapshot, targetWindow)
            is PasteTargetValidity.Gone or PasteTargetValidity.Recycled)
        {
            Logger.Warning(
                "Image paste target 0x{Target:X} vanished/recycled during focus restore; image left on clipboard for manual retry",
                targetWindow);
            return PasteResult.Fail(PasteAttemptOutcome.TargetGone, PasteResultPresentation.WindowClosedMessage);
        }

        // Post-gate foreground guard — mirrors the text path: the gate can fail open
        // after a stall, and an image pasted into whatever stole focus meanwhile would
        // be a wrong-app paste.
        if (targetWindow != IntPtr.Zero && NativeInterop.GetForegroundWindow() != targetWindow)
        {
            Logger.Warning(
                "Image paste target 0x{Target:X} lost foreground during the pre-send gate; image left on clipboard for manual retry",
                targetWindow);
            return PasteResult.Fail(PasteAttemptOutcome.LostForegroundPostUia, PasteResultPresentation.NotInFrontMessage);
        }

        // IMG-BG final fence, immediately before the send: the foreground checks above can't
        // see a recording that keeps the SAME target foreground (the pill never steals focus).
        if (proceedGate is { } gateBeforeSend && !gateBeforeSend())
        {
            Logger.Information("Image paste aborted before Ctrl+V — recording started; image left on clipboard");
            return PasteResult.Fail(PasteAttemptOutcome.AbortedByRecordingStart, PasteResultPresentation.RecordingStartedMessage);
        }

        // The gate rides INTO the sender too — its bounded modifier wait (400 ms) is long
        // enough for the recording-hotkey press to start a recording (Codex diff round 3).
        // A false return is disambiguated by re-reading the gate: tripped ⇒ aborted-by-
        // recording (image safely on clipboard), else a genuine SendInput failure.
        if (!await SendCtrlVModifierSafeAsync(proceedGate).ConfigureAwait(false))
        {
            if (proceedGate is { } gateAfterSendAttempt && !gateAfterSendAttempt())
            {
                Logger.Information("Image paste aborted during the modifier wait — recording started; image left on clipboard");
                return PasteResult.Fail(PasteAttemptOutcome.AbortedByRecordingStart, PasteResultPresentation.RecordingStartedMessage);
            }
            Logger.Warning("Failed to send Ctrl+V for image paste; image left on clipboard for manual retry");
            return PasteResult.Fail(PasteAttemptOutcome.SendInputFailed, PasteResultPresentation.KeystrokeBlockedMessage);
        }

        Logger.Debug("Image pasted — clipboard kept (no restore) so user can re-paste");
        return PasteResult.Success();
    }

    /// <summary>
    /// Restore DOM-element focus inside the (now-foreground) target before SendCtrlV.
    /// Returns true when a live DOM editable is believed focused. On a stale captured element
    /// (SetFocus → UIA_E_ELEMENTNOTAVAILABLE 0x80040201), falls back to re-capturing whatever
    /// currently has focus in the target and focusing that — recovers the Chromium case where
    /// the editable regained focus on window activation but the recording-start RCW was
    /// invalidated by a DOM re-render. Returns false for non-UIA targets (focusedElement == null);
    /// the caller MUST still send Ctrl+V in that case — Win32 foreground focus is already correct,
    /// so gating the paste on UIA success would break Win32 edit controls (Notepad, Office, terminals).
    /// <para>PST-2b: callers MUST run the NoEditableFocused pre-restore gate first (via
    /// <see cref="Helpers.NoEditableFocusGate.RunPreRestoreStageAsync"/>) — SetFocus on a
    /// non-editable page element moves Chromium keyboard focus into the renderer child,
    /// manufacturing the gate's success shape and erasing the loss evidence.</para>
    /// </summary>
    private static bool TryRestoreUiaFocus(object? focusedElement)
    {
        if (focusedElement is not Helpers.UiaFocusBridge.IUIAutomationElement uiaElement)
            return false;

        if (Helpers.UiaFocusBridge.TryRestoreFocus(uiaElement))
        {
            Logger.Information("UIA focus restore: success");
            return true;
        }

        var refreshed = Helpers.UiaFocusBridge.TryRefocusCurrentElement();
        Logger.Information("UIA focus restore: failed; fresh-recapture {Result}",
            refreshed ? "succeeded" : "failed");
        return refreshed;
    }

    /// <summary>
    /// PST-3 rescue restore: the DIRECT SetFocus path ONLY — structurally never
    /// the fresh-recapture fallback of <see cref="TryRestoreUiaFocus"/>.
    /// Recapturing inside the rescue would focus whatever non-editable element
    /// currently holds focus and manufacture the gate's success shape (the
    /// PST-2b evidence-destruction scenario the rescue must not reopen).
    /// </summary>
    /// <param name="candidate">
    /// PST-11: WHICH candidate is being restored — <c>"capture"</c> for the element this attempt
    /// captured, <c>"retained"</c> for the recording's element offered as the fallback.
    ///
    /// <para>One flags-only token, added because this card was diagnosed end-to-end from the
    /// owner's logs and without it the two rescues read identically (Kimi verification round). The
    /// question it answers — "did PST-11 actually fire in the field?" — is otherwise unanswerable
    /// from a support bundle, which is how the original defect was found in the first place.</para>
    /// </param>
    private static bool TryRestoreUiaFocusDirect(
        Helpers.UiaFocusBridge.IUIAutomationElement element, string candidate)
    {
        var ok = Helpers.UiaFocusBridge.TryRestoreFocus(element);
        Logger.Information(
            "UIA focus restore (rescue, direct-only, candidate={Candidate}): {Result}",
            candidate, ok ? "success" : "failed");
        return ok;
    }

    /// <summary>
    /// PST-3: rescue delegates for the pre-restore gate, or null when no UIA
    /// element was captured at recording start (the rescue then never engages
    /// and a block stands as before). Shape/restore run on the main UIA worker
    /// (the captured RCW's home apartment); the live verification runs on the
    /// probe worker. A normally-completing captured-shape probe releases the
    /// worker's busy gate before the restore is enqueued (worker clears _busy
    /// in the item's finally, before completing the TCS); a timed-out straggler
    /// makes the restore fast-fail → the rescue degrades to the block.
    /// </summary>
    private static Helpers.CapturedElementRescue? BuildCapturedElementRescue(
        object? focusedElement,
        Func<Helpers.CapturedElementProbe> capturedProbe,
        object? fallbackFocusedElement = null)
    {
        var fallback = BuildRescueFallback(focusedElement, fallbackFocusedElement);

        if (focusedElement is not Helpers.UiaFocusBridge.IUIAutomationElement element)
        {
            // PST-11: with no first element there is no rescue to extend, and the retained element
            // is deliberately NOT promoted to first candidate — an intentional safe degradation to
            // today's block, not an oversight (Codex diff round 1 asked for this to be pinned
            // either way).
            //
            // Why not promote it: a null capture means the redo learned NOTHING about focus at
            // picker-open time. The retained element then becomes the only evidence in play, and
            // it is evidence about a moment that has already passed — the window guard in
            // RedoFocusRetention.ElementFor proves the WINDOW still matches, but nothing proves
            // the user has not since moved to a different field inside it. Restoring focus on
            // that basis is a wrong-field paste in the right window, which is precisely the class
            // PST-4 and PST-7 exist to refuse.
            //
            // The cost is a missed rescue on a path that is already the unusual one (the capture
            // itself failed), and the outcome there is the pre-PST-11 decline with the text on the
            // clipboard — the same answer the user gets today.
            return null;
        }

        return new Helpers.CapturedElementRescue(
            capturedProbe,
            () => TryRestoreUiaFocusDirect(element, "capture"),
            Helpers.UiaFocusBridge.TryGetFocusedElementShapeWithIdentity,
            fallback);
    }

    /// <summary>
    /// PST-11: the retained recording element as a second rescue candidate, or null.
    ///
    /// <para>Its probe is memoized SEPARATELY from the first candidate's — they are different
    /// elements, so one memo cannot answer for both — which keeps the "one bounded probe per
    /// element per attempt" property PST-8 established, now with at most two.</para>
    ///
    /// <para>Null when the two references are the same object: offering an element as its own
    /// fallback would spend a second cross-process probe to re-learn the answer that just sent us
    /// down this path.</para>
    /// </summary>
    private static Helpers.RescueCandidate? BuildRescueFallback(
        object? focusedElement, object? fallbackFocusedElement)
    {
        if (fallbackFocusedElement is not Helpers.UiaFocusBridge.IUIAutomationElement fallbackElement
            || ReferenceEquals(focusedElement, fallbackFocusedElement))
            return null;

        return new Helpers.RescueCandidate(
            MemoizeCapturedProbe(fallbackElement),
            () => TryRestoreUiaFocusDirect(fallbackElement, "retained"));
    }

    /// <summary>
    /// PST-8: one bounded typed captured probe per paste attempt, resolved on first use and
    /// reused by the opaque-hybrid suppression decision AND both delegate builders. The
    /// probe is the expensive part (a bounded cross-process UIA call), and the pre-PST-8
    /// code path performed exactly one per attempt — memoizing preserves that while letting
    /// the suppression decision consult it. Not thread-safe by design: a paste attempt is
    /// sequential and holds the write lease.
    /// </summary>
    private static Func<Helpers.CapturedElementProbe> MemoizeCapturedProbe(object? focusedElement)
        => Memoize(() => focusedElement is Helpers.UiaFocusBridge.IUIAutomationElement element
            ? Helpers.UiaFocusBridge.ProbeCapturedElement(element)
            : new Helpers.CapturedElementProbe(Helpers.CapturedProbeKind.ProbeUnavailable, null, null));

    /// <summary>
    /// Resolve-once wrapper, kept separate from the COM call so the "exactly one probe per
    /// attempt" claim is unit-testable (the COM half cannot be). Deliberately not
    /// thread-safe: a paste attempt is sequential and holds the write lease.
    /// </summary>
    internal static Func<T> Memoize<T>(Func<T> source) where T : struct
    {
        T? cached = null;
        return () => cached ??= source();
    }

    /// <summary>PST-8: target process name for the app-identity condition, or null when it
    /// cannot be resolved (exited / access denied) — which never matches, so an unresolvable
    /// process keeps today's paste path.</summary>
    private static string? TryGetProcessName(uint pid)
    {
        if (pid == 0) return null;
        try
        {
            using var p = global::System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    /// <summary>
    /// PST-8: does the FOCUSED window provably belong to the same process as the paste
    /// target? Without this, "focus is on a Chromium child" is an unverified pairing — the
    /// focus HWND could belong to another process entirely (Codex plan round 2). Cheap:
    /// one native call, no COM. Unresolvable PIDs (0) never match.
    /// </summary>
    private static bool FocusBelongsToTarget(FocusInfo focus)
    {
        if (focus.FocusHwnd == IntPtr.Zero || focus.ProcessId == 0)
            return false;
        NativeInterop.GetWindowThreadProcessId(focus.FocusHwnd, out var focusPid);
        return focusPid != 0 && focusPid == focus.ProcessId;
    }

    /// <summary>
    /// PST-4: delegates for the identity-verified restore on the non-blocked
    /// path ("paste where intended, or nowhere"), or null when no UIA element
    /// was captured — the legacy restore then runs unchanged. The recapture
    /// delegate is the legacy fresh-recapture fallback; the pure sequence
    /// invokes it ONLY on a certified-dead captured element (stale-RCW
    /// signature from the probe or from SetFocus) — an ALIVE captured element
    /// (Windows Terminal's hidden-tab TermControl) can never be silently
    /// swapped for whatever the user focused since.
    /// </summary>
    private static Helpers.IdentityVerifiedRestore? BuildIdentityVerifiedRestore(
        object? focusedElement, Func<Helpers.CapturedElementProbe> capturedProbe)
    {
        if (focusedElement is not Helpers.UiaFocusBridge.IUIAutomationElement element)
            return null;

        return new Helpers.IdentityVerifiedRestore(
            capturedProbe,
            Helpers.UiaFocusBridge.TryGetFocusedElementShapeWithIdentity,
            () =>
            {
                var result = Helpers.UiaFocusBridge.TryRestoreFocusTyped(element);
                Logger.Information("UIA focus restore (identity-verified, direct): {Result}", result);
                return result;
            },
            () =>
            {
                var refreshed = Helpers.UiaFocusBridge.TryRefocusCurrentElement();
                Logger.Information("UIA focus restore: captured element dead; fresh-recapture {Result}",
                    refreshed ? "succeeded" : "failed");
                return refreshed;
            });
    }

    /// <summary>Maps a pre-restore stage block to the paste outcome + pill message
    /// (content-kind-agnostic — the shared decline family in
    /// <see cref="PasteResultPresentation"/>).</summary>
    private static PasteResult MapPreRestoreBlock(Helpers.PreRestoreBlock block)
        => block == Helpers.PreRestoreBlock.FocusMovedInTarget
            ? PasteResult.Fail(
                PasteAttemptOutcome.FocusMovedInTarget,
                PasteResultPresentation.FocusMovedMessage)
            : PasteResult.Fail(
                PasteAttemptOutcome.NoEditableFocused,
                PasteResultPresentation.NoTextBoxMessage);

    /// <summary>
    /// PST-6 scope gate: delivery verification runs for CONFIDENTLY-normal
    /// UIA routes into Chromium-shaped targets only. Everything else (Win32 editors,
    /// Office, terminals, the OOP-WebView2 route) keeps pre-PST-6 behavior — their
    /// accessibility values are not reliably live, and a false "didn't appear" verdict
    /// on a paste that landed would be a new bug in exchange for a fix they don't need.
    /// </summary>
    private static bool ShouldVerifyDelivery(
        IntPtr targetWindow, (Helpers.PasteFocusRoute.Route Route, bool Confident) route)
        => targetWindow != IntPtr.Zero
           && route.Confident
           && route.Route == Helpers.PasteFocusRoute.Route.NormalUiaRestore
           && NoEditableFocusGate.IsChromiumTopLevelClass(GetWindowClassName(targetWindow));

    /// <summary>
    /// Per-attempt verification context. The clipboard callback is a closure over state
    /// THIS attempt owns while holding the write lease — the verifier never resolves or
    /// calls this service (the lease is non-reentrant, so a public-entry call from inside
    /// would deadlock), so the rewrite goes to the lock-free core.
    /// <para>PST-16 removed the escalation rungs, and with them every send/target callback
    /// this context used to carry: nothing downstream of the caller's own Ctrl+V dispatches
    /// input any more, so there is nothing left to re-target or re-authorise.</para>
    /// </summary>
    private PasteDeliveryContext BuildDeliveryContext(
        string text, Helpers.PasteTextReadback? baseline, bool proofRequired)
        => new(
            PastedText: text,
            Before: baseline,
            RewriteClipboardUnderLease: SetClipboardCoreUnderLease,
            ProofRequired: proofRequired);

    /// <summary>
    /// PST-16: one non-Delivered outcome, so one decline string — unless the clipboard
    /// postcondition itself failed, which outranks it.
    /// </summary>
    private static string DeliveryDeclineMessage(PasteDeliveryReport report)
        => report.ClipboardAsserted
            ? PasteResultPresentation.DeliveryUncertainMessage
            : PasteResultPresentation.ClipboardFailedMessage;

    internal Task<ClipboardSnapshot?> CaptureSnapshotAsync()
    {
        return RunOnMainDispatcherAsync(CaptureSnapshotCore);
    }

    internal async Task<bool> RestoreSnapshotAsync(ClipboardSnapshot snapshot)
    {
        try
        {
            return await RunOnMainDispatcherAsync(() => RestoreSnapshotCore(snapshot)).ConfigureAwait(false);
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    /// <summary>
    /// Paste text at cursor.
    /// Saves clipboard → sets text → sends Ctrl+V → restores original clipboard after delay.
    /// Clipboard restore only applies to text paste, not image paste (images stay on clipboard for re-use).
    /// </summary>
    /// <param name="focusedElement">
    /// Optional UIA-element reference (typed <c>object</c> here so this service does not
    /// import COM types — see <see cref="Helpers.UiaFocusBridge.IUIAutomationElement"/>).
    /// When non-null, <see cref="Helpers.UiaFocusBridge.TryRestoreFocus"/> is invoked
    /// after <c>ForceForegroundWindow</c> and immediately before <c>SendCtrlV</c>, so
    /// DOM-element focus inside Chromium-based targets (Electron apps) is restored to
    /// the element that had focus at recording-start. Caller retains ownership of the
    /// RCW; this method never releases. Failure is non-fatal — paste falls through to
    /// the existing <c>SendCtrlV</c> behavior.
    /// </param>
    /// <param name="targetCapturedAtUtc">
    /// When the <paramref name="targetWindow"/> / <paramref name="focusedElement"/> were captured
    /// (recording-start). Diagnostic only — logged as the paste-attempt "ageMs" so a stale-capture
    /// miss is visible from the summary line. <c>default</c> suppresses the age field.
    /// </param>
    /// <param name="targetSnapshot">
    /// Optional identity snapshot of <paramref name="targetWindow"/> captured when the target
    /// was (hwnd/pid/tid, class enriched async). Enables the liveness/identity gate — a closed
    /// or recycled target fails fast instead of being force-activated. Self-disarms when the
    /// snapshot's hwnd differs from <paramref name="targetWindow"/> (redo edge cases) or when
    /// null (paste-last, external callers).
    /// </param>
    /// <param name="probeSink">
    /// REL-16 diagnostic: attempt-scoped clipboard probe collector. When the caller passes one
    /// (the send-Enter funnel, which appends its own pre-enter endpoint), the CALLER owns
    /// emission; when null, a fresh attempt is created and emitted here after the lease is
    /// released. Emission is idempotent either way.
    /// </param>
    /// <param name="fallbackFocusedElement">
    /// PST-11: a SECOND element for the captured-element rescue, tried only when
    /// <paramref name="focusedElement"/> is not alive-and-editable and only on the already-blocked
    /// path. Today's one caller is the redo, which passes the element the RECORDING pasted into —
    /// its own capture is taken at picker-OPEN time, by which point a send-Enter has typically
    /// blurred the composer. Null everywhere else, which leaves the rescue exactly as it was.
    /// </param>
    internal async Task<PasteResult> PasteAtCursorAsync(string text, IntPtr targetWindow = default, object? focusedElement = null, DateTime targetCapturedAtUtc = default, PasteTargetSnapshot? targetSnapshot = null, ClipboardProbeAttempt? probeSink = null, object? fallbackFocusedElement = null)
    {
        Logger.Information("Pasting {Length} chars at cursor, target=0x{Target:X}", text.Length, targetWindow);

        var probeAttempt = probeSink ?? new ClipboardProbeAttempt();
        var ownsEmit = probeSink is null;

        // Write lease held across capture → set → gates → Ctrl+V (see PasteImageAtCursorAsync).
        await _writeLease.WaitAsync().ConfigureAwait(false);
        try
        {
            return await PasteTextCoreUnderLeaseAsync(text, targetWindow, focusedElement, targetCapturedAtUtc, targetSnapshot, probeAttempt, fallbackFocusedElement).ConfigureAwait(false);
        }
        finally
        {
            _writeLease.Release();
            // AFTER the release — the emit's process-name lookup and logging must never
            // extend the lease hold (that would delay a competing clipboard writer at
            // exactly the boundary this diagnostic studies).
            if (ownsEmit)
                EmitClipboardProbes(probeAttempt);
        }
    }

    private async Task<PasteResult> PasteTextCoreUnderLeaseAsync(string text, IntPtr targetWindow, object? focusedElement, DateTime targetCapturedAtUtc, PasteTargetSnapshot? targetSnapshot, ClipboardProbeAttempt probeAttempt, object? fallbackFocusedElement = null)
    {
        var shouldRestore = _settings.GetBool(AppDefaults.RestoreClipboardAfterPaste, false);
        ClipboardSnapshot? savedClipboard = null;

        if (shouldRestore)
        {
            savedClipboard = await CaptureSnapshotAsync().ConfigureAwait(false);
            if (savedClipboard == null)
            {
                Logger.Warning("Clipboard backup failed; original clipboard contents may not be restorable");
            }
        }

        // The clipboard is set BEFORE any target gate below, so every gated exit keeps the
        // "text left on clipboard for manual Ctrl+V" guarantee.
        if (!SetClipboardCoreUnderLease(text))
        {
            Logger.Error("Failed to set clipboard for paste");
            if (savedClipboard != null)
            {
                try
                {
                    await RestoreSnapshotAsync(savedClipboard).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to restore clipboard after paste setup failure");
                }
            }
            LogAttemptSummary(PasteAttemptOutcome.ClipboardSetFailed, targetCapturedAtUtc, "n/a", focusedElement != null, false, "n/a");
            // Truthful message: on this path the text did NOT make it onto the clipboard.
            // The SHARED constant, not a local string: the redo copy-only path reports the same
            // failure, and the two read differently until the 2026-07-25 copy review (this site
            // said "Couldn't access the clipboard — nothing was pasted", which is also wrong for
            // the copy-only path, where no paste was ever intended).
            return PasteResult.Fail(PasteAttemptOutcome.ClipboardSetFailed, PasteResultPresentation.ClipboardFailedMessage);
        }

        // REL-16: the after-set endpoint — its seq also best-effort-tags our own text write.
        // Bounded managed collection + open-free syscalls only; NO logging or process
        // lookup under the lease.
        probeAttempt.Add(CaptureClipboardProbe(ClipboardProbePhase.AfterSet));

        // Exactly-one-summary bookkeeping: every exit below funnels through the finally.
        var outcome = PasteAttemptOutcome.SendInputFailed;
        var routeLabel = "n/a";
        var uiaRestored = false;
        var escalation = "n/a";

        // Ensure the snapshot is restored on all early-return paths below.
        // On the happy path the snapshot is handed off to RestoreSnapshotAfterDelayAsync,
        // which owns disposal via RestoreSnapshotAsync → finally { snapshot.Dispose() }.
        try
        {
            // Liveness/identity gate: never force-activate a destroyed or recycled handle
            // (a recycled HWND could pass every foreground check and paste into the wrong app).
            var validity = ValidateTargetSnapshot(targetSnapshot, targetWindow);
            if (validity is PasteTargetValidity.Gone or PasteTargetValidity.Recycled)
            {
                Logger.Warning(
                    "Paste target 0x{Target:X} is {Validity} (captured pid={Pid} tid={Tid}); transcription left on clipboard for manual Ctrl+V",
                    targetWindow, validity, targetSnapshot!.Pid, targetSnapshot.ThreadId);
                outcome = PasteAttemptOutcome.TargetGone;
                return PasteResult.Fail(outcome, PasteResultPresentation.WindowClosedMessage);
            }

            // UIPI gate: a non-elevated process cannot inject input into an elevated target and
            // Windows gives NO error signal. Detect, fail fast, and say so. Unknown ⇒ fail open.
            if (targetWindow != IntPtr.Zero && targetSnapshot != null && targetSnapshot.Hwnd == targetWindow)
            {
                var targetElevated = NativeInterop.TryGetProcessElevation(targetSnapshot.Pid);
                if (ElevationGate.ShouldSkipElevatedTarget(targetElevated, SelfElevated.Value))
                {
                    Logger.Warning(
                        "Paste target 0x{Target:X} (pid={Pid}) is elevated and VoiceWink is not — UIPI blocks synthetic input; transcription left on clipboard",
                        targetWindow, targetSnapshot.Pid);
                    outcome = PasteAttemptOutcome.TargetElevated;
                    return PasteResult.Fail(outcome, PasteResultPresentation.ElevatedMessage);
                }
            }

            // Acquire the target foreground (attach+SetForegroundWindow, then the bounded
            // escalation ladder where the single-shot attempt used to bail outright).
            var (acquired, escalationLabel) = await EnsureTargetForegroundAsync(targetWindow, targetSnapshot, "text paste (initial)").ConfigureAwait(false);
            escalation = escalationLabel;
            if (!acquired)
            {
                Logger.Warning(
                    "Target window 0x{Target:X} did not regain focus before text paste; transcription left on clipboard for manual Ctrl+V",
                    targetWindow);
                outcome = PasteAttemptOutcome.ForegroundAcquireFailed;
                return PasteResult.Fail(outcome, PasteResultPresentation.NotInFrontMessage);
            }

            // PST-2b: the capture below is REUSED by the pre-restore gate. Null means the
            // bounded capture was already attempted and failed open — the gate must then
            // fail open too, NOT recapture (a wedged target must not pay the 250 ms budget
            // twice on the same paste).
            var prePasteFocus = await LogPrePasteStateAsync(targetWindow, text).ConfigureAwait(false);

            // Decide the focus-restore route (ONCE — reused by the UIA step and the
            // NoEditableFocused gate below). Out-of-process WebView2 hosts (new Microsoft
            // Teams) render the editable in a separate process; our UIA SetFocus there only
            // touches the host UIA tree and can blur the already-focused DOM box, so for
            // those we skip UIA entirely and paste into the box that already holds DOM focus.
            var route = ResolvePasteRoute(targetWindow);
            routeLabel = route.Route.ToString();
            if (route.Route == Helpers.PasteFocusRoute.Route.SkipUiaOopWebView2)
                Logger.Information("Paste route: skipping UIA focus restore (out-of-process WebView2 target)");

            // PST-2b: the gate's authoritative evaluation happens HERE, before the UIA
            // restore — the restore (SetFocus on any page element, editable or not)
            // converts top-level Chromium focus into renderer-child focus, manufacturing
            // the success shape the post-restore gate cannot distinguish (2026-07-06
            // 18:20–18:21: three pastes vanished that way). A block skips the restore so
            // the evidence survives and an immediate retry blocks identically.
            //
            // Restore rationale (unchanged): Win32 SetForegroundWindow brings the OS
            // window forward but cannot tell a Chromium renderer (Electron apps like
            // Claude Desktop) which DOM editable to focus. UIA can. Failure is non-fatal —
            // fall through to existing SendCtrlV behavior (correct for non-UIA Win32
            // targets, whose focus is already restored by SetForegroundWindow).
            // PST-8: ONE memoized typed captured probe for this attempt, shared by the
            // suppression decision below and BOTH delegate builders — so the probe count is
            // exactly what it was before (pinned by call-count tests).
            var capturedProbe = MemoizeCapturedProbe(focusedElement);

            // PST-8: suppress our own UIA focus restore on a UIA-OPAQUE HYBRID (measured:
            // WhatsApp Desktop — WinUI top level hosting an offscreen Chromium widget whose
            // hidden child holds Win32 focus). There the restore is the FAILURE: UIA sees
            // only a bare Pane, SetFocus moves focus to the WinUI bridge, and Ctrl+V lands
            // nowhere. Evaluated in two stages so the captured probe stays LAZY — the cheap
            // native prefilter runs first, so the WebView2 skip route still performs no
            // captured probe at all.
            // Decided from NATIVE signals only — deliberately no accessibility call. Live UAT
            // proved the probe-confirmed design self-defeating: WhatsApp's captured probe timed
            // out at its 500 ms bound and the straggler answered 17 s later, jamming the shared
            // UIA worker so the SetFocus fast-failed and the paste declined anyway. The target
            // this rule exists for cannot answer the question that was meant to authorise it.
            var suppressRestore = false;
            if (prePasteFocus is { } nativeFocus
                && NoEditableFocusGate.IsOpaqueHybridCandidate(
                    route.Confident, route.Route, nativeFocus.TargetClass, nativeFocus.FocusClass,
                    FocusBelongsToTarget(nativeFocus),
                    () => TryGetProcessName(nativeFocus.ProcessId)))
            {
                suppressRestore = true;
                Logger.Information(
                    "text paste: suppressing UIA focus restore — {TargetClass} target with {FocusClass} focus " +
                    "in the target process (UIA-opaque; app keeps its own focus)",
                    nativeFocus.TargetClass, nativeFocus.FocusClass);
            }

            var preRestoreStage = await NoEditableFocusGate.RunPreRestoreStageAsync(
                route.Route == Helpers.PasteFocusRoute.Route.SkipUiaOopWebView2 || suppressRestore,
                () => ShouldBlockNoEditableFocusedAsync(targetWindow, route, "text paste (pre-restore)", prePasteFocus),
                () => TryRestoreUiaFocus(focusedElement),
                BuildCapturedElementRescue(focusedElement, capturedProbe, fallbackFocusedElement),
                BuildIdentityVerifiedRestore(focusedElement, capturedProbe),
                // PST-6 live-shape fallback (TEXT path only — the image path stays
                // byte-identical this wave). Consulted solely when the rescue could
                // not engage: a certified-DEAD capture (Chromium re-rendered the
                // composer — the 2026-07-29 19:38:35 Claude Desktop miss) or the SAME
                // element having hydrated into an editable shape. Probe-only: no
                // SetFocus, no recapture, so PST-2b's evidence survives a refusal.
                Helpers.UiaFocusBridge.TryGetFocusedElementShapeWithIdentity,
                // PST-7 post-restore identity proof — TEXT path only, by design. The image
                // call site passes nothing, so RunIdentityVerifiedRestore performs no extra
                // probe there and image latency / worker contention are unchanged (Codex plan
                // round 1: that function is shared by both paths).
                Helpers.UiaFocusBridge.TryGetFocusedElementShapeWithIdentity).ConfigureAwait(false);
            if (preRestoreStage.Blocked)
            {
                var mapped = MapPreRestoreBlock(preRestoreStage.Block);
                outcome = mapped.Outcome;
                if (preRestoreStage.Block == Helpers.PreRestoreBlock.FocusMovedInTarget)
                    Logger.Warning(
                        "text paste: focus moved to a different element inside target 0x{Target:X} (same window, e.g. another tab) and could not be restored — blocking Ctrl+V, text kept on clipboard",
                        targetWindow);
                return mapped;
            }
            uiaRestored = preRestoreStage.UiaRestored;
            if (preRestoreStage.Kind != Helpers.PreRestorePassKind.None)
                Logger.Information(
                    "text paste: pre-restore gate passed via {PassKind} — identity-bound drift net armed",
                    preRestoreStage.Kind);

            // Re-verify foreground AFTER the UIA work (which can take up to ~500ms, or ~1s with
            // the recapture fallback). If focus drifted to another window during that window,
            // bail rather than fire Ctrl+V into the wrong app — the text stays on the clipboard
            // for a manual paste. (The earlier recheck was before the UIA work, so it can't see
            // a drift that happens during it.)
            if (targetWindow != IntPtr.Zero && NativeInterop.GetForegroundWindow() != targetWindow
                && !await RecoverTargetForegroundOnceAsync(targetWindow, targetSnapshot, "text paste (post-UIA)").ConfigureAwait(false))
            {
                Logger.Warning(
                    "Target window 0x{Target:X} lost foreground during focus restore; transcription left on clipboard for manual Ctrl+V",
                    targetWindow);
                outcome = PasteAttemptOutcome.LostForegroundPostUia;
                return PasteResult.Fail(outcome, PasteResultPresentation.NotInFrontMessage);
            }

            // PST-2b drift net: the authoritative gate already ran pre-restore. This
            // second evaluation (fresh capture) only catches a suspicious shape that
            // APPEARED during the restore/drift-recovery window — it cannot see the
            // original loss family (the restore converts it to renderer-child focus).
            // ORDERING INVARIANTS (call-site-level, review-guarded): this gate MUST run
            // when the pre-restore gate passed or failed open, and it MUST stay before
            // the final liveness/foreground checks below — its bounded waits (250 ms
            // focus snapshot + up to 500 ms UIA probe) must not open a gap between
            // "final" validation and SendInput.
            if (targetWindow != IntPtr.Zero
                && await ShouldBlockNoEditableFocusedAsync(targetWindow, route, "text paste (post-restore)", rescueStage: preRestoreStage).ConfigureAwait(false))
            {
                outcome = PasteAttemptOutcome.NoEditableFocused;
                return PasteResult.Fail(outcome, PasteResultPresentation.NoTextBoxMessage);
            }

            // PST-6 send sequencing. On a VERIFIED path the modifier wait moves AHEAD of
            // the baseline + final checks, so nothing awaits between the last check and
            // SendInput (Codex diff review round 5): SendCtrlVModifierSafeAsync's bounded
            // 400 ms wait sat there, and a switch to another field INSIDE the same
            // Chromium window is invisible to the HWND-equality checks — the paste would
            // land in the wrong field, and rung 2 could then also paste into the right
            // one, leaving the dictation in both. Unverified paths keep the shipped
            // ordering byte-for-byte.
            var willVerify = ShouldVerifyDelivery(targetWindow, route);
            // PST-8: the suppression route joins the pre-waited/synchronous send path. WinUI
            // targets never enter ShouldVerifyDelivery, so they would otherwise take the
            // unverified path whose 400 ms modifier wait sits BETWEEN the identity check and
            // SendInput — leaving exactly the same-window drift window the suppression's
            // identity check exists to close (Codex diff review round 2).
            // PST-7: any path carrying a PROVEN element identity joins the pre-waited /
            // synchronous send too — that is the whole fix. The identity was proven by a probe
            // taken after the restore's last focus-changing call, so re-checking it after the
            // modifier wait catches a same-window field switch that the HWND-equality checks
            // below structurally cannot see. Paths with no proven identity keep the shipped
            // ordering byte-for-byte.
            var priorIdentity = preRestoreStage.VerifiedRuntimeId;
            var identityGuardApplicable = priorIdentity is { Length: > 0 };
            var preWaitModifiers = Helpers.PreSendIdentityCoordinator.RequiresPreWaitedModifiers(
                willVerify, suppressRestore, identityGuardApplicable);

            // The insertion-verification BASELINE. Taken before the final liveness/
            // foreground checks so those remain the last thing between us and SendInput
            // (Codex plan round 2: a bounded UIA read placed after them would reopen the
            // drift window they exist to close). Null (non-Chromium target, unreadable,
            // password field) simply means no verification: the paste keeps its pre-PST-6
            // semantics end to end.
            //
            // The baseline is read INSIDE the sequence's prepare step (after the modifier
            // wait) so its runtime id can double as PST-7's late identity — a full shape read
            // costs up to 500 ms, and paying it twice for the same answer is waste.
            Helpers.PasteTextReadback? deliveryBaseline = null;

            // One Information line per attempt, and the four outcomes SUM to total attempts so
            // UAT can compute real PST-7 coverage rather than infer it (both gap branches are
            // deliberately visible — an unreadable or never-proven identity means the
            // wrong-field window stays open on that attempt). BOTH paths below route through
            // this single template.
            void LogIdentityOutcome(Helpers.PreSendIdentityResult r) => Logger.Information(
                "text paste: pre-send identity check {IdentityOutcome} (source={IdentitySource})",
                r.Outcome, r.Source);

            // The three final checks exist ONCE and serve BOTH paths below (ordering seam,
            // 2026-07-30). Their bodies are the shipped ones verbatim — same log templates,
            // same outcomes, same presentations.
            //
            // checkFallbackBaseline — a PST-6 fallback pass overturned the gate ON THE PROMISE
            // that delivery would be verified. No baseline ⇒ no verification ⇒ the promise
            // cannot be kept, and proceeding would be strictly worse than the block it
            // replaced (unverified paste + success claim + Enter, vs. an honest "press
            // Ctrl+V"). Restore the original block. The PST-3 rescue is excluded — it carries
            // its own fail-closed live verification (Codex diff review round 3). "Non-null" is
            // NOT the bar (round 4): a capped baseline cannot prove non-insertion, and a
            // missing/mismatched runtime ID makes every later comparison Unknown — both of
            // which the ladder would fail OPEN into a success claim.
            Func<PasteResult?> checkFallbackBaseline = () =>
            {
                if (!preRestoreStage.RequiresDeliveryVerification
                    || Helpers.PasteInsertionVerification.IsUsableFallbackBaseline(
                        deliveryBaseline, preRestoreStage.VerifiedRuntimeId))
                    return null;
                Logger.Warning(
                    "text paste: {PassKind} pass cannot be verified (baseline present={Present} capped={Capped} identityMatch={Match}) — restoring the no-editable block, text kept on clipboard",
                    preRestoreStage.Kind,
                    deliveryBaseline.HasValue,
                    deliveryBaseline?.CapHit,
                    NoEditableFocusGate.RuntimeIdsEqual(
                        deliveryBaseline?.RuntimeId, preRestoreStage.VerifiedRuntimeId));
                return PasteResult.Fail(PasteAttemptOutcome.NoEditableFocused, PasteResultPresentation.NoTextBoxMessage);
            };

            // checkTargetAlive — final liveness/identity re-check immediately before the send:
            // the UIA work, drift recovery, and gate above can take ~1.5 s — long enough for a
            // destroy+recycle that the hwnd-equality foreground checks cannot see.
            Func<PasteResult?> checkTargetAlive = () =>
            {
                if (ValidateTargetSnapshot(targetSnapshot, targetWindow)
                    is not (PasteTargetValidity.Gone or PasteTargetValidity.Recycled))
                    return null;
                Logger.Warning(
                    "Paste target 0x{Target:X} vanished/recycled during focus restore; transcription left on clipboard for manual Ctrl+V",
                    targetWindow);
                return PasteResult.Fail(PasteAttemptOutcome.TargetGone, PasteResultPresentation.WindowClosedMessage);
            };

            Func<PasteResult?> checkForeground = () =>
            {
                if (targetWindow == IntPtr.Zero || NativeInterop.GetForegroundWindow() == targetWindow)
                    return null;
                Logger.Warning(
                    "Target window 0x{Target:X} lost foreground during the pre-send gate; transcription left on clipboard for manual Ctrl+V",
                    targetWindow);
                return PasteResult.Fail(PasteAttemptOutcome.LostForegroundPostUia, PasteResultPresentation.NotInFrontMessage);
            };

            if (preWaitModifiers)
            {
                // ORDERING SEAM (2026-07-30, closes Codex PST-7 diff round 2's open gap): the
                // whole pre-waited sequence — prepare → identity decision → log → block? →
                // fallback baseline → target alive → foreground → SYNCHRONOUS send — runs
                // inside the coordinator's non-async tail, where the compiler forbids an
                // await between the decision and the keystroke. Named arguments are
                // load-bearing for the three same-typed checks (review-pinned order), and the
                // send closure must stay a single direct SendCtrlVWithPrefix expression.
                var sequence = await Helpers.PreSendIdentityCoordinator.RunPreWaitedSequenceAsync(
                    prepare: async () =>
                    {
                        var prefix = await BuildModifierReleasePrefixAsync().ConfigureAwait(false);
                        if (willVerify)
                            deliveryBaseline = Helpers.UiaFocusBridge.TryGetFocusedElementTextReadback();
                        return prefix;
                    },
                    guardApplicable: identityGuardApplicable,
                    priorIdentity: priorIdentity,
                    usableBaselineIdentity: () => deliveryBaseline?.RuntimeId,
                    probeIdentity: () => Helpers.UiaFocusBridge.TryGetFocusedElementShapeWithIdentity()?.RuntimeId,
                    logOutcome: LogIdentityOutcome,
                    blockedResult: () =>
                    {
                        Logger.Warning(
                            "text paste: focus moved to a different element inside target 0x{Target:X} during the modifier wait — blocking Ctrl+V, text kept on clipboard",
                            targetWindow);
                        return PasteResult.Fail(PasteAttemptOutcome.FocusMovedInTarget, PasteResultPresentation.FocusMovedMessage);
                    },
                    checkFallbackBaseline: checkFallbackBaseline,
                    checkTargetAlive: checkTargetAlive,
                    checkForeground: checkForeground,
                    send: prefix => SendCtrlVWithPrefix(prefix, probeAttempt),
                    sendFailedResult: () =>
                    {
                        Logger.Warning("Failed to send Ctrl+V for text paste; transcription left on clipboard for manual Ctrl+V");
                        return PasteResult.Fail(PasteAttemptOutcome.SendInputFailed, PasteResultPresentation.KeystrokeBlockedMessage);
                    }).ConfigureAwait(false);
                if (sequence.Failure is { } sequenceFail)
                {
                    outcome = sequenceFail.Outcome;
                    return sequenceFail;
                }
            }
            else
            {
                // PLAIN path — the shipped ordering byte-for-byte: identity decision + its
                // log line still run on EVERY attempt (the metric above), then the same
                // named checks, then the awaited modifier-safe send whose internal 400 ms
                // wait is exactly the pre-PST-6 behaviour this path preserves.
                var identityResult = await Helpers.PreSendIdentityCoordinator.RunAsync(
                    identityGuardApplicable,
                    priorIdentity,
                    waitModifiers: () => Task.CompletedTask,
                    usableBaselineIdentity: () => deliveryBaseline?.RuntimeId,
                    probeIdentity: () => Helpers.UiaFocusBridge.TryGetFocusedElementShapeWithIdentity()?.RuntimeId)
                    .ConfigureAwait(false);
                LogIdentityOutcome(identityResult);
                if (identityResult.ShouldBlock)
                {
                    // Provably unreachable here — this path implies the guard is inapplicable,
                    // which resolves NoPriorGap — kept so the plain path mirrors the shipped
                    // combined flow exactly (zero-behaviour-change refactor).
                    Logger.Warning(
                        "text paste: focus moved to a different element inside target 0x{Target:X} during the modifier wait — blocking Ctrl+V, text kept on clipboard",
                        targetWindow);
                    outcome = PasteAttemptOutcome.FocusMovedInTarget;
                    return PasteResult.Fail(outcome, PasteResultPresentation.FocusMovedMessage);
                }
                if (checkFallbackBaseline() is { } baselineFail)
                {
                    outcome = baselineFail.Outcome;
                    return baselineFail;
                }
                if (checkTargetAlive() is { } targetFail)
                {
                    outcome = targetFail.Outcome;
                    return targetFail;
                }
                if (checkForeground() is { } foregroundFail)
                {
                    outcome = foregroundFail.Outcome;
                    return foregroundFail;
                }
                var sent = await SendCtrlVModifierSafeAsync(probeSink: probeAttempt).ConfigureAwait(false);
                if (!sent)
                {
                    Logger.Warning("Failed to send Ctrl+V for text paste; transcription left on clipboard for manual Ctrl+V");
                    outcome = PasteAttemptOutcome.SendInputFailed;
                    return PasteResult.Fail(outcome, PasteResultPresentation.KeystrokeBlockedMessage);
                }
            }

            // PST-6: Ctrl+V was DISPATCHED — that is all SendInput proves. Verify whether the
            // text actually arrived and report honestly. PST-16: an unconfirmed paste is NOT
            // re-sent — the readback lags, so "not seen" is not "not there", and re-sending
            // duplicated the user's text. When verification is off (non-Chromium target,
            // unreadable baseline) the check is skipped and this stays today's unconditional
            // success.
            if (deliveryBaseline is not null)
            {
                var report = await _deliveryVerifier.VerifyAsync(
                    BuildDeliveryContext(
                        text, deliveryBaseline,
                        proofRequired: preRestoreStage.RequiresDeliveryVerification)).ConfigureAwait(false);
                if (report.Outcome != Helpers.PasteDeliveryOutcome.Delivered)
                {
                    // The check re-asserted the clipboard as a postcondition; if even that
                    // failed, present the RED clipboard failure — never a decline message
                    // promising a Ctrl+V that cannot work.
                    outcome = report.ClipboardAsserted
                        ? PasteAttemptOutcome.PasteDeliveryUncertain
                        : PasteAttemptOutcome.ClipboardSetFailed;
                    return PasteResult.Fail(outcome, DeliveryDeclineMessage(report));
                }
            }

            // Paste succeeded — schedule deferred clipboard restore and transfer ownership
            // of the snapshot to RestoreSnapshotAfterDelayAsync (it calls RestoreSnapshotAsync
            // which disposes inside its own finally block).
            if (shouldRestore && savedClipboard != null)
            {
                var restoreDelay = _settings.GetDouble(AppDefaults.ClipboardRestoreDelay, 2.0);
                restoreDelay = Math.Max(restoreDelay, 0.25);

                _ = RestoreSnapshotAfterDelayAsync(savedClipboard, TimeSpan.FromSeconds(restoreDelay), text);
                savedClipboard = null; // transfer ownership — do not dispose in finally
            }

            outcome = PasteAttemptOutcome.Pasted;
            // Verified attempts already read the post-paste state through the ladder;
            // running the legacy fire-and-forget focus probe too would double-probe the
            // same instant on the same worker (plan C5: one probe source).
            if (deliveryBaseline is null)
                LogPasteDiagnostics(targetWindow);

            return PasteResult.Success();
        }
        finally
        {
            // On failure paths, do NOT restore the original clipboard — the transcription
            // text is on the clipboard and the user can manually Ctrl+V it.
            // On success, savedClipboard is null (ownership transferred to deferred restore).
            // Just dispose the snapshot to release the COM object.
            savedClipboard?.Dispose();

            // Single per-attempt summary — success AND failure — so a paste miss is
            // diagnosable from one log line (which dimension failed) instead of
            // hand-correlating warnings. ageMs = how long the target/UIA capture sat
            // between recording-start and this paste.
            LogAttemptSummary(outcome, targetCapturedAtUtc, routeLabel, focusedElement != null, uiaRestored, escalation);
        }
    }

    private static void LogAttemptSummary(
        PasteAttemptOutcome outcome, DateTime targetCapturedAtUtc, string route,
        bool uiaTarget, bool uiaFocused, string escalation)
    {
        var ageMs = targetCapturedAtUtc == default
            ? -1
            : (int)(DateTime.UtcNow - targetCapturedAtUtc).TotalMilliseconds;
        Logger.Information("Paste attempt summary: {Summary}",
            PasteSummary.Format(outcome, ageMs, route, uiaTarget, uiaFocused, escalation));
    }

    /// <summary>
    /// REL-16 diagnostic: detailed clipboard identity sample for the NON-boundary instants
    /// (after-set / pre-enter). Under <see cref="_writeLease"/> this adds only bounded
    /// managed collection plus open-free Win32 probes — no <c>OpenClipboard</c>, no logging,
    /// no process lookup — minimal nonzero perturbation, never contention. The SendInput
    /// boundary must NOT use this; it takes <see cref="SafeClipboardSequence"/> alone
    /// (one syscall).
    /// </summary>
    internal static ClipboardProbeSample CaptureClipboardProbe(ClipboardProbePhase phase)
    {
        try
        {
            var seqBefore = NativeInterop.GetClipboardSequenceNumber();
            var owner = NativeInterop.GetClipboardOwner();
            uint ownerPid = 0;
            if (owner != IntPtr.Zero)
            {
                NativeInterop.GetWindowThreadProcessId(owner, out ownerPid);
            }
            var hasText = NativeInterop.IsClipboardFormatAvailable(NativeInterop.CF_UNICODETEXT);
            var hasDib = NativeInterop.IsClipboardFormatAvailable(NativeInterop.CF_DIB);
            var seqAfter = NativeInterop.GetClipboardSequenceNumber();
            return new ClipboardProbeSample(phase, true, true, seqBefore, seqAfter, owner, ownerPid, hasText, hasDib);
        }
        catch (Exception)
        {
            return ClipboardProbeSample.Failed(phase);
        }
    }

    /// <summary>REL-16: the boundary sequence read — one open-free syscall, exception-hardened
    /// to 0 (= unavailable in the report semantics).</summary>
    internal static uint SafeClipboardSequence()
    {
        try
        {
            return NativeInterop.GetClipboardSequenceNumber();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// REL-16: emit one attempt's probe endpoints + verdict, OFF the paste path. Idempotent
    /// (first caller wins — both the Enter-phase seam and the caller's throw-backstop invoke
    /// it) and fully fail-contained. Owner-pid → process-name resolution happens only here,
    /// on a thread-pool worker, never under <see cref="_writeLease"/>. The name rides the
    /// dedicated <c>{OwnerProcess}</c> property — redacted by name on the Sentry sub-logger,
    /// and scrubbed from RENDERED lines (Support zip / GDPR export) by
    /// <c>LogRedactionEnricher.RedactString</c>'s <c>ownerProc="…"</c> pattern.
    /// </summary>
    internal static void EmitClipboardProbes(ClipboardProbeAttempt attempt)
    {
        if (!attempt.TryMarkEmitted())
            return;

        _ = Task.Run(() =>
        {
            try
            {
                foreach (var sample in attempt.Ordered)
                {
                    var ownerName = "";
                    if (sample.Detailed && sample.OwnerPid != 0)
                    {
                        try
                        {
                            using var p = global::System.Diagnostics.Process.GetProcessById((int)sample.OwnerPid);
                            ownerName = p.ProcessName;
                        }
                        catch { /* process exited / access denied */ }
                    }
                    // SEC-3: sanitized like every other PII-class value, even though the quoted
                    // OwnerProcessPattern is already exactly bounded here (a Windows process name
                    // cannot contain a quote or a newline). Uniform "sanitize at emit" is the
                    // point — the next person to reuse this token for a value that CAN contain
                    // one should not have to discover the distinction (Kimi diff r3).
                    Logger.Information("{ClipboardProbe} ownerProc=\"{OwnerProcess}\"",
                        ClipboardProbeSummary.Format(sample),
                        global::VoiceWink.Helpers.LogValueSanitizer.SingleLine(ownerName));
                }
                Logger.Information("Clipboard probe verdict: {ClipboardProbeVerdict}",
                    ClipboardProbeReport.Describe(attempt));
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Clipboard probe emission failed");
            }
        });
    }

    /// <summary>
    /// Reliably set the foreground window, even from a background process.
    /// <para>Windows only honors a background <c>SetForegroundWindow</c> when the calling thread
    /// shares an input queue (via <c>AttachThreadInput</c>) with the thread that CURRENTLY owns
    /// the foreground. The previous implementation attached to the TARGET window's thread, which
    /// does nothing to lift the foreground lock held by a third app — so when the user switched
    /// away mid-recording (e.g. to Edge) the steal-back to the original target silently failed.
    /// We now attach to the current foreground owner's thread.</para>
    /// Returns true after a best-effort activation attempt (or when the target is already
    /// foreground); the caller's <see cref="WaitForForegroundSettleAsync"/> + the
    /// <c>GetForegroundWindow() == target</c> rechecks remain the authority on whether the
    /// activation actually landed, so a slow async activation is not prematurely treated as a
    /// failure here. Returns false only when <c>SetForegroundWindow</c> itself reports failure.
    /// </summary>
    private static bool ForceForegroundWindow(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero)
        {
            return true;
        }

        var foreground = NativeInterop.GetForegroundWindow();
        if (foreground == targetWindow)
        {
            // Already foreground (the common hold-to-talk / never-switched-away path).
            return true;
        }

        var currentThread = NativeInterop.GetCurrentThreadId();

        // Un-minimize the target so the activation surfaces a usable window. Guard with IsIconic —
        // an unconditional SW_RESTORE would also un-maximize a maximized target.
        if (NativeInterop.IsIconic(targetWindow))
        {
            NativeInterop.ShowWindow(targetWindow, NativeInterop.SW_RESTORE);
        }

        var foregroundThread = foreground == IntPtr.Zero
            ? 0u
            : NativeInterop.GetWindowThreadProcessId(foreground, out _);

        // No foreground owner to borrow an input queue from (none, or it is our own thread):
        // attempt a plain activation. Never AttachThreadInput to thread 0 or to ourselves.
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            if (!NativeInterop.SetForegroundWindow(targetWindow))
            {
                Logger.Warning("SetForegroundWindow failed for target window 0x{Target:X}", targetWindow);
                return false;
            }
            return true;
        }

        // Share the CURRENT foreground owner's input queue, then steal back to the target.
        var attached = NativeInterop.AttachThreadInput(currentThread, foregroundThread, true);
        if (!attached)
        {
            Logger.Warning("AttachThreadInput to foreground thread failed (Win32 {Err}) for target window 0x{Target:X}; attempting activation anyway",
                Marshal.GetLastWin32Error(), targetWindow);
        }

        try
        {
            if (!NativeInterop.SetForegroundWindow(targetWindow))
            {
                Logger.Warning("SetForegroundWindow failed for target window 0x{Target:X}", targetWindow);
                return false;
            }
            return true;
        }
        finally
        {
            if (attached && !NativeInterop.AttachThreadInput(currentThread, foregroundThread, false))
            {
                Logger.Debug("AttachThreadInput detach (foreground) failed (Win32 {Err}) for target window 0x{Target:X}",
                    Marshal.GetLastWin32Error(), targetWindow);
            }
        }
    }

    /// <summary>
    /// Wait for the OS foreground window to actually become <paramref name="targetWindow"/>
    /// before pasting. Keeps the original ~50ms settle (lets the just-activated app's UI thread
    /// breathe), then polls up to an extra ~250ms (default) only if the foreground hasn't matched
    /// yet — so the happy path is unchanged but a slow/contended target gets more time instead of
    /// an immediate bail. Purely additive: never pastes earlier than the old fixed delay did.
    /// Escalation-ladder steps pass a shorter <paramref name="pollBudget"/> so the whole ladder
    /// stays bounded.
    /// </summary>
    private static async Task WaitForForegroundSettleAsync(IntPtr targetWindow, TimeSpan? pollBudget = null)
    {
        await Task.Delay(50).ConfigureAwait(false);

        if (targetWindow == IntPtr.Zero) return;

        var deadline = DateTime.UtcNow + (pollBudget ?? TimeSpan.FromMilliseconds(250));
        while (NativeInterop.GetForegroundWindow() != targetWindow && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Acquire the OS foreground for <paramref name="targetWindow"/>, escalating through the
    /// bounded ladder when the plain attach+SetForegroundWindow attempt does not land:
    /// RetryAttach (100 ms later — transient input-state churn, the observed 2026-07-05
    /// failure class) → InputNudge (inert VK_F24 down+up via SendInput earns this process
    /// "received the last input event" credit, one of SetForegroundWindow's success
    /// conditions) → SwitchToThisWindow (Alt-Tab-equivalent last resort, gated on the target
    /// re-passing the liveness/identity check so a recycled handle is never Alt-Tabbed to).
    /// Escalation runs ONLY where the paste previously failed outright; the happy path is
    /// byte-for-byte today's behavior. Returns success + the escalation label for the
    /// per-attempt summary ("none" = step 0 sufficed).
    /// </summary>
    private static async Task<(bool Acquired, string Escalation)> EnsureTargetForegroundAsync(
        IntPtr targetWindow, PasteTargetSnapshot? targetSnapshot, string stage)
    {
        if (targetWindow == IntPtr.Zero)
            return (true, "none");

        var sfwOk = ForceForegroundWindow(targetWindow);
        await WaitForForegroundSettleAsync(targetWindow).ConfigureAwait(false);
        if (NativeInterop.GetForegroundWindow() == targetWindow)
            return (true, "none");

        // Diagnose before escalating: what holds the foreground right now? This was the
        // blind spot in the 2026-07-05 failure (SetForegroundWindow failed, nothing logged).
        var holder = NativeInterop.GetForegroundWindow();
        NativeInterop.GetWindowThreadProcessId(holder, out var holderPid);
        Logger.Warning(
            "Foreground acquire at {Stage}: initial attempt {SfwResult} (target=0x{Target:X}); foreground=0x{Holder:X} class=\"{Class}\" pid={Pid} — escalating",
            stage, sfwOk ? "did not settle" : "SetForegroundWindow failed", targetWindow,
            holder, GetWindowClassName(holder), holderPid);

        for (var attempt = 0; ; attempt++)
        {
            var step = ForegroundEscalation.NextStep(attempt);
            if (step == ForegroundEscalation.Step.GiveUp)
                return (false, "GiveUp");

            // Re-run the liveness/identity gate before EVERY escalation attempt — the
            // ladder spans up to ~750 ms, long enough for the target to be destroyed
            // and its HWND value recycled by another window. Activating a recycled
            // handle would make the caller's hwnd-equality recheck pass and paste
            // into the wrong app. Snapshotless targets get the IsWindow floor.
            if (!NativeInterop.IsWindow(targetWindow)
                || ValidateTargetSnapshot(targetSnapshot, targetWindow)
                    is PasteTargetValidity.Gone or PasteTargetValidity.Recycled)
            {
                Logger.Warning("Foreground acquire at {Stage}: target gone/recycled before {Step} — giving up", stage, step);
                return (false, step.ToString());
            }

            switch (step)
            {
                case ForegroundEscalation.Step.RetryAttach:
                    await Task.Delay(ForegroundEscalation.RetryDelay).ConfigureAwait(false);
                    ForceForegroundWindow(targetWindow);
                    break;

                case ForegroundEscalation.Step.InputNudge:
                    SendInputNudge();
                    ForceForegroundWindow(targetWindow);
                    break;

                case ForegroundEscalation.Step.SwitchToThisWindow:
                    NativeInterop.SwitchToThisWindow(targetWindow, true);
                    break;
            }

            await WaitForForegroundSettleAsync(targetWindow, ForegroundEscalation.StepSettleBudget).ConfigureAwait(false);
            if (NativeInterop.GetForegroundWindow() == targetWindow)
            {
                Logger.Information("Foreground acquire at {Stage}: recovered via {Step}", stage, step);
                return (true, step.ToString());
            }
        }
    }

    /// <summary>Inert VK_F24 down+up. F24 is in none of HotkeyService's assignable key
    /// lists (the LL hook passes it through untouched) and means nothing to real apps.</summary>
    private static void SendInputNudge()
    {
        var inputs = new NativeInterop.INPUT[2];
        inputs[0] = NativeInterop.CreateKeyInputEx(NativeInterop.VK_F24, false);
        inputs[1] = NativeInterop.CreateKeyInputEx(NativeInterop.VK_F24, true);
        var sent = NativeInterop.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInterop.INPUT>());
        if (sent != inputs.Length)
            Logger.Debug("Input nudge: SendInput returned {Sent}/2 (Win32 {Err})", sent, Marshal.GetLastWin32Error());
    }

    /// <summary>Production wrapper over the pure <see cref="PasteTargetValidation.Validate"/>.</summary>
    private static PasteTargetValidity ValidateTargetSnapshot(PasteTargetSnapshot? snapshot, IntPtr targetWindow)
        => PasteTargetValidation.Validate(
            snapshot,
            targetWindow,
            NativeInterop.IsWindow,
            hwnd =>
            {
                var tid = NativeInterop.GetWindowThreadProcessId(hwnd, out var pid);
                return (pid, tid);
            },
            GetWindowClassName);

    /// <summary>VoiceWink's own elevation, resolved once (UIPI gate input).</summary>
    private static readonly Lazy<bool> SelfElevated = new(() =>
        NativeInterop.TryGetProcessElevation((uint)Environment.ProcessId) ?? false);

    /// <summary>
    /// Wait (bounded 400 ms) for physically-held side-specific modifiers to be released,
    /// returning KEYUP inputs — prepended to the synthetic chord in the SAME SendInput
    /// call — for the ones still down that are PHANTOM-PRONE: modifiers serving as
    /// VoiceWink hotkeys, whose keyups our own LL hook has historically suppressed
    /// (AltGr on an AZERTY RightAlt hotkey being the canonical stuck case). Modifiers
    /// the user is genuinely holding for other reasons are never force-released — the
    /// chord proceeds with them held (today's behavior) and the hold is logged. The LL
    /// hook passes our injected keyups through during a paste
    /// (HotkeyService.SuppressPromptActions window).
    /// </summary>
    /// <summary>
    /// PST-16: returns the KEYUP prefix alone. It used to carry a second field,
    /// <c>GenuineHeld</c> — whether GENUINE (non-phantom-prone) modifiers were still
    /// physically held — which existed solely so the rung-3 type-out could ABORT (raw
    /// Unicode events merging with a held Ctrl/Alt/Win fire shortcuts instead of
    /// typing). With the type-out gone the flag had no reader: all three call sites
    /// discarded it. The held-modifier fact is still LOGGED below, which is what it
    /// was worth to a human reading a paste trace.
    /// </summary>
    private async Task<NativeInterop.INPUT[]> BuildModifierReleasePrefixAsync()
    {
        var stillHeld = await ModifierReleasePlan.WaitForReleaseAsync(
            vk => (NativeInterop.GetAsyncKeyState(vk) & 0x8000) != 0,
            Task.Delay).ConfigureAwait(false);

        if (stillHeld.Length == 0)
            return [];

        // Live hotkey snapshot (main + paste-last + redo-last + prompt hotkeys) —
        // prompt hotkeys are user-assignable to modifiers too, and every hotkey's
        // keyups flow through the suppressing hook, so all are phantom-prone.
        var phantomProne = _hotkeyService.GetPhantomProneModifierVks();

        var toRelease = stillHeld.Where(phantomProne.Contains).ToArray();
        var keptHeld = stillHeld.Where(v => !phantomProne.Contains(v)).ToArray();

        if (keptHeld.Length > 0)
        {
            Logger.Information(
                "Proceeding with physically-held non-hotkey modifiers (not force-released): {Vks}",
                string.Join(",", keptHeld.Select(v => $"0x{v:X2}")));
        }
        if (toRelease.Length == 0)
            return [];

        Logger.Information(
            "Force-releasing phantom-prone hotkey modifiers before synthetic input: {Vks}",
            string.Join(",", toRelease.Select(v => $"0x{v:X2}")));

        var releases = new NativeInterop.INPUT[toRelease.Length];
        for (var i = 0; i < toRelease.Length; i++)
            releases[i] = NativeInterop.CreateKeyInputEx(toRelease[i], true);
        return releases;
    }

    /// <summary>
    /// A foreground recheck found the OS foreground is no longer <paramref name="targetWindow"/>.
    /// Log what it drifted to (so the destination is never a blind spot — this was the gap behind
    /// the 22:46:59 Claude Desktop miss), then re-assert the target ONCE (ForceForegroundWindow +
    /// a single settle) and recheck. Returns true if the target is foreground again. One attempt,
    /// no retry loop, by design. The most common drift is our own UIA SetFocus momentarily moving
    /// foreground; re-asserting recovers the intended target without any wrong-window paste.
    /// </summary>
    private static async Task<bool> RecoverTargetForegroundOnceAsync(IntPtr targetWindow, PasteTargetSnapshot? targetSnapshot, string stage)
    {
        var drifted = NativeInterop.GetForegroundWindow();
        NativeInterop.GetWindowThreadProcessId(drifted, out var driftedPid);
        Logger.Warning(
            "Foreground drift at {Stage}: target=0x{Target:X} drifted-to=0x{Drift:X} class=\"{Class}\" pid={Pid} — re-asserting target",
            stage, targetWindow, drifted, GetWindowClassName(drifted), driftedPid);

        // Re-assert through the shared acquisition path — the plain attach+SFW re-assert
        // first (the common self-inflicted UIA drift), the bounded ladder only when that
        // fails the same way the initial acquisition can.
        var (recovered, _) = await EnsureTargetForegroundAsync(targetWindow, targetSnapshot, stage).ConfigureAwait(false);
        Logger.Information("Foreground re-assert at {Stage}: {Result}", stage, recovered ? "recovered" : "still-drifted");
        return recovered;
    }

    /// <summary>
    /// Decide the paste focus route for <paramref name="targetWindow"/>. For UIA routing the
    /// default on any error/missing signal stays <see cref="Helpers.PasteFocusRoute.Route.NormalUiaRestore"/>
    /// (Electron / Win32 paths never affected). <c>Confident</c> is false on that fallback —
    /// the NoEditableFocused gate only ever acts on a CONFIDENT route, because a fallback
    /// that was safe for routing would be unsafe as a blocking-gate precondition (e.g. a
    /// failed WebView2 child scan misclassifying a Teams-like target).
    /// Resolved ONCE per paste attempt and reused.
    /// </summary>
    private static (Helpers.PasteFocusRoute.Route Route, bool Confident) ResolvePasteRoute(IntPtr targetWindow)
    {
        try
        {
            if (targetWindow == IntPtr.Zero) return (Helpers.PasteFocusRoute.Route.NormalUiaRestore, false);
            var targetClass = GetWindowClassName(targetWindow);
            NativeInterop.GetWindowThreadProcessId(targetWindow, out var hostPid);
            var crossProcNames = CollectCrossProcessChildProcessNames(targetWindow, hostPid);
            var (route, reason) = Helpers.PasteFocusRoute.Decide(
                new Helpers.PasteFocusRoute.Snapshot(targetClass, crossProcNames));
            Logger.Information("Paste route: {Route} — {Reason}", route, reason);
            return (route, true);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Paste route resolution failed — defaulting to NormalUiaRestore (fail-closed)");
            return (Helpers.PasteFocusRoute.Route.NormalUiaRestore, false);
        }
    }

    /// <summary>
    /// PST-2 gate: detect the field-proven "no editable focused" Chromium loss shape
    /// and confirm it with a bounded UIA ControlType probe before blocking. Fail-OPEN
    /// on every missing signal — a false block on a working target (e.g. the omnibox,
    /// which keeps HWND focus on the top-level frame yet pastes fine and reports
    /// ControlType.Edit) would be worse than the silent loss.
    /// <para>Called at TWO stages (PST-2b): pre-restore (the authoritative evaluation —
    /// the focus shape is still unmodified evidence there; a block skips the restore)
    /// and post-restore (a drift net only — the restore itself converts the loss shape
    /// into renderer-child focus, so this stage can no longer see the original family).</para>
    /// This overload performs its own bounded focus capture (image path, post-restore
    /// stages). The pre-captured overload reuses an earlier capture.
    /// </summary>
    private async Task<bool> ShouldBlockNoEditableFocusedAsync(
        IntPtr targetWindow, (Helpers.PasteFocusRoute.Route Route, bool Confident) route, string context,
        Helpers.PreRestoreStage? rescueStage = null)
    {
        var focus = await CaptureFocusInfoBoundedAsync(targetWindow).ConfigureAwait(false);
        return ShouldBlockNoEditableFocusedCore(targetWindow, route, context, focus, rescueStage);
    }

    /// <summary>
    /// Pre-captured overload (PST-2b): <paramref name="preCapturedFocus"/> is the result
    /// of a bounded capture the CALLER already attempted (LogPrePasteStateAsync). Null
    /// means attempted-and-failed — the gate fails open WITHOUT a second 250 ms capture
    /// (the wedged-target path must not pay the budget twice).
    /// </summary>
    private Task<bool> ShouldBlockNoEditableFocusedAsync(
        IntPtr targetWindow, (Helpers.PasteFocusRoute.Route Route, bool Confident) route, string context,
        FocusInfo? preCapturedFocus)
        => Task.FromResult(ShouldBlockNoEditableFocusedCore(targetWindow, route, context, preCapturedFocus));

    /// <summary>
    /// Shared gate core. Deliberately contains NO focus capture — the "null focus fails
    /// open without recapture" contract is structural. The final foreground re-check
    /// guards against a stale Stage-1 pairing: the snapshot can be a few hundred ms old
    /// (pre-restore stage), and blocking based on another window's focused element would
    /// emit a false NoEditableFocused on a target that never had the loss shape.
    /// </summary>
    private static bool ShouldBlockNoEditableFocusedCore(
        IntPtr targetWindow, (Helpers.PasteFocusRoute.Route Route, bool Confident) route, string context,
        FocusInfo? focus, Helpers.PreRestoreStage? rescueStage = null)
    {
        if (focus is not { } info)
            return false; // focus snapshot unavailable/stalled — fail open

        if (!NoEditableFocusGate.ShouldProbe(
                route.Confident, route.Route, info.TargetClass, targetWindow, info.FocusHwnd, info.FocusClass))
            return false;

        // Net selection + verdict live in the pure gate (NoEditableFocusGate.EvaluatePostRestore):
        // after ANY overturned pre-restore block the broad shape predicate applies, but ONLY
        // to the exact element that pass verified (runtime-ID match) — targets like Claude
        // Desktop keep Win32 focus on the top-level frame permanently, so the Edit-only rule
        // would re-block every pass, while an unbound broad rule would admit a drifted-to
        // non-editable element. Extracted so the ROUTING is test-pinned at this layer: keying
        // it on the rescue flag alone silently re-blocked both PST-6 fallback kinds and undid
        // the fix, and a predicate-only test could not see it (Codex diff review rounds 2–3).
        var (block, probeProduced, detail) = NoEditableFocusGate.EvaluatePostRestore(
            rescueStage,
            Helpers.UiaFocusBridge.TryGetFocusedElementShapeWithIdentity,
            Helpers.UiaFocusBridge.TryGetFocusedControlType);

        if (block && NativeInterop.GetForegroundWindow() != targetWindow)
        {
            Logger.Information(
                "{Context}: stale focus pairing (foreground moved off 0x{Target:X} since the snapshot) — failing open; drift recovery owns this case",
                context, targetWindow);
            return false;
        }
        if (block)
        {
            Logger.Warning(
                "{Context}: no editable focused in Chromium target 0x{Target:X} (focusHwnd=0x{Focus:X} focusClass=\"{FocusClass}\" {Detail}) — blocking Ctrl+V, content kept on clipboard",
                context, targetWindow, info.FocusHwnd, info.FocusClass, detail);
        }
        else if (probeProduced)
        {
            Logger.Information(
                "{Context}: top-level Chromium focus but {Detail} — paste proceeds",
                context, detail);
        }
        return block;
    }

    /// <summary>
    /// Bounded focus snapshot for the paste path. <see cref="CaptureFocusInfo"/>'s
    /// GetGUIThreadInfo/GetClassName have been observed blocking for seconds under
    /// target contention, so the paste path never calls it inline — a thread-pool
    /// worker takes the hit and a 250 ms budget fails open (null). (BoundedComCall
    /// isn't reusable here: its type parameter is class-constrained and FocusInfo
    /// is a struct.)
    /// </summary>
    private static async Task<FocusInfo?> CaptureFocusInfoBoundedAsync(IntPtr targetWindow)
    {
        try
        {
            var work = Task.Run(() => CaptureFocusInfo(targetWindow));
            var winner = await Task.WhenAny(work, Task.Delay(TimeSpan.FromMilliseconds(250))).ConfigureAwait(false);
            if (winner != work)
            {
                Logger.Information("Bounded focus snapshot timed out for 0x{Target:X}", targetWindow);
                return null;
            }
            return work.Status == TaskStatus.RanToCompletion ? work.Result : null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Bounded focus snapshot failed");
            return null;
        }
    }

    /// <summary>
    /// Bounded, fail-closed scan of <paramref name="parent"/>'s descendant windows for processes
    /// OTHER than the host (<paramref name="hostPid"/>). An in-process Electron app yields none; an
    /// out-of-process WebView2 host yields <c>msedgewebview2</c>. Caps the windows inspected and
    /// stops early once the WebView2 process is seen. Runs on the (background) paste path, never the
    /// UI thread.
    /// </summary>
    private static IReadOnlyList<string> CollectCrossProcessChildProcessNames(IntPtr parent, uint hostPid)
    {
        var names = new List<string>();
        var seenPids = new HashSet<uint>();
        var inspected = 0;
        const int maxInspect = 400;
        NativeInterop.EnumChildWindows(parent, (hwnd, _) =>
        {
            if (++inspected > maxInspect) return false; // bounded — stop enumerating
            NativeInterop.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == hostPid) return true;
            if (!seenPids.Add(pid)) return true; // already resolved this process
            try
            {
                using var p = global::System.Diagnostics.Process.GetProcessById((int)pid);
                var name = p.ProcessName;
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                    if (name.IndexOf("msedgewebview2", global::System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return false; // found the discriminator — stop early
                }
            }
            catch { /* process exited / access denied — ignore */ }
            return true;
        }, IntPtr.Zero);
        return names;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new global::System.Text.StringBuilder(256);
        return NativeInterop.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>
    /// Simulate Ctrl+V via SendInput, modifier-safe: waits (bounded 400 ms) for any
    /// physically-held side-specific modifier to be released and force-releases
    /// stragglers in the SAME input batch ahead of the chord — so the target never
    /// observes Ctrl+V merged with a held Alt/Shift/Win (AltGr on the RightAlt
    /// recording hotkey being the canonical case). Keys are scan-code-enriched
    /// (CreateKeyInputEx) for targets that read raw scan codes.
    /// <para>IMG-BG: <paramref name="proceedGate"/> re-checks AFTER the modifier wait,
    /// immediately before SendInput — the 400 ms wait is itself long enough for the user's
    /// recording-hotkey press to start a recording (very plausibly the exact press the wait
    /// observed), and an earlier fence can't see it (Codex diff review round 3).</para>
    /// </summary>
    private async Task<bool> SendCtrlVModifierSafeAsync(Func<bool>? proceedGate = null, ClipboardProbeAttempt? probeSink = null)
    {
        var releases = await BuildModifierReleasePrefixAsync().ConfigureAwait(false);

        if (proceedGate is { } gate && !gate())
        {
            Logger.Information("Ctrl+V send aborted after the modifier wait — recording started");
            return false;
        }

        return SendCtrlVWithPrefix(releases, probeSink);
    }

    /// <summary>
    /// PST-6: the Ctrl+V dispatch WITHOUT the modifier wait — the caller's
    /// resend calls this directly because round-4 sequencing requires the wait to
    /// happen BEFORE the rung's identity/liveness/foreground checks, so no await
    /// separates the last check from the send. <paramref name="probeSink"/> is
    /// null for ladder resends: the REL-16 probe's fixed PreSend/PostSend phases
    /// belong to the ORIGINAL send alone, and a second pair would poison
    /// <see cref="ClipboardProbeAttempt.Integrity"/>.
    /// </summary>
    private bool SendCtrlVWithPrefix(NativeInterop.INPUT[] releases, ClipboardProbeAttempt? probeSink = null)
    {
        var inputs = new NativeInterop.INPUT[releases.Length + 4];
        releases.CopyTo(inputs, 0);
        inputs[releases.Length + 0] = NativeInterop.CreateKeyInputEx(NativeInterop.VK_CONTROL, false);
        inputs[releases.Length + 1] = NativeInterop.CreateKeyInputEx(NativeInterop.VK_V, false);
        inputs[releases.Length + 2] = NativeInterop.CreateKeyInputEx(NativeInterop.VK_V, true);
        inputs[releases.Length + 3] = NativeInterop.CreateKeyInputEx(NativeInterop.VK_CONTROL, true);

        // REL-16: seq-only samples bracket the send — the race boundary pays ONE open-free
        // syscall per side, never a detailed sample. CtrlVSendSequence pins the order
        // send → error snapshot → post-send seq, so the probe can't overwrite SendInput's
        // Win32 error; the image path (probeSink null) skips the post-send read entirely.
        probeSink?.Add(ClipboardProbeSample.SeqOnly(ClipboardProbePhase.PreSend, SafeClipboardSequence()));

        var (result, lastErr, postSendSeq) = CtrlVSendSequence.Run(
            () => NativeInterop.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInterop.INPUT>()),
            Marshal.GetLastWin32Error,
            probeSink is null ? static () => 0u : SafeClipboardSequence);

        probeSink?.Add(ClipboardProbeSample.SeqOnly(ClipboardProbePhase.PostSend, postSendSeq));

        if (result != inputs.Length)
        {
            Logger.Warning("SendInput returned {Result}, expected {Expected} (Win32 error {Err})", result, inputs.Length, lastErr);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Logs the pre-paste diagnostic line and returns the bounded focus capture it
    /// performed for it (PST-2b): the pre-restore NoEditableFocused gate reuses this
    /// snapshot instead of capturing again. Null = capture was ATTEMPTED and failed
    /// open (timeout/fault) — the gate must then fail open too, not recapture.
    /// </summary>
    private async Task<FocusInfo?> LogPrePasteStateAsync(IntPtr targetWindow, string expectedText)
    {
        try
        {
            var fg = NativeInterop.GetForegroundWindow();
            var current = GetClipboard();
            var state = current == null ? "unreadable"
                : current == expectedText ? "match"
                : $"mismatch(currentLen={current.Length},expectedLen={expectedText.Length})";
            // Pair the existing foreground/clipboard signal with native focus state so a
            // mid-recording focus drift (target's hwndFocus changed between recording-start
            // and the moment we send Ctrl+V) is visible in the log without state plumbing.
            // Same shape as recording-start log in MainViewModel.StartRecordingAsync.
            // BOUNDED: CaptureFocusInfo can block for seconds on a wedged target — a
            // diagnostic must never stall the paste (same posture as recording-start).
            var focus = await CaptureFocusInfoBoundedAsync(targetWindow).ConfigureAwait(false);
            var info = focus ?? default;
            Logger.Information(
                "Pre-paste: foreground=0x{FG:X} expectedTarget=0x{Target:X} clipboard={State} focusCaptured={Captured} " +
                "targetClass=\"{TargetClass}\" tid={Tid} pid={Pid} hwndActive=0x{Active:X} hwndFocus=0x{Focus:X} " +
                "focusClass=\"{FocusClass}\" caretHwnd=0x{Caret:X} caretRect=({CL},{CT},{CR},{CB}) flags=0x{Flags:X}",
                fg, targetWindow, state, focus.HasValue,
                info.TargetClass, info.ThreadId, info.ProcessId, info.ActiveHwnd, info.FocusHwnd,
                info.FocusClass, info.CaretHwnd, info.CaretRect.Left, info.CaretRect.Top,
                info.CaretRect.Right, info.CaretRect.Bottom, info.Flags);
            return focus;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Pre-paste diagnostics failed");
            return null;
        }
    }

    /// <summary>
    /// Capture post-paste state to help diagnose silent paste failures.
    /// SendInput reporting success only proves we dispatched Ctrl+V; it cannot prove the
    /// target app's focused control accepted it. Logs the focused control's class name
    /// (so we can spot non-editable focus) and re-checks the focused control after a short
    /// delay. For a Chromium target a landed paste shows focusClass="Chrome_RenderWidgetHostHWND";
    /// focus stuck on the top-level "Chrome_WidgetWin_1" means the keystroke had no editable to
    /// land in. (The old "clipboard still has pasted text" probe was non-informative — Ctrl+V
    /// never clears the clipboard, so it read true on both success and failure.)
    /// </summary>
    private void LogPasteDiagnostics(IntPtr targetWindow)
    {
        // Fire-and-forget diagnostics ONLY — nothing on the paste path waits for this.
        // Both snapshots go through the BOUNDED capture (F18): CaptureFocusInfo's
        // GetGUIThreadInfo/GetClassName can block for seconds under target contention,
        // and this method used to call it inline on the paste path, contradicting the
        // bounded helper's own "the paste path never calls it inline" contract.
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await CaptureFocusInfoBoundedAsync(targetWindow).ConfigureAwait(false);
                if (info is { } i)
                {
                    Logger.Information(
                        "Paste diagnostics: target=0x{Target:X} targetClass=\"{TargetClass}\" tid={Tid} pid={Pid} " +
                        "active=0x{Active:X} focusHwnd=0x{Focus:X} focusClass=\"{FocusClass}\" " +
                        "caretHwnd=0x{Caret:X} caretRect=({CL},{CT},{CR},{CB}) flags=0x{Flags:X}",
                        targetWindow, i.TargetClass, i.ThreadId, i.ProcessId,
                        i.ActiveHwnd, i.FocusHwnd, i.FocusClass,
                        i.CaretHwnd, i.CaretRect.Left, i.CaretRect.Top, i.CaretRect.Right, i.CaretRect.Bottom,
                        i.Flags);
                }

                await Task.Delay(150).ConfigureAwait(false);
                // Re-read the focused control: did focus settle on an editable, or drift
                // back to the top-level window (paste had nowhere to land)?
                var after = await CaptureFocusInfoBoundedAsync(targetWindow).ConfigureAwait(false);
                if (after is { } a)
                {
                    Logger.Information(
                        "Paste diagnostics (post-150ms): focusHwnd=0x{Focus:X} focusClass=\"{FocusClass}\" caretHwnd=0x{Caret:X}",
                        a.FocusHwnd, a.FocusClass, a.CaretHwnd);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Paste diagnostics failed");
            }
        });
    }

    internal readonly record struct FocusInfo(
        IntPtr ActiveHwnd, IntPtr FocusHwnd, string FocusClass,
        IntPtr CaretHwnd, NativeInterop.RECT CaretRect, uint Flags,
        uint ThreadId, uint ProcessId, string TargetClass);

    /// <summary>
    /// Capture native focus state for <paramref name="targetWindow"/>'s thread. Returns
    /// an empty <see cref="FocusInfo"/> on any failure path. Used by paste diagnostics
    /// AND by <c>MainViewModel.StartRecordingAsync</c> for the recording-start log
    /// (so the same shape compares cleanly between recording-start and pre-paste).
    /// Native focus only — for Chromium-based targets (Electron apps) this CANNOT see
    /// DOM-element focus inside the renderer; it sees the renderer's HWND.
    /// </summary>
    internal static FocusInfo CaptureFocusInfo(IntPtr targetWindow)
    {
        var empty = new FocusInfo(IntPtr.Zero, IntPtr.Zero, "", IntPtr.Zero, default, 0, 0, 0, "");
        if (targetWindow == IntPtr.Zero) return empty;

        var threadId = NativeInterop.GetWindowThreadProcessId(targetWindow, out var processId);
        if (threadId == 0) return empty;

        var targetClass = "";
        var classBuf = new global::System.Text.StringBuilder(256);
        if (NativeInterop.GetClassName(targetWindow, classBuf, classBuf.Capacity) > 0)
            targetClass = classBuf.ToString();

        var info = new NativeInterop.GUITHREADINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeInterop.GUITHREADINFO>()
        };
        if (!NativeInterop.GetGUIThreadInfo(threadId, ref info))
        {
            return empty with { ThreadId = threadId, ProcessId = processId, TargetClass = targetClass };
        }

        var focusClass = "";
        if (info.hwndFocus != IntPtr.Zero)
        {
            var sb = new global::System.Text.StringBuilder(256);
            var len = NativeInterop.GetClassName(info.hwndFocus, sb, sb.Capacity);
            if (len > 0) focusClass = sb.ToString();
        }
        return new FocusInfo(info.hwndActive, info.hwndFocus, focusClass, info.hwndCaret, info.rcCaret, info.flags,
            threadId, processId, targetClass);
    }

    /// <summary>
    /// Simulate pressing the Enter key, modifier-safe (same held-modifier wait +
    /// force-release as the Ctrl+V path — a held Ctrl would turn Enter into
    /// Ctrl+Enter, a different action in most chat apps).
    /// </summary>
    public async Task PressEnterAsync()
    {
        var releases = await BuildModifierReleasePrefixAsync().ConfigureAwait(false);

        var inputs = new NativeInterop.INPUT[releases.Length + 2];
        releases.CopyTo(inputs, 0);
        inputs[releases.Length + 0] = NativeInterop.CreateKeyInputEx(NativeInterop.VK_RETURN, false);
        inputs[releases.Length + 1] = NativeInterop.CreateKeyInputEx(NativeInterop.VK_RETURN, true);
        var result = NativeInterop.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInterop.INPUT>());
        if (result != inputs.Length)
        {
            Logger.Warning("PressEnter: SendInput returned {Result}, expected {Expected} (Win32 error {Err})", result, inputs.Length, Marshal.GetLastWin32Error());
        }
    }

    private static bool SetClipboardHandle(uint format, IntPtr handle, string description)
    {
        var result = NativeInterop.SetClipboardData(format, handle);
        if (result != IntPtr.Zero)
        {
            // After SetClipboardData succeeds, the system owns the handle.
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        NativeInterop.GlobalFree(handle);
        Logger.Warning("SetClipboardData failed for {Description}, Win32={Error}", description, error);
        return false;
    }

    private static bool ClearClipboardContents()
    {
        if (!TryOpenClipboard())
        {
            return false;
        }

        try
        {
            return NativeInterop.EmptyClipboard();
        }
        finally
        {
            NativeInterop.CloseClipboard();
        }
    }

    private ClipboardSnapshot? CaptureSnapshotCore()
    {
        try
        {
            if (NativeInterop.CountClipboardFormats() == 0)
            {
                return ClipboardSnapshot.Empty;
            }

            if (!TryOpenClipboard())
            {
                Logger.Warning("Failed to open clipboard for snapshot capture");
                return null;
            }

            try
            {
                // Standard formats that return GDI/OS handles instead of HGLOBAL.
                // Calling GlobalLock on these causes a native access violation.
                // Windows re-synthesizes them from primary formats when needed.
                const uint CF_BITMAP = 2;
                const uint CF_METAFILEPICT = 3;
                const uint CF_PALETTE = 9;
                const uint CF_ENHMETAFILE = 14;
                const uint CF_OWNERDISPLAY = 0x0080;
                const uint CF_DSPBITMAP = 0x0082;
                const uint CF_DSPENHMETAFILE = 0x008E;

                var formats = new List<(uint format, byte[] data)>();
                uint fmt = 0;
                while ((fmt = NativeInterop.EnumClipboardFormats(fmt)) != 0)
                {
                    if (fmt is CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or CF_ENHMETAFILE
                            or CF_OWNERDISPLAY or CF_DSPBITMAP or CF_DSPENHMETAFILE)
                        continue;

                    var hData = NativeInterop.GetClipboardData(fmt);
                    if (hData == IntPtr.Zero) continue;

                    // GlobalSize returns 0 for non-HGLOBAL handles — skip them safely.
                    var size = (int)NativeInterop.GlobalSize(hData);
                    if (size <= 0) continue;

                    var ptr = NativeInterop.GlobalLock(hData);
                    if (ptr == IntPtr.Zero) continue;

                    try
                    {
                        var bytes = new byte[size];
                        Marshal.Copy(ptr, bytes, 0, size);
                        formats.Add((fmt, bytes));
                    }
                    finally
                    {
                        NativeInterop.GlobalUnlock(hData);
                    }
                }

                if (formats.Count == 0)
                {
                    Logger.Debug("Clipboard snapshot: no formats captured (all had zero-size handles)");
                    return ClipboardSnapshot.Empty;
                }

                Logger.Debug("Clipboard snapshot captured: {Count} format(s)", formats.Count);
                return ClipboardSnapshot.FromFormats(formats);
            }
            finally
            {
                NativeInterop.CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to capture clipboard snapshot");
            return null;
        }
    }

    private bool RestoreSnapshotCore(ClipboardSnapshot snapshot)
    {
        try
        {
            var restored = snapshot.Restore();
            if (!restored)
            {
                Logger.Warning("Clipboard snapshot restore failed");
            }

            return restored;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to restore clipboard snapshot");
            return false;
        }
    }

    private async Task RestoreSnapshotAfterDelayAsync(ClipboardSnapshot snapshot, TimeSpan delay, string pastedText)
    {
        global::System.Threading.Interlocked.Increment(ref _pendingRestores);
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);

            // Compare-and-restore under the write lease AND inside one native clipboard
            // ownership window (IMG-BG): the old read-compare-then-reopen-and-restore was a
            // TOCTOU — another writer (in-process image completion or another app) could land
            // between the comparison and the restore and be silently overwritten. The dispatcher
            // hop mirrors RestoreSnapshotAsync (snapshot restores ran on the UI thread before).
            await _writeLease.WaitAsync().ConfigureAwait(false);
            bool? restored;
            try
            {
                restored = await RunOnMainDispatcherAsync(() => CompareAndRestoreCoreUnderLease(snapshot, pastedText))
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeLease.Release();
                snapshot.Dispose();
            }

            if (restored == null)
                Logger.Debug("Clipboard changed during restore delay — skipping restore");
            else if (restored == true)
                Logger.Debug("Clipboard restored after {Delay}s", delay.TotalSeconds);
            else
                Logger.Warning("Clipboard snapshot restore failed");
        }
        catch (Exception ex)
        {
            // Fire-and-forget task — an escaped exception would be unobserved.
            Logger.Warning(ex, "Deferred clipboard restore failed");
        }
        finally
        {
            global::System.Threading.Interlocked.Decrement(ref _pendingRestores);
        }
    }

    /// <summary>
    /// Atomic compare-and-restore: within ONE OpenClipboard window, read the current text,
    /// compare with what we pasted (a mismatch — including null because the user or an image
    /// completion replaced the text — skips the restore), and restore the snapshot on a match.
    /// Returns null when skipped, else the restore result. Lock-free w.r.t. the write lease.
    /// </summary>
    private static bool? CompareAndRestoreCoreUnderLease(ClipboardSnapshot snapshot, string pastedText)
    {
        if (!TryOpenClipboard())
            return false;

        try
        {
            var current = ReadUnicodeTextUnderOpenClipboard();
            if (current != pastedText)
                return null;

            return snapshot.RestoreUnderOpenClipboard();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Compare-and-restore failed");
            return false;
        }
        finally
        {
            NativeInterop.CloseClipboard();
        }
    }

    private static Task RunOnMainDispatcherAsync(Action action)
    {
        return RunOnMainDispatcherAsync(() =>
        {
            action();
            return true;
        });
    }

    private static Task<T> RunOnMainDispatcherAsync<T>(Func<T> func)
    {
        var queue = App.MainWindow?.DispatcherQueue;
        if (queue == null || queue.HasThreadAccess)
        {
            return Task.FromResult(func());
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("Failed to enqueue clipboard work on the UI dispatcher."));
        }

        return tcs.Task;
    }
}

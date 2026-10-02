using System.Runtime.InteropServices;
using Serilog;
using VoiceWink.Helpers;

namespace VoiceWink.Services.Support;

/// <summary>
/// Opens the user's default mail client with a message pre-filled AND the report ZIP already
/// attached, via Simple MAPI (<c>MAPISendMailW</c>, the ANSI <c>MAPISendMail</c> only when the client
/// refuses Unicode — REL-40). A <c>mailto:</c> link cannot carry an
/// attachment, so this is the standard Win32 way to hand a compose window + attachment to whatever
/// MAPI client is the default (classic Outlook, Thunderbird, …). Returns <c>false</c> when no MAPI
/// client is registered or the call fails, so the caller can fall back to a plain <c>mailto:</c>.
///
/// <para>The P/Invoke + interop structs live here (not <c>NativeInterop</c>) deliberately: they are
/// meaningless outside Simple MAPI and keep this self-contained interop module cohesive.</para>
/// </summary>
public static class MapiMailService
{
    private static ILogger Logger => Log.ForContext(typeof(MapiMailService));

    private const int MAPI_LOGON_UI = 0x00000001;
    private const int MAPI_DIALOG = 0x00000008;
    // REL-40: on the wide call only. Without it the MAPI stub converts to ANSI ITSELF for a client
    // that has no Unicode entry point, so a "Unicode" success would not mean the client got Unicode
    // and the encoding line would lie; with it that client answers MAPI_E_UNICODE_NOT_SUPPORTED and
    // the send takes the ANSI retry, which then says so (Codex plan review, 2026-10-02).
    internal const int MAPI_FORCE_UNICODE = 0x00040000;
    private const int MAPI_TO = 1;
    private const int SUCCESS_SUCCESS = 0;
    private const int MAPI_USER_ABORT = 1;

    /// <summary>
    /// REL-25 item 2 (owner UAT 2026-08-17, §32): name the Simple MAPI return code in the log.
    /// The failure path is the ONLY thing standing between "the attachment rode the email" and
    /// "please attach it by hand", and it used to log a bare integer at Information — below the
    /// level anyone reads, and meaningless without a lookup table. The owner's §32.3 report (the
    /// RAW option degrading to manual attach while the default option attached fine) is exactly
    /// the shape a size- or attachment-related code would produce, and the bare integer is why
    /// that could not be confirmed from the report bundle.
    /// <para>Values are the Simple MAPI constant numbering. MAPISendMail's reference page documents
    /// a SUBSET of them as its own return values; the rest are included because a client may return
    /// any of them and a name is more useful than a number. Anything unlisted returns the number, so
    /// an unmapped code degrades to what was logged before rather than to nothing.</para>
    /// </summary>
    internal static string DescribeMapiCode(int code) => code switch
    {
        0 => "SUCCESS_SUCCESS",
        1 => "MAPI_USER_ABORT",
        2 => "MAPI_E_FAILURE",
        3 => "MAPI_E_LOGIN_FAILURE",
        4 => "MAPI_E_DISK_FULL",
        5 => "MAPI_E_INSUFFICIENT_MEMORY",
        6 => "MAPI_E_ACCESS_DENIED",
        8 => "MAPI_E_TOO_MANY_SESSIONS",
        9 => "MAPI_E_TOO_MANY_FILES",
        10 => "MAPI_E_TOO_MANY_RECIPIENTS",
        11 => "MAPI_E_ATTACHMENT_NOT_FOUND",
        12 => "MAPI_E_ATTACHMENT_OPEN_FAILURE",
        13 => "MAPI_E_ATTACHMENT_WRITE_FAILURE",
        14 => "MAPI_E_UNKNOWN_RECIPIENT",
        15 => "MAPI_E_BAD_RECIPTYPE",
        16 => "MAPI_E_NO_MESSAGES",
        17 => "MAPI_E_INVALID_MESSAGE",
        18 => "MAPI_E_TEXT_TOO_LARGE",
        19 => "MAPI_E_INVALID_SESSION",
        20 => "MAPI_E_TYPE_NOT_SUPPORTED",
        21 => "MAPI_E_AMBIGUOUS_RECIPIENT",
        22 => "MAPI_E_MESSAGE_IN_USE",
        23 => "MAPI_E_NETWORK_FAILURE",
        24 => "MAPI_E_INVALID_EDITFIELDS",
        25 => "MAPI_E_INVALID_RECIPS",
        26 => "MAPI_E_NOT_SUPPORTED",
        // 27 comes back from MAPISendMailW when the default client cannot take Unicode (REL-40):
        // the send then retries once through the ANSI MAPISendMail, so this name reaches a log
        // only as the reason for that retry.
        27 => "MAPI_E_UNICODE_NOT_SUPPORTED",
        // 28 IS THE ONE THIS CARD IS ABOUT (Codex diff r2, verified against the MAPISendMail
        // reference: "The specified attachment was too large. No message was sent."). The owner's
        // §32.3 report is a larger bundle being refused where a smaller one was accepted, so this
        // is the single most likely answer -- and omitting it would have logged a bare "code 28"
        // for exactly the failure this change exists to name.
        28 => "MAPI_E_ATTACHMENT_TOO_LARGE",
        _ => $"code {code}",
    };

    /// <summary>
    /// Show the default MAPI client's compose window with the given recipient / subject / body and
    /// an optional attachment. BLOCKS until the user closes the compose window (MAPI_DIALOG is
    /// modal), so call it off the UI thread. Returns <c>true</c> if MAPI handled it (the compose
    /// window was shown — sent or cancelled); <c>false</c> if no MAPI client is registered or the
    /// call failed, in which case the caller decides what follows — since REL-29 a decline is
    /// offered as a retry before the <c>mailto:</c> fallback, except on a host this class refuses
    /// outright (REL-27), where the caller falls back directly.
    /// </summary>
    public static bool TrySend(string recipientEmail, string subject, string body, string? attachmentPath)
        => TrySend(recipientEmail, subject, body, attachmentPath,
            RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture, SendViaSimpleMapi);

    /// <summary>
    /// The native leg, as a delegate so a test can stand in for it. Same shape as
    /// <see cref="TrySend(string,string,string,string?)"/>.
    /// </summary>
    internal delegate bool NativeMapiSend(string recipientEmail, string subject, string body, string? attachmentPath);

    /// <summary>
    /// REL-27 boundary. Architectures AND the native leg are parameters for one reason: a test must
    /// be able to prove the guard runs BEFORE the P/Invoke and that an unsafe host reaches it ZERO
    /// times. Asserting only "returns false" would pass just as happily against a reversed or
    /// missing guard — and the failure that would ship is a hard process crash, so "the test could
    /// not tell" is not an acceptable gap. A test must never reach the real native leg: it would
    /// open a mail compose window on a developer's machine.
    /// </summary>
    internal static bool TrySend(
        string recipientEmail, string subject, string body, string? attachmentPath,
        Architecture processArchitecture, Architecture osArchitecture, NativeMapiSend nativeSend)
    {
        // Ordering is the whole fix: MAPISendMail loads the default mail client's provider (plus,
        // for Office, its App-V shim) INTO this process, and on an emulated host that crashes the
        // emulation subsystem outright — 0xc000026f STATUS_WX86_INTERNAL_ERROR, twice on 2026-08-24,
        // same WER bucket. It is a NATIVE fault, so the catch inside the native leg cannot see it and
        // the process simply dies, taking the user's typed report with it. Returning false routes to
        // the mailto fallback, which keeps the bundle and reveals it in Explorer.
        if (!MapiHostSupport.IsSafeToLoadInProcess(processArchitecture, osArchitecture))
        {
            // WARNING, and naming BOTH architectures, because ReportSendFlow's fallback line says
            // only THAT it fell back — this is the line that says why. Without it a deliberate
            // safety refusal is indistinguishable in a support bundle from a mail-client failure,
            // which is exactly the REL-25 item 2 diagnostic gap. The values are two enum names: no
            // user data, no path, no mail content, so this is safe in a bundle and in a breadcrumb.
            Logger.Warning(
                "Simple MAPI skipped: {Reason} (process={ProcessArchitecture}, os={OSArchitecture}). "
                + "Falling back to mailto. Attachment: {AttachmentPresent}",
                MapiHostSupport.RefusalReason,
                processArchitecture, osArchitecture,
                attachmentPath != null);
            return false;
        }

        return nativeSend(recipientEmail, subject, body, attachmentPath);
    }

    private static bool SendViaSimpleMapi(string recipientEmail, string subject, string body, string? attachmentPath)
        => SendPreferringUnicode(recipientEmail, subject, body, attachmentPath, CallMapiSendMail);

    /// <summary>
    /// REL-40: which string encoding a Simple MAPI call carries. The ANSI <c>MAPISendMail</c>
    /// converts every string to the system code page, so a description typed in a script that
    /// code page lacks (Chinese on a Western-locale PC, the field case) reached the compose window
    /// as a row of <c>?</c> — and the description is not in the bundle, so the text was lost.
    /// </summary>
    internal enum MapiEncoding { Unicode, Ansi }

    /// <summary>One native Simple MAPI call over an already-built message block. Returns the MAPI
    /// code; throws what the P/Invoke throws (<see cref="EntryPointNotFoundException"/> when the
    /// stub has no <c>MAPISendMailW</c>). A seam so tests never reach <c>MAPI32.DLL</c>.</summary>
    internal delegate int MapiNativeCall(MapiEncoding encoding, IntPtr message, int flags);

    /// <summary>
    /// REL-40: may the send retry through the ANSI call after a Unicode attempt? Only when the
    /// Unicode call REFUSED before any compose window could exist — the client cannot take Unicode
    /// (<c>MAPI_E_UNICODE_NOT_SUPPORTED</c>) or the stub lacks the export (<paramref name="result"/>
    /// null). Every other code is final: success and user-abort mean a window was shown, and a real
    /// failure (REL-29's add-in veto, 28's too-large) must not open a second attempt behind the
    /// user's back — the caller already offers a retry for those.
    /// </summary>
    internal static bool ShouldRetryWithAnsi(MapiEncoding tried, int? result)
        => tried == MapiEncoding.Unicode && (result is null || result == MAPI_E_UNICODE_NOT_SUPPORTED);

    internal static bool SendPreferringUnicode(
        string recipientEmail, string subject, string body, string? attachmentPath, MapiNativeCall call)
    {
        var encoding = MapiEncoding.Unicode;
        try
        {
            var result = SendOnce(encoding, recipientEmail, subject, body, attachmentPath, call);
            if (ShouldRetryWithAnsi(encoding, result))
            {
                // Information: a client without Unicode support is a fact about this PC worth
                // seeing in a bundle (its text may lose characters), not a failure.
                Logger.Information("Simple MAPI client does not take Unicode ({Reason}); sending through the ANSI call",
                    result is { } code ? DescribeMapiCode(code) : "no MAPISendMailW export");
                encoding = MapiEncoding.Ansi;
                result = SendOnce(encoding, recipientEmail, subject, body, attachmentPath, call);
            }

            if (result == SUCCESS_SUCCESS || result == MAPI_USER_ABORT)
            {
                Logger.Information("Simple MAPI compose handed over ({Encoding})", encoding);
                return true; // compose window was shown with the attachment in place
            }

            // WARNING, not Information (REL-25 item 2): this is the branch that silently turns an
            // auto-attached report into "attach it yourself", so it has to be findable in a bundle.
            // The attachment SIZE rides along because it is the one input that differs between the
            // owner's working (redacted) and failing (raw) runs -- without it the next occurrence is
            // as undiagnosable as this one was.
            // "the caller decides" rather than "falling back to mailto" since REL-29: what follows
            // this line is the user's choice (retry, or the manual attach), and a log line that
            // asserts a path which no longer runs would mislead the next REL-25-style investigation.
            Logger.Warning(
                "MAPISendMail failed with {MapiCode} ({MapiCodeValue}, {Encoding}); the bundle is kept and the caller decides (retry or mailto). "
                + "Attachment: {AttachmentPresent}, {AttachmentBytes} bytes",
                DescribeMapiCode(result ?? -1), result ?? -1, encoding,
                attachmentPath != null, TryGetLength(attachmentPath));
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MAPISendMail threw; the bundle is kept and the caller decides (retry or mailto). Attachment: {AttachmentBytes} bytes",
                TryGetLength(attachmentPath));
            return false;
        }
    }

    /// <summary>Builds the message block in <paramref name="encoding"/>, makes one native call, and
    /// frees the block. Returns null only when the Unicode export is missing.</summary>
    private static int? SendOnce(
        MapiEncoding encoding, string recipientEmail, string subject, string body, string? attachmentPath, MapiNativeCall call)
    {
        using var block = MapiMessageBlock.Create(encoding, recipientEmail, subject, body, attachmentPath);
        try
        {
            var flags = MAPI_LOGON_UI | MAPI_DIALOG;
            if (encoding == MapiEncoding.Unicode)
                flags |= MAPI_FORCE_UNICODE;
            return call(encoding, block.Message, flags);
        }
        catch (EntryPointNotFoundException) when (encoding == MapiEncoding.Unicode)
        {
            return null;
        }
    }

    private static int CallMapiSendMail(MapiEncoding encoding, IntPtr message, int flags)
        => encoding == MapiEncoding.Unicode
            ? MAPISendMailW(IntPtr.Zero, IntPtr.Zero, message, flags, 0)
            : MAPISendMail(IntPtr.Zero, IntPtr.Zero, message, flags, 0);

    /// <summary>Attachment size for the failure log, or -1 when there is no attachment or it
    /// cannot be measured. Never throws — a diagnostic must not become a second failure.</summary>
    private static long TryGetLength(string? path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }

    private const int MAPI_E_UNICODE_NOT_SUPPORTED = 27;

    [DllImport("MAPI32.DLL", EntryPoint = "MAPISendMail", ExactSpelling = true)]
    private static extern int MAPISendMail(IntPtr session, IntPtr uiparam, IntPtr message, int flags, int reserved);

    [DllImport("MAPI32.DLL", EntryPoint = "MAPISendMailW", ExactSpelling = true)]
    private static extern int MAPISendMailW(IntPtr session, IntPtr uiparam, IntPtr message, int flags, int reserved);

    /// <summary>
    /// REL-40: the unmanaged message, recipient and attachment for one call, every string allocated
    /// in the call's encoding and freed on dispose. ONE struct layout serves both calls because
    /// Simple MAPI's ANSI and wide structs (<c>MapiMessage</c>/<c>MapiMessageW</c> and their
    /// recipient and file descriptors) differ only in the TYPE their string pointers point at;
    /// holding the strings as <see cref="IntPtr"/> moves that difference to allocation time
    /// instead of duplicating three structs.
    /// </summary>
    internal sealed class MapiMessageBlock : IDisposable
    {
        private readonly List<IntPtr> _allocations = new();
        private readonly MapiEncoding _encoding;

        public IntPtr Message { get; private set; }

        private MapiMessageBlock(MapiEncoding encoding) => _encoding = encoding;

        public static MapiMessageBlock Create(
            MapiEncoding encoding, string recipientEmail, string subject, string body, string? attachmentPath)
        {
            var block = new MapiMessageBlock(encoding);
            try
            {
                block.Build(recipientEmail, subject, body, attachmentPath);
                return block;
            }
            catch
            {
                block.Dispose();
                throw;
            }
        }

        private void Build(string recipientEmail, string subject, string body, string? attachmentPath)
        {
            // Single recipient.
            var recip = new MapiRecipDesc
            {
                recipClass = MAPI_TO,
                name = String(recipientEmail),
                address = String("SMTP:" + recipientEmail),
            };
            var recipPtr = Struct(recip);

            var message = new MapiMessage
            {
                subject = String(subject ?? string.Empty),
                noteText = String(body ?? string.Empty),
                recipCount = 1,
                recips = recipPtr,
            };

            // Optional single attachment (the redacted log ZIP).
            if (!string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath))
            {
                var file = new MapiFileDesc
                {
                    position = -1, // not embedded at a specific point in the body
                    path = String(attachmentPath),
                    name = String(Path.GetFileName(attachmentPath)),
                };
                message.fileCount = 1;
                message.files = Struct(file);
            }

            Message = Struct(message);
        }

        private IntPtr String(string value)
        {
            var ptr = _encoding == MapiEncoding.Unicode
                ? Marshal.StringToHGlobalUni(value)
                : Marshal.StringToHGlobalAnsi(value);
            _allocations.Add(ptr);
            return ptr;
        }

        private IntPtr Struct<T>(T value) where T : struct
        {
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            _allocations.Add(ptr);
            Marshal.StructureToPtr(value, ptr, false);
            return ptr;
        }

        public void Dispose()
        {
            // Every field is blittable (strings are pointers we allocated), so freeing the blocks
            // is the whole cleanup — no DestroyStructure.
            foreach (var ptr in _allocations)
                Marshal.FreeHGlobal(ptr);
            _allocations.Clear();
            Message = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MapiMessage
    {
        public int reserved;
        public IntPtr subject;
        public IntPtr noteText;
        public IntPtr messageType;
        public IntPtr dateReceived;
        public IntPtr conversationID;
        public int flags;
        public IntPtr originator;
        public int recipCount;
        public IntPtr recips;
        public int fileCount;
        public IntPtr files;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MapiRecipDesc
    {
        public int reserved;
        public int recipClass;
        public IntPtr name;
        public IntPtr address;
        public int eIDSize;
        public IntPtr entryID;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MapiFileDesc
    {
        public int reserved;
        public int flags;
        public int position;
        public IntPtr path;
        public IntPtr name;
        public IntPtr type;
    }
}

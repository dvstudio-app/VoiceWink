using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VoiceWink.Helpers;

/// <summary>
/// TRN-34: does this file carry a VALID Authenticode signature, and whose?
///
/// <para><b>Why <c>WinVerifyTrust</c> and not just reading the certificate.</b>
/// <c>X509Certificate.CreateFromSignedFile</c> only READS the embedded certificate — it verifies
/// nothing. A tampered binary keeps its original certificate blob and would still report a
/// convincing <c>CN=DV Studio</c> subject, so a subject-only check is provenance theatre. The
/// signature itself has to be verified against the file's own hash and chained to a trusted root,
/// and on Windows that is <c>WinVerifyTrust</c> with
/// <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>.</para>
///
/// <para><b>Both halves are required and neither is sufficient.</b> <c>WinVerifyTrust</c> alone
/// proves only that SOMEBODY with a trusted certificate signed it — every signed binary on the
/// machine passes that. The certificate alone proves nothing, per above. Callers need
/// <see cref="Verdict.IsTrusted"/>, which is the conjunction.</para>
///
/// <para><b>The identity is taken from DER, never from a formatted subject string.</b>
/// <see cref="SignerCommonName"/> is read out of the certificate's own
/// <see cref="X509Certificate2.SubjectName"/> — decoded from the DER encoding — and compared whole.
/// The obvious alternative, matching against the display subject, is unsound in a way that is easy
/// to miss and was MEASURED here (Codex diff r1 Blocker plus the verification that followed):
/// a substring test accepts <c>CN=DV Studio Tools, O=Somebody Else</c>, and re-parsing the display
/// string does not save it either, because
/// <c>new X500DistinguishedName("CN=Evil\, CN=DV Studio")</c> yields TWO RDNs —
/// <c>CN='Evil\'</c> and <c>CN='DV Studio'</c> — so a crafted CN round-trips into a clean-looking
/// match. Reading DER removes the ambiguity at the source rather than defending against it.</para>
///
/// <para>The P/Invoke lives here rather than in <c>NativeInterop</c> for the reason
/// <c>MapiMailService</c> records for Simple MAPI: these structures are meaningless outside
/// Authenticode and keeping the interop module self-contained is what makes it readable.</para>
///
/// <para><b>Never throws for verification reasons.</b> An unreadable, absent, or malformed file is
/// an untrusted verdict, not an exception — every caller is a fail-closed gate and an exception
/// escaping here would turn "cannot verify" into a crash.</para>
/// </summary>
public static class AuthenticodeSignature
{
    /// <summary>
    /// The outcome of one verification.
    /// <para><see cref="SignerCommonName"/> is the DER-decoded CN and is what identity decisions
    /// use. <see cref="SignerSubject"/> is the full display subject and is for LOGS ONLY — never
    /// match on it (see the type remarks).</para>
    /// </summary>
    public readonly record struct Verdict(bool IsSignatureValid, string? SignerSubject, string? SignerCommonName)
    {
        /// <summary>Valid signature AND the certificate's common name equals
        /// <paramref name="expectedCommonName"/> exactly (case-insensitive). Whole-value equality,
        /// never a prefix or substring: <c>DV Studio Tools</c> is a different company.</summary>
        public bool IsTrusted(string expectedCommonName)
            => IsSignatureValid
               && !string.IsNullOrWhiteSpace(expectedCommonName)
               && string.Equals(SignerCommonName, expectedCommonName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Why a verdict was not trusted, for a log line. App-authored text over a bool and
        /// a certificate subject — no file contents, no paths.</summary>
        public string Describe()
            => (IsSignatureValid, SignerSubject) switch
            {
                (false, null) => "not signed (or signature unreadable)",
                (false, _) => $"signature INVALID (signer {SignerSubject})",
                (true, null) => "signature valid but signer unreadable",
                (true, _) => $"signed by {SignerSubject}",
            };

        public static Verdict Unsigned => new(false, null, null);
    }

    /// <summary>Verify <paramref name="filePath"/>. Total: any failure is an untrusted verdict.</summary>
    public static Verdict Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Verdict.Unsigned;

        var valid = IsSignatureValid(filePath);
        var (subject, commonName) = TryReadSigner(filePath);
        return new Verdict(valid, subject, commonName);
    }

    private static bool IsSignatureValid(string filePath)
    {
        var fileInfoPtr = IntPtr.Zero;
        var dataPtr = IntPtr.Zero;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                // NONE: never prompt. This runs on a background thread during a recording; a UI
                // prompt here would be a hang, not a question anyone could answer.
                dwUIChoice = WTD_UI_NONE,
                // The revocation check is deliberately NOT forced online. A spawn happens on the
                // recording path and an offline or slow CRL fetch would stall it; Windows still
                // uses cached revocation data. Provenance here guards against a swapped or
                // corrupted file, not against a revoked-certificate adversary who already has
                // write access to the install directory.
                //
                // Bounded residual (Kimi plan r1 A6): this does not make verification strictly
                // offline. Building the chain for the FIRST time can still fetch a missing
                // intermediate over AIA, so the very first spawn after an install may pay a
                // one-time, crypt32-timeout-bounded stall while the caller holds its gate.
                // Accepted rather than engineered around: it is once per install, the timeout is
                // the OS's, and moving verification off the gate would let an unverified child
                // spawn concurrently — which is the thing this check exists to prevent.
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pUnion = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_IGNORE,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = null,
                // NO WTD_SAFER_FLAG, deliberately (Kimi plan r1 A3). The release-side post-pack
                // signing gate (check-pack-signatures) uses Get-AuthenticodeSignature, which applies no
                // SAFER evaluation; adding it here would let the two gates reach DIFFERENT verdicts
                // about the same file on a policy-managed machine (SRP/WDAC publisher-vs-path
                // rules). Two gates disagreeing about one binary is precisely the defect TRN-34
                // fixed, so their semantics are kept matched by construction.
                dwProvFlags = 0,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero,
            };
            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, dataPtr, false);

            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            // 0 == ERROR_SUCCESS is the ONLY trusted result. Every other value is a specific
            // distrust reason (TRUST_E_NOSIGNATURE, TRUST_E_BAD_DIGEST, CERT_E_CHAINING, …) and
            // they are deliberately not enumerated: the caller needs trusted-or-not, and a partial
            // enumeration that treated an unlisted code as success would fail OPEN.
            return WinVerifyTrust(INVALID_HANDLE_VALUE, ref action, dataPtr) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false; // cannot verify ⇒ not verified
        }
        finally
        {
            if (dataPtr != IntPtr.Zero) { Marshal.DestroyStructure<WINTRUST_DATA>(dataPtr); Marshal.FreeHGlobal(dataPtr); }
            if (fileInfoPtr != IntPtr.Zero) { Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPtr); Marshal.FreeHGlobal(fileInfoPtr); }
        }
    }

    /// <summary>
    /// The display subject (logs) and the DER-decoded common name (decisions). Both null when the
    /// file carries no readable certificate — the ordinary case on a dev build, not an error.
    /// </summary>
    private static (string? Subject, string? CommonName) TryReadSigner(string filePath)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));

            string? commonName = null;
            // SubjectName is built from the certificate's DER bytes, so enumerating it cannot be
            // confused by display-string escaping. Exactly ONE CN is required: a subject carrying
            // several is not a shape our signer produces, and picking one of them would be a guess.
            foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
            {
                if (rdn.GetSingleElementType().Value != Oids.CommonName) continue;
                if (commonName is not null) return (cert.Subject, null); // ambiguous ⇒ no identity
                commonName = rdn.GetSingleElementValue();
            }

            return (cert.Subject, commonName);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static class Oids
    {
        /// <summary>id-at-commonName. The OID, not the friendly name — a friendly name is a
        /// localization/display concern and is the wrong thing to branch on.</summary>
        public const string CommonName = "2.5.4.3";
    }

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}

namespace VoiceWink.Services.Transcription;

/// <summary>
/// Validates a model name before it becomes a filesystem path — by REJECTING anything unsafe,
/// never by quietly repairing it.
///
/// <para>The previous guard used <c>Path.GetFileName</c>, which STRIPS rather than refuses. Given
/// <c>..\ggml-small</c> it returned <c>ggml-small</c> — not an escape from the models directory,
/// but an ALIAS: a crafted name silently resolving onto a different, legitimate model's file. That
/// is survivable while the only consequence is <c>File.Delete</c> on one <c>.bin</c>. It stops
/// being survivable when the same guard drives the recursive DIRECTORY delete that Parakeet 2/3
/// introduces, which is why this was split out and landed first.</para>
///
/// <para>Each rule below exists because it lets two different strings name one file:</para>
/// <list type="bullet">
/// <item>trailing dot or space: Windows trims them, so <c>ggml-small.</c> and <c>ggml-small </c>
/// both open <c>ggml-small</c></item>
/// <item><c>~</c>: 8.3 short-name aliases. Measured on this machine — <c>ggml-large-v3-turbo-q5_0.bin</c>
/// is simultaneously addressable as <c>GGML-L~1.BIN</c>, and the guard's own
/// <c>name + ".bin"</c> composition turns the model name <c>GGML-L~1</c> into a working handle on
/// the real file: reading it returned the real payload and deleting it destroyed the real model.
/// Short names always contain a tilde, so refusing the character closes the whole form</item>
/// </list>
///
/// <para><b>Reserved device names are a measured case, not a recited one.</b> The familiar rule is
/// that <c>CON</c>/<c>NUL</c>/<c>COM1</c> are devices at any depth and regardless of extension.
/// Probed on this target (Windows 11, .NET 8, <c>System.IO</c>, inside a subdirectory) that is NOT
/// what happens: <c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>COM1</c> and <c>LPT1</c> all create
/// ordinary files AND ordinary directories, and every name carrying an extension
/// (<c>CON.bin</c>, <c>NUL.bin</c>) is ordinary too. Exactly one is intercepted: bare <c>NUL</c>,
/// where a file write vanishes into the null device and <c>Directory.CreateDirectory</c> throws.
/// They are rejected anyway, for two reasons that survive the measurement — the behaviour is
/// environment-dependent (Win32's own normalization, other tooling, other Windows/.NET versions
/// may still intercept, and a guard whose meaning depends on which API touches the name is no
/// guard), and bare <c>NUL</c> — the one case that IS intercepted — is precisely the shape a
/// Parakeet bundle directory takes. No shipped model is named <c>CON</c>, so refusing costs
/// nothing.</para>
///
/// <para>Names reaching here come from the catalog (clean) or from <c>settings.json</c>, which
/// import validates only as a string. Since Parakeet 1/3 an unrecognised name already fails
/// honestly before it gets this far — so rejecting here is consistent with that, not a new way for
/// a normal user to get stuck.</para>
/// </summary>
internal static class ModelNameGuard
{
    /// <summary>
    /// The Win32-documented device names, including the superscript <c>COM¹</c>/<c>LPT¹</c> forms
    /// Windows also reserves. Deliberately not an exhaustive sweep of every legacy DOS name
    /// (<c>CLOCK$</c> and friends are absent): per the measurement above, .NET 8 does not intercept
    /// even the mainstream ones here, so this list is defense-in-depth against other APIs and other
    /// environments rather than a load-bearing lookup. Growing it costs nothing and proves nothing.
    /// </summary>
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        // Superscript 1/2/3, written as escapes rather than literal characters so the bytes
        // cannot be mangled by an editor or a re-encoding pass.
        "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    ];

    /// <summary>
    /// Returns the name unchanged when it is safe to compose into a path, or throws.
    /// Never returns a DIFFERENT string than it was given — that is the whole point.
    /// </summary>
    /// <exception cref="ArgumentException">The name could alias another model or is not a usable
    /// filename.</exception>
    internal static string Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw Reject(name, "it is empty");

        // Directory components are rejected outright rather than stripped: stripping is what let
        // "..\ggml-small" alias a real model. This is the ONE rule that is name-only — a bundle
        // relative path legitimately contains separators, which is why the rest of the rule set
        // lives in DescribeSegmentProblem and is shared.
        if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw Reject(name, "it contains a directory separator");

        var problem = DescribeSegmentProblem(name);
        if (problem != null)
            throw Reject(name, problem);

        return name;
    }

    /// <summary>
    /// The per-segment rules, as a DECISION rather than an exception: null when the segment is safe,
    /// otherwise the reason. Shared with <c>BundleRelativePathGuard</c> (TRN-1 step 2), which applies
    /// them to each segment of a bundle-relative path.
    ///
    /// <para>A decision and not a throw because the two callers need different exception text — one
    /// is reporting an invalid model NAME, the other an invalid bundle FILE PATH — and a shared
    /// throw would have to lie to one of them. Every rule below is measured; see the type's
    /// remarks.</para>
    /// </summary>
    internal static string? DescribeSegmentProblem(string segment)
    {
        // Both callers reject emptiness before they get here, so this is not reachable today — but
        // the seam is shared now, and the trailing-dot rule below indexes [^1]. An empty string
        // would throw IndexOutOfRangeException from a method whose contract is to RETURN a reason.
        if (segment.Length == 0)
            return "it is empty";

        // Deliberate defense-in-depth, NOT a restatement of the separator check: an embedded "a..b"
        // reaches here past both the separator and invalid-char rules, and does not alias anything
        // on its own. It is refused because ".." is the traversal token and this rule set is now
        // also handed bundle-relative paths — pinned by EveryPredefinedModelName_IsValid.
        if (segment.Contains(".."))
            return "it contains '..'";

        // Also an alternate-data-stream marker on NTFS ("name:stream").
        if (segment.Contains(':'))
            return "it contains ':'";

        if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "it contains characters that are not valid in a filename";

        if (segment[^1] is '.' or ' ')
            return "it ends with a dot or space, which Windows trims — two different names would open the same file";

        // 8.3 short-name aliases always carry a tilde, so this one character closes the entire
        // form. Verified live: "GGML-L~1" composed to a working handle on
        // "ggml-large-v3-turbo-q5_0.bin" — it read the real payload, and deleting it destroyed the
        // real model. This is the sharpest alias the guard blocks, and the reason a reject-only
        // guard beats a stripping one.
        if (segment.Contains('~'))
            return "it contains '~', which can address another model through its 8.3 short name";

        // Checked against the stem so an extension cannot disguise the name.
        var stem = segment.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return $"'{stem}' is a reserved Windows device name";

        return null;
    }

    /// <summary>True when the name is safe — for callers that need a decision rather than an
    /// exception (a UI list filter, say). Same rules, no throw.</summary>
    internal static bool IsValid(string? name)
    {
        try
        {
            Validate(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether two model names would resolve to the same file on Windows.
    ///
    /// <para>Used where a per-name lock or cache key must not let <c>GGML-small</c> and
    /// <c>ggml-small</c> operate on one file through two different keys — a download and a delete
    /// racing each other because they disagreed about being the same model. This is the single
    /// definition; <c>ModelDiskReconciliation.NameComparer</c> forwards to it.</para>
    /// </summary>
    internal static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    private static ArgumentException Reject(string? name, string because)
        => new($"Invalid model name: {because}. " +
               $"Received: '{Helpers.UntrustedTextSanitizer.Sanitize(name, 40) ?? "(empty)"}'.");
}

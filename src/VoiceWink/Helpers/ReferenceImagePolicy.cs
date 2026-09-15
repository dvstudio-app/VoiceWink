namespace VoiceWink.Helpers;

/// <summary>
/// ENH-6i: what a JPEG's marker structure says about its container. Only
/// <see cref="MultiPicture"/> triggers normalization; the enum exists so a future
/// evidence-backed class slots in without changing the classifier's shape.
/// </summary>
public enum JpegContainerKind
{
    Plain,
    MultiPicture,
}

/// <summary>
/// The single home for ENH-6 reference-image constraints: extension→mime whitelist
/// and the size cap. Pure except <see cref="IsUsableAppImagePath"/> (File.Exists).
/// </summary>
public static class ReferenceImagePolicy
{
    /// <summary>
    /// Maximum reference file size — an APP-SIDE sanity bound, deliberately NOT a
    /// provider budget (owner decision 2026-07-13, supersedes the 10 MB cap sized to
    /// Gemini's inline budget). Provider limits move without notice (Gemini's inline
    /// budget went 20 MB → 100 MB in Jan 2026 while their own docs still disagree), so
    /// they are not tracked here: a reference a provider won't take surfaces as that
    /// provider's own error. What this bound protects is the app itself — the file is
    /// read whole into memory and, on the inline transports (Gemini inline_data,
    /// OpenRouter data-URL), copied through base64 (+33%), the JSON string, and its
    /// UTF-8 bytes (transiently several times the file size; those builds run off the
    /// calling thread), the upload must fit the images client's 10-min budget, and
    /// ENH-6b persists a copy under %LOCALAPPDATA% (content-addressed and shared
    /// between rows since ENH-6g). Checked against the opened stream's length BEFORE
    /// any allocation or read.
    /// </summary>
    public const long MaxBytes = 50 * 1024 * 1024;

    /// <summary>
    /// <b>SUPERSEDED (owner, 2026-08-02): "NO CAP" — and the replacement is now IN CODE.</b> This 6
    /// is a legacy ENH-6f UI sanity bound from when per-model maxima were unknowable, and it is NOT
    /// an accepted position. It survives only as the FLOOR inside
    /// <see cref="MaxReferenceCountFor"/>, which is what the dialog actually asks: a published
    /// figure raises the bound, a lower one never lowers it. Do not treat this constant as policy.
    ///
    /// Maximum number of reference images per generation (ENH-6f) — a UI sanity bound,
    /// NOT a provider limit (providers cap differently — OpenAI edits ≤16, Gemini
    /// model-dependent — and move without notice; an over-count upload surfaces the
    /// provider's own error, same philosophy as <see cref="MaxBytes"/>). The dialog
    /// refuses adds past this; the read gate re-checks as defense-in-depth.
    /// </summary>
    public const int MaxReferenceCount = 6;

    /// <summary>
    /// IMG-6: how many references THIS model accepts, when the catalog says so.
    ///
    /// <para>Owner decision 2026-08-01: <i>"if the chosen model can handle more, I don't want to
    /// limit capability in any way."</i> So a published maximum RAISES the bound (gpt-image
    /// publishes 16, gemini-3 14, riverflow-v2 10) rather than being clamped back to the app's
    /// sanity number. <see cref="MaxReferenceCount"/> remains the fallback for models with no
    /// published figure — OpenAI-direct and Gemini-direct publish none, and Gemini's docs express
    /// limits per ROLE (objects / characters / style) which the app has no concept of.</para>
    ///
    /// <para><b>A published maximum is never used to LOWER the bound below what is already
    /// attached</b> — that is the enforcement question, and IMG-6 deliberately answers it with an
    /// advisory instead (<see cref="DescribeReferenceOverage"/>). The limit is a property of the
    /// MODEL, and the model is switchable mid-dialog, so lowering would mean silently discarding
    /// photos the user deliberately chose. This method answers "how many may be added", not
    /// "how many are allowed to remain".</para>
    /// </summary>
    public static int MaxReferenceCountFor(ImageModelCapabilities? capabilities)
        => MaxReferenceCountFor(capabilities, PublishedMaxForDirectProvider(null, null));

    /// <summary>
    /// IMG-6: the add bound for a model, from the catalog when we have it and from documented
    /// per-provider figures when we do not.
    ///
    /// <para><b>Why the second source exists (Codex diff review):</b> only OpenRouter publishes a
    /// catalog, so a capabilities-only rule left OpenAI-direct and Gemini-direct pinned at the
    /// legacy 6 — and OpenAI documents <b>16</b> for its edits route. "NO CAP" was therefore
    /// implemented for exactly one of three providers. <paramref name="documentedMax"/> carries the
    /// documented figure for the direct routes.</para>
    /// </summary>
    public static int MaxReferenceCountFor(ImageModelCapabilities? capabilities, int? documentedMax)
    {
        var published = capabilities?.MaxReferences ?? documentedMax;
        if (published is not { } max || max <= 0) return MaxReferenceCount;
        var raised = max > MaxReferenceCount ? max : MaxReferenceCount;
        // CLAMPED to the read gate's ceiling (Codex diff review r2, blocking). The catalog figure is
        // LIVE and unbounded, so without this a model publishing 33 would let the dialog admit 33
        // and generation reject them — the very dialog-vs-gate disagreement this card exists to
        // remove, reintroduced from the other direction. Clamping here makes
        // "dialog admission <= read gate" true BY CONSTRUCTION rather than by a numeric coincidence
        // that holds only while every published figure stays under the ceiling.
        return raised > AbsoluteMaxReferenceCount ? AbsoluteMaxReferenceCount : raised;
    }

    /// <summary>
    /// Documented reference maxima for the providers that publish NO catalog. OpenAI's edits route
    /// documents 16 images for the gpt-image family; Gemini documents limits per ROLE (objects /
    /// characters / style) which the app has no concept of, so it stays null and keeps the fallback.
    /// Null means "no documented figure" — never zero, which would read as "none allowed".
    /// </summary>
    public static int? PublishedMaxForDirectProvider(Services.AIEnhancement.AIProvider? provider, string? model)
    {
        if (provider != Services.AIEnhancement.AIProvider.OpenAI) return null;
        if (string.IsNullOrEmpty(model)) return null;

        // The gpt-image family, PLUS `chatgpt-image-*` (Codex diff review r2, blocking): that alias
        // is a real catalog id the OpenAI image dropdown already offers (2026-07-30 model review),
        // so leaving it out kept a selectable model at the fallback 6.
        //
        // Matched HERE rather than by widening ImageOptions.IsGptImageFamily, which carries other
        // meanings — SupportsInputFidelity deliberately answers FALSE for chatgpt-image-latest, and
        // widening the shared classifier would silently change that too.
        if (ImageOptions.IsGptImageFamily(model)) return 16;

        // DELIMITER-BOUND, not a bare prefix (Codex diff review r3, blocking): `StartsWith
        // ("chatgpt-image")` also matched `chatgpt-imagery-*` and `chatgpt-imagefoo-*`, handing an
        // unrelated model OpenAI's 16. The id must be exactly `chatgpt-image` or continue with a
        // separator, which is the same delimiter discipline every other family rule in
        // ImageOptions uses.
        var bare = ImageOptions.Bare(model!);
        const string alias = "chatgpt-image";
        if (bare.StartsWith(alias, System.StringComparison.OrdinalIgnoreCase)
            && (bare.Length == alias.Length || bare[alias.Length] is '-' or '.'))
            return 16;

        return null;
    }

    /// <summary>
    /// The read gate's SANITY ceiling — deliberately NOT a per-model figure.
    ///
    /// <para><b>This is the correction to IMG-6's first attempt (Codex diff review, blocking).</b>
    /// Making the read gate per-model meant that attaching 16 references under a 16-capable model
    /// and then switching to krea made <i>VoiceWink itself</i> reject the list before the provider
    /// ever saw it — flatly contradicting the advisory policy shipped in the same card ("we do not
    /// gate; the provider decides"). The dialog deliberately keeps those references attached, so the
    /// read gate must not be the thing that refuses them.</para>
    ///
    /// <para>The read gate's documented job is defense-in-depth against corrupt or absurd state, not
    /// provider policy. So it enforces one generous ceiling instead: high enough that anything the
    /// dialog can legitimately admit passes, low enough to stop a corrupt persisted list. Policy
    /// lives in the two places that can express it honestly — the dialog's add bound (guidance, and
    /// where the advisory speaks) and the provider's own response (authoritative).</para>
    ///
    /// <para>The dialog can never admit more than this, because
    /// <see cref="MaxReferenceCountFor"/> CLAMPS to it — the catalog figure is live and unbounded,
    /// so "admission &lt;= gate" has to hold by construction rather than by a numeric coincidence
    /// (Codex r3; the earlier text claimed 32 merely exceeded every known figure, which a catalog
    /// publishing 33 would have falsified). 16 is the highest figure observed today.</para>
    /// </summary>
    public const int AbsoluteMaxReferenceCount = 32;

    /// <summary>
    /// IMG-6: the bound that applies to ONE attempt to put a reference into the dialog's list.
    /// Extracted as a pure seam (Codex diff review r3) because the rule it encodes is the fix for a
    /// real data-loss bug, and it previously lived only inside a WinUI local function where no test
    /// could reach it.
    ///
    /// <para><b>Seeding and adding are different questions.</b> Seeding RESTORES what a run already
    /// had; adding admits something new. Sharing one bound silently destroyed data: 16 references
    /// attached under gpt-image, the model switched to a low-limit one, the provider refused the
    /// generation, failure sanitation correctly retained all 16 — and reopening then seeded 6 while
    /// telling the user the other 10 were "no longer available". They were fine.</para>
    ///
    /// <para>So a seed is bounded only by <see cref="AbsoluteMaxReferenceCount"/> — enough to stop
    /// corrupt state, never enough to silently discard a user's chosen photos. The per-model figure
    /// is guidance for the NEXT add, and the advisory already tells the user the selected model may
    /// not take them all.</para>
    /// </summary>
    public static int AdmissionBound(bool isSeed, int perModelBound)
        => isSeed ? AbsoluteMaxReferenceCount : perModelBound;

    /// <summary>
    /// IMG-6: advisory text when more references are attached than the selected model publishes
    /// support for, or <see langword="null"/> when there is nothing to say.
    ///
    /// <para><b>Advisory ONLY — nothing is gated, dropped, or altered.</b> Same posture as AUD-4's
    /// Bluetooth-microphone hint: the app knows something the user cannot see, so it says so and
    /// lets them decide. Enforcement was considered and rejected because the limit belongs to the
    /// model, which is switchable mid-dialog — attaching four references against gpt-image and then
    /// selecting krea would force us to silently discard three chosen photos, a worse failure than
    /// the provider error it avoids.</para>
    ///
    /// <para>Null on every missing signal — no capabilities, no published maximum, or a count
    /// within it — so a model we know nothing about produces no advice rather than a guess
    /// (<c>CaptureEndpointClassifier</c>'s <c>Unknown</c> yields nothing for the same reason). The
    /// spread is wide enough to matter: krea, recraft and mai publish <b>1</b>, so even a second
    /// reference exceeds them.</para>
    /// </summary>
    public static string? DescribeReferenceOverage(int attachedCount, ImageModelCapabilities? capabilities)
    {
        if (capabilities?.MaxReferences is not { } max || max <= 0) return null;
        if (attachedCount <= max) return null;

        // Wording history, because both ends of it were wrong and the middle is the point:
        //
        // "the rest may be ignored" was DISPROVEN at owner UAT (2026-08-02) — krea answered HTTP
        // 400, `Krea: input_references: must have between 0 and 1`. It told the user the generation
        // would proceed minus the extras, so the real failure arrived as a surprise.
        //
        // "more will be rejected" then OVER-corrected (both reviewers, independently): krea is the
        // only provider observed rejecting. Whether recraft, mai, flux, grok or gemini-2.5 reject
        // or silently trim is unknown, and this one string speaks for every catalogued model. So
        // the claim is scoped to what is actually known — the published limit is certain, the
        // consequence of exceeding it is not.
        return max == 1
            ? "This model supports only 1 reference image; more may be rejected."
            : $"This model supports only {max} reference images; more may be rejected.";
    }

    /// <summary>
    /// Aggregate byte budget across ALL references of one generation (ENH-6f, Codex
    /// plan round 1): the inline transports copy every reference through base64, the
    /// JSON string, and its UTF-8 bytes SIMULTANEOUSLY, so without an aggregate bound
    /// six max-size files would transiently allocate well over a gigabyte. Kept equal
    /// to <see cref="MaxBytes"/> so multi-reference peak allocation can never exceed
    /// the long-accepted single-reference worst case. Checked against stream lengths
    /// BEFORE each allocation (running budget through the read gate).
    /// </summary>
    /// <remarks>
    /// <b>Raised to 3× <see cref="MaxBytes"/> by IMG-6.</b> The owner approved the RAISE on 2026-08-01, before its cost was measured; acceptance of that measured cost is separately OPEN (below). Keeping it
    /// equal to the single-file bound made the BYTE budget the binding constraint the moment the
    /// count bound rose: 16 references at typical phone-photo size (3–8 MB) cannot fit in 50 MB, so
    /// users would have stopped hitting a clear "this model takes 1 reference" and started hitting
    /// a confusing aggregate rejection — and <c>ReferenceImageBudgetExceededException</c> is
    /// non-collectable, so it drops the WHOLE list rather than the offending item.
    ///
    /// <para><b>Peak allocation — MEASURED, and the cause since FIXED by IMG-7.</b> The old
    /// UTF-16 chain cost 13-17x the input bytes (2,284 MiB at this bound). IMG-7 writes the request
    /// bodies as UTF-8 directly, measured at <b>5.0x — 750 MiB at 150 MiB input</b>, roughly a 3x
    /// reduction and ~11x faster. Harness and full table: <c>tools/reference-payload-memory/</c>.
    /// </para>
    ///
    /// <para>Three ESTIMATES preceded that measurement and every one was wrong (mine: "3x, ~450 MB";
    /// Codex: "~1.15 GiB"; then a single run reported as a constant when it is a range). The number
    /// above is measured; do not replace it with reasoning.</para>
    ///
    /// <para>Remaining, and deliberately NOT changed here: <c>RetryingHandler.CloneAsync</c> buffers
    /// and copies the whole payload per attempt — now the largest single term. Touching shared retry
    /// infrastructure needs its own plan.</para>
    /// </remarks>
    public const long MaxTotalBytes = 3 * MaxBytes;

    /// <summary>
    /// IMG-4: aggregate reference budget for a PARALLEL multi-version batch. All N
    /// concurrent provider calls build their payloads from the same shared reference
    /// bytes, so the transient request-build allocation multiplies by N — dividing the
    /// single-call budget by the version count keeps a batch's peak allocation at
    /// ~the long-accepted single-call worst case (the same invariant
    /// <see cref="MaxTotalBytes"/> established for multi-reference). N=1 returns the
    /// unchanged <see cref="MaxTotalBytes"/>; corrupt counts clamp to 1.
    /// </summary>
    public static long MaxBatchTotalBytes(int versionCount)
        => MaxTotalBytes / Math.Max(1, versionCount);

    /// <summary>
    /// ENH-6i: classify a JPEG's CONTAINER by walking its marker segments. The one
    /// class that normalizes today is <see cref="JpegContainerKind.MultiPicture"/> —
    /// MPO, the multi-frame container phones produce for portrait/depth shots
    /// (a normal-looking .jpg with extra pictures appended, signalled by an APP2
    /// segment whose payload starts <c>MPF\0</c>). OpenAI's edits validator rejects
    /// the container outright regardless of resolution ("Invalid image file or
    /// mode", live incident 2026-07-15 — a 3.1 MP MPO failed while 28.7 MP plain
    /// JPEGs passed). Walker contract (Codex plan rounds 1–2):
    /// <list type="bullet">
    /// <item>SOI required; FF fill bytes and standalone markers (01, D0–D7)
    /// tolerated; every segment length bounds-checked without overflow; hard cap of
    /// 64 segments.</item>
    /// <item><c>MPF\0</c> must be the FIRST FOUR PAYLOAD BYTES of an APP2 segment —
    /// never a substring scan.</item>
    /// <item>MultiPicture is returned only when the header ALSO walks cleanly to
    /// SOS — EOF/malformed data anywhere returns Plain (fail-open passthrough:
    /// today's behavior for bytes the classifier can't vouch for; a positively
    /// classified image that later fails the codec throws instead).</item>
    /// </list>
    /// CMYK deliberately NOT a class: the repo's own evidence (ENH-6, 2026-07-11)
    /// shows 4-component JPEGs upload and generate fine — only their in-app
    /// thumbnail once rendered black.
    /// </summary>
    public static JpegContainerKind ClassifyJpegContainer(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 4) return JpegContainerKind.Plain;
        if (bytes[0] != 0xFF || bytes[1] != 0xD8) return JpegContainerKind.Plain; // SOI required

        var sawMpf = false;
        var i = 2;
        for (var segments = 0; segments < 64; segments++)
        {
            if (i >= bytes.Length - 1) return JpegContainerKind.Plain; // EOF before SOS
            if (bytes[i] != 0xFF) return JpegContainerKind.Plain;      // misaligned — malformed
            // Consume FF fill bytes (a marker may be preceded by any number of FFs).
            while (i < bytes.Length - 1 && bytes[i + 1] == 0xFF) i++;
            if (i >= bytes.Length - 1) return JpegContainerKind.Plain;

            var marker = bytes[i + 1];
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2; // standalone marker — no length field
                continue;
            }
            if (marker == 0xD9)
                return JpegContainerKind.Plain; // EOI before SOS — not a decodable frame

            if (i + 3 >= bytes.Length) return JpegContainerKind.Plain;
            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (length < 2) return JpegContainerKind.Plain;
            var segmentEnd = (long)i + 2 + length;
            if (segmentEnd > bytes.Length) return JpegContainerKind.Plain; // truncated

            if (marker == 0xDA)
                // Valid SOS — its own length header validated above like every other
                // segment (a truncated/corrupt SOS must not classify; Codex diff r1).
                return sawMpf ? JpegContainerKind.MultiPicture : JpegContainerKind.Plain;

            if (marker == 0xE2 && length >= 6
                && bytes[i + 4] == (byte)'M' && bytes[i + 5] == (byte)'P'
                && bytes[i + 6] == (byte)'F' && bytes[i + 7] == 0x00)
            {
                sawMpf = true; // still requires a clean walk to SOS below
            }

            i = (int)segmentEnd;
        }
        return JpegContainerKind.Plain; // segment cap hit — refuse to vouch
    }

    /// <summary>
    /// The one user-facing string for a reference we will not send. Rewritten rather than appended
    /// to when AVIF joined: "…, or WebP" plus a tacked-on clause would drift past the pill's
    /// 55-code-unit budget. Shared so the picker and the read gate cannot advertise different sets —
    /// and it names AVIF only when this machine can actually read it, so a codec-less user is never
    /// told to use a format that would then be refused.
    /// </summary>
    public static string UnsupportedReferenceTypeMessage
        => AvifSupported()
            ? "Use PNG, JPEG, WebP, or AVIF references"
            : "Use PNG, JPEG, or WebP references";

    /// <summary>
    /// Whether AVIF may be offered and accepted. Injectable so tests can exercise BOTH machines
    /// (codec present and absent) without installing an OS component; production reads the real
    /// pixel-decode probe.
    ///
    /// <para>This sits inside <see cref="MimeFromExtension"/> deliberately. That method is the single
    /// question every reference path asks — Browse, seeded redo references, "Use last image", and
    /// Iterate's visibility gate — so putting the capability check anywhere else would have left
    /// those paths attaching AVIFs that generation could not process (Codex diff review r2: the
    /// first version gated only the file picker).</para>
    /// </summary>
    internal static Func<bool> AvifSupported { get; set; } = () => ImageCodecSupport.CanDecodeAvif;

    /// <summary>
    /// Every extension a reference may be picked with, right now, on this machine — the list the
    /// file picker offers.
    ///
    /// <para>Derived from <see cref="MimeFromExtension"/> rather than hand-maintained beside it, so
    /// the picker cannot drift from what the read gate accepts. The first version listed the
    /// extensions inline in <c>App.xaml.cs</c> and immediately drifted: AVIF was added to the policy
    /// and the picker kept offering four types.</para>
    ///
    /// <para>Capability-aware by construction: <c>.avif</c> appears only when this machine can
    /// decode it, because <see cref="MimeFromExtension"/> is what decides that.</para>
    /// </summary>
    public static IReadOnlyList<string> SupportedReferenceExtensions
    {
        get
        {
            var supported = new List<string>(ReferenceFormats.Length);
            foreach (var (ext, _, requiresCodec) in ReferenceFormats)
                if (!requiresCodec || AvifSupported()) supported.Add(ext);
            return supported;
        }
    }

    /// <summary>
    /// THE reference-format table: extension, the mime it maps to, and whether it needs an optional
    /// OS codec. Both <see cref="MimeFromExtension"/> and
    /// <see cref="SupportedReferenceExtensions"/> read this — a parallel list beside the mime switch
    /// was the previous shape and it drifted immediately (AVIF was accepted by the gate while Browse
    /// kept offering four types). Adding a format here reaches the gate AND the picker at once.
    ///
    /// <para>Mime is never inferred: foreign bytes must not default to <c>image/png</c>, because the
    /// value rides the provider request verbatim.</para>
    /// </summary>
    private static readonly (string Extension, string Mime, bool RequiresOptionalCodec)[] ReferenceFormats =
    {
        (".png",  "image/png",  false),
        (".jpg",  "image/jpeg", false),
        (".jpeg", "image/jpeg", false),
        (".webp", "image/webp", false),
        // AVIF is accepted for SELECTION only, and only when this machine can DECODE it. It never
        // reaches a provider as image/avif — the read gate converts it to PNG (no provider documents
        // AVIF, and ImagePayloadWriter refuses any mime outside png/jpeg/webp). Accepting it without
        // that conversion, or without the codec that makes the conversion possible, trades "cannot
        // attach" for "attaches, then fails".
        (".avif", "image/avif", true),
    };

    /// <summary>
    /// Extension → mime for the supported reference formats, read from <see cref="ReferenceFormats"/>
    /// so this and <see cref="SupportedReferenceExtensions"/> can never disagree. Anything else —
    /// including a codec-gated format this machine cannot decode — returns null and the selection is
    /// rejected; foreign bytes are NEVER defaulted to image/png.
    /// </summary>
    public static string? MimeFromExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string ext;
        try { ext = Path.GetExtension(path); }
        catch { return null; }

        ext = ext.ToLowerInvariant();
        foreach (var (extension, mime, requiresCodec) in ReferenceFormats)
        {
            if (extension != ext) continue;
            return !requiresCodec || AvifSupported() ? mime : null;
        }
        return null;
    }

    /// <summary>
    /// The shared "usable app-owned image" predicate (ENH-6): the path canonicalizes
    /// inside <paramref name="imagesDir"/> (MediaPathPolicy containment — corrupt DB
    /// rows rejected) AND the file exists. Backs the History Iterate visibility rule
    /// and History's image existence checks. NOTE: lexical containment only — the
    /// upload gate (<see cref="TryOpenVerifiedAppImage"/>) additionally verifies the
    /// kernel-resolved final path so reparse points can't escape.
    /// </summary>
    public static bool IsUsableAppImagePath(string? path, string imagesDir, out string fullPath)
    {
        fullPath = string.Empty;
        return MediaPathPolicy.TryResolveWithin(path, imagesDir, out fullPath)
            && File.Exists(fullPath);
    }

    /// <summary>
    /// "Usable AS A REFERENCE": whitelisted extension, VERIFIED containment (the same
    /// kernel-resolved gate the upload uses — <see cref="TryOpenVerifiedAppImage"/>),
    /// and 0 &lt; length ≤ <see cref="MaxBytes"/>. Backs the Use-last-image scan so it
    /// never offers an image the generation-time validation would reject (oversized,
    /// foreign extension, OR a junction-reached path) — it falls through to the
    /// next-newest usable one instead (Codex diff rounds 1+2, 2026-07-11).
    /// </summary>
    public static bool IsReferenceUsableAppImagePath(string? path, string imagesDir, out string fullPath)
    {
        fullPath = string.Empty;
        if (MimeFromExtension(path) == null) return false;
        if (!MediaPathPolicy.TryResolveWithin(path, imagesDir, out fullPath)) return false;

        using var stream = TryOpenVerifiedAppImage(path, imagesDir);
        if (stream == null || stream.Length == 0 || stream.Length > MaxBytes)
        {
            fullPath = string.Empty;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The AppImages UPLOAD gate (ENH-6, Codex diff review 2026-07-11): opens the file
    /// and verifies the KERNEL-RESOLVED final path of the opened handle is inside the
    /// kernel-resolved final path of <paramref name="imagesDir"/> before any byte is
    /// read. Lexical containment (<see cref="MediaPathPolicy.TryResolveWithin"/>) can't
    /// see reparse points — a junction/symlink planted inside the Images folder could
    /// otherwise turn a crafted history row into an upload of a file OUTSIDE the app's
    /// own data. Resolving BOTH sides keeps a legitimately-junctioned profile
    /// (e.g. relocated %LOCALAPPDATA%) working: both resolve through the same links.
    /// Returns the open read stream on success (read from THIS stream — no re-open, no
    /// TOCTOU window); null on any containment or IO failure (fail closed).
    /// </summary>
    public static FileStream? TryOpenVerifiedAppImage(string? path, string imagesDir, bool asyncIo = false)
        => TryOpenVerifiedAppImage(path, imagesDir, out _, asyncIo);

    /// <summary>
    /// Overload exposing the KERNEL-RESOLVED final path of the verified file via
    /// <paramref name="verifiedPath"/> (empty on failure). Consumers that must hand a
    /// PATH to another component (ShellExecute, BitmapImage) use this so they act on the
    /// physical resolved target that was verified — never the junction-bearing persisted
    /// string, which could be re-pointed between check and use.
    /// <paramref name="asyncIo"/> opens the handle with <see cref="FileOptions.Asynchronous"/>
    /// so ReadAsync is genuinely interruptible (the async reference read uses it); the
    /// default stays synchronous for the existing sync consumers.
    /// </summary>
    public static FileStream? TryOpenVerifiedAppImage(
        string? path, string imagesDir, out string verifiedPath, bool asyncIo = false)
        // REL-17: the open+verify body moved to the SHARED VerifiedFileAccess gate (the
        // support bundle's recording reads and the GDPR Debug reads reuse it); this
        // remains the image-domain entry point and keeps its public contract unchanged.
        => VerifiedFileAccess.TryOpenVerifiedUnder(path, imagesDir, out verifiedPath, asyncIo);

    /// <summary>
    /// Kernel-verified path resolution for READ-side consumers of app-owned images
    /// (History preview / open-in-shell / copy): same gate as the upload
    /// (<see cref="TryOpenVerifiedAppImage(string?, string, out string)"/>), returning the
    /// verified FINAL path instead of the stream. Lexical containment alone would pass a
    /// junction planted inside the images dir (<c>Images\junction\outside.png</c>) —
    /// resolving through the opened handle can't be fooled by reparse points, and acting
    /// on the returned physical path is immune to the junction being re-pointed afterwards.
    /// </summary>
    public static bool TryGetVerifiedAppImagePath(string? path, string imagesDir, out string verifiedPath)
    {
        using var stream = TryOpenVerifiedAppImage(path, imagesDir, out verifiedPath);
        return stream != null;
    }

}

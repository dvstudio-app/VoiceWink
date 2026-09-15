using Serilog.Core;
using Serilog.Events;

namespace VoiceWink.Services.System;

/// <summary>
/// Serilog enricher that redacts secrets from log event properties before
/// they reach the <b>remote-bound</b> sinks. Wired into the Sentry sub-logger
/// only — the local file sink and Debug sink intentionally see un-redacted
/// events so on-device troubleshooting still has full detail (response bodies,
/// stack traces, etc.). Privacy redaction for the user-triggered Support-zip
/// path (REL-3) happens at bundle time via <see cref="RedactString"/>.
///
/// Redaction happens at two levels:
///   1. <b>Property name</b> — any scalar property whose name is in
///      <see cref="RedactedPropertyNames"/> is replaced with <c>&lt;REDACTED&gt;</c>
///      regardless of value. Covers cases like <c>Log.Information("{ApiKey}", key)</c>.
///   2. <b>Value pattern</b> — scalar string properties are regex-scanned for
///      known key shapes (Anthropic / OpenAI / OpenRouter / Groq / Gemini),
///      auth headers, and LemonSqueezy license key format.
///
/// Note: string-concatenated log calls (<c>Log.Information("Key=" + key)</c>) bake
/// the secret into the message template, which Serilog treats as immutable. The
/// project-wide audit sweep (REL-2) flags those call sites — do NOT rely on the
/// enricher alone. Use structured logging (<c>{PropertyName}</c>).
///
/// <b>Known limitation — non-scalar property values.</b> Values arriving as
/// <see cref="StructureValue"/> / <see cref="SequenceValue"/> / <see cref="DictionaryValue"/>
/// are <i>not</i> recursed into: the value arm below matches only
/// <see cref="ScalarValue"/> holding a <see cref="string"/>.
/// <b>A <c>{@}</c> audit is NOT a sufficient regression check for this</b>, and saying so is the
/// correction — an ordinary <c>{Values}</c> collection property becomes a
/// <see cref="SequenceValue"/> with no destructuring operator anywhere, so the grep that used to
/// justify this paragraph ("zero <c>{@}</c> sites") was answering a narrower question than the one
/// that matters (REL-31 self-review). The NAME layer still covers any allowlisted property
/// whatever its value shape, which is what keeps the gap benign: the exposure needs a
/// NON-sensitive name carrying user data in a collection at Error or above, and no such site
/// exists today. Tracked as REL-33, whose fix is recursion here — but note that alone does not
/// protect the REL-3 support zip or the LGL-2 GDPR export, which scrub already-RENDERED lines
/// where the collection has been formatted into text; those need an emit-time contract (Codex).
///
/// The consent audit log (<c>consent.log</c>) is written directly with
/// <c>File.AppendAllLines</c> and bypasses Serilog — so GDPR-defensible acceptance
/// receipts are not accidentally scrubbed by this enricher.
/// </summary>
public sealed class LogRedactionEnricher : ILogEventEnricher
{
    // internal, not private: SentryInitializer's Extra scrub (REL-31) applies the NAME layer
    // itself — it cannot go through RedactNamedValue, which bypasses the RedactOverride test
    // seam — so it needs the same marker rather than a second string literal to drift from.
    internal const string RedactedMarker = "<REDACTED>";

    // Ordered most-specific-first. "sk-ant-" must be matched before "sk-"; same
    // for "sk-or-v1-" (OpenRouter). Compiled for hot-path perf.
    private static readonly global::System.Text.RegularExpressions.Regex[] KeyPatterns =
    {
        new(@"sk-ant-[A-Za-z0-9_-]{30,}",
            global::System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"sk-or-v1-[A-Za-z0-9]{32,}",
            global::System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"sk-[A-Za-z0-9_-]{20,}",
            global::System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"gsk_[A-Za-z0-9]{40,}",
            global::System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"AIza[0-9A-Za-z_-]{35}",
            global::System.Text.RegularExpressions.RegexOptions.Compiled),
    };

    private static readonly global::System.Text.RegularExpressions.Regex HeaderPattern =
        new(@"(Bearer\s+|X-Api-Key:\s*|X-Goog-Api-Key:\s*|Api-Key:\s*|CF-Access-Client-Id:\s*|CF-Access-Client-Secret:\s*)\S+",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // REL-32: `Authorization:` has its own pattern because it is the one header whose value carries
    // a SCHEME WORD before the credential, and that broke the shared shape above. While it was an
    // arm of HeaderPattern, the leftmost match on "Authorization: Bearer <token>" started at the
    // header NAME, `\S+` consumed the word "Bearer", and Regex.Replace resumed scanning past it —
    // so the `Bearer\s+` arm could never fire on the token it existed for, and the credential
    // survived. `Authorization: Token <key>` (Deepgram's scheme) had the identical shape.
    //
    // It redacts the WHOLE field value to end of line, plus any folded continuation, and that is
    // the only bound that works — TWO earlier shapes were tried against real inputs and both
    // leaked, so this comment records them rather than leaving the next reader to rediscover them:
    //
    //   1. `Authorization:\s*(?:\S+\s+)?` as the CAPTURED prefix (proposed in review). Group 1 is
    //      preserved verbatim by the replacement, so a schemeless header followed by prose put the
    //      credential straight back: "Authorization: MySecretToken42 and then more prose" →
    //      "Authorization: MySecretToken42 <REDACTED> then more prose".
    //   2. `(Authorization:\s*)(?:\S+\s+)?\S+` — scheme outside the capture, so 1 was fixed, but a
    //      two-token bound cannot hold a PARAMETER LIST: "Authorization: Digest username=\"alice\",
    //      realm=\"api\", response=\"digest-proof\"" matched only through `username="alice",` and
    //      left the response hash intact. Digest, Signature and HOBA all have this shape.
    //
    // Both were measured, not reasoned, and both are pinned by tests below. The lesson generalises:
    // there is no scheme-shaped boundary that is safe for a value an ATTACKER OR A PROVIDER writes,
    // which is exactly what `.claude/rules/diagnostics.md` already concluded for SEC-3's
    // `capProcs=` / `appMode=` tokens — "end-of-line is the one terminator a caller-supplied value
    // cannot forge". This pattern is that rule applied here, and it should have been the first
    // choice rather than the third.
    //
    // ACCEPTED COST, stated rather than discovered later: everything after `Authorization:` on that
    // line is lost, including diagnostic prose and the scheme name, and a line that merely CONTAINS
    // the word (say "Payment authorization: declined for order 123") loses its tail too. Over-
    // redaction is the safe direction; a survived credential is not.
    private static readonly global::System.Text.RegularExpressions.Regex AuthorizationHeaderPattern =
        new(@"(Authorization:[^\S\r\n]*)[^\r\n]*(?:(?:\r\n|\r|\n)[ \t]+[^\r\n]*)*",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // License keys, two shapes (F43):
    // 1. Grouped keys — 4+ groups of 4 UPPERCASE alphanumerics (AAAA-BBBB-CCCC-DDDD).
    //    Deliberately case-sensitive: with IgnoreCase it would swallow ordinary kebab-case
    //    identifiers ("test-data-file-name") that real grouped keys never lowercase into.
    // 2. REAL LemonSqueezy keys are UUID v4 (8-4-4-4-12 hex, e.g.
    //    38b1460a-5e79-4c11-8b09-1170cb87c47c) — the grouped pattern structurally cannot
    //    match them, so this defense-in-depth layer was inert against the actual key shape.
    //    Deliberate over-match: any dashed UUID in a Sentry-bound string redacts, including
    //    activation instance ids — an acceptable trade, since a UUID in a log line is
    //    indistinguishable from a license key. The local file sink is unaffected.
    private static readonly global::System.Text.RegularExpressions.Regex LsLicensePattern =
        new(@"\b[A-Z0-9]{4}(-[A-Z0-9]{4}){3,7}\b",
            global::System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly global::System.Text.RegularExpressions.Regex LsLicenseUuidPattern =
        new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Windows user-profile paths leak the account name (often a real person's name = PII) when a
    // file/DB exception or a path-bearing log line reaches a REMOTE-bound sink. Fold the username
    // segment of "X:\Users\<name>\..." (either slash direction) to "<USER>", keeping the path tail.
    // The username may contain spaces/apostrophes (e.g. "Jane Doe", "O'Connor"), so the segment runs
    // to the NEXT path separator — the lookahead requires one, which both (a) supports spaced names
    // and (b) stops the match from swallowing trailing prose when there is no continuation. Scope is
    // local DRIVE paths only (%LOCALAPPDATA% is never UNC/roaming); a bare "C:\Users\<name>" with no
    // trailing separator is intentionally left as-is. One pattern here covers the Sentry path, the
    // Support-zip, AND the GDPR export — all route through RedactString. (Codex + adversarial review.)
    // SEC-4 r3: the root alternation now matches UNC as well as a drive letter. It was
    // drive-only, and `UserDocumentPathPattern` CAPTURES the username in its group 1 and preserves
    // it — so `\\server\share\Users\Jane\Documents\Acme\note.csv` folded the tail and left
    // `\\server\share\Users\Jane\<USERPATH>`, i.e. the username survived on exactly the paths the
    // tail fold was extended to cover. My own test asserted `Acme` and `note.csv` were gone and
    // never checked `Jane` (Codex diff r3) — the two patterns must cover the same root shapes or
    // one silently undoes the other's guarantee.
    private static readonly global::System.Text.RegularExpressions.Regex UserProfilePathPattern =
        new(@"((?:[A-Za-z]:|\\\\\?\\UNC\\[^\\/\r\n]+\\[^\\/\r\n]+|\\\\[^\\/\r\n]+\\[^\\/\r\n]+)[\\/]Users[\\/])([^\\/\r\n]+)(?=[\\/])",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // SEC-4: the pattern above folds only the USERNAME, and that is not enough for a path that
    // names the user's own work — `…\Documents\Acme\medical-note.csv` keeps the client and the
    // subject. Emit sites route user-chosen paths through LogPathProjection, but that cannot
    // reach the biggest remaining channel: an ATTACHED EXCEPTION. An IOException or
    // UnauthorizedAccessException carries the full path in its Message, several export/import
    // failure sites log `Logger.Error(ex, …)`, and SentryInitializer's BeforeSend runs
    // RedactString over exception values — so folding here closes that class generically instead
    // of chasing individual catch blocks.
    //
    // The AppData carve-out is what keeps this safe to apply broadly: every app-owned path lives
    // under `…\Users\<name>\AppData\…` (models, recordings, logs, settings) and stays fully
    // readable, because those name the APP's files, not the user's. Anything else directly under
    // the profile — Documents, Desktop, Downloads, OneDrive — is the user's own tree and folds.
    // SCOPE, stated plainly because the first version of this comment over-claimed: this closes
    // PROFILE-ROOTED paths only. A user-chosen path on another root — `D:\Clients\…` — or on a UNC
    // share that is not `\\host\share\Users\…` is NOT matched, and no regex can recognise an
    // arbitrary filesystem path safely (Codex diff r2). The emit-site projection is the real
    // control for paths the app itself logs; this covers the one channel projection cannot reach,
    // an ATTACHED EXCEPTION, for the profile-rooted case that dominates in practice. The residual
    // is recorded in .claude/rules/diagnostics.md rather than papered over.
    //
    // The carve-out names THIS APP's roots EXACTLY, and got narrower twice under review. A blanket
    // `AppData` exemption left `…\AppData\Roaming\OtherCorp\Acme-client.json` readable (Codex r2);
    // narrowing to `AppData\Local\(VoiceWink*|Temp)` still exempted `VoiceWinkNotes` — any folder
    // merely STARTING with our name — and the whole of `%LOCALAPPDATA%\Temp`, which is SHARED, so
    // `…\Temp\OtherCorp\Acme-client.json` survived intact (Codex r3). The exemption is now:
    //   • `AppData\Local\VoiceWink\`    — AppPaths' root
    //   • `AppData\Local\VoiceWinkApp\` — Velopack's install root
    //   • `AppData\Local\Temp\voicewink-report-<yyyyMMdd-HHmmss>.zip[.tmp]` — the legacy report
    //     bundles ONLY, matching the exact shape `ReportSendFlow` generates. A bare
    //     `voicewink-report-` PREFIX was the r4 version and accepted any suffix and extension, so
    //     a user-chosen `voicewink-report-Acme-patient.wav` stayed readable (Codex r4)
    // Every other application's data under the profile folds like any other user content.
    //
    // THE EXEMPTION IS DRIVE-ROOTED ONLY, and that is the r4 blocking fix: `%LOCALAPPDATA%` is by
    // definition local, so a UNC path merely SHAPED like it —
    // `\\server\share\Users\Jane\AppData\Local\VoiceWink\Acme\medical-note.csv` — is somebody's
    // file share, not our storage. The combined pattern exempted it, folding the username while
    // the sensitive tail survived: the same "two rules disagreeing about scope" defect as r3, one
    // round later. UNC tails now fold UNCONDITIONALLY, in their own pattern with no carve-out.
    //
    // `\\?\C:\…` is handled by the UNC pattern, NOT this one — the generic `\\host\share` arm
    // claims it (host `?`, share `C:`) before the drive arm ever sees it. So an extended-length
    // LOCAL path gets no app-root exemption and folds whole. That is over-redaction, the safe
    // direction, and it is stated here because an earlier version of this comment claimed the
    // opposite — that `\\?\C:\` "stays on the drive side" — which was simply wrong about which
    // pattern matches (Kimi diff r5). Do not "restore" the carve-out for it on the strength of a
    // comment; measure first.
    //
    // The `.zip[.tmp]` terminator is `(?![^\s"])` — the next character must be whitespace, a
    // quote, or end of line. `(?:[\\/]|$)` was the r4 version and accepted a SEPARATOR, so
    // `…\Temp\voicewink-report-20260807-101500.zip\Acme\medical-note.csv` — the bundle name worn
    // as a DIRECTORY — was exempted whole (Codex r5). A plain `(?![\\/])` is not enough either:
    // the optional `\.tmp` backtracks, and `.zip.tmp\Acme\…` would re-match at `.zip` with `.`
    // ahead, passing the lookahead. Requiring a non-path character closes both.
    //
    // Both tails exclude `"` (a quoted path's terminator) but deliberately NOT `'` — apostrophes
    // are legal in Windows path components, and stopping at one left
    // `…\Documents\<USERPATH>'Brien\medical-note.csv`: a PARTIAL redaction that reads as complete,
    // which is worse than none (Codex diff r2). The cost is that an unquoted path followed by
    // prose eats the prose — over-redaction, the safe direction.
    private static readonly global::System.Text.RegularExpressions.Regex UserDocumentPathPattern =
        new(@"([A-Za-z]:[\\/]Users[\\/][^\\/\r\n]+[\\/])(?!AppData[\\/]Local[\\/](?:VoiceWink(?:App)?[\\/]|Temp[\\/]voicewink-report-\d{8}-\d{6}\.zip(?:\.tmp)?(?![^\s""])))[^\r\n""]*",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // The UNC twin: same tail rule, NO app-root carve-out. See the block above for why.
    private static readonly global::System.Text.RegularExpressions.Regex UncUserDocumentPathPattern =
        new(@"((?:\\\\\?\\UNC\\[^\\/\r\n]+\\[^\\/\r\n]+|\\\\[^\\/\r\n]+\\[^\\/\r\n]+)[\\/]Users[\\/][^\\/\r\n]+[\\/])[^\r\n""]*",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // PRM-3: Deepgram vocabulary rides in the request URL as keyterm=/keywords= query
    // values — Dictionary words are often personal names, and a failure path echoing a
    // request URL must not carry them into Sentry, the support zip, or the GDPR log
    // export. Values redact; the param NAMES survive so diagnostics stay readable.
    // The value consumes up to the next '&', quote, or end of line — NOT stopping at
    // whitespace, because .NET's Uri.ToString() DECODES %20 back to literal spaces
    // (Codex diff review High: "keyterm=customer%20service" logged decoded left
    // "service" exposed). Deliberate over-redaction: prose following a decoded value
    // on the same line may be consumed — privacy wins over log cosmetics.
    private static readonly global::System.Text.RegularExpressions.Regex VocabularyQueryPattern =
        new(@"([?&](?:keyterm|keywords)=)([^&""'\r\n]*)",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // REL-16: the clipboard-probe emitter logs the clipboard OWNER's process name as
    // ownerProc="<name>" — an installed-app name on the user's machine, PII-adjacent, so it
    // must not leave the device. The {OwnerProcess} property-name redaction covers the Sentry
    // breadcrumb path, but the Support zip and GDPR log export process already-RENDERED lines
    // through RedactString (property names are gone there) — this value pattern is what
    // scrubs those. The emitters always quote the value, so the match is exactly bounded.
    // HKY-2 joined the alternation: the watchdog's foreground-context line renders the
    // foreground process name as foregroundProc="<name>" — same PII class, same two layers
    // ({WatchdogForegroundProcess} property name + this rendered-value scrub).
    private static readonly global::System.Text.RegularExpressions.Regex OwnerProcessPattern =
        new(@"((?:ownerProc|foregroundProc)="")([^""]*)("")",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // SEC-3 (2026-08-07) — two rendered-line scrubs anchored to END-OF-LINE rather than to a
    // closing delimiter.
    //
    // The quoted shape used by OwnerProcessPattern above is safe there only because a Windows
    // PROCESS name cannot contain a quote. It is NOT safe for these: an audio endpoint's
    // FriendlyName is arbitrary driver text and an App Mode label is user-authored, so either
    // could embed a quote, `[^"]*` would stop early, and the tail would survive into a support
    // bundle. End-of-line is the one terminator a caller-supplied value cannot forge — which is
    // why the emit sites keep these tokens LAST and pass every value through
    // LogValueSanitizer.SingleLine (a raw CR/LF would otherwise split the line and leave the
    // remainder outside the match; the bundle and GDPR export scrub line by line).
    //
    // `$` without RegexOptions.Multiline still matches before a trailing newline, so a single
    // rendered line is handled whether or not the caller stripped its terminator.
    private static readonly global::System.Text.RegularExpressions.Regex CaptureSessionProcessPattern =
        new(@"((?<![^\s])capProcs=).*$",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Covers BOTH App Mode tokens in one match: the emit sites render `mode=<label>` last, or
    // `mode=<label> proc=<process>` where both are sensitive, so redacting from `mode=` to the
    // end of the line is correct rather than lossy.
    private static readonly global::System.Text.RegularExpressions.Regex AppModePattern =
        new(@"((?<![^\s])appMode=).*$",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ANCHORING: (?<![^\s]) means the token must start the line or follow whitespace. \b`r
    // was tried first and matches after - and . (safe-mode=); (?<![\w.-]) was tried
    // second and still matched after ?, /, : and \" (Codex diff r3). Over-redaction is
    // not the safe failure here — it silently deletes a field from a support bundle.
    // Dictionary source/replacement text (dictTerm= on the three word-replacement failure
    // lines). Same EOL reasoning: the value is whatever the user typed, so it can contain any
    // delimiter. On the two-value line both tokens sit after `dictTerm=`, so one match covers
    // the pair.
    private static readonly global::System.Text.RegularExpressions.Regex DictionaryTermPattern =
        new(@"((?<![^\s])dictTerm=).*$",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // SEC-4: prompt titles, trigger words, and arbitrary import keys all ride `userTerm=`.
    // `prompt` / `triggerword` / `triggerwords` have been on the NAME allowlist since C8, which
    // covers Sentry — but the rendered forms survived into the REL-3 support zip and the LGL-2
    // GDPR export, because those scrub already-rendered lines and the property bag is gone by
    // then (Codex diff r3). One token for all three: every value is user-authored or
    // transcript-derived, so redacting from it to end-of-line is correct rather than lossy.
    private static readonly global::System.Text.RegularExpressions.Regex UserTermPattern =
        new(@"((?<![^\s])userTerm=).*$",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // SEC-4: user-chosen file paths, already projected to filename+extension at the emit site by
    // LogPathProjection. The projection is the primary control (the DIRECTORY tree is what names
    // a client or a subject); this scrub covers the residual, since a file NAME can itself be
    // user-authored ("Acme Q3 layoffs.csv"). App-owned paths under %LOCALAPPDATA% are NOT routed
    // here — they carry no user content and stay readable, as they always have.
    private static readonly global::System.Text.RegularExpressions.Regex UserFilePathPattern =
        new(@"((?<![^\s])filePath=).*$",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // The persisted capture-endpoint NAME — rendered as `micName=` on the pin line and the fallback
    // warning. (This comment said `dev=` until AUD-16; that was already wrong — SettingsViewModel's
    // own comment says the token is "deliberately distinctive rather than a short `dev=`" — and it
    // became dangerous when AUD-16 introduced a real `dev={tag}` token, because a reader trusting it
    // could add an end-of-line `dev=` scrub that ate the tag AND everything after it on that line.)
    // Same end-of-line reasoning as the two above: an endpoint name is arbitrary text a user
    // may have typed, so it can contain a quote and no delimited pattern would bound it.
    private static readonly global::System.Text.RegularExpressions.Regex RecordingDeviceNamePattern =
        new(@"((?<![^\s])micName=).*$",
            global::System.Text.RegularExpressions.RegexOptions.Compiled
            | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly HashSet<string> RedactedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "api_key", "apiKey",
        "authorization", "bearer", "token",
        "transcription", "text",
        // Prompt titles are user-authored (and a trigger word is transcript-derived), so they
        // must not ride into Sentry breadcrumbs. Every {Prompt} log property in the codebase is
        // a prompt title, so redacting the generic name is safe and also covers the sites in
        // AIEnhancementService / MainViewModel. "triggerword" is the SPECIFIC property name used
        // by PromptDetectionService for the matched transcript word — deliberately NOT the generic
        // "trigger", which many non-sensitive diagnostics (MiniRecorder recreate, hotkey restart)
        // use and must keep flowing to Sentry.
        // "triggerwords" is the user-authored trigger LIST (EnhancementViewModel); sensitive
        // custom prompt-title sites log under {Prompt}, never the generic {Title} (dialog/page
        // titles), which stays un-redacted.
        "prompt", "triggerword", "triggerwords",
        // AUD-16: the capture-device tag. It is a PSEUDONYM, not a secret — 8 hex of the endpoint
        // id, not reversible to a name — and the whole point of it is longitudinal same-device
        // correlation in the LOCAL log. That is exactly why it must not reach Sentry: the
        // crash-reporting opt-in is presented to the user as "anonymous crash reports"
        // (SettingsPage / OnboardingPage / README), and a stable per-device pseudonym would let
        // multiple reports be correlated to one machine. Pseudonymous is not anonymous — GDPR
        // Recital 26 draws exactly that line — so keeping the promise means redacting the tag on the
        // Sentry path while the local file, the REL-3 support zip and the GDPR export keep it.
        // Property-name redaction ONLY: deliberately NO rendered-value pattern, because the export
        // paths are precisely where the tag is meant to survive (Codex diff review).
        "devicetag",
        // REL-16: the clipboard-probe emitter's {OwnerProcess} property (clipboard owner's
        // process name — an installed-app name, PII-adjacent). Property-name redaction covers
        // the Sentry sub-logger + BeforeBreadcrumb Data scrub; OwnerProcessPattern below covers
        // the rendered-line export paths. HKY-2's {WatchdogForegroundProcess} (the watchdog
        // restart line's foreground process name) is the same PII class with the same two
        // layers — its rendered token foregroundProc="…" rides the same value pattern.
        "ownerprocess", "watchdogforegroundprocess",
        // SEC-3 (2026-08-07): three more of the same PII class, all on Information-or-above
        // lines and therefore Sentry breadcrumbs. "capturesessionprocesses" is the list of OTHER
        // applications holding an audio session on the mic at recording start; "appmodeprocess"
        // is the foreground process an App Mode matched. "appmodename" is a user-authored label
        // that can name a client or employer — the {Prompt} test, applied to App Mode.
        // The generic {Name} and {Process} are deliberately NOT listed: dialog/page titles and
        // many benign diagnostics use them, which is the same reason {Title} and {Trigger} stay
        // un-redacted. Every emit site was renamed to the specific property instead.
        "capturesessionprocesses", "appmodeprocess", "appmodename",
        // "recordingdevicename" is the PERSISTED capture-endpoint name (the pin line and the
        // fallback warning). It derives from the endpoint FriendlyName — users rename endpoints
        // — but the literal "FriendlyName" never appears at those sites, which is precisely how
        // the first SEC-3 sweep missed them (Codex diff r1). Redacted rather than dropped:
        // unlike the capture-sessions line, no adjacent line carries the useful part.
        "recordingdevicename",
        // Dictionary entries are user-authored and frequently personal names — the same class as
        // "triggerword", already listed above. Found in review after the first two sweeps
        // (Codex diff r2); one of the three sites logs at ERROR, which is a direct Sentry EVENT
        // rather than merely a breadcrumb. Both names are used at those sites only.
        "original", "replacement",
        // SEC-4 (Codex diff r1 on that PR): the rendered-line token is NOT enough on its own.
        // The Sentry path redacts by property NAME and by value PATTERN — and a value like
        // "medical-note.csv" or "someUserKey" contains neither `filePath=` nor `userTerm=`, so a
        // generic {Path}/{Input}/{Key} property survived in breadcrumb DATA even while the
        // rendered message was scrubbed. Specific names, both layers, no exceptions.
        "userfilepath", "importedsettingkey",
        // IMG-4 failure-independence (2026-07-28): the per-item batch failure log carries
        // ex.ToString() under {LocalException} — full exception text (message + stack) for
        // the LOCAL file only. Provider exception messages can embed server-supplied prose
        // (the ProviderApiException reason-phrase class, SEC-2), so Sentry-bound events
        // drop the property by NAME; BeforeBreadcrumb's Data scrub double-covers via
        // RedactNamedValue. Never attach the exception OBJECT to such events — the
        // enricher cannot redact LogEvent.Exception, and Sentry.Serilog copies its
        // message into breadcrumb data.
        "localexception",
        "licensekey", "license_key",
        "body", "errorbody", "requestbody", "response", "promptfeedback", "feedback",
        // SEC-2: the HTTP reason phrase is SERVER-SUPPLIED — a custom OpenAI-compatible endpoint
        // can return arbitrary prose in it. Redacted by NAME rather than by allowlisting
        // {ErrorMessage}, which would blind all 21 of its emit sites (7 source files) to close one
        // channel -- nearly all of them ordinary local exceptions worth keeping in Sentry.
        // Never {@}-destructure a property of this name: this enricher does not recurse (see the
        // known-limitation note above), so a nested value would pass through unscrubbed.
        "reasonphrase",
        "cf-access-client-id", "cf-access-client-secret",
        "cfaccessclientid", "cfaccessclientsecret",
        "cf_access_client_id", "cf_access_client_secret",
    };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Collect replacements first — mutating Properties mid-iteration throws.
        List<LogEventProperty>? replacements = null;

        foreach (var kvp in logEvent.Properties)
        {
            LogEventProperty? replacement = null;

            if (RedactedPropertyNames.Contains(kvp.Key))
            {
                replacement = new LogEventProperty(kvp.Key, new ScalarValue(RedactedMarker));
            }
            else if (kvp.Value is ScalarValue sv && sv.Value is string s && !string.IsNullOrEmpty(s))
            {
                var redacted = RedactString(s);
                if (!ReferenceEquals(redacted, s))
                    replacement = new LogEventProperty(kvp.Key, new ScalarValue(redacted));
            }

            if (replacement != null)
                (replacements ??= new List<LogEventProperty>()).Add(replacement);
        }

        if (replacements != null)
            foreach (var r in replacements)
                logEvent.AddOrUpdateProperty(r);
    }

    /// <summary>
    /// Public for reuse by Sentry's <c>BeforeSend</c> hook (REL-1) and the
    /// Support-zip log-bundling path (REL-3). Returns the original reference
    /// unchanged if no patterns matched, so callers can cheap-compare with
    /// <see cref="object.ReferenceEquals"/>.
    /// </summary>
    public static string RedactString(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        string result = input;
        foreach (var r in KeyPatterns)
            result = r.Replace(result, RedactedMarker);
        // REL-32: before HeaderPattern, so the scheme-carrying form is consumed whole rather than
        // leaving a partly-scrubbed remainder for the shared pattern to pick over.
        result = AuthorizationHeaderPattern.Replace(result, m => m.Groups[1].Value + RedactedMarker);
        result = HeaderPattern.Replace(result, m => m.Groups[1].Value + RedactedMarker);
        result = LsLicensePattern.Replace(result, RedactedMarker);
        result = LsLicenseUuidPattern.Replace(result, RedactedMarker);
        result = UserProfilePathPattern.Replace(result, "$1<USER>");
        // MUST run after the username fold: this consumes the tail, so ordering it first would
        // leave nothing for that pattern to match and the username would survive in group 1.
        result = UserDocumentPathPattern.Replace(result, "$1<USERPATH>");
        result = UncUserDocumentPathPattern.Replace(result, "$1<USERPATH>");
        result = VocabularyQueryPattern.Replace(result, m => m.Groups[1].Value + RedactedMarker);
        result = OwnerProcessPattern.Replace(result, m => m.Groups[1].Value + RedactedMarker + m.Groups[3].Value);
        result = CaptureSessionProcessPattern.Replace(result, m => m.Groups[1].Value + RedactedMarker);
        result = AppModePattern.Replace(result, m => m.Groups[1].Value + RedactedMarker);
        result = RecordingDeviceNamePattern.Replace(result, m => m.Groups[1].Value + RedactedMarker);
        result = DictionaryTermPattern.Replace(result, m => m.Groups[1].Value + RedactedMarker);
        result = UserTermPattern.Replace(result, m => m.Groups[1].Value + RedactedMarker);
        result = UserFilePathPattern.Replace(result, m => m.Groups[1].Value + RedactedMarker);

        return result;
    }

    /// <summary>
    /// True if a property / breadcrumb-data key should have its whole value redacted by NAME
    /// (regardless of value shape) — the same allowlist <see cref="Enrich"/> applies.
    /// </summary>
    internal static bool IsSensitivePropertyName(string? name) =>
        !string.IsNullOrEmpty(name) && RedactedPropertyNames.Contains(name);

    /// <summary>
    /// Two-layer redaction for a named value (mirrors what <see cref="Enrich"/> does to log
    /// properties): a sensitive NAME drops the whole value; otherwise the value is pattern/path
    /// scrubbed via <see cref="RedactString"/>. Lets non-Serilog sinks (the Sentry BeforeBreadcrumb
    /// Data scrub) reuse the property-name layer, not just the value-pattern layer.
    /// </summary>
    internal static string RedactNamedValue(string? name, string? value)
    {
        if (IsSensitivePropertyName(name)) return RedactedMarker;
        return string.IsNullOrEmpty(value) ? string.Empty : RedactString(value);
    }
}

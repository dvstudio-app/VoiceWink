namespace VoiceWink.Helpers;

/// <summary>
/// One model's Models-page rating, and the written basis behind it.
/// </summary>
/// <param name="Model">The id as it appears in CloudModels / PredefinedModels.</param>
/// <param name="Identity">Models sharing an identity are the SAME WEIGHTS on different hosts, so
/// they must carry equal accuracy stars — pinned by test. That is the owner's anchoring rule
/// (followed since 2026-07-31) and it is a PRESENTATION rule, not a measured fact: published
/// benchmark indices score identical Whisper weights very differently depending on who serves
/// them, so accuracy plainly is NOT a property of the model alone. Speed does not follow the
/// rule at all: that is openly the model PLUS its host.</param>
/// <param name="Source">The written basis for the stars, verbatim enough to re-check. Every row
/// carries one — a star with no stated basis is exactly the drift TRN-3 was opened to remove.</param>
/// <param name="Speed">The CPU speed star — the set every machine can honour, and the ONLY set a
/// cloud row or a CPU-only engine carries. Rendered unless the row's engine resolved a GPU
/// backend in this process (TRN-52; see <see cref="GpuSpeed"/>).</param>
internal sealed record ModelRating(
    string Model,
    int Accuracy,
    int Speed,
    string Languages,
    string Source,
    string? Note = null)
{
    public string? Identity { get; init; }

    /// <summary>TRN-52: the GPU speed star — the SECOND measured set, rendered in place of
    /// <see cref="Speed"/> when the row's engine resolved a GPU backend
    /// (<see cref="LocalComputeSnapshot"/>). Null on every cloud row (a network round trip has no
    /// local compute class) and on a CPU-only engine (the sherpa kill-switch row). Carries its own
    /// basis in <see cref="GpuSource"/>, per row and per set — the same rule the CPU column
    /// follows, pinned both ways by test. <b>Accuracy has no GPU counterpart by design</b>: the
    /// card's hard constraint is that accuracy stars are identical across backends, because no
    /// accuracy difference was measured and a per-backend accuracy star would publish a CPU-side
    /// single-clip defect (B112, NOT length-dependent — TRN-54) as a GPU virtue.</summary>
    public int? GpuSpeed { get; init; }

    /// <summary>The written basis for <see cref="GpuSpeed"/> — its measurement, machine and
    /// results file — present exactly when <see cref="GpuSpeed"/> is.</summary>
    public string? GpuSource { get; init; }
}

/// <summary>
/// The Models-page star table (backlog TRN-3), and the honesty rules that keep it from drifting.
///
/// <para><b>Why this exists.</b> The ratings used to be two <c>switch</c> expressions in
/// <c>ModelsPage</c> with the reasoning in a comment. Nothing checked the reasoning, and the
/// accuracy column had accumulated FOUR different evidence sources at once — mixing them inside
/// one column produced a visible inversion where a better-measured model carried fewer stars.
/// TRN-3 moved the rows into this one committed table with a written basis per row.</para>
///
/// <para><b>What each star RESTS ON, and what it does not.</b> The table previously carried
/// measured values read from a third-party benchmark index; those values were REMOVED before
/// go-public per the data-licence decision on backlog LNC-6 (owner decision 2026-08-03, legal
/// addenda A10 — the index's free tier permits internal use, not redistribution, and this file
/// ships in the binary and publishes in the public source snapshot). The stars the measurements
/// had established KEEP their values — removing the citation does not change the judgement it
/// informed. <b>That scope collapsed on 2026-08-30, and this is now a MEASURED table.</b> Every
/// SHIPPING row's ACCURACY star rests on a first-party measurement of that model on this project's
/// own audio, and every LOCAL speed star does too. <b>One stated exception:</b> the sherpa Parakeet
/// row, which compiles only under the <c>PcppEnabled=false</c> kill switch, keeps ★4 by the
/// same-weights anchoring rule — a PRESENTATION choice, because the host-matching rule forbids
/// claiming the GGUF measurement for a different bundle. It is not a shipping row and does not
/// make this a mixed-evidence column. <b>The CLOUD speed column stopped being judgement on
/// 2026-08-31</b>: it is measured, and deliberately LEVELLED — all eight rows carry ★5, because the
/// measurement's finding was that they are not distinguishable. See the cloud-speed block below
/// for the two-network evidence and for what ★5 does and does not promise.</para>
///
/// <para><b>Since TRN-52 (2026-09-02) every LOCAL row carries TWO speed sets, and the page renders
/// the one the row's ENGINE resolved.</b> <c>Speed</c> is the CPU set — the 2026-08-31 laptop
/// ladder below, unchanged — and <c>GpuSpeed</c>/<c>GpuSource</c> is the GPU set, measured on the
/// SAME laptop's integrated Intel Arc 140V (Vulkan, 2026-09-02, WARM) over its pinned 99-clip
/// dictation sample. <b>The GPU set is NOT flat, and that is the measurement:</b> Tiny, Base, Small
/// and Parakeet decode under one second at p50 there (★5), Medium and Large V3 Turbo take 1.4–1.5 s
/// (★4); the desktop's RTX 3080 decodes every row under 0.65 s and is the discrete-class
/// corroboration, not the basis (each row's <c>GpuSource</c> carries its figures and its results
/// files). Which set renders is decided per ENGINE by <see cref="LocalComputeSnapshot"/>:
/// the Whisper rows follow the backend <see cref="WhisperBackendLog"/> pinned or loaded, the
/// Parakeet row follows the child's launch mode; a cloud row has no GPU set (its speed is the
/// network's), and neither has the sherpa kill-switch row (a CPU-only engine). <b>Accuracy has no
/// per-backend counterpart, by design and by test:</b> no accuracy difference was measured between
/// CPU and Vulkan on either dev machine, and the one aggregate "GPU better" came from a single clip
/// where the CPU arm drops 25 words the GPU arm keeps (TRN-54 — one clip, NOT length-dependent) — a per-backend accuracy star would
/// publish that bug as a GPU virtue. Two bounds, stated so nobody reads the GPU column as a
/// universal promise: it is banded on the laptop's INTEGRATED Intel Arc 140V — the CPU set's own
/// machine (owner decision 2026-09-02: one reference machine for both columns) — measured WARM,
/// because a Vulkan backend compiles its pipelines on a model's first exposure to each clip shape
/// and the cold sweep's numbers are lower bounds (kept as evidence); the desktop's RTX 3080 reads
/// ★5 for every local row and is the discrete-class CORROBORATION, not the basis, so a
/// discrete-GPU owner sees an under-promise on Medium and Turbo — the cheaper error. And the two
/// sets are the same machine but different pinned samples, so a CPU figure and a GPU figure here
/// compare by band only, never by number.</para>
///
/// <para><b>The accuracy bands, and an honest note on how they were placed.</b> ★5 &lt;15%,
/// ★4 15–18%, ★3 18–21%, ★2 21–29%, ★1 &gt;29%, cut on whole-corpus WER. <b>One row does not follow
/// them: <c>ggml-large-v3-turbo-q8_0</c> measured 19.3% and carries ★4 by owner override — the one
/// such override, per the note below.</b> Each boundary sits in an
/// EMPTY GAP of the observed distribution rather than through a cluster, and each is robust across
/// its whole gap — the ★1/★2 edge is the clearest case: every value between 27.6% and 30.4% gives
/// the same answer, so nothing here balances on the 29 itself. <b>Review challenged an earlier
/// version of this paragraph as outcome-fitting, and the challenge was fair</b>: it justified the
/// edge by the star it produced instead of by the gap it sits in. The narrowest boundary is
/// ★4/★5 (0.8 pp, between 14.5% and 15.3%), so the GPT-Transcribe-versus-Parakeet distinction is
/// the least robust one on this page and should not be leaned on.</para>
///
/// <para><b>The accuracy corpus, and why it is not TRN-4's.</b> 56 recordings the owner made in
/// ordinary use, transcribed BY THE OWNER from the audio without looking at any app transcript —
/// TRN-22's ground-truth batches, built for a different investigation and reused here. It carries
/// <c>[unclear]</c> markers and a cut-off-word list. Scored through the app's own
/// <c>ITranscriptionService</c> implementations. Results:
/// <c>tools/VoiceWink.AsrBench/results/2026-08-30-*.json</c>. <b>The truth text itself lives
/// OUTSIDE this repository, always</b> — it is dictation, and dictation never enters the repo,
/// which is exactly why the harness had to learn to read an external manifest before this could be
/// measured at all.</para>
///
/// <para><b>Rules that survive the removal, and now govern every re-measurement:</b> one evidence source per
/// column, never mixed; a measurement counts only for the HOST we actually call (published
/// indices score the same Whisper weights very differently across providers, so a value is not
/// a property of the weights alone); a MEASURED star moves only when a measurement moves it,
/// never on a vendor claim; and any future measured row carries its own source and its own
/// read-date — per row, never a shared constant, because a shared date once let a stale
/// inherited number present as freshly read.</para>
///
/// <para><b>Where a star's evidence comes from — the owner-approved order, 2026-08-30.</b> Three
/// sources, ranked by how well each has held up against this project's own audio. It is NOT
/// "take the public number and adjust": the corpus is the ARBITER, not a correction term.
/// <list type="number">
/// <item><b>The Open ASR Leaderboard is PRIMARY for open-weight models.</b> It runs the models
/// itself on standard datasets, and it has been checked rather than trusted — with a result more
/// mixed than the first draft of this paragraph claimed. On the four models it overlaps with this
/// table the TOP TWO agree exactly (Scribe, then Parakeet) and the BOTTOM TWO REVERSE: it puts
/// Turbo ahead of Large V3, our corpus puts Large V3 ahead. <b>That reversal is not dismissible as
/// noise on our side</b> — the same 1.2 pp gap is cited as a real measured difference on the
/// <c>whisper-large-v3</c> row below, and it cannot be a finding there and a rounding error here.
/// Read this as a QUALITATIVE cross-check that the leaderboard is not measuring something
/// unrelated, never as a validated ordering.</item>
/// <item><b>The commercial index is a STARTING HYPOTHESIS for the proprietary cloud rows, never an
/// answer.</b> On the <c>gpt-4o-transcribe</c> / <c>gpt-4o-mini-transcribe</c> pair it ordered
/// the two the OPPOSITE way round from our measurement, and rated the one we measured worst of
/// any cloud row near its own best. It is
/// still the only public source that covers those rows, which is why it is a hypothesis and not
/// simply discarded. <b>Reading it is licensed internal use; reproducing it here is not</b> — its
/// name and its values were stripped from <c>src/**</c> by LNC-6 on 2026-08-23 and must not
/// return, because <c>src/</c> is the first entry in the publish allowlist. <b>LNC-6 scoped its
/// strip to that index alone and deliberately left the Open ASR figures</b> — which is a record of
/// what was decided, NOT a claim that Open ASR's licence permits more: the leaderboard's CODE is
/// Apache-2.0 while its RESULTS dataset carries no explicit top-level licence, so "openly
/// licensed" is an assumption the backlog already flags as unverified. Do not strip the two
/// together on the strength of that decision, and do not widen it into a permission either. <c>NoSourceFileReferencesTheRemovedBenchmarkIndex</c> is the tripwire.</item>
/// <item><b>This project's own corpus is the ARBITER</b>, re-run whenever the catalog changes. It
/// is what caught the ★4 sitting on the worst-measured cloud model in the table, and no public
/// source would have. It needs no new recording — see the corpus paragraph above.</item>
/// </list>
/// <b>TRN-4's scripted recording sitting was RETIRED by the owner on 2026-08-31</b>, after first
/// being repriced P3 → P4. It would have bought keyterm recall, stratified soft/noisy slices and a
/// SECOND SPEAKER. <b>That last one is now a permanent property of this table, not a temporary
/// gap:</b> every SHIPPING accuracy star here rests on a measurement of ONE person's voice, and no
/// shipping figure on this page can be separated from it. (The non-shipping sherpa kill-switch row
/// is the standing exception it always was — a same-weights presentation anchor, not a second host
/// measurement.) Nothing on the Models page waited on the sitting, which is why
/// retiring it was cheap — but a reader comparing these stars against a published index should know
/// that the index has many speakers and this table has one.</para>
///
/// <para><b>ONE OWNER OVERRIDE OF A MEASURED STAR — dated, bound, and called what it is
/// (2026-08-31).</b> <c>ggml-large-v3-turbo-q8_0</c> measured 19.3% against Medium's 17.0%, which
/// the bands put one band apart, and it carries ★4 anyway — level with Medium — because the owner
/// overruled the star. <b>The measurement stands; the STAR is overridden.</b> A first draft of this
/// paragraph dressed the act up as a third epistemic category ("the corpus cannot resolve the
/// row"), and review called the fig leaf correctly: the rule two paragraphs down says a measured
/// star moves only on a measurement, this moved on a decision, and inventing a category to do what
/// a rule forbids is how the rule dies. So the rule survives by exception, not by reinterpretation:
/// this is the one measured star an owner decision overrides, and a second one requires the same
/// thing this one had — the OWNER, in so many words. No session judgement qualifies, whatever clip
/// arithmetic it finds; that is the whole anti-copy bound (nova-3 sits 0.1 pp inside ★4, and a
/// paragraph a session could reuse would walk it off-band next).
///
/// <b>The evidence the owner weighed, all of it recomputable from the two committed results files
/// (<c>results/2026-08-30-ggml-large-v3-turbo-q8_0.json</c>, <c>…-ggml-medium-q8_0.json</c>):</b>
/// the error-count gap is 41 (347 vs 306 over 1800 words), and 31 of it — 76% — is the single clip
/// B101, where Turbo scored 49 errors to Medium's 18 on 104 reference words. (WHY it scored them —
/// a duplicated hallucinated sentence — is a diagnosis from re-running the clip, not a field in
/// those files and not claimed as recomputable here.) Remove that one
/// clip and the rows measure 17.6% against 17.0%,
/// both inside ★4. The owner ruled a 56-clip corpus too small to carry a one-band distinction that
/// one clip decides. TRN-42 recorded these same figures and required ★3; this override supersedes
/// that requirement (its card says so), while its refusal to DELETE B101 stands — the clip stays in
/// the corpus, the measurement stays 19.3%, and only the star is ruled.
///
/// <b>Addendum, same day, after the decode change shipped (temperature 0.2 / no_context / 6
/// threads):</b> the shipped-config re-measure puts Turbo at 18.2% against Medium's 17.1%
/// (<c>results/2026-08-31-*</c>) — the gap HALVES and B101's duplicated sentence no longer
/// reproduces at all. The override's basis figures above are the 2026-08-30 run it was decided
/// against and are kept as its record; the shipped figures strengthen the ruling rather than
/// revisit it. 18.2% is still 0.2 pp inside ★3, so the row remains the one off-band star.</para>
///
/// <para><b>The one way an UNMEASURED star moves: the owner correcting it from their own use of
/// the app.</b> The rule exists to stop a vendor's claim or an inadmissible host's number
/// becoming a star; it was never meant to freeze a row whose value nothing measured in the first
/// place. <b>In the DEFAULT build that scope is empty: every shipping star is measured</b> —
/// accuracy fell in on 2026-08-30 and cloud speed on 2026-08-31. <b>Under
/// <c>PcppEnabled=false</c> it is NOT empty:</b> the sherpa Parakeet row's accuracy is a
/// same-weights presentation choice, and the owner-correction route still applies to it. This
/// sentence has now over-claimed "every star" three times in three PRs by forgetting that branch —
/// scope it to the default build, do not restate it unqualified. It is NOT
/// the local Whisper speed stars: they were in scope for one day — an owner +1 across all five on
/// 2026-08-30, the second such correction after 2026-07-31 — and left it the same day, when the
/// TRN-4 sweep measured them. Read that as the file working rather than as a reversal: a
/// judgement held the column until a measurement could take it, which is the whole design. The
/// PARAKEET speed star was never in scope, measured since TRN-1. A MEASURED star is never
/// overridden this way: once a measurement stands behind one, moving it takes another
/// measurement.</para>
///
/// <para><b>What is NOT claimed.</b> That any star is evidence. The rendered row shows identical
/// stars whatever the basis — an owner decision ("this is quick help, it does not need to be
/// perfect") — so the honesty lives in source only, in each row's <c>Source</c> text.</para>
///
/// <para><b>A known conflict, and it has now been measured rather than reasoned about.</b> The
/// owner reports poor real-world results from <c>gpt-4o-mini-transcribe</c>. The 2026-08-30 corpus
/// did NOT reproduce that report: it measured 16.0% WER over 56 owner-transcribed recordings —
/// mid-field, not poor — while <c>gpt-4o-transcribe</c> measured near-worst at 27.6%. So ★4 rests
/// on that measurement, not on the published indices it used to cite, and the standing owner report
/// is recorded here as unreproduced rather than pending. The remaining caveat is CORPUS SCOPE, one
/// speaker, and no longer a run that might happen: the sitting that would have added a second voice
/// was retired on 2026-08-31.</para>
/// </summary>
internal static class ModelRatings
{
    // The shared basis for the rows whose stars were established by third-party index values
    // before the LNC-6 removal. One const rather than per-row text, deliberately: the per-row
    // provenance those rows used to carry was the thing removed, and re-writing six bespoke
    // paragraphs around an absent number would imply more evidence than remains.
    private const string BenchmarkInformedJudgement =
        "judgement — established against published third-party benchmark indices whose values " +
        "are not reproduced in this repository (data-licence decision, backlog LNC-6 / legal " +
        "addenda A10). This project's own dictation corpus is what replaced judgement with " +
        "measurement on 2026-08-30.";

    // RETIRED 2026-08-30, and the reason is the point: this constant existed because the accuracy
    // cost of requantization had never been measured, so a q8 row could only INHERIT its parent's
    // star. It no longer inherits anything — the q8 build we actually ship was scored directly on
    // the owner's own recordings, so every local row carries its own accuracy number, its own
    // read-date and its own results file, exactly as the speed column already did.
    //
    // Kept only as history in this comment. Do not reintroduce an inheriting constant: the whole
    // failure it encoded was a star standing on an assumption about a build nobody had run.

    // SPEED, local rows: MEASURED on this machine 2026-08-31 at the SHIPPED decode config
    // (6 threads, temperature 0.2, no_context — the config this branch adopted), by the TRN-4
    // sweep (tools/VoiceWink.AsrBench + run-speed-sweep.ps1;
    // results/speed-2026-08-31-*-run-shipped62.json, committed beside this table). 100 real
    // dictation clips re-pinned by CONTENT HASH — 21.1 min of audio from the app's own debug
    // recordings; the folder had GROWN since the 2026-08-30 pin, so per-model numbers are not
    // comparable across the two ladders — band placement is what carries. Thermal control: tiny
    // first and last, 15.96x / 15.42x = 3.4% drift — within the few-percent bar, looser than the
    // previous run's 0.13%, and no band placement below sits within that margin of an edge (the
    // nearest are parakeet and turbo, both ~9.5% clear of their lines: 906 ms vs 1 s, 21.9 s vs
    // 20 s).
    //
    //   model             x real-time      p50       p95
    //   tiny                  15.96x     0.6 s     1.9 s
    //   base                   8.22x     1.2 s     3.9 s
    //   parakeet (4 thr)       7.61x     0.9 s*    5.1 s    *906 ms over the 92 NON-EMPTY decodes
    //   small                  2.67x     3.8 s    11.1 s
    //   medium                 0.92x    11.6 s    24.9 s
    //   large-v3-turbo         0.54x    21.9 s    37.3 s
    //
    // Versus the 2026-08-30 ladder the slower rows moved a lot — small +53%, medium +23%, turbo
    // +15% — but NONE of that is a clean thread measurement: the sample AND the decode config
    // (temperature, no_context) changed in the same step, and tiny's constant-4-thread control
    // across the two samples moved −24% (17.86× → 13.53×,
    // speed-2026-08-31-ggml-tiny-q8_0-t4-pass1.json) — on the sample/config change AND a third
    // variable: the sweep arms ran --language en where both ladders ran auto (the files record
    // it). The clean, same-sample, same-language thread measurement is
    // TRN-46's sweep: +12% for 6-vs-4 on tiny. Band placement above is what this ladder claims.
    // Medium emitted ~12% fewer chars than its neighbours on this sample; speed mode carries no
    // reference text, so that is an observation for TRN-46, not a finding.
    //
    // THE LOCAL BANDS. Local rows use ONE strict p50 scale, below. Cloud ★5 is a SEPARATE
    // documented class (see the cloud-speed block further down) — this was written as "one scale
    // across local and cloud" in the same change that introduced the cloud class, which contradicted
    // it two hundred lines later.
    // Cut on p50, by how long a person waits before the wait
    // changes what they do:
    //
    //   ★5   < 1 s      the text is there when you look up   (LOCAL rows: a strict cut)
    //   ★4   1–3 s      a beat; you wait, but you do not switch away
    //   ★3   3–8 s      a noticeable pause — where the slowest cloud round trip also sits
    //   ★2   8–20 s     long enough to break attention
    //   ★1   > 20 s     you go and do something else
    //
    // Said plainly: those boundaries are a JUDGEMENT about human waiting, anchored on the usual
    // interaction-latency thresholds, and they were checked against this lineup AFTER being
    // chosen. They are not a measured constant. TRN-3's warning about bands "fitted to the handful
    // of values covering our lineup" is the thing to re-read before moving one.
    //
    // TWO RESULTS WORTH KEEPING — both of which contradict what this comment claimed yesterday.
    //
    // 1. LARGE-V3-TURBO IS THE SLOWEST MODEL ON THIS PAGE. The 2026-08-30 version of this comment
    //    asserted it "should comfortably beat" medium. It is slower: 1.59x by THROUGHPUT (0.47x
    //    vs 0.75x real-time) and 1.48x on p50 (23.9 s vs 16.2 s). Both are stated because the
    //    bands above are cut on p50 while the headline ratio is throughput, and an unattributed
    //    "1.6x" beside a p50 table is the kind of number this file exists to stop. The mechanism is
    //    dictation-specific, and it is precisely why published RTF figures mislead here: turbo
    //    keeps the full large encoder (32 layers, 1280-dim) and cuts only the DECODER to 4 layers,
    //    so on short clips the encoder dominates and the decoder saving never pays off. Published
    //    benchmarks measure long-form audio, where it does pay off. Dictation is not that.
    // 2. The published ladder this comment used to rest on was wrong in SHAPE, not merely in
    //    scale. Tiny, base and turbo all measured near HALF their published figures while small
    //    landed on its published 1.5–2x — so it is not a flat machine-speed offset. The base→small
    //    drop is smaller than published; the small→turbo drop is far larger.
    //
    // How the owner's 2026-08-30 +1 fared, since it is what prompted the sweep: right on three
    // rows (base, small, medium), one star short on tiny, one too generous on turbo — which the
    // measurement returns to ★1. Worth recording rather than quietly overwriting: a perception
    // correction that survives measurement on 3 of 5 rows is a good instrument, not a bad one.
    //
    // THE COLUMN WAS MIXED FROM 2026-08-30 TO 2026-08-31, and the note here said the mix "cannot
    // be closed from here" because the harness refuses cloud rows. That was wrong about the
    // EVIDENCE, though right about the mechanism: `speed` mode does still refuse cloud, but `run`
    // mode times the full TranscribeAsync round trip for every model as a byproduct of the accuracy
    // pass — upload, inference and response — so eight cloud p50s already existed in the committed
    // results while this comment said they could not exist.
    //
    // CLOUD SPEED, MEASURED ON TWO NETWORKS (2026-08-30 home, 2026-08-31 corporate Wi-Fi; same 56
    // clips, same code, one variable changed):
    //
    //   model                     home p50   work p50    delta
    //   nova-3                       813 ms     623 ms     -23%
    //   gpt-transcribe               915 ms     789 ms     -14%
    //   gpt-4o-transcribe            791 ms     765 ms      -3%
    //   gpt-4o-mini-transcribe       769 ms     859 ms     +12%
    //   nova-2                       710 ms     987 ms     +39%
    //   scribe_v2                    754 ms    1001 ms     +33%
    //   whisper-large-v3 (Groq)      776 ms          -     rate-limited on the work run
    //   whisper-large-v3-turbo       484 ms          -     rate-limited on the work run
    //
    // SIX rows carry a two-network measurement. The Groq PAIR carries one network only — HTTP 429 on
    // 32 of 56 clips made the corporate run rejection latency, so it was discarded rather than
    // committed. Their ★5 is INHERITED from the levelling decision, not established by their own
    // second measurement, and every claim about "two networks" in this file and the README is scoped
    // to the six accordingly.
    //
    // THE FINDING IS THAT THEY ARE NOT DISTINGUISHABLE. Between-model spread is 205 ms at home and
    // 378 ms at work, while ONE model swings up to 277 ms just from changing network — so the gaps
    // between providers are the same size as the noise. The ordering fully reshuffles: nova-2 is
    // fastest at home and fifth at work; nova-3 is fifth at home and fastest at work; scribe_v2
    // goes second to last. Deltas run BOTH ways (three faster, three slower), so this is variance,
    // not "the corporate network is slower".
    //
    // So the column is levelled at ★5 rather than ranked. What the old ★3/★4/★5 spread encoded was
    // not provider speed: it produced a two-star gap between scribe_v2 and nova-3 while scribe_v2
    // measured 59 ms FASTER — the same visible-inversion failure this file's own header says TRN-3
    // was opened to remove, surviving in the speed column after the accuracy column was fixed.
    //
    // WHAT THE CLOUD ★5 MEANS, STATED AS ITS OWN BAND — because the strict local cut does not
    // survive contact with the data and pretending otherwise was the first version of this note.
    // Review put it plainly: scribe_v2 measures 1001 ms while ★5 above says "< 1 s", so a flat ★5
    // read against the LOCAL cut is indefensible. Two ways out; the owner picked the level, so this
    // file owes an honest band rather than a stretched one.
    //
    // THE CLOUD CLASS: p50 sub-second in normal conditions. Fourteen measurements exist — six rows
    // on two networks each, plus the two Groq rows on one — and THIRTEEN are sub-second. The lone
    // exception is scribe_v2's corporate run at 1001 ms, over by one millisecond. The measured range
    // across every cloud row and both networks is 484–1001 ms. That is the claim: a class that
    // clusters just under a second, not fourteen values each individually below 1000.
    //
    // It is a DIFFERENT band from the local ★5 and must not be read as the same one. The file
    // already says the two halves are not commensurable (below); this is where that stops being an
    // abstraction — a local ★5 is a strict sub-second decode on this CPU, a cloud ★5 is a class that
    // straddles the second on a bad connection. Do not "tidy" this into one cut.
    //
    // A connection worse than either measured network degrades all eight TOGETHER, which is the real
    // content of the old judgement note and is why the LEVELLING is the robust half of this change
    // and the absolute band the softer half. Owner decision 2026-08-31: data-centre inference is
    // fast and uniform, the variable is the network, so rank them equally.
    //
    // STILL NOT COMMENSURABLE ACROSS THE BOUNDARY. Tiny's ★5 is strictly p50 < 1 s; the cloud ★5
    // is the separately documented cloud class above, which straddles the second on a bad connection.
    // One is a network round trip and the other a local decode; they answer to different failure
    // modes. Do not treat the two halves as one scale.
    //
    // LIMITS. One machine, one CPU (8 logical processors; whisper rows decode at the shipped
    // formula's 6 threads, parakeet at its server's 4), so these rank models RELATIVE to each
    // other and are not a spec another user inherits. The machine block's threadsUsed field in the
    // 2026-08-31 files was emitted by a reporting bug (it restated the RETIRED ProcessorCount/2
    // formula, printing 4 for every arm) and was corrected in place to the engine's actual decode
    // threads — the correction and its derivation are in the commit that fixed the field's writer.
    // The ACCURACY files (2026-08-31-*~label-*.json) predate the decode block entirely and were
    // not amended: their configuration lives in the label string and the run's backlog record.
    // Decode only: no capture, no paste, and the no-speech gate does not run. The machine was not
    // idle-locked during the run, so the absolute numbers run slightly pessimistic — deliberately
    // stated without a figure, because the ambient-load reading lived in the run console and the
    // sweep log is NOT committed (it carried local paths). Every number quoted above is
    // re-derivable from the committed results files; nothing here asks you to take a figure on
    // trust that you cannot check.

    // GPU SPEED, local rows (TRN-52, regraded the same day in PR #734): MEASURED 2026-09-02 on the
    // laptop's INTEGRATED Intel Arc 140V (Vulkan) — the CPU ladder's own machine — by the same sweep
    // at the SHIPPED decode config, `--compute auto`, over the laptop's pinned 99-clip sample
    // arc140v-20260902 (1214.2 s; a NEW sample, the 2026-08-31 corpus having been deleted per the
    // harness README, so per-model figures compare with the CPU ladder by BAND only).
    // results/speed-2026-09-02-*-run-gpu-vulkan-arc140v-warm.json, committed beside the CPU files;
    // re-checkable on the laptop only (content-hash pin over a corpus that lives there). Every warm
    // file's `compute` block records whisperBackend=Vulkan (Parakeet: parakeetLaunchMode=Auto,
    // vulkanProbe=Available), so the backend is PROVEN per file rather than read off a label.
    //
    // WARM, and why that word carries the ladder: a Vulkan backend compiles its pipelines on a
    // model's first exposure to each clip shape (TRN-49's phenomenon), and on an integrated GPU
    // that compile is large against 2–3 s clips. The first Arc sweep — COLD, on the PRE-TRN-52
    // harness (no `--compute` switch, no `compute` block: its GPU backend rests on Whisper.net's
    // fall-through and the run label, so those files are NOT a GPU measurement by their own
    // evidence), each model once on fresh shapes — read tiny's thermal pair as pass1 336 ms → pass2
    // 255 ms at p50 (−24%; +40% by throughput, 23.69x → 33.05x; p95 1411 → 822 ms), which no thermal
    // effect produces; so every cold row carried its own compile, and the sweep's
    // fastest-first/repeat-last control was measuring the compile. Those files
    // (…-run-gpu-vulkan-arc140v.json) are committed ONLY as that evidence and as lower bounds; the
    // sweep script gained a discarded per-model warm-up pass (-Warmup) from it.
    //
    //   model             x real-time      p50       p95     cold p50   RTX 3080 p50
    //   parakeet             37.56x     0.22 s*    0.8 s     0.23 s      0.17 s*   *NON-EMPTY (93 of 99)
    //   tiny                 35.05x     0.21 s     0.8 s     0.34 s      0.45 s
    //   base                 25.47x     0.31 s     0.9 s     0.38 s      0.33 s
    //   small                13.62x     0.65 s     1.9 s     0.87 s      0.44 s
    //   medium                6.25x     1.43 s     3.9 s     1.71 s      0.65 s
    //   large-v3-turbo        6.65x     1.48 s     3.9 s     3.72 s      0.56 s
    //
    // BANDS by the p50 scale above: tiny, base, small and parakeet ★5; MEDIUM and TURBO ★4 (1–3 s).
    // Not flat, and that is the measurement: an integrated GPU does not carry a flat column. THERMAL
    // CONTROL: tiny first and last read 35.05x → 38.26x, p50 212 → 200 ms (−6%, the second pass
    // faster — clock variance, not heat); no band placement is within that of an edge (small is 35%
    // inside the sub-second line, medium 43% above it). Idle machine: 16.4% ambient CPU at start,
    // 9.7% at end, High performance, plugged in (from the sweep log, which is not committed —
    // it carries local paths; every other figure here is re-derivable from the committed files).
    //
    // ONE REFERENCE MACHINE FOR BOTH COLUMNS — owner decision 2026-09-02 ("I agree"), after the
    // first version of this ladder banded FLAT ★5 on the desktop's RTX 3080 (results/…-rtx3080.json,
    // every row under 0.65 s; those files stay committed and cited per row as the discrete-class
    // CORROBORATION). Two GPU classes cannot share one honest column: the 3080 is up to 2.6x faster
    // than the Arc on the big models. Banding on the integrated class most laptops have makes a
    // discrete-GPU owner read medium and turbo as an under-promise — the cheaper error against a
    // 1.5 s "★5" on the machine class most users hold. A third star set by GPU class was considered
    // and declined (class detection plus a third column, beyond the card's "one correct column").
    // SHAPE FACTS: the CPU ★1 row, large-v3-turbo, is ★4 here and a ★5 figure on the 3080 — the full
    // large encoder that costs 21.9 s of CPU is what a GPU absorbs, and it also carried the largest
    // compile share (60% of its cold p50); tiny is the fastest Whisper row on the Arc and NOT on the
    // 3080 (where a per-clip pipeline overhead lets base beat it) — a per-GPU fact, not a rule.

    internal static readonly IReadOnlyList<ModelRating> All =
    [
        // ── Cloud ────────────────────────────────────────────────────────────────────────
        new("scribe_v2", 5, 5, "90+ languages",
            "ACCURACY measured on this project's own corpus 2026-08-30: 13.2% WER over 56 owner-" +
            "transcribed recordings — the best of all 14 models scored " +
            "(results/2026-08-30-scribe_v2.json). SPEED measured: p50 754 ms at home, 1001 ms " +
            "on a corporate network (results/2026-08-31-scribe_v2~label-worknet.json) — the " +
            "widest network swing of any row, and why this column is levelled rather than " +
            "ranked. The 1001 ms is ONE millisecond outside the sub-second band; recorded, not " +
            "rounded."),
        new("gpt-transcribe", 5, 5, "Multilingual",
            "ACCURACY measured on this project's own corpus 2026-08-30: 14.5% WER over 56 owner-" +
            "transcribed recordings (results/2026-08-30-gpt-transcribe.json). SPEED measured: " +
            "p50 915 ms at home, 789 ms on a corporate network " +
            "(results/2026-08-31-gpt-transcribe~label-worknet.json) — FASTER on the second " +
            "network, which is why no cloud row is ranked above another."),
        // ★2, DOWN from ★4. The single largest correction this table has ever made, and the one
        // no public index would have produced: the published third-party index covering this model
        // rates it near its own best, and it measured 27.6% here — 13th of 14, behind local Whisper
        // Base. That index is not named and its figure is not reproduced: LNC-6 stripped both from
        // src/** on 2026-08-23 (internal-use-only licence, and src/ publishes), and this comment
        // carried both back until NoSourceFileReferencesTheRemovedBenchmarkIndex was widened to
        // read source files rather than only row strings.
        //
        // Not an artefact: it is SYSTEMATIC, and that much IS recomputable from the committed
        // rows — median 29.3%, 12 of 56 clips above 60% WER, zero API errors. Its errors are real
        // substitutions and deletions rather than a scoring artefact.
        new("gpt-4o-transcribe", 2, 5, "Multilingual",
            "ACCURACY measured on this project's own corpus 2026-08-30: 27.6% WER over 56 owner-" +
            "transcribed recordings; median 29.3%, and 12 of the 56 clips score above 60% " +
            "(results/2026-08-30-gpt-4o-transcribe.json — every WER figure here is recomputable " +
            "from that file's rows). SPEED measured: p50 791 ms at home, 765 ms on a corporate " +
            "network (results/2026-08-31-gpt-4o-transcribe~label-worknet.json). This project " +
            "separately observed prompt echo from this model on near-silent audio " +
            "(OpenAITranscriptionParameters)."),

        // Was ★3 while nova-3 was ★4 on a worse measured value — the inversion TRN-3 was
        // opened for. Corrected while the measurements were in the table; the corrected
        // ordering stands as judgement now the values are removed.
        new("gpt-4o-mini-transcribe", 4, 5, "Multilingual",
            "ACCURACY measured on this project's own corpus 2026-08-30: 16.0% WER over 56 owner-" +
            "transcribed recordings (results/2026-08-30-gpt-4o-mini-transcribe.json). SPEED is " +
            "measured: p50 769 ms at home, 859 ms on a corporate network " +
            "(results/2026-08-31-gpt-4o-mini-transcribe~label-worknet.json). " +
            "Note the standing owner report of poor real-world results from THIS model is not " +
            "reproduced: it measured mid-field, while gpt-4o-transcribe measured near-worst."),
        new("nova-3", 4, 5, "50+ languages",
            "ACCURACY measured on this project's own corpus 2026-08-30: 17.9% WER over 56 owner-" +
            "transcribed recordings (results/2026-08-30-nova-3.json). SPEED measured: p50 " +
            "813 ms at home, 623 ms on a corporate network " +
            "(results/2026-08-31-nova-3~label-worknet.json) — the FASTEST row on the second " +
            "network and only fifth on the first, which is the reshuffle in one line."),

        // 17.6% on Groq against our local q8 build's 19.3% — the SAME weights, 1.7 pp apart, which
        // is why these two rows do not share an Identity and never should: a requantized build is
        // not the same bits on another host.
        new("whisper-large-v3-turbo", 4, 5, "90+ languages",
            "ACCURACY measured on Groq's endpoint 2026-08-30: 17.6% WER over 56 owner-transcribed " +
            "recordings (results/2026-08-30-whisper-large-v3-turbo.json). SPEED measured: p50 " +
            "484 ms at home — the fastest cloud p50 recorded. The 2026-08-31 corporate-network " +
            "re-run is MISSING for this row: Groq returned HTTP 429 on 32 of 56 clips, so its " +
            "timings were rejection latency and the file was discarded rather than committed. " +
            "★5 is levelled with the other cloud rows, not established on one network.")
            { Identity = "whisper-large-v3-turbo" },

        // Never measured on the host we call: the published figures for these weights vary
        // widely by provider and none of the published runs is Groq's endpoint. Rating this row
        // off another provider's endpoint would be exactly the substitution this table exists
        // to stop, so the star is a judgement and says so.
        // The "no Groq measurement exists for these weights" note is RETIRED: one exists now, taken
        // on Groq's own endpoint, which is exactly what the host-matching rule demanded.
        new("whisper-large-v3", 4, 5, "90+ languages",
            "ACCURACY measured on Groq's endpoint 2026-08-30: 16.4% WER over 56 owner-transcribed " +
            "recordings (results/2026-08-30-whisper-large-v3.json) — 1.2 pp better than Turbo on " +
            "the same host, which is the measured version of upstream's claim that Turbo costs " +
            "little accuracy. SPEED measured: p50 776 ms at home. Like the Turbo row the " +
            "corporate-network re-run is MISSING — Groq rate-limited it (HTTP 429 on 32 of 56 " +
            "clips) and the rejection-latency file was discarded. ★5 by levelling.")
            { Identity = "whisper-large-v3" },

        new("nova-2", 3, 5, "30+ languages",
            "ACCURACY measured on this project's own corpus 2026-08-30: 19.1% WER over 56 owner-" +
            "transcribed recordings (results/2026-08-30-nova-2.json) — the row was 'unmeasured, on " +
            "neither public index' until then, and it is now measured on the only index that " +
            "matters for this app. SPEED measured: p50 710 ms at home, 987 ms on a corporate " +
            "network (results/2026-08-31-nova-2~label-worknet.json) — fastest cloud row on the " +
            "first network, fifth on the second."),

        // ── Local Whisper (q8_0 only since TRN-6, 2026-08-03) ────────────────────────────
        // ACCURACY is the SAME LADDER the full-precision rows carried: quantization changes how
        // the weights are stored, not which weights they are, and nothing measured justifies
        // moving a star in either direction. SPEED no longer inherits anything — every row below
        // carries its own 2026-08-30 measurement of THIS q8 build, which is the only build we
        // ship.
        //
        // NOTE none of these carries an Identity. Identity means IDENTICAL WEIGHTS shown across
        // hosts, and a requantized build is not that — which is why the q5 rows never joined the
        // groups either (Codex diff review: do not put q8 in a group whose meaning is "same bits").
        new("ggml-tiny-q8_0", 1, 5, "90+ languages",
            "ACCURACY measured on this project's own corpus at the SHIPPED decode config, 2026-08-31: " +
            "30.2% WER over 56 owner-transcribed recordings " +
            "(results/2026-08-31-ggml-tiny-q8_0~label-shipped.json; the pre-change 2026-08-30 run " +
            "measured 30.4%). SPEED measured at the SHIPPED config 2026-08-31: 15.96x " +
            "real-time, p50 0.6 s over the re-pinned 100-clip sample " +
            "(results/speed-2026-08-31-ggml-tiny-q8_0-pass1-run-shipped62.json).")
            {
                GpuSpeed = 5,
                GpuSource =
                    "GPU SPEED measured 2026-09-02 on the laptop's Intel Arc 140V (Vulkan, integrated — the " +
                    "CPU set's own machine; owner decision 2026-09-02: one reference machine for both " +
                    "columns), WARM ladder (the file's compute block records whisperBackend=Vulkan): 35.05x " +
                    "real-time, p50 0.21 s over the laptop's pinned 99-clip " +
                    "sample arc140v-20260902 " +
                    "(results/speed-2026-09-02-ggml-tiny-q8_0-pass1-run-gpu-vulkan-arc140v-warm.json; " +
                    "re-checkable on the laptop only — the sample is pinned by content hash over a corpus " +
                    "that lives there). The thermal repeat (…-pass2-thermal-…-warm.json) read 38.26x, p50 " +
                    "0.20 s: −6%, pass2 faster — clock variance, not heat. The COLD sweep's pair " +
                    "(…-run-gpu-vulkan-arc140v.json) read 0.34 s → 0.26 s at p50 (−24%; +40% by throughput), " +
                    "the gap that exposed the pipeline compile inside the timing. The desktop's RTX 3080 " +
                    "corroborates the discrete " +
                    "class at 19.79x, p50 0.45 s (…-run-gpu-vulkan-rtx3080.json), where Base beats it — on " +
                    "the Arc tiny is the fastest Whisper row again.",
            },
        new("ggml-base-q8_0", 2, 4, "90+ languages",
            "ACCURACY measured on this project's own corpus at the SHIPPED decode config, 2026-08-31: " +
            "23.9% WER over 56 owner-transcribed recordings " +
            "(results/2026-08-31-ggml-base-q8_0~label-shipped.json; the pre-change 2026-08-30 run " +
            "measured 24.4%). SPEED measured at the SHIPPED config 2026-08-31: 8.22x " +
            "real-time, p50 1.2 s over the re-pinned 100-clip sample " +
            "(results/speed-2026-08-31-ggml-base-q8_0-run-shipped62.json).")
            {
                GpuSpeed = 5,
                GpuSource =
                    "GPU SPEED measured 2026-09-02 on the laptop's Intel Arc 140V (Vulkan, integrated; the " +
                    "CPU set's own machine), WARM ladder: 25.47x real-time, p50 0.31 s over the laptop's " +
                    "pinned 99-clip sample arc140v-20260902 " +
                    "(results/speed-2026-09-02-ggml-base-q8_0-run-gpu-vulkan-arc140v-warm.json; the cold " +
                    "pass read 0.38 s). The desktop's RTX 3080 corroborates the discrete class at 25.27x, " +
                    "p50 0.33 s (…-run-gpu-vulkan-rtx3080.json), where it is the fastest Whisper row.",
            },
        new("ggml-small-q8_0", 3, 3, "90+ languages",
            "ACCURACY measured on this project's own corpus at the SHIPPED decode config, 2026-08-31: " +
            "19.7% WER over 56 owner-transcribed recordings " +
            "(results/2026-08-31-ggml-small-q8_0~label-shipped.json; the pre-change 2026-08-30 run " +
            "measured 19.9%). SPEED measured at the SHIPPED config 2026-08-31: 2.67x " +
            "real-time, p50 3.8 s over the re-pinned 100-clip sample " +
            "(results/speed-2026-08-31-ggml-small-q8_0-run-shipped62.json) — +53% over the " +
            "pre-change run, NOT a clean thread figure: the sample and the decode config " +
            "changed in the same step (the clean 6-vs-4 measure is TRN-46's +12% on tiny).")
            {
                GpuSpeed = 5,
                GpuSource =
                    "GPU SPEED measured 2026-09-02 on the laptop's Intel Arc 140V (Vulkan, integrated; the " +
                    "CPU set's own machine), WARM ladder: 13.62x real-time, p50 0.65 s over the laptop's " +
                    "pinned 99-clip sample arc140v-20260902 " +
                    "(results/speed-2026-09-02-ggml-small-q8_0-run-gpu-vulkan-arc140v-warm.json) — 35% " +
                    "inside the sub-second line; the cold pass read 0.87 s. The desktop's RTX 3080 " +
                    "corroborates the discrete class at 20.55x, p50 0.44 s (…-run-gpu-vulkan-rtx3080.json).",
            },
        new("ggml-medium-q8_0", 4, 2, "90+ languages",
            "ACCURACY measured on this project's own corpus at the SHIPPED decode config, 2026-08-31: " +
            "17.1% WER over 56 owner-transcribed recordings " +
            "(results/2026-08-31-ggml-medium-q8_0~label-shipped.json; the pre-change 2026-08-30 run " +
            "measured 17.0%). SPEED measured at the SHIPPED config 2026-08-31: 0.92x " +
            "real-time, p50 11.6 s over the re-pinned 100-clip sample " +
            "(results/speed-2026-08-31-ggml-medium-q8_0-run-shipped62.json).")
            {
                GpuSpeed = 4,
                GpuSource =
                    "GPU SPEED measured 2026-09-02 on the laptop's Intel Arc 140V (Vulkan, integrated; the " +
                    "CPU set's own machine), WARM ladder: 6.25x real-time, p50 1.43 s over the laptop's " +
                    "pinned 99-clip sample arc140v-20260902 " +
                    "(results/speed-2026-09-02-ggml-medium-q8_0-run-gpu-vulkan-arc140v-warm.json) — ★4 by " +
                    "the 1–3 s band; the cold pass read 1.71 s. The desktop's RTX 3080 decodes it in 0.65 s " +
                    "(15.10x; …-run-gpu-vulkan-rtx3080.json), a ★5 figure the set does NOT carry: one GPU " +
                    "column, banded on the integrated class most laptops have (owner decision 2026-09-02) — " +
                    "a discrete-GPU owner sees an under-promise here, the cheaper error.",
            },

        // ★1 SPEED (down from the owner's ★2 correction the same day — the one row the speed
        // measurement went against). ACCURACY measured 19.3% — a ★3 figure — and carries ★4 by the
        // 2026-08-31 owner override in the class doc's override note; the bands do not produce this
        // star and the note says so.
        //
        // Measured 19.3% here against medium's 17.0%, and the conjunction it forms
        // with the speed number — slower AND less accurate AND a larger download than medium — is
        // what backlog TRN-42 exists to resolve. Do not retire the row on this card's evidence
        // alone: TRN-27's GPU work could reverse the speed half, and this is one CPU.
        //
        // The kinder explanation was tested and failed. "We ship it badly in q8" had real support —
        // Groq serves the same weights at 17.6% — so an f16 build was scored on the same corpus
        // and measured 25.3%, WORSE than our q8's 19.3%, reproduced on an independent re-run.
        new("ggml-large-v3-turbo-q8_0", 4, 1, "90+ languages",
            "ACCURACY at the SHIPPED decode config, 2026-08-31: 18.2% WER over 56 owner-" +
            "transcribed recordings (results/2026-08-31-ggml-large-v3-turbo-q8_0~label-temp02a.json; " +
            "reproduced by ~label-temp02b on every accuracy field, 0 of 56 rows differ) — still a ★3 " +
            "figure, and the row " +
            "carries ★4 by the OWNER OVERRIDE of 2026-08-31, per the class doc's override note. " +
            "The override was decided against the pre-change run (19.3%, " +
            "results/2026-08-30-ggml-large-v3-turbo-q8_0.json, 76% of the gap to Medium in the " +
            "single clip B101); the shipped decode config then KILLED B101's failure (49 errors " +
            "to 18) and halved the corpus gap to Medium (18.2 vs 17.1), which strengthens the " +
            "override without re-litigating it. SPEED measured at the SHIPPED config 2026-08-31: 0.54x " +
            "real-time, p50 21.9 s over the re-pinned 100-clip sample " +
            "(results/speed-2026-08-31-ggml-large-v3-turbo-q8_0-run-shipped62.json) — faster than " +
            "before, still over the 20 s line, so ★1 stands on measurement.")
            {
                GpuSpeed = 4,
                GpuSource =
                    "GPU SPEED measured 2026-09-02 on the laptop's Intel Arc 140V (Vulkan, integrated; the " +
                    "CPU set's own machine), WARM ladder: 6.65x real-time, p50 1.48 s over the laptop's " +
                    "pinned 99-clip sample arc140v-20260902 " +
                    "(results/speed-2026-09-02-ggml-large-v3-turbo-q8_0-run-gpu-vulkan-arc140v-warm.json) — " +
                    "★4 by the 1–3 s band; the cold pass read 3.72 s, the largest compile share of any row " +
                    "(60% of its cold p50). The desktop's RTX 3080 decodes it in 0.56 s (18.05x; " +
                    "…-run-gpu-vulkan-rtx3080.json). The CPU ★1 row is ★4 on an integrated GPU and a ★5 " +
                    "figure on a discrete one: the full large encoder that costs 21.9 s on the laptop's CPU " +
                    "is exactly the part a GPU absorbs.",
            },
        // TRN-1 step 3 / TRN-29 flip. WHICH Parakeet bundle this row rates follows the build's
        // catalog row (ParakeetCatalog.ActiveRow) — the host-matching rule is why the two eras
        // carry DIFFERENT speed stars with different sources: a sherpa-onnx measurement is
        // inadmissible for the parakeet.cpp host and vice versa. Accuracy ★4 is shared by the
        // same-weights anchoring presentation rule (both bundles are the same v3 weights).
        // Lookups canonicalize (Find), so a persisted or History name in EITHER spelling
        // resolves to this row.
#if PCPP_ENABLED
        // SPEED ★5 since 2026-08-31, moved BY A MEASUREMENT — the file's one sanctioned way. The
        // shipped-config ladder put this row on the SAME 100-clip dictation sample that bands every
        // Whisper row: p50 906 ms over the 92 NON-EMPTY decodes. The raw p50 is 802 ms and is NOT
        // the banding figure: 8 of 100 clips decoded EMPTY (TRN-48), six of them failing fast, and
        // a nothing-returned "decode" dragging the median down is not "the text is there when you
        // look up". Excluding failures is the honest read, and 906 ms clears the sub-second band
        // with the run's 3.4% drift margin to spare.
        //
        // The previous ★4 rested on TRN-29 G5's 781-recording corpus (p50 1.665 s, p95 7.7 s, 4.8×
        // aggregate — docs/plans/2026-08-23-trn29-parakeetcpp-swap/evidence/). Both measurements
        // stand; they differ because the SAMPLES differ, and the band is defined on typical
        // dictation — which the 100-clip debug sample is and the long-skewed 781 corpus is not.
        //
        // The 906 ms figure was measured at the THEN-shipped 4 server threads; TRN-47 (2026-09-01)
        // moved the server to min(8, hw) on a three-sweep measurement. What that licenses here is
        // only that the BAND is safe: the 8-thread arms' p50s measured 733 and 814 ms in the two
        // runs with committed files, and 651 ms in run 2 (log-sourced — its files were overwritten;
        // TRN-47 card), all comfortably inside the cited band. It does NOT license "faster on p50"
        // — run 3's 8-thread p50 (814) sat above its 4-thread pass-2 (726), so per-arm p50 noise
        // swamps any per-metric claim; 8's measured wins are aggregate throughput and the p95 tail.
        // TRN-48 (same day) then added 600 ms of silence to every slice's decode input (the edge
        // pad) — roughly +75 ms of decode at this row's throughput, which the 906 ms citation
        // predates; the ~9% band margin absorbs it on paper, and the next ladder re-measures
        // rather than extrapolates. The citation stays the banding record until then.
        // The sherpa row's ★5 rested on a sherpa-host measurement and is inadmissible here.
        //
        // THE p50 IS QUOTED FIRST BECAUSE IT IS THE BANDING METRIC, and until 2026-08-31 this row
        // quoted only the ratio and the p95 — neither of which the bands use. In the ★4 era that
        // meant the star could not be checked from the row: 1.665 s sat inside ★4 (1–3 s), so THAT
        // star was right for THAT corpus, but only a plan evidence file said so. The lesson
        // survives the star move. Do not "simplify" back to the throughput figure: reasoning from 4.8× and a
        // p95 gives 2.2 s or 3.3 s depending on which you scale from, and those straddle the ★3/★4
        // edge. The reason the p95 misleads here is that this engine's p95/p50 ratio is 4.6 against
        // the Whisper lineup's ~2.3 — a far more skewed distribution, so a ratio borrowed from those
        // rows does not transfer.
        new("parakeet-tdt-0.6b-v3-gguf", 4, 5, "25 European languages",
            "ACCURACY measured on this project's own corpus 2026-08-30: 15.3% WER over 56 owner-" +
            "transcribed recordings (results/2026-08-30-parakeet-tdt-0.6b-v3.json) — the best of " +
            "any LOCAL model scored, ahead of Whisper Medium's 17.0%, and ahead of four paid cloud " +
            "rows. It no longer rests on the same-weights anchoring rule. SPEED measured at the " +
            "SHIPPED config 2026-08-31: p50 0.9 s over the non-empty decodes of the same 100-clip " +
            "sample that bands every local row " +
            "(results/speed-2026-08-31-parakeet-tdt-0.6b-v3-run-shipped62.json); the 2026-08-24 " +
            "781-recording run (4.8× aggregate, p50 1.7 s on that longer-skewed corpus) stands as " +
            "the long-form record.")
            {
                GpuSpeed = 5,
                GpuSource =
                    "GPU SPEED measured 2026-09-02 on the laptop's Intel Arc 140V (Vulkan, integrated; the " +
                    "CPU set's own machine), WARM ladder: 37.56x real-time, p50 0.22 s over the 93 NON-EMPTY " +
                    "decodes of the laptop's pinned 99-clip sample arc140v-20260902 (6 empties, the TRN-48 " +
                    "class; raw p50 0.20 s) " +
                    "(results/speed-2026-09-02-parakeet-tdt-0.6b-v3-run-gpu-vulkan-arc140v-warm.json; the " +
                    "cold pass read 31.62x, raw p50 0.23 s). The warm file's compute block records " +
                    "parakeetLaunchMode=Auto with vulkanProbe=Available — the backend is proven by the " +
                    "file, not inferred. The desktop's RTX 3080 corroborates the discrete class at 60.18x, " +
                    "non-empty p50 0.17 s " +
                    "(…-run-gpu-vulkan-rtx3080.json, compute block parakeetLaunchMode=Auto) — the fastest " +
                    "local row on the CPU and on the 3080; on the Arc, tiny edges it by the banding metric " +
                    "(212 ms vs this row's 217 ms non-empty p50) — same band, either way.",
            },
#else
        // ACCURACY was unmeasured here until 2026-08-30 — the only published figures were taken
        // on hosted endpoints, not sherpa-onnx int8 on a laptop CPU, and this file's host-matching
        // rule makes another host's number inadmissible. The 2026-08-30 corpus run measured the
        // PCPP_ENABLED GGUF bundle at 15.3% — NOT this row, which is the active one in THIS
        // branch; the sherpa row keeps ★4 by the same-weights anchoring rule, a PRESENTATION
        // choice and one this comment states rather than hides.
        //
        // SPEED ★5 rests on a first-party measurement: 9.0× real-time on this project's own
        // dictation corpus, against the then-shipped default's 0.6× — 15.8× faster than that
        // default and 74× faster than large-v3. The measurement lives in the source string, where
        // a reader can check it.
        //
        // It was "the ONE star on this page with a first-party measurement" until 2026-08-30, when
        // TRN-4's sweep measured the five local Whisper SPEED rows in BOTH build configurations.
        // Corrected here rather than only in the PCPP branch: this comment is compiled out of the
        // shipping build, which is exactly why a stale claim can sit in it unnoticed.
        new("parakeet-tdt-0.6b-v3", 4, 5, "25 European languages",
            "accuracy ★4 by the same-weights anchoring rule: the 2026-08-30 corpus run measured " +
            "the PCPP_ENABLED GGUF bundle at 15.3% WER — not this row — and this row shares those " +
            "v3 weights on a different host — a presentation choice, not a measurement of THIS bundle, which the " +
            "host-matching rule forbids claiming. SPEED ★5 on this machine: decode p50 0.527 s " +
            "over TRN-29 G5's 781-recording corpus (the banding metric — inside the sub-1 s band), " +
            "with 9.0× real-time over 30 clips measured 2026-08-02 " +
            "(docs/model-review/2026-08-02-parakeet-vs-local-whisper-cpu.md)."),
#endif
    ];

    private static readonly Dictionary<string, ModelRating> ByModel =
        All.ToDictionary(r => r.Model, StringComparer.OrdinalIgnoreCase);

    // Null/blank is "no such model", not an exception. Describe() is now the single entry point
    // for arbitrary model names — including whatever a user called their own Models\*.bin — and
    // a settings key that has never been written reads as null. TryGetValue would throw on it,
    // turning an empty setting into a crashed page rather than the generic subtitle (Kimi).
    internal static ModelRating? Find(string? model)
    {
        // Either Parakeet bundle spelling rates as the ACTIVE row (TRN-29 flip): persisted
        // selections and History rows carry whichever name was current when written, and the
        // table holds exactly this build's catalog row — the coverage sweep demands that.
        model = Models.ParakeetCatalog.CanonicalName(model);
        return !string.IsNullOrEmpty(model) && ByModel.TryGetValue(model, out var rating) ? rating : null;
    }

    private static string Stars(int filled) => new string('★', filled) + new string('☆', 5 - filled);

    /// <summary>The Models-page row subtitle. Format is unchanged from the hand-written version
    /// — TRN-3 moved where the stars come from, not how they look.
    ///
    /// <para>The fallback is supplied by the CALLER rather than guessed from the id, because the
    /// caller knows which catalog it is rendering and the id does not say. Sniffing a
    /// <c>ggml-</c> prefix would be a guess, and a wrong guess labels a cloud model "local" —
    /// <c>ModelsPageTests</c> pins an arbitrary name ("patient-notes") for that reason.</para>
    ///
    /// <para><b>Scope, stated exactly</b> — two earlier versions of this comment overclaimed it
    /// in opposite directions. No production caller is known to reach the fallback: every path
    /// into <c>Describe</c> renders a CATALOG row (<c>ModelManagementViewModel.LoadModels</c>
    /// builds only from <c>PredefinedModels</c>; <c>InstalledCatalogModels</c> filters unknown
    /// files out before anything renders), so a hand-placed unknown <c>.bin</c> gets no card.
    /// The fallback is kept as defence, not as a served case: a settings key can hold any string,
    /// and <c>GetDownloadedModels()</c> accepts any stem the name guard allows — weaker than
    /// "runnable". It is pinned by test so an id that ever does slip through degrades to a
    /// sentence rather than blank text.</para></summary>
    internal static string Describe(string? model, string fallback)
        => Describe(model, fallback, LocalComputeSnapshot.Current);

    /// <summary>The same row rendered against an explicit compute snapshot (TRN-52) — the pure
    /// form the tests pin; the two-argument overload above reads the process-wide
    /// <see cref="LocalComputeSnapshot.Current"/>, which is what the Models page calls.</summary>
    internal static string Describe(string? model, string fallback, LocalComputeSnapshot compute)
    {
        var rating = Find(model);
        if (rating is null) return fallback;
        return $"Accuracy {Stars(rating.Accuracy)} · Speed {Stars(SpeedFor(rating, compute))} · {rating.Languages}";
    }

    /// <summary>TRN-52: which local engine serves a row — DERIVED from the catalog row's own
    /// <see cref="Models.TranscriptionModelInfo.Runtime"/>, never a second hand-kept field that
    /// could disagree with the catalog. Null for a cloud row and for an unknown name. Canonicalizes
    /// like <see cref="Find"/>, so either Parakeet spelling resolves to the ACTIVE row's engine.</summary>
    internal static Models.LocalRuntimeKind? RuntimeOf(string? model)
        => Models.PredefinedModels.RuntimeOf(model); // the catalog's ONE lookup (AUD-36 folded three copies)

    /// <summary>TRN-52: the speed star a row shows under a compute snapshot. The GPU set is used
    /// only when the row HAS one and its own engine resolved <see cref="LocalCompute.Gpu"/>; every
    /// other combination — a cloud row, a CPU-only engine, an engine that resolved CPU, an engine
    /// the snapshot does not model — renders the CPU set. Accuracy is never consulted here: it does
    /// not vary by backend, and the test suite holds that line.</summary>
    internal static int SpeedFor(ModelRating rating, LocalComputeSnapshot compute)
    {
        if (rating.GpuSpeed is not int gpuSpeed) return rating.Speed;
        return RuntimeOf(rating.Model) is { } runtime && compute.For(runtime) == LocalCompute.Gpu
            ? gpuSpeed
            : rating.Speed;
    }

    /// <summary>The language bucket alone, for the Active Model card.</summary>
    internal static string? LanguagesFor(string? model) => Find(model)?.Languages;

    /// <summary>The accuracy/speed star pair alone, for the Active Model card (TRN-7, owner UAT
    /// 2026-08-03 — the card must show the ratings like the rows below it). Deliberately built
    /// from the same <see cref="Stars"/> helper and the same row as <see cref="Describe"/> rather
    /// than re-deriving them, so the card and the row it describes cannot drift apart; the card
    /// needs the two halves separately only because it interleaves the size/provider between
    /// them. Null for an unknown model — the caller drops it from the subtitle instead of
    /// rendering an invented rating, matching <see cref="LanguagesFor"/>.</summary>
    internal static string? StarsFor(string? model)
        => StarsFor(model, LocalComputeSnapshot.Current);

    /// <summary>The card's star pair against an explicit compute snapshot (TRN-52) — same
    /// <see cref="SpeedFor"/> decision as <see cref="Describe"/>, so the card and the row it
    /// describes render the same speed star for the same snapshot.</summary>
    internal static string? StarsFor(string? model, LocalComputeSnapshot compute)
    {
        var rating = Find(model);
        return rating is null
            ? null
            : $"Accuracy {Stars(rating.Accuracy)} · Speed {Stars(SpeedFor(rating, compute))}";
    }
}

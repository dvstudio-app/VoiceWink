namespace VoiceWink.Helpers;

/// <summary>
/// REL-30: at what level a GPU self-test FAILURE is logged — the one decision behind all three
/// failure sites (Whisper's warm decode, Parakeet's warm decode, and the coordinator's
/// empty-decode safety net).
///
/// <para><b>Error is how a failure reaches the field.</b> The Sentry sink's
/// <c>MinimumEventLevel</c> is Error (<c>App.ConfigureLogging</c>), so a Warning produces no event
/// — only a breadcrumb that ships if some later, unrelated error happens to fire. Before this,
/// a driver that loaded cleanly and decoded speech to nothing was caught on the machine and
/// invisible everywhere else: [[TRN-62]]'s Adreno case was found because the owner reported it,
/// not because anything told us. Error is also the honest level rather than a trick to reach the
/// sink — `.claude/rules/diagnostics.md` reserves Warning for handled provider noise and Error for
/// contract drift, and a GPU that reports success while returning nothing is contract drift.</para>
///
/// <para><b>Why the durability gate.</b> The verdict is what pins the engine to the CPU for this
/// app version, and <see cref="GpuWarmupMarker"/> writes are best-effort by design (an unwritable
/// marker costs a redundant warm-up, never capability). But an unpersisted FAIL is rediscovered at
/// EVERY launch — so reporting it unconditionally would put one Sentry event per start in front of
/// exactly the users whose machines are already misbehaving. Reporting only a DURABLE verdict makes
/// the event what it claims to be: this app version, on this driver, learned this once.
/// The recovery is unaffected either way — the CPU pin, the re-decode and the Settings advisory all
/// happen regardless; only the level changes (Codex plan round, Blocker 1).</para>
///
/// <para><b><c>Slower</c> (TRN-64 PR 2) is deliberately outside this decision.</b> A GPU that decodes
/// the clip correctly but too slowly is not contract drift — the driver did what it claimed, just
/// not fast — so both engines log it at Warning directly (a breadcrumb, never an event), whatever
/// the marker write's durability. Only wrong or missing output earns Error.</para>
/// </summary>
internal static class GpuSelfTestReport
{
    /// <summary>What stands in for the driver signature when the registry could not be read.
    /// A literal, never an empty template hole — an event whose driver field is blank is
    /// indistinguishable from one nobody filled in.</summary>
    internal const string UnknownDriver = "unknown";

    /// <summary>Error once the verdict is durable (it reaches Sentry, once per app version per
    /// driver); Warning otherwise (local only, because it will recur).</summary>
    internal static Serilog.Events.LogEventLevel LevelFor(bool verdictPersisted)
        => verdictPersisted
            ? Serilog.Events.LogEventLevel.Error
            : Serilog.Events.LogEventLevel.Warning;

    /// <summary>The driver signature for a report line, never null.</summary>
    internal static string DriverOrUnknown(string? signature)
        => string.IsNullOrWhiteSpace(signature) ? UnknownDriver : signature;
}

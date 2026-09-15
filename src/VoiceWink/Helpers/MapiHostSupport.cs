using System.Runtime.InteropServices;

namespace VoiceWink.Helpers;

/// <summary>
/// REL-27: may this process let Simple MAPI load the default mail client's provider INTO itself?
///
/// <para><c>MAPISendMail</c> is not self-contained. The <c>MAPI32.DLL</c> stub loads the registered
/// default mail client's provider into the CALLING process — measured here as Office's
/// <c>msmapi32.dll</c>, with its App-V shim (<c>AppvIsvSubsystems64.dll</c>) coming along. So a
/// Report-a-problem click runs third-party native code inside VoiceWink. (Which registry value
/// selects the provider is deliberately not asserted: the stub's documented Simple-MAPI lookup is
/// <c>DLLPath</c>, with <c>MSIComponentID</c> taking precedence when present, and an earlier draft
/// of this comment named <c>DLLPathEx</c> — an Extended-MAPI value — as though it were the
/// mechanism. The load chain below is measured from a crash dump; the registry mechanism was not,
/// so it is not claimed.)</para>
///
/// <para><b>The measured failure.</b> The owner's machine is a Snapdragon X Elite running Windows
/// on ARM64. VoiceWink ships x64 only (<c>&lt;Platforms&gt;x64&lt;/Platforms&gt;</c>), so it runs
/// under x64 emulation; Office is x64 too. Loading emulated x64 Office code into an emulated x64
/// process crashed the emulation subsystem outright — Windows Error Reporting recorded
/// <c>0xc000026f STATUS_WX86_INTERNAL_ERROR</c> ("an internal error occurred in the Win32 x86
/// emulation subsystem") faulting in <c>AppvIsvSubsystems64.dll</c>, twice on 2026-08-24, same
/// bucket and same fault offset both times.</para>
///
/// <para><b>Why a guard and not a catch.</b> <c>MapiMailService.TrySend</c> already wraps the
/// P/Invoke in <c>catch (Exception)</c> and it does not help: this is a native fault in the
/// emulation subsystem, not a managed exception. .NET never surfaces it, the process is simply
/// gone, and the user's typed report goes with it. The only fix available to us is to not make the
/// call.</para>
///
/// <para><b>Why this predicate and not an ARM64 check.</b> The bad ingredient is EMULATION, not
/// ARM64 — an x64 process on an ARM64 host. Comparing process architecture to OS architecture says
/// that, and it has two properties an <c>== Arm64</c> test would not: it is inert on every machine
/// where the feature works today (native: process and OS match, so the guard never fires and the
/// path is byte-identical), and it SELF-HEALS — the day VoiceWink ships a native ARM64 build, the
/// two match again and Simple MAPI is attempted normally, with no stale exclusion list to remember
/// to update.</para>
///
/// <para><b>It is deliberately WIDER than what was measured, and that is a choice, not a
/// conflation</b> (Codex plan r1). Only x64-on-ARM64 has been observed to crash; this refuses every
/// emulated combination. Narrowing it to the one measured pair would be equivalent today — the
/// project is <c>win-x64</c>-only, so no other mismatch can arise — while being wrong the moment a
/// second RID ships. Refusing a hypothetical emulated host costs an auto-attach that degrades to a
/// working fallback; admitting one costs a hard process crash. The asymmetry decides it.</para>
///
/// <para><b>Scope, honestly.</b> This is not a claim that Simple MAPI is safe on every matched
/// host — only that the one configuration measured to hard-crash is refused. If a crash is ever
/// observed on a non-emulated host, the answer is to stop hosting the provider in-process at all
/// (run it in a child process), not to widen this predicate; that alternative is recorded on the
/// REL-27 card rather than built, because nothing has been observed to justify it.</para>
/// </summary>
public static class MapiHostSupport
{
    /// <summary>
    /// True when it is safe to hand Simple MAPI this process. Architectures are parameters rather
    /// than reads of <see cref="RuntimeInformation"/> so the decision is unit-testable on hardware
    /// that is not ARM64 — the crash cannot be reproduced in CI, so the DECISION is what gets
    /// pinned.
    /// </summary>
    public static bool IsSafeToLoadInProcess(Architecture processArchitecture, Architecture osArchitecture)
        => processArchitecture == osArchitecture;

    /// <summary>
    /// Why a refusal happened, for the log. A CONST rather than a formatter over the two
    /// architectures on purpose: the caller logs those as structured properties (queryable, and
    /// what a log search matches on), so interpolating them here too would render them twice in
    /// one sentence. This is app-authored prose with no substitution of any kind, which is also
    /// what makes it safe in a support bundle and a Sentry breadcrumb — there is no interpolation
    /// site for a path, a recipient or mail content to reach it through.
    /// </summary>
    public const string RefusalReason =
        "the process is emulated, and loading the mail client's in-process MAPI provider into an "
        + "emulated process crashes the emulation subsystem";
}

namespace VoiceWink.Helpers;

/// <summary>
/// TRN-29's rollout flag for the parakeet.cpp server backend — the `ParakeetFeature` /
/// `VadFeature` pattern exactly: an MSBuild-defined constant, read ONLY at the composition
/// root, so a build either has the backend or it does not and no runtime state can flip it.
///
/// <para><b>Default ON since the TRN-29 G6 flip (2026-08-24).</b> Every ordinary build compiles
/// the parakeet.cpp-backed path; <c>-p:PcppEnabled=false</c> (the VALIDATED property -
/// `ValidatePcppEnabled` in Directory.Build.targets rejects anything but exact true/false;
/// never set DefineConstants directly, which would bypass the validation) is the KILL SWITCH's
/// build form, restoring the sherpa-only era including the sherpa catalog row
/// (<c>ParakeetCatalog.ActiveRow</c> follows this constant). The OFF configuration has NO
/// automated executor (its weekly lever-check leg was owner-retired the day it shipped,
/// 2026-08-24) — an incident pull should expect to fix the rebuild first.
/// `PcppFeatureGateTests` pins the committed default AND asserts whichever way the current
/// build is flagged.</para>
/// </summary>
internal static class PcppFeature
{
#if PCPP_ENABLED
    internal const bool IsEnabled = true;
#else
    internal const bool IsEnabled = false;
#endif
}

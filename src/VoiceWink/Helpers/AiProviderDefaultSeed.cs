using VoiceWink.Services.AIEnhancement;

namespace VoiceWink.Helpers;

/// <summary>
/// Which text-enhancement provider a NEW install starts on (owner, 2026-09-30): VoiceWink Engine,
/// where it is offered. The setup wizard's FIRST finish stores it when no provider is stored. A
/// finish after Settings → "Relaunch setup wizard" is not a first finish (that button writes the
/// completion flag, so the key exists), which is what keeps an existing install — whose provider
/// may never have been stored — on exactly the provider it has; the settings table's default is
/// untouched for the same reason.
/// </summary>
/// <remarks>The <c>HotkeyDefaultSeed</c> shape: a stored value always wins, and "stored" is asked
/// of the settings file (<c>SettingsService.Contains</c>), never of a read that would supply the
/// fallback. AI enhancement itself stays off by default, so the seed changes which provider the
/// Enhancement page opens on, not whether anything runs. Pinned by <c>ProviderListLayoutTests</c>.</remarks>
internal static class AiProviderDefaultSeed
{
    /// <summary>The provider to store at the end of setup, or null to store nothing.</summary>
    internal static AIProvider? ForNewInstall(bool firstFinish, bool providerStored, bool engineOffered)
        => firstFinish && !providerStored && engineOffered ? AIProvider.OnThisPc : null;
}

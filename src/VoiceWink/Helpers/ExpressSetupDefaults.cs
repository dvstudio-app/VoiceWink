namespace VoiceWink.Helpers;

/// <summary>What Express setup does about AI clean-up (LNC-15). Public only so xUnit theories can take it.</summary>
public enum ExpressAiAction
{
    /// <summary>Something was already chosen, or no built-in model fits this PC.</summary>
    LeaveAlone,

    /// <summary>The built-in pick is on disk: use it and switch AI clean-up on now.</summary>
    UseInstalledNow,

    /// <summary>Download the built-in pick; AI clean-up switches on once it is in (<c>BuiltInModelAdoption</c>).</summary>
    DownloadThenAdopt,
}

/// <summary>Express setup's decision, before anything is written.</summary>
/// <param name="SpeechModel">The speech model the run ends on.</param>
/// <param name="WriteSpeechModel">Whether <c>SelectedModelName</c> must be written (false when a stored choice is kept).</param>
/// <param name="DownloadSpeechModel">Whether to start <see cref="SpeechModel"/>'s download.</param>
/// <param name="Ai">What to do about AI clean-up.</param>
internal readonly record struct ExpressSetupDecision(
    string SpeechModel, bool WriteSpeechModel, bool DownloadSpeechModel, ExpressAiAction Ai);

/// <summary>
/// The defaults Express setup applies where the Advanced path would ask (owner, 2026-10-13: Express
/// asks language and hotkey; the speech model and the built-in AI model for this PC download in the
/// background and AI clean-up switches on).
///
/// <para><b>Defaults fill gaps; a choice that still works is kept</b>, so a wizard relaunched from
/// Settings on an existing install keeps the user's model and AI settings. A stored speech model is
/// replaced only when it cannot work here: a cloud model without its key, a local model this PC
/// cannot run, or one that cannot recognise the language just chosen (the Advanced path's ONB-5 note
/// says the same about it). The Done card shows the replacement before anything is written.</para>
///
/// <para>"Stored" means the key EXISTS (<c>SettingsService.Contains</c>), never what a read returns:
/// <c>SelectedModelName</c> reads back <c>AppDefaults.DefaultWhisperModel</c> when absent, and that
/// row is serviceable, so a read-based test would keep the fallback on every fresh install and never
/// select the recommendation (Grok plan review, Blocker 1).</para>
///
/// <para>The AI switch counts as a choice only when it is stored OFF. Stored ON with no built-in
/// model chosen is the half-set state the built-in model completes — the AI step's switch turned on
/// before going Back to Express, or the AI page's switch turned on while the download runs.</para>
///
/// <para>Pure and write-free: the Done step renders this decision as a preview, and only the
/// finish click performs it, so going Back to change the language cannot leave an earlier
/// decision written.</para>
/// </summary>
internal static class ExpressSetupDefaults
{
    /// <param name="speechModelStored"><c>Contains(SelectedModelName)</c>.</param>
    /// <param name="storedSpeechModel">The stored value (meaningful only when stored).</param>
    /// <param name="storedIsServiceableLocal">The stored value is a local model this PC can run.</param>
    /// <param name="storedIsCloudWithKey">The stored value is a cloud model whose provider key is stored.</param>
    /// <param name="storedInstalled">The stored value is a local model already on disk.</param>
    /// <param name="storedCannotRecogniseLanguage">The stored local model is known not to recognise the chosen language.</param>
    /// <param name="recommendedModel">What the Advanced path's local screen would recommend.</param>
    /// <param name="recommendedInstalled">The recommendation is already on disk.</param>
    /// <param name="builtInOffered">A built-in AI model fits this PC.</param>
    /// <param name="aiProviderStored"><c>Contains(AiProvider)</c>.</param>
    /// <param name="aiSwitchedOff">The AI switch is stored, and OFF.</param>
    /// <param name="builtInPickInstalled">The built-in model for this PC is already on disk.</param>
    internal static ExpressSetupDecision Decide(
        bool speechModelStored, string? storedSpeechModel, bool storedIsServiceableLocal,
        bool storedIsCloudWithKey, bool storedInstalled, bool storedCannotRecogniseLanguage,
        string recommendedModel, bool recommendedInstalled,
        bool builtInOffered, bool aiProviderStored, bool aiSwitchedOff, bool builtInPickInstalled)
    {
        var keep = speechModelStored
                   && !string.IsNullOrEmpty(storedSpeechModel)
                   && ((storedIsServiceableLocal && !storedCannotRecogniseLanguage) || storedIsCloudWithKey);

        var speech = keep
            ? (storedSpeechModel!, Write: false, Download: storedIsServiceableLocal && !storedInstalled)
            : (recommendedModel, Write: true, Download: !recommendedInstalled);

        var ai = !builtInOffered || aiProviderStored || aiSwitchedOff ? ExpressAiAction.LeaveAlone
               : builtInPickInstalled ? ExpressAiAction.UseInstalledNow
               : ExpressAiAction.DownloadThenAdopt;

        return new ExpressSetupDecision(speech.Item1, speech.Write, speech.Download, ai);
    }
}

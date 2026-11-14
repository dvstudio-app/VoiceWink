using VoiceWink.Services.AIEnhancement;
using VoiceWink.Services.AIEnhancement.LocalEngine;
using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

internal enum SetupModelAction { None, UseNow, Track }

/// <summary>What is stored for one text provider: whether it needs a key, its key's verdict (null = none), its model.</summary>
internal readonly record struct ProviderTextFacts(bool RequiresKey, ApiKeyFormatVerdict? Key, string? Model);

/// <summary>Everything setup's Finish and Done card read about AI enhancement (ONB-14).</summary>
/// <param name="Switch">The stored AI switch; null = absent, which is never a stored OFF.</param>
/// <param name="Provider">The stored text provider; null when absent or unparseable (reads as OpenAI).</param>
/// <param name="Installed">Installed built-in models, catalog order.</param>
/// <param name="Running">Built-in models downloading now, catalog order.</param>
/// <param name="Job">The pending adoption job's model, when the catalog knows it.</param>
internal sealed record SetupAiState(
    bool FirstFinish, bool EngineOffered, bool? Switch, bool ProviderStored, AIProvider? Provider,
    PromptRoute Prompt, Func<AIProvider, ProviderTextFacts> Facts,
    IReadOnlyList<string> Installed, IReadOnlyList<string> Running, string? Job);

/// <summary>What Finish writes, in order: the provider, the model action, the switch removal; and the Done card's line.</summary>
internal readonly record struct SetupAiOutcome(
    AIProvider? ProviderWrite, SetupModelAction Action, string? ModelId, bool RemoveSwitch, string DoneText);

/// <summary>ONB-14: one decision for setup's Finish click and Done card, so setup never ends with AI enhancement ON and nothing able to run it.</summary>
internal static class SetupAiFinish
{
    internal const string On = "ON";
    internal const string Off = "OFF";

    internal static SetupAiOutcome Decide(SetupAiState s)
    {
        bool IsInstalled(string id) => s.Installed.Contains(id);
        TextReadiness ReadinessOf(AIProvider provider, string? modelOverride)
        {
            var facts = s.Facts(provider);
            return TextModelReadiness.Of(facts.RequiresKey, facts.Key,
                string.IsNullOrWhiteSpace(modelOverride) ? facts.Model : modelOverride,
                provider == AIProvider.OnThisPc, s.EngineOffered, IsInstalled);
        }

        var route = s.Prompt;
        var modelOverridden = !string.IsNullOrWhiteSpace(route.ModelOverride);
        var noOverride = route.ProviderOverride is null && !modelOverridden;

        // The provider write is decided first, and every rule below judges the state after it.
        var write = AiProviderDefaultSeed.ForNewInstall(s.FirstFinish, s.ProviderStored, s.EngineOffered);
        // A rerun with no provider stored reads as OpenAI: move to the built-in models when only they can serve.
        if (write is null && !s.FirstFinish && !s.ProviderStored && s.Switch == true && noOverride && !route.IsImage
            && s.EngineOffered && (s.Installed.Count > 0 || s.Running.Count > 0 || s.Job is not null)
            && ReadinessOf(AIProvider.OpenAI, null) != TextReadiness.Ready)
        {
            write = AIProvider.OnThisPc;
        }

        var global = write ?? s.Provider ?? AIProvider.OpenAI;
        var builtInModel = s.Facts(AIProvider.OnThisPc).Model;
        SetupAiOutcome Result(string text, SetupModelAction action = SetupModelAction.None, string? id = null, bool remove = false)
            => new(write, action, id, remove, text);

        if (s.Switch == false)
            return Result(Off);
        if (s.Switch is null)
        {
            var adopts = s.Job is { } job && AdoptionHolds(global == AIProvider.OnThisPc, storedOff: false,
                s.EngineOffered, pendingKnown: true, Serves(route, job), builtInModel, job, IsInstalled);
            return Result(adopts ? WaitsFor(s.Job!) : Off);
        }

        var readiness = ReadinessOf(route.ProviderOverride ?? global, route.ModelOverride);
        // An image prompt uses no text model, so it is not judged.
        if (route.IsImage || readiness == TextReadiness.Ready)
            return Result(On);

        // Only a prompt on the GLOBAL built-in selection can be served by a global model action.
        var builtInGlobal = global == AIProvider.OnThisPc && !modelOverridden
                            && route.ProviderOverride is null or AIProvider.OnThisPc;
        if (builtInGlobal && readiness is TextReadiness.NoModel or TextReadiness.NotInstalled)
        {
            if (s.Installed.Count > 0)
                return Result(On, SetupModelAction.UseNow, s.Installed[0]);
            if (s.Running.Count > 0)
            {
                var pick = !string.IsNullOrEmpty(builtInModel) && s.Running.Contains(builtInModel) ? builtInModel
                         : s.Job is { } live && s.Running.Contains(live) ? live
                         : s.Running[0];
                return Result(WaitsFor(pick), SetupModelAction.Track, pick);
            }
            // A job kept after a failed attempt is retried at the next start; restarting it here would spend that retry.
            return Result(s.Job is { } kept ? WaitsFor(kept) : OffBecause(TextReadiness.NotInstalled), remove: true);
        }
        return Result(OffBecause(readiness), remove: true);
    }

    /// <summary>After the built-in models become the global provider with <paramref name="id"/>, the prompt runs on it.</summary>
    internal static bool Serves(PromptRoute route, string id)
    {
        var modelOverridden = !string.IsNullOrWhiteSpace(route.ModelOverride);
        return route.IsImage
            || (route.ProviderOverride is null && !modelOverridden)
            || (route.ProviderOverride == AIProvider.OnThisPc
                && (!modelOverridden || string.Equals(route.ModelOverride, id, StringComparison.Ordinal)));
    }

    /// <summary>Whether a finished download of <paramref name="pending"/> may switch AI on; one rule for the adopter and the Done card's line.</summary>
    internal static bool AdoptionHolds(bool providerIsBuiltIn, bool storedOff, bool engineOffered, bool pendingKnown,
        bool serves, string? storedModel, string pending, Func<string, bool> isInstalled)
        => providerIsBuiltIn && !storedOff && engineOffered && pendingKnown && serves
           // The AI page can heal the selection onto the pending model once it is installed: not a contrary choice.
           && (string.IsNullOrEmpty(storedModel) || !isInstalled(storedModel)
               || string.Equals(storedModel, pending, StringComparison.Ordinal));

    /// <summary>Gathers the state from what is stored. The only impure part.</summary>
    internal static SetupAiState Read(SettingsService settings, Func<AIProvider, string?> storedKey,
        Func<AIProvider, bool> requiresKey, PromptRoute prompt, bool engineOffered,
        IReadOnlyList<string> installed, Func<string, bool> isDownloading)
    {
        bool? aiSwitch = settings.Contains(AppDefaults.AiEnhancementEnabled)
            ? settings.GetBool(AppDefaults.AiEnhancementEnabled)
            : null;
        // Contains first: a read of an absent key answers the settings default, not "nothing stored".
        var providerStored = settings.Contains(AppDefaults.AiProvider);
        AIProvider? provider = providerStored
                               && Enum.TryParse<AIProvider>(settings.GetString(AppDefaults.AiProvider, ""), out var parsed)
            ? parsed
            : null;
        var pending = settings.GetString(AppDefaults.ExpressBuiltInPending, "");

        ProviderTextFacts Facts(AIProvider p)
        {
            var key = storedKey(p);
            return new ProviderTextFacts(requiresKey(p),
                string.IsNullOrEmpty(key) ? null : ApiKeyFormat.Validate(key),
                settings.GetString(AppDefaults.AiModelKey(p), ""));
        }

        return new SetupAiState(
            FirstFinish: !settings.Contains(AppDefaults.HasCompletedOnboarding),
            EngineOffered: engineOffered,
            Switch: aiSwitch,
            ProviderStored: providerStored,
            Provider: provider,
            Prompt: prompt,
            Facts: Facts,
            Installed: installed,
            Running: LocalModelCatalog.All.Select(m => m.Id).Where(isDownloading).ToList(),
            Job: !string.IsNullOrEmpty(pending) && LocalModelCatalog.Find(pending) is not null ? pending : null);
    }

    private static string WaitsFor(string id)
        => $"OFF — turns on once {LocalModelCatalog.Find(id)?.DisplayName ?? id} has downloaded";

    private static string OffBecause(TextReadiness readiness) => readiness switch
    {
        TextReadiness.NoKey => "OFF — no API key",
        TextReadiness.BadKey => "OFF — API key not usable",
        TextReadiness.NotInstalled => "OFF — built-in model not downloaded",
        TextReadiness.EngineUnavailable => "OFF — built-in models unavailable",
        _ => "OFF — no usable AI model chosen",
    };
}

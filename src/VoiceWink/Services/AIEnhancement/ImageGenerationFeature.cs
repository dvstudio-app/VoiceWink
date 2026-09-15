namespace VoiceWink.Services.AIEnhancement;

/// <summary>
/// Single named flag gating AI <b>image generation</b>. Image generation is enabled in <b>all</b>
/// build configurations.
///
/// <para>Historically this was compiled out of Debug builds (to keep the dev box undistracted),
/// mirroring <see cref="VoiceWink.Services.Updates.UpdateCheckFeature"/>. That Debug limitation was
/// removed 2026-07-09 at the owner's request so image generation can be exercised from a normal
/// Debug/dev build too. The flag is retained as the one place call sites read from (the recording
/// pipeline trigger in <see cref="VoiceWink.ViewModels.MainViewModel"/> and the image provider/model
/// UI on the AI Enhancement page) and as a single home for any future kill-switch.</para>
///
/// <para>Deliberately a <c>static readonly</c> field, not a <c>const</c>: keeps the guarded
/// <c>if</c> bodies from being flagged unreachable (<c>CS0162</c>) at the direct call sites.</para>
/// </summary>
internal static class ImageGenerationFeature
{
    public static readonly bool IsEnabled = true;
}

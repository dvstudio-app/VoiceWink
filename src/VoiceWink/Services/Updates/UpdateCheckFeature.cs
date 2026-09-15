namespace VoiceWink.Services.Updates;

/// <summary>
/// Compile-time-derived flag mirroring the <c>UPDATE_CHECK_ENABLED</c>
/// preprocessor symbol, which itself is gated by
/// <c>&lt;UpdateCheckEnabled&gt;true&lt;/UpdateCheckEnabled&gt;</c> in
/// <c>VoiceWink.csproj</c>. Exposed as a runtime constant so:
///
/// <list type="number">
///   <item><see cref="UpdateService"/> can check it from regular C# without
///     littering call sites with <c>#if</c> blocks.</item>
///   <item>Unit tests can <i>assert</i> the value matches the build
///     configuration (catching the case where someone strips the csproj
///     property but leaves the runtime check in place).</item>
/// </list>
///
/// <para>The committed csproj default is <c>false</c>, so dev/IDE/CI builds
/// return <c>false</c> here and <see cref="UpdateService.CheckForUpdatesAsync"/>
/// short-circuits before any network egress. Signed release builds opt in via
/// <c>-p:UpdateCheckEnabled=true</c> (see installer/release-update.*); the
/// committed default deliberately stays false and is pinned that way by a unit
/// test. The disclosure + CDN prerequisites are already met (the bundled
/// privacy policy's §4 + the live R2 CDN at updates.voicewink.app).</para>
/// </summary>
internal static class UpdateCheckFeature
{
    public const bool IsEnabled =
#if UPDATE_CHECK_ENABLED
        true;
#else
        false;
#endif
}

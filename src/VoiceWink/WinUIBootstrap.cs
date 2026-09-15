namespace VoiceWink;

/// <summary>
/// Isolated WinUI startup. Microsoft.UI.Xaml type references stay confined to
/// this class so the JIT only resolves them when <see cref="Run"/> is first
/// touched — never while <c>Program.Main</c> is still in stage 1–3, where
/// Microsoft.WindowsAppRuntime may not yet be installed on a clean machine.
///
/// Splitting this out is one half of the "no eager-load" invariant; the other
/// half is the csproj's <c>WindowsAppSdkBootstrapInitialize=false</c>, which
/// disables the module initializer that would otherwise auto-fire at
/// <c>VoiceWink.dll</c> load time.
/// </summary>
internal static class WinUIBootstrap
{
    public static void Run()
    {
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            // App's ctor wires itself into the WinUI Application singleton.
            // The constructed App reference is intentionally unused here —
            // its side effects (OnLaunched, DI registration, tray icon)
            // happen during construction and downstream WinUI callbacks.
            _ = new App();
        });
    }
}

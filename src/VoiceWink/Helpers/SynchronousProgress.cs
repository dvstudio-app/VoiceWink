namespace VoiceWink.Helpers;

/// <summary>Runs the handler inline on the reporting thread, so reports arrive in order; keep it fast and non-throwing (LAI-16).</summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

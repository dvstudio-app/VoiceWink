using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.Transcription;

namespace VoiceWink.Services.AIEnhancement.LocalEngine;

/// <summary>Spawns one llama-server child. The seam <see cref="LlamaServerProcess"/> takes, so its
/// lifecycle rules are tested against fakes and never open a process.</summary>
internal interface ILlamaServerLauncher
{
    /// <summary>Launch llama-server from <paramref name="exePath"/> on <paramref name="modelPath"/>.
    /// Null = no child exists.</summary>
    ILlamaServerChild? TryLaunch(string exePath, string modelPath, string apiKey, LlamaLaunchMode mode);
}

/// <summary>One launched llama-server. The Parakeet child's contract (PID-keyed port readback,
/// idempotent kill, confirmed exit) plus what THIS child printed about itself — parsed fields only.</summary>
internal interface ILlamaServerChild : IDisposable
{
    uint Pid { get; }
    bool HasExited { get; }

    /// <summary>The loopback port this child listens on, from the kernel's TCP table keyed by its
    /// PID; null until the socket opens.</summary>
    int? TryReadListeningPort();

    void Kill();
    bool WaitForExit(TimeSpan timeout);

    /// <summary>The device the child put the model on — its token (<c>Vulkan0</c>) and name as ONE
    /// snapshot, so a reader never pairs one line's token with another line's name (a model split
    /// across two GPUs prints two). Null when it printed none: a CPU-only child prints none, so
    /// null is never GPU evidence.</summary>
    LlamaObservedDevice? ObservedDevice { get; }

    /// <summary>The child's own <c>thinking = 0|1</c> statement; null until printed.</summary>
    bool? ObservedThinking { get; }
}

/// <summary>One parsed device line: the token and the bounded name.</summary>
internal sealed record LlamaObservedDevice(string Token, string Name);

/// <summary>
/// LAI-2: the production launcher — <see cref="NativeChildLauncher"/> (the Win32 core shared with
/// parakeet-server) with llama-server's command line (<see cref="LlamaServerLaunch"/>), its
/// stripped environment, and a drain that hands every line to <see cref="LlamaServerLogLine"/> and
/// logs only what it parsed. The working directory is the payload directory, which is
/// load-bearing: ggml's backend loader also scans the current directory for backend DLLs.
/// </summary>
internal sealed class NativeLlamaServerLauncher : ILlamaServerLauncher
{
    private static ILogger Logger => Log.ForContext<NativeLlamaServerLauncher>();

    private readonly int _threads;

    internal NativeLlamaServerLauncher(int? threads = null)
    {
        _threads = threads ?? ParakeetServerPolicy.ServerDecodeThreads;
    }

    public ILlamaServerChild? TryLaunch(string exePath, string modelPath, string apiKey, LlamaLaunchMode mode)
    {
        var commandLine = LlamaServerLaunch.BuildCommandLine(
            exePath, LlamaServerLaunch.BuildArguments(modelPath, apiKey, mode, _threads));
        var observation = new Observation();
        var handle = NativeChildLauncher.TryLaunch(
            "llama-server",
            exePath,
            commandLine,
            LlamaServerLaunch.BuildEnvironmentBlock(global::System.Environment.GetEnvironmentVariables()),
            Path.GetDirectoryName(exePath)!,
            (reader, pid) => NativeChildOutputPump.Pump<LlamaServerLogLine>(reader, LlamaServerLogLine.TryParse, line =>
            {
                observation.Observe(line);
                OnLine(pid, line);
            }));
        return handle is null ? null : new Child(handle, observation);
    }

    /// <summary>One Information line per parsed fact. The device name is driver-supplied text and
    /// goes through the single-line sanitizer like every other OS-supplied value.</summary>
    private static void OnLine(uint pid, LlamaServerLogLine line)
    {
        switch (line.Kind)
        {
            case LlamaServerLogLineKind.DeviceSelected:
                Logger.Information("llama-server (pid {Pid}): device {GpuDevice} ({GpuName})",
                    pid, line.DeviceToken, LogValueSanitizer.SingleLine(line.DeviceName));
                break;
            case LlamaServerLogLineKind.LayersOffloaded:
                Logger.Information("llama-server (pid {Pid}): offloaded {LayersOnGpu}/{LayersTotal} layers to the GPU",
                    pid, line.LayersOnGpu, line.LayersTotal);
                break;
            case LlamaServerLogLineKind.Thinking:
                Logger.Information("llama-server (pid {Pid}): thinking {Thinking}", pid, line.ThinkingOn ? "on" : "off");
                break;
        }
    }

    /// <summary>What the child printed about itself; written by the drain thread, read by the
    /// manager on another, hence volatile.</summary>
    private sealed class Observation
    {
        private volatile LlamaObservedDevice? _device;
        private volatile int _thinking = -1; // -1 unknown, 0 off, 1 on

        public LlamaObservedDevice? Device => _device;
        public bool? Thinking => _thinking < 0 ? null : _thinking == 1;

        public void Observe(LlamaServerLogLine line)
        {
            switch (line.Kind)
            {
                case LlamaServerLogLineKind.DeviceSelected:
                    _device = new LlamaObservedDevice(line.DeviceToken, line.DeviceName);
                    break;
                case LlamaServerLogLineKind.Thinking:
                    _thinking = line.ThinkingOn ? 1 : 0;
                    break;
            }
        }
    }

    private sealed class Child(NativeChildHandle handle, Observation observation) : ILlamaServerChild
    {
        public uint Pid => handle.Pid;
        public bool HasExited => handle.HasExited;
        public int? TryReadListeningPort() => handle.TryReadListeningPort();
        public void Kill() => handle.Kill();
        public bool WaitForExit(TimeSpan timeout) => handle.WaitForExit(timeout);
        public LlamaObservedDevice? ObservedDevice => observation.Device;
        public bool? ObservedThinking => observation.Thinking;
        public void Dispose() => handle.Dispose();
    }
}

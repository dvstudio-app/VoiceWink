using System.Diagnostics;
using Serilog;

namespace VoiceWink.Services.AIEnhancement;

/// <summary>What <see cref="OllamaIntegratedGraphics.ApplyAsync"/> achieved.</summary>
public enum OllamaGraphicsFixOutcome
{
    /// <summary>Set, restarted, and Ollama now reports the model in video memory.</summary>
    NowOnGraphics,

    /// <summary>Set and restarted, but Ollama still runs the model on the processor.</summary>
    StillOnProcessor,

    /// <summary>Set and restarted; Ollama did not say where the model runs.</summary>
    Unknown,

    /// <summary>Set, but Ollama was not started from its tray app, so VoiceWink left it alone.</summary>
    RestartManually,

    /// <summary>Set, but Ollama did not answer again after the restart.</summary>
    DidNotRestart,

    /// <summary>The variable could not be written.</summary>
    SetFailed
}

/// <summary>A running process as the restart sees it: its image name and full path (null when unreadable).</summary>
internal readonly record struct OllamaProcessInfo(int Id, string Name, string? Path);

/// <summary>The side effects <see cref="OllamaIntegratedGraphics"/> needs, as a seam for tests.</summary>
internal interface IOllamaHost
{
    string? ReadUserVariable(string name);
    void WriteUserVariable(string name, string value);
    IReadOnlyList<OllamaProcessInfo> ListOllamaProcesses();
    bool FileExists(string path);
    void StopProcess(int id);
    void StartTrayApp(string path, string variable, string value);
}

/// <summary>
/// The Enhancement page's "Use integrated graphics" fix: Ollama skips integrated graphics unless
/// <c>OLLAMA_IGPU_ENABLE=1</c> is set (measured on an Intel Arc 140V, LAI-0 bench README). Sets the
/// variable for the Windows user, restarts Ollama through its tray app, then asks Ollama where the
/// model runs now. A server started any other way — <c>ollama serve</c> in a terminal, a service —
/// is never stopped: VoiceWink cannot start it again the same way.
/// </summary>
internal sealed class OllamaIntegratedGraphics
{
    internal const string VariableName = "OLLAMA_IGPU_ENABLE";
    internal const string VariableValue = "1";

    /// <summary>The tray app's image name, without ".exe" (what <see cref="Process.ProcessName"/> reports).</summary>
    internal const string TrayAppName = "ollama app";

    private static ILogger Logger => Log.ForContext<OllamaIntegratedGraphics>();

    private readonly IOllamaHost _host;
    private readonly Func<CancellationToken, Task<bool>> _waitForServer;
    private readonly Func<CancellationToken, Task<LocalServerCompute>> _recheck;

    public OllamaIntegratedGraphics(
        IOllamaHost host,
        Func<CancellationToken, Task<bool>> waitForServer,
        Func<CancellationToken, Task<LocalServerCompute>> recheck)
    {
        _host = host;
        _waitForServer = waitForServer;
        _recheck = recheck;
    }

    /// <summary>True when the variable is already "1" for the Windows user.</summary>
    public bool IsVariableSet()
    {
        try { return _host.ReadUserVariable(VariableName) == VariableValue; }
        catch { return false; }
    }

    public async Task<OllamaGraphicsFixOutcome> ApplyAsync(CancellationToken ct)
    {
        try
        {
            if (!IsVariableSet())
                _host.WriteUserVariable(VariableName, VariableValue);
        }
        catch (Exception ex)
        {
            Logger.Warning("Could not set {Variable}: {ErrorType}", VariableName, ex.GetType().Name);
            return OllamaGraphicsFixOutcome.SetFailed;
        }

        RestartPlan? plan;
        try
        {
            plan = PlanRestart(_host.ListOllamaProcesses(), _host.FileExists);
        }
        catch (Exception ex)
        {
            Logger.Warning("Could not list Ollama processes: {ErrorType}", ex.GetType().Name);
            plan = null;
        }
        if (plan is null)
        {
            Logger.Information("{Variable} set; Ollama is not running from its tray app, so it was not restarted", VariableName);
            return OllamaGraphicsFixOutcome.RestartManually;
        }

        try
        {
            foreach (var id in plan.StopIds)
                _host.StopProcess(id);
            _host.StartTrayApp(plan.TrayAppPath, VariableName, VariableValue);
        }
        catch (Exception ex)
        {
            // The tray app may already be stopped, so "restart Ollama to apply it" would describe
            // a server that is no longer running (Grok diff r1).
            Logger.Warning("Ollama restart failed: {ErrorType}", ex.GetType().Name);
            return OllamaGraphicsFixOutcome.DidNotRestart;
        }

        if (!await _waitForServer(ct).ConfigureAwait(false))
        {
            Logger.Warning("Ollama did not answer after the restart");
            return OllamaGraphicsFixOutcome.DidNotRestart;
        }

        var compute = await _recheck(ct).ConfigureAwait(false);
        Logger.Information("Ollama restarted with {Variable}=1; model now on the {Compute}", VariableName, compute);
        return compute switch
        {
            LocalServerCompute.GraphicsCard => OllamaGraphicsFixOutcome.NowOnGraphics,
            LocalServerCompute.Processor => OllamaGraphicsFixOutcome.StillOnProcessor,
            _ => OllamaGraphicsFixOutcome.Unknown
        };
    }

    /// <summary>What to stop and what to start again.</summary>
    internal sealed record RestartPlan(IReadOnlyList<int> StopIds, string TrayAppPath);

    /// <summary>
    /// Restart only an Ollama that runs from its tray app, and only when the app's path is known:
    /// stop the tray app(s) at THAT path — the host kills each one's process tree, which is the
    /// server the app started — and start the app again. Null when no tray app runs or its path is
    /// unreadable. A bare <c>ollama</c> process is never selected: in the app's folder it can just as
    /// well be a terminal <c>ollama serve</c>, <c>ollama run</c> or <c>ollama pull</c>. A tray app
    /// at another path (another install, another user's session) is never selected either.
    /// </summary>
    internal static RestartPlan? PlanRestart(IReadOnlyList<OllamaProcessInfo> processes, Func<string, bool> fileExists)
    {
        var apps = processes.Where(p => string.Equals(p.Name, TrayAppName, StringComparison.OrdinalIgnoreCase)).ToList();
        var appPath = apps.Select(p => p.Path).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (appPath is null || !fileExists(appPath))
            return null;

        var stop = apps
            .Where(p => string.Equals(p.Path, appPath, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToList();
        return new RestartPlan(stop, appPath);
    }
}

/// <summary>The real <see cref="IOllamaHost"/>: the user environment block and the process table.</summary>
internal sealed class WindowsOllamaHost : IOllamaHost
{
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

    public string? ReadUserVariable(string name)
        => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    // Writes HKCU\Environment and broadcasts WM_SETTINGCHANGE, so apps started from Explorer
    // afterwards see it too.
    public void WriteUserVariable(string name, string value)
        => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);

    public IReadOnlyList<OllamaProcessInfo> ListOllamaProcesses()
    {
        var list = new List<OllamaProcessInfo>();
        foreach (var p in Process.GetProcessesByName(OllamaIntegratedGraphics.TrayAppName))
        {
            using (p)
            {
                string? path = null;
                try { path = p.MainModule?.FileName; }
                catch { /* another user's process, or exited: path unknown */ }
                list.Add(new OllamaProcessInfo(p.Id, p.ProcessName, path));
            }
        }
        return list;
    }

    public bool FileExists(string path) => File.Exists(path);

    public void StopProcess(int id)
    {
        try
        {
            using var p = Process.GetProcessById(id);
            // The tree is the server this tray app started; nothing else is touched.
            p.Kill(entireProcessTree: true);
            if (!p.WaitForExit(StopWait))
                throw new TimeoutException("Ollama did not exit");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Already gone (before the lookup, or between the lookup and the kill).
        }
    }

    public void StartTrayApp(string path, string variable, string value)
    {
        // Started from this process, the app inherits VoiceWink's environment, which predates the
        // write above; the variable is added to the child's block explicitly.
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path) ?? ""
        };
        psi.Environment[variable] = value;
        using var _ = Process.Start(psi);
    }
}

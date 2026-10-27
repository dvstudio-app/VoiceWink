using NAudio.Wave;
using Serilog;
using VoiceWink.Helpers;
using VoiceWink.Services.System;

namespace VoiceWink.Services.Audio;

/// <summary>
/// Plays start/stop recording sounds.
/// Uses NAudio WaveOutEvent for fire-and-forget playback at 0.4 volume.
/// </summary>
public sealed class SoundFeedbackService : IDisposable
{
    private static ILogger Logger => Log.ForContext<SoundFeedbackService>();

    private const float Volume = 0.4f;

    private readonly SettingsService _settings;
    private readonly string _startSoundPath;
    private readonly string _stopSoundPath;
    private readonly Func<IWavePlayer> _createPlayer = () => new WaveOutEvent();
    private readonly List<IDisposable> _activePlayers = new();
    private readonly object _lock = new();
    private bool _disposed;

    public SoundFeedbackService(SettingsService settings)
    {
        _settings = settings;

        var baseDir = AppContext.BaseDirectory;
        _startSoundPath = Path.Combine(baseDir, "Assets", "Sounds", "recstart.wav");
        _stopSoundPath = Path.Combine(baseDir, "Assets", "Sounds", "recstop.wav");
    }

    internal SoundFeedbackService(SettingsService settings, string startSoundPath, string stopSoundPath,
        Func<IWavePlayer>? createPlayer = null)
    {
        _settings = settings;
        _startSoundPath = startSoundPath;
        _stopSoundPath = stopSoundPath;
        if (createPlayer != null) _createPlayer = createPlayer;
    }

    public void PlayStartSound()
    {
        if (!_settings.GetBool(AppDefaults.IsSoundFeedbackEnabled, true))
            return;

        PlaySound(_startSoundPath);
    }

    public void PlayStopSound()
    {
        if (!_settings.GetBool(AppDefaults.IsSoundFeedbackEnabled, true))
            return;

        PlaySound(_stopSoundPath);
    }

    private void PlaySound(string filePath)
    {
        AudioFileReader? reader = null;
        IWavePlayer? player = null;
        try
        {
            if (_disposed) return;

            if (!File.Exists(filePath))
            {
                Logger.Warning("Sound file not found: {Path}", filePath);
                return;
            }

            // Fire-and-forget: create player, play, dispose when done
            reader = new AudioFileReader(filePath) { Volume = Volume };
            player = _createPlayer();
            player.Init(reader);

            lock (_lock)
            {
                _activePlayers.Add(player);
                _activePlayers.Add(reader);
            }

            player.PlaybackStopped += (_, _) =>
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    _activePlayers.Remove(player);
                    _activePlayers.Remove(reader);
                }
                player.Dispose();
                reader.Dispose();
            };

            player.Play();
            Logger.Debug("Playing sound: {Path}", filePath);
        }
        catch (Exception ex)
        {
            // AUD-39: a failed Init (no output device) must not keep the sound file open.
            lock (_lock)
            {
                if (player != null) _activePlayers.Remove(player);
                if (reader != null) _activePlayers.Remove(reader);
            }
            DisposeQuietly(player);
            DisposeQuietly(reader);
            // AUD-39: fires on every beep while the cause lasts, so one Sentry event per run.
            Logger.Write(SessionErrorBudget.LevelFor("sound-feedback:" + ex.GetType().Name), ex,
                "Failed to play sound: {Path}", filePath);
        }
    }

    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Error disposing audio player");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var disposable in _activePlayers)
                DisposeQuietly(disposable);
            _activePlayers.Clear();
        }
    }
}

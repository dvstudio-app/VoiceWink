using System;

namespace VoiceWink.Views;

/// <summary>
/// Residual pure helper after the ERR-PERSIST controller refactor (2026-07-21). The rehydration
/// decision, App-held state snapshot, and state-apply gating all moved into
/// <see cref="MiniRecorderPresentationController"/> (single-owner presentation). Only the pure
/// elapsed-time formatter remains — the MiniRecorder timer text renders through it.
/// </summary>
internal static class MiniRecorderLifecycleState
{
    public static string FormatElapsed(DateTime startedAtUtc, DateTime nowUtc)
    {
        var elapsed = nowUtc - startedAtUtc;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
    }
}

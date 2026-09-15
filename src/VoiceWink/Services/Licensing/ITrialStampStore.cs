namespace VoiceWink.Services.Licensing;

/// <summary>What a durable trial-stamp store answered (LIC-21 PR A).</summary>
internal enum TrialStampReadOutcome
{
    /// <summary>The store holds a readable stamp.</summary>
    Present,
    /// <summary>The store could be consulted and holds no usable stamp — absent, undecryptable or
    /// malformed. Re-seeded silently: the LIC-11 polarity, where "no tattoo" is what a fresh device
    /// and a reinstalled Windows both look like.</summary>
    Missing,
    /// <summary>The store itself could not be reached (a denied registry key, a dead DPAPI). The
    /// orchestrator logs this ONCE per process and stops trying; the trial keeps working from the
    /// settings leg alone.</summary>
    Unavailable,
}

/// <summary>One durable store's answer: the outcome, and the stamp when <see cref="TrialStampReadOutcome.Present"/>.</summary>
internal readonly record struct TrialStampReading(TrialStampReadOutcome Outcome, FirstRunTattoo.Reading? Value)
{
    internal static TrialStampReading Present(FirstRunTattoo.Reading value) => new(TrialStampReadOutcome.Present, value);
    internal static TrialStampReading Missing => new(TrialStampReadOutcome.Missing, null);
    internal static TrialStampReading Unavailable => new(TrialStampReadOutcome.Unavailable, null);
}

/// <summary>
/// The durable leg of the free-trial window — the part that survives deleting
/// <c>%LOCALAPPDATA%\VoiceWink</c> and an uninstall + reinstall (LIC-11's target: the average user's
/// two reset gestures). A seam so that <see cref="TrialWindow"/> can be exercised with an in-memory
/// store and no test ever reads or writes the developer's real registry.
/// <para>Three-way read, two-way write (Codex plan round, PR A): the difference between a store that
/// holds nothing and a store that cannot be reached decides whether the orchestrator re-seeds
/// silently or warns once and stops.</para>
/// </summary>
internal interface ITrialStampStore
{
    TrialStampReading Read();

    /// <summary>Returns <c>false</c> when the write could not land; the caller owns the Warning and the memo.</summary>
    bool Write(FirstRunTattoo.Reading reading);
}

/// <summary>
/// Production store: <c>HKCU\Software\VoiceWink\InstallEpoch</c>, a DPAPI CurrentUser blob
/// (<see cref="FirstRunTattoo"/>'s registry primitives). Velopack's uninstaller removes its own
/// Uninstall entry, the app root and the shortcuts — never this key (its <c>uninstall.rs</c>, read
/// 2026-09-06). The real uninstall + reinstall ran on the VM on 2026-09-07 (owner UAT 180.12,
/// v1.78.370) and the trial resumed — which proves the WINDOW survived (INS-1 keeps the data folder
/// across an uninstall, so the settings leg was still there), not that this key did; the folder-wipe
/// row 180.11 is this store's proof, and nobody inspected HKCU after the uninstall.
/// </summary>
internal sealed class RegistryTrialStampStore : ITrialStampStore
{
    public TrialStampReading Read()
    {
        if (!FirstRunTattoo.TryReadRegistry(out var reading)) return TrialStampReading.Unavailable;
        return reading is { } value ? TrialStampReading.Present(value) : TrialStampReading.Missing;
    }

    public bool Write(FirstRunTattoo.Reading reading) => FirstRunTattoo.TryWriteRegistry(reading);
}

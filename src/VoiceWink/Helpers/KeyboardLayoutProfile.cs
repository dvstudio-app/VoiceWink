using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Answers TWO installed-layout questions: whether any layout uses AltGr at all
/// (<see cref="AnyInstalledLayoutUsesAltGr"/>, the hook's disambiguation), and whether any layout
/// needs AltGr for ESSENTIAL typing characters
/// (<see cref="AnyInstalledLayoutNeedsAltGrForTyping"/>, the HKY-4 seed and the Right Alt warning).
/// </summary>
/// <remarks>
/// <para><b>Why the app needs to know.</b> AltGr is not a modifier Windows reports on its own —
/// it is delivered as a synthetic LeftControl press immediately followed by RightAlt. So on a
/// Belgian, German, French, Nordic or Polish layout, typing <c>€</c> or <c>@</c> puts
/// LeftControl physically down. Without this flag every "Ctrl+X" binding would fire on
/// AltGr+X, which would hit precisely the users two-key combos were added for
/// (HKY-3, 2026-08-30).</para>
///
/// <para><b>How.</b> <c>VkKeyScanExW</c> maps a character to the virtual key and shift state
/// that produces it, and Microsoft documents that a character requiring the right-hand Alt
/// comes back with shift state 6 — Ctrl+Alt — citing the French layout by name. So a sweep of
/// characters that AltGr layouts are known to place behind AltGr answers the question with no
/// side effects.</para>
///
/// <para><b>Why not <c>ToUnicodeEx</c>,</b> which would be the more general instrument: it
/// MUTATES the layout's dead-key state as a side effect, so probing with it can corrupt the
/// user's very next keystroke. <c>VkKeyScanExW</c> is a pure query.</para>
///
/// <para><b>Evaluated once, over every INSTALLED layout</b> — not per keystroke, and not
/// against the foreground window's layout. The hook callback runs inside
/// <c>LowLevelHooksTimeout</c>, a budget <see cref="Services.Input.HotkeyService"/> guards
/// closely enough to push even logging onto the thread pool; a foreground-thread layout lookup
/// per key-down would spend it. Treating "any installed layout uses AltGr" as the verdict is
/// deliberately conservative: a user with US <i>and</i> Belgian installed gets the rule always
/// on, and the only cost of that false positive is that LeftControl will not satisfy Ctrl
/// <i>while RightAlt is also physically down</i> — a chord nobody presses deliberately.</para>
/// </remarks>
internal static class KeyboardLayoutProfile
{
    private static ILogger Logger => Log.ForContext(typeof(KeyboardLayoutProfile));

    /// <summary>High-byte shift-state bits returned by <c>VkKeyScanEx</c>.</summary>
    private const int ShiftStateCtrl = 2;
    private const int ShiftStateAlt = 4;

    /// <summary>
    /// Characters that AltGr layouts commonly place behind AltGr. A layout only has to place
    /// ONE of them there to be an AltGr layout — the sweep is an existence check, not a survey.
    /// A plain US layout reaches none of them via Ctrl+Alt, so it correctly reports false.
    /// </summary>
    internal static readonly char[] ProbeCharacters =
        ['@', '€', '#', '[', ']', '{', '}', '\\', '|', '~', '²'];

    /// <summary>
    /// The ESSENTIAL subset — characters daily typing cannot do without and cannot produce any
    /// other way on layouts that put them behind AltGr.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately excludes <c>€</c> AND <c>²</c>, and BOTH exclusions are load-bearing:
    /// United States-International places € on AltGr+5 and ² on AltGr+2, so dropping only the €
    /// would have left the owner's own layout tripping the probe on ² — the exact false alarm this
    /// set exists to stop (2026-08-30, first live UAT of HKY-4).
    /// <c>EssentialProbeSet_ExcludesConvenienceCharacters</c> pins both.</para>
    ///
    /// <para><b>The exclusion is a JUDGEMENT about cost, not a claim of no cost.</b> A bound bare
    /// modifier's key-down is suppressed whatever the layout, so while Right Alt is the hotkey a
    /// US-International user does lose € and ². The owner judged that not worth an amber warning
    /// or a reseeded default, where losing <c>@ [ ] { } \ |</c> — the AZERTY/QWERTZ case — plainly
    /// is. Do not restate this as "Right Alt is free on US-International": it is cheap there, not
    /// free, and a future reader reasoning from "free" would mis-handle the next layout question.
    /// The layouts the seed and warning exist for (Belgian, German, French, Nordic) all place
    /// several characters of THIS set behind AltGr, so the narrowing costs them nothing.</para>
    /// </remarks>
    internal static readonly char[] TypingEssentialProbeCharacters =
        ['@', '#', '[', ']', '{', '}', '\\', '|', '~'];

    /// <summary>
    /// Pure verdict for ONE <c>VkKeyScanEx</c> result. -1 means the character cannot be typed
    /// on that layout at all.
    /// </summary>
    /// <remarks>
    /// Tests the Ctrl and Alt bits as a MASK rather than comparing the shift state to 6: a
    /// character that needs AltGr+Shift returns 7, and an equality test would miss it.
    /// </remarks>
    internal static bool ScanIndicatesAltGr(int vkKeyScanResult)
    {
        if (vkKeyScanResult < 0) return false; // character unreachable on this layout
        var shiftState = (vkKeyScanResult >> 8) & 0xFF;
        return (shiftState & (ShiftStateCtrl | ShiftStateAlt)) == (ShiftStateCtrl | ShiftStateAlt);
    }

    /// <summary>
    /// Pure sweep over one layout, given a scanner. Injected so tests exercise the decision
    /// without a keyboard layout — the native call is supplied by <see cref="AnyInstalledLayoutUsesAltGr"/>.
    /// </summary>
    internal static bool LayoutUsesAltGr(Func<char, int> scan)
        => AnyProbedCharacterNeedsAltGr(ProbeCharacters, scan);

    /// <summary>
    /// The narrower sweep behind <see cref="AnyInstalledLayoutNeedsAltGrForTyping"/> — see
    /// <see cref="TypingEssentialProbeCharacters"/> for why the two questions differ.
    /// </summary>
    internal static bool LayoutNeedsAltGrForTyping(Func<char, int> scan)
        => AnyProbedCharacterNeedsAltGr(TypingEssentialProbeCharacters, scan);

    private static bool AnyProbedCharacterNeedsAltGr(char[] probeSet, Func<char, int> scan)
    {
        foreach (var ch in probeSet)
        {
            if (ScanIndicatesAltGr(scan(ch))) return true;
        }
        return false;
    }

    /// <summary>
    /// True when ANY layout installed on this machine uses AltGr.
    /// </summary>
    /// <remarks>
    /// <b>Fails toward TRUE.</b> If the layout list or the scan cannot be read, we assume AltGr
    /// is in play, because the two failure directions are not symmetric: a false <c>true</c>
    /// costs a chord nobody presses (LeftControl+RightAlt+key), while a false <c>false</c>
    /// re-opens the exact defect this exists to close — AltGr+X firing an X combo on a European
    /// keyboard.
    /// </remarks>
    public static bool AnyInstalledLayoutUsesAltGr()
        => AnyInstalledLayoutMatches(LayoutUsesAltGr, "AltGr disambiguation");

    /// <summary>
    /// True when any installed layout puts ESSENTIAL typing characters behind AltGr — the
    /// question the HKY-4 first-run seed and the Right Alt warning ask.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a DIFFERENT question from <see cref="AnyInstalledLayoutUsesAltGr"/>,</b>
    /// which stays broad because its consumer is the hook's AltGr disambiguation: on
    /// United States-International, € is AltGr+5, and typing it must never fire a Ctrl+5 combo —
    /// so the hook must know AltGr exists there. That same layout keeps every ESSENTIAL character
    /// off AltGr, so the cost of binding Right Alt there is € and ² only — cheap enough that
    /// seeding away from it and amber-warning about it was the false alarm the owner reported on
    /// first live UAT (2026-08-30), NOT free (see
    /// <see cref="TypingEssentialProbeCharacters"/>). Same failure direction as the broad probe:
    /// unreadable layouts assume TRUE. Known residual, recorded on the HKY-4 card: layouts whose
    /// NATIONAL LETTERS ride AltGr over a US-like base (Polish programmers, Romanian programmers)
    /// pass this probe, so they keep the RightAlt default — their loss is real but was never
    /// detected by the broad probe's character set either.
    /// </remarks>
    public static bool AnyInstalledLayoutNeedsAltGrForTyping()
        => AnyInstalledLayoutMatches(LayoutNeedsAltGrForTyping, "essential-AltGr typing");

    private static bool AnyInstalledLayoutMatches(Func<Func<char, int>, bool> layoutPredicate, string purpose)
    {
        try
        {
            var count = NativeInterop.GetKeyboardLayoutList(0, null);
            if (count <= 0)
            {
                Logger.Warning("Keyboard layout list unavailable (count {Count}) — assuming AltGr is in use for {Purpose}", count, purpose);
                return true;
            }

            var layouts = new IntPtr[count];
            var filled = NativeInterop.GetKeyboardLayoutList(count, layouts);
            if (filled <= 0)
            {
                Logger.Warning("Keyboard layout list returned no entries — assuming AltGr is in use for {Purpose}", purpose);
                return true;
            }

            for (var i = 0; i < filled; i++)
            {
                var hkl = layouts[i];
                if (layoutPredicate(ch => NativeInterop.VkKeyScanExW(ch, hkl)))
                {
                    Logger.Information("AltGr-relevant keyboard layout detected ({Layout:X}) for {Purpose}", hkl.ToInt64(), purpose);
                    return true;
                }
            }

            Logger.Information("No AltGr-relevant keyboard layout among {Count} installed for {Purpose}", filled, purpose);
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Keyboard layout probe failed — assuming AltGr is in use for {Purpose}", purpose);
            return true;
        }
    }
}

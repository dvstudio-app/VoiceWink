using System.Runtime.InteropServices;

namespace VoiceWink.Helpers;

/// <summary>Which language a text reads as: the E-mail / Chat language rule's detector
/// (<see cref="LocalOutputGuard.MeasureLanguage"/>). A seam so tests can answer without Windows.</summary>
public interface ITextLanguageDetector
{
    /// <summary>The most likely language's primary subtag in lower case ("nl", "en"), or null when
    /// the detector gives no answer or is unavailable.</summary>
    string? TopLanguage(string text);
}

/// <summary>
/// Windows' own language detector — Extended Linguistic Services (<c>elscore.dll</c>,
/// <c>ELS_GUID_LANGUAGE_DETECTION</c>), part of every Windows edition since 7, no download, no
/// network. It ranks language tags, most likely first, and reports no confidence. Chosen over word
/// lists by measurement (<c>tools/language-guard-compare</c>, 2026-10-21): it judges every language
/// it knows instead of the eight a list could hold. Never throws: any failure, an absent DLL
/// included, reads as no answer, so the rule abstains. Its P/Invoke lives here rather than in
/// <see cref="NativeInterop"/> so the harness can link this file alone.
/// </summary>
internal sealed class TextLanguageDetector : ITextLanguageDetector, IDisposable
{
    private static readonly Guid LanguageDetection = new("CF7E00B1-909B-4D95-A8F4-611F7C377702");

    /// <summary>The longest text passed to ELS: a dictation's language shows in its first lines,
    /// and the cap bounds the native buffer.</summary>
    internal const int MaxChars = 4096;

    // One lock over the service handle's whole life: ELS's services must outlive every property
    // bag they produced, so a detection (through its free) and Dispose never interleave.
    private readonly object _gate = new();
    private readonly Guid _service;
    private readonly Action<string>? _reportUnavailable;
    private IntPtr _services;
    private bool _initialized;
    private bool _disposed;

    /// <param name="reportUnavailable">Told once, with a cause and never text, when the detector
    /// cannot be reached on this PC: the language rule is then off for the session and nothing
    /// else would say so. The app passes a log line; the harness passes nothing.</param>
    public TextLanguageDetector(Action<string>? reportUnavailable = null)
        : this(reportUnavailable, LanguageDetection)
    {
    }

    /// <summary>Test seam: a service GUID ELS does not have exercises the unavailable path.</summary>
    internal TextLanguageDetector(Action<string>? reportUnavailable, Guid service)
    {
        _reportUnavailable = reportUnavailable;
        _service = service;
    }

    public string? TopLanguage(string text)
    {
        var ranked = Detect(text);
        return ranked.Count == 0 ? null : ranked[0];
    }

    /// <summary>ELS's ranked primary subtags, most likely first; empty when it gives no answer.</summary>
    internal IReadOnlyList<string> Detect(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        if (text.Length > MaxChars)
            text = text[..MaxChars];
        lock (_gate)
        {
            if (!EnsureServices())
                return result;
            try
            {
                // The input buffer must stay valid until MappingFreePropertyBag returns.
                var buffer = Marshal.StringToHGlobalUni(text);
                try
                {
                    var bag = new PropertyBag { Size = (UIntPtr)Marshal.SizeOf<PropertyBag>() };
                    if (MappingRecognizeText(_services, buffer, (uint)text.Length, 0, IntPtr.Zero, ref bag) != 0)
                        return result;
                    try
                    {
                        if (bag.RangesCount > 0 && bag.ResultRanges != IntPtr.Zero)
                            ReadTags(Marshal.PtrToStructure<DataRange>(bag.ResultRanges), result);
                    }
                    finally
                    {
                        MappingFreePropertyBag(ref bag);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception)
            {
                // An unexpected native failure reads as no answer: the rule then abstains.
                result.Clear();
            }
        }
        return result;
    }

    // The range's data is a double-null-terminated list of UTF-16 tags ("nl\0de\0\0").
    private static void ReadTags(DataRange range, List<string> result)
    {
        if (range.Data == IntPtr.Zero)
            return;
        var end = IntPtr.Add(range.Data, (int)range.DataSize);
        var p = range.Data;
        while (p.ToInt64() < end.ToInt64())
        {
            var tag = Marshal.PtrToStringUni(p);
            if (string.IsNullOrEmpty(tag))
                break;
            result.Add(tag.Split('-')[0].ToLowerInvariant());
            p = IntPtr.Add(p, (tag.Length + 1) * 2);
        }
    }

    // Under _gate. Enumerated once; a failure is remembered, so an absent detector costs one try.
    private bool EnsureServices()
    {
        if (_disposed)
            return false;
        if (_initialized)
            return _services != IntPtr.Zero;
        _initialized = true;
        var pGuid = IntPtr.Zero;
        string? cause = null;
        try
        {
            pGuid = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(_service, pGuid, false);
            var options = new EnumOptions { Size = (UIntPtr)Marshal.SizeOf<EnumOptions>(), Guid = pGuid };
            var hr = MappingGetServices(ref options, out var services, out var count);
            if (hr != 0)
            {
                cause = $"HRESULT 0x{hr:X8}";
            }
            else if (count > 0)
            {
                _services = services;
            }
            else
            {
                cause = "no detection service";
                if (services != IntPtr.Zero)
                    MappingFreeServices(services);
            }
        }
        catch (Exception ex)
        {
            _services = IntPtr.Zero;
            cause = ex.GetType().Name;
        }
        finally
        {
            if (pGuid != IntPtr.Zero)
                Marshal.FreeHGlobal(pGuid);
        }
        if (cause is not null)
        {
            try { _reportUnavailable?.Invoke(cause); }
            catch (Exception) { }
        }
        return _services != IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_services != IntPtr.Zero)
            {
                try { MappingFreeServices(_services); }
                catch (Exception) { }
                _services = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumOptions
    {
        public UIntPtr Size;
        public IntPtr Category, InputLanguage, OutputLanguage, InputScript, OutputScript, InputContentType, OutputContentType;
        public IntPtr Guid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyBag
    {
        public UIntPtr Size;
        public IntPtr ResultRanges;
        public uint RangesCount;
        public IntPtr ServiceData;
        public uint ServiceDataSize;
        public IntPtr CallerData;
        public uint CallerDataSize;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataRange
    {
        public uint StartIndex, EndIndex;
        public IntPtr Description;
        public uint DescriptionLength;
        public IntPtr Data;
        public uint DataSize;
        public IntPtr ContentType, ActionIds;
        public uint ActionsCount;
        public IntPtr ActionDisplayNames;
    }

    [DllImport("elscore.dll")]
    private static extern int MappingGetServices(ref EnumOptions options, out IntPtr services, out uint count);

    [DllImport("elscore.dll")]
    private static extern int MappingFreeServices(IntPtr services);

    [DllImport("elscore.dll")]
    private static extern int MappingRecognizeText(IntPtr service, IntPtr text, uint length, uint index, IntPtr options, ref PropertyBag bag);

    [DllImport("elscore.dll")]
    private static extern int MappingFreePropertyBag(ref PropertyBag bag);
}

namespace VoiceWink.Helpers;

/// <summary>
/// The save/open file pickers, initialised with the main window's handle — an unpackaged WinUI 3
/// app must do that or the picker throws. Null means the user cancelled, or there was no main
/// window to own the picker (logged).
/// </summary>
public static class FilePickers
{
    private static Serilog.ILogger Logger => Serilog.Log.ForContext(typeof(FilePickers));

    public static async Task<string?> PickSaveFileAsync(string suggestedFileName, string typeLabel, string extension)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            SuggestedFileName = suggestedFileName,
        };
        picker.FileTypeChoices.Add(typeLabel, new List<string> { extension });
        if (!TryInitialize(picker)) return null;
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickOpenFileAsync(string extension)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add(extension);
        if (!TryInitialize(picker)) return null;
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static bool TryInitialize(object picker)
    {
        if (App.MainWindow is null)
        {
            Logger.Warning("File picker aborted: no main window for the picker's owner handle");
            return false;
        }
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        return true;
    }
}

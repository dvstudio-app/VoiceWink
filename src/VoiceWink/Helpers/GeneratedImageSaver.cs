using Serilog;

namespace VoiceWink.Helpers;

/// <summary>
/// Collision-proof save for generated images (IMG-3). Extracted from
/// <c>MainViewModel.SaveGeneratedImageAsync</c> with two hardenings that make the
/// batch ambiguity probe sound: a full 32-hex GUID in the name (the old 8-hex slice
/// left same-second collisions plausible inside a batch) and
/// <see cref="FileMode.CreateNew"/> instead of overwrite-capable
/// <c>File.WriteAllBytes</c> — a collision refuses and retries once with a fresh GUID
/// rather than silently destroying an earlier item's file. Fail-soft null on any
/// failure, exactly like the original. GUID/clock are injected for deterministic tests;
/// the production wrapper resolves the directory (<c>AppPaths.EnsureImages()</c>)
/// INSIDE its own fail-soft boundary, as today.
/// </summary>
internal static class GeneratedImageSaver
{
    private static ILogger Logger => Log.ForContext(typeof(GeneratedImageSaver));

    /// <param name="kind">
    /// What the payload ACTUALLY is, classified once at the receipt boundary. The file is named for
    /// this and never for an assumption: providers return WebP (riverflow) and others through the
    /// same code path, and a <c>.png</c> holding WebP breaks the clipboard, Explorer, and — until
    /// the read gate learned to sniff — the mime sent back to providers on Iterate.
    /// </param>
    internal static async Task<string?> SaveAsync(
        byte[] imageBytes, ImageBytesKind kind, string imagesDir, Func<Guid> newGuid, Func<DateTime> now)
    {
        // Outside THIS method's fail-soft catch, which exists for IO failures: being handed a kind
        // that must never be persisted (SVG) is a caller bug, not a disk problem.
        //
        // Honest limit: MainViewModel.SaveGeneratedImageAsync wraps this call in its own catch-all,
        // so in production the throw still lands as a null there. It is unreachable anyway —
        // AIEnhancementService.ClassifyGeneratedImage rejects SVG before Kind can hold it — so this
        // is a guard against a FUTURE caller that skips the classifier, and the placement keeps the
        // distinction visible at the level where it is decided rather than being merged into "some
        // IO went wrong".
        var extension = ImageBytesFormat.PersistedExtension(kind);

        try
        {
            return await Task.Run(() =>
            {
                // One retry on a name collision: with a full GUID a collision is
                // astronomically rare, so a SECOND one means something is genuinely
                // wrong — fall through to the fail-soft null rather than loop.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var filePath = Path.Combine(
                        imagesDir, $"image_{now():yyyyMMdd_HHmmss}_{newGuid():N}{extension}");
                    try
                    {
                        using var stream = new FileStream(
                            filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        stream.Write(imageBytes, 0, imageBytes.Length);
                        Logger.Information("Generated image saved: {Path} ({Size} bytes, {Kind})",
                            filePath, imageBytes.Length, kind);
                        return (string?)filePath;
                    }
                    catch (IOException) when (attempt == 0 && File.Exists(filePath))
                    {
                        Logger.Warning("Generated image name collided, retrying with a fresh id: {Path}", filePath);
                    }
                }
                Logger.Warning("Failed to save generated image to disk (name collisions)");
                return null;
            });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to save generated image to disk");
            return null;
        }
    }
}

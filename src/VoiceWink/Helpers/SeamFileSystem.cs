namespace VoiceWink.Helpers;

/// <summary>
/// Narrow filesystem seam shared by the two pre-bootstrap install helpers
/// (<see cref="UpdateExeSelfHeal"/> and <see cref="PrereqInstaller"/>) so their
/// IO can be exercised by unit tests without touching the real
/// <c>%LOCALAPPDATA%</c> tree, spawning processes, or building temp files on
/// disk. Production code always uses <see cref="ProductionFileSystem.Instance"/>,
/// which is a verbatim pass-through to <see cref="global::System.IO.File"/> /
/// <see cref="global::System.IO.Directory"/> — the seam exists only to let the
/// <c>VoiceWink.Tests</c> project (granted <c>InternalsVisibleTo</c>) inject a
/// fake. No production call site changes behavior.
///
/// <para>The interface is the UNION of what both helpers need so there is a
/// single production implementation and a single test fake — no duplicated
/// <c>File.*</c> wrappers.</para>
/// </summary>
internal interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);

    /// <summary>Length in bytes of an existing file. Mirrors
    /// <c>new FileInfo(path).Length</c>.</summary>
    long GetFileLength(string path);

    /// <summary>Enumerate files in <paramref name="directory"/> matching the
    /// simple <paramref name="searchPattern"/> (e.g. <c>*.nupkg</c>), returning
    /// full paths. Mirrors <see cref="global::System.IO.Directory.EnumerateFiles(string,string)"/>.</summary>
    global::System.Collections.Generic.IEnumerable<string> EnumerateFiles(string directory, string searchPattern);

    /// <summary>Create (truncating) a file for writing and return the open
    /// stream. Mirrors <see cref="global::System.IO.File.Create(string)"/>.</summary>
    global::System.IO.Stream CreateFile(string path);

    void MoveFile(string sourcePath, string destPath, bool overwrite);
    void DeleteFile(string path);

    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void CreateDirectory(string path);
}

/// <summary>
/// Production <see cref="IFileSystem"/> — a thin, behavior-identical pass-through
/// to <c>System.IO</c>. Stateless singleton; never holds locks or caches.
/// </summary>
internal sealed class ProductionFileSystem : IFileSystem
{
    internal static readonly ProductionFileSystem Instance = new();
    private ProductionFileSystem() { }

    public bool FileExists(string path) => global::System.IO.File.Exists(path);

    public bool DirectoryExists(string path) => global::System.IO.Directory.Exists(path);

    public long GetFileLength(string path) => new global::System.IO.FileInfo(path).Length;

    public global::System.Collections.Generic.IEnumerable<string> EnumerateFiles(
        string directory, string searchPattern) =>
        global::System.IO.Directory.EnumerateFiles(directory, searchPattern);

    public global::System.IO.Stream CreateFile(string path) =>
        global::System.IO.File.Create(path);

    public void MoveFile(string sourcePath, string destPath, bool overwrite) =>
        global::System.IO.File.Move(sourcePath, destPath, overwrite);

    public void DeleteFile(string path) => global::System.IO.File.Delete(path);

    public string ReadAllText(string path) => global::System.IO.File.ReadAllText(path);

    public void WriteAllText(string path, string contents) =>
        global::System.IO.File.WriteAllText(path, contents);

    public void CreateDirectory(string path) =>
        global::System.IO.Directory.CreateDirectory(path);
}

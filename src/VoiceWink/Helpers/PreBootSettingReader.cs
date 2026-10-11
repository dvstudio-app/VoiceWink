namespace VoiceWink.Helpers;

/// <summary>What a pre-boot read of one boolean setting found.</summary>
public enum PreBootSetting
{
    /// <summary>No <c>settings.json</c> at all (a first install, or one whose data was removed).</summary>
    FileAbsent,

    /// <summary>The file is a readable JSON object without this key.</summary>
    KeyAbsent,

    /// <summary>The key holds JSON <c>true</c>.</summary>
    True,

    /// <summary>The key holds JSON <c>false</c>.</summary>
    False,

    /// <summary>The file could not be read or parsed, its root is not an object, or the key holds
    /// something other than a JSON boolean.</summary>
    Unreadable,
}

/// <summary>
/// The ONE reader of a boolean setting straight from <c>settings.json</c>, for code that runs before
/// any <c>SettingsService</c> exists — the crash opt-in, the GPU toggle and the verbose-logging flag at
/// startup, and the installer's report inside Velopack's install hook, where no service ever exists.
/// A real <c>SettingsService</c> would log and start its own debounce timer, both undesirable there.
///
/// <para>It reports WHAT it found and never decides: each caller chooses the direction an absent or
/// unreadable value fails in (<see cref="ReadBool"/>), because the right direction differs — consent
/// fails off, a performance preference fails toward its default, and the installer's report fails
/// toward not sending when settings exist but cannot be read.</para>
/// </summary>
internal static class PreBootSettingReader
{
    public static PreBootSetting Read(string settingsFilePath, string key)
    {
        // Read first, never File.Exists: it answers false on an ACCESS error too, which would turn
        // settings that exist but cannot be read into "absent" — for the installer's report a
        // fail-open, since the policy says settings it cannot read stop it (SettingsService's own
        // TryLoadPrimary avoids File.Exists for the same reason). Only a missing file or folder is absent.
        string json;
        try
        {
            json = File.ReadAllText(settingsFilePath);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return PreBootSetting.FileAbsent;
        }
        catch
        {
            return PreBootSetting.Unreadable;
        }

        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(json);
            // Guard against a top-level array or primitive — TryGetProperty throws on non-object roots.
            if (doc.RootElement.ValueKind != global::System.Text.Json.JsonValueKind.Object) return PreBootSetting.Unreadable;
            if (!doc.RootElement.TryGetProperty(key, out var el)) return PreBootSetting.KeyAbsent;
            return el.ValueKind switch
            {
                global::System.Text.Json.JsonValueKind.True => PreBootSetting.True,
                global::System.Text.Json.JsonValueKind.False => PreBootSetting.False,
                _ => PreBootSetting.Unreadable,
            };
        }
        catch
        {
            return PreBootSetting.Unreadable;
        }
    }

    /// <summary>The setting as a bool: the stored value when it is a JSON boolean,
    /// <paramref name="whenAbsent"/> when the file or the key is missing, and
    /// <paramref name="whenUnreadable"/> otherwise.</summary>
    public static bool ReadBool(string settingsFilePath, string key, bool whenAbsent, bool whenUnreadable) =>
        Read(settingsFilePath, key) switch
        {
            PreBootSetting.True => true,
            PreBootSetting.False => false,
            PreBootSetting.FileAbsent or PreBootSetting.KeyAbsent => whenAbsent,
            _ => whenUnreadable,
        };
}

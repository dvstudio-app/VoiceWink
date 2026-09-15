using VoiceWink.Services.System;

namespace VoiceWink.Helpers;

/// <summary>
/// ENH-6g: the per-install HMAC key behind content-addressed reference-copy filenames
/// (<c>ref_&lt;HMACSHA256(key, bytes)&gt;.&lt;ext&gt;</c>). A KEYED hash — not a raw
/// content hash — so the basenames that ride <c>history.json</c> exports reveal
/// nothing testable about which images were used (the GUID-era privacy property).
/// Stored under <see cref="AppDefaults.ReferenceDedupKey"/>, which is classified
/// SENSITIVE (GDPR export redacts it, settings export excludes it, import rejects it)
/// and erased with settings on Art. 17 erasure. A malformed stored value regenerates
/// (never throws): the only cost of a new key is that dedup stops matching
/// pre-existing copies, which then age out through the normal row lifecycle.
/// Process-atomicity is the CALLER's job — <c>ReferencePersistence</c> resolves this
/// once, lazily, under its own lock (concurrent first use must not mint two keys).
/// </summary>
public static class ReferenceDedupKey
{
    public const int KeyBytes = 32;

    public static byte[] GetOrCreate(SettingsService settings)
    {
        var stored = settings.GetString(AppDefaults.ReferenceDedupKey, "");
        if (!string.IsNullOrEmpty(stored))
        {
            try
            {
                var key = Convert.FromBase64String(stored);
                if (key.Length == KeyBytes)
                    return key;
            }
            catch (FormatException)
            {
                // Malformed — fall through to regenerate.
            }
        }

        var fresh = global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(KeyBytes);
        settings.SetString(AppDefaults.ReferenceDedupKey, Convert.ToBase64String(fresh));
        return fresh;
    }
}

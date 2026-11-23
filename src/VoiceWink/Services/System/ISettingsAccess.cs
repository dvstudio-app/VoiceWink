namespace VoiceWink.Services.System;

/// <summary>The settings reads and writes the startup migrations use, so an import can run them on a candidate that is not yet the store (SET-1).</summary>
internal interface ISettingsAccess
{
    bool Contains(string key);
    string GetString(string key, string defaultValue = "");
    void SetString(string key, string value);
    bool Remove(string key);
}

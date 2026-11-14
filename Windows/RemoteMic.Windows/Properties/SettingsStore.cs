using Microsoft.Win32;

namespace RemoteMic.Windows.Properties;

internal static class SettingsStore
{
    private const string KeyPath = @"Software\RemoteMic";

    public static string? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(name, value, RegistryValueKind.String);
        }
        catch
        {
            // Settings are optional; a locked-down profile must not prevent startup.
        }
    }

    public static void Delete(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
            // Settings are optional; a locked-down profile must not prevent startup.
        }
    }
}

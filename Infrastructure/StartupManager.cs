using Microsoft.Win32;

namespace Noticker.Infrastructure;

public static class StartupManager
{
    private const string RegKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Noticker";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegKey);
        return key?.GetValue(AppName) is not null;
    }

    public static void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open Run registry key.");
        key.SetValue(AppName, $"\"{exePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }
}

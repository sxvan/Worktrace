using Microsoft.Win32;

namespace Capture.App;

/// <summary>
/// Start at sign-in through the per-user Run key. Chosen over a scheduled task because it needs
/// no administrator rights, shows up in Task Manager's startup list, and is one value to remove.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TimeCapture";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>True if the Run entry exists and points at this executable.</summary>
    public static bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value
                   && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

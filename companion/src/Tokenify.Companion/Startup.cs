using Microsoft.Win32;

namespace Tokenify.Companion;

/// <summary>"Start with Windows": a normal per-user Run entry (no administrator rights, no service, nothing
/// that touches Fortnite). The companion then starts quietly in the notification area.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TOKENIFY Companion";

    /// <summary>A test copy (pointed at a test server or a test data folder) never registers itself to start with
    /// Windows: started at sign-in it would run without its test settings, as if it were a real install.</summary>
    public static bool Available => !AppInfo.TestCopy;

    public static bool IsEnabled()
    {
        if (!Available) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && v.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        if (!Available)
        {
            Log.Info("Start with Windows is not available for a test copy; nothing changed");
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
            {
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not change Start with Windows", ex);
        }
    }
}

using Microsoft.Win32;

namespace SonarLite;

/// <summary>
/// Autostart via HKCU\...\Run. Enabling it also parks GG's own Run entry (the official
/// stack would otherwise start at login and fight with us); disabling puts it back.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ParkedKey = @"Software\SonarLite\ParkedRun";
    private const string OwnValue = "SonarLite";
    private const string GgValue = "SteelSeriesGG";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue(OwnValue) != null;
        }
    }

    /// <summary>True when GG has re-added its autostart entry (e.g. after a GG update).</summary>
    public static bool IsGgAutostartActive
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue(GgValue) != null;
        }
    }

    public static void Enable()
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(OwnValue, $"\"{Environment.ProcessPath}\" --minimized");
        if (run.GetValue(GgValue) is string ggCommand)
        {
            using var parked = Registry.CurrentUser.CreateSubKey(ParkedKey);
            parked.SetValue(GgValue, ggCommand);
            run.DeleteValue(GgValue);
        }
    }

    public static void Disable()
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.DeleteValue(OwnValue, throwOnMissingValue: false);
        using var parked = Registry.CurrentUser.OpenSubKey(ParkedKey, writable: true);
        if (parked?.GetValue(GgValue) is string ggCommand)
        {
            run.SetValue(GgValue, ggCommand);
            parked.DeleteValue(GgValue);
        }
    }

    /// <summary>GG's launch command, from its Run entry or our parked copy.</summary>
    public static string? GetGgCommand()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(GgValue) is string fromRun)
            return fromRun;
        using var parked = Registry.CurrentUser.OpenSubKey(ParkedKey);
        return parked?.GetValue(GgValue) as string;
    }
}

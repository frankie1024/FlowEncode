using System;
using System.Runtime.InteropServices;
using Windows.UI.ViewManagement;

namespace FlowEncode.Controls.Shared;

public static class UiMotionPolicy
{
    private static readonly UISettings UiSettings = new();
    private static readonly AccessibilitySettings AccessibilitySettings = new();

    internal static event EventHandler? SettingsChanged;

    static UiMotionPolicy()
    {
        try
        {
            AccessibilitySettings.HighContrastChanged += (_, _) => SettingsChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // Some desktop sessions reject notifications. The policy is still checked when effects update.
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                UiSettings.AnimationsEnabledChanged += (_, _) => SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                // Keep checking the current setting when progress or visibility changes.
            }
        }
    }

    public static bool AreCustomAnimationsEnabled()
    {
        return AreSystemAnimationsEnabled() && !IsHighContrastEnabled();
    }

    public static bool ArePlatformTransitionsEnabled()
    {
        return AreSystemAnimationsEnabled() && !IsHighContrastEnabled();
    }

    private static bool AreSystemAnimationsEnabled()
    {
        try
        {
            return UiSettings.AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }

    public static bool IsHighContrastEnabled()
    {
        try
        {
            return AccessibilitySettings.HighContrast;
        }
        catch
        {
            return false;
        }
    }
}

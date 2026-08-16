using Windows.UI.ViewManagement;

namespace FlowEncode.Controls.Shared;

public static class UiMotionPolicy
{
    private static readonly UISettings UiSettings = new();
    private static readonly AccessibilitySettings AccessibilitySettings = new();

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

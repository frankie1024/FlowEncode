using System;
namespace FlowEncode.Controls.Shared;

internal static class UiTokens
{
    public static double SpacingXS => GetDouble("AppSpacingXS");
    public static double SpacingS => GetDouble("AppSpacingS");
    public static double SpacingM => GetDouble("AppSpacingM");
    public static double SpacingL => GetDouble("AppSpacingL");
    public static double SpacingXL => GetDouble("AppSpacingXL");
    public static double SpacingXXL => GetDouble("AppSpacingXXL");
    public static double DisplayFontSize => GetDouble("AppDisplayFontSize");
    public static double DisplayFontSizeCompact => GetDouble("AppDisplayFontSizeCompact");
    public static Microsoft.UI.Xaml.Thickness PagePadding => GetThickness("AppPagePadding");
    public static Microsoft.UI.Xaml.Thickness CardPadding => GetThickness("AppCardPadding");
    public static Microsoft.UI.Xaml.Thickness InsetPanelPadding => GetThickness("AppInsetPanelPadding");

    public static Microsoft.UI.Xaml.Thickness UniformThickness(double value)
    {
        return new Microsoft.UI.Xaml.Thickness(value);
    }

    private static double GetDouble(string key)
    {
        var value = GetRequiredResource(key);
        if (value is double result)
        {
            return result;
        }

        throw new InvalidOperationException($"Resource '{key}' is not a double.");
    }

    private static Microsoft.UI.Xaml.Thickness GetThickness(string key)
    {
        var value = GetRequiredResource(key);
        if (value is Microsoft.UI.Xaml.Thickness result)
        {
            return result;
        }

        throw new InvalidOperationException($"Resource '{key}' is not a Thickness.");
    }

    private static object GetRequiredResource(string key)
    {
        var resources = Microsoft.UI.Xaml.Application.Current?.Resources
            ?? throw new InvalidOperationException("Application resources are unavailable.");

        if (resources.TryGetValue(key, out var value) && value is not null)
        {
            return value;
        }

        throw new InvalidOperationException($"Resource '{key}' was not found.");
    }
}

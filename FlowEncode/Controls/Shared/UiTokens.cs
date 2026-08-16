using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
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
    public static double MotionFast => GetDouble("MotionFast");
    public static double MotionNormal => GetDouble("MotionNormal");
    public static double MotionEmphasis => GetDouble("MotionEmphasis");
    public static double MotionFieldOffsetY => GetDouble("MotionFieldOffsetY");
    public static double MotionHoverLiftY => GetDouble("MotionHoverLiftY");
    public static double MotionListInsertOffsetY => GetDouble("MotionListInsertOffsetY");
    public static double MotionProgressSmoothMilliseconds => GetDouble("MotionProgressSmoothMilliseconds");
    public static Microsoft.UI.Xaml.Thickness PagePadding => GetThickness("AppPagePadding");
    public static Microsoft.UI.Xaml.Thickness CardPadding => GetThickness("AppCardPadding");
    public static Microsoft.UI.Xaml.Thickness InsetPanelPadding => GetThickness("AppInsetPanelPadding");
    public static Duration MotionFastDuration => GetDuration("MotionFast");
    public static Duration MotionNormalDuration => GetDuration("MotionNormal");
    public static Duration MotionEmphasisDuration => GetDuration("MotionEmphasis");
    public static EasingFunctionBase MotionEasingEnter => GetEasing("MotionEasingEnter");
    public static EasingFunctionBase MotionEasingInOut => GetEasing("MotionEasingInOut");

    public static Microsoft.UI.Xaml.Thickness UniformThickness(double value)
    {
        return new Microsoft.UI.Xaml.Thickness(value);
    }

    public static Duration GetDuration(string key)
    {
        return new Duration(TimeSpan.FromMilliseconds(GetDouble(key)));
    }

    public static bool TryGetThemeResource(FrameworkElement element, string key, out object? value)
    {
        value = null;
        if (UiMotionPolicy.IsHighContrastEnabled())
        {
            return false;
        }

        var themeKey = element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (TryGetThemeResource(element.Resources, themeKey, key, out value))
        {
            return true;
        }

        return TryGetThemeResource(Microsoft.UI.Xaml.Application.Current?.Resources, themeKey, key, out value);
    }

    private static bool TryGetThemeResource(ResourceDictionary? resources, string themeKey, string key, out object? value)
    {
        value = null;
        if (resources is null)
        {
            return false;
        }

        if (resources.ThemeDictionaries.TryGetValue(themeKey, out var dictionary)
            && dictionary is ResourceDictionary themeResources
            && themeResources.TryGetValue(key, out value)
            && value is not null)
        {
            return true;
        }

        foreach (var mergedDictionary in resources.MergedDictionaries)
        {
            if (TryGetThemeResource(mergedDictionary, themeKey, key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
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

    private static EasingFunctionBase GetEasing(string key)
    {
        var value = GetRequiredResource(key);
        if (value is CubicEase cubicEase)
        {
            return new CubicEase
            {
                EasingMode = cubicEase.EasingMode
            };
        }

        throw new InvalidOperationException($"Resource '{key}' is not a CubicEase.");
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

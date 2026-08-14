using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.Controls.Shared;

public enum UiElevationLevel
{
    E0 = 0,
    E1 = 1,
    E2 = 2,
    E3 = 3
}

public static class UiElevation
{
    private static readonly ConditionalWeakTable<FrameworkElement, ElevationState> States = new();

    public static readonly DependencyProperty LevelProperty = DependencyProperty.RegisterAttached(
        "Level",
        typeof(UiElevationLevel),
        typeof(UiElevation),
        new PropertyMetadata(UiElevationLevel.E0, OnElevationPropertyChanged));

    public static readonly DependencyProperty HoverLevelProperty = DependencyProperty.RegisterAttached(
        "HoverLevel",
        typeof(UiElevationLevel),
        typeof(UiElevation),
        new PropertyMetadata(UiElevationLevel.E0, OnElevationPropertyChanged));

    public static UiElevationLevel GetLevel(DependencyObject obj) => (UiElevationLevel)obj.GetValue(LevelProperty);
    public static void SetLevel(DependencyObject obj, UiElevationLevel value) => obj.SetValue(LevelProperty, value);

    public static UiElevationLevel GetHoverLevel(DependencyObject obj) => (UiElevationLevel)obj.GetValue(HoverLevelProperty);
    public static void SetHoverLevel(DependencyObject obj, UiElevationLevel value) => obj.SetValue(HoverLevelProperty, value);

    private static void OnElevationPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var level = GetLevel(element);
        var hoverLevel = GetHoverLevel(element);
        if (level == UiElevationLevel.E0 && hoverLevel == UiElevationLevel.E0)
        {
            if (States.TryGetValue(element, out var existingState))
            {
                existingState.Detach();
                States.Remove(element);
            }

            ApplyElevation(element, UiElevationLevel.E0);
            return;
        }

        States.GetValue(element, CreateState).Refresh();
    }

    private static ElevationState CreateState(FrameworkElement element)
    {
        return new ElevationState(element);
    }

    private static void ApplyElevation(FrameworkElement element, UiElevationLevel level)
    {
        element.Shadow = ResolveShadow(level);

        var translation = element.Translation;
        element.Translation = new Vector3(translation.X, translation.Y, ResolveTranslationZ(level));
    }

    private static Shadow? ResolveShadow(UiElevationLevel level)
    {
        var resourceKey = level switch
        {
            UiElevationLevel.E1 => "ElevationShadowE1",
            UiElevationLevel.E2 => "ElevationShadowE2",
            UiElevationLevel.E3 => "ElevationShadowE3",
            _ => null
        };

        if (resourceKey is null
            || Microsoft.UI.Xaml.Application.Current?.Resources.TryGetValue(resourceKey, out var resource) != true)
        {
            return null;
        }

        return resource as Shadow;
    }

    private static float ResolveTranslationZ(UiElevationLevel level)
    {
        var resourceKey = level switch
        {
            UiElevationLevel.E1 => "ThemeElevationZ_E1",
            UiElevationLevel.E2 => "ThemeElevationZ_E2",
            UiElevationLevel.E3 => "ThemeElevationZ_E3",
            _ => null
        };

        if (resourceKey is null
            || Microsoft.UI.Xaml.Application.Current?.Resources.TryGetValue(resourceKey, out var resource) != true)
        {
            return 0f;
        }

        return resource switch
        {
            double value => (float)value,
            float value => value,
            _ => 0f
        };
    }

    private sealed class ElevationState
    {
        private readonly FrameworkElement _element;
        private bool _isPointerOver;
        private bool _isPressed;

        public ElevationState(FrameworkElement element)
        {
            _element = element;
            _element.Loaded += Element_Loaded;
            _element.Unloaded += Element_Unloaded;
            _element.ActualThemeChanged += Element_ActualThemeChanged;
            _element.PointerEntered += Element_PointerEntered;
            _element.PointerExited += Element_PointerExited;
            _element.PointerPressed += Element_PointerPressed;
            _element.PointerReleased += Element_PointerReleased;
            _element.PointerCanceled += Element_PointerCanceled;
        }

        public void Detach()
        {
            _element.Loaded -= Element_Loaded;
            _element.Unloaded -= Element_Unloaded;
            _element.ActualThemeChanged -= Element_ActualThemeChanged;
            _element.PointerEntered -= Element_PointerEntered;
            _element.PointerExited -= Element_PointerExited;
            _element.PointerPressed -= Element_PointerPressed;
            _element.PointerReleased -= Element_PointerReleased;
            _element.PointerCanceled -= Element_PointerCanceled;
        }

        public void Refresh()
        {
            ApplyCurrentLevel();
        }

        private void Element_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyCurrentLevel();
        }

        private void Element_Unloaded(object sender, RoutedEventArgs e)
        {
            _isPointerOver = false;
            _isPressed = false;
            ApplyCurrentLevel();
        }

        private void Element_ActualThemeChanged(FrameworkElement sender, object args)
        {
            ApplyCurrentLevel();
        }

        private void Element_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            ApplyCurrentLevel();
        }

        private void Element_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            _isPressed = false;
            ApplyCurrentLevel();
        }

        private void Element_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPressed = true;
            ApplyCurrentLevel();
        }

        private void Element_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPressed = false;
            ApplyCurrentLevel();
        }

        private void Element_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPressed = false;
            ApplyCurrentLevel();
        }

        private void ApplyCurrentLevel()
        {
            var baseLevel = GetLevel(_element);
            var hoverLevel = GetHoverLevel(_element);
            var effectiveLevel = (_isPointerOver || _isPressed) && hoverLevel > baseLevel
                ? hoverLevel
                : baseLevel;

            ApplyElevation(_element, effectiveLevel);
        }
    }
}

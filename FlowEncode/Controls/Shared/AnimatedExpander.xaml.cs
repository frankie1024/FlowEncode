using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace FlowEncode.Controls.Shared;

public sealed partial class AnimatedExpander : UserControl
{
    private static readonly Duration ExpandCollapseDuration = new(TimeSpan.FromMilliseconds(220));
    private readonly UISettings _uiSettings = new();
    private Storyboard? _currentStoryboard;
    private RotateTransform? _chevronRotateTransform;
    private RectangleGeometry? _contentClipGeometry;
    private bool _isLoaded;

    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent),
        typeof(object),
        typeof(AnimatedExpander),
        new PropertyMetadata(null));

    public static readonly DependencyProperty BodyContentProperty = DependencyProperty.Register(
        nameof(BodyContent),
        typeof(object),
        typeof(AnimatedExpander),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(AnimatedExpander),
        new PropertyMetadata(false, OnIsExpandedChanged));

    public static readonly DependencyProperty HeaderPaddingProperty = DependencyProperty.Register(
        nameof(HeaderPadding),
        typeof(Thickness),
        typeof(AnimatedExpander),
        new PropertyMetadata(new Thickness(12, 10, 8, 10)));

    public static readonly DependencyProperty ContentMarginProperty = DependencyProperty.Register(
        nameof(ContentMargin),
        typeof(Thickness),
        typeof(AnimatedExpander),
        new PropertyMetadata(new Thickness(0, 8, 0, 0)));

    public AnimatedExpander()
    {
        InitializeComponent();
        Loaded += AnimatedExpander_Loaded;
        Unloaded += AnimatedExpander_Unloaded;
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public object? BodyContent
    {
        get => GetValue(BodyContentProperty);
        set => SetValue(BodyContentProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public Thickness HeaderPadding
    {
        get => (Thickness)GetValue(HeaderPaddingProperty);
        set => SetValue(HeaderPaddingProperty, value);
    }

    public Thickness ContentMargin
    {
        get => (Thickness)GetValue(ContentMarginProperty);
        set => SetValue(ContentMarginProperty, value);
    }

    public event RoutedEventHandler? Expanded;

    public event RoutedEventHandler? Collapsed;

    public event RoutedEventHandler? ExpandedCompleted;

    public event RoutedEventHandler? CollapsedCompleted;

    private static void OnIsExpandedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var expander = (AnimatedExpander)dependencyObject;
        expander.HandleIsExpandedChanged((bool)args.OldValue, (bool)args.NewValue);
    }

    private void AnimatedExpander_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        EnsureVisualHelpers();
        ApplyImmediateState(IsExpanded);
    }

    private void AnimatedExpander_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopCurrentAnimation();
    }

    private void ChevronButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleExpansion();
    }

    private void HeaderBorder_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!ShouldToggleForOriginalSource(e.OriginalSource))
        {
            return;
        }

        e.Handled = true;
        ToggleExpansion();
    }

    private void ContentClipHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureContentClipGeometry();
        UpdateContentClip();
    }

    private void HandleIsExpandedChanged(bool previousValue, bool currentValue)
    {
        if (previousValue == currentValue)
        {
            return;
        }

        if (!_isLoaded)
        {
            return;
        }

        if (currentValue)
        {
            Expanded?.Invoke(this, new RoutedEventArgs());
        }
        else
        {
            Collapsed?.Invoke(this, new RoutedEventArgs());
        }

        if (ShouldAnimate())
        {
            StartAnimation(currentValue);
            return;
        }

        ApplyImmediateState(currentValue);

        if (currentValue)
        {
            ExpandedCompleted?.Invoke(this, new RoutedEventArgs());
        }
        else
        {
            CollapsedCompleted?.Invoke(this, new RoutedEventArgs());
        }
    }

    private void ToggleExpansion()
    {
        IsExpanded = !IsExpanded;
    }

    private bool ShouldToggleForOriginalSource(object? originalSource)
    {
        if (originalSource is not DependencyObject dependencyObject)
        {
            return true;
        }

        DependencyObject? current = dependencyObject;
        while (current is not null)
        {
            if (ReferenceEquals(current, ChevronButton))
            {
                return false;
            }

            if (current is ButtonBase
                or HyperlinkButton
                or TextBox
                or PasswordBox
                or ComboBox
                or ToggleSwitch
                or NumberBox)
            {
                return false;
            }

            if (ReferenceEquals(current, HeaderBorder))
            {
                break;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    private bool ShouldAnimate()
    {
        return _uiSettings.AnimationsEnabled;
    }

    private void ApplyImmediateState(bool isExpanded)
    {
        EnsureVisualHelpers();
        StopCurrentAnimation();

        ContentBorder.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
        ContentClipHost.Height = isExpanded ? double.NaN : 0;
        UpdateHeaderCornerRadius(isExpanded);
        HeaderBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _chevronRotateTransform!.Angle = isExpanded ? 180 : 0;
        UpdateContentClip();
    }

    private void StartAnimation(bool expand)
    {
        EnsureVisualHelpers();
        StopCurrentAnimation();

        var fromHeight = Math.Max(0, ContentClipHost.ActualHeight);
        if (expand)
        {
            ContentBorder.Visibility = Visibility.Visible;
            ContentClipHost.Height = 0;
            UpdateLayout();
        }

        var targetHeight = expand ? MeasureExpandedHeight() : fromHeight;
        if (expand && targetHeight <= 0)
        {
            ApplyImmediateState(true);
            ExpandedCompleted?.Invoke(this, new RoutedEventArgs());
            return;
        }

        if (!expand && fromHeight <= 0)
        {
            ApplyImmediateState(false);
            CollapsedCompleted?.Invoke(this, new RoutedEventArgs());
            return;
        }

        var animationStartHeight = expand ? 0 : fromHeight;
        var animationEndHeight = expand ? targetHeight : 0;
        var startAngle = _chevronRotateTransform!.Angle;
        var endAngle = expand ? 180 : 0;

        ContentClipHost.Height = animationStartHeight;
        UpdateHeaderCornerRadius(expand);
        UpdateContentClip();

        var storyboard = BuildStoryboard(animationStartHeight, animationEndHeight, startAngle, endAngle);
        storyboard.Completed += (_, _) =>
        {
            if (!expand)
            {
                ContentBorder.Visibility = Visibility.Collapsed;
                ContentClipHost.Height = 0;
            }
            else
            {
                ContentClipHost.Height = double.NaN;
            }

            UpdateHeaderCornerRadius(expand);
            UpdateContentClip();
            _currentStoryboard = null;

            if (expand)
            {
                ExpandedCompleted?.Invoke(this, new RoutedEventArgs());
            }
            else
            {
                CollapsedCompleted?.Invoke(this, new RoutedEventArgs());
            }
        };

        _currentStoryboard = storyboard;
        storyboard.Begin();
    }

    private double MeasureExpandedHeight()
    {
        ContentBorder.Visibility = Visibility.Visible;
        ContentClipHost.Height = double.NaN;
        UpdateLayout();

        var availableWidth = ContentClipHost.ActualWidth;
        var measureWidth = availableWidth > 0 ? availableWidth : double.PositiveInfinity;
        ContentInnerRoot.Measure(new Size(measureWidth, double.PositiveInfinity));
        var desiredHeight = ContentInnerRoot.DesiredSize.Height;
        return desiredHeight > 0 ? desiredHeight : ContentInnerRoot.ActualHeight;
    }

    private Storyboard BuildStoryboard(double fromHeight, double toHeight, double fromAngle, double toAngle)
    {
        var easing = new CubicEase
        {
            EasingMode = EasingMode.EaseInOut
        };

        var heightAnimation = new DoubleAnimation
        {
            From = fromHeight,
            To = toHeight,
            Duration = ExpandCollapseDuration,
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(heightAnimation, ContentClipHost);
        Storyboard.SetTargetProperty(heightAnimation, "Height");

        var angleAnimation = new DoubleAnimation
        {
            From = fromAngle,
            To = toAngle,
            Duration = ExpandCollapseDuration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(angleAnimation, _chevronRotateTransform);
        Storyboard.SetTargetProperty(angleAnimation, "Angle");

        var storyboard = new Storyboard();
        storyboard.Children.Add(heightAnimation);
        storyboard.Children.Add(angleAnimation);
        return storyboard;
    }

    private void StopCurrentAnimation()
    {
        if (_currentStoryboard is null)
        {
            return;
        }

        var currentHeight = ContentClipHost.ActualHeight;
        _currentStoryboard.Stop();
        _currentStoryboard = null;

        if (currentHeight > 0)
        {
            ContentClipHost.Height = currentHeight;
        }
    }

    private void EnsureVisualHelpers()
    {
        if (ChevronIcon.RenderTransform is RotateTransform chevronRotateTransform)
        {
            _chevronRotateTransform = chevronRotateTransform;
        }
        else
        {
            _chevronRotateTransform = new RotateTransform();
            ChevronIcon.RenderTransformOrigin = new Point(0.5, 0.5);
            ChevronIcon.RenderTransform = _chevronRotateTransform;
        }

        EnsureContentClipGeometry();
    }

    private void EnsureContentClipGeometry()
    {
        _contentClipGeometry ??= new RectangleGeometry();
        ContentClipHost.Clip = _contentClipGeometry;
    }

    private void UpdateContentClip()
    {
        if (_contentClipGeometry is null)
        {
            return;
        }

        _contentClipGeometry.Rect = new Rect(0, 0, Math.Max(0, ContentClipHost.ActualWidth), Math.Max(0, ContentClipHost.ActualHeight));
    }

    private void UpdateHeaderCornerRadius(bool isExpanded)
    {
        HeaderBorder.CornerRadius = isExpanded
            ? new CornerRadius(6, 6, 0, 0)
            : new CornerRadius(6);
    }
}

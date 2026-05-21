using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace FlowEncode.Controls.Shared;

public static class ExpanderLayoutAnimation
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ExpanderLayoutAnimation),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller",
        typeof(ExpanderAnimationController),
        typeof(ExpanderLayoutAnimation),
        new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject obj, bool value)
    {
        obj.SetValue(IsEnabledProperty, value);
    }

    private static ExpanderAnimationController? GetController(DependencyObject obj)
    {
        return (ExpanderAnimationController?)obj.GetValue(ControllerProperty);
    }

    private static void SetController(DependencyObject obj, ExpanderAnimationController? value)
    {
        obj.SetValue(ControllerProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not Expander expander)
        {
            return;
        }

        if (args.NewValue is true)
        {
            if (GetController(expander) is null)
            {
                SetController(expander, new ExpanderAnimationController(expander));
            }

            return;
        }

        var controller = GetController(expander);
        if (controller is null)
        {
            return;
        }

        controller.Dispose();
        SetController(expander, null);
    }

    private sealed class ExpanderAnimationController : IDisposable
    {
        private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(333);
        private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(167);

        private readonly Expander _expander;
        private Storyboard? _storyboard;
        private Border? _contentClip;
        private Border? _contentBorder;
        private bool _isLoaded;
        private bool _lastIsExpanded;
        private bool _animationTargetExpanded;
        private long _isExpandedPropertyToken;
        private bool _isDisposed;

        public ExpanderAnimationController(Expander expander)
        {
            _expander = expander;
            _lastIsExpanded = expander.IsExpanded;
            _expander.Loaded += Expander_Loaded;
            _expander.Unloaded += Expander_Unloaded;
            _isExpandedPropertyToken = _expander.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, Expander_IsExpandedChanged);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            StopAnimation();
            _expander.Loaded -= Expander_Loaded;
            _expander.Unloaded -= Expander_Unloaded;

            if (_isExpandedPropertyToken != 0)
            {
                _expander.UnregisterPropertyChangedCallback(Expander.IsExpandedProperty, _isExpandedPropertyToken);
                _isExpandedPropertyToken = 0;
            }

            _contentClip = null;
            _contentBorder = null;
        }

        private void Expander_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            SyncCurrentState();
        }

        private void Expander_Unloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            StopAnimation();
            _contentClip = null;
            _contentBorder = null;
        }

        private void Expander_IsExpandedChanged(DependencyObject sender, DependencyProperty dependencyProperty)
        {
            if (_isDisposed)
            {
                return;
            }

            var isExpanded = _expander.IsExpanded;
            if (isExpanded == _lastIsExpanded)
            {
                return;
            }

            _lastIsExpanded = isExpanded;
            if (!_isLoaded || !TryResolveTemplateParts())
            {
                return;
            }

            if (isExpanded)
            {
                StartExpandAnimation();
                return;
            }

            StartCollapseAnimation();
        }

        private void SyncCurrentState()
        {
            if (!TryResolveTemplateParts())
            {
                return;
            }

            StopAnimation();
            _lastIsExpanded = _expander.IsExpanded;
            _contentClip!.Height = double.NaN;
            _contentBorder!.Visibility = _expander.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool TryResolveTemplateParts()
        {
            _expander.ApplyTemplate();
            _contentClip = FindDescendantByName<Border>(_expander, "ExpanderContentClip");
            _contentBorder = FindDescendantByName<Border>(_expander, "ExpanderContent");
            return _contentClip is not null && _contentBorder is not null;
        }

        private void StartExpandAnimation()
        {
            StopAnimation();

            if (_contentClip is null || _contentBorder is null)
            {
                return;
            }

            var startHeight = ResolveCurrentClipHeight();
            _contentClip.Height = startHeight;
            _contentBorder.Visibility = Visibility.Visible;
            var targetHeight = MeasureExpandedHeight();
            if (targetHeight <= 0)
            {
                _contentClip.Height = double.NaN;
                return;
            }

            BeginHeightAnimation(startHeight, targetHeight, ExpandDuration, EasingMode.EaseOut, isExpanding: true);
        }

        private void StartCollapseAnimation()
        {
            StopAnimation();

            if (_contentClip is null || _contentBorder is null)
            {
                return;
            }

            var startHeight = ResolveCurrentClipHeight();
            if (startHeight <= 0)
            {
                _contentClip.Height = double.NaN;
                _contentBorder.Visibility = Visibility.Collapsed;
                return;
            }

            _contentBorder.Visibility = Visibility.Visible;
            _contentClip.Height = startHeight;
            BeginHeightAnimation(startHeight, 0, CollapseDuration, EasingMode.EaseIn, isExpanding: false);
        }

        private void BeginHeightAnimation(double from, double to, TimeSpan duration, EasingMode easingMode, bool isExpanding)
        {
            if (_contentClip is null)
            {
                return;
            }

            if (Math.Abs(from - to) < 0.5)
            {
                CompleteAnimation(isExpanding);
                return;
            }

            _animationTargetExpanded = isExpanding;

            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(duration),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase
                {
                    EasingMode = easingMode
                }
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, _contentClip);
            Storyboard.SetTargetProperty(animation, nameof(FrameworkElement.Height));
            storyboard.Children.Add(animation);
            storyboard.Completed += Storyboard_Completed;
            _storyboard = storyboard;
            storyboard.Begin();
        }

        private void Storyboard_Completed(object? sender, object e)
        {
            CompleteAnimation(_animationTargetExpanded);
        }

        private void CompleteAnimation(bool isExpanding)
        {
            StopAnimation();

            if (_contentClip is null || _contentBorder is null)
            {
                return;
            }

            _contentClip.Height = double.NaN;
            _contentBorder.Visibility = isExpanding || _expander.IsExpanded
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void StopAnimation()
        {
            if (_storyboard is null)
            {
                return;
            }

            _storyboard.Completed -= Storyboard_Completed;
            _storyboard.Stop();
            _storyboard = null;
        }

        private double ResolveCurrentClipHeight()
        {
            if (_contentClip is null)
            {
                return 0;
            }

            if (_contentClip.ActualHeight > 0)
            {
                return Math.Ceiling(_contentClip.ActualHeight);
            }

            return !double.IsNaN(_contentClip.Height) && _contentClip.Height > 0
                ? Math.Ceiling(_contentClip.Height)
                : 0;
        }

        private double MeasureExpandedHeight()
        {
            if (_contentBorder is null)
            {
                return 0;
            }

            var availableWidth = _contentClip?.ActualWidth > 0
                ? _contentClip.ActualWidth
                : _expander.ActualWidth > 0
                    ? _expander.ActualWidth
                    : double.PositiveInfinity;

            _contentBorder.Measure(new Size(availableWidth, double.PositiveInfinity));
            var targetHeight = Math.Max(_contentBorder.DesiredSize.Height, _contentBorder.ActualHeight);
            return targetHeight > 0 ? Math.Ceiling(targetHeight) : 0;
        }

        private static TElement? FindDescendantByName<TElement>(DependencyObject root, string name)
            where TElement : FrameworkElement
        {
            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < childCount; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is TElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
                {
                    return element;
                }

                var descendant = FindDescendantByName<TElement>(child, name);
                if (descendant is not null)
                {
                    return descendant;
                }
            }

            return null;
        }
    }
}

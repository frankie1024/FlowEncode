using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace FlowEncode.Controls.Shared;

public static class UiMotion
{
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, Storyboard>> ActiveAnimations = new();
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, SmoothAnimationState>> SmoothAnimationStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, HoverLiftState> HoverLiftStates = new();
    private static readonly ConditionalWeakTable<FrameworkElement, VisibilityState> VisibilityStates = new();
    private static readonly ConditionalWeakTable<ListViewBase, ListEntranceState> ListEntranceStates = new();

    public static readonly DependencyProperty HoverLiftEnabledProperty = DependencyProperty.RegisterAttached(
        "HoverLiftEnabled",
        typeof(bool),
        typeof(UiMotion),
        new PropertyMetadata(false, OnHoverLiftEnabledChanged));

    public static readonly DependencyProperty AnimateOnVisibleProperty = DependencyProperty.RegisterAttached(
        "AnimateOnVisible",
        typeof(bool),
        typeof(UiMotion),
        new PropertyMetadata(false, OnAnimateOnVisibleChanged));

    public static readonly DependencyProperty SmoothProgressValueProperty = DependencyProperty.RegisterAttached(
        "SmoothProgressValue",
        typeof(double),
        typeof(UiMotion),
        new PropertyMetadata(0d, OnSmoothProgressValueChanged));

    public static readonly DependencyProperty SmoothScaleXProperty = DependencyProperty.RegisterAttached(
        "SmoothScaleX",
        typeof(double),
        typeof(UiMotion),
        new PropertyMetadata(0d, OnSmoothScaleXChanged));

    public static readonly DependencyProperty AnimateListEntranceProperty = DependencyProperty.RegisterAttached(
        "AnimateListEntrance",
        typeof(bool),
        typeof(UiMotion),
        new PropertyMetadata(false, OnAnimateListEntranceChanged));

    public static bool GetHoverLiftEnabled(DependencyObject obj) => (bool)obj.GetValue(HoverLiftEnabledProperty);
    public static void SetHoverLiftEnabled(DependencyObject obj, bool value) => obj.SetValue(HoverLiftEnabledProperty, value);

    public static bool GetAnimateOnVisible(DependencyObject obj) => (bool)obj.GetValue(AnimateOnVisibleProperty);
    public static void SetAnimateOnVisible(DependencyObject obj, bool value) => obj.SetValue(AnimateOnVisibleProperty, value);

    public static double GetSmoothProgressValue(DependencyObject obj) => (double)obj.GetValue(SmoothProgressValueProperty);
    public static void SetSmoothProgressValue(DependencyObject obj, double value) => obj.SetValue(SmoothProgressValueProperty, value);

    public static double GetSmoothScaleX(DependencyObject obj) => (double)obj.GetValue(SmoothScaleXProperty);
    public static void SetSmoothScaleX(DependencyObject obj, double value) => obj.SetValue(SmoothScaleXProperty, value);

    public static bool GetAnimateListEntrance(DependencyObject obj) => (bool)obj.GetValue(AnimateListEntranceProperty);
    public static void SetAnimateListEntrance(DependencyObject obj, bool value) => obj.SetValue(AnimateListEntranceProperty, value);

    private static bool AnimationsEnabled => UiMotionPolicy.AreCustomAnimationsEnabled();

    public static void PlayEntrance(FrameworkElement element, double offsetY)
    {
        if (!AnimationsEnabled)
        {
            ResetEntranceState(element);
            return;
        }

        var transform = EnsureCompositeTransform(element);
        transform.TranslateY = offsetY;
        element.Opacity = 0;
        AnimateDouble(transform, nameof(CompositeTransform.TranslateY), transform.TranslateY, 0, UiTokens.MotionNormalDuration, UiTokens.MotionEasingEnter, false);
        AnimateDouble(element, nameof(UIElement.Opacity), element.Opacity, 1, UiTokens.MotionNormalDuration, UiTokens.MotionEasingEnter, false);
    }

    public static void PlayHorizontalEntrance(FrameworkElement element, double offsetX)
    {
        if (!AnimationsEnabled)
        {
            ResetEntranceState(element);
            return;
        }

        var transform = EnsureCompositeTransform(element);
        transform.TranslateX = offsetX;
        element.Opacity = 0;
        AnimateDouble(transform, nameof(CompositeTransform.TranslateX), transform.TranslateX, 0, UiTokens.MotionEmphasisDuration, UiTokens.MotionEasingEnter, false);
        AnimateDouble(element, nameof(UIElement.Opacity), element.Opacity, 1, UiTokens.MotionEmphasisDuration, UiTokens.MotionEasingEnter, false);
    }

    public static bool PlayExit(FrameworkElement element, double offsetY)
    {
        if (!AnimationsEnabled)
        {
            ResetEntranceState(element);
            return false;
        }

        var transform = EnsureCompositeTransform(element);
        AnimateDouble(transform, nameof(CompositeTransform.TranslateY), transform.TranslateY, offsetY, UiTokens.MotionFastDuration, UiTokens.MotionEasingInOut, false);
        AnimateDouble(element, nameof(UIElement.Opacity), element.Opacity, 0, UiTokens.MotionFastDuration, UiTokens.MotionEasingInOut, false);
        return true;
    }

    private static void OnHoverLiftEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            HoverLiftStates.GetValue(element, CreateHoverLiftState);
        }
        else if (HoverLiftStates.TryGetValue(element, out var state))
        {
            state.Detach();
            HoverLiftStates.Remove(element);
        }
    }

    private static HoverLiftState CreateHoverLiftState(FrameworkElement element)
    {
        return new HoverLiftState(element);
    }

    private static void OnAnimateOnVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            VisibilityStates.GetValue(element, CreateVisibilityState);
        }
        else if (VisibilityStates.TryGetValue(element, out var state))
        {
            state.Detach();
            VisibilityStates.Remove(element);
        }
    }

    private static VisibilityState CreateVisibilityState(FrameworkElement element)
    {
        return new VisibilityState(element);
    }

    private static void OnSmoothProgressValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProgressBar progressBar)
        {
            return;
        }

        var targetValue = CoerceProgressValue(progressBar, (double)e.NewValue);
        if (!AnimationsEnabled || progressBar.IsIndeterminate)
        {
            StopSmoothAnimation(progressBar, nameof(RangeBase.Value));
            StopAnimation(progressBar, GetStoryboardKey(progressBar, nameof(RangeBase.Value)));
            progressBar.Value = targetValue;
            return;
        }

        QueueSmoothAnimation(
            progressBar,
            nameof(RangeBase.Value),
            () => progressBar.Value,
            targetValue,
            true);
    }

    private static void OnSmoothScaleXChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScaleTransform transform)
        {
            return;
        }

        var targetValue = Math.Clamp((double)e.NewValue, 0, 1);
        if (!AnimationsEnabled)
        {
            StopSmoothAnimation(transform, nameof(ScaleTransform.ScaleX));
            StopAnimation(transform, GetStoryboardKey(transform, nameof(ScaleTransform.ScaleX)));
            transform.ScaleX = targetValue;
            return;
        }

        QueueSmoothAnimation(
            transform,
            nameof(ScaleTransform.ScaleX),
            () => transform.ScaleX,
            targetValue,
            false);
    }

    private static void OnAnimateListEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListViewBase listView)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            ListEntranceStates.GetValue(listView, CreateListEntranceState);
        }
        else if (ListEntranceStates.TryGetValue(listView, out var state))
        {
            state.Detach();
            ListEntranceStates.Remove(listView);
        }
    }

    private static ListEntranceState CreateListEntranceState(ListViewBase listView)
    {
        return new ListEntranceState(listView);
    }

    private static void ResetEntranceState(FrameworkElement element)
    {
        StopAnimation(element, GetStoryboardKey(element, nameof(UIElement.Opacity)));
        element.Opacity = 1;
        var transform = EnsureCompositeTransform(element);
        StopAnimation(transform, GetStoryboardKey(transform, nameof(CompositeTransform.TranslateX)));
        StopAnimation(transform, GetStoryboardKey(transform, nameof(CompositeTransform.TranslateY)));
        transform.TranslateX = 0;
        transform.TranslateY = 0;
    }

    private static CompositeTransform EnsureCompositeTransform(FrameworkElement element)
    {
        if (element.RenderTransform is CompositeTransform transform)
        {
            return transform;
        }

        if (element.RenderTransform is null || element.RenderTransform is MatrixTransform)
        {
            transform = new CompositeTransform();
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            element.RenderTransform = transform;
            return transform;
        }

        transform = new CompositeTransform();
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        element.RenderTransform = transform;
        return transform;
    }

    private static double CoerceProgressValue(ProgressBar progressBar, double value)
    {
        var minimum = progressBar.Minimum;
        var maximum = progressBar.Maximum;
        if (maximum <= minimum)
        {
            return minimum;
        }

        return Math.Clamp(value, minimum, maximum);
    }

    private static void AnimateDouble(
        DependencyObject target,
        string propertyName,
        double from,
        double to,
        Duration duration,
        EasingFunctionBase easing,
        bool enableDependentAnimation)
    {
        var storyboardKey = GetStoryboardKey(target, propertyName);
        StopAnimation(target, storyboardKey);

        if (Math.Abs(from - to) < 0.0001)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = enableDependentAnimation
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, propertyName);

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => RemoveAnimation(target, storyboardKey, storyboard);

        StoreAnimation(target, storyboardKey, storyboard);
        storyboard.Begin();
    }

    private static void StoreAnimation(DependencyObject target, string key, Storyboard storyboard)
    {
        var storyboards = ActiveAnimations.GetOrCreateValue(target);
        storyboards[key] = storyboard;
    }

    private static void StopAnimation(DependencyObject target, string key)
    {
        if (!ActiveAnimations.TryGetValue(target, out var storyboards)
            || !storyboards.TryGetValue(key, out var storyboard))
        {
            return;
        }

        storyboard.Stop();
        storyboards.Remove(key);
    }

    private static string GetStoryboardKey(DependencyObject target, string propertyName)
    {
        return $"{target.GetType().FullName}:{propertyName}";
    }

    private static void QueueSmoothAnimation(
        DependencyObject target,
        string propertyName,
        Func<double> currentValue,
        double targetValue,
        bool enableDependentAnimation)
    {
        var states = SmoothAnimationStates.GetOrCreateValue(target);
        if (!states.TryGetValue(propertyName, out var state))
        {
            state = new SmoothAnimationState(target, propertyName);
            states[propertyName] = state;
        }

        state.Queue(currentValue, targetValue, enableDependentAnimation);
    }

    private static void StopSmoothAnimation(DependencyObject target, string propertyName)
    {
        if (!SmoothAnimationStates.TryGetValue(target, out var states)
            || !states.Remove(propertyName, out var state))
        {
            return;
        }

        state.Cancel();
    }

    private static void RemoveAnimation(DependencyObject target, string key, Storyboard storyboard)
    {
        if (!ActiveAnimations.TryGetValue(target, out var storyboards)
            || !storyboards.TryGetValue(key, out var current)
            || !ReferenceEquals(current, storyboard))
        {
            return;
        }

        storyboards.Remove(key);
    }

    private sealed class SmoothAnimationState
    {
        private readonly DependencyObject _target;
        private readonly string _propertyName;
        private DispatcherQueueTimer? _timer;
        private PendingAnimation? _pendingAnimation;
        private long _lastAnimationStart;

        public SmoothAnimationState(DependencyObject target, string propertyName)
        {
            _target = target;
            _propertyName = propertyName;
        }

        public void Queue(Func<double> currentValue, double targetValue, bool enableDependentAnimation)
        {
            _pendingAnimation = new PendingAnimation(currentValue, targetValue, enableDependentAnimation);
            var elapsed = Environment.TickCount64 - _lastAnimationStart;
            if (_lastAnimationStart == 0 || elapsed >= UiTokens.MotionInstant)
            {
                FlushLatest();
                return;
            }

            var remaining = Math.Max(1, UiTokens.MotionInstant - elapsed);
            EnsureTimer(TimeSpan.FromMilliseconds(remaining));
        }

        public void Cancel()
        {
            _pendingAnimation = null;
            _lastAnimationStart = 0;
            if (_timer is null)
            {
                return;
            }

            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _timer = null;
        }

        private void EnsureTimer(TimeSpan interval)
        {
            _timer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = interval;
            _timer.Tick -= Timer_Tick;
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            FlushLatest();
        }

        private void FlushLatest()
        {
            if (_pendingAnimation is not { } pending)
            {
                return;
            }

            _pendingAnimation = null;
            _timer?.Stop();
            if (!AnimationsEnabled)
            {
                return;
            }

            _lastAnimationStart = Environment.TickCount64;
            AnimateDouble(
                _target,
                _propertyName,
                pending.CurrentValue(),
                pending.TargetValue,
                new Duration(TimeSpan.FromMilliseconds(UiTokens.MotionProgressSmoothMilliseconds)),
                UiTokens.MotionEasingInOut,
                pending.EnableDependentAnimation);
        }

        private readonly record struct PendingAnimation(
            Func<double> CurrentValue,
            double TargetValue,
            bool EnableDependentAnimation);
    }

    private sealed class HoverLiftState
    {
        private readonly FrameworkElement _element;
        private bool _isPointerOver;

        public HoverLiftState(FrameworkElement element)
        {
            _element = element;
            _element.PointerEntered += Element_PointerEntered;
            _element.PointerExited += Element_PointerExited;
            _element.PointerPressed += Element_PointerPressed;
            _element.PointerReleased += Element_PointerReleased;
            _element.Unloaded += Element_Unloaded;
        }

        public void Detach()
        {
            _element.PointerEntered -= Element_PointerEntered;
            _element.PointerExited -= Element_PointerExited;
            _element.PointerPressed -= Element_PointerPressed;
            _element.PointerReleased -= Element_PointerReleased;
            _element.Unloaded -= Element_Unloaded;
        }

        private void Element_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            ApplyHoverOffset(-UiTokens.MotionHoverLiftY);
        }

        private void Element_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            ApplyHoverOffset(0);
        }

        private void Element_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ApplyHoverOffset(0);
        }

        private void Element_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            ApplyHoverOffset(_isPointerOver ? -UiTokens.MotionHoverLiftY : 0);
        }

        private void Element_Unloaded(object sender, RoutedEventArgs e)
        {
            _isPointerOver = false;
            ApplyHoverOffset(0);
        }

        private void ApplyHoverOffset(double offset)
        {
            var transform = EnsureCompositeTransform(_element);
            if (!AnimationsEnabled)
            {
                transform.TranslateY = offset;
                return;
            }

            AnimateDouble(transform, nameof(CompositeTransform.TranslateY), transform.TranslateY, offset, UiTokens.MotionFastDuration, UiTokens.MotionEasingInOut, false);
        }
    }

    private sealed class VisibilityState
    {
        private readonly FrameworkElement _element;
        private readonly long _visibilityToken;

        public VisibilityState(FrameworkElement element)
        {
            _element = element;
            _visibilityToken = _element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, OnVisibilityChanged);
            _element.Unloaded += Element_Unloaded;
        }

        public void Detach()
        {
            _element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _visibilityToken);
            _element.Unloaded -= Element_Unloaded;
        }

        private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
        {
            if (_element.Visibility == Visibility.Visible)
            {
                PlayEntrance(_element, UiTokens.MotionFieldOffsetY);
            }
            else
            {
                ResetEntranceState(_element);
            }
        }

        private void Element_Unloaded(object sender, RoutedEventArgs e)
        {
            ResetEntranceState(_element);
        }
    }

    private sealed class ListEntranceState
    {
        private readonly ListViewBase _listView;
        private ConditionalWeakTable<FrameworkElement, ItemMarker> _seenContainers = new();

        public ListEntranceState(ListViewBase listView)
        {
            _listView = listView;
            _listView.ContainerContentChanging += ListView_ContainerContentChanging;
        }

        public void Detach()
        {
            _listView.ContainerContentChanging -= ListView_ContainerContentChanging;
            _seenContainers = new ConditionalWeakTable<FrameworkElement, ItemMarker>();
        }

        private void ListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue || args.ItemContainer is not FrameworkElement container || args.Item is null)
            {
                return;
            }

            var marker = _seenContainers.GetValue(container, static _ => new ItemMarker());
            if (ReferenceEquals(marker.Item, args.Item))
            {
                return;
            }

            marker.Item = args.Item;

            PlayEntrance(container, UiTokens.MotionListInsertOffsetY);
        }

        private sealed class ItemMarker
        {
            public object? Item { get; set; }
        }
    }
}

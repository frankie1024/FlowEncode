using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace FlowEncode.Controls.Shared;

public static class ProgressEffects
{
    private static readonly ConditionalWeakTable<FrameworkElement, ProgressLayer> Layers = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ProgressEffects), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty ParticleProgressProperty = DependencyProperty.RegisterAttached(
        "ParticleProgress", typeof(double), typeof(ProgressEffects), new PropertyMetadata(0d, OnParticleProgressChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetParticleProgress(DependencyObject element) => (double)element.GetValue(ParticleProgressProperty);
    public static void SetParticleProgress(DependencyObject element, double value) => element.SetValue(ParticleProgressProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element)
        {
            Layers.GetValue(element, static target => new ProgressLayer(target)).Update();
        }
    }

    private static void OnParticleProgressChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element && Layers.TryGetValue(element, out var layer))
        {
            layer.Update();
        }
    }

    private sealed class ProgressLayer
    {
        private const float MaxParticleSpeedIncrease = 0.20f;
        private readonly FrameworkElement _element;
        private readonly ProgressBar? _progressBar;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
        private ContainerVisual? _layer;
        private List<AnimationController>? _particleAnimations;
        private float _particlePlaybackRate = 1f;

        public ProgressLayer(FrameworkElement element)
        {
            _element = element;
            _progressBar = element as ProgressBar;
            _dispatcherQueue = element.DispatcherQueue;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            element.SizeChanged += (_, _) => Update();
            element.ActualThemeChanged += (_, _) => Update();
            element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Update());

            if (_progressBar is not null)
            {
                _progressBar.ValueChanged += (_, _) => Update();
                _progressBar.RegisterPropertyChangedCallback(ProgressBar.IsIndeterminateProperty, (_, _) => Update());
            }

            if (element.IsLoaded)
            {
                UiMotionPolicy.SettingsChanged += OnMotionSettingsChanged;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            UiMotionPolicy.SettingsChanged -= OnMotionSettingsChanged;
            UiMotionPolicy.SettingsChanged += OnMotionSettingsChanged;
            Update();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            UiMotionPolicy.SettingsChanged -= OnMotionSettingsChanged;
            Clear();
        }

        private void OnMotionSettingsChanged(object? sender, EventArgs args)
        {
            _dispatcherQueue.TryEnqueue(Update);
        }

        public void Update()
        {
            var shimmer = _progressBar is not null;
            var progress = GetProgress();
            var width = (float)_element.ActualWidth;
            var height = (float)_element.ActualHeight;
            var inset = shimmer ? 0f : 3f;
            var effectWidth = width * progress - 2 * inset;
            var effectHeight = height - 2 * inset;
            if (!GetIsEnabled(_element) || !_element.IsLoaded || _element.Visibility != Visibility.Visible || !(effectWidth > 0 && effectHeight > 0)
                || !UiMotionPolicy.AreCustomAnimationsEnabled())
            {
                Clear();
                return;
            }

            _layer ??= CreateLayer(shimmer);
            _layer.Offset = new Vector3(inset, inset, 0);
            _layer.Size = new Vector2(effectWidth, effectHeight);
            UpdateParticlePlaybackRate(progress);
        }

        private void UpdateParticlePlaybackRate(float progress)
        {
            var playbackRate = 1f + MaxParticleSpeedIncrease * progress;
            if (_particleAnimations is null || playbackRate == _particlePlaybackRate)
            {
                return;
            }

            foreach (var animation in _particleAnimations)
            {
                animation.PlaybackRate = playbackRate;
            }

            _particlePlaybackRate = playbackRate;
        }

        private float GetProgress()
        {
            if (_progressBar is null)
            {
                return (float)Math.Clamp(GetParticleProgress(_element), 0d, 1d);
            }

            var range = _progressBar.Maximum - _progressBar.Minimum;
            return !_progressBar.IsIndeterminate && range > 0
                ? (float)Math.Clamp((_progressBar.Value - _progressBar.Minimum) / range, 0d, 1d)
                : 0f;
        }

        private ContainerVisual CreateLayer(bool shimmer)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(_element).Compositor;
            var layer = compositor.CreateContainerVisual();
            layer.Clip = compositor.CreateInsetClip();
            if (shimmer)
            {
                AddShimmer(layer, compositor);
            }
            else
            {
                AddParticles(layer, compositor);
            }

            ElementCompositionPreview.SetElementChildVisual(_element, layer);
            return layer;
        }

        private void Clear()
        {
            if (_layer is null)
            {
                return;
            }

            foreach (var visual in _layer.Children)
            {
                visual.StopAnimation(nameof(Visual.Offset));
                if (_progressBar is null)
                {
                    visual.StopAnimation(nameof(Visual.Opacity));
                }
            }

            _layer.Children.RemoveAll();
            ElementCompositionPreview.SetElementChildVisual(_element, null);
            _layer = null;
            _particleAnimations = null;
            _particlePlaybackRate = 1f;
        }

        private static void AddShimmer(ContainerVisual layer, Compositor compositor)
        {
            const float highlightWidth = 64f;
            var brush = compositor.CreateLinearGradientBrush();
            brush.StartPoint = new Vector2(0, 0.5f);
            brush.EndPoint = new Vector2(1, 0.5f);
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0, Colors.Transparent));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.48f, Windows.UI.Color.FromArgb(190, 255, 255, 255)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1, Colors.Transparent));

            var band = compositor.CreateSpriteVisual();
            band.Brush = brush;
            band.Size = new Vector2(highlightWidth, 0);
            band.RelativeSizeAdjustment = new Vector2(0, 1);
            band.Offset = new Vector3(-highlightWidth, 0, 0);
            layer.Children.InsertAtTop(band);

            var linear = compositor.CreateLinearEasingFunction();
            var travel = compositor.CreateVector3KeyFrameAnimation();
            travel.SetReferenceParameter("progress", layer);
            travel.InsertKeyFrame(0, band.Offset);
            travel.InsertExpressionKeyFrame(0.64f, "Vector3(progress.Size.X, 0, 0)", linear);
            travel.InsertExpressionKeyFrame(1f, "Vector3(progress.Size.X, 0, 0)", linear);
            travel.Duration = TimeSpan.FromSeconds(2.6);
            travel.IterationBehavior = AnimationIterationBehavior.Forever;
            band.StartAnimation(nameof(Visual.Offset), travel);
        }

        private void AddParticles(ContainerVisual layer, Compositor compositor)
        {
            var random = new Random(31);
            var halo = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(48, 255, 255, 255));
            var core = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(186, 255, 255, 255));
            var linear = compositor.CreateLinearEasingFunction();
            const int particleCount = 22;
            var animations = new List<AnimationController>(particleCount * 2);
            for (var index = 0; index < particleCount; index++)
            {
                var radius = 0.65f + random.NextSingle() * 0.85f;
                var particle = compositor.CreateShapeVisual();
                particle.Size = new Vector2(8, 8);
                particle.RelativeOffsetAdjustment = new Vector3(
                    0.06f + (index + random.NextSingle()) / particleCount * 0.84f,
                    0.16f + random.NextSingle() * 0.68f, 0);
                particle.Shapes.Add(CreateDot(compositor, halo, radius * 2));
                particle.Shapes.Add(CreateDot(compositor, core, radius));
                layer.Children.InsertAtTop(particle);

                var duration = TimeSpan.FromSeconds(2.5f + random.NextSingle() * 2.4f);
                var delay = TimeSpan.FromSeconds(random.NextSingle() * 1.8f);
                var drift = compositor.CreateVector3KeyFrameAnimation();
                drift.InsertKeyFrame(0, new Vector3(-9, 0, 0));
                drift.InsertKeyFrame(1, new Vector3(8 + random.NextSingle() * 16, -5 - random.NextSingle() * 8, 0), linear);
                drift.Duration = duration;
                drift.DelayTime = delay;
                drift.IterationBehavior = AnimationIterationBehavior.Forever;
                particle.StartAnimation(nameof(Visual.Offset), drift);

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0, 0);
                fade.InsertKeyFrame(0.38f, 0.56f + random.NextSingle() * 0.44f);
                fade.InsertKeyFrame(1, 0);
                fade.Duration = duration;
                fade.DelayTime = delay;
                fade.IterationBehavior = AnimationIterationBehavior.Forever;
                particle.Opacity = 0;
                particle.StartAnimation(nameof(Visual.Opacity), fade);

                if (particle.TryGetAnimationController(nameof(Visual.Offset)) is { } driftController
                    && particle.TryGetAnimationController(nameof(Visual.Opacity)) is { } fadeController)
                {
                    animations.Add(driftController);
                    animations.Add(fadeController);
                }
            }

            _particleAnimations = animations;
        }

        private static CompositionSpriteShape CreateDot(Compositor compositor, CompositionBrush brush, float radius)
        {
            var ellipse = compositor.CreateEllipseGeometry();
            ellipse.Center = new Vector2(4, 4);
            ellipse.Radius = new Vector2(radius);
            var dot = compositor.CreateSpriteShape(ellipse);
            dot.FillBrush = brush;
            return dot;
        }
    }
}

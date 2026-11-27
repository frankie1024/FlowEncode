using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas.Geometry;
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

    public static readonly DependencyProperty WaterFillTargetProperty = DependencyProperty.RegisterAttached(
        "WaterFillTarget", typeof(UIElement), typeof(ProgressEffects), new PropertyMetadata(null, OnWaterFillTargetChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetParticleProgress(DependencyObject element) => (double)element.GetValue(ParticleProgressProperty);
    public static void SetParticleProgress(DependencyObject element, double value) => element.SetValue(ParticleProgressProperty, value);

    public static UIElement? GetWaterFillTarget(DependencyObject element) => (UIElement?)element.GetValue(WaterFillTargetProperty);
    public static void SetWaterFillTarget(DependencyObject element, UIElement? value) => element.SetValue(WaterFillTargetProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            Layers.GetValue(element, static target => new ProgressLayer(target)).Update();
        }
        else if (Layers.TryGetValue(element, out var layer))
        {
            layer.Detach();
            Layers.Remove(element);
        }
    }

    private static void OnParticleProgressChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element && Layers.TryGetValue(element, out var layer))
        {
            layer.Update();
        }
    }

    private static void OnWaterFillTargetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is UIElement oldTarget)
        {
            oldTarget.Opacity = 1d;
        }

        if (sender is FrameworkElement element && Layers.TryGetValue(element, out var layer))
        {
            layer.Update();
        }
    }

    private sealed class ProgressLayer
    {
        private const float MaxBubbleSpeedIncrease = 0.20f;
        private const float CrossSectionWaveAmplitude = 4.2f;
        private const float HorizontalRipplePeriod = 240f;
        private const float WaveLeftExtent = 2600f;
        private const float RimPadX = 16f;

        private readonly FrameworkElement _element;
        private readonly ProgressBar? _progressBar;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
        private readonly long _visibilityCallbackToken;
        private readonly long _indeterminateCallbackToken;
        private ContainerVisual? _layer;
        private InsetClip? _layerClip;
        private CompositionPropertySet? _waterProps;
        private CompositionGeometricClip? _waterClip;
        private ElementTheme _layerTheme = ElementTheme.Default;
        private float _layerBuiltWidth;
        private float _layerBuiltHeight;
        private List<AnimationController>? _bubbleAnimations;
        private float _bubblePlaybackRate = 1f;
        private bool _isDetached;

        public ProgressLayer(FrameworkElement element)
        {
            _element = element;
            _progressBar = element as ProgressBar;
            _dispatcherQueue = element.DispatcherQueue;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            element.SizeChanged += OnSizeChanged;
            element.ActualThemeChanged += OnActualThemeChanged;
            _visibilityCallbackToken = element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, OnDependencyPropertyChanged);

            if (_progressBar is not null)
            {
                _progressBar.ValueChanged += OnProgressBarValueChanged;
                _indeterminateCallbackToken = _progressBar.RegisterPropertyChangedCallback(ProgressBar.IsIndeterminateProperty, OnDependencyPropertyChanged);
            }

            if (element.IsLoaded)
            {
                UiMotionPolicy.SettingsChanged += OnMotionSettingsChanged;
            }
        }

        public void Detach()
        {
            if (_isDetached)
            {
                return;
            }

            _isDetached = true;
            UiMotionPolicy.SettingsChanged -= OnMotionSettingsChanged;
            _element.Loaded -= OnLoaded;
            _element.Unloaded -= OnUnloaded;
            _element.SizeChanged -= OnSizeChanged;
            _element.ActualThemeChanged -= OnActualThemeChanged;
            _element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _visibilityCallbackToken);

            if (_progressBar is not null)
            {
                _progressBar.ValueChanged -= OnProgressBarValueChanged;
                _progressBar.UnregisterPropertyChangedCallback(ProgressBar.IsIndeterminateProperty, _indeterminateCallbackToken);
            }

            Clear();
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_isDetached)
            {
                return;
            }

            UiMotionPolicy.SettingsChanged -= OnMotionSettingsChanged;
            UiMotionPolicy.SettingsChanged += OnMotionSettingsChanged;
            Update();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            UiMotionPolicy.SettingsChanged -= OnMotionSettingsChanged;
            Clear();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Update();

        private void OnActualThemeChanged(FrameworkElement sender, object args) => Update();

        private void OnProgressBarValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args) => Update();

        private void OnDependencyPropertyChanged(DependencyObject sender, DependencyProperty dp) => Update();

        private void OnMotionSettingsChanged(object? sender, EventArgs args)
        {
            _dispatcherQueue.TryEnqueue(Update);
        }

        public void Update()
        {
            if (_isDetached)
            {
                return;
            }

            var shimmer = _progressBar is not null;
            var progress = GetProgress();
            var width = (float)_element.ActualWidth;
            var height = (float)_element.ActualHeight;
            var shimmerCompleted = shimmer && progress >= 1f;

            if (!GetIsEnabled(_element)
                || !_element.IsLoaded
                || _element.Visibility != Visibility.Visible
                || shimmerCompleted
                || !(progress > 0f && width > 0f && height > 0f)
                || !UiMotionPolicy.AreCustomAnimationsEnabled())
            {
                Clear();
                return;
            }

            var theme = _element.ActualTheme;
            var needsRebuild = _layer is not null
                && (_layerTheme != theme
                    || (!shimmer && (Math.Abs(height - _layerBuiltHeight) > 1f || width > _layerBuiltWidth + 1f)));
            if (needsRebuild)
            {
                Clear();
            }

            if (shimmer)
            {
                var visibleWidth = width * progress;
                if (!(visibleWidth > 0f))
                {
                    Clear();
                    return;
                }

                _layer ??= CreateShimmerLayer(theme);
                _layer.Offset = Vector3.Zero;
                _layer.Size = new Vector2(visibleWidth, height);
                if (_layerClip is not null)
                {
                    _layerClip.RightInset = 0f;
                }
            }
            else
            {
                _layer ??= CreateWaterLayer(width, height, theme);
                _layer.Offset = Vector3.Zero;
                _layer.Size = new Vector2(width, height);

                // Sweep the curved cross-section cleanly past the right edge once near 100%.
                var fillX = progress >= 0.995f
                    ? width + CrossSectionWaveAmplitude * 3f
                    : width * progress;
                _waterProps?.InsertScalar("FillX", fillX);
                SetWaterFillTargetHidden(true);
                UpdateBubblePlaybackRate(progress);
            }
        }

        private void UpdateBubblePlaybackRate(float progress)
        {
            var playbackRate = 1f + MaxBubbleSpeedIncrease * progress;
            if (_bubbleAnimations is null || playbackRate == _bubblePlaybackRate)
            {
                return;
            }

            foreach (var animation in _bubbleAnimations)
            {
                animation.PlaybackRate = playbackRate;
            }

            _bubblePlaybackRate = playbackRate;
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

        private void SetWaterFillTargetHidden(bool hidden)
        {
            if (GetWaterFillTarget(_element) is { } target)
            {
                target.Opacity = hidden ? 0d : 1d;
            }
        }

        private ContainerVisual CreateShimmerLayer(ElementTheme theme)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(_element).Compositor;
            var layer = compositor.CreateContainerVisual();
            var clip = compositor.CreateInsetClip();
            layer.Clip = clip;
            _layerClip = clip;
            _layerTheme = theme;
            AddShimmer(layer, compositor, theme);
            ElementCompositionPreview.SetElementChildVisual(_element, layer);
            return layer;
        }

        private ContainerVisual CreateWaterLayer(float width, float height, ElementTheme theme)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(_element).Compositor;
            var layer = compositor.CreateContainerVisual();
            var clip = compositor.CreateInsetClip();
            layer.Clip = clip;
            _layerClip = clip;
            _layerTheme = theme;
            _layerBuiltWidth = Math.Max(width, 480f);
            _layerBuiltHeight = height;

            var isLight = theme == ElementTheme.Light;
            var wavePeriodY = Math.Max(32f, height);

            // Shared CompositionPropertySet so FillX updates never interrupt the GPU wave loops.
            var props = compositor.CreatePropertySet();
            props.InsertScalar("FillX", 0f);
            props.InsertScalar("WaveY", 0f);
            props.InsertScalar("BackWaveY", 0f);
            _waterProps = props;

            var linear = compositor.CreateLinearEasingFunction();

            var frontWaveLoop = compositor.CreateScalarKeyFrameAnimation();
            frontWaveLoop.InsertKeyFrame(0f, 0f);
            frontWaveLoop.InsertKeyFrame(1f, -wavePeriodY, linear);
            frontWaveLoop.Duration = TimeSpan.FromSeconds(2.8);
            frontWaveLoop.IterationBehavior = AnimationIterationBehavior.Forever;
            props.StartAnimation("WaveY", frontWaveLoop);

            var backWaveLoop = compositor.CreateScalarKeyFrameAnimation();
            backWaveLoop.InsertKeyFrame(0f, 0f);
            backWaveLoop.InsertKeyFrame(1f, -wavePeriodY, linear);
            backWaveLoop.Duration = TimeSpan.FromSeconds(3.7);
            backWaveLoop.IterationBehavior = AnimationIterationBehavior.Forever;
            props.StartAnimation("BackWaveY", backWaveLoop);

            // 1) Back translucent cross-section wave (liquid depth at leading edge)
            var backWaveVisual = compositor.CreateShapeVisual();
            backWaveVisual.Size = new Vector2(WaveLeftExtent + 32f, wavePeriodY * 2f);
            var backWaveGeometry = CreateVerticalWaveAreaGeometry(compositor, wavePeriodY, CrossSectionWaveAmplitude * 0.92f, phaseShift: 2.1f);
            var backWaveShape = compositor.CreateSpriteShape(backWaveGeometry);
            backWaveShape.FillBrush = compositor.CreateColorBrush(isLight
                ? Windows.UI.Color.FromArgb(42, 15, 123, 15)
                : Windows.UI.Color.FromArgb(40, 108, 203, 95));
            backWaveVisual.Shapes.Add(backWaveShape);

            var backWaveOffset = compositor.CreateExpressionAnimation("Vector3(props.FillX + 2.8 - 2600.0, props.BackWaveY, 0)");
            backWaveOffset.SetReferenceParameter("props", props);
            backWaveVisual.StartAnimation(nameof(Visual.Offset), backWaveOffset);
            layer.Children.InsertAtTop(backWaveVisual);

            // 2) Main water container clipped by the undulating front cross-section wave
            var waterBody = compositor.CreateContainerVisual();
            waterBody.RelativeSizeAdjustment = Vector2.One;
            var frontWaveAreaGeometry = CreateVerticalWaveAreaGeometry(compositor, wavePeriodY, CrossSectionWaveAmplitude, phaseShift: 0f);
            var waterClip = compositor.CreateGeometricClip(frontWaveAreaGeometry);
            var clipOffset = compositor.CreateExpressionAnimation("Vector2(props.FillX - 2600.0, props.WaveY)");
            clipOffset.SetReferenceParameter("props", props);
            waterClip.StartAnimation("Offset", clipOffset);
            waterBody.Clip = waterClip;
            _waterClip = waterClip;

            // 2a) Base water body subtle vertical depth gradient
            var baseWater = compositor.CreateSpriteVisual();
            baseWater.RelativeSizeAdjustment = Vector2.One;
            var baseBrush = compositor.CreateLinearGradientBrush();
            baseBrush.StartPoint = new Vector2(0.5f, 0f);
            baseBrush.EndPoint = new Vector2(0.5f, 1f);
            if (isLight)
            {
                baseBrush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(74, 15, 123, 15)));
                baseBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.55f, Windows.UI.Color.FromArgb(96, 15, 123, 15)));
                baseBrush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(116, 15, 123, 15)));
            }
            else
            {
                baseBrush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(58, 108, 203, 95)));
                baseBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.55f, Windows.UI.Color.FromArgb(78, 108, 203, 95)));
                baseBrush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(96, 108, 203, 95)));
            }

            baseWater.Brush = baseBrush;
            waterBody.Children.InsertAtTop(baseWater);

            // 2b) Two flowing horizontal water ripple ribbons + specular crest lines
            AddWaterRippleRibbon(
                waterBody,
                compositor,
                _layerBuiltWidth,
                height,
                yRatio: 0.30f,
                amplitude: Math.Min(3.0f, height * 0.065f),
                thickness: Math.Min(11f, height * 0.22f),
                durationSeconds: 4.8,
                phaseShift: 0f,
                ribbonColor: isLight
                    ? Windows.UI.Color.FromArgb(38, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(28, 220, 255, 228),
                crestColor: isLight
                    ? Windows.UI.Color.FromArgb(50, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(36, 235, 255, 240));

            AddWaterRippleRibbon(
                waterBody,
                compositor,
                _layerBuiltWidth,
                height,
                yRatio: 0.68f,
                amplitude: Math.Min(2.6f, height * 0.055f),
                thickness: Math.Min(13f, height * 0.25f),
                durationSeconds: 6.6,
                phaseShift: 2.3f,
                ribbonColor: isLight
                    ? Windows.UI.Color.FromArgb(30, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(22, 220, 255, 228),
                crestColor: isLight
                    ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(28, 235, 255, 240));

            // 2c) Slow forward current sheen band
            AddWaterCurrentBand(waterBody, compositor, isLight);

            // 2d) Tiny hollow rising micro-bubbles carried gently by the current
            AddMicroBubbles(waterBody, compositor, isLight);

            layer.Children.InsertAtTop(waterBody);

            // 3) Crisp luminous meniscus rim along the undulating leading cross-section
            var rimVisual = compositor.CreateShapeVisual();
            rimVisual.Size = new Vector2(RimPadX * 2f, wavePeriodY * 2f);
            var rimGeometry = CreateVerticalWaveStrokeGeometry(compositor, wavePeriodY, CrossSectionWaveAmplitude, phaseShift: 0f);
            var rimShape = compositor.CreateSpriteShape(rimGeometry);
            rimShape.StrokeBrush = compositor.CreateColorBrush(isLight
                ? Windows.UI.Color.FromArgb(148, 255, 255, 255)
                : Windows.UI.Color.FromArgb(124, 215, 255, 224));
            rimShape.StrokeThickness = 1.5f;
            rimVisual.Shapes.Add(rimShape);

            var rimOffset = compositor.CreateExpressionAnimation("Vector3(props.FillX - 16.0, props.WaveY, 0)");
            rimOffset.SetReferenceParameter("props", props);
            rimVisual.StartAnimation(nameof(Visual.Offset), rimOffset);
            layer.Children.InsertAtTop(rimVisual);

            ElementCompositionPreview.SetElementChildVisual(_element, layer);
            return layer;
        }

        private void Clear()
        {
            SetWaterFillTargetHidden(false);
            if (_layer is null)
            {
                return;
            }

            StopAnimationsRecursive(_layer);
            _waterClip?.StopAnimation("Offset");
            _waterProps?.StopAnimation("WaveY");
            _waterProps?.StopAnimation("BackWaveY");

            _layer.Children.RemoveAll();
            ElementCompositionPreview.SetElementChildVisual(_element, null);
            _waterProps?.Dispose();
            _waterProps = null;
            _waterClip = null;
            _layer = null;
            _layerClip = null;
            _layerBuiltWidth = 0f;
            _layerBuiltHeight = 0f;
            _bubbleAnimations = null;
            _bubblePlaybackRate = 1f;
        }

        private static void StopAnimationsRecursive(Visual visual)
        {
            visual.StopAnimation(nameof(Visual.Offset));
            visual.StopAnimation(nameof(Visual.Opacity));
            if (visual is ContainerVisual container)
            {
                foreach (var child in container.Children)
                {
                    StopAnimationsRecursive(child);
                }
            }
        }

        private static CompositionPathGeometry CreateVerticalWaveAreaGeometry(
            Compositor compositor,
            float wavePeriodY,
            float amplitude,
            float phaseShift)
        {
            var totalHeight = wavePeriodY * 2f;
            const int stepsPerPeriod = 24;
            var totalSteps = stepsPerPeriod * 2;

            using var builder = new CanvasPathBuilder(null);
            builder.BeginFigure(0f, 0f);
            for (var i = 0; i <= totalSteps; i++)
            {
                var y = totalHeight * i / totalSteps;
                var x = WaveLeftExtent + EvaluateVerticalWaveX(y, wavePeriodY, amplitude, phaseShift);
                builder.AddLine(x, y);
            }

            builder.AddLine(0f, totalHeight);
            builder.EndFigure(CanvasFigureLoop.Closed);

            using var canvasGeometry = CanvasGeometry.CreatePath(builder);
            return compositor.CreatePathGeometry(new CompositionPath(canvasGeometry));
        }

        private static CompositionPathGeometry CreateVerticalWaveStrokeGeometry(
            Compositor compositor,
            float wavePeriodY,
            float amplitude,
            float phaseShift)
        {
            var totalHeight = wavePeriodY * 2f;
            const int stepsPerPeriod = 24;
            var totalSteps = stepsPerPeriod * 2;

            using var builder = new CanvasPathBuilder(null);
            var startX = RimPadX + EvaluateVerticalWaveX(0f, wavePeriodY, amplitude, phaseShift);
            builder.BeginFigure(startX, 0f);
            for (var i = 1; i <= totalSteps; i++)
            {
                var y = totalHeight * i / totalSteps;
                var x = RimPadX + EvaluateVerticalWaveX(y, wavePeriodY, amplitude, phaseShift);
                builder.AddLine(x, y);
            }

            builder.EndFigure(CanvasFigureLoop.Open);

            using var canvasGeometry = CanvasGeometry.CreatePath(builder);
            return compositor.CreatePathGeometry(new CompositionPath(canvasGeometry));
        }

        private static float EvaluateVerticalWaveX(float y, float wavePeriodY, float amplitude, float phaseShift)
        {
            var angle = MathF.Tau * (y / wavePeriodY);
            return MathF.Sin(angle + phaseShift) * amplitude * 0.78f
                + MathF.Cos(angle * 2f - phaseShift * 0.6f) * amplitude * 0.22f;
        }

        private static void AddWaterRippleRibbon(
            ContainerVisual parent,
            Compositor compositor,
            float targetWidth,
            float height,
            float yRatio,
            float amplitude,
            float thickness,
            double durationSeconds,
            float phaseShift,
            Windows.UI.Color ribbonColor,
            Windows.UI.Color crestColor)
        {
            var tileCount = Math.Max(3, (int)MathF.Ceiling(targetWidth / HorizontalRipplePeriod) + 2);
            var totalWidth = tileCount * HorizontalRipplePeriod;
            var baseY = height * yRatio;
            const int stepsPerTile = 16;
            var totalSteps = tileCount * stepsPerTile;

            using var ribbonBuilder = new CanvasPathBuilder(null);
            for (var i = 0; i <= totalSteps; i++)
            {
                var x = totalWidth * i / totalSteps;
                var angle = MathF.Tau * (x / HorizontalRipplePeriod) + phaseShift;
                var y = baseY + MathF.Sin(angle) * amplitude - thickness * 0.5f;
                if (i == 0)
                {
                    ribbonBuilder.BeginFigure(x, y);
                }
                else
                {
                    ribbonBuilder.AddLine(x, y);
                }
            }

            for (var i = totalSteps; i >= 0; i--)
            {
                var x = totalWidth * i / totalSteps;
                var angle = MathF.Tau * (x / HorizontalRipplePeriod) + phaseShift + 0.55f;
                var y = baseY + MathF.Sin(angle) * (amplitude * 0.85f) + thickness * 0.5f;
                ribbonBuilder.AddLine(x, y);
            }

            ribbonBuilder.EndFigure(CanvasFigureLoop.Closed);

            using var crestBuilder = new CanvasPathBuilder(null);
            for (var i = 0; i <= totalSteps; i++)
            {
                var x = totalWidth * i / totalSteps;
                var angle = MathF.Tau * (x / HorizontalRipplePeriod) + phaseShift;
                var y = baseY + MathF.Sin(angle) * amplitude;
                if (i == 0)
                {
                    crestBuilder.BeginFigure(x, y);
                }
                else
                {
                    crestBuilder.AddLine(x, y);
                }
            }

            crestBuilder.EndFigure(CanvasFigureLoop.Open);

            var ribbonVisual = compositor.CreateShapeVisual();
            ribbonVisual.Size = new Vector2(totalWidth, height);

            using (var ribbonGeo = CanvasGeometry.CreatePath(ribbonBuilder))
            {
                var shape = compositor.CreateSpriteShape(compositor.CreatePathGeometry(new CompositionPath(ribbonGeo)));
                shape.FillBrush = compositor.CreateColorBrush(ribbonColor);
                ribbonVisual.Shapes.Add(shape);
            }

            using (var crestGeo = CanvasGeometry.CreatePath(crestBuilder))
            {
                var crestShape = compositor.CreateSpriteShape(compositor.CreatePathGeometry(new CompositionPath(crestGeo)));
                crestShape.StrokeBrush = compositor.CreateColorBrush(crestColor);
                crestShape.StrokeThickness = 1f;
                ribbonVisual.Shapes.Add(crestShape);
            }

            parent.Children.InsertAtTop(ribbonVisual);

            var linear = compositor.CreateLinearEasingFunction();
            var drift = compositor.CreateVector3KeyFrameAnimation();
            drift.InsertKeyFrame(0f, new Vector3(-HorizontalRipplePeriod, 0f, 0f));
            drift.InsertKeyFrame(1f, Vector3.Zero, linear);
            drift.Duration = TimeSpan.FromSeconds(durationSeconds);
            drift.IterationBehavior = AnimationIterationBehavior.Forever;
            ribbonVisual.StartAnimation(nameof(Visual.Offset), drift);
        }

        private static void AddWaterCurrentBand(ContainerVisual parent, Compositor compositor, bool isLight)
        {
            const float bandWidth = 160f;
            var brush = compositor.CreateLinearGradientBrush();
            brush.StartPoint = new Vector2(0f, 0.5f);
            brush.EndPoint = new Vector2(1f, 0.5f);
            var peakColor = isLight
                ? Windows.UI.Color.FromArgb(38, 255, 255, 255)
                : Windows.UI.Color.FromArgb(28, 230, 255, 235);
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Colors.Transparent));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, peakColor));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Colors.Transparent));

            var band = compositor.CreateSpriteVisual();
            band.Brush = brush;
            band.Size = new Vector2(bandWidth, 0f);
            band.RelativeSizeAdjustment = new Vector2(0f, 1f);
            band.Offset = new Vector3(-bandWidth, 0f, 0f);
            parent.Children.InsertAtTop(band);

            var linear = compositor.CreateLinearEasingFunction();
            var travel = compositor.CreateVector3KeyFrameAnimation();
            travel.SetReferenceParameter("container", parent);
            travel.InsertKeyFrame(0f, new Vector3(-bandWidth, 0f, 0f));
            travel.InsertExpressionKeyFrame(0.82f, "Vector3(container.Size.X, 0, 0)", linear);
            travel.InsertExpressionKeyFrame(1f, "Vector3(container.Size.X, 0, 0)", linear);
            travel.Duration = TimeSpan.FromSeconds(5.6);
            travel.IterationBehavior = AnimationIterationBehavior.Forever;
            band.StartAnimation(nameof(Visual.Offset), travel);
        }

        private void AddMicroBubbles(ContainerVisual parent, Compositor compositor, bool isLight)
        {
            var random = new Random(31);
            var ringBrush = compositor.CreateColorBrush(isLight
                ? Windows.UI.Color.FromArgb(156, 12, 96, 20)
                : Windows.UI.Color.FromArgb(196, 235, 255, 240));
            var glintBrush = compositor.CreateColorBrush(isLight
                ? Windows.UI.Color.FromArgb(220, 255, 255, 255)
                : Windows.UI.Color.FromArgb(235, 255, 255, 255));
            var linear = compositor.CreateLinearEasingFunction();

            const int bubbleCount = 18;
            var animations = new List<AnimationController>(bubbleCount * 2);
            for (var index = 0; index < bubbleCount; index++)
            {
                var radius = 0.95f + random.NextSingle() * 1.10f;
                var bubble = compositor.CreateShapeVisual();
                bubble.Size = new Vector2(8f, 8f);
                bubble.RelativeOffsetAdjustment = new Vector3(
                    0.03f + (index + random.NextSingle()) / bubbleCount * 0.92f,
                    0.22f + random.NextSingle() * 0.64f,
                    0f);

                // Hollow micro-bubble ring + tiny upper-left specular glint
                bubble.Shapes.Add(CreateBubbleRing(compositor, ringBrush, radius));
                bubble.Shapes.Add(CreateBubbleGlint(compositor, glintBrush, radius));
                parent.Children.InsertAtTop(bubble);

                var duration = TimeSpan.FromSeconds(3.6f + random.NextSingle() * 2.6f);
                var delay = TimeSpan.FromSeconds(random.NextSingle() * 2.4f);

                var drift = compositor.CreateVector3KeyFrameAnimation();
                var dx = 22f + random.NextSingle() * 22f;
                var dy = -4.5f - random.NextSingle() * 6.5f;
                drift.InsertKeyFrame(0f, new Vector3(-10f, 1.5f, 0f));
                drift.InsertKeyFrame(0.5f, new Vector3(-10f + dx * 0.5f, dy * 0.45f - 1.0f, 0f), linear);
                drift.InsertKeyFrame(1f, new Vector3(-10f + dx, dy, 0f), linear);
                drift.Duration = duration;
                drift.DelayTime = delay;
                drift.IterationBehavior = AnimationIterationBehavior.Forever;
                bubble.StartAnimation(nameof(Visual.Offset), drift);

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(0.35f, 0.52f + random.NextSingle() * 0.30f);
                fade.InsertKeyFrame(1f, 0f);
                fade.Duration = duration;
                fade.DelayTime = delay;
                fade.IterationBehavior = AnimationIterationBehavior.Forever;
                bubble.Opacity = 0f;
                bubble.StartAnimation(nameof(Visual.Opacity), fade);

                if (bubble.TryGetAnimationController(nameof(Visual.Offset)) is { } driftController
                    && bubble.TryGetAnimationController(nameof(Visual.Opacity)) is { } fadeController)
                {
                    animations.Add(driftController);
                    animations.Add(fadeController);
                }
            }

            _bubbleAnimations = animations;
        }

        private static CompositionSpriteShape CreateBubbleRing(Compositor compositor, CompositionBrush strokeBrush, float radius)
        {
            var ellipse = compositor.CreateEllipseGeometry();
            ellipse.Center = new Vector2(4f, 4f);
            ellipse.Radius = new Vector2(radius);
            var ring = compositor.CreateSpriteShape(ellipse);
            ring.StrokeBrush = strokeBrush;
            ring.StrokeThickness = 0.8f;
            return ring;
        }

        private static CompositionSpriteShape CreateBubbleGlint(Compositor compositor, CompositionBrush fillBrush, float radius)
        {
            var ellipse = compositor.CreateEllipseGeometry();
            ellipse.Center = new Vector2(4f - radius * 0.32f, 4f - radius * 0.32f);
            ellipse.Radius = new Vector2(Math.Max(0.35f, radius * 0.28f));
            var glint = compositor.CreateSpriteShape(ellipse);
            glint.FillBrush = fillBrush;
            return glint;
        }

        private static void AddShimmer(ContainerVisual layer, Compositor compositor, ElementTheme theme)
        {
            const float highlightWidth = 64f;
            var peakAlpha = (byte)(theme == ElementTheme.Light ? 155 : 175);
            var brush = compositor.CreateLinearGradientBrush();
            brush.StartPoint = new Vector2(0f, 0.5f);
            brush.EndPoint = new Vector2(1f, 0.5f);
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Colors.Transparent));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(0.48f, Windows.UI.Color.FromArgb(peakAlpha, 255, 255, 255)));
            brush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Colors.Transparent));

            var band = compositor.CreateSpriteVisual();
            band.Brush = brush;
            band.Size = new Vector2(highlightWidth, 0f);
            band.RelativeSizeAdjustment = new Vector2(0f, 1f);
            band.Offset = new Vector3(-highlightWidth, 0f, 0f);
            layer.Children.InsertAtTop(band);

            var linear = compositor.CreateLinearEasingFunction();
            var travel = compositor.CreateVector3KeyFrameAnimation();
            travel.SetReferenceParameter("progress", layer);
            travel.InsertKeyFrame(0f, band.Offset);
            travel.InsertExpressionKeyFrame(0.64f, "Vector3(progress.Size.X, 0, 0)", linear);
            travel.InsertExpressionKeyFrame(1f, "Vector3(progress.Size.X, 0, 0)", linear);
            travel.Duration = TimeSpan.FromSeconds(2.6);
            travel.IterationBehavior = AnimationIterationBehavior.Forever;
            band.StartAnimation(nameof(Visual.Offset), travel);
        }
    }
}

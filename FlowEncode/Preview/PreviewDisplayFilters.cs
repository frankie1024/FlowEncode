using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FlowEncode.Application;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace FlowEncode;

internal enum PreviewDisplayFilter
{
    Original,
    Luma,
    Chroma,
    Solar1,
    Solar2,
    Residual,
    ShadowBoost,
    LegalRangeAlert,
    EdgeHighlight
}

internal static class PreviewDisplayFilters
{
    private const int SideBySideSnapshotGap = 4;
    private static readonly SolarTables SolarOnce = new(1);
    private static readonly SolarTables SolarTwice = new(2);
    private static readonly LegalRangeTables LegalRangeLut = new();
    private static readonly HeatmapTables TurboHeatmapLut = new();

    public static void Draw(
        CanvasDrawingSession drawingSession,
        CanvasBitmap bitmap,
        PreviewDisplayFilter filter,
        IReadOnlyList<VapourSynthPreviewFrameProperty>? properties)
    {
        var disposables = new List<IDisposable>();
        try
        {
            var image = CreateFilteredImage(bitmap, filter, properties, disposables);
            drawingSession.DrawImage(image);
        }
        finally
        {
            DisposeAll(disposables);
        }
    }

    public static void DrawDifference(
        CanvasDrawingSession drawingSession,
        CanvasBitmap bitmapA,
        CanvasBitmap bitmapB,
        PreviewDisplayFilter filter,
        PreviewDifferenceGain gain,
        PreviewCompareSizeAlignment sizeAlignment,
        CanvasImageInterpolation interpolation,
        IReadOnlyList<VapourSynthPreviewFrameProperty>? propertiesA,
        IReadOnlyList<VapourSynthPreviewFrameProperty>? propertiesB,
        PreviewDifferenceStyle differenceStyle = PreviewDifferenceStyle.Absolute)
    {
        var widthA = Math.Max(1, (int)bitmapA.SizeInPixels.Width);
        var heightA = Math.Max(1, (int)bitmapA.SizeInPixels.Height);
        var widthB = Math.Max(1, (int)bitmapB.SizeInPixels.Width);
        var heightB = Math.Max(1, (int)bitmapB.SizeInPixels.Height);
        var layout = PreviewCompareCoordinator.ComputeAlignedCanvasLayout(widthA, heightA, widthB, heightB, sizeAlignment);

        drawingSession.Clear(Color.FromArgb(255, 0, 0, 0));
        var disposables = new List<IDisposable>();
        try
        {
            var filteredA = CreateFilteredImage(bitmapA, filter, propertiesA, disposables);
            var alignedA = PlaceImageInRect(filteredA, widthA, heightA, layout.RectA, interpolation, disposables);
            var filteredB = CreateFilteredImage(bitmapB, filter, propertiesB, disposables);
            var alignedB = PlaceImageInRect(filteredB, widthB, heightB, layout.RectB, interpolation, disposables);

            ICanvasImage output;
            if (differenceStyle == PreviewDifferenceStyle.Signed)
            {
                output = CreateSignedDifferenceImage(alignedA, alignedB, gain, disposables);
            }
            else
            {
                var difference = new BlendEffect
                {
                    Background = alignedA,
                    Foreground = alignedB,
                    Mode = BlendEffectMode.Difference
                };
                disposables.Add(difference);

                var gained = ApplyDifferenceGain(difference, gain, disposables);
                output = differenceStyle == PreviewDifferenceStyle.Heatmap
                    ? CreateHeatmapImage(gained, disposables)
                    : gained;
            }

            drawingSession.DrawImage(output);
        }
        finally
        {
            DisposeAll(disposables);
        }
    }

    public static (byte[] Pixels, int Width, int Height) RenderCompositeSnapshotBytes(
        CanvasDevice device,
        PreviewCompareMode mode,
        CanvasBitmap bitmapA,
        CanvasBitmap bitmapB,
        PreviewDisplayFilter filter,
        PreviewDifferenceGain gain,
        PreviewCompareSizeAlignment sizeAlignment,
        double splitRatio,
        CanvasImageInterpolation interpolation,
        IReadOnlyList<VapourSynthPreviewFrameProperty>? propertiesA,
        IReadOnlyList<VapourSynthPreviewFrameProperty>? propertiesB,
        PreviewDifferenceStyle differenceStyle = PreviewDifferenceStyle.Absolute)
    {
        var widthA = Math.Max(1, (int)bitmapA.SizeInPixels.Width);
        var heightA = Math.Max(1, (int)bitmapA.SizeInPixels.Height);
        var widthB = Math.Max(1, (int)bitmapB.SizeInPixels.Width);
        var heightB = Math.Max(1, (int)bitmapB.SizeInPixels.Height);

        if (mode == PreviewCompareMode.SideBySide)
        {
            var targetWidth = checked(widthA + SideBySideSnapshotGap + widthB);
            var targetHeight = Math.Max(heightA, heightB);
            using var renderTarget = new CanvasRenderTarget(
                device,
                targetWidth,
                targetHeight,
                96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied);
            using (var drawingSession = renderTarget.CreateDrawingSession())
            {
                drawingSession.Clear(Color.FromArgb(255, 18, 18, 18));
                var disposables = new List<IDisposable>();
                try
                {
                    var imageA = CreateFilteredImage(bitmapA, filter, propertiesA, disposables);
                    var imageB = CreateFilteredImage(bitmapB, filter, propertiesB, disposables);
                    var offsetYA = (targetHeight - heightA) / 2f;
                    var offsetYB = (targetHeight - heightB) / 2f;
                    drawingSession.DrawImage(imageA, new Vector2(0, offsetYA));
                    drawingSession.DrawImage(imageB, new Vector2(widthA + SideBySideSnapshotGap, offsetYB));
                }
                finally
                {
                    DisposeAll(disposables);
                }
            }

            return (renderTarget.GetPixelBytes(), targetWidth, targetHeight);
        }

        var layout = PreviewCompareCoordinator.ComputeAlignedCanvasLayout(widthA, heightA, widthB, heightB, sizeAlignment);
        using (var renderTarget = new CanvasRenderTarget(
                   device,
                   layout.CanvasWidth,
                   layout.CanvasHeight,
                   96,
                   DirectXPixelFormat.B8G8R8A8UIntNormalized,
                   CanvasAlphaMode.Premultiplied))
        {
            using (var drawingSession = renderTarget.CreateDrawingSession())
            {
                drawingSession.Clear(Color.FromArgb(0, 0, 0, 0));
                if (mode == PreviewCompareMode.Difference)
                {
                    DrawDifference(
                        drawingSession,
                        bitmapA,
                        bitmapB,
                        filter,
                        gain,
                        sizeAlignment,
                        interpolation,
                        propertiesA,
                        propertiesB,
                        differenceStyle);
                }
                else
                {
                    var clampedRatio = PreviewCompareCoordinator.ClampSplitRatio(splitRatio);
                    var splitX = (float)Math.Clamp(Math.Round(layout.CanvasWidth * clampedRatio), 0, layout.CanvasWidth);
                    var disposables = new List<IDisposable>();
                    try
                    {
                        var filteredA = CreateFilteredImage(bitmapA, filter, propertiesA, disposables);
                        var alignedA = PlaceImageInRect(filteredA, widthA, heightA, layout.RectA, interpolation, disposables);
                        var filteredB = CreateFilteredImage(bitmapB, filter, propertiesB, disposables);
                        var alignedB = PlaceImageInRect(filteredB, widthB, heightB, layout.RectB, interpolation, disposables);

                        if (splitX > 0)
                        {
                            using (drawingSession.CreateLayer(1f, new Rect(0, 0, splitX, layout.CanvasHeight)))
                            {
                                drawingSession.DrawImage(alignedA);
                            }
                        }

                        if (splitX < layout.CanvasWidth)
                        {
                            using (drawingSession.CreateLayer(1f, new Rect(splitX, 0, layout.CanvasWidth - splitX, layout.CanvasHeight)))
                            {
                                drawingSession.DrawImage(alignedB);
                            }
                        }

                        if (splitX > 0 && splitX < layout.CanvasWidth)
                        {
                            drawingSession.DrawLine(
                                new Vector2(splitX, 0),
                                new Vector2(splitX, layout.CanvasHeight),
                                Color.FromArgb(220, 255, 255, 255),
                                2f);
                        }
                    }
                    finally
                    {
                        DisposeAll(disposables);
                    }
                }
            }

            return (renderTarget.GetPixelBytes(), layout.CanvasWidth, layout.CanvasHeight);
        }
    }

    private static ICanvasImage CreateFilteredImage(
        ICanvasImage source,
        PreviewDisplayFilter filter,
        IReadOnlyList<VapourSynthPreviewFrameProperty>? properties,
        List<IDisposable> disposables)
    {
        switch (filter)
        {
            case PreviewDisplayFilter.Luma:
            case PreviewDisplayFilter.Chroma:
                var channelEffect = new ColorMatrixEffect
                {
                    Source = source,
                    ColorMatrix = CreateChannelMatrix(filter, properties),
                    ClampOutput = true
                };
                disposables.Add(channelEffect);
                return channelEffect;
            case PreviewDisplayFilter.Solar1:
            case PreviewDisplayFilter.Solar2:
                var tables = filter == PreviewDisplayFilter.Solar1 ? SolarOnce : SolarTwice;
                var solarEffect = new TableTransferEffect
                {
                    Source = source,
                    RedTable = tables.Red,
                    GreenTable = tables.Green,
                    BlueTable = tables.Blue,
                    AlphaDisable = true
                };
                disposables.Add(solarEffect);
                return solarEffect;
            case PreviewDisplayFilter.Residual:
                return CreateResidualImage(source, disposables);
            case PreviewDisplayFilter.ShadowBoost:
                return CreateShadowBoostImage(source, disposables);
            case PreviewDisplayFilter.LegalRangeAlert:
                return CreateLegalRangeAlertImage(source, properties, disposables);
            case PreviewDisplayFilter.EdgeHighlight:
                return CreateEdgeHighlightImage(source, disposables);
            default:
                return source;
        }
    }

    private static ICanvasImage PlaceImageInRect(
        ICanvasImage source,
        int sourceWidth,
        int sourceHeight,
        PreviewCompareAlignedRect targetRect,
        CanvasImageInterpolation interpolation,
        List<IDisposable> disposables)
    {
        var scaleX = (float)(targetRect.Width / Math.Max(1, sourceWidth));
        var scaleY = (float)(targetRect.Height / Math.Max(1, sourceHeight));
        var translateX = (float)targetRect.X;
        var translateY = (float)targetRect.Y;

        if (Math.Abs(scaleX - 1f) < 0.0001f
            && Math.Abs(scaleY - 1f) < 0.0001f
            && Math.Abs(translateX) < 0.0001f
            && Math.Abs(translateY) < 0.0001f)
        {
            return source;
        }

        var transform = new Transform2DEffect
        {
            Source = source,
            TransformMatrix = Matrix3x2.CreateScale(scaleX, scaleY) * Matrix3x2.CreateTranslation(translateX, translateY),
            InterpolationMode = interpolation
        };
        disposables.Add(transform);
        return transform;
    }

    private static ICanvasImage ApplyDifferenceGain(
        ICanvasImage difference,
        PreviewDifferenceGain gain,
        List<IDisposable> disposables)
    {
        switch (gain)
        {
            case PreviewDifferenceGain.X4:
            case PreviewDifferenceGain.X16:
                var factor = gain == PreviewDifferenceGain.X16 ? 16f : 4f;
                var scaled = new ColorMatrixEffect
                {
                    Source = difference,
                    ColorMatrix = new Matrix5x4
                    {
                        M11 = factor,
                        M22 = factor,
                        M33 = factor,
                        M44 = 1f
                    },
                    ClampOutput = true
                };
                disposables.Add(scaled);
                return scaled;
            case PreviewDifferenceGain.Boost:
                var boosted = new GammaTransferEffect
                {
                    Source = difference,
                    RedExponent = 0.35f,
                    GreenExponent = 0.35f,
                    BlueExponent = 0.35f,
                    AlphaDisable = true
                };
                disposables.Add(boosted);
                return boosted;
            default:
                return difference;
        }
    }

    private static Matrix5x4 CreateChannelMatrix(
        PreviewDisplayFilter filter,
        IReadOnlyList<VapourSynthPreviewFrameProperty>? properties)
    {
        var matrix = properties?.FirstOrDefault(static property => property.Key == "_Matrix")?.Value;
        var primaries = properties?.FirstOrDefault(static property => property.Key == "_Primaries")?.Value;
        var (red, green, blue) = matrix switch
        {
            "9" or "10" => (0.2627f, 0.6780f, 0.0593f),
            "5" or "6" => (0.299f, 0.587f, 0.114f),
            null or "0" or "2" when primaries == "9" => (0.2627f, 0.6780f, 0.0593f),
            _ => (0.2126f, 0.7152f, 0.0722f)
        };

        // Direct2D transforms a row vector. Each matrix row is one input channel's contribution.
        if (filter == PreviewDisplayFilter.Luma)
        {
            return new Matrix5x4
            {
                M11 = red, M12 = red, M13 = red,
                M21 = green, M22 = green, M23 = green,
                M31 = blue, M32 = blue, M33 = blue,
                M44 = 1
            };
        }

        // Subtract luma from each channel and display the residual over neutral gray.
        const float neutral = (128f - 16f) / 219f;
        return new Matrix5x4
        {
            M11 = 1 - red, M12 = -red, M13 = -red,
            M21 = -green, M22 = 1 - green, M23 = -green,
            M31 = -blue, M32 = -blue, M33 = 1 - blue,
            M44 = 1,
            M51 = neutral, M52 = neutral, M53 = neutral
        };
    }

    private static ICanvasImage CreateResidualImage(ICanvasImage source, List<IDisposable> disposables)
    {
        var smooth = new GaussianBlurEffect
        {
            Source = source,
            BlurAmount = 2,
            BorderMode = EffectBorderMode.Hard
        };
        disposables.Add(smooth);
        var difference = new BlendEffect
        {
            Background = source,
            Foreground = smooth,
            Mode = BlendEffectMode.Difference
        };
        disposables.Add(difference);
        var boosted = new GammaTransferEffect
        {
            Source = difference,
            RedExponent = 0.35f,
            GreenExponent = 0.35f,
            BlueExponent = 0.35f,
            AlphaDisable = true
        };
        disposables.Add(boosted);
        return boosted;
    }

    private static ICanvasImage CreateShadowBoostImage(ICanvasImage source, List<IDisposable> disposables)
    {
        var boosted = new GammaTransferEffect
        {
            Source = source,
            RedExponent = 0.38f,
            GreenExponent = 0.38f,
            BlueExponent = 0.38f,
            AlphaDisable = true
        };
        disposables.Add(boosted);
        return boosted;
    }

    private static ICanvasImage CreateLegalRangeAlertImage(
        ICanvasImage source,
        IReadOnlyList<VapourSynthPreviewFrameProperty>? properties,
        List<IDisposable> disposables)
    {
        var luma = new ColorMatrixEffect
        {
            Source = source,
            ColorMatrix = CreateChannelMatrix(PreviewDisplayFilter.Luma, properties),
            ClampOutput = true
        };
        disposables.Add(luma);

        var alert = new TableTransferEffect
        {
            Source = luma,
            RedTable = LegalRangeLut.Red,
            GreenTable = LegalRangeLut.Green,
            BlueTable = LegalRangeLut.Blue,
            AlphaDisable = true
        };
        disposables.Add(alert);
        return alert;
    }

    private static ICanvasImage CreateEdgeHighlightImage(ICanvasImage source, List<IDisposable> disposables)
    {
        var positiveLaplacian = new ConvolveMatrixEffect
        {
            Source = source,
            KernelWidth = 3,
            KernelHeight = 3,
            KernelMatrix =
            [
                -1f, -1f, -1f,
                -1f,  8f, -1f,
                -1f, -1f, -1f
            ],
            Divisor = 2f,
            Offset = 0f,
            BorderMode = EffectBorderMode.Hard,
            PreserveAlpha = true
        };
        disposables.Add(positiveLaplacian);

        var negativeLaplacian = new ConvolveMatrixEffect
        {
            Source = source,
            KernelWidth = 3,
            KernelHeight = 3,
            KernelMatrix =
            [
                 1f,  1f,  1f,
                 1f, -8f,  1f,
                 1f,  1f,  1f
            ],
            Divisor = 2f,
            Offset = 0f,
            BorderMode = EffectBorderMode.Hard,
            PreserveAlpha = true
        };
        disposables.Add(negativeLaplacian);

        var combined = new ArithmeticCompositeEffect
        {
            Source1 = positiveLaplacian,
            Source2 = negativeLaplacian,
            MultiplyAmount = 0f,
            Source1Amount = 1f,
            Source2Amount = 1f,
            Offset = 0f,
            ClampOutput = true
        };
        disposables.Add(combined);

        var boosted = new GammaTransferEffect
        {
            Source = combined,
            RedExponent = 0.55f,
            GreenExponent = 0.55f,
            BlueExponent = 0.55f,
            AlphaDisable = true
        };
        disposables.Add(boosted);
        return boosted;
    }

    private static ICanvasImage CreateSignedDifferenceImage(
        ICanvasImage alignedA,
        ICanvasImage alignedB,
        PreviewDifferenceGain gain,
        List<IDisposable> disposables)
    {
        var factor = gain switch
        {
            PreviewDifferenceGain.X4 => 4f,
            PreviewDifferenceGain.X16 => 16f,
            PreviewDifferenceGain.Boost => 6f,
            _ => 1f
        };

        var signed = new ArithmeticCompositeEffect
        {
            Source1 = alignedB,
            Source2 = alignedA,
            MultiplyAmount = 0f,
            Source1Amount = factor,
            Source2Amount = -factor,
            Offset = 0.5f,
            ClampOutput = true
        };
        disposables.Add(signed);
        return signed;
    }

    private static ICanvasImage CreateHeatmapImage(ICanvasImage gainedDifference, List<IDisposable> disposables)
    {
        // Convert difference magnitude to grayscale intensity, then map through Turbo heatmap LUT.
        var intensity = new ColorMatrixEffect
        {
            Source = gainedDifference,
            ColorMatrix = new Matrix5x4
            {
                M11 = 0.3334f, M12 = 0.3334f, M13 = 0.3334f,
                M21 = 0.3334f, M22 = 0.3334f, M23 = 0.3334f,
                M31 = 0.3334f, M32 = 0.3334f, M33 = 0.3334f,
                M44 = 1f
            },
            ClampOutput = true
        };
        disposables.Add(intensity);

        var heatmap = new TableTransferEffect
        {
            Source = intensity,
            RedTable = TurboHeatmapLut.Red,
            GreenTable = TurboHeatmapLut.Green,
            BlueTable = TurboHeatmapLut.Blue,
            AlphaDisable = true
        };
        disposables.Add(heatmap);
        return heatmap;
    }

    private static void DisposeAll(List<IDisposable> disposables)
    {
        for (var i = disposables.Count - 1; i >= 0; i--)
        {
            disposables[i].Dispose();
        }
    }

    private sealed class SolarTables(int passes)
    {
        public float[] Red { get; } = CreateSolarTable(0, passes);
        public float[] Green { get; } = CreateSolarTable(-5, passes);
        public float[] Blue { get; } = CreateSolarTable(5, passes);

        private static float[] CreateSolarTable(int channelOffset, int passes)
        {
            // Solar curve adapted from yacomp (MIT), see docs/THIRD_PARTY_LICENSES.md.
            const double phaseCorrection = (5.5 * Math.PI - 128.0 / 5) / 2;
            var table = new float[256];
            for (var input = 0; input < table.Length; input++)
            {
                double value = input;
                for (var pass = 0; pass < passes; pass++)
                {
                    var shifted = value + channelOffset;
                    var normalized = shifted / 128;
                    var phase = shifted / 5 + phaseCorrection * normalized * normalized * (3 - normalized);
                    value = Math.Clamp(127.5 - 127.9999 * Math.Cos(phase), 0, 255);
                }

                table[input] = (float)(value / 255);
            }

            return table;
        }
    }

    private sealed class LegalRangeTables
    {
        public float[] Red { get; } = new float[256];
        public float[] Green { get; } = new float[256];
        public float[] Blue { get; } = new float[256];

        public LegalRangeTables()
        {
            for (var i = 0; i < 256; i++)
            {
                if (i < 16)
                {
                    // Below legal black (16): vivid cyan-blue alert.
                    Red[i] = 0.0f;
                    Green[i] = 0.72f;
                    Blue[i] = 1.0f;
                }
                else if (i > 235)
                {
                    // Above legal white (235): vivid red alert.
                    Red[i] = 1.0f;
                    Green[i] = 0.16f;
                    Blue[i] = 0.16f;
                }
                else
                {
                    // In-range reference grayscale attenuated slightly so alerts stand out.
                    var gray = (float)((i - 16) / 219.0 * 0.62 + 0.10);
                    Red[i] = gray;
                    Green[i] = gray;
                    Blue[i] = gray;
                }
            }
        }
    }

    private sealed class HeatmapTables
    {
        public float[] Red { get; } = new float[256];
        public float[] Green { get; } = new float[256];
        public float[] Blue { get; } = new float[256];

        public HeatmapTables()
        {
            // Compact perceptual heatmap gradient: Black -> Deep Blue -> Cyan -> Green -> Yellow -> Red -> White.
            ReadOnlySpan<(double Pos, double R, double G, double B)> stops =
            [
                (0.00, 0.00, 0.00, 0.00),
                (0.04, 0.09, 0.05, 0.30),
                (0.20, 0.12, 0.36, 0.86),
                (0.42, 0.06, 0.78, 0.66),
                (0.66, 0.94, 0.88, 0.12),
                (0.86, 0.96, 0.24, 0.08),
                (1.00, 1.00, 0.96, 0.92)
            ];

            for (var i = 0; i < 256; i++)
            {
                var t = i / 255.0;
                var seg = 0;
                while (seg < stops.Length - 2 && t > stops[seg + 1].Pos)
                {
                    seg++;
                }

                var a = stops[seg];
                var b = stops[seg + 1];
                var local = b.Pos > a.Pos ? Math.Clamp((t - a.Pos) / (b.Pos - a.Pos), 0.0, 1.0) : 0.0;
                Red[i] = (float)(a.R + (b.R - a.R) * local);
                Green[i] = (float)(a.G + (b.G - a.G) * local);
                Blue[i] = (float)(a.B + (b.B - a.B) * local);
            }
        }
    }
}

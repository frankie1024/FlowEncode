using System;
using System.Collections.Generic;
using System.Linq;
using FlowEncode.Application;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace FlowEncode;

internal enum PreviewDisplayFilter
{
    Original,
    Luma,
    Chroma,
    Solar1,
    Solar2,
    Residual
}

internal static class PreviewDisplayFilters
{
    private static readonly SolarTables SolarOnce = new(1);
    private static readonly SolarTables SolarTwice = new(2);

    public static void Draw(
        CanvasDrawingSession drawingSession,
        CanvasBitmap bitmap,
        PreviewDisplayFilter filter,
        IReadOnlyList<VapourSynthPreviewFrameProperty>? properties)
    {
        switch (filter)
        {
            case PreviewDisplayFilter.Luma:
            case PreviewDisplayFilter.Chroma:
                using (var effect = new ColorMatrixEffect
                {
                    Source = bitmap,
                    ColorMatrix = CreateChannelMatrix(filter, properties),
                    ClampOutput = true
                })
                {
                    drawingSession.DrawImage(effect);
                }

                break;
            case PreviewDisplayFilter.Solar1:
            case PreviewDisplayFilter.Solar2:
                var tables = filter == PreviewDisplayFilter.Solar1 ? SolarOnce : SolarTwice;
                using (var effect = new TableTransferEffect
                {
                    Source = bitmap,
                    RedTable = tables.Red,
                    GreenTable = tables.Green,
                    BlueTable = tables.Blue,
                    AlphaDisable = true
                })
                {
                    drawingSession.DrawImage(effect);
                }

                break;
            case PreviewDisplayFilter.Residual:
                DrawResidual(drawingSession, bitmap);
                break;
            default:
                drawingSession.DrawImage(bitmap);
                break;
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

    private static void DrawResidual(CanvasDrawingSession drawingSession, CanvasBitmap bitmap)
    {
        using var smooth = new GaussianBlurEffect
        {
            Source = bitmap,
            BlurAmount = 2,
            BorderMode = EffectBorderMode.Hard
        };
        using var difference = new BlendEffect
        {
            Background = bitmap,
            Foreground = smooth,
            Mode = BlendEffectMode.Difference
        };
        using var boosted = new GammaTransferEffect
        {
            Source = difference,
            RedExponent = 0.35f,
            GreenExponent = 0.35f,
            BlueExponent = 0.35f,
            AlphaDisable = true
        };
        drawingSession.DrawImage(boosted);
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
}

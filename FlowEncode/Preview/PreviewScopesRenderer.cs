using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace FlowEncode;

internal enum PreviewScopeKind
{
    Histogram,
    Waveform,
    Vectorscope
}

internal static class PreviewScopesRenderer
{
    private static readonly CanvasTextFormat AxisLabelFormat = new()
    {
        FontSize = 11f,
        FontFamily = "Cascadia Code, Consolas, Segoe UI"
    };

    public static void Render(
        CanvasDrawingSession session,
        float width,
        float height,
        PreviewScopeKind kind,
        byte[]? bgraPixels,
        int frameWidth,
        int frameHeight)
    {
        session.Clear(Color.FromArgb(255, 16, 18, 22));
        if (width < 20 || height < 20 || bgraPixels is null || frameWidth <= 0 || frameHeight <= 0)
        {
            return;
        }

        if (bgraPixels.Length < checked(frameWidth * frameHeight * 4))
        {
            return;
        }

        switch (kind)
        {
            case PreviewScopeKind.Histogram:
                RenderHistogram(session, width, height, bgraPixels, frameWidth, frameHeight);
                break;
            case PreviewScopeKind.Waveform:
                RenderWaveform(session, width, height, bgraPixels, frameWidth, frameHeight);
                break;
            case PreviewScopeKind.Vectorscope:
                RenderVectorscope(session, width, height, bgraPixels, frameWidth, frameHeight);
                break;
        }
    }

    private static void RenderHistogram(
        CanvasDrawingSession session,
        float width,
        float height,
        byte[] bgra,
        int frameWidth,
        int frameHeight)
    {
        var histR = new int[256];
        var histG = new int[256];
        var histB = new int[256];
        var histY = new int[256];

        // Subsample so even 4K frames compute in < 3ms.
        var stepX = Math.Max(1, frameWidth / 400);
        var stepY = Math.Max(1, frameHeight / 225);

        for (var y = 0; y < frameHeight; y += stepY)
        {
            var rowOffset = y * frameWidth * 4;
            for (var x = 0; x < frameWidth; x += stepX)
            {
                var i = rowOffset + x * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                var luma = (int)Math.Clamp(Math.Round(0.2126 * r + 0.7152 * g + 0.0722 * b), 0, 255);
                histR[r]++;
                histG[g]++;
                histB[b]++;
                histY[luma]++;
            }
        }

        // Ignore extreme 0/255 clipping spikes when scaling chart height so midtones remain legible.
        var peak = 1;
        for (var i = 2; i < 254; i++)
        {
            peak = Math.Max(peak, histY[i]);
            peak = Math.Max(peak, histR[i]);
            peak = Math.Max(peak, histG[i]);
            peak = Math.Max(peak, histB[i]);
        }

        var padLeft = 14f;
        var padRight = 14f;
        var padTop = 14f;
        var padBottom = 24f;
        var plotW = Math.Max(1f, width - padLeft - padRight);
        var plotH = Math.Max(1f, height - padTop - padBottom);

        // Background frame and horizontal grid lines
        session.DrawRectangle(padLeft, padTop, plotW, plotH, Color.FromArgb(45, 255, 255, 255), 1f);
        for (var g = 1; g <= 3; g++)
        {
            var gy = padTop + plotH * (g / 4f);
            session.DrawLine(padLeft, gy, padLeft + plotW, gy, Color.FromArgb(32, 255, 255, 255), 1f);
        }

        // Legal range (16 & 235) and midpoint (128) guides
        var x16 = padLeft + (16f / 255f) * plotW;
        var x128 = padLeft + (128f / 255f) * plotW;
        var x235 = padLeft + (235f / 255f) * plotW;
        session.DrawLine(x16, padTop, x16, padTop + plotH, Color.FromArgb(110, 90, 185, 255), 1f);
        session.DrawLine(x128, padTop, x128, padTop + plotH, Color.FromArgb(40, 255, 255, 255), 1f);
        session.DrawLine(x235, padTop, x235, padTop + plotH, Color.FromArgb(110, 255, 105, 105), 1f);

        DrawHistogramCurve(session, histB, peak, padLeft, padTop, plotW, plotH, Color.FromArgb(170, 80, 160, 255), fill: false);
        DrawHistogramCurve(session, histG, peak, padLeft, padTop, plotW, plotH, Color.FromArgb(170, 80, 230, 135), fill: false);
        DrawHistogramCurve(session, histR, peak, padLeft, padTop, plotW, plotH, Color.FromArgb(170, 255, 95, 95), fill: false);
        DrawHistogramCurve(session, histY, peak, padLeft, padTop, plotW, plotH, Color.FromArgb(205, 240, 244, 250), fill: true);

        // Bottom scale labels
        var labelY = padTop + plotH + 4f;
        var labelColor = Color.FromArgb(165, 210, 220, 235);
        session.DrawText("0", padLeft, labelY, labelColor, AxisLabelFormat);
        session.DrawText("16", x16 + 2f, labelY, Color.FromArgb(190, 110, 195, 255), AxisLabelFormat);
        session.DrawText("128", x128 - 10f, labelY, labelColor, AxisLabelFormat);
        session.DrawText("235", x235 - 24f, labelY, Color.FromArgb(190, 255, 125, 125), AxisLabelFormat);
        session.DrawText("255", padLeft + plotW - 22f, labelY, labelColor, AxisLabelFormat);
    }

    private static void DrawHistogramCurve(
        CanvasDrawingSession session,
        int[] bins,
        int peak,
        float padX,
        float padY,
        float plotW,
        float plotH,
        Color color,
        bool fill)
    {
        using var builder = new CanvasPathBuilder(session.Device);
        var bottomY = padY + plotH;
        builder.BeginFigure(padX, bottomY);
        for (var i = 0; i < 256; i++)
        {
            var x = padX + (i / 255f) * plotW;
            var norm = Math.Min(1.0, Math.Log(1.0 + bins[i]) / Math.Log(1.0 + peak));
            var y = bottomY - (float)(norm * plotH);
            builder.AddLine(x, y);
        }

        if (fill)
        {
            builder.AddLine(padX + plotW, bottomY);
            builder.EndFigure(CanvasFigureLoop.Closed);
            using var geometry = CanvasGeometry.CreatePath(builder);
            session.FillGeometry(geometry, Color.FromArgb(58, color.R, color.G, color.B));
            session.DrawGeometry(geometry, color, 1.5f);
        }
        else
        {
            builder.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(builder);
            session.DrawGeometry(geometry, color, 1.35f);
        }
    }

    private static void RenderWaveform(
        CanvasDrawingSession session,
        float width,
        float height,
        byte[] bgra,
        int frameWidth,
        int frameHeight)
    {
        var padLeft = 32f;
        var padRight = 12f;
        var padTop = 14f;
        var padBottom = 14f;
        var plotW = Math.Max(1f, width - padLeft - padRight);
        var plotH = Math.Max(1f, height - padTop - padBottom);

        const int cols = 240;
        const int rows = 180;
        var density = new int[cols * rows];
        var stepX = Math.Max(1, frameWidth / cols);
        var stepY = Math.Max(1, frameHeight / 240);
        var maxDensity = 1;

        for (var y = 0; y < frameHeight; y += stepY)
        {
            var rowOffset = y * frameWidth * 4;
            for (var x = 0; x < frameWidth; x += stepX)
            {
                var i = rowOffset + x * 4;
                var luma = 0.2126 * bgra[i + 2] + 0.7152 * bgra[i + 1] + 0.0722 * bgra[i];
                var cx = Math.Clamp((int)((long)x * cols / frameWidth), 0, cols - 1);
                var ry = Math.Clamp((int)(luma / 255.0 * (rows - 1)), 0, rows - 1);
                var idx = ry * cols + cx;
                var count = ++density[idx];
                if (count > maxDensity)
                {
                    maxDensity = count;
                }
            }
        }

        // Border and horizontal grid lines
        session.DrawRectangle(padLeft, padTop, plotW, plotH, Color.FromArgb(45, 255, 255, 255), 1f);
        var y16 = padTop + plotH * (1f - 16f / 255f);
        var y128 = padTop + plotH * (1f - 128f / 255f);
        var y235 = padTop + plotH * (1f - 235f / 255f);
        session.DrawLine(padLeft, y16, padLeft + plotW, y16, Color.FromArgb(115, 90, 185, 255), 1f);
        session.DrawLine(padLeft, y128, padLeft + plotW, y128, Color.FromArgb(40, 255, 255, 255), 1f);
        session.DrawLine(padLeft, y235, padLeft + plotW, y235, Color.FromArgb(115, 255, 105, 105), 1f);

        session.DrawText("255", 4f, padTop - 4f, Color.FromArgb(150, 200, 210, 225), AxisLabelFormat);
        session.DrawText("235", 4f, y235 - 7f, Color.FromArgb(190, 255, 125, 125), AxisLabelFormat);
        session.DrawText("128", 4f, y128 - 7f, Color.FromArgb(145, 200, 210, 225), AxisLabelFormat);
        session.DrawText("16", 8f, y16 - 7f, Color.FromArgb(190, 110, 195, 255), AxisLabelFormat);
        session.DrawText("0", 12f, padTop + plotH - 10f, Color.FromArgb(150, 200, 210, 225), AxisLabelFormat);

        var cellW = Math.Max(1.6f, plotW / cols);
        var cellH = Math.Max(1.6f, plotH / rows);
        var logMax = Math.Log(1.0 + maxDensity);

        for (var ry = 0; ry < rows; ry++)
        {
            var drawY = padTop + plotH - ((ry + 1) / (float)rows) * plotH;
            var rowBase = ry * cols;
            for (var cx = 0; cx < cols; cx++)
            {
                var count = density[rowBase + cx];
                if (count == 0)
                {
                    continue;
                }

                var intensity = Math.Clamp(Math.Log(1.0 + count) / logMax, 0.22, 1.0);
                var alpha = (byte)(intensity * 245);
                var drawX = padLeft + (cx / (float)cols) * plotW;
                session.FillRectangle(drawX, drawY, cellW, cellH, Color.FromArgb(alpha, 110, 238, 175));
            }
        }
    }

    private static void RenderVectorscope(
        CanvasDrawingSession session,
        float width,
        float height,
        byte[] bgra,
        int frameWidth,
        int frameHeight)
    {
        var centerX = width / 2f;
        var centerY = height / 2f;
        var radius = Math.Max(16f, Math.Min(width, height) / 2f - 18f);

        // Outer graticule and axes
        var gridColor = Color.FromArgb(75, 255, 255, 255);
        session.DrawCircle(centerX, centerY, radius, gridColor, 1.2f);
        session.DrawCircle(centerX, centerY, radius * 0.75f, Color.FromArgb(38, 255, 255, 255), 1f);
        session.DrawCircle(centerX, centerY, radius * 0.5f, Color.FromArgb(38, 255, 255, 255), 1f);
        session.DrawLine(centerX - radius, centerY, centerX + radius, centerY, gridColor, 1f);
        session.DrawLine(centerX, centerY - radius, centerX, centerY + radius, gridColor, 1f);

        // BT.709 75% color bar target markers (R, Mg, B, Cy, G, Yl)
        DrawVectorTarget(session, centerX, centerY, radius, -0.1146, 0.5000, "R", Color.FromArgb(200, 255, 95, 95));
        DrawVectorTarget(session, centerX, centerY, radius, 0.3854, 0.4542, "Mg", Color.FromArgb(200, 240, 110, 255));
        DrawVectorTarget(session, centerX, centerY, radius, 0.5000, -0.0458, "B", Color.FromArgb(200, 95, 165, 255));
        DrawVectorTarget(session, centerX, centerY, radius, 0.1146, -0.5000, "Cy", Color.FromArgb(200, 85, 235, 245));
        DrawVectorTarget(session, centerX, centerY, radius, -0.3854, -0.4542, "G", Color.FromArgb(200, 90, 235, 130));
        DrawVectorTarget(session, centerX, centerY, radius, -0.5000, 0.0458, "Yl", Color.FromArgb(200, 250, 230, 85));

        const int grid = 160;
        var density = new int[grid * grid];
        var sumR = new int[grid * grid];
        var sumG = new int[grid * grid];
        var sumB = new int[grid * grid];
        var stepX = Math.Max(1, frameWidth / 300);
        var stepY = Math.Max(1, frameHeight / 200);
        var maxDensity = 1;

        for (var y = 0; y < frameHeight; y += stepY)
        {
            var rowOffset = y * frameWidth * 4;
            for (var x = 0; x < frameWidth; x += stepX)
            {
                var i = rowOffset + x * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];

                // BT.709 normalized Cb (U) & Cr (V) in [-0.5, 0.5]
                var rf = r / 255.0;
                var gf = g / 255.0;
                var bf = b / 255.0;
                var cb = -0.1146 * rf - 0.3854 * gf + 0.5 * bf;
                var cr = 0.5 * rf - 0.4542 * gf - 0.0458 * bf;

                var gx = Math.Clamp((int)((cb + 0.5) * (grid - 1)), 0, grid - 1);
                var gy = Math.Clamp((int)((0.5 - cr) * (grid - 1)), 0, grid - 1);
                var idx = gy * grid + gx;
                var count = ++density[idx];
                sumR[idx] += r;
                sumG[idx] += g;
                sumB[idx] += b;
                if (count > maxDensity)
                {
                    maxDensity = count;
                }
            }
        }

        var logMax = Math.Log(1.0 + maxDensity);
        var dotSize = Math.Max(2.0f, (radius * 2f) / grid);
        for (var gy = 0; gy < grid; gy++)
        {
            var rowBase = gy * grid;
            var py = centerY + (((gy / (float)(grid - 1)) - 0.5f) * 2f) * radius;
            for (var gx = 0; gx < grid; gx++)
            {
                var idx = rowBase + gx;
                var count = density[idx];
                if (count == 0)
                {
                    continue;
                }

                var px = centerX + (((gx / (float)(grid - 1)) - 0.5f) * 2f) * radius;
                var intensity = Math.Clamp(Math.Log(1.0 + count) / logMax, 0.25, 1.0);
                var alpha = (byte)(intensity * 245);
                var avgR = (byte)Math.Clamp(sumR[idx] / count + 45, 75, 255);
                var avgG = (byte)Math.Clamp(sumG[idx] / count + 45, 75, 255);
                var avgB = (byte)Math.Clamp(sumB[idx] / count + 45, 75, 255);
                session.FillRectangle(px - dotSize / 2f, py - dotSize / 2f, dotSize, dotSize, Color.FromArgb(alpha, avgR, avgG, avgB));
            }
        }
    }

    private static void DrawVectorTarget(
        CanvasDrawingSession session,
        float centerX,
        float centerY,
        float radius,
        double cb,
        double cr,
        string label,
        Color color)
    {
        var tx = centerX + (float)(cb * 1.5 * radius);
        var ty = centerY - (float)(cr * 1.5 * radius);
        session.DrawRectangle(tx - 5f, ty - 5f, 10f, 10f, color, 1.1f);
        session.DrawText(label, tx + 7f, ty - 7f, color, AxisLabelFormat);
    }
}

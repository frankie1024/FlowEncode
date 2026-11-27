using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
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

        // Subsample so even 4K frames compute in < 2ms.
        var stepX = Math.Max(1, frameWidth / 320);
        var stepY = Math.Max(1, frameHeight / 180);

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

        var padX = 8f;
        var padY = 8f;
        var plotW = Math.Max(1f, width - padX * 2f);
        var plotH = Math.Max(1f, height - padY * 2f);

        // Legal range guide lines (16 & 235)
        var x16 = padX + (16f / 255f) * plotW;
        var x235 = padX + (235f / 255f) * plotW;
        var guideColor = Color.FromArgb(72, 255, 255, 255);
        session.DrawLine(x16, padY, x16, padY + plotH, guideColor, 1f);
        session.DrawLine(x235, padY, x235, padY + plotH, guideColor, 1f);

        // Horizontal grid lines
        for (var g = 1; g <= 3; g++)
        {
            var gy = padY + plotH * (g / 4f);
            session.DrawLine(padX, gy, padX + plotW, gy, Color.FromArgb(35, 255, 255, 255), 1f);
        }

        DrawHistogramCurve(session, histB, peak, padX, padY, plotW, plotH, Color.FromArgb(140, 80, 155, 255), fill: false);
        DrawHistogramCurve(session, histG, peak, padX, padY, plotW, plotH, Color.FromArgb(140, 80, 225, 130), fill: false);
        DrawHistogramCurve(session, histR, peak, padX, padY, plotW, plotH, Color.FromArgb(140, 255, 95, 95), fill: false);
        DrawHistogramCurve(session, histY, peak, padX, padY, plotW, plotH, Color.FromArgb(170, 235, 240, 248), fill: true);
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
            session.FillGeometry(geometry, Color.FromArgb(52, color.R, color.G, color.B));
            session.DrawGeometry(geometry, color, 1.15f);
        }
        else
        {
            builder.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(builder);
            session.DrawGeometry(geometry, color, 1.1f);
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
        var padX = 8f;
        var padY = 8f;
        var plotW = Math.Max(1f, width - padX * 2f);
        var plotH = Math.Max(1f, height - padY * 2f);

        const int cols = 160;
        const int rows = 128;
        var density = new int[cols * rows];
        var stepX = Math.Max(1, frameWidth / cols);
        var stepY = Math.Max(1, frameHeight / 180);
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

        // Legal range horizontal guides (16 and 235)
        var y16 = padY + plotH * (1f - 16f / 255f);
        var y235 = padY + plotH * (1f - 235f / 255f);
        session.DrawLine(padX, y16, padX + plotW, y16, Color.FromArgb(90, 90, 185, 255), 1f);
        session.DrawLine(padX, y235, padX + plotW, y235, Color.FromArgb(90, 255, 105, 105), 1f);

        var cellW = Math.Max(1.2f, plotW / cols);
        var cellH = Math.Max(1.2f, plotH / rows);
        var logMax = Math.Log(1.0 + maxDensity);

        for (var ry = 0; ry < rows; ry++)
        {
            var drawY = padY + plotH - ((ry + 1) / (float)rows) * plotH;
            var rowBase = ry * cols;
            for (var cx = 0; cx < cols; cx++)
            {
                var count = density[rowBase + cx];
                if (count == 0)
                {
                    continue;
                }

                var intensity = Math.Clamp(Math.Log(1.0 + count) / logMax, 0.15, 1.0);
                var alpha = (byte)(intensity * 235);
                var drawX = padX + (cx / (float)cols) * plotW;
                session.FillRectangle(drawX, drawY, cellW, cellH, Color.FromArgb(alpha, 120, 230, 175));
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
        var radius = Math.Max(12f, Math.Min(width, height) / 2f - 12f);

        // Outer graticule and axes
        var gridColor = Color.FromArgb(65, 255, 255, 255);
        session.DrawCircle(centerX, centerY, radius, gridColor, 1f);
        session.DrawCircle(centerX, centerY, radius * 0.5f, Color.FromArgb(35, 255, 255, 255), 1f);
        session.DrawLine(centerX - radius, centerY, centerX + radius, centerY, gridColor, 1f);
        session.DrawLine(centerX, centerY - radius, centerX, centerY + radius, gridColor, 1f);

        const int grid = 128;
        var density = new int[grid * grid];
        var sumR = new int[grid * grid];
        var sumG = new int[grid * grid];
        var sumB = new int[grid * grid];
        var stepX = Math.Max(1, frameWidth / 240);
        var stepY = Math.Max(1, frameHeight / 160);
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
        var dotSize = Math.Max(1.5f, (radius * 2f) / grid);
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
                var avgR = (byte)Math.Clamp(sumR[idx] / count + 40, 0, 255);
                var avgG = (byte)Math.Clamp(sumG[idx] / count + 40, 0, 255);
                var avgB = (byte)Math.Clamp(sumB[idx] / count + 40, 0, 255);
                session.FillRectangle(px - dotSize / 2f, py - dotSize / 2f, dotSize, dotSize,
                    Color.FromArgb((byte)(intensity * 235), avgR, avgG, avgB));
            }
        }

        // Primary 75% saturation target markers (R, Mg, B, Cy, G, Yl)
        DrawChromaTarget(session, centerX, centerY, radius, -0.1146, 0.5000, Color.FromArgb(190, 255, 85, 85));
        DrawChromaTarget(session, centerX, centerY, radius, 0.3854, 0.4542, Color.FromArgb(190, 255, 95, 220));
        DrawChromaTarget(session, centerX, centerY, radius, 0.5000, -0.0458, Color.FromArgb(190, 95, 155, 255));
        DrawChromaTarget(session, centerX, centerY, radius, 0.1146, -0.5000, Color.FromArgb(190, 85, 235, 245));
        DrawChromaTarget(session, centerX, centerY, radius, -0.3854, -0.4542, Color.FromArgb(190, 85, 235, 120));
        DrawChromaTarget(session, centerX, centerY, radius, -0.5000, 0.0458, Color.FromArgb(190, 250, 230, 85));
    }

    private static void DrawChromaTarget(
        CanvasDrawingSession session,
        float cx,
        float cy,
        float radius,
        double cb,
        double cr,
        Color color)
    {
        var x = cx + (float)(cb * 1.5 * radius);
        var y = cy - (float)(cr * 1.5 * radius);
        session.DrawRectangle(x - 3.5f, y - 3.5f, 7f, 7f, color, 1.2f);
    }
}

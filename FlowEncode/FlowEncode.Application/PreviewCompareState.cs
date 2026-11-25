using System;
using System.Collections.Generic;
using System.Linq;

namespace FlowEncode.Application;

public enum PreviewCompareMode
{
    Single,
    Wipe,
    SideBySide,
    Difference
}

public enum PreviewDifferenceGain
{
    X1,
    X4,
    X16,
    Boost
}

public enum PreviewCompareSizeAlignment
{
    Center1To1,
    UniformFit
}

public readonly record struct PreviewCompareAlignedRect(
    double X,
    double Y,
    double Width,
    double Height);

public readonly record struct PreviewCompareAlignedLayout(
    int CanvasWidth,
    int CanvasHeight,
    PreviewCompareAlignedRect RectA,
    PreviewCompareAlignedRect RectB);

public static class PreviewCompareCoordinator
{
    private static readonly PreviewCompareMode[] ModeCycle =
    [
        PreviewCompareMode.Single,
        PreviewCompareMode.Wipe,
        PreviewCompareMode.SideBySide,
        PreviewCompareMode.Difference
    ];

    public static bool CanCompare(int outputCount) => outputCount >= 2;

    public static PreviewCompareMode NormalizeMode(PreviewCompareMode mode, int outputCount) =>
        CanCompare(outputCount) ? mode : PreviewCompareMode.Single;

    public static PreviewCompareMode CycleMode(
        PreviewCompareMode currentMode,
        int outputCount,
        bool reverse = false)
    {
        if (!CanCompare(outputCount))
        {
            return PreviewCompareMode.Single;
        }

        var currentIndex = Array.IndexOf(ModeCycle, currentMode);
        if (currentIndex < 0)
        {
            return PreviewCompareMode.Wipe;
        }

        var delta = reverse ? -1 : 1;
        var nextIndex = (currentIndex + delta + ModeCycle.Length) % ModeCycle.Length;
        return ModeCycle[nextIndex];
    }

    public static int? ResolveSecondaryOutputIndex(
        IReadOnlyList<VapourSynthPreviewOutputInfo> outputs,
        int primaryOutputIndex,
        int? preferredSecondaryOutputIndex)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        if (outputs.Count < 2)
        {
            return null;
        }

        if (preferredSecondaryOutputIndex is int preferred
            && preferred != primaryOutputIndex
            && outputs.Any(output => output.Index == preferred))
        {
            return preferred;
        }

        foreach (var output in outputs)
        {
            if (output.Index != primaryOutputIndex)
            {
                return output.Index;
            }
        }

        return null;
    }

    public static (int PrimaryIndex, int? SecondaryIndex) SwapOutputs(
        int primaryOutputIndex,
        int? secondaryOutputIndex)
    {
        if (secondaryOutputIndex is not int secondary || secondary == primaryOutputIndex)
        {
            return (primaryOutputIndex, secondaryOutputIndex);
        }

        return (secondary, primaryOutputIndex);
    }

    public static int ResolveSecondaryFrameNumber(
        VapourSynthPreviewOutputInfo primaryOutput,
        VapourSynthPreviewOutputInfo secondaryOutput,
        int primaryFrameNumber,
        string? outputSyncMode,
        bool isTimelineTimeMode)
    {
        ArgumentNullException.ThrowIfNull(primaryOutput);
        ArgumentNullException.ThrowIfNull(secondaryOutput);

        var clampedPrimary = Math.Clamp(primaryFrameNumber, 0, Math.Max(0, primaryOutput.TotalFrames - 1));
        var maxSecondaryFrame = Math.Max(0, secondaryOutput.TotalFrames - 1);

        var useTimestamp = string.Equals(outputSyncMode, "time", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(outputSyncMode, "timeline", StringComparison.OrdinalIgnoreCase) && isTimelineTimeMode);

        if (!useTimestamp)
        {
            return Math.Clamp(clampedPrimary, 0, maxSecondaryFrame);
        }

        var primaryFps = ResolveFps(primaryOutput);
        var secondaryFps = ResolveFps(secondaryOutput);
        var timestampSeconds = clampedPrimary / primaryFps;
        var mappedFrame = (int)Math.Round(timestampSeconds * secondaryFps);
        return Math.Clamp(mappedFrame, 0, maxSecondaryFrame);
    }

    public static double ClampSplitRatio(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            return 0.5;
        }

        return Math.Clamp(ratio, 0.0, 1.0);
    }

    public static double ComputeSplitRatioFromViewportX(
        double pointerViewportX,
        double imageLeftInViewport,
        double horizontalOffset,
        double imageWidth)
    {
        if (imageWidth <= 0 || double.IsNaN(imageWidth) || double.IsInfinity(imageWidth))
        {
            return 0.5;
        }

        var imageLocalX = pointerViewportX - imageLeftInViewport + Math.Max(0.0, horizontalOffset);
        return ClampSplitRatio(imageLocalX / imageWidth);
    }

    public static double ComputeViewportDividerX(
        double splitRatio,
        double imageLeftInViewport,
        double horizontalOffset,
        double imageWidth)
    {
        if (imageWidth <= 0 || double.IsNaN(imageWidth) || double.IsInfinity(imageWidth))
        {
            return imageLeftInViewport;
        }

        return imageLeftInViewport + ClampSplitRatio(splitRatio) * imageWidth - Math.Max(0.0, horizontalOffset);
    }

    public static double ComputeCenteredViewportSplitRatio(
        double viewportWidth,
        double imageLeftInViewport,
        double horizontalOffset,
        double imageWidth)
    {
        if (viewportWidth <= 0 || imageWidth <= 0)
        {
            return 0.5;
        }

        return ComputeSplitRatioFromViewportX(
            viewportWidth / 2.0,
            imageLeftInViewport,
            horizontalOffset,
            imageWidth);
    }

    public static double ComputeEffectiveViewportWidth(
        double totalViewportWidth,
        PreviewCompareMode mode,
        double splitterGapWidth = 8.0)
    {
        var normalizedTotal = Math.Max(0.0, totalViewportWidth);
        if (mode != PreviewCompareMode.SideBySide)
        {
            return normalizedTotal;
        }

        return Math.Max(1.0, (normalizedTotal - Math.Max(0.0, splitterGapWidth)) / 2.0);
    }

    public static bool AreDimensionsEqual(
        VapourSynthPreviewOutputInfo left,
        VapourSynthPreviewOutputInfo right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.Width == right.Width && left.Height == right.Height;
    }

    public static PreviewCompareSizeAlignment RecommendSizeAlignment(
        int widthA,
        int heightA,
        int widthB,
        int heightB)
    {
        widthA = Math.Max(1, widthA);
        heightA = Math.Max(1, heightA);
        widthB = Math.Max(1, widthB);
        heightB = Math.Max(1, heightB);

        if (widthA == widthB || heightA == heightB)
        {
            return PreviewCompareSizeAlignment.Center1To1;
        }

        var sameDirection = (widthA > widthB) == (heightA > heightB);
        var scaleX = Math.Max(widthA, widthB) / (double)Math.Min(widthA, widthB);
        var scaleY = Math.Max(heightA, heightB) / (double)Math.Min(heightA, heightB);
        if (sameDirection && scaleX >= 1.15 && scaleY >= 1.15)
        {
            return PreviewCompareSizeAlignment.UniformFit;
        }

        return PreviewCompareSizeAlignment.Center1To1;
    }

    public static PreviewCompareAlignedLayout ComputeAlignedCanvasLayout(
        int widthA,
        int heightA,
        int widthB,
        int heightB,
        PreviewCompareSizeAlignment alignment)
    {
        widthA = Math.Max(1, widthA);
        heightA = Math.Max(1, heightA);
        widthB = Math.Max(1, widthB);
        heightB = Math.Max(1, heightB);

        var canvasWidth = Math.Max(widthA, widthB);
        var canvasHeight = Math.Max(heightA, heightB);

        if (alignment == PreviewCompareSizeAlignment.UniformFit)
        {
            return new PreviewCompareAlignedLayout(
                canvasWidth,
                canvasHeight,
                ComputeUniformFitRect(widthA, heightA, canvasWidth, canvasHeight),
                ComputeUniformFitRect(widthB, heightB, canvasWidth, canvasHeight));
        }

        return new PreviewCompareAlignedLayout(
            canvasWidth,
            canvasHeight,
            new PreviewCompareAlignedRect(
                (canvasWidth - widthA) / 2.0,
                (canvasHeight - heightA) / 2.0,
                widthA,
                heightA),
            new PreviewCompareAlignedRect(
                (canvasWidth - widthB) / 2.0,
                (canvasHeight - heightB) / 2.0,
                widthB,
                heightB));
    }

    private static PreviewCompareAlignedRect ComputeUniformFitRect(
        int width,
        int height,
        int canvasWidth,
        int canvasHeight)
    {
        var scale = Math.Min(canvasWidth / (double)width, canvasHeight / (double)height);
        var scaledWidth = width * scale;
        var scaledHeight = height * scale;
        return new PreviewCompareAlignedRect(
            (canvasWidth - scaledWidth) / 2.0,
            (canvasHeight - scaledHeight) / 2.0,
            scaledWidth,
            scaledHeight);
    }

    private static double ResolveFps(VapourSynthPreviewOutputInfo output)
    {
        return output.FpsNumerator > 0 && output.FpsDenominator > 0
            ? output.FpsNumerator / (double)output.FpsDenominator
            : 24.0;
    }
}

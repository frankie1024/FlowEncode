using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FlowEncode.Application;

public sealed record PreviewWorstFrameSamplingPlan(
    IReadOnlyList<int> SampleFrames,
    int StartFrame,
    int EndFrame,
    double AppliedSkipMinutes,
    bool AdjustedForShortVideo);

public sealed record PreviewWorstFrameEntry(
    int FrameNumber,
    int SecondaryFrameNumber,
    double Score,
    string? FrameType);

public sealed record PreviewWorstFrameScanResult(
    string MetricUsed,
    string BackendUsed,
    int SampledCount,
    double MedianScore,
    double P10Score,
    double MinScore,
    double ElapsedMs,
    IReadOnlyList<PreviewWorstFrameEntry> WorstFrames);

public sealed record PreviewAutoCropResult(
    int Left,
    int Top,
    int Right,
    int Bottom,
    int SampledFrames,
    double ElapsedMs,
    PreviewRepairSuggestion? CropRepair = null);

public sealed record PreviewSceneRange(
    int StartFrame,
    int EndFrame,
    string Label = "",
    double BitrateFactor = 1.2);

public static class PreviewWorstFramePlanner
{
    public static PreviewWorstFrameSamplingPlan BuildSamplingPlan(
        int totalFrames,
        double fps,
        double requestedSkipMinutes = 10.0,
        int sampleCount = 80)
    {
        if (totalFrames <= 0)
        {
            return new PreviewWorstFrameSamplingPlan([], 0, 0, 0, false);
        }

        var safeFps = fps > 0.01 && !double.IsNaN(fps) && !double.IsInfinity(fps) ? fps : 24.0;
        var totalSeconds = totalFrames / safeFps;
        var totalMinutes = totalSeconds / 60.0;
        var requestedSkip = Math.Max(0.0, requestedSkipMinutes);
        var adjusted = false;
        double appliedSkipMinutes;

        if (requestedSkip <= 0.0)
        {
            appliedSkipMinutes = 0.0;
        }
        else if (totalMinutes <= 25.0 || requestedSkip * 2.0 >= totalMinutes * 0.6)
        {
            // Short video safeguard: avoid skipping the entire clip (e.g., 24-min anime episodes).
            appliedSkipMinutes = Math.Round(totalMinutes * 0.10, 2);
            adjusted = true;
        }
        else
        {
            appliedSkipMinutes = requestedSkip;
        }

        var skipFrames = (int)Math.Round(appliedSkipMinutes * 60.0 * safeFps);
        var startFrame = Math.Clamp(skipFrames, 0, Math.Max(0, totalFrames - 1));
        var endFrame = Math.Clamp(totalFrames - 1 - skipFrames, startFrame, Math.Max(0, totalFrames - 1));

        var desiredCount = Math.Clamp(sampleCount, 1, 500);
        var span = endFrame - startFrame + 1;
        var actualCount = Math.Min(desiredCount, span);

        var frames = new List<int>(actualCount);
        var seen = new HashSet<int>();
        for (var i = 0; i < actualCount; i++)
        {
            var t = actualCount == 1 ? 0.5 : i / (double)(actualCount - 1);
            var frame = Math.Clamp((int)Math.Round(startFrame + (endFrame - startFrame) * t), 0, totalFrames - 1);
            if (seen.Add(frame))
            {
                frames.Add(frame);
            }
        }

        return new PreviewWorstFrameSamplingPlan(
            frames,
            startFrame,
            endFrame,
            appliedSkipMinutes,
            adjusted);
    }
}

public static class PreviewSceningFormatter
{
    public static string FormatReplaceRanges(
        IReadOnlyList<PreviewSceneRange> ranges,
        string clipVariable = "clip",
        string filteredVariable = "flt")
    {
        if (ranges.Count == 0)
        {
            return $"# No scene ranges marked for {clipVariable}";
        }

        var tuples = ranges
            .OrderBy(static r => r.StartFrame)
            .Select(static r => $"({r.StartFrame}, {r.EndFrame})");
        return $"{clipVariable} = replace_ranges({clipVariable}, {filteredVariable}, [{string.Join(", ", tuples)}])";
    }

    public static string FormatEncoderZones(IReadOnlyList<PreviewSceneRange> ranges)
    {
        if (ranges.Count == 0)
        {
            return string.Empty;
        }

        var segments = ranges
            .OrderBy(static r => r.StartFrame)
            .Select(static r => string.Create(
                CultureInfo.InvariantCulture,
                $"{r.StartFrame},{r.EndFrame},b={r.BitrateFactor:0.##}"));
        return "--zones " + string.Join("/", segments);
    }

    public static string FormatAv1anZones(IReadOnlyList<PreviewSceneRange> ranges)
    {
        if (ranges.Count == 0)
        {
            return string.Empty;
        }

        var lines = ranges
            .OrderBy(static r => r.StartFrame)
            .Select(static r => $"{r.StartFrame} {r.EndFrame} -q 18");
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatCropAndBbmodSnippet(
        int left,
        int top,
        int right,
        int bottom,
        string clipVariable = "clip")
    {
        var lines = new List<string>
        {
            "import awsmfunc as awf"
        };

        if (left > 0 || top > 0 || right > 0 || bottom > 0)
        {
            lines.Add($"{clipVariable} = core.std.CropRel({clipVariable}, left={left}, right={right}, top={top}, bottom={bottom})");
        }

        lines.Add($"{clipVariable} = awf.bbmod({clipVariable}, top=2, bottom=2, left=2, right=2, thresh=32, blur=20)");
        return string.Join(Environment.NewLine, lines);
    }
}

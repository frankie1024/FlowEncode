using System.Globalization;
using System.Text.RegularExpressions;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class EncodingProgressParser
{
    private static readonly Regex X26xProgressRegex = new(@"(?<progress>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex X265PipeMetricsRegex = new(@"^\[\s*(?<progress>\d{1,3}(?:\.\d+)?)\s*%\]\s+(?<current>\d+)\s*\/\s*(?<total>\d+)\s+Frames\s+@\s+(?<fps>\d+(?:\.\d+)?)\s+FPS\s+\|\s+(?<bitrate>\d+(?:\.\d+)?)\s+kb\/s\s+\|\s+(?<eta>\d+:\d{2}:\d{2})(?:\s+\[(?<remainingeta>-?\d+:\d{2}:\d{2})\])?\s+\|\s+(?<currentsize>\d+(?:\.\d+)?)\s*(?<currentunit>[KMGTP]?B)(?:\s+\[(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B)\])?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xMetricsRegex = new(@"\[?\s*(?<progress>\d{1,3}(?:\.\d+)?)\s*%\]?\s+(?:(?<current>\d+)\s*\/\s*(?<total>\d+)\s+frames|(?<framesonly>\d+)\s+frames:)\s*,?\s*(?<fps>\d+(?:\.\d+)?)\s+fps,\s*(?<bitrate>\d+(?:\.\d+)?)\s+kb/s(?:,\s*eta\s+(?<eta>\d+:\d{2}:\d{2}))?(?:,\s*est\.\s*file\s*size\s+(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xFrameMetricsRegex = new(@"(?<current>\d+)\s+frames:\s*(?<fps>\d+(?:\.\d+)?)\s+fps,\s*(?<bitrate>\d+(?:\.\d+)?)\s+kb/s", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xPipeFrameMetricsRegex = new(@"^(?:x26[45]\s+)?(?<current>\d+)\s+frames\s+@\s+(?<fps>\d+(?:\.\d+)?)\s+fps\s*\|\s*(?<bitrate>\d+(?:\.\d+)?)\s+kb/s\s*\|\s*(?<currentsize>\d+(?:\.\d+)?)\s*(?<currentunit>[KMGTP]?B)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xEncodedSummaryRegex = new(@"^encoded\s+(?<current>\d+)\s+frames(?:,\s+(?<fps>\d+(?:\.\d+)?)\s+fps|\s+in\s+\d+(?:\.\d+)?s\s+\((?<fpsparenthesized>\d+(?:\.\d+)?)\s+fps\)),\s+(?<bitrate>\d+(?:\.\d+)?)\s+kb/s(?:,\s*Avg\s+QP:\s*\d+(?:\.\d+)?)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xFrameRatioRegex = new(@"(?<current>\d+)\s*\/\s*(?<total>\d+)\s+frames?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xFrameEqualsRegex = new(@"\bframe=\s*(?<current>\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xLooseFpsRegex = new(@"(?<fps>\d+(?:\.\d+)?)\s*fps\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xLooseBitrateRegex = new(@"(?<bitrate>\d+(?:\.\d+)?)\s*kb(?:\/s|ps)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xLooseEtaRegex = new(@"(?:eta|time)\s*:?\s*(?<eta>-?\d+:\d{2}:\d{2})(?:\s*\[(?<remainingeta>-?\d+:\d{2}:\d{2})\])?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xLooseSizeRegex = new(@"(?:est\.\s*file\s*size|size)\s*:?\s*(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex X26xBracketedSizeRegex = new(@"\[(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LsmasLwiIndexProgressRegex = new(@"^Creating lwi index file\s+(?<progress>\d{1,3})%$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BestSourceIndexProgressRegex = new(@"^(?:Information:\s+)?VideoSource\s+track\s+#\d+\s+index\s+progress\s+(?<progress>\d{1,3})%$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtFrameRegex = new(@"Encoding\s+frame\s+(?<frame>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtOutputRegex = new(@"Output\s+(?<frame>\d+)\s+frames", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusPrefixRegex = new(
        @"^Encoding:\s*(?<current>\d+)\s*\/\s*(?<total>\d+)\s+Frames?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusFpsRegex = new(
        @"@\s*(?<fps>\d+(?:\.\d+)?)\s+fps\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusBitrateRegex = new(
        @"\|\s*(?<bitrate>\d+(?:\.\d+)?)\s+kb(?:\/s|ps)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusTimeRegex = new(
        @"Time:\s*(?<elapsed>-?\d+:\d{2}:\d{2})(?:\s*\[(?<eta>-?\d+:\d{2}:\d{2})\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtStatusSizeRegex = new(
        @"Size:\s*(?<currentsize>\d+(?:\.\d+)?)\s*(?<currentunit>[KMGTP]?B)(?:\s*\[(?<size>\d+(?:\.\d+)?)\s*(?<unit>[KMGTP]?B)\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtLooseMetricsRegex = new(
        @"^Encoding:\s*(?<current>\d+)\s*\/\s*(?<total>\d+)\s+Frames?\b.*?(?<fps>\d+(?:\.\d+)?)\s+fps\b.*?(?<bitrate>\d+(?:\.\d+)?)\s+kb(?:\/s|ps)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SvtMetricsRegex = new(@"Encoding\s+frame\s+(?<current>\d+)\s+(?<bitrate>\d+(?:\.\d+)?)\s+kbps\s+(?<fps>\d+(?:\.\d+)?)\s+fps", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static EncodingProgressParseResult? ParseSnapshot(
        EncoderKind kind,
        long? totalFrames,
        double? sourceFramesPerSecond,
        string line)
    {
        var normalizedLine = EncoderConsoleLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalizedLine))
        {
            return null;
        }

        if (kind is EncoderKind.X264 or EncoderKind.X265)
        {
            var normalizedX26xLine = NormalizeX26xProgressPrefix(normalizedLine);

            var x265PipeMetricsMatch = X265PipeMetricsRegex.Match(normalizedX26xLine);
            if (x265PipeMetricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(x265PipeMetricsMatch.Groups["current"].Value);
                var parsedTotalFrames = ParseInvariantLong(x265PipeMetricsMatch.Groups["total"].Value) ?? totalFrames;
                var progress = TryBuildProgressFraction(
                    ParseInvariantDoubleNullable(x265PipeMetricsMatch.Groups["progress"].Value),
                    currentFrame,
                    parsedTotalFrames);
                var fps = ParseInvariantDoubleNullable(x265PipeMetricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(x265PipeMetricsMatch.Groups["bitrate"].Value);
                var eta = ParseEta(x265PipeMetricsMatch.Groups["remainingeta"].Value)
                    ?? ParseEta(x265PipeMetricsMatch.Groups["eta"].Value);
                var estimatedFileSizeBytes =
                    ParseSizeToBytes(x265PipeMetricsMatch.Groups["size"].Value, x265PipeMetricsMatch.Groups["unit"].Value)
                    ?? ParseSizeToBytes(x265PipeMetricsMatch.Groups["currentsize"].Value, x265PipeMetricsMatch.Groups["currentunit"].Value);

                return new EncodingProgressParseResult(
                    progress,
                    new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedFileSizeBytes));
            }

            var metricsMatch = X26xMetricsRegex.Match(normalizedX26xLine);
            if (metricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(metricsMatch.Groups["current"].Value)
                    ?? ParseInvariantLong(metricsMatch.Groups["framesonly"].Value);
                var parsedTotalFrames = ParseInvariantLong(metricsMatch.Groups["total"].Value) ?? totalFrames;
                var progress = TryBuildProgressFraction(
                    ParseInvariantDoubleNullable(metricsMatch.Groups["progress"].Value),
                    currentFrame,
                    parsedTotalFrames);
                var fps = ParseInvariantDoubleNullable(metricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(metricsMatch.Groups["bitrate"].Value);
                var eta = ParseEta(metricsMatch.Groups["eta"].Value);
                var estimatedFileSizeBytes = ParseSizeToBytes(metricsMatch.Groups["size"].Value, metricsMatch.Groups["unit"].Value);

                return new EncodingProgressParseResult(
                    progress,
                    new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedFileSizeBytes));
            }

            var frameMetricsMatch = X26xFrameMetricsRegex.Match(normalizedX26xLine);
            if (frameMetricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(frameMetricsMatch.Groups["current"].Value);
                var fps = ParseInvariantDoubleNullable(frameMetricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(frameMetricsMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, totalFrames);
                var eta = currentFrame.HasValue && totalFrames is > 0 && fps is > 0
                    ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (totalFrames.Value - currentFrame.Value) / fps.Value))
                    : null;
                var estimatedSizeBytes = totalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(totalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : null;

                return new EncodingProgressParseResult(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, totalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var pipeFrameMetricsMatch = X26xPipeFrameMetricsRegex.Match(normalizedX26xLine);
            if (pipeFrameMetricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(pipeFrameMetricsMatch.Groups["current"].Value);
                var fps = ParseInvariantDoubleNullable(pipeFrameMetricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(pipeFrameMetricsMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, totalFrames);
                var eta = currentFrame.HasValue && totalFrames is > 0 && fps is > 0
                    ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (totalFrames.Value - currentFrame.Value) / fps.Value))
                    : null;
                var estimatedSizeBytes = totalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(totalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : ParseSizeToBytes(pipeFrameMetricsMatch.Groups["currentsize"].Value, pipeFrameMetricsMatch.Groups["currentunit"].Value);

                return new EncodingProgressParseResult(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, totalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var encodedSummaryMatch = X26xEncodedSummaryRegex.Match(normalizedX26xLine);
            if (encodedSummaryMatch.Success)
            {
                var currentFrame = ParseInvariantLong(encodedSummaryMatch.Groups["current"].Value);
                var fps = ParseInvariantDoubleNullable(encodedSummaryMatch.Groups["fps"].Value)
                    ?? ParseInvariantDoubleNullable(encodedSummaryMatch.Groups["fpsparenthesized"].Value);
                var bitrate = ParseInvariantDoubleNullable(encodedSummaryMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, totalFrames);
                var estimatedSizeBytes = totalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(totalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : null;

                return new EncodingProgressParseResult(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, totalFrames, fps, bitrate, null, estimatedSizeBytes));
            }

            var looseSnapshot = TryParseLooseX26xMetrics(normalizedX26xLine, totalFrames, sourceFramesPerSecond);
            if (looseSnapshot is not null)
            {
                return looseSnapshot;
            }

            var match = X26xProgressRegex.Match(normalizedX26xLine);
            if (match.Success)
            {
                return new EncodingProgressParseResult(
                    Math.Clamp(ParseInvariantDouble(match.Groups["progress"].Value) / 100.0, 0.0, 1.0),
                    null);
            }
        }

        if (kind == EncoderKind.SvtAv1)
        {
            var statusPrefixMatch = SvtStatusPrefixRegex.Match(normalizedLine);
            if (statusPrefixMatch.Success)
            {
                var currentFrame = ParseInvariantLong(statusPrefixMatch.Groups["current"].Value);
                var parsedTotalFrames = ParseInvariantLong(statusPrefixMatch.Groups["total"].Value) ?? totalFrames;
                var fps = ParseInvariantDoubleNullable(SvtStatusFpsRegex.Match(normalizedLine).Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(SvtStatusBitrateRegex.Match(normalizedLine).Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, parsedTotalFrames);
                var statusTimeMatch = SvtStatusTimeRegex.Match(normalizedLine);
                var eta = ParseEta(statusTimeMatch.Groups["eta"].Value)
                    ?? (currentFrame.HasValue && parsedTotalFrames is > 0 && fps is > 0
                        ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (parsedTotalFrames.Value - currentFrame.Value) / fps.Value))
                        : null);
                var statusSizeMatch = SvtStatusSizeRegex.Match(normalizedLine);
                var estimatedSizeBytes =
                    ParseSizeToBytes(statusSizeMatch.Groups["size"].Value, statusSizeMatch.Groups["unit"].Value)
                    ?? ParseSizeToBytes(statusSizeMatch.Groups["currentsize"].Value, statusSizeMatch.Groups["currentunit"].Value)
                    ?? (parsedTotalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                        ? (long?)EstimateFileSizeBytes(parsedTotalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                        : null);

                return new EncodingProgressParseResult(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var looseMetricsMatch = SvtLooseMetricsRegex.Match(normalizedLine);
            if (looseMetricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(looseMetricsMatch.Groups["current"].Value);
                var parsedTotalFrames = ParseInvariantLong(looseMetricsMatch.Groups["total"].Value) ?? totalFrames;
                var fps = ParseInvariantDoubleNullable(looseMetricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(looseMetricsMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, parsedTotalFrames);
                var eta = currentFrame.HasValue && parsedTotalFrames is > 0 && fps is > 0
                    ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (parsedTotalFrames.Value - currentFrame.Value) / fps.Value))
                    : null;
                var estimatedSizeBytes = parsedTotalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(parsedTotalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : null;

                return new EncodingProgressParseResult(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var metricsMatch = SvtMetricsRegex.Match(normalizedLine);
            if (metricsMatch.Success)
            {
                var currentFrame = ParseInvariantLong(metricsMatch.Groups["current"].Value);
                var fps = ParseInvariantDoubleNullable(metricsMatch.Groups["fps"].Value);
                var bitrate = ParseInvariantDoubleNullable(metricsMatch.Groups["bitrate"].Value);
                var progressFraction = TryBuildProgressFraction(null, currentFrame, totalFrames);
                var eta = currentFrame.HasValue && totalFrames is > 0 && fps is > 0
                    ? (TimeSpan?)TimeSpan.FromSeconds(Math.Max(0, (totalFrames.Value - currentFrame.Value) / fps.Value))
                    : null;
                var estimatedSizeBytes = currentFrame.HasValue && totalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0
                    ? (long?)EstimateFileSizeBytes(totalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value)
                    : null;

                return new EncodingProgressParseResult(
                    progressFraction,
                    new EncodingProgressSnapshot(currentFrame, totalFrames, fps, bitrate, eta, estimatedSizeBytes));
            }

            var match = SvtFrameRegex.Match(normalizedLine);
            if (!match.Success)
            {
                match = SvtOutputRegex.Match(normalizedLine);
            }

            if (match.Success
                && totalFrames is > 0
                && long.TryParse(match.Groups["frame"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
            {
                return new EncodingProgressParseResult(
                    Math.Clamp(frame / (double)totalFrames.Value, 0.0, 1.0),
                    new EncodingProgressSnapshot(frame, totalFrames, null, null, null, null));
            }
        }

        return null;
    }

    internal static int? ParseSourcePreparationProgressPercent(string line)
    {
        var normalizedLine = EncoderConsoleLineNormalizer.Normalize(line);
        if (string.IsNullOrWhiteSpace(normalizedLine))
        {
            return null;
        }

        var match = LsmasLwiIndexProgressRegex.Match(normalizedLine);
        if (!match.Success)
        {
            match = BestSourceIndexProgressRegex.Match(normalizedLine);
            if (!match.Success)
            {
                return null;
            }
        }

        return int.TryParse(match.Groups["progress"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var progress)
            ? Math.Clamp(progress, 0, 100)
            : null;
    }

    private static EncodingProgressParseResult? TryParseLooseX26xMetrics(
        string line,
        long? totalFrames,
        double? sourceFramesPerSecond)
    {
        if (!(line.Contains('%')
              || line.Contains("frame=", StringComparison.OrdinalIgnoreCase)
              || line.Contains("frames", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (!line.Contains("fps", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var explicitPercent = ParseInvariantDoubleNullable(X26xProgressRegex.Match(line).Groups["progress"].Value);

        var frameRatioMatch = X26xFrameRatioRegex.Match(line);
        long? currentFrame = null;
        long? parsedTotalFrames = totalFrames;
        if (frameRatioMatch.Success)
        {
            currentFrame = ParseInvariantLong(frameRatioMatch.Groups["current"].Value);
            parsedTotalFrames = ParseInvariantLong(frameRatioMatch.Groups["total"].Value) ?? totalFrames;
        }
        else
        {
            var frameEqualsMatch = X26xFrameEqualsRegex.Match(line);
            if (frameEqualsMatch.Success)
            {
                currentFrame = ParseInvariantLong(frameEqualsMatch.Groups["current"].Value);
            }
        }

        var fps = ParseInvariantDoubleNullable(X26xLooseFpsRegex.Match(line).Groups["fps"].Value);
        var bitrate = ParseInvariantDoubleNullable(X26xLooseBitrateRegex.Match(line).Groups["bitrate"].Value);

        var etaMatch = X26xLooseEtaRegex.Match(line);
        var eta = ParseEta(etaMatch.Groups["remainingeta"].Value)
            ?? ParseEta(etaMatch.Groups["eta"].Value);

        long? estimatedFileSizeBytes = null;
        var sizeMatch = X26xLooseSizeRegex.Match(line);
        if (sizeMatch.Success)
        {
            estimatedFileSizeBytes = ParseSizeToBytes(sizeMatch.Groups["size"].Value, sizeMatch.Groups["unit"].Value);
        }

        if (!estimatedFileSizeBytes.HasValue)
        {
            var bracketedSizeMatches = X26xBracketedSizeRegex.Matches(line);
            if (bracketedSizeMatches.Count > 0)
            {
                var lastSize = bracketedSizeMatches[bracketedSizeMatches.Count - 1];
                estimatedFileSizeBytes = ParseSizeToBytes(lastSize.Groups["size"].Value, lastSize.Groups["unit"].Value);
            }
        }

        if (!estimatedFileSizeBytes.HasValue && parsedTotalFrames is > 0 && sourceFramesPerSecond is > 0 && bitrate is > 0)
        {
            estimatedFileSizeBytes = EstimateFileSizeBytes(parsedTotalFrames.Value, sourceFramesPerSecond.Value, bitrate.Value);
        }

        var progressFraction = TryBuildProgressFraction(explicitPercent, currentFrame, parsedTotalFrames);

        if (!progressFraction.HasValue
            && !currentFrame.HasValue
            && !fps.HasValue
            && !bitrate.HasValue
            && !eta.HasValue
            && !estimatedFileSizeBytes.HasValue)
        {
            return null;
        }

        return new EncodingProgressParseResult(
            progressFraction,
            new EncodingProgressSnapshot(currentFrame, parsedTotalFrames, fps, bitrate, eta, estimatedFileSizeBytes));
    }

    private static string NormalizeX26xProgressPrefix(string line)
    {
        if (line.StartsWith("x264 ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("x265 ", StringComparison.OrdinalIgnoreCase))
        {
            var bracketIndex = line.IndexOf('[');
            if (bracketIndex > 0)
            {
                return line[bracketIndex..].TrimStart();
            }
        }

        return line;
    }

    private static double ParseInvariantDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
    }

    private static double? ParseInvariantDoubleNullable(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? ParseInvariantLong(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static TimeSpan? ParseEta(string value)
    {
        var normalized = value?.Trim().TrimStart('-');
        return TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var eta)
            ? eta
            : null;
    }

    private static long? ParseSizeToBytes(string sizeValue, string unit)
    {
        if (string.IsNullOrWhiteSpace(sizeValue))
        {
            return null;
        }

        var size = ParseInvariantDouble(sizeValue);
        var multiplier = unit.Trim().ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "TB" => 1024d * 1024d * 1024d * 1024d,
            _ => 1d
        };

        return (long)Math.Round(size * multiplier, MidpointRounding.AwayFromZero);
    }

    private static long EstimateFileSizeBytes(long totalFrames, double sourceFramesPerSecond, double bitrateKbps)
    {
        var durationSeconds = totalFrames / sourceFramesPerSecond;
        var bytes = durationSeconds * (bitrateKbps * 1000d / 8d);
        return (long)Math.Round(bytes, MidpointRounding.AwayFromZero);
    }

    private static double? TryBuildProgressFraction(double? explicitPercent, long? currentFrame, long? totalFrames)
    {
        if (explicitPercent.HasValue)
        {
            return Math.Clamp(explicitPercent.Value / 100.0, 0.0, 1.0);
        }

        if (currentFrame.HasValue && totalFrames is > 0)
        {
            return Math.Clamp(currentFrame.Value / (double)totalFrames.Value, 0.0, 1.0);
        }

        return null;
    }
}

internal sealed record EncodingProgressParseResult(
    double? ProgressFraction,
    EncodingProgressSnapshot? Snapshot);

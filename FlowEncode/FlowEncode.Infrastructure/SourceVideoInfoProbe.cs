using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed partial class SourceVideoInfoProbe
{
    private readonly ExternalToolLocator _toolLocator;
    private readonly ConcurrentDictionary<SourceVideoInfoCacheKey, Lazy<SourceVideoInfo?>> _cache = new();

    public SourceVideoInfoProbe(ExternalToolLocator toolLocator)
    {
        _toolLocator = toolLocator;
    }

    public SourceVideoInfo? Probe(
        string sourcePath,
        InputPipelineKind pipelineKind,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowCached = false)
    {
        if (allowCached && TryBuildCacheKey(sourcePath, pipelineKind) is { } cacheKey)
        {
            return ProbeCached(cacheKey, sourcePath, pipelineKind, progress, cancellationToken);
        }

        return ProbeCore(sourcePath, pipelineKind, progress, cancellationToken);
    }

    private SourceVideoInfo? ProbeCached(
        SourceVideoInfoCacheKey cacheKey,
        string sourcePath,
        InputPipelineKind pipelineKind,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lazy = _cache.GetOrAdd(
            cacheKey,
            _ => new Lazy<SourceVideoInfo?>(
                () => ProbeCore(sourcePath, pipelineKind, progress, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazy.Value;
        }
        catch
        {
            _cache.TryRemove(cacheKey, out _);
            throw;
        }
    }

    private SourceVideoInfo? ProbeCore(
        string sourcePath,
        InputPipelineKind pipelineKind,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        return pipelineKind switch
        {
            InputPipelineKind.VapourSynth => ProbeVapourSynth(sourcePath, progress, cancellationToken),
            InputPipelineKind.Y4mFile => ProbeY4m(sourcePath),
            InputPipelineKind.FfmpegPipe => ProbeFfprobe(sourcePath),
            InputPipelineKind.RawYuvFile => null,
            _ => null
        };
    }

    private static SourceVideoInfoCacheKey? TryBuildCacheKey(string sourcePath, InputPipelineKind pipelineKind)
    {
        if (pipelineKind == InputPipelineKind.RawYuvFile || string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to normalize source video path '{sourcePath}'. {ex}");
            normalizedPath = sourcePath.Trim();
        }

        var fileInfo = new FileInfo(normalizedPath);
        if (!fileInfo.Exists)
        {
            return null;
        }

        return new SourceVideoInfoCacheKey(
            normalizedPath,
            pipelineKind,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks);
    }

    private SourceVideoInfo ProbeVapourSynth(string sourcePath, Action<string>? progress, CancellationToken cancellationToken)
    {
        var executablePath = _toolLocator.ResolveVspipe();
        var errorBuilder = new StringBuilder();
        var errorGate = new object();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"--info {Quote(sourcePath)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            var normalized = ConsoleOutputLineNormalizer.Normalize(e.Data);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            lock (errorGate)
            {
                if (errorBuilder.Length > 0)
                {
                    errorBuilder.AppendLine();
                }

                errorBuilder.Append(normalized);
            }

            progress?.Invoke(normalized);
        };

        VapourSynthRuntimePathResolver.EnrichProcessPath(process.StartInfo);
        process.Start();
        using var cancellationRegistration = cancellationToken.Register(state =>
        {
            if (state is not Process cancellableProcess)
            {
                return;
            }

            try
            {
                if (!cancellableProcess.HasExited)
                {
                    cancellableProcess.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to terminate VapourSynth probe process for '{sourcePath}'. {ex}");
            }
        }, process);

        process.BeginErrorReadLine();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        cancellationToken.ThrowIfCancellationRequested();
        string error;
        lock (errorGate)
        {
            error = errorBuilder.ToString();
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"vspipe --info 失败：{FirstMeaningfulLine(error)}");
        }

        var sourceInfo = ParseVspipeInfo(output);
        return MergeVapourSynthFrameProperties(sourcePath, executablePath, sourceInfo);
    }

    private static SourceVideoInfo ProbeY4m(string sourcePath)
    {
        using var stream = File.OpenRead(sourcePath);
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var header = reader.ReadLine();

        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("YUV4MPEG2", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Y4M 头信息无效，无法探测源信息。");
        }

        var width = 0;
        var height = 0;
        var bitDepth = 8;
        var fpsNumerator = default(int?);
        var fpsDenominator = default(int?);
        var pixelFormat = "YUV420P8";
        string? colorRange = null;
        string? colorPrimaries = null;
        string? colorTransfer = null;
        string? colorMatrix = null;
        string? chromaLocation = null;

        foreach (var token in header.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 2)
            {
                continue;
            }

            switch (token[0])
            {
                case 'W':
                    width = ParseInt(token[1..], "Y4M 宽度");
                    break;
                case 'H':
                    height = ParseInt(token[1..], "Y4M 高度");
                    break;
                case 'F':
                    (fpsNumerator, fpsDenominator) = ParseY4mFps(token[1..]);
                    break;
                case 'C':
                    bitDepth = ParseY4mBitDepth(token[1..]);
                    pixelFormat = NormalizeY4mPixelFormat(token[1..], bitDepth);
                    break;
                case 'X':
                    ReadY4mExtendedColorToken(
                        token[1..],
                        ref colorRange,
                        ref colorPrimaries,
                        ref colorTransfer,
                        ref colorMatrix,
                        ref chromaLocation);
                    break;
            }
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Y4M 头信息缺少宽高字段。");
        }

        return new SourceVideoInfo(
            width,
            height,
            null,
            bitDepth,
            fpsNumerator,
            fpsDenominator,
            pixelFormat,
            colorRange,
            colorPrimaries,
            colorTransfer,
            colorMatrix,
            chromaLocation);
    }

    private SourceVideoInfo ProbeFfprobe(string sourcePath)
    {
        var output = RunFfprobe(
            $"-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate,r_frame_rate,nb_frames,pix_fmt,bits_per_raw_sample,duration,color_range,color_space,color_transfer,color_primaries,chroma_location:stream_side_data:format=duration -of json {Quote(sourcePath)}");

        return ParseFfprobeInfo(output);
    }

    private string RunFfprobe(string arguments)
    {
        var executablePath = _toolLocator.ResolveFfprobe();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        VapourSynthRuntimePathResolver.EnrichProcessPath(process.StartInfo);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe 探测失败：{FirstMeaningfulLine(error)}");
        }

        return output;
    }

    private static SourceVideoInfo ParseVspipeInfo(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = InfoLineRegex().Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            values[match.Groups["key"].Value.Trim()] = match.Groups["value"].Value.Trim();
        }

        var width = ParseInt(values.TryGetValue("Width", out var widthValue) ? widthValue : string.Empty, "Width");
        var height = ParseInt(values.TryGetValue("Height", out var heightValue) ? heightValue : string.Empty, "Height");
        var frames = ParseLong(values.TryGetValue("Frames", out var framesValue) ? framesValue : string.Empty);
        var pixelFormat = values.TryGetValue("Format Name", out var formatValue) ? formatValue : "Unknown";
        var bitDepth = values.TryGetValue("Bits", out var bitsValue)
            ? ParseInt(bitsValue, "Bits")
            : ParseBitDepthFromFormat(pixelFormat);
        var (fpsNumerator, fpsDenominator) = ParseVspipeFps(values.TryGetValue("FPS", out var fpsValue) ? fpsValue : string.Empty);

        return new SourceVideoInfo(width, height, frames, bitDepth, fpsNumerator, fpsDenominator, pixelFormat);
    }

    private static SourceVideoInfo ParseFfprobeInfo(string output)
    {
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("ffprobe 未返回可用的视频流信息。");
        }

        JsonElement? streamElement = null;
        foreach (var stream in streams.EnumerateArray())
        {
            streamElement = stream;
            break;
        }

        if (!streamElement.HasValue)
        {
            throw new InvalidOperationException("ffprobe 未找到视频流。");
        }

        var streamInfo = streamElement.Value;
        var width = ParseInt(GetJsonString(streamInfo, "width"), "Width");
        var height = ParseInt(GetJsonString(streamInfo, "height"), "Height");
        var pixelFormat = GetJsonString(streamInfo, "pix_fmt");
        var bitDepth = ParseBitDepthFromFfprobe(streamInfo, pixelFormat);
        var (fpsNumerator, fpsDenominator) = ParseFfprobeRate(
            GetJsonString(streamInfo, "avg_frame_rate"),
            GetJsonString(streamInfo, "r_frame_rate"));
        var durationSeconds = ParseInvariantDoubleNullable(GetJsonString(streamInfo, "duration"))
            ?? ParseFfprobeFormatDuration(document.RootElement);
        var totalFrames = ParseFfprobeFrameCount(GetJsonString(streamInfo, "nb_frames"), fpsNumerator, fpsDenominator, durationSeconds);

        var (masteringDisplay, contentLightLevel) = ParseFfprobeHdrMetadata(streamInfo);

        return new SourceVideoInfo(
            width,
            height,
            totalFrames,
            bitDepth,
            fpsNumerator,
            fpsDenominator,
            string.IsNullOrWhiteSpace(pixelFormat) ? "unknown" : pixelFormat,
            NormalizeMetadataValue(GetJsonString(streamInfo, "color_range")),
            NormalizeMetadataValue(GetJsonString(streamInfo, "color_primaries")),
            NormalizeMetadataValue(GetJsonString(streamInfo, "color_transfer")),
            NormalizeMetadataValue(GetJsonString(streamInfo, "color_space")),
            NormalizeMetadataValue(GetJsonString(streamInfo, "chroma_location")),
            masteringDisplay,
            contentLightLevel);
    }

    private static SourceVideoInfo MergeVapourSynthFrameProperties(
        string sourcePath,
        string vspipePath,
        SourceVideoInfo sourceInfo)
    {
        try
        {
            var pythonPath = ResolvePythonForVspipe(vspipePath);
            if (string.IsNullOrWhiteSpace(pythonPath))
            {
                return sourceInfo;
            }

            var output = RunVapourSynthFramePropertyProbe(pythonPath, sourcePath);
            if (string.IsNullOrWhiteSpace(output))
            {
                return sourceInfo;
            }

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;

            return sourceInfo with
            {
                ColorRange = MapVapourSynthRange(root) ?? sourceInfo.ColorRange,
                ColorPrimaries = MapH273ColorPrimaries(TryGetJsonInt(root, "_Primaries")) ?? sourceInfo.ColorPrimaries,
                ColorTransfer = MapH273ColorTransfer(TryGetJsonInt(root, "_Transfer")) ?? sourceInfo.ColorTransfer,
                ColorMatrix = MapH273ColorMatrix(TryGetJsonInt(root, "_Matrix")) ?? sourceInfo.ColorMatrix,
                ChromaLocation = MapVapourSynthChromaLocation(root) ?? sourceInfo.ChromaLocation
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to enrich VapourSynth frame properties for '{sourcePath}'. {ex}");
            return sourceInfo;
        }
    }

    private static string? ResolvePythonForVspipe(string vspipePath)
    {
        var vspipeDirectory = Path.GetDirectoryName(vspipePath);
        if (!string.IsNullOrWhiteSpace(vspipeDirectory))
        {
            var directoryInfo = new DirectoryInfo(vspipeDirectory);
            if (directoryInfo.Name.Equals("Scripts", StringComparison.OrdinalIgnoreCase)
                && directoryInfo.Parent is not null)
            {
                var sidecarPython = Path.Combine(directoryInfo.Parent.FullName, "python.exe");
                if (File.Exists(sidecarPython))
                {
                    return sidecarPython;
                }
            }
        }

        foreach (var root in EnumeratePathRoots())
        {
            var candidate = Path.Combine(root, "python.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string RunVapourSynthFramePropertyProbe(string pythonPath, string sourcePath)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"flowencode-vs-props-{Guid.NewGuid():N}.py");

        try
        {
            File.WriteAllText(scriptPath, VapourSynthFramePropertyProbeScript, Encoding.UTF8);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"{Quote(scriptPath)} {Quote(sourcePath)}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            VapourSynthRuntimePathResolver.EnrichProcessPath(process.StartInfo);
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(15_000))
            {
                process.Kill(true);
                return string.Empty;
            }

            Task.WaitAll(outputTask, errorTask);
            return process.ExitCode == 0 ? outputTask.Result : string.Empty;
        }
        finally
        {
            try
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete temporary VapourSynth frame probe script '{scriptPath}'. {ex}");
            }
        }
    }

    private static void ReadY4mExtendedColorToken(
        string token,
        ref string? colorRange,
        ref string? colorPrimaries,
        ref string? colorTransfer,
        ref string? colorMatrix,
        ref string? chromaLocation)
    {
        var separatorIndex = token.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return;
        }

        var key = token[..separatorIndex].Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        var value = NormalizeMetadataValue(token[(separatorIndex + 1)..]);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        switch (key.ToUpperInvariant())
        {
            case "COLORRANGE":
            case "RANGE":
                colorRange = NormalizeColorRangeDescriptor(value);
                break;
            case "COLORPRIM":
            case "COLORPRIMARIES":
            case "PRIMARIES":
                colorPrimaries = value;
                break;
            case "COLORTRANSFER":
            case "TRANSFER":
                colorTransfer = value;
                break;
            case "COLORSPACE":
            case "COLORMATRIX":
            case "MATRIX":
                colorMatrix = value;
                break;
            case "CHROMALOC":
            case "CHROMALOCATION":
                chromaLocation = value;
                break;
        }
    }

    private static (string? MasteringDisplay, string? ContentLightLevel) ParseFfprobeHdrMetadata(JsonElement streamInfo)
    {
        if (!streamInfo.TryGetProperty("side_data_list", out var sideDataList)
            || sideDataList.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        string? masteringDisplay = null;
        string? contentLightLevel = null;

        foreach (var sideData in sideDataList.EnumerateArray())
        {
            var sideDataType = GetJsonString(sideData, "side_data_type");
            if (sideDataType.Contains("Mastering display metadata", StringComparison.OrdinalIgnoreCase))
            {
                masteringDisplay ??= TryBuildMasteringDisplay(sideData);
                continue;
            }

            if (sideDataType.Contains("Content light level metadata", StringComparison.OrdinalIgnoreCase))
            {
                contentLightLevel ??= TryBuildContentLightLevel(sideData);
            }
        }

        return (masteringDisplay, contentLightLevel);
    }

    private static string? TryBuildMasteringDisplay(JsonElement sideData)
    {
        var greenX = ParseScaledMetadataValue(GetJsonString(sideData, "green_x"), 50_000d, 1d);
        var greenY = ParseScaledMetadataValue(GetJsonString(sideData, "green_y"), 50_000d, 1d);
        var blueX = ParseScaledMetadataValue(GetJsonString(sideData, "blue_x"), 50_000d, 1d);
        var blueY = ParseScaledMetadataValue(GetJsonString(sideData, "blue_y"), 50_000d, 1d);
        var redX = ParseScaledMetadataValue(GetJsonString(sideData, "red_x"), 50_000d, 1d);
        var redY = ParseScaledMetadataValue(GetJsonString(sideData, "red_y"), 50_000d, 1d);
        var whiteX = ParseScaledMetadataValue(GetJsonString(sideData, "white_point_x"), 50_000d, 1d);
        var whiteY = ParseScaledMetadataValue(GetJsonString(sideData, "white_point_y"), 50_000d, 1d);
        var maxLuminance = ParseScaledMetadataValue(GetJsonString(sideData, "max_luminance"), 10_000d, 10_000d);
        var minLuminance = ParseScaledMetadataValue(GetJsonString(sideData, "min_luminance"), 10_000d, 10_000d);

        if (greenX is null || greenY is null
            || blueX is null || blueY is null
            || redX is null || redY is null
            || whiteX is null || whiteY is null
            || maxLuminance is null || minLuminance is null)
        {
            return null;
        }

        return $"G({greenX},{greenY})B({blueX},{blueY})R({redX},{redY})WP({whiteX},{whiteY})L({maxLuminance},{minLuminance})";
    }

    private static string? TryBuildContentLightLevel(JsonElement sideData)
    {
        var maxContent = ParseIntegerMetadataValue(GetJsonString(sideData, "max_content"));
        var maxAverage = ParseIntegerMetadataValue(GetJsonString(sideData, "max_average"));
        return maxContent is not null && maxAverage is not null
            ? $"{maxContent},{maxAverage}"
            : null;
    }

    private static long? ParseScaledMetadataValue(string value, double scale, double alreadyScaledThreshold)
    {
        var parsed = ParseRationalMetadataValue(value);
        if (!parsed.HasValue)
        {
            return null;
        }

        var scaled = Math.Abs(parsed.Value) <= alreadyScaledThreshold
            ? parsed.Value * scale
            : parsed.Value;
        return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    private static long? ParseIntegerMetadataValue(string value)
    {
        var parsed = ParseRationalMetadataValue(value);
        return parsed.HasValue
            ? (long)Math.Round(parsed.Value, MidpointRounding.AwayFromZero)
            : null;
    }

    private static double? ParseRationalMetadataValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        var separatorIndex = normalized.IndexOf('/');
        if (separatorIndex > 0
            && double.TryParse(normalized[..separatorIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(normalized[(separatorIndex + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && Math.Abs(denominator) > double.Epsilon)
        {
            return numerator / denominator;
        }

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static (int? Numerator, int? Denominator) ParseVspipeFps(string value)
    {
        var match = FractionRegex().Match(value);
        if (!match.Success)
        {
            return (null, null);
        }

        return (ParseInt(match.Groups["num"].Value, "FPS numerator"), ParseInt(match.Groups["den"].Value, "FPS denominator"));
    }

    private static (int? Numerator, int? Denominator) ParseFfprobeRate(string primaryValue, string fallbackValue)
    {
        var primaryRate = ParseFraction(primaryValue);
        if (primaryRate.Numerator is > 0 && primaryRate.Denominator is > 0)
        {
            return primaryRate;
        }

        return ParseFraction(fallbackValue);
    }

    private static (int? Numerator, int? Denominator) ParseY4mFps(string value)
    {
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return (null, null);
        }

        return (ParseInt(parts[0], "Y4M FPS numerator"), ParseInt(parts[1], "Y4M FPS denominator"));
    }

    private static int ParseY4mBitDepth(string colorDescriptor)
    {
        var match = BitDepthRegex().Match(colorDescriptor);
        if (match.Success)
        {
            return ParseInt(match.Groups["bits"].Value, "Y4M bit depth");
        }

        return 8;
    }

    private static string NormalizeY4mPixelFormat(string colorDescriptor, int bitDepth)
    {
        var normalized = colorDescriptor.ToUpperInvariant();

        if (normalized.Contains("420", StringComparison.Ordinal))
        {
            return $"YUV420P{bitDepth}";
        }

        if (normalized.Contains("422", StringComparison.Ordinal))
        {
            return $"YUV422P{bitDepth}";
        }

        if (normalized.Contains("444", StringComparison.Ordinal))
        {
            return $"YUV444P{bitDepth}";
        }

        return $"YUV{bitDepth}";
    }

    private static int ParseBitDepthFromFormat(string formatName)
    {
        var match = BitDepthRegex().Match(formatName);
        return match.Success ? ParseInt(match.Groups["bits"].Value, "Format bit depth") : 8;
    }

    private static int ParseBitDepthFromFfprobe(JsonElement streamInfo, string pixelFormat)
    {
        var rawValue = GetJsonString(streamInfo, "bits_per_raw_sample");
        if (!string.IsNullOrWhiteSpace(rawValue)
            && int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        return ParseBitDepthFromFormat(pixelFormat);
    }

    private static (int? Numerator, int? Denominator) ParseFraction(string value)
    {
        var match = FractionRegex().Match(value);
        if (!match.Success)
        {
            return (null, null);
        }

        return (ParseInt(match.Groups["num"].Value, "rate numerator"), ParseInt(match.Groups["den"].Value, "rate denominator"));
    }

    private static int ParseInt(string value, string fieldName)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"无法解析 {fieldName}：{value}");
    }

    private static long? ParseLong(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ParseInvariantDoubleNullable(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static long? ParseFfprobeFrameCount(
        string frameCountValue,
        int? fpsNumerator,
        int? fpsDenominator,
        double? durationSeconds)
    {
        var frameCount = ParseLong(frameCountValue);
        if (frameCount is > 0)
        {
            return frameCount;
        }

        if (durationSeconds is > 0
            && fpsNumerator is > 0
            && fpsDenominator is > 0)
        {
            var totalFrames = durationSeconds.Value * fpsNumerator.Value / fpsDenominator.Value;
            return (long)Math.Round(totalFrames, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    private static double? ParseFfprobeFormatDuration(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("format", out var formatElement)
            || formatElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ParseInvariantDoubleNullable(GetJsonString(formatElement, "duration"));
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => property.GetRawText().Trim('"')
        };
    }

    private static int? TryGetJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? MapVapourSynthRange(JsonElement root)
    {
        var range = TryGetJsonInt(root, "_Range");
        if (range.HasValue)
        {
            return range.Value switch
            {
                0 => "tv",
                1 => "pc",
                _ => null
            };
        }

        var legacyRange = TryGetJsonInt(root, "_ColorRange");
        return legacyRange switch
        {
            0 => "pc",
            1 => "tv",
            _ => null
        };
    }

    private static string? MapVapourSynthChromaLocation(JsonElement root)
    {
        var chromaLocation = TryGetJsonInt(root, "_ChromaLocation");
        if (!chromaLocation.HasValue)
        {
            return null;
        }

        return chromaLocation.Value switch
        {
            0 => "left",
            1 => "center",
            2 => "topleft",
            3 => "top",
            4 => "bottomleft",
            5 => "bottom",
            _ => null
        };
    }

    private static string? MapH273ColorPrimaries(int? value)
    {
        return value switch
        {
            1 => "bt709",
            4 => "bt470m",
            5 => "bt470bg",
            6 => "smpte170m",
            7 => "smpte240m",
            8 => "film",
            9 => "bt2020",
            10 => "smpte428",
            11 => "smpte431",
            12 => "smpte432",
            22 => "ebu3213",
            _ => null
        };
    }

    private static string? MapH273ColorTransfer(int? value)
    {
        return value switch
        {
            1 => "bt709",
            4 => "bt470m",
            5 => "bt470bg",
            6 => "smpte170m",
            7 => "smpte240m",
            8 => "linear",
            9 => "log100",
            10 => "log316",
            11 => "iec61966-2-4",
            12 => "bt1361e",
            13 => "iec61966-2-1",
            14 => "bt2020-10",
            15 => "bt2020-12",
            16 => "smpte2084",
            17 => "smpte428",
            18 => "arib-std-b67",
            _ => null
        };
    }

    private static string? MapH273ColorMatrix(int? value)
    {
        return value switch
        {
            0 => "gbr",
            1 => "bt709",
            4 => "fcc",
            5 => "bt470bg",
            6 => "smpte170m",
            7 => "smpte240m",
            8 => "ycgco",
            9 => "bt2020nc",
            10 => "bt2020c",
            11 => "smpte2085",
            12 => "chroma-derived-nc",
            13 => "chroma-derived-c",
            14 => "ictcp",
            _ => null
        };
    }

    private static string? NormalizeMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Trim('"').ToLowerInvariant();
        return normalized switch
        {
            "" or "n/a" or "na" or "unknown" or "unspecified" or "undef" or "undefined" or "reserved" => null,
            _ => NormalizeColorRangeDescriptor(normalized)
        };
    }

    private static string NormalizeColorRangeDescriptor(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "full" or "pc" or "jpeg" => "pc",
            "limited" or "tv" or "mpeg" => "tv",
            var normalized => normalized
        };
    }

    private static IEnumerable<string> EnumeratePathRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathVariables = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        };

        foreach (var pathVariable in pathVariables)
        {
            if (string.IsNullOrWhiteSpace(pathVariable))
            {
                continue;
            }

            foreach (var root in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(root))
                {
                    yield return root;
                }
            }
        }
    }

    private const string VapourSynthFramePropertyProbeScript = """
import contextlib
import json
import os
import runpy
import sys

import vapoursynth as vs


def normalize(value):
    if isinstance(value, bytes):
        return value.decode("utf-8", "replace")

    try:
        return int(value)
    except Exception:
        return str(value)


def main():
    source_path = sys.argv[1]

    with open(os.devnull, "w", encoding="utf-8") as sink:
        with contextlib.redirect_stdout(sink):
            runpy.run_path(source_path, run_name="__vapoursynth_probe__")

    output = vs.get_output(0)
    frame = output.clip.get_frame(0)
    props = frame.props
    result = {}

    for key in (
        "_Matrix",
        "_Primaries",
        "_Transfer",
        "_Range",
        "_ColorRange",
        "_ChromaLocation",
    ):
        if key in props:
            result[key] = normalize(props[key])

    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
""";

    private static string FirstMeaningfulLine(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? "未返回可读的错误信息。";
    }

    private static string Quote(string value)
    {
        return $"\"{value}\"";
    }

    [GeneratedRegex(@"^(?<key>[^:]+):\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InfoLineRegex();

    [GeneratedRegex(@"(?<num>\d+)\s*/\s*(?<den>\d+)", RegexOptions.Compiled)]
    private static partial Regex FractionRegex();

    [GeneratedRegex(@"(?<bits>8|9|10|12|14|16)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BitDepthRegex();

    private sealed record SourceVideoInfoCacheKey(
        string SourcePath,
        InputPipelineKind PipelineKind,
        long Length,
        long LastWriteTimeUtcTicks);
}

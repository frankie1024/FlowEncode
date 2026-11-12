using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class AutoCompressionTools
{
    private static readonly string[] ProbingStatistics = ["auto", "mean", "median", "harmonic", "mode", "minimum", "maximum", "root-mean-square"];
    private static readonly string[] FourthRoundInterpolationMethods = ["linear", "quadratic", "natural"];
    private static readonly string[] InterpolationMethods = ["linear", "quadratic", "natural", "pchip", "catmull", "akima", "cubic-polynomial"];

    [McpServerTool(Name = "preview_auto_compression_command", ReadOnly = true, Idempotent = true)]
    [Description("Builds the Av1an target-quality command (search for the encoder quality that reaches a VMAF / "
        + "SSIMULACRA2 / Butteraugli / XPSNR target) and the output path, without running it.")]
    public static AutoCompressionPreview PreviewAutoCompressionCommand(
        IAutoCompressionRunner runner,
        [Description("Absolute path of the source video or .vpy script.")] string sourcePath,
        [Description("Encoder: x264, x265 or svt-av1.")] string encoder,
        [Description("Metric: vmaf (default), ssimulacra2, butteraugli-inf, butteraugli-3, xpsnr or xpsnr-weighted.")] string? metric = null,
        [Description("Target score for the metric, e.g. 95 for VMAF or 80 for SSIMULACRA2. Defaults to 95.")] double targetQuality = 95,
        [Description("Number of probe encodes per scene. Defaults to 4.")] int probes = 4,
        [Description("Absolute output folder. Defaults to the source folder.")] string? outputDirectory = null,
        [Description("Extra encoder arguments, single line (e.g. \"--preset 4\").")] string? videoParameters = null,
        [Description("Extra Av1an arguments, single line.")] string? backendArguments = null,
        [Description("Av1an worker count. Omit for Av1an's default.")] int? workers = null,
        [Description("Probe score aggregation: auto, mean, median, harmonic, mode, minimum, maximum or root-mean-square.")] string? probingStatistic = null,
        [Description("Probe interpolation as <4th round>-<5th round>, e.g. linear-pchip. Omit for the backend default.")] string? interpolationMethod = null,
        [Description("Resolution of probe encodes, e.g. 1280x720. Omit for the source resolution.")] string? probeResolution = null,
        [Description("Use every Nth frame when probing (1 = all frames).")] int probingRate = 1)
    {
        var request = BuildRequest(
            sourcePath, encoder, metric, targetQuality, probes, outputDirectory, videoParameters, backendArguments,
            workers, probingStatistic, interpolationMethod, probeResolution, probingRate);
        return new AutoCompressionPreview(McpToolGuard.Run(() => runner.BuildDisplayCommand(request)), request.OutputPath);
    }

    [McpServerTool(Name = "start_auto_compression", Destructive = false)]
    [Description("Runs a target-quality encode in the FlowEncode desktop app's auto-compression module (starting the app if "
        + "needed). The module runs one task at a time; poll get_task_status with module=autocompression.")]
    public static Task<AutomationModuleStatus> StartAutoCompression(
        DesktopBridge desktop,
        [Description("Absolute path of the source video or .vpy script.")] string sourcePath,
        [Description("Encoder: x264, x265 or svt-av1.")] string encoder,
        [Description("Metric: vmaf (default), ssimulacra2, butteraugli-inf, butteraugli-3, xpsnr or xpsnr-weighted.")] string? metric = null,
        [Description("Target score for the metric, e.g. 95 for VMAF or 80 for SSIMULACRA2. Defaults to 95.")] double targetQuality = 95,
        [Description("Number of probe encodes per scene. Defaults to 4.")] int probes = 4,
        [Description("Absolute output folder. Defaults to the source folder.")] string? outputDirectory = null,
        [Description("Extra encoder arguments, single line (e.g. \"--preset 4\").")] string? videoParameters = null,
        [Description("Extra Av1an arguments, single line.")] string? backendArguments = null,
        [Description("Av1an worker count. Omit for Av1an's default.")] int? workers = null,
        [Description("Probe score aggregation: auto, mean, median, harmonic, mode, minimum, maximum or root-mean-square.")] string? probingStatistic = null,
        [Description("Probe interpolation as <4th round>-<5th round>, e.g. linear-pchip. Omit for the backend default.")] string? interpolationMethod = null,
        [Description("Resolution of probe encodes, e.g. 1280x720. Omit for the source resolution.")] string? probeResolution = null,
        [Description("Use every Nth frame when probing (1 = all frames).")] int probingRate = 1,
        [Description("Allow replacing an existing output file. Defaults to false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(
            sourcePath, encoder, metric, targetQuality, probes, outputDirectory, videoParameters, backendArguments,
            workers, probingStatistic, interpolationMethod, probeResolution, probingRate);
        if (!overwrite && File.Exists(request.OutputPath))
        {
            throw new McpException($"Output file already exists: {request.OutputPath}. Pass overwrite=true or choose another outputDirectory.");
        }

        return desktop.SendAsync<AutomationModuleStatus>(
            DesktopAutomationMethods.AutoCompressionStart,
            new StartAutoCompressionParams(request),
            cancellationToken);
    }

    internal static AutoCompressionRequest BuildRequest(
        string sourcePath,
        string encoder,
        string? metric,
        double targetQuality,
        int probes,
        string? outputDirectory,
        string? videoParameters,
        string? backendArguments,
        int? workers,
        string? probingStatistic,
        string? interpolationMethod,
        string? probeResolution,
        int probingRate)
    {
        var fullSourcePath = McpPaths.RequireExistingFile(sourcePath, nameof(sourcePath));
        var encoderKind = EncodingProfileResolver.ParseEncoder(encoder);
        var metricKind = string.IsNullOrWhiteSpace(metric) ? AutoCompressionMetric.Vmaf : McpEnum.Parse<AutoCompressionMetric>(metric, nameof(metric));
        var fullOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(fullSourcePath) ?? throw new McpException("Cannot derive an output folder from sourcePath.")
            : McpPaths.RequireAbsolute(outputDirectory, nameof(outputDirectory));
        if (File.Exists(fullOutputDirectory))
        {
            throw new McpException($"outputDirectory points to a file: {fullOutputDirectory}");
        }

        var outputPath = AutoCompressionOutputPathPlanner.BuildOutputPath(fullSourcePath, fullOutputDirectory, encoderKind, metricKind, targetQuality);
        if (string.Equals(outputPath, fullSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("The output would overwrite the source. Choose another outputDirectory.");
        }

        var request = new AutoCompressionRequest(
            Guid.NewGuid(),
            fullSourcePath,
            outputPath,
            encoderKind,
            metricKind,
            targetQuality,
            Math.Max(1, probes),
            videoParameters?.Trim() ?? string.Empty,
            backendArguments?.Trim() ?? string.Empty,
            workers is > 0 ? workers : null,
            BuildSearchProfile(probingStatistic, interpolationMethod, probeResolution, probingRate));
        McpToolGuard.Run(() => RequestValidation.ValidateAutoCompressionRequest(request));
        return request;
    }

    /// <summary>Mirrors the desktop form: null when everything is at the backend default.</summary>
    internal static AutoCompressionSearchProfile? BuildSearchProfile(
        string? probingStatistic,
        string? interpolationMethod,
        string? probeResolution,
        int probingRate)
    {
        var statistic = string.IsNullOrWhiteSpace(probingStatistic) ? "auto" : probingStatistic.Trim().ToLowerInvariant();
        if (!ProbingStatistics.Contains(statistic))
        {
            throw new McpException($"Unknown probingStatistic '{probingStatistic}'. Use one of: {string.Join(", ", ProbingStatistics)}.");
        }

        var interpolation = interpolationMethod?.Trim().ToLowerInvariant() ?? string.Empty;
        if (interpolation.Length > 0)
        {
            var separator = interpolation.IndexOf('-');
            var fourthRound = separator > 0 ? interpolation[..separator] : string.Empty;
            var fifthRound = separator > 0 ? interpolation[(separator + 1)..] : string.Empty;
            if (!FourthRoundInterpolationMethods.Contains(fourthRound) || !InterpolationMethods.Contains(fifthRound))
            {
                throw new McpException(
                    $"Unknown interpolationMethod '{interpolationMethod}'. Use '<4th>-<5th>' where 4th is one of "
                    + $"{string.Join(", ", FourthRoundInterpolationMethods)} and 5th one of {string.Join(", ", InterpolationMethods)} "
                    + "(e.g. linear-pchip).");
            }
        }

        var resolution = probeResolution?.Trim() ?? string.Empty;
        var rate = Math.Max(1, probingRate);
        if (interpolation.Length == 0 && statistic == "auto" && resolution.Length == 0 && rate == 1)
        {
            return null;
        }

        return new AutoCompressionSearchProfile(
            interpolation.Length == 0 ? null : interpolation,
            statistic == "auto" ? null : statistic,
            resolution.Length == 0 ? null : resolution,
            rate,
            ScoutEnabled: false);
    }
}

public sealed record AutoCompressionPreview(string Command, string OutputPath);

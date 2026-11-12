using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class AudioTools
{
    private static readonly int[] OpusBitrates = [510, 384, 192, 96];

    [McpServerTool(Name = "preview_audio_command", ReadOnly = true, Idempotent = true)]
    [Description("Builds the command FlowEncode's audio module would run (eac3to FLAC/AC3, deew Dolby Digital Plus, or "
        + "FFmpeg + opusenc Opus) and the resulting output path, without starting it.")]
    public static async Task<AudioCommandPreview> PreviewAudioCommand(
        IAudioProcessingRunner runner,
        IAudioSourceInfoService sourceInfoService,
        [Description("Absolute path of the audio file. eac3to needs an elementary stream (demux video containers first).")] string sourcePath,
        [Description("Workflow: ddp (Dolby Digital Plus via deew), opus, flac or ac3 (both via eac3to).")] string workflow,
        [Description("Absolute output folder. Defaults to the source folder.")] string? outputDirectory = null,
        [Description("Opus bitrate in kbps: 510, 384, 192 or 96. Defaults to 192 for stereo and 384 for surround.")] int? opusBitrateKbps = null,
        [Description("Opus: use channel mapping family 1 (surround). Defaults to true for 5.1 / 7.1 sources.")] bool? opusMappingFamily1 = null,
        [Description("Extra eac3to arguments for flac / ac3 (e.g. \"-down16\").")] string? eac3toArguments = null,
        CancellationToken cancellationToken = default)
    {
        var request = await BuildRequestAsync(
            sourceInfoService, sourcePath, workflow, outputDirectory, opusBitrateKbps, opusMappingFamily1, eac3toArguments, cancellationToken);
        return new AudioCommandPreview(runner.BuildDisplayCommand(request), request.OutputPath, request.Mode.ToString(), request.OpusBitrateKbps);
    }

    [McpServerTool(Name = "start_audio_task", Destructive = false)]
    [Description("Runs an audio conversion in the FlowEncode desktop app's audio module (starting the app if needed): "
        + "ddp, opus, flac or ac3. The module runs one task at a time; poll get_task_status with module=audio.")]
    public static async Task<AutomationModuleStatus> StartAudioTask(
        DesktopBridge desktop,
        IAudioSourceInfoService sourceInfoService,
        [Description("Absolute path of the audio file. eac3to needs an elementary stream (demux video containers first).")] string sourcePath,
        [Description("Workflow: ddp (Dolby Digital Plus via deew), opus, flac or ac3 (both via eac3to).")] string workflow,
        [Description("Absolute output folder. Defaults to the source folder.")] string? outputDirectory = null,
        [Description("Opus bitrate in kbps: 510, 384, 192 or 96. Defaults to 192 for stereo and 384 for surround.")] int? opusBitrateKbps = null,
        [Description("Opus: use channel mapping family 1 (surround). Defaults to true for 5.1 / 7.1 sources.")] bool? opusMappingFamily1 = null,
        [Description("Extra eac3to arguments for flac / ac3 (e.g. \"-down16\").")] string? eac3toArguments = null,
        [Description("Allow replacing an existing output file. Defaults to false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var request = await BuildRequestAsync(
            sourceInfoService, sourcePath, workflow, outputDirectory, opusBitrateKbps, opusMappingFamily1, eac3toArguments, cancellationToken);
        if (!overwrite && request.Mode != AudioProcessingMode.Ddp && File.Exists(request.OutputPath))
        {
            throw new McpException($"Output file already exists: {request.OutputPath}. Pass overwrite=true or choose another outputDirectory.");
        }

        return await desktop.SendAsync<AutomationModuleStatus>(
            DesktopAutomationMethods.AudioStart,
            new StartAudioParams(request),
            cancellationToken);
    }

    internal static async Task<AudioProcessingRequest> BuildRequestAsync(
        IAudioSourceInfoService sourceInfoService,
        string sourcePath,
        string workflow,
        string? outputDirectory,
        int? opusBitrateKbps,
        bool? opusMappingFamily1,
        string? eac3toArguments,
        CancellationToken cancellationToken)
    {
        var fullSourcePath = McpPaths.RequireExistingFile(sourcePath, nameof(sourcePath));
        var (mode, eac3ToFormat) = ParseWorkflow(workflow);
        var fullOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(fullSourcePath) ?? throw new McpException("Cannot derive an output folder from sourcePath.")
            : McpPaths.RequireAbsolute(outputDirectory, nameof(outputDirectory));
        if (File.Exists(fullOutputDirectory))
        {
            throw new McpException($"outputDirectory points to a file: {fullOutputDirectory}");
        }

        if (mode == AudioProcessingMode.Eac3To && AudioProcessingOutputPathPlanner.IsDirectSourceUnsupported(fullSourcePath))
        {
            throw new McpException("eac3to cannot read audio inside a video container. Demux the audio track first "
                + "(for Blu-ray use start_bluray_demux), or use the ddp / opus workflow.");
        }

        var info = await McpToolGuard.RunAsync(() => sourceInfoService.ProbeAsync(fullSourcePath, cancellationToken));
        var isSurround = info?.HasSurround51Or71Channels() == true;
        int? bitrate = null;
        if (mode == AudioProcessingMode.Opus)
        {
            bitrate = opusBitrateKbps ?? (isSurround || info?.Channels > 2 ? 384 : 192);
            if (!OpusBitrates.Contains(bitrate.Value))
            {
                throw new McpException($"opusBitrateKbps must be one of {string.Join(", ", OpusBitrates)}.");
            }
        }

        IReadOnlyList<string> extraArguments = [];
        if (mode == AudioProcessingMode.Eac3To && !string.IsNullOrWhiteSpace(eac3toArguments))
        {
            extraArguments = McpToolGuard.Run(() => CommandArgumentTokenizer.Tokenize(eac3toArguments, throwOnUnclosedQuote: true));
        }

        var outputPath = AudioProcessingOutputPathPlanner.BuildOutputPath(fullSourcePath, fullOutputDirectory, mode, eac3ToFormat);
        if (string.Equals(outputPath, fullSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("The output would overwrite the source. Choose another outputDirectory.");
        }

        var request = new AudioProcessingRequest(
            Guid.NewGuid(),
            fullSourcePath,
            outputPath,
            mode,
            eac3ToFormat,
            extraArguments,
            info?.DurationSeconds,
            info?.Channels,
            info?.ChannelLayout,
            bitrate,
            mode == AudioProcessingMode.Opus && (opusMappingFamily1 ?? isSurround));
        McpToolGuard.Run(() => RequestValidation.ValidateAudioProcessingRequest(request));
        return request;
    }

    private static (AudioProcessingMode Mode, AudioEac3ToOutputFormat? Format) ParseWorkflow(string? workflow)
    {
        var normalized = new string((workflow ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized switch
        {
            "ddp" or "eac3" or "ddplus" or "dolbydigitalplus" or "deew" => (AudioProcessingMode.Ddp, null),
            "opus" => (AudioProcessingMode.Opus, null),
            "flac" or "eac3to" or "eac3toflac" => (AudioProcessingMode.Eac3To, AudioEac3ToOutputFormat.Flac),
            "ac3" or "eac3toac3" => (AudioProcessingMode.Eac3To, AudioEac3ToOutputFormat.Ac3),
            _ => throw new McpException($"Unknown workflow '{workflow}'. Use ddp, opus, flac or ac3.")
        };
    }
}

public sealed record AudioCommandPreview(
    string Command,
    string OutputPath,
    string Mode,
    int? OpusBitrateKbps);

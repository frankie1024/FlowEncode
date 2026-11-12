using System.Diagnostics;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure.Automation;

// The pipe is only a transport adapter. No window, dispatcher or ViewModel is involved.
public sealed class DesktopTaskHost(TaskCoordinator tasks, ITaskPlanningService plans, IAppSettingsService settings,
    IToolProbeService probes, IEncoderDiscoveryService encoders, IProfileLibraryService templates,
    IAudioSourceInfoService audioSources, IBluRayDiscProbeService discs) : IDisposable
{
    private DesktopAutomationServer? _server;
    public void Start()
    {
        if (_server is not null) return;
        _server = new(DesktopAutomationProtocol.GetPipeName(), HandleAsync, (message, exception) => Debug.WriteLine(message + " " + exception));
        _server.Start();
    }
    public void Dispose() { _server?.DisposeAsync().AsTask().GetAwaiter().GetResult(); _server = null; }

    public async Task<object?> HandleAsync(string method, JsonElement? parameters, CancellationToken token)
    {
        var current = settings.Load();
        if (method == DesktopTaskMethods.Status)
            return new DesktopTaskStatus(System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "3.0.0", Environment.ProcessId,
                true, current.EnableMcpAutomation, tasks.List().Count(t => t.State is EncodingJobState.Queued or EncodingJobState.Running), current.MaxConcurrentEncodingJobs);
        if (!current.EnableMcpAutomation) throw new DesktopAutomationException("Enable local MCP automation in FlowEncode Settings before using this tool.", "automation_disabled");
        T Read<T>() where T : class => parameters?.Deserialize<T>(DesktopAutomationProtocol.JsonOptions) ?? throw new ArgumentException("Missing request parameters.");
        switch (method)
        {
            case DesktopTaskMethods.Tasks: return tasks.List().Select(s => WithLogLimit(s, 20)).ToArray();
            case DesktopTaskMethods.Task:
                var get = Read<TaskIdParameters>(); return WithLogLimit(tasks.Get(get.TaskId), get.LogLines);
            case DesktopTaskMethods.Cancel: return new { cancelled = tasks.Cancel(Read<TaskIdParameters>().TaskId) };
            case DesktopTaskMethods.Retry:
                var retry = Read<RetryTaskParameters>(); return await plans.RetryAsync(retry.TaskId, retry.IdempotencyKey, token).ConfigureAwait(false);
            case DesktopTaskMethods.Submit:
                var submit = Read<SubmitPlanParameters>(); return await plans.SubmitAsync(submit.PlanId, submit.IdempotencyKey, token).ConfigureAwait(false);
            case DesktopTaskMethods.CreatePlan:
                var workload = Read<TaskWorkload>();
                workload.Validate();
                // Legacy manual requests default to replacement. MCP requires an explicit opt-in.
                var requestName = workload.Kind switch { TaskKind.Video => "video", TaskKind.Audio => "audio", TaskKind.BluRay => "bluRay", _ => "autoCompression" };
                var rawRequest = parameters!.Value.EnumerateObject().FirstOrDefault(p => p.Name.Equals(requestName, StringComparison.OrdinalIgnoreCase)).Value;
                var overwrite = rawRequest.ValueKind == JsonValueKind.Object && rawRequest.EnumerateObject()
                    .Any(p => p.Name.Equals("overwriteOutput", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True);
                workload = workload.Kind switch
                {
                    TaskKind.Video => workload with { Video = workload.Video! with { OverwriteOutput = overwrite } },
                    TaskKind.Audio => workload with { Audio = workload.Audio! with { OverwriteOutput = overwrite } },
                    TaskKind.BluRay => workload with { BluRay = workload.BluRay! with { OverwriteOutput = overwrite } },
                    _ => workload with { AutoCompression = workload.AutoCompression! with { OverwriteOutput = overwrite } }
                };
                return await plans.CreateAsync(workload, token).ConfigureAwait(false);
            case DesktopTaskMethods.VideoPlan:
                var video = Read<VideoPlanParameters>();
                var profile = video.Profile;
                if (!string.IsNullOrWhiteSpace(video.TemplateId))
                {
                    if (profile is not null) throw new ArgumentException("Choose either a saved template or an explicit profile.");
                    profile = (await templates.GetUserTemplatesAsync(token)).FirstOrDefault(t => t.Id == video.TemplateId)?.Profile
                        ?? throw new KeyNotFoundException("Template not found: " + video.TemplateId);
                }
                profile ??= DefaultEncodingProfiles.GetDefault(video.Encoder);
                return await plans.CreateAsync(new(TaskKind.Video, Video: new(Guid.NewGuid(), profile, video.SourcePath, video.OutputPath,
                    video.Pipeline, video.Architecture, video.Av1anParallel, video.Workers, video.OverwriteOutput)), token).ConfigureAwait(false);
            case DesktopTaskMethods.AudioPlan:
                var audio = Read<AudioPlanParameters>();
                var source = audio.Mode == AudioProcessingMode.Ddp ? null : await audioSources.ProbeAsync(audio.SourcePath, token).ConfigureAwait(false);
                return await plans.CreateAsync(new(TaskKind.Audio, Audio: new(Guid.NewGuid(), audio.SourcePath, audio.OutputPath, audio.Mode,
                    audio.Eac3ToFormat, [], source?.DurationSeconds, source?.Channels, source?.ChannelLayout, audio.OpusBitrateKbps,
                    audio.UseOpusMappingFamily1, audio.Deew, audio.OverwriteOutput)), token).ConfigureAwait(false);
            case DesktopTaskMethods.Templates: return await templates.GetUserTemplatesAsync(token).ConfigureAwait(false);
            case DesktopTaskMethods.DefaultProfile: return DefaultEncodingProfiles.GetDefault(Read<DefaultProfileParameters>().Encoder);
            case DesktopTaskMethods.Capabilities:
                var kinds = new[] { RegisteredToolKind.Ffmpeg, RegisteredToolKind.Ffprobe, RegisteredToolKind.Deew, RegisteredToolKind.Vspipe, RegisteredToolKind.Av1an, RegisteredToolKind.Eac3To, RegisteredToolKind.DgDemux, RegisteredToolKind.OpusExt };
                var tools = await Task.WhenAll(kinds.Select(k => probes.ProbeAsync(k, token))).ConfigureAwait(false);
                return new { tools, encoders = encoders.DiscoverSystemBinaries(), deewFormats = DeewArguments.Formats,
                    supportedTaskKinds = Enum.GetNames<TaskKind>(), videoOutput = "Video elementary stream; audio/subtitles are not automatically muxed",
                    deewVersionMinimum = "5.1.0", deeVersionMinimum = "6.7.0", unverifiedFeatures = new[] { "Atmos real-media validation is pending; it requires TrueHD Atmos source and the corresponding licensed DEE components." } };
            case DesktopTaskMethods.ProbeMedia:
                var media = Read<ProbeMediaParameters>();
                if (!Path.IsPathFullyQualified(media.SourcePath) || !File.Exists(media.SourcePath)) throw new FileNotFoundException("Supply an absolute path to an existing media file.");
                var ffprobe = await probes.ProbeAsync(RegisteredToolKind.Ffprobe, token).ConfigureAwait(false);
                if (!ffprobe.IsReady) throw new InvalidOperationException(ffprobe.FailureReason);
                var info = new ProcessStartInfo(ffprobe.ExecutablePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var arg in new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", media.SourcePath }) info.ArgumentList.Add(arg);
                var result = await ProcessProbeRunner.RunAsync(info, TimeSpan.FromSeconds(30), "Media probe timed out.", token).ConfigureAwait(false);
                if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
                using (var parsed = JsonDocument.Parse(result.StandardOutput)) return parsed.RootElement.Clone();
            case DesktopTaskMethods.ScanDisc:
                var scan = Read<ScanDiscParameters>(); return await discs.ScanDiscAsync(scan.Backend, scan.DiscPath, token).ConfigureAwait(false);
            case DesktopTaskMethods.ScanPlaylist:
                var playlist = Read<ScanPlaylistParameters>(); return await discs.ScanPlaylistAsync(playlist.Backend, playlist.DiscPath, playlist.Playlist, token).ConfigureAwait(false);
            default: throw new KeyNotFoundException("Unknown automation method: " + method);
        }
    }

    private static TaskSnapshot WithLogLimit(TaskSnapshot snapshot, int lines)
    {
        if (lines is < 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(lines), "Log line count must be between 0 and 2000.");
        return snapshot with { Log = lines == 0 ? "" : string.Join('\n', snapshot.Log.Split('\n').TakeLast(lines)) };
    }
}

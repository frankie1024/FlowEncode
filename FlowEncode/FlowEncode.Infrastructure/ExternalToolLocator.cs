using System.Diagnostics;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed class ExternalToolLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly LocalAppPaths _paths;
    private readonly IAppSettingsService? _settingsService;

    public ExternalToolLocator(LocalAppPaths paths, IAppSettingsService? settingsService = null)
    {
        _paths = paths;
        _settingsService = settingsService;
    }

    public string ResolveVspipe()
    {
        var manualVspipe = ResolveManual(RegisteredToolKind.Vspipe);
        if (!string.IsNullOrWhiteSpace(manualVspipe) && IsUsableVspipe(manualVspipe))
        {
            return manualVspipe;
        }

        foreach (var candidate in EnumerateVspipeCandidates())
        {
            if (IsUsableVspipe(candidate))
            {
                return candidate;
            }
        }

        throw CreateMissingToolException(RegisteredToolKind.Vspipe);
    }

    public string ResolveAvs2PipeMod()
    {
        return ResolveManual(RegisteredToolKind.Avs2PipeMod)
            ?? ResolveAny("avs2pipemod64.exe", "avs2pipemod.exe", "Avs2Pipemod.exe")
            ?? throw CreateMissingToolException(RegisteredToolKind.Avs2PipeMod);
    }

    public string ResolveFfmpeg()
    {
        return ResolveManual(RegisteredToolKind.Ffmpeg)
            ?? ResolveAny("ffmpeg.exe", "ffmpeg64.exe")
            ?? throw CreateMissingToolException(RegisteredToolKind.Ffmpeg);
    }

    public string ResolveFfprobe()
    {
        return ResolveManual(RegisteredToolKind.Ffprobe)
            ?? ResolveAny("ffprobe.exe", "ffprobe64.exe")
            ?? throw CreateMissingToolException(RegisteredToolKind.Ffprobe);
    }

    public string ResolveAv1an()
    {
        return ResolveManual(RegisteredToolKind.Av1an)
            ?? ResolveAny("av1an.exe", "Av1an.exe")
            ?? throw CreateMissingToolException(RegisteredToolKind.Av1an);
    }

    internal static string BuildMissingToolMessage(RegisteredToolKind kind, AppLanguage language)
    {
        return kind switch
        {
            RegisteredToolKind.Vspipe => T(
                language,
                "No usable vspipe.exe was found. Install VapourSynth before using .vpy input, or place the real vspipe.exe in PATH or the tools folder.",
                "未找到可用的 vspipe.exe。处理 .vpy 输入前，请先安装 VapourSynth，或把真正的 vspipe.exe 放到 PATH / tools 目录。"),
            RegisteredToolKind.Avs2PipeMod => T(
                language,
                "avs2pipemod was not found. Install Avs2Pipemod before using .avs input, or place the executable in PATH or the tools folder.",
                "未找到 avs2pipemod。处理 .avs 输入前，请先安装 Avs2Pipemod，或把可执行文件放到 PATH / tools 目录。"),
            RegisteredToolKind.Ffmpeg => T(
                language,
                "ffmpeg.exe was not found. Install FFmpeg before processing .mkv, .mp4, .m2ts, .avc, and other media inputs, or place ffmpeg.exe in PATH or the tools folder.",
                "未找到 ffmpeg.exe。处理 .mkv / .mp4 / .m2ts / .avc 等媒体文件前，请先安装 FFmpeg，或把 ffmpeg.exe 放到 PATH / tools 目录。"),
            RegisteredToolKind.Ffprobe => T(
                language,
                "ffprobe.exe was not found. Install FFmpeg before probing media source information, or place ffprobe.exe in PATH or the tools folder.",
                "未找到 ffprobe.exe。探测媒体文件源信息前，请先安装 FFmpeg，或把 ffprobe.exe 放到 PATH / tools 目录。"),
            RegisteredToolKind.Av1an => T(
                language,
                "av1an.exe was not found. Install Av1an and make sure it is available in PATH, or place av1an.exe in the tools folder.",
                "未找到 av1an.exe。请先安装 Av1an 并确保它位于 PATH，或将 av1an.exe 放到 tools 目录。"),
            _ => T(
                language,
                $"No usable {kind.ToDisplayName()} was found.",
                $"未找到可用的 {kind.ToDisplayName()}。")
        };
    }

    private string? ResolveManual(RegisteredToolKind kind)
    {
        var settings = _settingsService?.Load();
        if (settings is null
            || !settings.EffectiveManualToolPaths.TryGetValue(ManualToolPathKeys.ForRegisteredTool(kind), out var value))
        {
            return null;
        }

        var normalized = value.Trim().Trim('"');
        return File.Exists(normalized) ? Path.GetFullPath(normalized) : null;
    }

    private string? ResolveAny(params string[] fileNames)
    {
        foreach (var root in GetSearchRoots())
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(root, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateVspipeCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in GetSearchRoots())
        {
            foreach (var fileName in new[] { "vspipe.exe", "VSPipe.exe" })
            {
                var directCandidate = Path.Combine(root, fileName);
                if (File.Exists(directCandidate) && seen.Add(directCandidate))
                {
                    yield return directCandidate;
                }
            }

            var sidecarCandidate = ResolvePythonSidecarVspipe(root);
            if (!string.IsNullOrWhiteSpace(sidecarCandidate) && seen.Add(sidecarCandidate))
            {
                yield return sidecarCandidate;
            }
        }
    }

    private static string? ResolvePythonSidecarVspipe(string root)
    {
        return VapourSynthRuntimePathResolver.ResolvePythonSidecarVspipe(root);
    }

    private static bool IsUsableVspipe(string executablePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            VapourSynthRuntimePathResolver.EnrichProcessPath(startInfo);
            using var _ = ErrorDialogSuppression.Enter();
            var result = ProcessProbeRunner.Run(startInfo, ProbeTimeout, "vspipe probe timed out.");
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private InvalidOperationException CreateMissingToolException(RegisteredToolKind kind)
    {
        return new InvalidOperationException(BuildMissingToolMessage(kind, GetLanguage()));
    }

    private AppLanguage GetLanguage()
    {
        try
        {
            return _settingsService?.Load().Language ?? AppSettings.Default.Language;
        }
        catch
        {
            return AppSettings.Default.Language;
        }
    }

    private static string T(AppLanguage language, string en, string zh) =>
        language == AppLanguage.English ? en : zh;

    private IEnumerable<string> GetSearchRoots()
    {
        yield return _paths.ToolsRootPath;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var root in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return root;
            }
        }
    }
}

using System.Diagnostics;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed class ParallelVideoEncodingOutputVerifier
{
    private static readonly TimeSpan FfprobeTimeout = TimeSpan.FromSeconds(10);
    private readonly ExternalToolLocator _toolLocator;

    public ParallelVideoEncodingOutputVerifier(ExternalToolLocator toolLocator)
    {
        _toolLocator = toolLocator;
    }

    public void VerifyVideoOutput(string outputPath, AppLanguage language, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException(T(
                language,
                $"Av1an finished but did not produce an output file: {outputPath}",
                $"Av1an 已结束，但未生成输出文件：{outputPath}"));
        }

        var fileInfo = new FileInfo(outputPath);
        if (fileInfo.Length <= 0)
        {
            throw new InvalidOperationException(T(
                language,
                $"Av1an produced an empty output file: {outputPath}",
                $"Av1an 生成了空输出文件：{outputPath}"));
        }

        var ffprobePath = _toolLocator.ResolveFfprobe();
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("v:0");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=codec_type");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("csv=p=0");
        startInfo.ArgumentList.Add(outputPath);

        var result = ProcessProbeRunner.Run(
            startInfo,
            FfprobeTimeout,
            T(language, "ffprobe timed out while verifying the encoded video output.", "ffprobe 校验压制输出时超时。"),
            cancellationToken);
        if (result.ExitCode != 0 || !ContainsVideoStream(result.StandardOutput))
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new InvalidOperationException(T(
                language,
                $"The encoded output could not be verified as a readable video stream. {detail}",
                $"无法确认压制输出包含可读取的视频流。{detail}"));
        }
    }

    private static bool ContainsVideoStream(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(static line => string.Equals(line, "video", StringComparison.OrdinalIgnoreCase));
    }

    private static string T(AppLanguage language, string en, string zh) =>
        language == AppLanguage.English ? en : zh;
}

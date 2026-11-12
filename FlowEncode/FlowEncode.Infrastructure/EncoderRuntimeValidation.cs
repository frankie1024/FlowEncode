using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class EncoderRuntimeValidation
{
    private static readonly ConcurrentDictionary<string, string> HelpCache = new();
    private static readonly ConcurrentDictionary<string, bool> DepthCache = new();

    internal static void Validate(string encoder, string ffprobe, EncodingProfile profile, SourceVideoInfo? source,
        CancellationToken token)
    {
        RequestValidation.ValidateEncodingProfile(profile);
        var contract = VideoOutputContract.Resolve(profile, source);
        if (profile.Kind == EncoderKind.SvtAv1)
        {
            var help = HelpCache.GetOrAdd(Identity(encoder), _ => Run(encoder, ["--help"], token));
            var qpArguments = profile.RateControl is RateControlMode.Qp or RateControlMode.Cq
                ? EncoderCliParameters.BuildSvtQpArguments(profile.Quality) : string.Empty;
            if ((qpArguments.StartsWith("--cqp", StringComparison.Ordinal)
                    || EncoderCliParameters.Read(profile.AdditionalArguments).ContainsKey("--cqp"))
                && !help.Contains("--cqp", StringComparison.Ordinal))
                throw new InvalidOperationException("This SVT-AV1 version does not support fractional / extended CQP. Upgrade the encoder or select an integer QP from 1 to 63. / 当前 SVT 不支持小数或扩展 CQP，请更新编码器或选择 1–63 的整数 QP。");
            return;
        }

        var key = Identity(encoder) + "|" + Identity(ffprobe) + "|" + contract.BitDepth;
        if (DepthCache.ContainsKey(key)) return;
        var root = Path.Combine(Path.GetTempPath(), "FlowEncodeEncoderCapability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "probe.y4m");
            var output = Path.Combine(root, profile.Kind == EncoderKind.X264 ? "probe.264" : "probe.hevc");
            WriteY4m(input, contract.BitDepth);
            var arguments = new List<string> { "--preset", "ultrafast", "--output-depth", contract.BitDepth.ToString() };
            arguments.AddRange(profile.Kind == EncoderKind.X264
                ? ["--demuxer", "y4m", input, "-o", output]
                : ["--y4m", "--input", input, "-o", output]);
            var log = Run(encoder, arguments, token);
            if (log.Contains("falling back to default bit-depth", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{profile.Kind} cannot encode {contract.BitDepth}-bit output. Import a matching high-bit-depth / multilib build. / 当前编码器不支持目标位深，请导入高位深或 multilib 构建。");
            EncodedMediaVerifier.VerifyVideo(ffprobe, output, new VideoOutputContract(profile.Kind, contract.BitDepth, "420", 1), token);
            DepthCache[key] = true;
        }
        finally
        {
            BestEffortCleanup.DeleteDirectoryRecursively(root, "encoder capability probe");
        }
    }

    private static string Run(string path, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var result = ProcessProbeRunner.Run(info, TimeSpan.FromSeconds(30), "Encoder capability probe timed out. / 编码器能力检测超时。", token);
        var log = result.StandardOutput + Environment.NewLine + result.StandardError;
        if (result.ExitCode != 0) throw new InvalidOperationException($"Encoder capability probe failed. / 编码器能力检测失败：{log}");
        return log;
    }

    private static string Identity(string path)
    {
        var file = new FileInfo(path);
        var dlls = Directory.EnumerateFiles(file.DirectoryName!, "*.dll")
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Select(static value => new FileInfo(value))
            .Select(static value => $"{value.Name}:{value.Length}:{value.LastWriteTimeUtc.Ticks}");
        return $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}|{string.Join('|', dlls)}";
    }

    private static void WriteY4m(string path, int depth)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W64 H64 F24:1 Ip A0:0 C{(depth == 8 ? "420jpeg" : $"420p{depth}")}\nFRAME\n"));
        foreach (var plane in new[] { (64 * 64, 16), (32 * 32, 128), (32 * 32, 128) })
            for (var index = 0; index < plane.Item1; index++)
                if (depth == 8) writer.Write((byte)plane.Item2);
                else writer.Write((ushort)(plane.Item2 << (depth - 8)));
    }
}

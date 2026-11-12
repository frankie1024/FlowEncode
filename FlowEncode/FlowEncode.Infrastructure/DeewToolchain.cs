using System.Diagnostics;
using System.Text.RegularExpressions;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class DeewToolchain
{
    internal static bool IsSupportedVersion(string text)
    {
        var match = Regex.Match(text, @"(?<!\d)(\d+\.\d+\.\d+)(?!\d)");
        return match.Success && Version.TryParse(match.Value, out var version) && version >= new Version(5, 1, 0);
    }

    public static ProcessStartInfo StartInfo(string executable, string ffmpeg, string ffprobe)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        info.Environment["PATH"] = string.Join(Path.PathSeparator, new[] { Path.GetDirectoryName(ffmpeg), Path.GetDirectoryName(ffprobe), Environment.GetEnvironmentVariable("PATH") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    public static string Resolve(string deew, string name, string path)
    {
        var exeName = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        var directory = Path.GetDirectoryName(Path.GetFullPath(deew))!;
        var local = Path.Combine(directory, name.StartsWith("dee_", StringComparison.Ordinal) && name != "dee_shim" ? "dee" : "", exeName);
        if (File.Exists(local)) return local;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry.Trim('"'), exeName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException($"deew requires {exeName}. Searched {local}, then child process PATH. Use the standard filename; DEE 6.7 tools belong in the adjacent dee directory.");
    }

    public static async Task ValidateAsync(string deew, string ffmpeg, string ffprobe,
        DeewEffectiveOutputProfile profile, Action<string> log, CancellationToken token)
    {
        var environment = StartInfo(deew, ffmpeg, ffprobe);
        var version = await ProbeAsync(environment, "--version", token);
        var deewLine = version.Split('\n').FirstOrDefault(l => Regex.IsMatch(l.TrimStart(), @"^deew\s", RegexOptions.IgnoreCase)) ?? "";
        if (!IsSupportedVersion(deewLine)) throw new InvalidOperationException("FlowEncode 3.0 requires deew >=5.1.0. Upgrade the portable toolchain; legacy deew is supported by FlowEncode 2.0.2. " + deewLine);
        foreach (var name in new[] { "ffmpeg", "ffprobe" }.Concat(profile.RequiredTools))
        {
            var actual = Resolve(deew, name, environment.Environment["PATH"] ?? "");
            var dee = name.StartsWith("dee_", StringComparison.Ordinal) && name != "dee_shim";
            var info = StartInfo(actual, ffmpeg, ffprobe);
            var output = await ProbeAsync(info, dee ? "--help" : name is "ffmpeg" or "ffprobe" ? "-version" : "--version", token);
            if (dee)
            {
                var match = Regex.Match(output, @"Dolby\s+Encoding\s+Engine\s+version\s+(?<version>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
                if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var v) || v < new Version(6, 7, 0))
                    throw new InvalidOperationException($"{name} at {actual} requires a readable DEE version >=6.7.0.");
            }
            log($"{name}: {actual} — {output.Split('\n').FirstOrDefault()?.Trim()}");
        }
    }

    public static async Task<string> ProbeAsync(ProcessStartInfo info, string argument, CancellationToken token)
    {
        info.ArgumentList.Add(argument);
        var result = await ProcessProbeRunner.RunAsync(info, TimeSpan.FromSeconds(15), "deew tool probe timed out: " + info.FileName, token);
        var output = result.StandardOutput + "\n" + result.StandardError;
        if (result.ExitCode != 0) throw new InvalidOperationException($"{info.FileName} {argument} exited {result.ExitCode}: {output.Trim()}");
        return output;
    }
}

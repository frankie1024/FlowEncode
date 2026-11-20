using System.Diagnostics;
using System.Text.RegularExpressions;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class DeewToolchain
{
    internal static readonly Version MinimumDeeVersion = new(6, 7, 0);

    internal static bool IsSupportedVersion(string text)
    {
        var match = Regex.Match(text, @"(?<!\d)(\d+\.\d+\.\d+)(?!\d)");
        return match.Success && Version.TryParse(match.Value, out var version) && version >= Version.Parse(DeewArguments.MinimumVersion);
    }

    public static ProcessStartInfo StartInfo(string deew, string ffmpeg, string ffprobe) =>
        StartInfo(deew, BuildSearchPath(deew, ffmpeg, ffprobe));

    internal static ProcessStartInfo StartInfo(string executable, string searchPath)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        info.Environment["PATH"] = searchPath;
        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    internal static string BuildSearchPath(string deew, string ffmpeg, string ffprobe) =>
        string.Join(Path.PathSeparator, new[]
        {
            string.IsNullOrWhiteSpace(deew) ? null : Path.GetDirectoryName(Path.GetFullPath(deew)),
            Path.GetDirectoryName(ffmpeg),
            Path.GetDirectoryName(ffprobe),
            Environment.GetEnvironmentVariable("PATH")
        }.Where(directory => !string.IsNullOrWhiteSpace(directory)));

    public static string Resolve(string deew, string name, string path) =>
        FindTool(deew, name, path)
        ?? throw new FileNotFoundException($"deew requires {ExecutableName(name)}. Searched {AdjacentToolPath(deew, name)}, then child process PATH. Keep DEE tools under their standard names in the adjacent dee directory, next to deew, or on PATH.");

    internal static string? FindTool(string deew, string name, string path)
    {
        if (!string.IsNullOrWhiteSpace(deew))
        {
            var local = AdjacentToolPath(deew, name);
            if (File.Exists(local)) return local;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry.Trim('"'), ExecutableName(name));
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static string AdjacentToolPath(string deew, string name) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(deew))!, IsDeeComponent(name) ? "dee" : "", ExecutableName(name));

    private static string ExecutableName(string name) => name + (OperatingSystem.IsWindows() ? ".exe" : "");

    private static bool IsDeeComponent(string name) => name.StartsWith("dee_", StringComparison.Ordinal) && name != "dee_shim";

    internal static Version? ReadDeeVersion(string output)
    {
        var match = Regex.Match(output, @"Dolby\s+Encoding\s+Engine\s+version\s+(?<version>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version) ? version : null;
    }

    public static async Task ValidateAsync(string deew, string ffmpeg, string ffprobe,
        DeewEffectiveOutputProfile profile, Action<string> log, CancellationToken token)
    {
        var environment = StartInfo(deew, ffmpeg, ffprobe);
        var searchPath = environment.Environment["PATH"] ?? "";
        var version = await ProbeAsync(environment, "--version", token);
        var deewLine = version.Split('\n').FirstOrDefault(l => Regex.IsMatch(l.TrimStart(), @"^deew\s", RegexOptions.IgnoreCase)) ?? "";
        if (!IsSupportedVersion(deewLine)) throw new InvalidOperationException($"FlowEncode requires deew >={DeewArguments.MinimumVersion}. Upgrade the portable deew executable. " + deewLine);
        foreach (var name in new[] { "ffmpeg", "ffprobe" }.Concat(profile.RequiredTools))
        {
            var actual = Resolve(deew, name, searchPath);
            var dee = IsDeeComponent(name);
            var info = StartInfo(actual, searchPath);
            var output = await ProbeAsync(info, dee ? "--help" : name is "ffmpeg" or "ffprobe" ? "-version" : "--version", token);
            if (dee)
            {
                var versionFound = ReadDeeVersion(output);
                if (versionFound is null || versionFound < MinimumDeeVersion)
                    throw new InvalidOperationException($"{name} at {actual} requires a readable DEE version >={MinimumDeeVersion}.");
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

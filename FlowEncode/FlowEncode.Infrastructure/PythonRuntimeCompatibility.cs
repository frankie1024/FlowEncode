namespace FlowEncode.Infrastructure;

internal static class PythonRuntimeCompatibility
{
    public static readonly Version MinimumSupportedVersion = new(3, 12);
    public static readonly IReadOnlyList<string> EnvironmentVariableNames = ["FLOWENCODE_PYTHON", "PYTHON_PATH", "PYTHON_EXE", "PYTHON"];

    public static bool IsSupportedVersion(Version version)
    {
        return version.Major == 3 && version >= MinimumSupportedVersion;
    }

    public static bool IsTargetMinor(Version version)
    {
        return version.Major == 3 && version.Minor == 14;
    }

    public static bool IsSupportedRuntime(Version version, bool is64Bit)
    {
        return IsSupportedVersion(version) && is64Bit;
    }

    public static string BuildProbeScript()
    {
        return string.Join(
            "\n",
            "import platform, struct, sys",
            "print('.'.join(map(str, sys.version_info[:3])))",
            "print(sys.executable)",
            "print(struct.calcsize('P') * 8)",
            "print(platform.machine())",
            "import importlib.util",
            "print('VS_PRESENT' if importlib.util.find_spec('vapoursynth') else 'VS_ABSENT')");
    }

    public static IEnumerable<string> EnumerateKnownPythonExecutablePaths()
    {
        var candidates = new List<(Version Version, string Path)>();
        foreach (var root in EnumerateKnownPythonRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root, "Python*", SearchOption.TopDirectoryOnly))
            {
                var candidatePath = Path.Combine(directory, "python.exe");
                if (File.Exists(candidatePath))
                {
                    candidates.Add((TryParsePythonDirectoryVersion(directory, out var version) ? version : new Version(0, 0), candidatePath));
                }
            }
        }

        return candidates
            .OrderByDescending(static item => item.Version)
            .Select(static item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IEnumerable<string> EnumerateEnvironmentPythonExecutablePaths()
    {
        foreach (var variableName in EnvironmentVariableNames)
        {
            var value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process)
                ?? Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine);

            var resolved = ResolvePythonExecutablePath(value);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                yield return resolved;
            }
        }
    }

    public static IEnumerable<string> EnumerateCompatiblePythonExecutablePaths()
    {
        return BuildCompatiblePythonRuntimes().Select(static item => item.ExecutablePath);
    }

    public static IReadOnlyList<string> EnumeratePythonRuntimeDirectories(string pythonExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(pythonExecutablePath))
        {
            return [];
        }

        var root = Path.GetDirectoryName(pythonExecutablePath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return [];
        }

        var result = new List<string>();

        void TryAdd(string path)
        {
            if (Directory.Exists(path)
                && !result.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(path);
            }
        }

        TryAdd(root);
        TryAdd(Path.Combine(root, "Scripts"));
        TryAdd(Path.Combine(root, "Lib", "site-packages", "vapoursynth"));
        TryAdd(Path.Combine(root, "site-packages", "vapoursynth"));

        return result;
    }

    private static IEnumerable<string> EnumerateKnownPythonRoots()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        return roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParsePythonDirectoryVersion(string directory, out Version version)
    {
        var name = Path.GetFileName(directory);
        if (!string.IsNullOrWhiteSpace(name)
            && name.StartsWith("Python", StringComparison.OrdinalIgnoreCase)
            && name.Length > "Python".Length)
        {
            var suffix = name["Python".Length..];
            if (suffix.Length >= 2
                && int.TryParse(suffix[0].ToString(), out var major)
                && int.TryParse(suffix[1..], out var minor))
            {
                version = new Version(major, minor);
                return true;
            }
        }

        version = new Version(0, 0);
        return false;
    }

    private static IReadOnlyList<PythonRuntimeProbe> BuildCompatiblePythonRuntimes()
    {
        var candidates = EnumerateEnvironmentPythonExecutablePaths()
            .Concat(EnumeratePathPythonExecutablePaths())
            .Concat(EnumerateKnownPythonExecutablePaths())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var results = new List<PythonRuntimeProbe>();

        foreach (var candidate in candidates)
        {
            var probe = ProbePythonRuntime(candidate);
            if (probe is not null && IsSupportedRuntime(probe.Version, probe.Is64Bit))
            {
                results.Add(probe);
            }
        }

        var bindings = EnumerateEnvironmentPythonExecutablePaths().ToArray();
        var pathRuntimes = EnumeratePathPythonExecutablePaths().ToArray();
        return results
            .OrderBy(item => SelectionRank(item.ExecutablePath, item.HasVapourSynth, item.Version, bindings, pathRuntimes))
            .ThenByDescending(static item => item.Version)
            .ToArray();
    }

    // An existing binding wins. Otherwise keep a VS environment before choosing
    // the fresh-install baseline; installing another Python must not migrate it.
    internal static int SelectionRank(string path, bool hasVapourSynth, Version version,
        IReadOnlyList<string> bindings, IReadOnlyList<string> pathRuntimes)
    {
        for (var index = 0; index < bindings.Count; index++)
            if (string.Equals(path, bindings[index], StringComparison.OrdinalIgnoreCase)) return index;
        if (hasVapourSynth)
            return bindings.Count + (pathRuntimes.Contains(path, StringComparer.OrdinalIgnoreCase) ? 0 : 1);
        return bindings.Count + (IsTargetMinor(version) ? 2 : 3);
    }

    internal static IEnumerable<string> EnumeratePathPythonExecutablePaths()
    {
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(root => Path.Combine(root, "python.exe"))
            .Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static string? FindOwningPython(string? toolPath)
    {
        if (string.IsNullOrWhiteSpace(toolPath)) return null;
        var directory = Directory.Exists(toolPath) ? new DirectoryInfo(toolPath) : new FileInfo(toolPath).Directory;
        for (var level = 0; directory is not null && level < 6; level++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "python.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static PythonRuntimeProbe? ProbePythonRuntime(string pythonPath)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(BuildProbeScript());
            process.Start();

            if (!process.WaitForExit(3000))
            {
                process.Kill(true);
                return null;
            }

            var output = string.Concat(process.StandardOutput.ReadToEnd(), Environment.NewLine, process.StandardError.ReadToEnd());
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (process.ExitCode != 0
                || lines.Length < 3
                || !Version.TryParse(lines[0], out var version)
                || !int.TryParse(lines[2], out var pointerSize))
            {
                return null;
            }

            var executablePath = File.Exists(lines[1])
                ? Path.GetFullPath(lines[1])
                : Path.GetFullPath(pythonPath);
            return new PythonRuntimeProbe(version, executablePath, pointerSize == 64, lines.Contains("VS_PRESENT"));
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolvePythonExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (File.Exists(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        if (Directory.Exists(expanded))
        {
            var candidate = Path.Combine(expanded, "python.exe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private sealed record PythonRuntimeProbe(Version Version, string ExecutablePath, bool Is64Bit, bool HasVapourSynth);
}

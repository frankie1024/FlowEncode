using System.Diagnostics;

namespace FlowEncode.Infrastructure;

internal static class VapourSynthRuntimePathResolver
{
    public static void EnrichProcessPath(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var directories = CollectSearchDirectories();
        if (directories.Count == 0)
        {
            return;
        }

        var currentPath = startInfo.Environment.TryGetValue("PATH", out var existing)
            ? existing ?? string.Empty
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        var pathSegments = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        foreach (var directory in directories.Reverse())
        {
            if (pathSegments.Any(item => string.Equals(item, directory, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            pathSegments.Insert(0, directory);
        }

        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, pathSegments);
    }

    public static string? ResolvePythonSidecarVspipe(string root)
    {
        foreach (var vapourSynthDirectory in EnumeratePythonVapourSynthDirectories(root))
        {
            var candidate = Path.Combine(vapourSynthDirectory, "vspipe.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> CollectPythonScriptDirectories()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || !seen.Add(path))
            {
                return;
            }

            result.Add(path);
        }

        foreach (var root in EnumeratePathRoots())
        {
            foreach (var directory in EnumeratePythonScriptDirectories(root))
            {
                TryAdd(directory);
            }
        }

        foreach (var pythonPath in PythonRuntimeCompatibility.EnumerateCompatiblePythonExecutablePaths())
        {
            foreach (var directory in PythonRuntimeCompatibility.EnumeratePythonRuntimeDirectories(pythonPath))
            {
                if (directory.EndsWith($"{Path.DirectorySeparatorChar}Scripts", StringComparison.OrdinalIgnoreCase))
                {
                    TryAdd(directory);
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<string> CollectSearchDirectories()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || !seen.Add(path))
            {
                return;
            }

            result.Add(path);
        }

        foreach (var root in EnumeratePathRoots())
        {
            TryAdd(root);

            foreach (var vapourSynthDirectory in EnumeratePythonVapourSynthDirectories(root))
            {
                TryAdd(vapourSynthDirectory);
            }
        }

        foreach (var pythonPath in PythonRuntimeCompatibility.EnumerateCompatiblePythonExecutablePaths())
        {
            foreach (var directory in PythonRuntimeCompatibility.EnumeratePythonRuntimeDirectories(pythonPath))
            {
                TryAdd(directory);
            }
        }

        TryAdd(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VapourSynth"));
        TryAdd(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VapourSynth", "core64"));

        return result;
    }

    public static IEnumerable<string> EnumeratePythonScriptDirectories(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var directory = new DirectoryInfo(root);
        if (directory.Name.Equals("Scripts", StringComparison.OrdinalIgnoreCase))
        {
            yield return directory.FullName;
            yield break;
        }

        if (directory.Name.StartsWith("Python", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(directory.FullName, "python.exe"))
            || File.Exists(Path.Combine(directory.FullName, "py.exe")))
        {
            yield return Path.Combine(directory.FullName, "Scripts");
        }
    }

    private static IEnumerable<string> EnumeratePythonVapourSynthDirectories(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            yield break;
        }

        var directory = new DirectoryInfo(root);
        if (directory.Name.Equals("Scripts", StringComparison.OrdinalIgnoreCase)
            && directory.Parent is not null)
        {
            yield return Path.Combine(directory.Parent.FullName, "Lib", "site-packages", "vapoursynth");
            yield return Path.Combine(directory.Parent.FullName, "site-packages", "vapoursynth");
        }

        if (directory.Name.StartsWith("Python", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(directory.FullName, "Lib", "site-packages", "vapoursynth");
            yield return Path.Combine(directory.FullName, "site-packages", "vapoursynth");
        }
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
}

using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class CliEnvironmentIntegrationService
{
    private const int ManifestSchemaVersion = 1;
    private static readonly nint HwndBroadcast = new(0xffff);
    private const uint WmSettingChange = 0x001a;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly string[] ManagedVariableNames =
    [
        "FLOWENCODE_WORKSPACE",
        "FLOWENCODE_TOOLS",
        "FLOWENCODE_PYTHON"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly LocalAppPaths _paths;
    private readonly object _gate = new();

    public CliEnvironmentIntegrationService(LocalAppPaths paths)
    {
        _paths = paths;
    }

    public void Synchronize(string? pythonPath = null)
    {
        lock (_gate)
        {
            var manifest = LoadManifestUnsafe();
            var selectedPythonPath = ResolvePythonPath(pythonPath);
            var desiredPathEntries = BuildDesiredPathEntries(selectedPythonPath);
            var currentUserPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
            var pathPlan = BuildUserPathSynchronizationPlan(
                currentUserPath,
                manifest.ManagedUserPathEntries,
                desiredPathEntries);
            manifest = RelocateWorkspaceComponentOwnership(manifest, _paths.RootPath);
            var nextUserPath = pathPlan.UserPath;
            var environmentSnapshot = CaptureEnvironmentSnapshot(currentUserPath);
            try
            {
                if (!string.Equals(currentUserPath, nextUserPath, StringComparison.Ordinal))
                {
                    Environment.SetEnvironmentVariable("PATH", nextUserPath, EnvironmentVariableTarget.User);
                }

                var previousVariables = new Dictionary<string, string?>(manifest.PreviousUserEnvironmentVariables, StringComparer.OrdinalIgnoreCase);
                SetManagedUserVariable(previousVariables, "FLOWENCODE_WORKSPACE", _paths.RootPath);
                SetManagedUserVariable(previousVariables, "FLOWENCODE_TOOLS", _paths.ToolsRootPath);
                if (!string.IsNullOrWhiteSpace(selectedPythonPath))
                {
                    SetManagedUserVariable(previousVariables, "FLOWENCODE_PYTHON", selectedPythonPath);
                }
                else
                {
                    RestoreManagedUserVariable(previousVariables, "FLOWENCODE_PYTHON");
                }

                UpdateProcessPath(
                    desiredPathEntries,
                    manifest.ManagedUserPathEntries.Select(static entry => entry.Value));
                manifest = manifest with
                {
                    ManagedUserPathEntries = pathPlan.ManagedEntries,
                    PreviousUserEnvironmentVariables = previousVariables
                };
                SaveManifestUnsafe(manifest);
            }
            catch
            {
                RestoreEnvironmentSnapshot(environmentSnapshot);
                throw;
            }

            BroadcastEnvironmentChange();
        }
    }

    private static EnvironmentSnapshot CaptureEnvironmentSnapshot(string currentUserPath)
    {
        return new EnvironmentSnapshot(
            currentUserPath,
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process),
            ManagedVariableNames.ToDictionary(
                static name => name,
                static name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
                StringComparer.OrdinalIgnoreCase),
            ManagedVariableNames.ToDictionary(
                static name => name,
                static name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process),
                StringComparer.OrdinalIgnoreCase));
    }

    private void RestoreEnvironmentSnapshot(EnvironmentSnapshot snapshot)
    {
        try
        {
            Environment.SetEnvironmentVariable("PATH", snapshot.UserPath, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("PATH", snapshot.ProcessPath, EnvironmentVariableTarget.Process);
            foreach (var name in ManagedVariableNames)
            {
                Environment.SetEnvironmentVariable(name, snapshot.UserVariables[name], EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable(name, snapshot.ProcessVariables[name], EnvironmentVariableTarget.Process);
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Write(
                _paths,
                nameof(CliEnvironmentIntegrationService),
                $"Failed to roll back CLI environment synchronization. {ex.GetType().Name}: {ex.Message}",
                AppDiagnosticSeverity.Error,
                exception: ex);
        }
    }

    public async Task VerifyCliEnvironmentAsync(
        SetupDependencyKind dependencyKind,
        string? pythonPath = null,
        CancellationToken cancellationToken = default)
    {
        var selectedPythonPath = ResolvePythonPath(pythonPath);
        var cleanPath = BuildCleanShellPath(
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty,
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty);
        var commands = BuildInstalledCliCommands(dependencyKind, selectedPythonPath);
        foreach (var command in commands)
        {
            foreach (var shell in new[] { CliShell.Cmd, CliShell.PowerShell })
            {
                await RunShellProbeAsync(shell, command, cleanPath, cancellationToken);
            }
        }
    }

    internal ManagedComponentOwnership? GetComponentOwnership(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var manifest = LoadManifestUnsafe();
            return manifest.Components.TryGetValue(key, out var ownership) ? ownership : null;
        }
    }

    internal void RecordComponentOwnership(
        string key,
        bool ownsComponent,
        string installationPath,
        string installedVersion,
        IEnumerable<string>? ownedItems = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var manifest = LoadManifestUnsafe();
            var components = new Dictionary<string, ManagedComponentOwnership>(manifest.Components, StringComparer.OrdinalIgnoreCase);
            var sameInstallation = components.TryGetValue(key, out var existing)
                && !string.IsNullOrWhiteSpace(existing.InstallationPath)
                && !string.IsNullOrWhiteSpace(installationPath)
                && AreSamePath(existing.InstallationPath, installationPath);
            var existingItems = sameInstallation
                ? existing!.OwnedItems
                : [];
            var mergedItems = existingItems
                .Concat(ownedItems ?? [])
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            components[key] = new ManagedComponentOwnership(
                ownsComponent || sameInstallation && existing!.OwnsComponent,
                installationPath,
                installedVersion,
                mergedItems,
                DateTimeOffset.UtcNow);
            SaveManifestUnsafe(manifest with { Components = components });
        }
    }

    internal void RemoveComponentOwnership(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var manifest = LoadManifestUnsafe();
            var components = new Dictionary<string, ManagedComponentOwnership>(manifest.Components, StringComparer.OrdinalIgnoreCase);
            if (components.Remove(key))
            {
                SaveManifestUnsafe(manifest with { Components = components });
            }
        }
    }

    internal static string GetPythonComponentKey() => "python";

    internal static string GetPythonPackageComponentKey(string packageName)
        => $"python-package:{packageName.Trim().ToLowerInvariant()}";

    internal static string GetVsPluginBundleComponentKey() => "vsrepo-plugin-bundle";

    internal static string GetVsrepoExtractorComponentKey() => "vsrepo-extractor";

    internal static string GetPortable7ZipComponentKey() => "portable-7zip";

    private static EnvironmentOwnershipManifest RelocateWorkspaceComponentOwnership(
        EnvironmentOwnershipManifest manifest,
        string currentWorkspaceRoot)
    {
        var currentRoot = Path.GetFullPath(currentWorkspaceRoot);
        var previousToolsPath = manifest.ManagedUserPathEntries
            .Select(static entry => entry.Value)
            .FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), "tools", StringComparison.OrdinalIgnoreCase)
                && !AreSamePath(path, Path.Combine(currentRoot, "tools")));
        var previousRoot = string.IsNullOrWhiteSpace(previousToolsPath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(previousToolsPath));
        if (string.IsNullOrWhiteSpace(previousRoot) || AreSamePath(previousRoot, currentRoot))
        {
            return manifest;
        }

        var components = manifest.Components.ToDictionary(
            static pair => pair.Key,
            pair => RelocateComponentOwnership(pair.Value, previousRoot, currentRoot),
            StringComparer.OrdinalIgnoreCase);
        return manifest with { Components = components };
    }

    internal static ManagedComponentOwnership RelocateComponentOwnership(
        ManagedComponentOwnership ownership,
        string previousWorkspaceRoot,
        string currentWorkspaceRoot)
    {
        var installationPath = RelocateExistingWorkspacePath(
            ownership.InstallationPath,
            previousWorkspaceRoot,
            currentWorkspaceRoot);
        var ownedItems = ownership.OwnedItems
            .Select(path => RelocateExistingWorkspacePath(path, previousWorkspaceRoot, currentWorkspaceRoot))
            .ToArray();
        if (string.Equals(installationPath, ownership.InstallationPath, StringComparison.OrdinalIgnoreCase)
            && ownedItems.SequenceEqual(ownership.OwnedItems, StringComparer.OrdinalIgnoreCase))
        {
            return ownership;
        }

        return ownership with
        {
            InstallationPath = installationPath,
            OwnedItems = ownedItems,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string RelocateExistingWorkspacePath(
        string path,
        string previousWorkspaceRoot,
        string currentWorkspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            var previousRoot = Path.GetFullPath(previousWorkspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(path);
            var relativePath = Path.GetRelativePath(previousRoot, normalizedPath);
            if (relativePath.Equals("..", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return path;
            }

            var relocatedPath = Path.GetFullPath(Path.Combine(currentWorkspaceRoot, relativePath));
            return File.Exists(relocatedPath) || Directory.Exists(relocatedPath)
                ? relocatedPath
                : path;
        }
        catch
        {
            return path;
        }
    }

    internal static UserPathSynchronizationPlan BuildUserPathSynchronizationPlan(
        string currentUserPath,
        IReadOnlyList<ManagedPathEntry> previousManagedEntries,
        IReadOnlyList<string> desiredPathEntries)
    {
        var currentSegments = SplitPath(currentUserPath).ToList();
        RestorePreviouslyManagedPathEntries(currentSegments, previousManagedEntries);

        var nextManagedEntries = new List<ManagedPathEntry>();
        foreach (var desiredPath in DeduplicatePaths(desiredPathEntries))
        {
            var existingIndex = FindPathIndex(currentSegments, desiredPath);
            var wasPreexisting = existingIndex >= 0;
            if (wasPreexisting)
            {
                currentSegments.RemoveAt(existingIndex);
            }

            nextManagedEntries.Add(new ManagedPathEntry(desiredPath, wasPreexisting, Math.Max(existingIndex, 0)));
        }

        currentSegments.InsertRange(0, nextManagedEntries.Select(static entry => entry.Value));
        return new UserPathSynchronizationPlan(
            string.Join(Path.PathSeparator, DeduplicatePaths(currentSegments)),
            nextManagedEntries);
    }

    internal static string BuildCleanShellPath(string userPath, string machinePath)
    {
        return string.Join(
            Path.PathSeparator,
            DeduplicatePaths(SplitPath(machinePath).Concat(SplitPath(userPath))));
    }

    private IReadOnlyList<string> BuildDesiredPathEntries(string? pythonPath)
    {
        var entries = new List<string>
        {
            _paths.ToolsRootPath,
            _paths.GetManagedExternalToolDirectory(ExternalToolKind.Ffmpeg),
            _paths.GetManagedExternalToolDirectory(ExternalToolKind.Av1an),
            _paths.GetBinaryDirectory(EncoderKind.X264, EncoderArchitecture.X64),
            _paths.GetBinaryDirectory(EncoderKind.X265, EncoderArchitecture.X64),
            _paths.GetBinaryDirectory(EncoderKind.SvtAv1, EncoderArchitecture.X64)
        };

        if (!string.IsNullOrWhiteSpace(pythonPath))
        {
            var pythonRoot = Path.GetDirectoryName(pythonPath);
            if (!string.IsNullOrWhiteSpace(pythonRoot))
            {
                entries.Add(pythonRoot);
                entries.Add(Path.Combine(pythonRoot, "Scripts"));
            }
        }

        return DeduplicatePaths(entries).ToArray();
    }

    private IReadOnlyList<CliCommandSpec> BuildInstalledCliCommands(
        SetupDependencyKind dependencyKind,
        string? pythonPath)
    {
        var commands = new List<CliCommandSpec>();
        if (dependencyKind is SetupDependencyKind.Python312
                or SetupDependencyKind.VapourSynth
                or SetupDependencyKind.Vsrepo
                or SetupDependencyKind.VsPluginBundle
                or SetupDependencyKind.Awsmfunc
                or SetupDependencyKind.Vsjetpack
            && !string.IsNullOrWhiteSpace(pythonPath)
            && File.Exists(pythonPath))
        {
            var pythonRoot = Path.GetDirectoryName(pythonPath)!;
            var scriptsRoot = Path.Combine(pythonRoot, "Scripts");
            commands.Add(new CliCommandSpec("python", ["--version"]));
            AddIfExists(commands, Path.Combine(scriptsRoot, "pip.exe"), "pip", "--version");
            if (dependencyKind == SetupDependencyKind.VapourSynth)
            {
                AddIfExists(commands, Path.Combine(scriptsRoot, "vspipe.exe"), "vspipe", "--version");
            }

            if (dependencyKind is SetupDependencyKind.Vsrepo or SetupDependencyKind.VsPluginBundle)
            {
                AddIfExists(commands, Path.Combine(scriptsRoot, "vsrepo.exe"), "vsrepo", "installed");
            }
        }

        if (dependencyKind == SetupDependencyKind.FfmpegBundle)
        {
            var ffmpegDirectory = _paths.GetManagedExternalToolDirectory(ExternalToolKind.Ffmpeg);
            AddIfExists(commands, Path.Combine(ffmpegDirectory, "ffmpeg.exe"), "ffmpeg", "-version");
            AddIfExists(commands, Path.Combine(ffmpegDirectory, "ffprobe.exe"), "ffprobe", "-version");
        }

        if (dependencyKind == SetupDependencyKind.Av1an)
        {
            AddIfExists(
                commands,
                _paths.GetManagedExternalToolPath(ExternalToolKind.Av1an),
                "av1an",
                "--version");
        }

        if (dependencyKind == SetupDependencyKind.X264)
        {
            AddIfExists(commands, _paths.GetBinaryPath(EncoderKind.X264, EncoderArchitecture.X64), "x264", "--version");
        }

        if (dependencyKind == SetupDependencyKind.X265)
        {
            AddIfExists(commands, _paths.GetBinaryPath(EncoderKind.X265, EncoderArchitecture.X64), "x265", "--version");
        }

        if (dependencyKind == SetupDependencyKind.SvtAv1)
        {
            AddIfExists(commands, _paths.GetBinaryPath(EncoderKind.SvtAv1, EncoderArchitecture.X64), "SvtAv1EncApp", "--version");
        }

        return commands;
    }

    private static void AddIfExists(
        ICollection<CliCommandSpec> commands,
        string expectedPath,
        string commandName,
        params string[] arguments)
    {
        if (File.Exists(expectedPath))
        {
            commands.Add(new CliCommandSpec(commandName, arguments));
        }
    }

    private async Task RunShellProbeAsync(
        CliShell shell,
        CliCommandSpec command,
        string cleanPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = shell == CliShell.Cmd ? "cmd.exe" : "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _paths.RootPath
        };
        startInfo.Environment["PATH"] = cleanPath;
        if (shell == CliShell.Cmd)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(string.Join(" ", new[] { command.Name }.Concat(command.Arguments.Select(QuoteShellArgument))));
        }
        else
        {
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add($"& '{command.Name.Replace("'", "''", StringComparison.Ordinal)}' {string.Join(" ", command.Arguments.Select(argument => $"'{argument.Replace("'", "''", StringComparison.Ordinal)}'"))}");
        }

        var result = await ProcessProbeRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(30),
            $"CLI verification timed out for {command.Name} in {shell}.",
            cancellationToken);
        if (result.ExitCode != 0)
        {
            var output = string.Concat(result.StandardOutput, Environment.NewLine, result.StandardError).Trim();
            throw new InvalidOperationException(
                $"CLI verification failed for '{command.Name}' in {shell} with exit code {result.ExitCode}: {output}");
        }
    }

    private static string QuoteShellArgument(string value)
        => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    private static string? ResolvePythonPath(string? pythonPath)
    {
        if (!string.IsNullOrWhiteSpace(pythonPath) && File.Exists(pythonPath))
        {
            return Path.GetFullPath(pythonPath);
        }

        var configuredPath = Environment.GetEnvironmentVariable("FLOWENCODE_PYTHON", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return PythonRuntimeCompatibility.EnumerateKnownPythonExecutablePaths()
            .Concat(PythonRuntimeCompatibility.EnumerateEnvironmentPythonExecutablePaths())
            .FirstOrDefault(File.Exists);
    }

    private static void RestorePreviouslyManagedPathEntries(
        List<string> currentSegments,
        IReadOnlyList<ManagedPathEntry> managedEntries)
    {
        foreach (var entry in managedEntries)
        {
            var currentIndex = FindPathIndex(currentSegments, entry.Value);
            if (currentIndex >= 0)
            {
                currentSegments.RemoveAt(currentIndex);
            }
        }

        foreach (var entry in managedEntries
                     .Where(static entry => entry.WasPreexisting)
                     .OrderBy(static entry => entry.OriginalIndex))
        {
            var insertIndex = Math.Clamp(entry.OriginalIndex, 0, currentSegments.Count);
            currentSegments.Insert(insertIndex, entry.Value);
        }
    }

    private static void SetManagedUserVariable(
        IDictionary<string, string?> previousVariables,
        string name,
        string value)
    {
        if (!previousVariables.ContainsKey(name))
        {
            previousVariables[name] = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
    }

    private static void RestoreManagedUserVariable(
        IReadOnlyDictionary<string, string?> previousVariables,
        string name)
    {
        if (!previousVariables.TryGetValue(name, out var previousValue))
        {
            return;
        }

        Environment.SetEnvironmentVariable(name, previousValue, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, previousValue, EnvironmentVariableTarget.Process);
    }

    internal static string BuildProcessPathSynchronizationValue(
        string currentProcessPath,
        IReadOnlyList<string> desiredPathEntries,
        IEnumerable<string> previousManagedEntries)
    {
        var pathsToReplace = desiredPathEntries.Concat(previousManagedEntries).ToArray();
        var processSegments = SplitPath(currentProcessPath)
            .Where(segment => pathsToReplace.All(managed => !AreSamePath(segment, managed)))
            .ToList();
        processSegments.InsertRange(0, desiredPathEntries);
        return string.Join(Path.PathSeparator, DeduplicatePaths(processSegments));
    }

    private static void UpdateProcessPath(
        IReadOnlyList<string> desiredPathEntries,
        IEnumerable<string> previousManagedEntries)
    {
        Environment.SetEnvironmentVariable(
            "PATH",
            BuildProcessPathSynchronizationValue(
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty,
                desiredPathEntries,
                previousManagedEntries),
            EnvironmentVariableTarget.Process);
    }

    private EnvironmentOwnershipManifest LoadManifestUnsafe()
    {
        if (!File.Exists(_paths.EnvironmentOwnershipPath))
        {
            return EnvironmentOwnershipManifest.Empty;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<EnvironmentOwnershipManifest>(
                File.ReadAllText(_paths.EnvironmentOwnershipPath),
                JsonOptions);
            return manifest is { SchemaVersion: ManifestSchemaVersion }
                ? NormalizeManifest(manifest)
                : EnvironmentOwnershipManifest.Empty;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Write(
                _paths,
                nameof(CliEnvironmentIntegrationService),
                $"Failed to read environment ownership manifest. {ex.GetType().Name}: {ex.Message}");
            return EnvironmentOwnershipManifest.Empty;
        }
    }

    private void SaveManifestUnsafe(EnvironmentOwnershipManifest manifest)
    {
        PersistentFileWriter.WriteAllText(
            _paths.EnvironmentOwnershipPath,
            JsonSerializer.Serialize(NormalizeManifest(manifest), JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            message => AppDiagnosticsLog.Write(_paths, nameof(CliEnvironmentIntegrationService), message));
    }

    private static EnvironmentOwnershipManifest NormalizeManifest(EnvironmentOwnershipManifest manifest)
    {
        return manifest with
        {
            ManagedUserPathEntries = manifest.ManagedUserPathEntries ?? [],
            PreviousUserEnvironmentVariables = new Dictionary<string, string?>(
                manifest.PreviousUserEnvironmentVariables ?? new Dictionary<string, string?>(),
                StringComparer.OrdinalIgnoreCase),
            Components = new Dictionary<string, ManagedComponentOwnership>(
                manifest.Components ?? new Dictionary<string, ManagedComponentOwnership>(),
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IEnumerable<string> SplitPath(string value)
    {
        return value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static entry => entry.Trim().Trim('"'))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry));
    }

    private static IEnumerable<string> DeduplicatePaths(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var trimmed = path.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparisonValue = NormalizePathForComparison(trimmed);
            if (seen.Add(comparisonValue))
            {
                yield return trimmed;
            }
        }
    }

    private static int FindPathIndex(IReadOnlyList<string> paths, string candidate)
    {
        for (var index = 0; index < paths.Count; index++)
        {
            if (AreSamePath(paths[index], candidate))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool AreSamePath(string left, string right)
        => string.Equals(NormalizePathForComparison(left), NormalizePathForComparison(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePathForComparison(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static void BroadcastEnvironmentChange()
    {
        try
        {
            _ = SendMessageTimeout(
                HwndBroadcast,
                WmSettingChange,
                UIntPtr.Zero,
                "Environment",
                SmtoAbortIfHung,
                2000,
                out _);
        }
        catch
        {
            // The registry update is authoritative; the broadcast only refreshes already-running shells.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);
}

internal enum CliShell
{
    Cmd,
    PowerShell
}

internal sealed record CliCommandSpec(string Name, IReadOnlyList<string> Arguments);

internal sealed record EnvironmentSnapshot(
    string UserPath,
    string? ProcessPath,
    IReadOnlyDictionary<string, string?> UserVariables,
    IReadOnlyDictionary<string, string?> ProcessVariables);

internal sealed record EnvironmentOwnershipManifest(
    int SchemaVersion,
    IReadOnlyList<ManagedPathEntry> ManagedUserPathEntries,
    IReadOnlyDictionary<string, string?> PreviousUserEnvironmentVariables,
    IReadOnlyDictionary<string, ManagedComponentOwnership> Components)
{
    public static EnvironmentOwnershipManifest Empty { get; } = new(
        SchemaVersion: 1,
        ManagedUserPathEntries: [],
        PreviousUserEnvironmentVariables: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        Components: new Dictionary<string, ManagedComponentOwnership>(StringComparer.OrdinalIgnoreCase));
}

internal sealed record ManagedPathEntry(string Value, bool WasPreexisting, int OriginalIndex);

internal sealed record UserPathSynchronizationPlan(
    string UserPath,
    IReadOnlyList<ManagedPathEntry> ManagedEntries);

internal sealed record ManagedComponentOwnership(
    bool OwnsComponent,
    string InstallationPath,
    string InstalledVersion,
    IReadOnlyList<string> OwnedItems,
    DateTimeOffset UpdatedAtUtc);

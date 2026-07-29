using FlowEncode.Domain;
using System.Diagnostics;
using System.Text.Json;

namespace FlowEncode.Infrastructure;

public sealed class LocalAppPaths
{
    private const string AppFolderName = "FlowEncode";
    private const string WorkspaceRootPathPropertyName = "workspaceRootPath";
    private readonly object _startupWorkspaceRecoveryGate = new();
    private readonly object _workspaceRootChangeGate = new();
    private WorkspacePathSet _workspacePaths = null!;
    private WorkspaceRootRecoveryInfo? _startupWorkspaceRecoveryInfo;

    public LocalAppPaths()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), null)
    {
    }

    internal LocalAppPaths(
        string localApplicationDataPath,
        string? installRootPathOverride = null,
        IReadOnlyList<string>? startupFallbackWorkspaceRootCandidates = null)
    {
        LocalStateRootPath = Path.Combine(
            localApplicationDataPath,
            AppFolderName);
        InstallRootPath = string.IsNullOrWhiteSpace(installRootPathOverride)
            ? ResolveExecutableDirectory(LocalStateRootPath)
            : Path.GetFullPath(installRootPathOverride);
        DataRootPath = Path.Combine(LocalStateRootPath, "data");
        SettingsRootPath = Path.Combine(DataRootPath, "settings");
        LocalizationRootPath = Path.Combine(DataRootPath, "localization");
        LogsRootPath = Path.Combine(DataRootPath, "logs");
        SettingsPath = Path.Combine(SettingsRootPath, "settings.json");
        SetupGuideCachePath = Path.Combine(SettingsRootPath, "setup-guide-cache.json");
        EnvironmentOwnershipPath = Path.Combine(SettingsRootPath, "environment-ownership.json");

        var configuredWorkspaceRootPath = ReadConfiguredWorkspaceRootPath(localApplicationDataPath);
        var workspaceRootPath = ResolveStartupWorkspaceRootPath(
            configuredWorkspaceRootPath,
            localApplicationDataPath,
            InstallRootPath,
            startupFallbackWorkspaceRootCandidates,
            out var resolvedConfiguredWorkspaceRootPath,
            out var startupWorkspaceRecoveryInfo);
        _workspacePaths = CreateWorkspacePathSet(workspaceRootPath);
        ConfiguredWorkspaceRootPath = resolvedConfiguredWorkspaceRootPath;
        _startupWorkspaceRecoveryInfo = startupWorkspaceRecoveryInfo;

        Directory.CreateDirectory(DataRootPath);
        Directory.CreateDirectory(SettingsRootPath);
        Directory.CreateDirectory(LocalizationRootPath);
        Directory.CreateDirectory(LogsRootPath);
        EnsureWorkspaceDirectories(_workspacePaths);
        MigrateLegacyEncoderBinaryNames(_workspacePaths);
    }

    public string LocalStateRootPath { get; }

    public string InstallRootPath { get; }

    public string RootPath => CurrentWorkspacePaths.RootPath;

    public string WorkspaceRootPath => CurrentWorkspacePaths.RootPath;

    public string ConfiguredWorkspaceRootPath { get; private set; }

    public string DataRootPath { get; }

    public string SettingsRootPath { get; }

    public string LocalizationRootPath { get; }

    public string LogsRootPath { get; }

    public string ToolDataRootPath => CurrentWorkspacePaths.ToolsetRootPath;

    public string ToolsetRootPath => CurrentWorkspacePaths.ToolsetRootPath;

    public string DownloadsRootPath => CurrentWorkspacePaths.DownloadsRootPath;

    public string ToolsRootPath => CurrentWorkspacePaths.ToolsRootPath;

    public string WorkspaceTemplatesRootPath => CurrentWorkspacePaths.WorkspaceTemplatesRootPath;

    public string SettingsPath { get; }

    public string SetupGuideCachePath { get; }

    public string EnvironmentOwnershipPath { get; }

    public string GetManagedExternalToolDirectory(ExternalToolKind kind)
    {
        return Path.Combine(ToolsRootPath, kind switch
        {
            ExternalToolKind.Ffmpeg => "ffmpeg",
            ExternalToolKind.Av1an => "av1an",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        });
    }

    public string GetManagedExternalToolPath(ExternalToolKind kind)
        => Path.Combine(GetManagedExternalToolDirectory(kind), kind.ToExpectedExecutableName());

    private WorkspacePathSet CurrentWorkspacePaths => Volatile.Read(ref _workspacePaths);

    public WorkspaceRootRecoveryInfo? ConsumeStartupWorkspaceRecoveryInfo()
    {
        lock (_startupWorkspaceRecoveryGate)
        {
            var recoveryInfo = _startupWorkspaceRecoveryInfo;
            _startupWorkspaceRecoveryInfo = null;
            return recoveryInfo;
        }
    }

    public string NormalizeWorkspaceRootPath(string? configuredWorkspaceRootPath)
    {
        return ResolveWorkspaceRootPath(configuredWorkspaceRootPath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    public bool IsWorkspaceRootInsideInstallRoot(string workspaceRootPath)
    {
        return IsWorkspaceRootInsideInstallRoot(workspaceRootPath, InstallRootPath);
    }

    public bool IsWorkspaceRootInsideProgramFiles(string workspaceRootPath)
    {
        return IsWorkspaceRootInsideProgramFilesCore(workspaceRootPath);
    }

    private static bool IsWorkspaceRootInsideInstallRoot(string workspaceRootPath, string installRootPath)
    {
        return IsSameOrChildPath(workspaceRootPath, installRootPath)
            || IsSameOrChildPath(installRootPath, workspaceRootPath);
    }

    private static bool IsWorkspaceRootInsideProgramFilesCore(string workspaceRootPath)
    {
        var programFilesPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        return programFilesPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Any(programFilesPath => IsSameOrChildPath(workspaceRootPath, programFilesPath));
    }

    public void PrepareWorkspaceRootChange(string configuredWorkspaceRootPath)
    {
        var targetWorkspaceRootPath = NormalizeWorkspaceRootPath(configuredWorkspaceRootPath);
        if (AreSamePath(targetWorkspaceRootPath, RootPath))
        {
            return;
        }

        ValidateWorkspaceRootCopy(DownloadsRootPath, Path.Combine(targetWorkspaceRootPath, "downloads"), "downloads");
        ValidateWorkspaceRootCopy(ToolDataRootPath, Path.Combine(targetWorkspaceRootPath, "encoders"), "encoders");
        ValidateWorkspaceRootCopy(ToolsRootPath, Path.Combine(targetWorkspaceRootPath, "tools"), "tools");
        ValidateWorkspaceRootCopy(WorkspaceTemplatesRootPath, Path.Combine(targetWorkspaceRootPath, "Templates"), "Templates");

        Directory.CreateDirectory(targetWorkspaceRootPath);
        CopyDirectoryContentsIfCompatible(DownloadsRootPath, Path.Combine(targetWorkspaceRootPath, "downloads"), "downloads");
        CopyDirectoryContentsIfCompatible(ToolDataRootPath, Path.Combine(targetWorkspaceRootPath, "encoders"), "encoders");
        CopyDirectoryContentsIfCompatible(ToolsRootPath, Path.Combine(targetWorkspaceRootPath, "tools"), "tools");
        CopyDirectoryContentsIfCompatible(WorkspaceTemplatesRootPath, Path.Combine(targetWorkspaceRootPath, "Templates"), "Templates");
    }

    public void ActivateWorkspaceRootPath(string configuredWorkspaceRootPath)
    {
        var targetWorkspaceRootPath = NormalizeWorkspaceRootPath(configuredWorkspaceRootPath);
        if (IsWorkspaceRootInsideInstallRoot(targetWorkspaceRootPath)
            || IsWorkspaceRootInsideProgramFiles(targetWorkspaceRootPath))
        {
            throw new InvalidOperationException("The workspace folder cannot be inside the install directory or Program Files.");
        }

        lock (_workspaceRootChangeGate)
        {
            var workspacePaths = CreateWorkspacePathSet(targetWorkspaceRootPath);
            EnsureWorkspaceDirectories(workspacePaths);
            MigrateLegacyEncoderBinaryNames(workspacePaths);
            ConfiguredWorkspaceRootPath = targetWorkspaceRootPath;
            Volatile.Write(ref _workspacePaths, workspacePaths);
        }
    }

    private static WorkspacePathSet CreateWorkspacePathSet(string rootPath)
    {
        var normalizedRootPath = Path.GetFullPath(rootPath);
        return new WorkspacePathSet(
            normalizedRootPath,
            Path.Combine(normalizedRootPath, "downloads"),
            Path.Combine(normalizedRootPath, "encoders"),
            Path.Combine(normalizedRootPath, "tools"),
            Path.Combine(normalizedRootPath, "Templates"));
    }

    private static void EnsureWorkspaceDirectories(WorkspacePathSet workspacePaths)
    {
        Directory.CreateDirectory(workspacePaths.RootPath);
        Directory.CreateDirectory(workspacePaths.DownloadsRootPath);
        Directory.CreateDirectory(workspacePaths.ToolsetRootPath);
        Directory.CreateDirectory(workspacePaths.ToolsRootPath);
        Directory.CreateDirectory(workspacePaths.WorkspaceTemplatesRootPath);
    }

    private static void ValidateWorkspaceRootCopy(string sourceRootPath, string targetRootPath, string targetRootRelativePath)
    {
        if (!Directory.Exists(sourceRootPath) || AreSamePath(sourceRootPath, targetRootPath))
        {
            return;
        }

        var sourceDirectories = Directory.EnumerateDirectories(sourceRootPath, "*", SearchOption.AllDirectories)
            .Select(sourceDirectory => Path.GetRelativePath(sourceRootPath, sourceDirectory))
            .ToList();
        var sourceFiles = Directory.EnumerateFiles(sourceRootPath, "*", SearchOption.AllDirectories)
            .Select(sourceFile => new
            {
                SourceFilePath = sourceFile,
                RelativePath = Path.GetRelativePath(sourceRootPath, sourceFile)
            })
            .ToList();

        foreach (var relativePath in sourceDirectories)
        {
            var targetDirectoryPath = Path.Combine(targetRootPath, relativePath);
            if (File.Exists(targetDirectoryPath))
            {
                throw new WorkspaceRootConflictException(CombineWorkspaceRelativePath(targetRootRelativePath, relativePath));
            }
        }

        foreach (var sourceFile in sourceFiles)
        {
            var targetFilePath = Path.Combine(targetRootPath, sourceFile.RelativePath);
            if (Directory.Exists(targetFilePath))
            {
                throw new WorkspaceRootConflictException(CombineWorkspaceRelativePath(targetRootRelativePath, sourceFile.RelativePath));
            }

            if (File.Exists(targetFilePath)
                && !FilesAreIdentical(sourceFile.SourceFilePath, targetFilePath))
            {
                throw new WorkspaceRootConflictException(CombineWorkspaceRelativePath(targetRootRelativePath, sourceFile.RelativePath));
            }
        }
    }

    private static string ResolveExecutableDirectory(string fallbackDirectory)
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(processDirectory))
            {
                return processDirectory;
            }
        }

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
        {
            return AppContext.BaseDirectory;
        }

        return fallbackDirectory;
    }

    private static string ResolveStartupWorkspaceRootPath(
        string? configuredWorkspaceRootPath,
        string localApplicationDataPath,
        string installRootPath,
        IReadOnlyList<string>? startupFallbackWorkspaceRootCandidates,
        out string resolvedConfiguredWorkspaceRootPath,
        out WorkspaceRootRecoveryInfo? startupWorkspaceRecoveryInfo)
    {
        startupWorkspaceRecoveryInfo = null;
        var normalizedConfiguredPath = TryNormalizeExplicitWorkspaceRootPath(configuredWorkspaceRootPath, out var configuredPathError);
        if (!string.IsNullOrWhiteSpace(normalizedConfiguredPath))
        {
            resolvedConfiguredWorkspaceRootPath = normalizedConfiguredPath;

            if (IsWorkspaceRootInsideInstallRoot(normalizedConfiguredPath, installRootPath)
                || IsWorkspaceRootInsideProgramFilesCore(normalizedConfiguredPath))
            {
                configuredPathError = "The saved workspace folder is inside the install directory or Program Files.";
            }
            else if (TryEnsureWorkspaceRootAvailable(normalizedConfiguredPath, out var resolvedWorkspaceRootPath, out configuredPathError))
            {
                return resolvedWorkspaceRootPath;
            }
        }
        else
        {
            resolvedConfiguredWorkspaceRootPath = string.IsNullOrWhiteSpace(configuredWorkspaceRootPath)
                ? string.Empty
                : configuredWorkspaceRootPath;
        }

        foreach (var candidatePath in startupFallbackWorkspaceRootCandidates ?? EnumerateFallbackWorkspaceRootCandidates(localApplicationDataPath))
        {
            if (string.IsNullOrWhiteSpace(candidatePath)
                || IsWorkspaceRootInsideInstallRoot(candidatePath, installRootPath)
                || IsWorkspaceRootInsideProgramFilesCore(candidatePath))
            {
                continue;
            }

            if (TryEnsureWorkspaceRootAvailable(candidatePath, out var resolvedWorkspaceRootPath, out _))
            {
                if (string.IsNullOrWhiteSpace(resolvedConfiguredWorkspaceRootPath))
                {
                    resolvedConfiguredWorkspaceRootPath = resolvedWorkspaceRootPath;
                }

                if (!string.IsNullOrWhiteSpace(normalizedConfiguredPath))
                {
                    startupWorkspaceRecoveryInfo = new WorkspaceRootRecoveryInfo(
                        normalizedConfiguredPath,
                        resolvedWorkspaceRootPath,
                        configuredPathError);
                }
                else if (!string.IsNullOrWhiteSpace(configuredWorkspaceRootPath))
                {
                    startupWorkspaceRecoveryInfo = new WorkspaceRootRecoveryInfo(
                        configuredWorkspaceRootPath,
                        resolvedWorkspaceRootPath,
                        configuredPathError);
                }

                return resolvedWorkspaceRootPath;
            }
        }

        var defaultWorkspaceRootPath = Path.Combine(localApplicationDataPath, AppFolderName, "workspace");
        if (string.IsNullOrWhiteSpace(resolvedConfiguredWorkspaceRootPath))
        {
            resolvedConfiguredWorkspaceRootPath = defaultWorkspaceRootPath;
        }

        if (!string.IsNullOrWhiteSpace(normalizedConfiguredPath))
        {
            startupWorkspaceRecoveryInfo = new WorkspaceRootRecoveryInfo(
                normalizedConfiguredPath,
                defaultWorkspaceRootPath,
                configuredPathError);
        }
        else if (!string.IsNullOrWhiteSpace(configuredWorkspaceRootPath))
        {
            startupWorkspaceRecoveryInfo = new WorkspaceRootRecoveryInfo(
                configuredWorkspaceRootPath,
                defaultWorkspaceRootPath,
                configuredPathError);
        }

        return defaultWorkspaceRootPath;
    }

    private static IEnumerable<string> EnumerateFallbackWorkspaceRootCandidates(string localApplicationDataPath)
    {
        var preferredDriveRoot = ResolvePreferredNonSystemDriveRootPath();
        if (!string.IsNullOrWhiteSpace(preferredDriveRoot))
        {
            yield return Path.Combine(preferredDriveRoot, AppFolderName);
        }

        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documentsPath))
        {
            yield return Path.Combine(documentsPath, AppFolderName);
        }

        yield return Path.Combine(localApplicationDataPath, AppFolderName, "workspace");
    }

    private static string ResolveWorkspaceRootPath(string? configuredWorkspaceRootPath, string localApplicationDataPath)
    {
        var normalizedConfiguredPath = NormalizeExplicitWorkspaceRootPath(configuredWorkspaceRootPath);
        if (!string.IsNullOrWhiteSpace(normalizedConfiguredPath))
        {
            return normalizedConfiguredPath;
        }

        var preferredDriveRoot = ResolvePreferredNonSystemDriveRootPath();
        if (!string.IsNullOrWhiteSpace(preferredDriveRoot))
        {
            return Path.Combine(preferredDriveRoot, AppFolderName);
        }

        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documentsPath))
        {
            return Path.Combine(documentsPath, AppFolderName);
        }

        return Path.Combine(localApplicationDataPath, AppFolderName, "workspace");
    }

    private static string? NormalizeExplicitWorkspaceRootPath(string? configuredWorkspaceRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredWorkspaceRootPath))
        {
            return null;
        }

        var trimmed = configuredWorkspaceRootPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(trimmed);
        return Path.GetFullPath(expanded);
    }

    private static string? TryNormalizeExplicitWorkspaceRootPath(string? configuredWorkspaceRootPath, out string? error)
    {
        error = null;

        try
        {
            return NormalizeExplicitWorkspaceRootPath(configuredWorkspaceRootPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string? ResolvePreferredNonSystemDriveRootPath()
    {
        try
        {
            var systemDriveRoot = Path.GetPathRoot(Environment.SystemDirectory);
            return DriveInfo.GetDrives()
                .Where(static drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Where(drive => !string.Equals(drive.RootDirectory.FullName, systemDriveRoot, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static drive => drive.AvailableFreeSpace)
                .ThenBy(static drive => drive.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static drive => drive.RootDirectory.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadConfiguredWorkspaceRootPath(string localApplicationDataPath)
    {
        var settingsPath = Path.Combine(localApplicationDataPath, AppFolderName, "data", "settings", "settings.json");
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return TryGetStringProperty(document.RootElement, WorkspaceRootPathPropertyName, out var value)
                && !string.IsNullOrWhiteSpace(value)
                    ? value
                    : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => property.Value.ToString()
            };
            return true;
        }

        return false;
    }

    private static void CopyDirectoryContentsIfCompatible(string sourceRootPath, string targetRootPath, string targetRootRelativePath)
    {
        if (!Directory.Exists(sourceRootPath) || AreSamePath(sourceRootPath, targetRootPath))
        {
            return;
        }

        var sourceDirectories = Directory.EnumerateDirectories(sourceRootPath, "*", SearchOption.AllDirectories)
            .Select(sourceDirectory => Path.GetRelativePath(sourceRootPath, sourceDirectory))
            .ToList();
        var sourceFiles = Directory.EnumerateFiles(sourceRootPath, "*", SearchOption.AllDirectories)
            .Select(sourceFile => new
            {
                SourceFilePath = sourceFile,
                RelativePath = Path.GetRelativePath(sourceRootPath, sourceFile)
            })
            .ToList();

        foreach (var relativePath in sourceDirectories)
        {
            var targetDirectoryPath = Path.Combine(targetRootPath, relativePath);
            if (File.Exists(targetDirectoryPath))
            {
                throw new WorkspaceRootConflictException(CombineWorkspaceRelativePath(targetRootRelativePath, relativePath));
            }
        }

        foreach (var sourceFile in sourceFiles)
        {
            var targetFilePath = Path.Combine(targetRootPath, sourceFile.RelativePath);
            if (Directory.Exists(targetFilePath))
            {
                throw new WorkspaceRootConflictException(CombineWorkspaceRelativePath(targetRootRelativePath, sourceFile.RelativePath));
            }

            if (File.Exists(targetFilePath)
                && !FilesAreIdentical(sourceFile.SourceFilePath, targetFilePath))
            {
                throw new WorkspaceRootConflictException(CombineWorkspaceRelativePath(targetRootRelativePath, sourceFile.RelativePath));
            }
        }

        foreach (var relativePath in sourceDirectories)
        {
            Directory.CreateDirectory(Path.Combine(targetRootPath, relativePath));
        }

        foreach (var sourceFile in sourceFiles)
        {
            var targetFilePath = Path.Combine(targetRootPath, sourceFile.RelativePath);
            if (File.Exists(targetFilePath))
            {
                continue;
            }

            var targetDirectory = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(sourceFile.SourceFilePath, targetFilePath, false);
        }
    }

    private static string CombineWorkspaceRelativePath(string rootRelativePath, string nestedRelativePath)
    {
        return string.IsNullOrWhiteSpace(nestedRelativePath)
            ? rootRelativePath
            : Path.Combine(rootRelativePath, nestedRelativePath);
    }

    private static bool FilesAreIdentical(string sourceFilePath, string targetFilePath)
    {
        var sourceInfo = new FileInfo(sourceFilePath);
        var targetInfo = new FileInfo(targetFilePath);
        if (sourceInfo.Length != targetInfo.Length)
        {
            return false;
        }

        const int bufferSize = 81920;
        using var sourceStream = File.OpenRead(sourceFilePath);
        using var targetStream = File.OpenRead(targetFilePath);
        var sourceBuffer = new byte[bufferSize];
        var targetBuffer = new byte[bufferSize];

        while (true)
        {
            var sourceRead = sourceStream.Read(sourceBuffer, 0, sourceBuffer.Length);
            var targetRead = targetStream.Read(targetBuffer, 0, targetBuffer.Length);
            if (sourceRead != targetRead)
            {
                return false;
            }

            if (sourceRead == 0)
            {
                return true;
            }

            for (var index = 0; index < sourceRead; index++)
            {
                if (sourceBuffer[index] != targetBuffer[index])
                {
                    return false;
                }
            }
        }
    }

    private static bool TryEnsureWorkspaceRootAvailable(string workspaceRootPath, out string resolvedWorkspaceRootPath, out string? error)
    {
        resolvedWorkspaceRootPath = string.Empty;
        error = null;

        try
        {
            var normalizedWorkspaceRootPath = Path.GetFullPath(workspaceRootPath);
            Directory.CreateDirectory(normalizedWorkspaceRootPath);

            var probeFilePath = Path.Combine(normalizedWorkspaceRootPath, $".flowencode-probe-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probeFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            resolvedWorkspaceRootPath = normalizedWorkspaceRootPath;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool AreSamePath(string leftPath, string rightPath)
    {
        try
        {
            var left = Path.GetFullPath(leftPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var right = Path.GetFullPath(rightPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameOrChildPath(string path, string basePath)
    {
        try
        {
            var normalizedBasePath = Path.GetFullPath(basePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(normalizedPath, normalizedBasePath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return (normalizedPath + Path.DirectorySeparatorChar)
                .StartsWith(normalizedBasePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public string GetBinaryDirectory(EncoderKind kind, EncoderArchitecture architecture)
    {
        var encoderFolder = kind.ToShortName();
        var archFolder = architecture == EncoderArchitecture.X64 ? "x64" : "x86";

        return Path.Combine(ToolsetRootPath, encoderFolder, archFolder);
    }

    public string GetBinaryPath(EncoderKind kind, EncoderArchitecture architecture)
    {
        return Path.Combine(GetBinaryDirectory(kind, architecture), GetExpectedFileName(kind, architecture));
    }

    public static string GetExpectedFileName(EncoderKind kind, EncoderArchitecture architecture)
    {
        _ = architecture;

        return kind switch
        {
            EncoderKind.X264 => "x264.exe",
            EncoderKind.X265 => "x265.exe",
            EncoderKind.SvtAv1 => "SvtAv1EncApp.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    internal string GetInstalledBinaryPath(EncoderKind kind, EncoderArchitecture architecture)
    {
        var canonicalPath = GetBinaryPath(kind, architecture);
        var legacyPath = GetLegacyBinaryPath(kind, architecture);
        if (!File.Exists(canonicalPath))
        {
            return File.Exists(legacyPath) ? legacyPath : canonicalPath;
        }

        if (!File.Exists(legacyPath))
        {
            return canonicalPath;
        }

        return File.GetLastWriteTimeUtc(legacyPath) > File.GetLastWriteTimeUtc(canonicalPath)
            ? legacyPath
            : canonicalPath;
    }

    internal string GetLegacyBinaryPath(EncoderKind kind, EncoderArchitecture architecture)
    {
        var architectureSuffix = architecture == EncoderArchitecture.X64 ? "x64" : "x86";
        var fileName = kind switch
        {
            EncoderKind.X264 => $"x264_{architectureSuffix}.exe",
            EncoderKind.X265 => $"x265_{architectureSuffix}.exe",
            EncoderKind.SvtAv1 => $"SvtAv1EncApp_{architectureSuffix}.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        return Path.Combine(GetBinaryDirectory(kind, architecture), fileName);
    }

    private static void MigrateLegacyEncoderBinaryNames(WorkspacePathSet workspacePaths)
    {
        foreach (var kind in Enum.GetValues<EncoderKind>())
        {
            foreach (var architecture in Enum.GetValues<EncoderArchitecture>())
            {
                var encoderFolder = kind.ToShortName();
                var architectureFolder = architecture == EncoderArchitecture.X64 ? "x64" : "x86";
                var directory = Path.Combine(workspacePaths.ToolsetRootPath, encoderFolder, architectureFolder);
                var canonicalPath = Path.Combine(directory, GetExpectedFileName(kind, architecture));
                var architectureSuffix = architecture == EncoderArchitecture.X64 ? "x64" : "x86";
                var legacyFileName = kind switch
                {
                    EncoderKind.X264 => $"x264_{architectureSuffix}.exe",
                    EncoderKind.X265 => $"x265_{architectureSuffix}.exe",
                    EncoderKind.SvtAv1 => $"SvtAv1EncApp_{architectureSuffix}.exe",
                    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
                };
                var legacyPath = Path.Combine(directory, legacyFileName);
                if (!File.Exists(legacyPath))
                {
                    continue;
                }

                try
                {
                    if (!File.Exists(canonicalPath)
                        || File.GetLastWriteTimeUtc(legacyPath) > File.GetLastWriteTimeUtc(canonicalPath))
                    {
                        File.Move(legacyPath, canonicalPath, overwrite: true);
                    }
                    else
                    {
                        File.Delete(legacyPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Failed to migrate legacy encoder binary '{legacyPath}' to '{canonicalPath}'. {ex}");
                }
            }
        }
    }

    private sealed record WorkspacePathSet(
        string RootPath,
        string DownloadsRootPath,
        string ToolsetRootPath,
        string ToolsRootPath,
        string WorkspaceTemplatesRootPath);
}

public sealed record WorkspaceRootRecoveryInfo(
    string ConfiguredPath,
    string ActivePath,
    string? FailureReason);

public sealed class WorkspaceRootConflictException : InvalidOperationException
{
    public WorkspaceRootConflictException(string relativePath)
        : base($"The target workspace folder already contains different content at '{relativePath}'.")
    {
        RelativePath = relativePath;
    }

    public string RelativePath { get; }
}

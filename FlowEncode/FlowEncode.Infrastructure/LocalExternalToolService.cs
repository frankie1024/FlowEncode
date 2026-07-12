using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;
using SharpCompress.Archives.SevenZip;

namespace FlowEncode.Infrastructure;

public sealed class LocalExternalToolService : IExternalToolService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ManagedAv1anRepositoryOverrideVariable = "FLOWENCODE_AV1AN_RELEASE_REPO";
    private static readonly string[] ManagedAv1anRepositoryCandidates =
    [
        "frankie1024/Av1an"
    ];

    private static readonly IReadOnlyDictionary<ExternalToolKind, string[]> ExecutableNames = new Dictionary<ExternalToolKind, string[]>
    {
        [ExternalToolKind.Av1an] = ["av1an.exe", "Av1an.exe"],
        [ExternalToolKind.Ffmpeg] = ["ffmpeg.exe", "ffmpeg64.exe"]
    };

    private static readonly IReadOnlyDictionary<ExternalToolKind, string[]> EnvironmentVariableNames = new Dictionary<ExternalToolKind, string[]>
    {
        [ExternalToolKind.Av1an] = ["FLOWENCODE_AV1AN", "AV1AN_PATH", "AV1AN_EXE", "AV1AN"],
        [ExternalToolKind.Ffmpeg] = ["FLOWENCODE_FFMPEG", "FFMPEG_PATH", "FFMPEG_EXE", "FFMPEG"]
    };

    private readonly LocalAppPaths _paths;
    private readonly HttpClient _apiHttpClient;
    private readonly HttpClient _downloadHttpClient;

    public LocalExternalToolService(LocalAppPaths paths, IFlowEncodeHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _paths = paths;
        _apiHttpClient = httpClientFactory.CreateClient(FlowEncodeHttpClientProfile.Api);
        _downloadHttpClient = httpClientFactory.CreateClient(FlowEncodeHttpClientProfile.Download);
    }

    public IReadOnlyList<DiscoveredExternalToolBinary> DiscoverSystemBinaries()
    {
        var results = new List<DiscoveredExternalToolBinary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in Enum.GetValues<ExternalToolKind>())
        {
            foreach (var variableName in EnvironmentVariableNames[kind])
            {
                var value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process)
                    ?? Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine);

                var resolvedPath = ExecutablePathResolver.ResolveFromInput(value, ExecutableNames[kind], EnumerateCurrentPathRoots());
                if (string.IsNullOrWhiteSpace(resolvedPath) || !seen.Add($"{kind}:{resolvedPath}"))
                {
                    continue;
                }

                results.Add(CreateCandidate(kind, resolvedPath, ExternalToolBinarySource.Path, variableName));
            }

            foreach (var resolvedPath in EnumeratePathMatches(kind))
            {
                if (!seen.Add($"{kind}:{resolvedPath}"))
                {
                    continue;
                }

                results.Add(CreateCandidate(kind, resolvedPath, ExternalToolBinarySource.Path, "PATH"));
            }
        }

        return results
            .OrderBy(static item => item.Kind)
            .ThenBy(static item => item.Source)
            .ThenBy(static item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public DiscoveredExternalToolBinary? ResolveTool(ExternalToolKind kind)
    {
        var localPath = GetLocalToolPath(kind);
        if (File.Exists(localPath))
        {
            return CreateCandidate(kind, localPath, ExternalToolBinarySource.LocalTools, "tools");
        }

        return DiscoverSystemBinaries()
            .FirstOrDefault(candidate => candidate.Kind == kind);
    }

    public async Task ImportBinaryAsync(
        ExternalToolKind kind,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Source path is required.", nameof(sourcePath));
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected tool binary was not found.", sourcePath);
        }

        Directory.CreateDirectory(_paths.ToolsRootPath);
        var sourceDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty;

        switch (kind)
        {
            case ExternalToolKind.Av1an:
            {
                var targetPath = GetLocalToolPath(ExternalToolKind.Av1an);
                await CopyFileAsync(sourcePath, targetPath, cancellationToken);
                return;
            }
            case ExternalToolKind.Ffmpeg:
            {
                var targetPath = GetLocalToolPath(ExternalToolKind.Ffmpeg);
                await CopyFileAsync(sourcePath, targetPath, cancellationToken);

                await CopySiblingIfExistsAsync(sourceDirectory, "ffprobe.exe", cancellationToken);
                await CopySiblingIfExistsAsync(sourceDirectory, "ffplay.exe", cancellationToken);

                foreach (var dllPath in Directory.EnumerateFiles(sourceDirectory, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    var targetDllPath = Path.Combine(_paths.ToolsRootPath, Path.GetFileName(dllPath));
                    await CopyFileAsync(dllPath, targetDllPath, cancellationToken);
                }

                return;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    public async Task<IReadOnlyList<ExternalToolUpdatePackage>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var packages = new List<ExternalToolUpdatePackage>();

        var av1an = await GetAv1anPackageAsync(cancellationToken);
        if (av1an is not null)
        {
            packages.Add(av1an);
        }

        var ffmpeg = await GetFfmpegPackageAsync(cancellationToken);
        if (ffmpeg is not null)
        {
            packages.Add(ffmpeg);
        }

        return packages;
    }

    public async Task<string> InstallUpdateAsync(
        ExternalToolUpdatePackage package,
        CancellationToken cancellationToken = default,
        IProgress<PackageDownloadProgress>? downloadProgress = null)
    {
        if (string.IsNullOrWhiteSpace(package.Sha256))
        {
            throw new InvalidOperationException("更新包未提供 SHA256 摘要，已拒绝自动安装。请改为手动下载并完成校验。");
        }

        var downloadPath = Path.Combine(_paths.DownloadsRootPath, package.AssetName);
        var extractRoot = Path.Combine(_paths.DownloadsRootPath, Guid.NewGuid().ToString("N"));

        try
        {
            await ResumablePackageDownloader.DownloadAsync(
                _downloadHttpClient,
                package.DownloadUrl,
                downloadPath,
                downloadProgress,
                cancellationToken);
        await PackageIntegrityVerifier.VerifySha256Async(
            downloadPath,
            package.Sha256,
            cancellationToken,
            "外部工具更新包");

            if (downloadPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var targetPath = GetLocalToolPath(package.Kind);
                await CopyFileAsync(downloadPath, targetPath, cancellationToken);
                return targetPath;
            }

            Directory.CreateDirectory(extractRoot);
            ExtractArchive(downloadPath, extractRoot);

            var result = package.Kind switch
            {
                ExternalToolKind.Av1an => await InstallAv1anFromExtractedAsync(extractRoot, cancellationToken),
                ExternalToolKind.Ffmpeg => await InstallFfmpegFromExtractedAsync(extractRoot, cancellationToken),
                _ => throw new ArgumentOutOfRangeException()
            };

            return result;
        }
        finally
        {
            BestEffortCleanup.DeleteFile(downloadPath, $"工具更新包 '{package.AssetName}'", WriteDiagnostic);

            try
            {
                if (Directory.Exists(extractRoot))
                {
                    Directory.Delete(extractRoot, true);
                }
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Failed to delete extracted update directory '{extractRoot}'. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public Task RemoveManagedBinaryAsync(
        ExternalToolKind kind,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (kind)
            {
                case ExternalToolKind.Av1an:
                    DeleteIfExists(GetLocalToolPath(ExternalToolKind.Av1an));
                    break;

                case ExternalToolKind.Ffmpeg:
                    DeleteIfExists(Path.Combine(_paths.ToolsRootPath, "ffmpeg.exe"));
                    DeleteIfExists(Path.Combine(_paths.ToolsRootPath, "ffmpeg64.exe"));
                    DeleteIfExists(Path.Combine(_paths.ToolsRootPath, "ffprobe.exe"));
                    DeleteIfExists(Path.Combine(_paths.ToolsRootPath, "ffplay.exe"));

                    if (Directory.Exists(_paths.ToolsRootPath))
                    {
                        foreach (var pattern in new[]
                                 {
                                     "avcodec-*.dll",
                                     "avdevice-*.dll",
                                     "avfilter-*.dll",
                                     "avformat-*.dll",
                                     "avutil-*.dll",
                                     "postproc-*.dll",
                                     "swresample-*.dll",
                                     "swscale-*.dll"
                                 })
                        {
                            foreach (var path in Directory.EnumerateFiles(_paths.ToolsRootPath, pattern, SearchOption.TopDirectoryOnly))
                            {
                                DeleteIfExists(path);
                            }
                        }
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }, cancellationToken);
    }

    public string GetToolsRootPath() => _paths.ToolsRootPath;

    public void Dispose()
    {
        _apiHttpClient.Dispose();
        _downloadHttpClient.Dispose();
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_paths, nameof(LocalExternalToolService), message);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task<ExternalToolUpdatePackage?> GetAv1anPackageAsync(CancellationToken cancellationToken)
    {
        foreach (var repository in GetManagedAv1anRepositoryCandidates())
        {
            var package = await TryGetManagedAv1anPackageFromRepositoryAsync(repository, cancellationToken);
            if (package is not null)
            {
                return package;
            }
        }

        return null;
    }

    private async Task<ExternalToolUpdatePackage?> TryGetManagedAv1anPackageFromRepositoryAsync(
        string repository,
        CancellationToken cancellationToken)
    {
        using var response = await _apiHttpClient.GetAsync(
            $"https://api.github.com/repos/{repository}/releases?per_page=20",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(contentStream, JsonOptions, cancellationToken);
        if (releases is null || releases.Count == 0)
        {
            return null;
        }

        var candidates = releases
            .Where(static release => IsStableGitHubRelease(release))
            .SelectMany(release => (release.Assets ?? [])
                .Where(static asset => IsManagedAv1anAssetName(asset.Name))
                .Select(asset => new { Release = release, Asset = asset }))
            .OrderByDescending(static candidate => candidate.Release.PublishedAt)
            .ThenByDescending(static candidate => ScoreManagedAv1anAssetName(candidate.Asset.Name))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var selected = candidates[0];
        var sha256 = PackageIntegrityVerifier.NormalizeSha256Digest(selected.Asset.Digest);
        var isAutomatic = !string.IsNullOrWhiteSpace(sha256);
        var notes = "使用 FlowEncode 兼容的 Av1an 托管发布包。";
        if (!isAutomatic)
        {
            notes += " 当前资源未提供 SHA256 摘要，已禁用自动安装。";
        }

        return new ExternalToolUpdatePackage(
            ExternalToolKind.Av1an,
            ResolveGitHubReleaseLabel(selected.Release),
            selected.Asset.Name,
            selected.Release.HtmlUrl,
            selected.Asset.BrowserDownloadUrl,
            selected.Release.PublishedAt,
            notes,
            sha256,
            isAutomatic);
    }

    private async Task<ExternalToolUpdatePackage?> GetFfmpegPackageAsync(CancellationToken cancellationToken)
    {
        using var response = await _apiHttpClient.GetAsync(
            "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(contentStream, JsonOptions, cancellationToken);
        if (release is null)
        {
            return null;
        }

        var asset = (release.Assets ?? [])
            .Where(static item => item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && item.Name.Contains("win64", StringComparison.OrdinalIgnoreCase)
                && item.Name.Contains("shared", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static item => item.Name.Contains("gpl-shared", StringComparison.OrdinalIgnoreCase))
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (asset is null)
        {
            return null;
        }

        var sha256 = PackageIntegrityVerifier.NormalizeSha256Digest(asset.Digest);
        var isAutomatic = !string.IsNullOrWhiteSpace(sha256);
        var notes = "使用 BtbN 官方 Win64 Shared 构建（包含 ffmpeg / ffprobe）。";
        if (!isAutomatic)
        {
            notes += " 当前资源未提供 SHA256 摘要，已禁用自动安装。";
        }

        return new ExternalToolUpdatePackage(
            ExternalToolKind.Ffmpeg,
            ResolveGitHubReleaseLabel(release),
            asset.Name,
            release.HtmlUrl,
            asset.BrowserDownloadUrl,
            release.PublishedAt,
            notes,
            sha256,
            isAutomatic);
    }

    private async Task<string> InstallAv1anFromExtractedAsync(string extractRoot, CancellationToken cancellationToken)
    {
        var av1anPath = Directory
            .EnumerateFiles(extractRoot, "av1an.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(av1anPath))
        {
            throw new FileNotFoundException("压缩包内未找到 av1an.exe。");
        }

        var targetPath = GetLocalToolPath(ExternalToolKind.Av1an);
        await CopyFileAsync(av1anPath, targetPath, cancellationToken);
        return targetPath;
    }

    private async Task<string> InstallFfmpegFromExtractedAsync(string extractRoot, CancellationToken cancellationToken)
    {
        var ffmpegPath = Directory
            .EnumerateFiles(extractRoot, "ffmpeg.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new FileNotFoundException("压缩包内未找到 ffmpeg.exe。");
        }

        var sourceDirectory = Path.GetDirectoryName(ffmpegPath)
            ?? throw new InvalidOperationException("无法解析 ffmpeg.exe 所在目录。");

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(_paths.ToolsRootPath, Path.GetFileName(file));
            await CopyFileAsync(file, destination, cancellationToken);
        }

        return GetLocalToolPath(ExternalToolKind.Ffmpeg);
    }

    private static async Task CopyFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        await using var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var target = File.Open(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
    }

    private async Task CopySiblingIfExistsAsync(string sourceDirectory, string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return;
        }

        var sourcePath = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var targetPath = Path.Combine(_paths.ToolsRootPath, fileName);
        await CopyFileAsync(sourcePath, targetPath, cancellationToken);
    }

    private static void ExtractArchive(string archivePath, string extractRoot)
    {
        var normalizedExtractRoot = Path.GetFullPath(extractRoot);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.Name)))
            {
                var entryPath = entry.FullName.TrimStart('/', '\\');
                var destinationPath = Path.GetFullPath(Path.Combine(normalizedExtractRoot, entryPath));
                EnsureDestinationPath(normalizedExtractRoot, destinationPath, entryPath);
                EnsureParentDirectory(destinationPath);
                entry.ExtractToFile(destinationPath, true);
            }

            return;
        }

        using var archiveStream = File.OpenRead(archivePath);
        using var archive7z = SevenZipArchive.OpenArchive(archiveStream);

        foreach (var entry in archive7z.Entries.Where(static entry => !entry.IsDirectory))
        {
            var entryKey = entry.Key?.TrimStart('/', '\\');
            if (string.IsNullOrWhiteSpace(entryKey))
            {
                continue;
            }

            var relativePath = entryKey
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(normalizedExtractRoot, relativePath));
            EnsureDestinationPath(normalizedExtractRoot, destinationPath, entryKey);
            EnsureParentDirectory(destinationPath);

            using var entryStream = entry.OpenEntryStream();
            using var fileStream = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            entryStream.CopyTo(fileStream);
        }
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void EnsureDestinationPath(string normalizedExtractRoot, string destinationPath, string entryName)
    {
        if (!destinationPath.StartsWith(normalizedExtractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"压缩包包含非法路径：{entryName}");
        }
    }

    private string GetLocalToolPath(ExternalToolKind kind)
    {
        return Path.Combine(_paths.ToolsRootPath, kind.ToExpectedExecutableName());
    }

    private DiscoveredExternalToolBinary CreateCandidate(
        ExternalToolKind kind,
        string executablePath,
        ExternalToolBinarySource source,
        string sourceLabel)
    {
        return new DiscoveredExternalToolBinary(
            kind,
            executablePath,
            source,
            sourceLabel,
            ProbeVersion(executablePath, kind));
    }

    private static string ProbeVersion(string executablePath, ExternalToolKind kind)
    {
        try
        {
            if (kind == ExternalToolKind.Av1an)
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                var version = versionInfo.ProductVersion ?? versionInfo.FileVersion;
                return string.IsNullOrWhiteSpace(version)
                    ? "Present (version probe skipped)"
                    : $"Av1an {version}";
            }

            var arguments = kind == ExternalToolKind.Ffmpeg ? "-version" : "--version";
            using var _ = ErrorDialogSuppression.Enter();
            var result = ProcessProbeRunner.Run(
                new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                TimeSpan.FromSeconds(5),
                "Version probe timed out.");

            var output = string.Concat(
                result.StandardOutput,
                Environment.NewLine,
                result.StandardError).Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                return "Present (version string unavailable)";
            }

            if (kind == ExternalToolKind.Ffmpeg)
            {
                var versionLine = output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(static line => line.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(versionLine))
                {
                    return versionLine;
                }
            }

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
                ?? "Present";
        }
        catch (InvalidOperationException ex) when (string.Equals(ex.Message, "Version probe timed out.", StringComparison.Ordinal))
        {
            return "Present (version probe timed out)";
        }
        catch (Exception ex)
        {
            // System probe is best-effort, but keep a breadcrumb for environment-specific failures.
            Debug.WriteLine($"Failed to probe external tool version for '{executablePath}'. {ex}");
            return "Present (version probe failed)";
        }
    }

    private IEnumerable<string> EnumeratePathMatches(ExternalToolKind kind)
    {
        foreach (var root in EnumerateCurrentPathRoots())
        {
            foreach (var fileName in ExecutableNames[kind])
            {
                var candidate = Path.GetFullPath(Path.Combine(root, fileName));
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static bool IsStableGitHubRelease(GitHubRelease release) =>
        !release.Draft
        && !release.Prerelease
        && !ContainsUnstableReleaseMarker(release.TagName)
        && !ContainsUnstableReleaseMarker(release.Name);

    internal static bool ContainsUnstableReleaseMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Contains("night", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("nightly", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("dev", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("beta", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("alpha", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("preview", StringComparison.OrdinalIgnoreCase)
            || IsReleaseCandidateMarker(normalized)
            || normalized.Contains("unstable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "latest", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReleaseCandidateMarker(string value)
    {
        // Match "rc" only as a release-candidate marker:
        // preceded by start/separator (-._ ) and optionally followed by digits.
        return Regex.IsMatch(value, @"(?:^|[-._\s])rc\d*(?:[-._\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ResolveGitHubReleaseLabel(GitHubRelease release)
    {
        return NormalizeGitHubReleaseLabel(release.TagName)
            ?? NormalizeGitHubReleaseLabel(release.Name)
            ?? release.TagName;
    }

    private static string? NormalizeGitHubReleaseLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "latest", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var versionMatch = Regex.Match(trimmed, "(\\d+\\.\\d+(?:\\.\\d+)*)");
        if (versionMatch.Success)
        {
            return versionMatch.Groups[1].Value;
        }

        if (trimmed.Length > 1
            && (trimmed[0] == 'v' || trimmed[0] == 'V')
            && char.IsDigit(trimmed[1]))
        {
            return trimmed[1..];
        }

        return trimmed;
    }

    private static IEnumerable<string> EnumerateCurrentPathRoots()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            yield break;
        }

        foreach (var root in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return root;
        }
    }

    internal static IReadOnlyList<string> GetManagedAv1anRepositoryCandidates()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var overrideRepository = Environment.GetEnvironmentVariable(ManagedAv1anRepositoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideRepository))
        {
            var normalizedOverride = overrideRepository.Trim().Trim('/');
            if (seen.Add(normalizedOverride))
            {
                result.Add(normalizedOverride);
            }
        }

        foreach (var candidate in ManagedAv1anRepositoryCandidates)
        {
            if (seen.Add(candidate))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    internal static bool IsManagedAv1anAssetName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasRecognizedExtension =
            normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
        if (!hasRecognizedExtension)
        {
            return false;
        }

        return normalized.Contains("av1an", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreManagedAv1anAssetName(string value)
    {
        var score = 0;
        if (value.Equals("av1an.exe", StringComparison.OrdinalIgnoreCase))
        {
            score += 500;
        }

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            score += 300;
        }
        else if (value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            score += 200;
        }
        else if (value.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (value.Contains("flowencode", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        return score;
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;
using SharpCompress.Archives.SevenZip;

namespace FlowEncode.Infrastructure;

public sealed class LocalExternalToolService : IExternalToolService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

                var resolvedPath = ResolveFromInput(value, kind);
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

    public async Task<string> InstallUpdateAsync(ExternalToolUpdatePackage package, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(package.Sha256))
        {
            throw new InvalidOperationException("更新包未提供 SHA256 摘要，已拒绝自动安装。请改为手动下载并完成校验。");
        }

        var downloadPath = Path.Combine(_paths.DownloadsRootPath, package.AssetName);
        var extractRoot = Path.Combine(_paths.DownloadsRootPath, Guid.NewGuid().ToString("N"));

        try
        {
            await DownloadAsync(package.DownloadUrl, downloadPath, cancellationToken);
            await VerifySha256Async(downloadPath, package.Sha256, cancellationToken);

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
        using var response = await _apiHttpClient.GetAsync(
            "https://api.github.com/repos/rust-av/Av1an/releases?per_page=20",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(contentStream, JsonOptions, cancellationToken);
        if (releases is null || releases.Count == 0)
        {
            return null;
        }

        var candidates = releases
            .Where(static release => IsStableGitHubRelease(release))
            .SelectMany(release => release.Assets
                .Where(static asset => asset.Name.Equals("av1an.exe", StringComparison.OrdinalIgnoreCase))
                .Select(asset => new { Release = release, Asset = asset }))
            .OrderByDescending(static candidate => candidate.Release.PublishedAt)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var selected = candidates[0];
        var sha256 = NormalizeSha256(selected.Asset.Digest);
        var isAutomatic = !string.IsNullOrWhiteSpace(sha256);
        var notes = "使用 Av1an 官方稳定发布版本。";
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

        var asset = release.Assets
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

        var sha256 = NormalizeSha256(asset.Digest);
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
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

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

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            using var entryStream = entry.OpenEntryStream();
            using var fileStream = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            entryStream.CopyTo(fileStream);
        }
    }

    private static void EnsureDestinationPath(string normalizedExtractRoot, string destinationPath, string entryName)
    {
        if (!destinationPath.StartsWith(normalizedExtractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"压缩包包含非法路径：{entryName}");
        }
    }

    private async Task DownloadAsync(string url, string filePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await using var target = File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var response = await _downloadHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(target, cancellationToken);
    }

    private static async Task VerifySha256Async(string filePath, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("下载完成，但 SHA256 校验失败，已停止安装。");
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
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var root in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in ExecutableNames[kind])
            {
                var candidate = Path.Combine(root, fileName);
                if (File.Exists(candidate))
                {
                    yield return Path.GetFullPath(candidate);
                }
            }
        }
    }

    private static string? ResolveFromInput(string? value, ExternalToolKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Trim('"');
        if (File.Exists(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        if (Directory.Exists(normalized))
        {
            foreach (var fileName in ExecutableNames[kind])
            {
                var candidate = Path.Combine(normalized, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        if (!normalized.Contains(Path.DirectorySeparatorChar) && !normalized.Contains(Path.AltDirectorySeparatorChar))
        {
            var pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathVariable))
            {
                foreach (var root in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var candidate = Path.Combine(root, normalized);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        return null;
    }

    private static string? NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..]
            : digest;
    }

    private static bool IsStableGitHubRelease(GitHubRelease release)
    {
        if (release.Draft || release.Prerelease)
        {
            return false;
        }

        return !ContainsUnstableReleaseMarker(release.TagName)
            && !ContainsUnstableReleaseMarker(release.Name);
    }

    private static bool ContainsUnstableReleaseMarker(string? value)
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
            || normalized.Contains("rc", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("unstable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "latest", StringComparison.OrdinalIgnoreCase);
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

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<GitHubReleaseAsset> Assets);

    private sealed record GitHubReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}

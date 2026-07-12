using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;
using SharpCompress.Archives.SevenZip;

namespace FlowEncode.Infrastructure;

public sealed class GitHubReleaseEncoderUpdateService : IEncoderUpdateService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _apiHttpClient;
    private readonly HttpClient _downloadHttpClient;
    private readonly LocalAppPaths _paths;
    private readonly EncoderCpuProfile _cpuProfile;

    public GitHubReleaseEncoderUpdateService(LocalAppPaths paths, IFlowEncodeHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _paths = paths;
        _cpuProfile = EncoderCpuCompatibilityPolicy.DetectCurrent();
        _apiHttpClient = httpClientFactory.CreateClient(FlowEncodeHttpClientProfile.Api);
        _downloadHttpClient = httpClientFactory.CreateClient(FlowEncodeHttpClientProfile.Download);
    }

    public async Task<IReadOnlyList<EncoderUpdatePackage>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var packages = new List<EncoderUpdatePackage>();

        var x264 = await GetLatestPackageAsync(
            EncoderKind.X264,
            EncoderArchitecture.X64,
            "frankie1024/x264-windows-builds",
            static asset => IsExpectedEncoderAssetName(asset.Name, "x264"),
            static asset => ScoreX264Asset(asset.Name),
            "默认使用 FlowEncode x264 Windows x64 发布包。",
            cancellationToken);
        if (x264 is not null)
        {
            packages.Add(x264);
        }

        var x265 = await GetLatestPackageAsync(
            EncoderKind.X265,
            EncoderArchitecture.X64,
            "frankie1024/x265-windows-builds",
            static asset => IsExpectedEncoderAssetName(asset.Name, "x265"),
            static asset => ScoreX265Asset(asset.Name),
            "默认使用 FlowEncode x265 Windows x64 发布包。",
            cancellationToken);
        if (x265 is not null)
        {
            packages.Add(x265);
        }

        var svt = await GetLatestPackageAsync(
            EncoderKind.SvtAv1,
            EncoderArchitecture.X64,
            "frankie1024/svt-av1-windows-builds",
            static asset => IsExpectedEncoderAssetName(asset.Name, "svt-av1"),
            static asset => ScoreSvtAsset(asset.Name),
            "默认使用 FlowEncode SVT-AV1 Windows x64 发布包。",
            cancellationToken);
        if (svt is not null)
        {
            packages.Add(svt);
        }

        return packages;
    }

    public async Task<string> InstallUpdateAsync(
        EncoderUpdatePackage package,
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
                "编码器更新包");

            Directory.CreateDirectory(extractRoot);
            ExtractArchive(downloadPath, extractRoot);

            var executableName = package.Kind switch
            {
                EncoderKind.X264 => "x264.exe",
                EncoderKind.X265 => "x265.exe",
                EncoderKind.SvtAv1 => "SvtAv1EncApp.exe",
                _ => throw new ArgumentOutOfRangeException()
            };

            var extractedExe = Directory
                .EnumerateFiles(extractRoot, executableName, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(extractedExe))
            {
                throw new FileNotFoundException($"压缩包内未找到 {executableName}。");
            }

            var sourceDirectory = Path.GetDirectoryName(extractedExe)!;
            var targetDirectory = _paths.GetBinaryDirectory(package.Kind, package.Architecture);
            var expectedBinaryPath = _paths.GetBinaryPath(package.Kind, package.Architecture);
            var expectedBinaryName = Path.GetFileName(expectedBinaryPath);

            cancellationToken.ThrowIfCancellationRequested();
            ManagedDirectoryInstaller.ReplaceDirectoryContents(sourceDirectory, targetDirectory, stagingDirectory =>
            {
                var stagedBinaryPath = Path.Combine(stagingDirectory, executableName);
                if (!File.Exists(stagedBinaryPath))
                {
                    throw new FileNotFoundException($"压缩包内未找到 {executableName}。");
                }

                var stagedExpectedBinaryPath = Path.Combine(stagingDirectory, expectedBinaryName);
                if (!string.Equals(stagedBinaryPath, stagedExpectedBinaryPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(stagedBinaryPath, stagedExpectedBinaryPath, true);
                }
            });

            if (!File.Exists(expectedBinaryPath))
            {
                throw new FileNotFoundException($"安装完成后未找到 {expectedBinaryName}。", expectedBinaryPath);
            }

            return expectedBinaryPath;
        }
        finally
        {
            BestEffortCleanup.DeleteFile(downloadPath, $"编码器更新包 '{package.AssetName}'", WriteDiagnostic);

            try
            {
                if (Directory.Exists(extractRoot))
                {
                    Directory.Delete(extractRoot, true);
                }
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Failed to delete extracted encoder update directory '{extractRoot}'. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _apiHttpClient.Dispose();
        _downloadHttpClient.Dispose();
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_paths, nameof(GitHubReleaseEncoderUpdateService), message);
    }

    private static void ExtractArchive(string archivePath, string extractRoot)
    {
        var normalizedExtractRoot = Path.GetFullPath(extractRoot);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zipArchive = ZipFile.OpenRead(archivePath);
            foreach (var entry in zipArchive.Entries.Where(static entry => !string.IsNullOrWhiteSpace(entry.Name)))
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
        using var archive = SevenZipArchive.OpenArchive(archiveStream);

        foreach (var entry in archive.Entries.Where(static entry => !entry.IsDirectory))
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

    private static int ScoreX264Asset(string assetName)
    {
        var score = 0;
        score += ScoreContains(assetName, "x64", 200);
        score += ScoreContains(assetName, "gcc", 80);
        score += ScoreContains(assetName, "clang", 70);
        score += ScoreContains(assetName, "msvc", 60);
        score += ScoreContains(assetName, "x86-64", 40);
        score += ScoreContains(assetName, "avx512", -120);
        score += ScoreContains(assetName, "znver", -120);
        score += ScoreContains(assetName, "alderlake", -120);
        score += ScoreContains(assetName, "sandybridge", -80);
        score += ScoreContains(assetName, "haswell", -80);
        score += ScoreContains(assetName, "skylake", -80);
        return score;
    }

    private static int ScoreX265Asset(string assetName)
    {
        var score = 0;
        score += ScoreContains(assetName, "x64-x86-64-", 260);
        score += ScoreContains(assetName, "x86-64-v3", 180);
        score += ScoreContains(assetName, "x64", 200);
        score += ScoreContains(assetName, "gcc", 80);
        score += ScoreContains(assetName, "clang", 70);
        score += ScoreContains(assetName, "msvc", 60);
        score += ScoreContains(assetName, "icc", 50);
        score += ScoreContains(assetName, "alderlake", -140);
        score += ScoreContains(assetName, "avx512", -160);
        score += ScoreContains(assetName, "znver", -140);
        score += ScoreContains(assetName, "skylake", -120);
        score += ScoreContains(assetName, "haswell", -120);
        score += ScoreContains(assetName, "sandybridge", -120);
        return score;
    }

    private static int ScoreSvtAsset(string assetName)
    {
        var score = 0;
        score += ScoreContains(assetName, "x64", 200);
        score += ScoreContains(assetName, "msvc", 80);
        score += ScoreContains(assetName, "gcc", 70);
        score += ScoreContains(assetName, "clang", 60);
        score += ScoreContains(assetName, "essential", -500);
        score += ScoreContains(assetName, "hdr", -500);
        score += ScoreContains(assetName, "tritium", -500);
        score += ScoreContains(assetName, "psyex", -500);
        return score;
    }

    private static int ScoreContains(string source, string token, int score)
    {
        return source.Contains(token, StringComparison.OrdinalIgnoreCase) ? score : 0;
    }

    internal static bool IsExpectedEncoderAssetName(string? assetName, string expectedPrefix)
    {
        if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(expectedPrefix))
        {
            return false;
        }

        if (assetName.Contains("-windows-x64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Regex.IsMatch(
            assetName.Trim(),
            $"^{Regex.Escape(expectedPrefix)}-(?!.*(?:^|-)x64-)(?!.*(?:^|-)windows(?:-|\\.))(?!.*(?:^|-)x86(?:-|\\.))[A-Za-z0-9._-]+-x64\\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private async Task<EncoderUpdatePackage?> GetLatestPackageAsync(
        EncoderKind kind,
        EncoderArchitecture architecture,
        string repository,
        Func<GitHubReleaseAsset, bool> assetFilter,
        Func<GitHubReleaseAsset, int> scoreSelector,
        string notes,
        CancellationToken cancellationToken)
    {
        var response = await _apiHttpClient.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(contentStream, JsonOptions, cancellationToken);
        if (release is null)
        {
            return null;
        }

        var assetSelection = (release.Assets ?? [])
            .Where(assetFilter)
            .Select(asset => new
            {
                Asset = asset,
                Compatibility = EncoderCpuCompatibilityPolicy.Evaluate(asset.Name, _cpuProfile)
            })
            .Where(candidate => candidate.Compatibility.IsCompatible)
            .OrderByDescending(candidate => candidate.Compatibility.Preference)
            .ThenByDescending(candidate => scoreSelector(candidate.Asset))
            .ThenBy(candidate => candidate.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (assetSelection is null)
        {
            return null;
        }

        var asset = assetSelection.Asset;
        var sha256 = PackageIntegrityVerifier.NormalizeSha256Digest(asset.Digest);
        var isAutomatic = !string.IsNullOrWhiteSpace(sha256);
        var compatibilityNote = EncoderCpuCompatibilityPolicy.BuildSelectionNote(_cpuProfile, assetSelection.Compatibility);
        var resolvedNotes = isAutomatic
            ? $"{notes} {compatibilityNote} 已自动选择资产：{asset.Name}"
            : $"{notes} {compatibilityNote} 当前资源未提供 SHA256 摘要，已禁用自动安装。";

        return new EncoderUpdatePackage(
            kind,
            architecture,
            ResolveGitHubReleaseLabel(release, asset.Name),
            asset.Name,
            release.HtmlUrl,
            asset.BrowserDownloadUrl,
            release.PublishedAt,
            resolvedNotes,
            sha256,
            isAutomatic);
    }

    private static string ResolveGitHubReleaseLabel(GitHubRelease release, string? assetName = null)
    {
        return NormalizeGitHubReleaseLabel(release.TagName)
            ?? NormalizeGitHubReleaseLabel(release.Name)
            ?? NormalizeGitHubReleaseLabel(assetName)
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
            var stripped = trimmed[1..];
            if (Regex.IsMatch(stripped, "^\\d+(?:\\.\\d+)*$"))
            {
                return stripped;
            }

            var numericPrefix = Regex.Match(stripped, "^(\\d+(?:\\.\\d+)*)");
            if (numericPrefix.Success)
            {
                return numericPrefix.Groups[1].Value;
            }
        }

        return null;
    }

}

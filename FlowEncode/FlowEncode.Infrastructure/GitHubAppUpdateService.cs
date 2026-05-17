using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class GitHubAppUpdateService : IAppUpdateService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex VersionLabelRegex = new("^(?<base>\\d+\\.\\d+(?:\\.\\d+)*)(?<suffix>[0-9a-f]{7,12})?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string PreferredInstallerAssetNamePrefix = "FlowEncode_Setup_v";
    private const string ReleasesPageUrl = "https://github.com/frankie1024/FlowEncode/releases";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/frankie1024/FlowEncode/releases/latest";
    private const string ReleasesApiUrl = "https://api.github.com/repos/frankie1024/FlowEncode/releases?per_page=30";

    private readonly LocalAppPaths _paths;
    private readonly HttpClient _apiHttpClient;
    private readonly HttpClient _downloadHttpClient;

    public GitHubAppUpdateService(LocalAppPaths paths, IFlowEncodeHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _paths = paths;
        _apiHttpClient = httpClientFactory.CreateClient(FlowEncodeHttpClientProfile.Api);
        _downloadHttpClient = httpClientFactory.CreateClient(FlowEncodeHttpClientProfile.Download);
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersionLabel();
        var latestRelease = await TryGetLatestReleaseAsync(cancellationToken)
            ?? await GetLatestReleaseFromListAsync(cancellationToken);

        if (latestRelease is null)
        {
            return new AppUpdateCheckResult(
                currentVersion,
                string.Empty,
                ReleasesPageUrl,
                DateTimeOffset.MinValue,
                false,
                false,
                false,
                false,
                null,
                null,
                null);
        }

        var versionComparison = CompareVersionLabels(currentVersion, latestRelease.VersionLabel);
        var versionsComparable = versionComparison.HasValue;
        var updateAvailable = versionComparison is < 0;
        var currentVersionNewerThanRelease = versionComparison is > 0;
        var installerAsset = latestRelease.Release.Assets?
            .Where(static asset => IsInstallerAsset(asset.Name))
            .OrderByDescending(static asset => asset.Name.StartsWith(PreferredInstallerAssetNamePrefix, StringComparison.OrdinalIgnoreCase))
            .ThenBy(static asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return new AppUpdateCheckResult(
            currentVersion,
            latestRelease.VersionLabel,
            latestRelease.Release.HtmlUrl,
            latestRelease.Release.PublishedAt,
            true,
            versionsComparable,
            updateAvailable,
            currentVersionNewerThanRelease,
            installerAsset?.Name,
            installerAsset?.BrowserDownloadUrl,
            PackageIntegrityVerifier.NormalizeSha256Digest(installerAsset?.Digest));
    }

    private async Task<ComparableRelease?> TryGetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await SendGitHubGetAsync(LatestReleaseApiUrl, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken);
        return release is { Draft: false, Prerelease: false }
            ? BuildComparableRelease(release)
            : null;
    }

    private async Task<ComparableRelease?> GetLatestReleaseFromListAsync(CancellationToken cancellationToken)
    {
        using var response = await SendGitHubGetAsync(ReleasesApiUrl, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("Program update source is not publicly accessible (GitHub releases returned 404).");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<GitHubRelease[]>(stream, JsonOptions, cancellationToken)
            ?? [];

        return releases
            .Where(static release => !release.Draft && !release.Prerelease)
            .Select(BuildComparableRelease)
            .Aggregate(
                seed: (ComparableRelease?)null,
                (best, candidate) => best is null || CompareReleases(candidate, best) > 0
                    ? candidate
                    : best);
    }

    private async Task<HttpResponseMessage> SendGitHubGetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true
        };

        return await _apiHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public async Task<string> DownloadInstallerAsync(
        AppUpdateCheckResult updateResult,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updateResult);

        if (!updateResult.CanDownloadInstaller)
        {
            throw new InvalidOperationException("当前版本未提供可校验的安装包，无法执行自动更新。");
        }

        var installerAssetName = Path.GetFileName(updateResult.InstallerAssetName);
        if (string.IsNullOrWhiteSpace(installerAssetName))
        {
            throw new InvalidOperationException("更新安装包名称无效。");
        }

        var versionFolderName = SanitizePathSegment(updateResult.LatestVersion);
        var downloadDirectory = Path.Combine(_paths.DownloadsRootPath, "app-updates", versionFolderName);
        var downloadPath = Path.Combine(downloadDirectory, installerAssetName);

        if (File.Exists(downloadPath))
        {
            try
            {
                await PackageIntegrityVerifier.VerifySha256Async(
                    downloadPath,
                    updateResult.InstallerSha256!,
                    cancellationToken,
                    "应用更新安装包");
                progress?.Report(1.0);
                CleanupOldAppUpdateFolders(downloadDirectory);
                return downloadPath;
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Cached installer verification failed for '{downloadPath}'. {ex.GetType().Name}: {ex.Message}");
                BestEffortCleanup.DeleteFile(downloadPath, $"失效安装包 '{Path.GetFileName(downloadPath)}'", WriteDiagnostic);
            }
        }

        await DownloadAsync(updateResult.InstallerDownloadUrl!, downloadPath, progress, cancellationToken);

        try
        {
            await PackageIntegrityVerifier.VerifySha256Async(
                downloadPath,
                updateResult.InstallerSha256!,
                cancellationToken,
                "应用更新安装包");
            progress?.Report(1.0);
            CleanupOldAppUpdateFolders(downloadDirectory);
            return downloadPath;
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"Downloaded installer verification failed for '{downloadPath}'. {ex.GetType().Name}: {ex.Message}");
            BestEffortCleanup.DeleteFile(downloadPath, $"无效安装包 '{Path.GetFileName(downloadPath)}'", WriteDiagnostic);

            throw;
        }
    }

    public void Dispose()
    {
        _apiHttpClient.Dispose();
        _downloadHttpClient.Dispose();
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_paths, nameof(GitHubAppUpdateService), message);
    }

    private void CleanupOldAppUpdateFolders(string currentDownloadDirectory)
    {
        var appUpdatesRoot = Path.GetDirectoryName(currentDownloadDirectory);
        if (string.IsNullOrWhiteSpace(appUpdatesRoot) || !Directory.Exists(appUpdatesRoot))
        {
            return;
        }

        foreach (var oldFolder in Directory.EnumerateDirectories(appUpdatesRoot))
        {
            if (string.Equals(oldFolder, currentDownloadDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            BestEffortCleanup.DeleteDirectoryRecursively(oldFolder, $"旧版更新包 '{Path.GetFileName(oldFolder)}'", WriteDiagnostic);
        }
    }

    private static string GetCurrentVersionLabel()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return NormalizeVersionLabel(informationalVersion)
            ?? NormalizeVersionLabel(assembly.GetName().Version?.ToString())
            ?? "0.0.0";
    }

    private static string? NormalizeVersionLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 1
            && (trimmed[0] == 'v' || trimmed[0] == 'V')
            && char.IsDigit(trimmed[1]))
        {
            trimmed = trimmed[1..];
        }

        var versionMatch = Regex.Match(trimmed, "(?<base>\\d+\\.\\d+(?:\\.\\d+)*)(?<suffix>[0-9a-f]{7,12})?", RegexOptions.IgnoreCase);
        if (versionMatch.Success)
        {
            var suffix = versionMatch.Groups["suffix"].Success
                ? versionMatch.Groups["suffix"].Value.ToLowerInvariant()
                : string.Empty;
            return versionMatch.Groups["base"].Value + suffix;
        }

        return trimmed;
    }

    private static bool IsInstallerAsset(string? assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            return false;
        }

        return assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && assetName.Contains("setup", StringComparison.OrdinalIgnoreCase)
            && assetName.Contains("flowencode", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var sanitized = value.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private async Task DownloadAsync(
        string url,
        string filePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        using var response = await _downloadHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var target = File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var totalBytes = response.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long totalRead = 0;
        var lastReportedPercent = -1;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (totalBytes is not > 0)
            {
                continue;
            }

            var percent = (int)Math.Round(totalRead * 100.0 / totalBytes.Value);
            percent = Math.Clamp(percent, 0, 100);
            if (percent == lastReportedPercent)
            {
                continue;
            }

            lastReportedPercent = percent;
            progress?.Report(percent / 100.0);
        }
    }

    private static int? CompareVersionLabels(string currentVersion, string latestVersion)
    {
        var currentParsed = ParseVersionLabel(currentVersion);
        var latestParsed = ParseVersionLabel(latestVersion);
        if (currentParsed is null || latestParsed is null)
        {
            return null;
        }

        var baseComparison = currentParsed.BaseVersion.CompareTo(latestParsed.BaseVersion);
        if (baseComparison != 0)
        {
            return Math.Sign(baseComparison);
        }

        if (string.Equals(currentParsed.Suffix, latestParsed.Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(currentParsed.Suffix) && !string.IsNullOrWhiteSpace(latestParsed.Suffix))
        {
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(currentParsed.Suffix) && string.IsNullOrWhiteSpace(latestParsed.Suffix))
        {
            return 1;
        }

        return -1;
    }

    private static int CompareReleases(ComparableRelease left, ComparableRelease right)
    {
        if (left.ParsedVersion is not null && right.ParsedVersion is not null)
        {
            var baseComparison = left.ParsedVersion.BaseVersion.CompareTo(right.ParsedVersion.BaseVersion);
            if (baseComparison != 0)
            {
                return Math.Sign(baseComparison);
            }

            var leftHasSuffix = !string.IsNullOrWhiteSpace(left.ParsedVersion.Suffix);
            var rightHasSuffix = !string.IsNullOrWhiteSpace(right.ParsedVersion.Suffix);
            if (leftHasSuffix != rightHasSuffix)
            {
                return leftHasSuffix ? 1 : -1;
            }

            var publishedComparison = left.Release.PublishedAt.CompareTo(right.Release.PublishedAt);
            if (publishedComparison != 0)
            {
                return Math.Sign(publishedComparison);
            }

            return string.Compare(left.VersionLabel, right.VersionLabel, StringComparison.OrdinalIgnoreCase);
        }

        if (left.ParsedVersion is not null)
        {
            return 1;
        }

        if (right.ParsedVersion is not null)
        {
            return -1;
        }

        var fallbackPublishedComparison = left.Release.PublishedAt.CompareTo(right.Release.PublishedAt);
        if (fallbackPublishedComparison != 0)
        {
            return Math.Sign(fallbackPublishedComparison);
        }

        return string.Compare(left.VersionLabel, right.VersionLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static ComparableRelease BuildComparableRelease(GitHubRelease release)
    {
        return new ComparableRelease(
            release,
            NormalizeVersionLabel(release.TagName)
                ?? NormalizeVersionLabel(release.Name)
                ?? release.TagName,
            ParseVersionLabel(release.TagName)
                ?? ParseVersionLabel(release.Name));
    }

    private static ParsedVersionLabel? ParseVersionLabel(string? value)
    {
        var normalized = NormalizeVersionLabel(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var match = VersionLabelRegex.Match(normalized);
        if (!match.Success || !Version.TryParse(match.Groups["base"].Value, out var baseVersion))
        {
            return null;
        }

        return new ParsedVersionLabel(
            baseVersion,
            match.Groups["suffix"].Success
                ? match.Groups["suffix"].Value.ToLowerInvariant()
                : string.Empty);
    }

    private sealed record ParsedVersionLabel(Version BaseVersion, string Suffix);

    private sealed record ComparableRelease(
        GitHubRelease Release,
        string VersionLabel,
        ParsedVersionLabel? ParsedVersion);

}

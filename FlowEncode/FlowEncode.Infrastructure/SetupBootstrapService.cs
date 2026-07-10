using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;
using Microsoft.Win32;

namespace FlowEncode.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class SetupBootstrapService : ISetupBootstrapService, IDisposable
{
    private const string PythonTargetVersion = "3.12.10";
    private const string PythonInstallerFileName = "python-3.12.10-amd64.exe";
    private const string PythonInstallerUrl = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe";
    private const string PythonInstallerSha256 = "67b5635e80ea51072b87941312d00ec8927c4db9ba18938f7ad2d27b328b95fb";
    private const string PythonReleaseUrl = "https://www.python.org/downloads/release/python-31210/";
    private const string VapourSynthReleaseUrl = "https://www.vapoursynth.com/doc/installation.html";
    private static readonly IReadOnlyList<string> PythonInstallerPublishers = ["Python Software Foundation"];
    private static readonly IReadOnlyList<string> VsrepoPackageNames = ["ffms2", "fpng", "libp2p", "lsmas", "placebo", "mvsfunc", "havsfunc"];
    private static readonly IReadOnlyList<RegisteredToolKind> VsrepoPackageToolKinds =
    [
        RegisteredToolKind.VsrepoPackageFfms2,
        RegisteredToolKind.VsrepoPackageFpng,
        RegisteredToolKind.VsrepoPackageLibp2p,
        RegisteredToolKind.VsrepoPackageLsmas,
        RegisteredToolKind.VsrepoPackagePlacebo,
        RegisteredToolKind.VsrepoPackageMvsfunc,
        RegisteredToolKind.VsrepoPackageHavsfunc
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LocalAppPaths _paths;
    private readonly IEncoderUpdateService _encoderUpdateService;
    private readonly IEncoderToolchainService _toolchainService;
    private readonly IExternalToolService _externalToolService;
    private readonly HttpClient _apiHttpClient;
    private readonly HttpClient _downloadHttpClient;

    public SetupBootstrapService(
        LocalAppPaths paths,
        IEncoderUpdateService encoderUpdateService,
        IEncoderToolchainService toolchainService,
        IExternalToolService externalToolService,
        IFlowEncodeHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _paths = paths;
        _encoderUpdateService = encoderUpdateService;
        _toolchainService = toolchainService;
        _externalToolService = externalToolService;
        _apiHttpClient = httpClientFactory.CreateClient(FlowEncodeHttpClientProfile.Api);
        _downloadHttpClient = httpClientFactory.CreateClient(FlowEncodeHttpClientProfile.Download);
    }

    public Task<SetupDependencyStatusReport> GetStatusReportAsync(
        EnvironmentReadinessReport readiness,
        CancellationToken cancellationToken = default)
    {
        return GetStatusReportCoreAsync(readiness, cancellationToken);
    }

    public Task<SetupDependencyStatusReport> GetLocalStatusReportAsync(
        EnvironmentReadinessReport readiness,
        SetupDependencyStatusReport? previousReport = null,
        CancellationToken cancellationToken = default)
    {
        return GetLocalStatusReportCoreAsync(readiness, previousReport, cancellationToken);
    }

    public async Task RefreshVsPluginPackageDefinitionsAsync(
        EnvironmentReadinessReport? readiness = null,
        CancellationToken cancellationToken = default)
    {
        var installations = (await DiscoverPythonInstallationsAsync(readiness, cancellationToken))
            .OrderByDescending(static item => PythonRuntimeCompatibility.IsTargetMinor(item.Version) && item.Is64Bit)
            .ThenByDescending(static item => item.Version)
            .ToList();
        var pythonPath = installations.FirstOrDefault()?.ExecutablePath;
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            return;
        }

        await TryUpdateVsrepoPackageDefinitionsAsync(pythonPath, cancellationToken);
    }

    public Task InstallAsync(
        SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return kind switch
        {
            SetupDependencyKind.Python312 => InstallPythonAsync(progress, cancellationToken),
            SetupDependencyKind.VapourSynth => InstallVapourSynthAsync(progress, cancellationToken),
            SetupDependencyKind.Vsrepo => InstallPythonPackageAsync(kind, "vsrepo", progress, cancellationToken),
            SetupDependencyKind.VsPluginBundle => InstallVsPluginBundleAsync(progress, cancellationToken),
            SetupDependencyKind.Awsmfunc => InstallPythonPackageAsync(kind, "awsmfunc", progress, cancellationToken),
            SetupDependencyKind.Vsjetpack => InstallPythonPackageAsync(kind, "vsjetpack", progress, cancellationToken),
            SetupDependencyKind.FfmpegBundle => InstallLatestToolAsync(ExternalToolKind.Ffmpeg, kind, progress, cancellationToken),
            SetupDependencyKind.X264 => InstallLatestEncoderAsync(EncoderKind.X264, kind, progress, cancellationToken),
            SetupDependencyKind.X265 => InstallLatestEncoderAsync(EncoderKind.X265, kind, progress, cancellationToken),
            SetupDependencyKind.SvtAv1 => InstallLatestEncoderAsync(EncoderKind.SvtAv1, kind, progress, cancellationToken),
            SetupDependencyKind.Av1an => InstallLatestToolAsync(ExternalToolKind.Av1an, kind, progress, cancellationToken),
            _ => Task.FromException(new InvalidOperationException("This dependency does not support automatic installation in the setup guide."))
        };
    }

    public Task UninstallAsync(
        SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return kind switch
        {
            SetupDependencyKind.Python312 => UninstallPythonAsync(progress, cancellationToken),
            SetupDependencyKind.VapourSynth => UninstallPythonPackageAsync(kind, "vapoursynth", progress, cancellationToken),
            SetupDependencyKind.Vsrepo => UninstallPythonPackageAsync(kind, "vsrepo", progress, cancellationToken),
            SetupDependencyKind.VsPluginBundle => UninstallVsPluginBundleAsync(progress, cancellationToken),
            SetupDependencyKind.Awsmfunc => UninstallPythonPackageAsync(kind, "awsmfunc", progress, cancellationToken),
            SetupDependencyKind.Vsjetpack => UninstallPythonPackageAsync(kind, "vsjetpack", progress, cancellationToken),
            SetupDependencyKind.FfmpegBundle => UninstallManagedToolAsync(ExternalToolKind.Ffmpeg, kind, progress, cancellationToken),
            SetupDependencyKind.X264 => UninstallManagedEncoderAsync(EncoderKind.X264, kind, progress, cancellationToken),
            SetupDependencyKind.X265 => UninstallManagedEncoderAsync(EncoderKind.X265, kind, progress, cancellationToken),
            SetupDependencyKind.SvtAv1 => UninstallManagedEncoderAsync(EncoderKind.SvtAv1, kind, progress, cancellationToken),
            SetupDependencyKind.Av1an => UninstallManagedToolAsync(ExternalToolKind.Av1an, kind, progress, cancellationToken),
            SetupDependencyKind.Avs2PipeMod or
            SetupDependencyKind.DgDemux or
            SetupDependencyKind.Eac3To or
            SetupDependencyKind.Deew or
            SetupDependencyKind.Dee or
            SetupDependencyKind.OpusExt => UninstallManualDependencyAsync(kind, progress, cancellationToken),
            _ => Task.FromException(new InvalidOperationException("This dependency does not support uninstall in the setup guide."))
        };
    }

    public void Dispose()
    {
        _apiHttpClient.Dispose();
        _downloadHttpClient.Dispose();
    }

    private async Task<SetupDependencyStatusReport> GetStatusReportCoreAsync(
        EnvironmentReadinessReport readiness,
        CancellationToken cancellationToken)
    {
        var localContextTask = BuildLocalStatusContextAsync(readiness, refreshVsrepoPackageDefinitions: true, cancellationToken);
        var pypiVersionsTask = GetLatestPyPiVersionsAsync(cancellationToken);
        var encoderUpdatesTask = SafeGetEncoderUpdatesAsync(cancellationToken);
        var toolUpdatesTask = SafeGetToolUpdatesAsync(cancellationToken);

        await Task.WhenAll(localContextTask, pypiVersionsTask, encoderUpdatesTask, toolUpdatesTask);

        return BuildStatusReport(
            readiness,
            localContextTask.Result,
            pypiVersionsTask.Result,
            encoderUpdatesTask.Result,
            toolUpdatesTask.Result,
            DateTimeOffset.Now);
    }

    private async Task<SetupDependencyStatusReport> GetLocalStatusReportCoreAsync(
        EnvironmentReadinessReport readiness,
        SetupDependencyStatusReport? previousReport,
        CancellationToken cancellationToken)
    {
        var localContext = await BuildLocalStatusContextAsync(readiness, refreshVsrepoPackageDefinitions: false, cancellationToken);
        var localReport = BuildStatusReport(
            readiness,
            localContext,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<EncoderUpdatePackage>(),
            Array.Empty<ExternalToolUpdatePackage>(),
            DateTimeOffset.Now);

        return MergeRemoteMetadata(localReport, previousReport);
    }

    private async Task<SetupStatusLocalContext> BuildLocalStatusContextAsync(
        EnvironmentReadinessReport readiness,
        bool refreshVsrepoPackageDefinitions,
        CancellationToken cancellationToken)
    {
        var pythonInstallations = (await DiscoverPythonInstallationsAsync(readiness, cancellationToken))
            .OrderByDescending(static item => item.Version)
            .ToList();
        var targetPython = pythonInstallations.FirstOrDefault(static item => PythonRuntimeCompatibility.IsTargetMinor(item.Version) && item.Is64Bit);
        var compatiblePython = targetPython ?? pythonInstallations.FirstOrDefault(static item => PythonRuntimeCompatibility.IsSupportedRuntime(item.Version, item.Is64Bit));
        var highestPython = pythonInstallations.FirstOrDefault();
        var preferredPythonPath = compatiblePython?.ExecutablePath;
        var compatiblePythonReady = compatiblePython is not null;

        var vsrepoPackagesTask = QueryVsrepoInstalledPackagesAsync(preferredPythonPath, refreshVsrepoPackageDefinitions, cancellationToken);
        var awsmfuncVersionTask = QueryPythonDistributionVersionAsync(preferredPythonPath, "awsmfunc", cancellationToken);
        var vsjetpackVersionTask = QueryPythonDistributionVersionAsync(preferredPythonPath, "vsjetpack", cancellationToken);

        await Task.WhenAll(vsrepoPackagesTask, awsmfuncVersionTask, vsjetpackVersionTask);

        return new SetupStatusLocalContext(
            targetPython,
            highestPython,
            compatiblePythonReady,
            vsrepoPackagesTask.Result,
            awsmfuncVersionTask.Result,
            vsjetpackVersionTask.Result);
    }

    private SetupDependencyStatusReport BuildStatusReport(
        EnvironmentReadinessReport readiness,
        SetupStatusLocalContext localContext,
        IReadOnlyDictionary<string, string> pypiVersions,
        IReadOnlyList<EncoderUpdatePackage> encoderUpdates,
        IReadOnlyList<ExternalToolUpdatePackage> toolUpdates,
        DateTimeOffset checkedAt)
    {
        var dependencies = new List<SetupDependencyStatus>
        {
            BuildPythonStatus(localContext.TargetPython, localContext.HighestPython),
            BuildVapourSynthStatus(readiness, pypiVersions, localContext.CompatiblePythonReady),
            BuildVsrepoStatus(readiness, pypiVersions, localContext.CompatiblePythonReady),
            BuildVsPluginBundleStatus(readiness, localContext.VsrepoPackages, localContext.CompatiblePythonReady),
            BuildAwsmfuncStatus(readiness, localContext.AwsmfuncVersion, pypiVersions, localContext.CompatiblePythonReady),
            BuildVsjetpackStatus(readiness, localContext.VsjetpackVersion, pypiVersions, localContext.CompatiblePythonReady),
            BuildFfmpegBundleStatus(readiness, toolUpdates),
            BuildEncoderStatus(readiness, RegisteredToolKind.X264, encoderUpdates),
            BuildEncoderStatus(readiness, RegisteredToolKind.X265, encoderUpdates),
            BuildEncoderStatus(readiness, RegisteredToolKind.SvtAv1, encoderUpdates),
            BuildAv1anBackendStatus(readiness, toolUpdates),
            BuildToolStatus(readiness, SetupDependencyKind.Avs2PipeMod, RegisteredToolKind.Avs2PipeMod),
            BuildToolStatus(readiness, SetupDependencyKind.DgDemux, RegisteredToolKind.DgDemux),
            BuildToolStatus(readiness, SetupDependencyKind.Eac3To, RegisteredToolKind.Eac3To),
            BuildToolStatus(readiness, SetupDependencyKind.Deew, RegisteredToolKind.Deew),
            BuildToolStatus(readiness, SetupDependencyKind.Dee, RegisteredToolKind.Dee),
            BuildToolStatus(readiness, SetupDependencyKind.OpusExt, RegisteredToolKind.OpusExt)
        };

        return new SetupDependencyStatusReport(checkedAt, dependencies);
    }

    private SetupDependencyStatusReport MergeRemoteMetadata(
        SetupDependencyStatusReport localReport,
        SetupDependencyStatusReport? previousReport)
    {
        if (previousReport is null)
        {
            return localReport;
        }

        var previousMap = previousReport.Dependencies.ToDictionary(static item => item.Kind, static item => item);
        var mergedDependencies = localReport.Dependencies
            .Select(localStatus =>
            {
                if (!previousMap.TryGetValue(localStatus.Kind, out var previousStatus))
                {
                    return localStatus;
                }

                if (!string.IsNullOrWhiteSpace(localStatus.LatestVersion))
                {
                    return localStatus;
                }

                var mergedLatestVersion = previousStatus.LatestVersion;
                if (string.IsNullOrWhiteSpace(mergedLatestVersion))
                {
                    return localStatus;
                }

                var mergedReleaseUrl = string.IsNullOrWhiteSpace(localStatus.ReleaseUrl)
                    ? previousStatus.ReleaseUrl
                    : localStatus.ReleaseUrl;

                var mergedUpdateAvailable = HasSetupDependencyVersionUpdate(localStatus.Kind, localStatus.InstalledVersion, mergedLatestVersion);

                return localStatus with
                {
                    LatestVersion = mergedLatestVersion,
                    UpdateAvailable = mergedUpdateAvailable,
                    ReleaseUrl = mergedReleaseUrl
                };
            })
            .ToList();

        return localReport with
        {
            Dependencies = mergedDependencies
        };
    }

    private SetupDependencyStatus BuildPythonStatus(PythonInstallation? targetPython, PythonInstallation? highestPython)
    {
        if (targetPython is not null)
        {
            return new SetupDependencyStatus(
                SetupDependencyKind.Python312,
                ReadinessState.Ready,
                targetPython.VersionText,
                string.Empty,
                false,
                targetPython.ExecutablePath,
                PythonReleaseUrl,
                false,
                false,
                targetPython.ExecutablePath);
        }

        if (highestPython is not null)
        {
            var state = PythonRuntimeCompatibility.IsSupportedVersion(highestPython.Version) && highestPython.Is64Bit
                ? ReadinessState.Partial
                : ReadinessState.Missing;

            return new SetupDependencyStatus(
                SetupDependencyKind.Python312,
                state,
                highestPython.VersionText,
                PythonTargetVersion,
                false,
                highestPython.ExecutablePath,
                PythonReleaseUrl,
                true,
                true,
                highestPython.ExecutablePath);
        }

        return new SetupDependencyStatus(
            SetupDependencyKind.Python312,
            ReadinessState.Missing,
            string.Empty,
            PythonTargetVersion,
            false,
            string.Empty,
            PythonReleaseUrl,
            true,
            true,
            string.Empty);
    }

    private SetupDependencyStatus BuildVapourSynthStatus(
        EnvironmentReadinessReport readiness,
        IReadOnlyDictionary<string, string> pypiVersions,
        bool targetPythonReady)
    {
        var probe = GetToolResult(readiness, RegisteredToolKind.Vspipe);
        var latestVersion = NormalizeVapourSynthVersion(GetValueOrEmpty(pypiVersions, "vapoursynth"));

        return new SetupDependencyStatus(
            SetupDependencyKind.VapourSynth,
            probe.State,
            NormalizeVapourSynthVersion(probe.DetectedVersion),
            latestVersion,
            AreVersionsComparableAndDifferent(probe.DetectedVersion, latestVersion),
            probe.ExecutablePath,
            string.IsNullOrWhiteSpace(probe.ReleaseUrl) ? VapourSynthReleaseUrl : probe.ReleaseUrl,
            true,
            targetPythonReady,
            BuildToolDetail(probe));
    }

    private SetupDependencyStatus BuildVsrepoStatus(
        EnvironmentReadinessReport readiness,
        IReadOnlyDictionary<string, string> pypiVersions,
        bool targetPythonReady)
    {
        var probe = GetToolResult(readiness, RegisteredToolKind.Vsrepo);
        var latestVersion = GetValueOrEmpty(pypiVersions, "vsrepo");

        return new SetupDependencyStatus(
            SetupDependencyKind.Vsrepo,
            probe.State,
            probe.State == ReadinessState.Ready ? probe.DetectedVersion : string.Empty,
            latestVersion,
            AreVersionsComparableAndDifferent(probe.DetectedVersion, latestVersion),
            probe.ExecutablePath,
            probe.ReleaseUrl,
            true,
            targetPythonReady,
            BuildToolDetail(probe));
    }

    private SetupDependencyStatus BuildVsPluginBundleStatus(
        EnvironmentReadinessReport readiness,
        IReadOnlyDictionary<string, VsrepoInstalledPackage> installedPackages,
        bool targetPythonReady)
    {
        var packageResults = VsrepoPackageToolKinds
            .Select(kind => GetToolResult(readiness, kind))
            .ToArray();
        var readyCount = packageResults.Count(static result => result.State == ReadinessState.Ready);
        var updateAvailable = VsrepoPackageNames.Any(packageName =>
        {
            return installedPackages.TryGetValue(packageName, out var package)
                && !string.IsNullOrWhiteSpace(package.InstalledVersion)
                && !string.IsNullOrWhiteSpace(package.LatestVersion)
                && !string.Equals(package.InstalledVersion, package.LatestVersion, StringComparison.OrdinalIgnoreCase);
        });

        var detailLines = VsrepoPackageToolKinds.Select(kind =>
        {
            var name = kind.ToDisplayName();
            if (installedPackages.TryGetValue(name, out var package))
            {
                var latestSuffix = string.IsNullOrWhiteSpace(package.LatestVersion)
                    ? string.Empty
                    : $" -> {package.LatestVersion}";
                return $"{name}: {package.InstalledVersion}{latestSuffix}";
            }

            return $"{name}: {GetToolResult(readiness, kind).State}";
        });

        return new SetupDependencyStatus(
            SetupDependencyKind.VsPluginBundle,
            ResolveCompositeState(packageResults),
            $"{readyCount}/{VsrepoPackageToolKinds.Count}",
            $"{VsrepoPackageToolKinds.Count} required",
            updateAvailable,
            string.Empty,
            GetToolResult(readiness, RegisteredToolKind.Vsrepo).ReleaseUrl,
            true,
            targetPythonReady,
            string.Join(Environment.NewLine, detailLines));
    }

    private SetupDependencyStatus BuildAwsmfuncStatus(
        EnvironmentReadinessReport readiness,
        string installedVersion,
        IReadOnlyDictionary<string, string> pypiVersions,
        bool targetPythonReady)
    {
        var probe = GetToolResult(readiness, RegisteredToolKind.PythonModuleAwsmfunc);
        var latestVersion = GetValueOrEmpty(pypiVersions, "awsmfunc");
        return new SetupDependencyStatus(
            SetupDependencyKind.Awsmfunc,
            probe.State,
            installedVersion,
            latestVersion,
            AreVersionsComparableAndDifferent(installedVersion, latestVersion),
            probe.ExecutablePath,
            probe.ReleaseUrl,
            true,
            targetPythonReady,
            BuildToolDetail(probe));
    }

    private SetupDependencyStatus BuildVsjetpackStatus(
        EnvironmentReadinessReport readiness,
        string installedVersion,
        IReadOnlyDictionary<string, string> pypiVersions,
        bool targetPythonReady)
    {
        var probe = GetToolResult(readiness, RegisteredToolKind.PythonModuleVsjetpack);
        var latestVersion = GetValueOrEmpty(pypiVersions, "vsjetpack");

        return new SetupDependencyStatus(
            SetupDependencyKind.Vsjetpack,
            probe.State,
            installedVersion,
            latestVersion,
            AreVersionsComparableAndDifferent(installedVersion, latestVersion),
            probe.ExecutablePath,
            probe.ReleaseUrl,
            true,
            targetPythonReady,
            BuildToolDetail(probe));
    }

    private SetupDependencyStatus BuildFfmpegBundleStatus(
        EnvironmentReadinessReport readiness,
        IReadOnlyList<ExternalToolUpdatePackage> toolUpdates)
    {
        var ffmpeg = GetToolResult(readiness, RegisteredToolKind.Ffmpeg);
        var ffprobe = GetToolResult(readiness, RegisteredToolKind.Ffprobe);
        var latestPackage = toolUpdates.FirstOrDefault(static package => package.Kind == ExternalToolKind.Ffmpeg);
        var detail = string.Join(
            Environment.NewLine,
            [
                $"ffmpeg: {BuildToolDetail(ffmpeg)}",
                $"ffprobe: {BuildToolDetail(ffprobe)}"
            ]);

        return new SetupDependencyStatus(
            SetupDependencyKind.FfmpegBundle,
            ResolveCompositeState(ffmpeg, ffprobe),
            ffmpeg.DetectedVersion,
            latestPackage?.ReleaseName ?? string.Empty,
            HasSetupDependencyVersionUpdate(
                SetupDependencyKind.FfmpegBundle,
                ffmpeg.DetectedVersion,
                latestPackage?.ReleaseName ?? string.Empty),
            ffmpeg.ExecutablePath,
            latestPackage?.ReleaseUrl ?? ffmpeg.ReleaseUrl,
            true,
            true,
            detail);
    }

    private SetupDependencyStatus BuildEncoderStatus(
        EnvironmentReadinessReport readiness,
        RegisteredToolKind toolKind,
        IReadOnlyList<EncoderUpdatePackage> encoderUpdates)
    {
        var probe = GetToolResult(readiness, toolKind);
        var encoderKind = toolKind switch
        {
            RegisteredToolKind.X264 => EncoderKind.X264,
            RegisteredToolKind.X265 => EncoderKind.X265,
            RegisteredToolKind.SvtAv1 => EncoderKind.SvtAv1,
            _ => throw new ArgumentOutOfRangeException(nameof(toolKind), toolKind, null)
        };
        var dependencyKind = toolKind switch
        {
            RegisteredToolKind.X264 => SetupDependencyKind.X264,
            RegisteredToolKind.X265 => SetupDependencyKind.X265,
            RegisteredToolKind.SvtAv1 => SetupDependencyKind.SvtAv1,
            _ => throw new ArgumentOutOfRangeException(nameof(toolKind), toolKind, null)
        };
        var latestPackage = encoderUpdates.FirstOrDefault(package => package.Kind == encoderKind && package.Architecture == EncoderArchitecture.X64);

        return new SetupDependencyStatus(
            dependencyKind,
            probe.State,
            probe.DetectedVersion,
            latestPackage?.ReleaseName ?? string.Empty,
            HasSetupDependencyVersionUpdate(
                dependencyKind,
                probe.DetectedVersion,
                latestPackage?.ReleaseName ?? string.Empty),
            probe.ExecutablePath,
            latestPackage?.ReleaseUrl ?? probe.ReleaseUrl,
            true,
            true,
            BuildToolDetail(probe));
    }

    private SetupDependencyStatus BuildToolStatus(
        EnvironmentReadinessReport readiness,
        SetupDependencyKind dependencyKind,
        RegisteredToolKind toolKind,
        IReadOnlyList<ExternalToolUpdatePackage>? toolUpdates = null,
        bool installSupported = false)
    {
        var probe = GetToolResult(readiness, toolKind);
        var latestPackage = dependencyKind == SetupDependencyKind.Av1an
            ? toolUpdates?.FirstOrDefault(static package => package.Kind == ExternalToolKind.Av1an)
            : null;

        return new SetupDependencyStatus(
            dependencyKind,
            probe.State,
            probe.DetectedVersion,
            latestPackage?.ReleaseName ?? string.Empty,
            AreVersionsComparableAndDifferent(probe.DetectedVersion, latestPackage?.ReleaseName ?? string.Empty),
            probe.ExecutablePath,
            latestPackage?.ReleaseUrl ?? probe.ReleaseUrl,
            installSupported,
            installSupported,
            BuildToolDetail(probe));
    }

    private SetupDependencyStatus BuildAv1anBackendStatus(
        EnvironmentReadinessReport readiness,
        IReadOnlyList<ExternalToolUpdatePackage>? toolUpdates)
    {
        var probe = GetToolResult(readiness, RegisteredToolKind.Av1an);
        var latestPackage = toolUpdates?.FirstOrDefault(static package => package.Kind == ExternalToolKind.Av1an);
        var effectiveState = probe.State == ReadinessState.Ready && !probe.IsProtocolCompatible
            ? ReadinessState.Partial
            : probe.State;
        var compatibilityDetail = probe.State switch
        {
            ReadinessState.Ready when probe.IsProtocolCompatible => "Protocol compatible",
            ReadinessState.Ready => "Executable detected, protocol compatibility pending",
            _ => probe.BackendCompatibilityDetail
        };

        var detail = string.IsNullOrWhiteSpace(probe.DetectedVersion)
            ? compatibilityDetail
            : string.IsNullOrWhiteSpace(compatibilityDetail)
                ? probe.DetectedVersion
                : $"{probe.DetectedVersion} · {compatibilityDetail}";

        return new SetupDependencyStatus(
            SetupDependencyKind.Av1an,
            effectiveState,
            probe.DetectedVersion,
            latestPackage?.ReleaseName ?? string.Empty,
            AreVersionsComparableAndDifferent(probe.DetectedVersion, latestPackage?.ReleaseName ?? string.Empty),
            probe.ExecutablePath,
            latestPackage?.ReleaseUrl ?? probe.ReleaseUrl,
            true,
            true,
            detail,
            probe.IsProtocolCompatible,
            probe.ProtocolVersion,
            compatibilityDetail);
    }

    private async Task InstallPythonAsync(
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.DownloadsRootPath);
        var installerPath = Path.Combine(_paths.DownloadsRootPath, PythonInstallerFileName);

        ReportProgress(progress, SetupDependencyKind.Python312, 5, "Preparing Python installer...");
        await DownloadFileAsync(PythonInstallerUrl, installerPath, SetupDependencyKind.Python312, progress, cancellationToken);

        ReportProgress(progress, SetupDependencyKind.Python312, 72, "Verifying Python installer integrity...");
        await PackageIntegrityVerifier.VerifySha256Async(
            installerPath,
            PythonInstallerSha256,
            cancellationToken,
            "Python 安装器");
        PackageIntegrityVerifier.VerifyAuthenticodeSignature(
            installerPath,
            PythonInstallerPublishers,
            "Python 安装器");

        ReportProgress(progress, SetupDependencyKind.Python312, 78, "Running Python installer...");
        await RunProcessAsync(
            installerPath,
            ["/quiet", "InstallAllUsers=0", "PrependPath=1", "Include_launcher=1", "Include_test=0"],
            cancellationToken,
            timeoutMs: 600_000);

        ReportProgress(progress, SetupDependencyKind.Python312, 95, "Verifying Python 3.12...");
        var installations = await DiscoverPythonInstallationsAsync(null, cancellationToken);
        if (!installations.Any(static item => PythonRuntimeCompatibility.IsTargetMinor(item.Version) && item.Is64Bit))
        {
            throw new InvalidOperationException("Python 3.12 x64 installation finished, but the interpreter could not be detected afterwards.");
        }

        ReportProgress(progress, SetupDependencyKind.Python312, 100, "Python 3.12 is ready.");
    }

    private async Task InstallVapourSynthAsync(
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pythonPath = await ResolveCompatiblePythonAsync(cancellationToken);
        await InstallPythonPackageCoreAsync(
            SetupDependencyKind.VapourSynth,
            pythonPath,
            "vapoursynth",
            progress,
            cancellationToken,
            finishPercent: 82);

        ReportProgress(progress, SetupDependencyKind.VapourSynth, 88, "Configuring VapourSynth...");
        await RunProcessAsync(
            pythonPath,
            ["-m", "vapoursynth", "config"],
            cancellationToken,
            timeoutMs: 120_000);

        ReportProgress(progress, SetupDependencyKind.VapourSynth, 100, "VapourSynth is ready.");
    }

    private async Task InstallPythonPackageAsync(
        SetupDependencyKind kind,
        string packageSpec,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pythonPath = await ResolveCompatiblePythonAsync(cancellationToken);
        await InstallPythonPackageCoreAsync(kind, pythonPath, packageSpec, progress, cancellationToken, finishPercent: 100);
        ReportProgress(progress, kind, 100, "Installation completed.");
    }

    private async Task InstallVsPluginBundleAsync(
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pythonPath = await ResolveCompatiblePythonAsync(cancellationToken);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await EnsureVsrepoArchiveExtractorAsync(pythonPath, progress, cancellationToken);
        await EnsureVsrepoUserSiteDirectoryAsync(pythonPath, cancellationToken);
        ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 5, "Updating vsrepo package definitions...");
        try
        {
            await RunProcessAsync(
                pythonPath,
                ["-m", "vsrepo.vsrepo", "-t", GetVsrepoTarget(), "update"],
                cancellationToken,
                timeoutMs: 120_000,
                onOutput: line => ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 10, line),
                onError: line => ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 10, line));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Unable to update vsrepo package definitions. Check the network connection and try again.",
                ex);
        }

        ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 20, $"Running vsrepo install {string.Join(" ", VsrepoPackageNames)}...");
        await RunProcessAsync(
            pythonPath,
            ["-m", "vsrepo.vsrepo", "-t", GetVsrepoTarget(), "install", .. VsrepoPackageNames],
            cancellationToken,
            timeoutMs: 600_000,
            onOutput: line =>
            {
                foreach (var packageName in VsrepoPackageNames)
                {
                    if (!line.Contains(packageName, StringComparison.OrdinalIgnoreCase) || !completed.Add(packageName))
                    {
                        continue;
                    }

                    var percent = 20 + (completed.Count * 70.0 / VsrepoPackageNames.Count);
                    ReportProgress(progress, SetupDependencyKind.VsPluginBundle, percent, line);
                }
            },
            onError: line => ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 30, line));

        ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 100, "VS plugin bundle is ready.");
    }

    private async Task InstallLatestEncoderAsync(
        EncoderKind encoderKind,
        SetupDependencyKind dependencyKind,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ReportProgress(progress, dependencyKind, 10, "Checking encoder package...");
        var package = (await _encoderUpdateService.GetAvailableUpdatesAsync(cancellationToken))
            .FirstOrDefault(item => item.Kind == encoderKind && item.Architecture == EncoderArchitecture.X64 && item.IsAutomatic)
            ?? throw new InvalidOperationException($"No automatic package was found for {encoderKind.ToDisplayName()}.");

        ReportProgress(progress, dependencyKind, 35, $"Installing {package.ReleaseName}...");
        await _encoderUpdateService.InstallUpdateAsync(package, cancellationToken);
        ReportProgress(progress, dependencyKind, 100, $"{encoderKind.ToDisplayName()} is ready.");
    }

    private async Task InstallLatestToolAsync(
        ExternalToolKind toolKind,
        SetupDependencyKind dependencyKind,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ReportProgress(progress, dependencyKind, 10, "Checking tool package...");
        var package = (await _externalToolService.GetAvailableUpdatesAsync(cancellationToken))
            .FirstOrDefault(item => item.Kind == toolKind && item.IsAutomatic)
            ?? throw new InvalidOperationException(
                toolKind == ExternalToolKind.Av1an
                    ? "当前未找到可自动安装的 Av1an 托管包。请先手动导入 av1an.exe，或等待后续 FlowEncode 更新。"
                    : $"No automatic package was found for {toolKind.ToDisplayName()}.");

        ReportProgress(progress, dependencyKind, 35, $"Installing {package.ReleaseName}...");
        await _externalToolService.InstallUpdateAsync(package, cancellationToken);
        ReportProgress(progress, dependencyKind, 100, $"{toolKind.ToDisplayName()} is ready.");
    }

    private async Task UninstallPythonAsync(
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ReportProgress(progress, SetupDependencyKind.Python312, 10, "Locating Python 3.12 uninstall entry...");
        var uninstallEntry = FindPython312UninstallEntry()
            ?? throw new InvalidOperationException("Python 3.12 uninstall entry was not found.");

        var command = BuildSilentUninstallCommand(uninstallEntry);
        ReportProgress(progress, SetupDependencyKind.Python312, 35, "Running Python uninstaller...");
        await RunProcessAsync(command.FileName, command.Arguments, cancellationToken, timeoutMs: 600_000);

        ReportProgress(progress, SetupDependencyKind.Python312, 90, "Verifying Python 3.12 removal...");
        var installations = await DiscoverPythonInstallationsAsync(null, cancellationToken);
        if (installations.Any(static item => item.Version.Major == 3 && item.Version.Minor == 12))
        {
            throw new InvalidOperationException("Python 3.12 is still present after uninstall completed.");
        }

        ReportProgress(progress, SetupDependencyKind.Python312, 100, "Python 3.12 was removed.");
    }

    private async Task UninstallPythonPackageAsync(
        SetupDependencyKind kind,
        string packageName,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pythonPath = await ResolvePreferredPythonAsync(cancellationToken);
        ReportProgress(progress, kind, 15, $"Uninstalling {packageName}...");
        await RunProcessAsync(
            pythonPath,
            ["-m", "pip", "uninstall", "-y", packageName],
            cancellationToken,
            timeoutMs: 600_000,
            onOutput: line => ReportProgress(progress, kind, 50, line),
            onError: line => ReportProgress(progress, kind, 50, line));
        ReportProgress(progress, kind, 100, $"{packageName} was removed.");
    }

    private async Task UninstallVsPluginBundleAsync(
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pythonPath = await ResolvePreferredPythonAsync(cancellationToken);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await EnsureVsrepoUserSiteDirectoryAsync(pythonPath, cancellationToken);
        ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 10, "Removing VS plugin bundle...");
        await RunProcessAsync(
            pythonPath,
            ["-m", "vsrepo.vsrepo", "-t", GetVsrepoTarget(), "uninstall", .. VsrepoPackageNames],
            cancellationToken,
            timeoutMs: 600_000,
            onOutput: line =>
            {
                foreach (var packageName in VsrepoPackageNames)
                {
                    if (!line.Contains(packageName, StringComparison.OrdinalIgnoreCase) || !completed.Add(packageName))
                    {
                        continue;
                    }

                    var percent = 10 + (completed.Count * 80.0 / VsrepoPackageNames.Count);
                    ReportProgress(progress, SetupDependencyKind.VsPluginBundle, percent, line);
                }
            },
            onError: line => ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 30, line));

        ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 100, "VS plugin bundle was removed.");
    }

    private async Task UninstallManagedEncoderAsync(
        EncoderKind encoderKind,
        SetupDependencyKind dependencyKind,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ReportProgress(progress, dependencyKind, 25, $"Removing {encoderKind.ToDisplayName()}...");
        await _toolchainService.RemoveBinaryAsync(encoderKind, cancellationToken);
        ReportProgress(progress, dependencyKind, 100, $"{encoderKind.ToDisplayName()} was removed.");
    }

    private async Task UninstallManagedToolAsync(
        ExternalToolKind toolKind,
        SetupDependencyKind dependencyKind,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ReportProgress(progress, dependencyKind, 25, $"Removing {toolKind.ToDisplayName()}...");
        await _externalToolService.RemoveManagedBinaryAsync(toolKind, cancellationToken);
        ReportProgress(progress, dependencyKind, 100, $"{toolKind.ToDisplayName()} was removed.");
    }

    private Task UninstallManualDependencyAsync(
        SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, kind, 25, "Removing app-managed binaries...");

            foreach (var fileName in GetManualDependencyFileNames(kind))
            {
                var path = Path.Combine(_paths.ToolsRootPath, fileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            ReportProgress(progress, kind, 100, "App-managed binary was removed.");
        }, cancellationToken);
    }

    private async Task InstallPythonPackageCoreAsync(
        SetupDependencyKind kind,
        string pythonPath,
        string packageSpec,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken,
        double finishPercent)
    {
        ReportProgress(progress, kind, 10, "Preparing pip install...");
        await RunProcessAsync(
            pythonPath,
            ["-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "--progress-bar", "off", packageSpec],
            cancellationToken,
            timeoutMs: 600_000,
            onOutput: line => ReportProgress(progress, kind, MapPipProgress(line, finishPercent), line),
            onError: line => ReportProgress(progress, kind, MapPipProgress(line, finishPercent), line));
    }

    private async Task<string> ResolveCompatiblePythonAsync(CancellationToken cancellationToken)
    {
        var installations = await DiscoverPythonInstallationsAsync(null, cancellationToken);
        var target = installations
            .OrderByDescending(static item => item.Version)
            .FirstOrDefault(static item => PythonRuntimeCompatibility.IsTargetMinor(item.Version) && item.Is64Bit)
            ?? installations
                .OrderByDescending(static item => item.Version)
                .FirstOrDefault(static item => PythonRuntimeCompatibility.IsSupportedRuntime(item.Version, item.Is64Bit));

        return target?.ExecutablePath
            ?? throw new InvalidOperationException("Python 3.12 x64 or newer x64 was not detected. Install Python 3.12+ x64 first.");
    }

    private async Task<string> ResolvePreferredPythonAsync(CancellationToken cancellationToken)
    {
        var installations = await DiscoverPythonInstallationsAsync(null, cancellationToken);
        var ordered = installations
            .OrderByDescending(static item => item.Version)
            .ToList();
        var target = ordered.FirstOrDefault(static item => PythonRuntimeCompatibility.IsTargetMinor(item.Version) && item.Is64Bit);
        return target?.ExecutablePath
            ?? ordered.FirstOrDefault(static item => PythonRuntimeCompatibility.IsSupportedRuntime(item.Version, item.Is64Bit))?.ExecutablePath
            ?? ordered.FirstOrDefault()?.ExecutablePath
            ?? throw new InvalidOperationException("No Python interpreter was detected.");
    }

    private PythonUninstallEntry? FindPython312UninstallEntry()
    {
        var paths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var path in paths)
        {
            using var currentUser = Registry.CurrentUser.OpenSubKey(path);
            foreach (var entry in EnumeratePythonUninstallEntries(currentUser))
            {
                if (PythonRuntimeCompatibility.IsTargetMinor(entry.Version))
                {
                    return entry;
                }
            }

            using var localMachine = Registry.LocalMachine.OpenSubKey(path);
            foreach (var entry in EnumeratePythonUninstallEntries(localMachine))
            {
                if (PythonRuntimeCompatibility.IsTargetMinor(entry.Version))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private static IEnumerable<PythonUninstallEntry> EnumeratePythonUninstallEntries(RegistryKey? root)
    {
        if (root is null)
        {
            yield break;
        }

        foreach (var keyName in root.GetSubKeyNames())
        {
            using var subKey = root.OpenSubKey(keyName);
            if (subKey is null)
            {
                continue;
            }

            var displayName = subKey.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(displayName)
                || !displayName.Contains("Python", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var uninstallString = subKey.GetValue("QuietUninstallString") as string
                ?? subKey.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(uninstallString))
            {
                continue;
            }

            var versionText = (subKey.GetValue("DisplayVersion") as string) ?? ExtractVersionFromText(displayName);
            if (!Version.TryParse(ExtractVersionFromText(versionText), out var version))
            {
                continue;
            }

            yield return new PythonUninstallEntry(
                displayName,
                version,
                uninstallString,
                subKey.GetValue("QuietUninstallString") as string);
        }
    }

    private static SilentCommand BuildSilentUninstallCommand(PythonUninstallEntry entry)
    {
        var commandLine = !string.IsNullOrWhiteSpace(entry.QuietUninstallString)
            ? entry.QuietUninstallString
            : entry.UninstallString;
        var parsed = ParseCommandLine(commandLine);
        var arguments = parsed.Arguments.ToList();

        if (Path.GetFileName(parsed.FileName).Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            if (!arguments.Any(static argument => argument.Equals("/qn", StringComparison.OrdinalIgnoreCase)))
            {
                arguments.Add("/qn");
            }

            if (!arguments.Any(static argument => argument.Equals("/norestart", StringComparison.OrdinalIgnoreCase)))
            {
                arguments.Add("/norestart");
            }
        }
        else if (!arguments.Any(static argument =>
                     argument.Equals("/quiet", StringComparison.OrdinalIgnoreCase)
                     || argument.Equals("-quiet", StringComparison.OrdinalIgnoreCase)
                     || argument.Equals("/passive", StringComparison.OrdinalIgnoreCase)))
        {
            arguments.Add("/quiet");
        }

        return new SilentCommand(parsed.FileName, arguments);
    }

    private static SilentCommand ParseCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new InvalidOperationException("The uninstall command line is empty.");
        }

        var text = commandLine.Trim();
        string fileName;
        string remaining;

        if (text.StartsWith('"'))
        {
            var closingQuote = text.IndexOf('"', 1);
            if (closingQuote < 0)
            {
                throw new InvalidOperationException("The uninstall command line is malformed.");
            }

            fileName = text[1..closingQuote];
            remaining = text[(closingQuote + 1)..].Trim();
        }
        else
        {
            var separator = text.IndexOf(' ');
            if (separator < 0)
            {
                fileName = text;
                remaining = string.Empty;
            }
            else
            {
                fileName = text[..separator];
                remaining = text[(separator + 1)..].Trim();
            }
        }

        return new SilentCommand(fileName, SplitCommandLineArguments(remaining));
    }

    private static IReadOnlyList<string> SplitCommandLineArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var arguments = new List<string>();
        var builder = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case ' ' when !inQuotes:
                    if (builder.Length > 0)
                    {
                        arguments.Add(builder.ToString());
                        builder.Clear();
                    }

                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        if (builder.Length > 0)
        {
            arguments.Add(builder.ToString());
        }

        return arguments;
    }

    private static IReadOnlyList<string> GetManualDependencyFileNames(SetupDependencyKind kind)
    {
        return kind switch
        {
            SetupDependencyKind.Avs2PipeMod => ["avs2pipemod64.exe", "avs2pipemod.exe", "Avs2Pipemod.exe"],
            SetupDependencyKind.DgDemux => ["DGDemux.exe", "dgdemux.exe"],
            SetupDependencyKind.Eac3To => ["eac3to.exe", "Eac3to.exe"],
            SetupDependencyKind.Deew => ["deew.exe", "Deew.exe"],
            SetupDependencyKind.Dee => ["dee.exe", "DEE.exe", "DolbyEncodingEngine.exe"],
            SetupDependencyKind.OpusExt => ["opusext.exe", "OpusExt.exe", "opusenc.exe", "OpusEnc.exe"],
            _ => Array.Empty<string>()
        };
    }

    private static string ExtractVersionFromText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var match = Regex.Match(value, "(\\d+\\.\\d+(?:\\.\\d+)*)");
        return match.Success ? match.Value : value;
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _downloadHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var targetStream = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long totalRead = 0;
        while (true)
        {
            var read = await sourceStream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await targetStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                var percent = 5 + (totalRead * 65.0 / totalBytes.Value);
                ReportProgress(progress, kind, percent, $"Downloading installer... {totalRead / 1024 / 1024} MB");
            }
        }
    }

    private async Task<IReadOnlyList<PythonInstallation>> DiscoverPythonInstallationsAsync(
        EnvironmentReadinessReport? readiness,
        CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PythonInstallation>();

        var pythonProbe = readiness?.Tools.FirstOrDefault(static tool => tool.Kind == RegisteredToolKind.Python);
        if (!string.IsNullOrWhiteSpace(pythonProbe?.ExecutablePath))
        {
            candidates.Add(pythonProbe.ExecutablePath);
        }

        foreach (var root in EnumeratePathRoots())
        {
            var candidatePath = Path.Combine(root, "python.exe");
            if (File.Exists(candidatePath))
            {
                candidates.Add(candidatePath);
            }
        }

        candidates.UnionWith(PythonRuntimeCompatibility.EnumerateEnvironmentPythonExecutablePaths());
        candidates.UnionWith(PythonRuntimeCompatibility.EnumerateKnownPythonExecutablePaths());

        foreach (var candidate in candidates)
        {
            var installation = await ProbePythonInstallationAsync(candidate, cancellationToken);
            if (installation is not null)
            {
                results.Add(installation);
            }
        }

        return results;
    }

    private async Task<PythonInstallation?> ProbePythonInstallationAsync(string pythonPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunProcessAsync(
                pythonPath,
                ["-c", PythonRuntimeCompatibility.BuildProbeScript()],
                cancellationToken,
                timeoutMs: 20_000);
            var lines = SplitLines(result.Output);
            if (result.ExitCode != 0
                || lines.Count < 3
                || !Version.TryParse(lines[0], out var version)
                || !int.TryParse(lines[2], out var pointerSize))
            {
                return null;
            }

            var executablePath = lines.Count > 1 && File.Exists(lines[1])
                ? Path.GetFullPath(lines[1])
                : Path.GetFullPath(pythonPath);
            return new PythonInstallation(version, executablePath, pointerSize == 64);
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> GetLatestPyPiVersionsAsync(CancellationToken cancellationToken)
    {
        var packageNames = new[] { "vapoursynth", "vsrepo", "awsmfunc", "vsjetpack" };
        var tasks = packageNames.ToDictionary(
            static packageName => packageName,
            packageName => GetLatestPyPiVersionAsync(packageName, cancellationToken));

        await Task.WhenAll(tasks.Values);

        return tasks.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Result,
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> GetLatestPyPiVersionAsync(string packageName, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _apiHttpClient.GetAsync($"https://pypi.org/pypi/{packageName}/json", cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<PyPiPackageResponse>(stream, JsonOptions, cancellationToken);
            return payload?.Info?.Version ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<IReadOnlyList<EncoderUpdatePackage>> SafeGetEncoderUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _encoderUpdateService.GetAvailableUpdatesAsync(cancellationToken);
        }
        catch
        {
            return Array.Empty<EncoderUpdatePackage>();
        }
    }

    private async Task<IReadOnlyList<ExternalToolUpdatePackage>> SafeGetToolUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _externalToolService.GetAvailableUpdatesAsync(cancellationToken);
        }
        catch
        {
            return Array.Empty<ExternalToolUpdatePackage>();
        }
    }

    private async Task<string> QueryPythonDistributionVersionAsync(
        string? pythonPath,
        string distributionName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
        {
            return string.Empty;
        }

        var escapedName = distributionName.Replace("\\", "\\\\").Replace("'", "\\'");
        var script = string.Join(
            "\n",
            "import importlib.metadata, sys",
            $"name = '{escapedName}'",
            "try:",
            "    print(importlib.metadata.version(name))",
            "except importlib.metadata.PackageNotFoundError:",
            "    sys.exit(3)");

        try
        {
            var result = await RunProcessAsync(
                pythonPath,
                ["-c", script],
                cancellationToken,
                timeoutMs: 20_000);
            return result.ExitCode == 0
                ? FirstMeaningfulLine(result.Output)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<IReadOnlyDictionary<string, VsrepoInstalledPackage>> QueryVsrepoInstalledPackagesAsync(
        string? pythonPath,
        bool refreshPackageDefinitions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
        {
            return new Dictionary<string, VsrepoInstalledPackage>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await EnsureVsrepoUserSiteDirectoryAsync(pythonPath, cancellationToken);
            if (refreshPackageDefinitions)
            {
                await TryUpdateVsrepoPackageDefinitionsAsync(pythonPath, cancellationToken);
            }

            var result = await RunProcessAsync(
                pythonPath,
                ["-m", "vsrepo.vsrepo", "-t", GetVsrepoTarget(), "installed"],
                cancellationToken,
                timeoutMs: 30_000);
            if (result.ExitCode != 0)
            {
                return new Dictionary<string, VsrepoInstalledPackage>(StringComparer.OrdinalIgnoreCase);
            }

            return ParseVsrepoInstalledPackages(result.Output);
        }
        catch
        {
            return new Dictionary<string, VsrepoInstalledPackage>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task TryUpdateVsrepoPackageDefinitionsAsync(
        string pythonPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureVsrepoUserSiteDirectoryAsync(pythonPath, cancellationToken);
            await RunProcessAsync(
                pythonPath,
                ["-m", "vsrepo.vsrepo", "-t", GetVsrepoTarget(), "update"],
                cancellationToken,
                timeoutMs: 120_000);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Write(
                _paths,
                nameof(SetupBootstrapService),
                $"Failed to update vsrepo package definitions. {ex.GetType().Name}: {ex.Message}",
                AppDiagnosticSeverity.Warning,
                exception: ex);
        }
    }

    private async Task EnsureVsrepoUserSiteDirectoryAsync(
        string pythonPath,
        CancellationToken cancellationToken)
    {
        const string script = "import os, site\nos.makedirs(site.getusersitepackages(), exist_ok=True)";
        await RunProcessAsync(
            pythonPath,
            ["-c", script],
            cancellationToken,
            timeoutMs: 20_000);
    }

    private async Task EnsureVsrepoArchiveExtractorAsync(
        string pythonPath,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var vsrepoDirectory = await GetVsrepoPackageDirectoryAsync(pythonPath, cancellationToken);
        var bundledExtractorPath = Path.Combine(vsrepoDirectory, "7z.exe");
        if (File.Exists(bundledExtractorPath))
        {
            return;
        }

        var extractorPath = Find7ZipExecutablePath();
        if (string.IsNullOrWhiteSpace(extractorPath))
        {
            ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 2, "Installing 7-Zip prerequisite...");
            try
            {
                await RunProcessAsync(
                    "winget.exe",
                    ["install", "--id", "7zip.7zip", "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements"],
                    cancellationToken,
                    timeoutMs: 600_000,
                    onOutput: line => ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 2, line),
                    onError: line => ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 2, line));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "VS plugin installation requires 7-Zip. FlowEncode could not install it automatically.",
                    ex);
            }

            extractorPath = Find7ZipExecutablePath();
        }

        if (string.IsNullOrWhiteSpace(extractorPath))
        {
            throw new InvalidOperationException(
                "VS plugin installation requires 7-Zip. Install 7-Zip and try again.");
        }

        File.Copy(extractorPath, bundledExtractorPath, overwrite: true);
        var extractorLibraryPath = Path.Combine(Path.GetDirectoryName(extractorPath)!, "7z.dll");
        if (File.Exists(extractorLibraryPath))
        {
            File.Copy(extractorLibraryPath, Path.Combine(vsrepoDirectory, "7z.dll"), overwrite: true);
        }
    }

    private async Task<string> GetVsrepoPackageDirectoryAsync(
        string pythonPath,
        CancellationToken cancellationToken)
    {
        const string script = "import os, vsrepo\nprint(os.path.dirname(vsrepo.__file__))";
        var result = await RunProcessAsync(
            pythonPath,
            ["-c", script],
            cancellationToken,
            timeoutMs: 20_000);
        var directoryPath = FirstMeaningfulLine(result.Output);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            throw new InvalidOperationException("The installed vsrepo package directory could not be located.");
        }

        return directoryPath;
    }

    private static string? Find7ZipExecutablePath()
    {
        var registryPath = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\7-Zip", "Path", null) as string;
        var candidates = new[]
        {
            registryPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "7-Zip", "7z.exe")
        }
        .Where(static path => !string.IsNullOrWhiteSpace(path))
        .Select(static path => path!.EndsWith("7z.exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.Combine(path, "7z.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IReadOnlyDictionary<string, VsrepoInstalledPackage> ParseVsrepoInstalledPackages(string output)
    {
        var result = new Dictionary<string, VsrepoInstalledPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in SplitLines(output))
        {
            if (string.IsNullOrWhiteSpace(rawLine)
                || rawLine.StartsWith("Name", StringComparison.OrdinalIgnoreCase)
                || rawLine.All(static character => character == '-'))
            {
                continue;
            }

            if (!TryParseVsrepoInstalledLine(rawLine, out var package))
            {
                continue;
            }

            result[package.Name] = package;
            result[package.Namespace] = package;
            result[package.Identifier] = package;
        }

        return result;
    }

    private static bool TryParseVsrepoInstalledLine(string rawLine, out VsrepoInstalledPackage package)
    {
        var parts = Regex.Split(rawLine.Trim(), "\\s{2,}");
        if (parts.Length < 3)
        {
            package = default!;
            return false;
        }

        var name = parts[0].Trim().TrimStart('*', '+');
        var packageNamespace = parts[1].Trim();
        string installedVersion;
        string latestVersion;
        string identifier;

        if (parts.Length >= 5)
        {
            installedVersion = parts[2].Trim();
            latestVersion = parts[3].Trim();
            identifier = parts[4].Trim();
        }
        else
        {
            var tailTokens = parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tailTokens.Length < 3)
            {
                package = default!;
                return false;
            }

            installedVersion = string.Join(" ", tailTokens.Take(Math.Max(1, tailTokens.Length - 2)));
            latestVersion = tailTokens[^2].Trim();
            identifier = tailTokens[^1].Trim();
        }

        package = new VsrepoInstalledPackage(name, packageNamespace, installedVersion, latestVersion, identifier);
        return true;
    }

    private static ToolProbeResult GetToolResult(EnvironmentReadinessReport readiness, RegisteredToolKind kind)
    {
        return readiness.Tools.FirstOrDefault(result => result.Kind == kind)
            ?? new ToolProbeResult(kind, ReadinessState.Unknown, ToolDetectionSource.None, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static CapabilityReadiness GetCapabilityReadiness(EnvironmentReadinessReport readiness, EnvironmentCapabilityKind kind)
    {
        return readiness.Capabilities.FirstOrDefault(result => result.Kind == kind)
            ?? new CapabilityReadiness(kind, ReadinessState.Unknown, Array.Empty<CapabilityRequirementReadiness>());
    }

    private static ReadinessState ResolveCompositeState(params ToolProbeResult[] results)
    {
        var requirements = results
            .Select(result => new CapabilityRequirementReadiness(
                new CapabilityToolRequirement(result.Kind),
                [result]))
            .ToArray();

        return ReadinessStateResolver.ResolveFromRequirements(requirements);
    }

    private static string BuildToolDetail(ToolProbeResult result)
    {
        return result.State switch
        {
            ReadinessState.Ready when !string.IsNullOrWhiteSpace(result.DetectedVersion) =>
                $"{GetSourceLabel(result)} | {result.DetectedVersion}",
            ReadinessState.Ready => GetSourceLabel(result),
            ReadinessState.Misconfigured when !string.IsNullOrWhiteSpace(result.FailureReason) => result.FailureReason,
            ReadinessState.Missing => "Missing",
            ReadinessState.Unknown when !string.IsNullOrWhiteSpace(result.FailureReason) => result.FailureReason,
            _ => "Not detected"
        };
    }

    private static string GetSourceLabel(ToolProbeResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.SourceLabel))
        {
            return result.SourceLabel;
        }

        return result.Source.ToString();
    }

    private static bool AreVersionsComparableAndDifferent(string installedVersion, string latestVersion)
    {
        var installed = ExtractComparableVersion(installedVersion);
        var latest = ExtractComparableVersion(latestVersion);
        return installed is not null && latest is not null && installed < latest;
    }

    internal static bool HasSetupDependencyVersionUpdate(
        SetupDependencyKind kind,
        string installedVersion,
        string latestVersion)
    {
        if (kind is SetupDependencyKind.X264 or SetupDependencyKind.X265 or SetupDependencyKind.SvtAv1
            && TryResolveEncoderVersionUpdate(installedVersion, latestVersion, out var encoderUpdateAvailable))
        {
            return encoderUpdateAvailable;
        }

        if (kind == SetupDependencyKind.FfmpegBundle
            && TryExtractFfmpegBuildDate(installedVersion, out var installedBuildDate)
            && TryExtractFfmpegBuildDate(latestVersion, out var latestBuildDate))
        {
            return installedBuildDate.Date < latestBuildDate.Date;
        }

        return AreVersionsComparableAndDifferent(installedVersion, latestVersion);
    }

    private static bool TryResolveEncoderVersionUpdate(
        string installedVersion,
        string latestVersion,
        out bool updateAvailable)
    {
        updateAvailable = false;

        if (string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(latestVersion))
        {
            return false;
        }

        var installedRevision = ExtractGitRevision(installedVersion);
        var latestRevision = ExtractGitRevision(latestVersion);
        if (installedRevision is not null && latestRevision is not null)
        {
            if (RevisionsMatch(installedRevision, latestRevision))
            {
                updateAvailable = false;
                return true;
            }

            if (TryExtractReleaseDate(installedVersion, out var installedDate)
                && TryExtractReleaseDate(latestVersion, out var latestDate))
            {
                updateAvailable = installedDate.Date < latestDate.Date;
                return true;
            }

            updateAvailable = true;
            return true;
        }

        return false;
    }

    private static string? ExtractGitRevision(string value)
    {
        var matches = Regex.Matches(value, @"(?<![A-Za-z0-9])[a-f0-9]{7,40}(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var revision = match.Value;
            if (!Regex.IsMatch(revision, "[a-f]", RegexOptions.IgnoreCase))
            {
                continue;
            }

            return revision.ToLowerInvariant();
        }

        return null;
    }

    private static bool RevisionsMatch(string left, string right)
    {
        var shortestLength = Math.Min(left.Length, right.Length);
        if (shortestLength < 7)
        {
            return false;
        }

        return left.AsSpan(0, shortestLength).SequenceEqual(right.AsSpan(0, shortestLength));
    }

    private static bool TryExtractReleaseDate(string value, out DateTime releaseDate)
    {
        return TryExtractFfmpegBuildDate(value, out releaseDate);
    }

    private static Version? ExtractComparableVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Try semver-style extraction on the original string first (e.g. "4.1.0", "SVT-AV1 Encoder Lib v4.1.0").
        // This must happen before NormalizeVapourSynthVersion because that function collapses
        // "4.1.0" to "R4" (first standalone digit), losing minor/patch information.
        var versionMatch = Regex.Match(value, "(\\d+\\.\\d+(?:\\.\\d+)*)");
        if (versionMatch.Success && Version.TryParse(versionMatch.Value, out var parsedVersion))
        {
            return parsedVersion;
        }

        // Fall back to VapourSynth R-style versioning (e.g. "R70", "VapourSynth R70").
        var normalized = NormalizeVapourSynthVersion(value);
        var releaseMatch = Regex.Match(normalized, "R(\\d+)", RegexOptions.IgnoreCase);
        if (releaseMatch.Success && int.TryParse(releaseMatch.Groups[1].Value, out var releaseNumber))
        {
            return new Version(releaseNumber, 0);
        }

        return null;
    }

    private static string NormalizeVapourSynthVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var releaseMatch = Regex.Match(value, "R(\\d+)", RegexOptions.IgnoreCase);
        if (releaseMatch.Success)
        {
            return $"R{releaseMatch.Groups[1].Value}";
        }

        var numericMatch = Regex.Match(value, "\\b(\\d+)\\b");
        if (numericMatch.Success)
        {
            return $"R{numericMatch.Groups[1].Value}";
        }

        return value.Trim();
    }

    private static bool TryExtractFfmpegBuildDate(string value, out DateTime buildDate)
    {
        buildDate = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compactMatch = Regex.Match(value, "(?<!\\d)(20\\d{6})(?!\\d)");
        if (compactMatch.Success
            && DateTime.TryParseExact(
                compactMatch.Groups[1].Value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out buildDate))
        {
            return true;
        }

        var dashedMatch = Regex.Match(value, "(?<!\\d)(20\\d{2})[-/.](\\d{2})[-/.](\\d{2})(?!\\d)");
        if (!dashedMatch.Success)
        {
            return false;
        }

        var normalized = string.Concat(
            dashedMatch.Groups[1].Value,
            dashedMatch.Groups[2].Value,
            dashedMatch.Groups[3].Value);

        return DateTime.TryParseExact(
            normalized,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out buildDate);
    }

    private static double MapPipProgress(string line, double finishPercent)
    {
        if (line.Contains("Collecting", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(finishPercent, 24);
        }

        if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase) || line.Contains("Installing build dependencies", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(finishPercent, 48);
        }

        if (line.Contains("Building wheel", StringComparison.OrdinalIgnoreCase) || line.Contains("Preparing metadata", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(finishPercent, 64);
        }

        if (line.Contains("Installing collected packages", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(finishPercent, 78);
        }

        if (line.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Min(finishPercent, 95);
        }

        return Math.Min(finishPercent, 18);
    }

    private static void ReportProgress(
        IProgress<SetupInstallProgress>? progress,
        SetupDependencyKind kind,
        double percent,
        string statusText,
        bool isIndeterminate = false)
    {
        progress?.Report(new SetupInstallProgress(kind, Math.Clamp(percent, 0, 100), statusText, isIndeterminate));
    }

    private static string GetVsrepoTarget()
    {
        return Environment.Is64BitOperatingSystem ? "win64" : "win32";
    }

    private static string GetValueOrEmpty(IReadOnlyDictionary<string, string> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : string.Empty;
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
                if (seen.Add(root) && Directory.Exists(root))
                {
                    yield return root;
                }
            }
        }
    }

    private async Task<ProcessExecutionResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        int timeoutMs,
        Action<string>? onOutput = null,
        Action<string>? onError = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        VapourSynthRuntimePathResolver.EnrichProcessPath(process.StartInfo);

        var outputLines = new ConcurrentQueue<string>();
        var errorLines = new ConcurrentQueue<string>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        using var registration = timeoutCts.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to terminate setup bootstrap process after cancellation. {ex}");
            }
        });

        process.Start();

        var stdoutTask = PumpReaderAsync(process.StandardOutput, outputLines, onOutput, timeoutCts.Token);
        var stderrTask = PumpReaderAsync(process.StandardError, errorLines, onError, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Command timed out: {fileName}");
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
        {
            var combinedOutput = new[]
            {
                string.Join(Environment.NewLine, outputLines),
                string.Join(Environment.NewLine, errorLines)
            };

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(string.Join(Environment.NewLine, outputLines))
                && string.IsNullOrWhiteSpace(string.Join(Environment.NewLine, errorLines))
                ? $"Command failed with exit code {process.ExitCode}: {fileName}"
                : string.Join(Environment.NewLine, combinedOutput.Where(static part => !string.IsNullOrWhiteSpace(part))));
        }

        return new ProcessExecutionResult(
            process.ExitCode,
            string.Join(Environment.NewLine, outputLines),
            string.Join(Environment.NewLine, errorLines));
    }

    private static async Task PumpReaderAsync(
        StreamReader reader,
        ConcurrentQueue<string> sink,
        Action<string>? callback,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            sink.Enqueue(line);
            callback?.Invoke(line);
        }
    }

    private static string FirstMeaningfulLine(string output)
    {
        return SplitLines(output).FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
    }

    private static List<string> SplitLines(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private sealed record PythonInstallation(Version Version, string ExecutablePath, bool Is64Bit)
    {
        public string VersionText => $"{Version} {(Is64Bit ? "x64" : "x86")}";
    }

    private sealed record SetupStatusLocalContext(
        PythonInstallation? TargetPython,
        PythonInstallation? HighestPython,
        bool CompatiblePythonReady,
        IReadOnlyDictionary<string, VsrepoInstalledPackage> VsrepoPackages,
        string AwsmfuncVersion,
        string VsjetpackVersion);

    private sealed record PythonUninstallEntry(
        string DisplayName,
        Version Version,
        string UninstallString,
        string? QuietUninstallString);

    private sealed record VsrepoInstalledPackage(
        string Name,
        string Namespace,
        string InstalledVersion,
        string LatestVersion,
        string Identifier);

    private sealed record SilentCommand(string FileName, IReadOnlyList<string> Arguments);

    private sealed record ProcessExecutionResult(int ExitCode, string Output, string Error)
    {
        public string CombinedOutput => string.Join(Environment.NewLine, new[] { Output, Error }.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private sealed record PyPiPackageResponse(PyPiPackageInfo? Info);

    private sealed record PyPiPackageInfo(string? Version);
}

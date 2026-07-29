using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;
using Microsoft.Win32;
using SharpCompress.Archives.SevenZip;

namespace FlowEncode.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class SetupBootstrapService : ISetupBootstrapService, IDisposable
{
    private const string PythonReleasesApiUrl = "https://www.python.org/api/v2/downloads/release/?is_published=true";
    private const string PythonReleaseFilesApiUrl = "https://www.python.org/api/v2/downloads/release_file/?release=";
    private static readonly PythonInstallerPackage PythonFallbackInstallerPackage = new(
        new Version(3, 12, 10),
        "python-3.12.10-amd64.exe",
        "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe",
        "67b5635e80ea51072b87941312d00ec8927c4db9ba18938f7ad2d27b328b95fb",
        "https://www.python.org/downloads/release/python-31210/");
    private const string VapourSynthReleaseUrl = "https://www.vapoursynth.com/doc/installation.html";
    private const string Portable7ZipFileName = "7z2602-extra.7z";
    private const string Portable7ZipDownloadUrl = "https://github.com/ip7z/7zip/releases/download/26.02/7z2602-extra.7z";
    private const string Portable7ZipSha256 = "081df9e9311dfd9c9e0e98c1c80180b99bb51e4cb24156b5f3057fe3c259d70a";
    private const string Portable7ZipExecutableSha256 = "35d4d69d7cd6cb44558f208c3b1334268013f9daf82d2dda848893a1c30c59c2";
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
    private readonly CliEnvironmentIntegrationService _cliEnvironmentIntegrationService;
    private readonly HttpClient _apiHttpClient;
    private readonly HttpClient _downloadHttpClient;
    private PythonInstallerPackage _latestPythonInstallerPackage = PythonFallbackInstallerPackage;

    public SetupBootstrapService(
        LocalAppPaths paths,
        IEncoderUpdateService encoderUpdateService,
        IEncoderToolchainService toolchainService,
        IExternalToolService externalToolService,
        CliEnvironmentIntegrationService cliEnvironmentIntegrationService,
        IFlowEncodeHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _paths = paths;
        _encoderUpdateService = encoderUpdateService;
        _toolchainService = toolchainService;
        _externalToolService = externalToolService;
        _cliEnvironmentIntegrationService = cliEnvironmentIntegrationService;
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
            .OrderByDescending(static item => PythonRuntimeCompatibility.IsSupportedRuntime(item.Version, item.Is64Bit))
            .ThenByDescending(static item => item.Version)
            .ToList();
        var pythonPath = installations.FirstOrDefault()?.ExecutablePath;
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            return;
        }

        await TryUpdateVsrepoPackageDefinitionsAsync(pythonPath, cancellationToken);
    }

    public async Task InstallAsync(
        SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await (kind switch
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
        });

        string? pythonPath = null;
        if (kind is SetupDependencyKind.Python312
            or SetupDependencyKind.VapourSynth
            or SetupDependencyKind.Vsrepo
            or SetupDependencyKind.VsPluginBundle
            or SetupDependencyKind.Awsmfunc
            or SetupDependencyKind.Vsjetpack)
        {
            pythonPath = await ResolvePreferredPythonAsync(cancellationToken);
        }

        _cliEnvironmentIntegrationService.Synchronize(pythonPath);
        await _cliEnvironmentIntegrationService.VerifyCliEnvironmentAsync(kind, pythonPath, cancellationToken);
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

    public bool CanUninstall(SetupDependencyKind kind)
    {
        var componentKey = kind switch
        {
            SetupDependencyKind.Python312 => CliEnvironmentIntegrationService.GetPythonComponentKey(),
            SetupDependencyKind.VapourSynth => CliEnvironmentIntegrationService.GetPythonPackageComponentKey("vapoursynth"),
            SetupDependencyKind.Vsrepo => CliEnvironmentIntegrationService.GetPythonPackageComponentKey("vsrepo"),
            SetupDependencyKind.VsPluginBundle => CliEnvironmentIntegrationService.GetVsPluginBundleComponentKey(),
            SetupDependencyKind.Awsmfunc => CliEnvironmentIntegrationService.GetPythonPackageComponentKey("awsmfunc"),
            SetupDependencyKind.Vsjetpack => CliEnvironmentIntegrationService.GetPythonPackageComponentKey("vsjetpack"),
            _ => null
        };

        return componentKey is null
            || _cliEnvironmentIntegrationService.GetComponentOwnership(componentKey)?.OwnsComponent == true;
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
        var pythonInstallerPackageTask = SafeGetLatestPythonInstallerPackageAsync(cancellationToken);
        var encoderUpdatesTask = SafeGetEncoderUpdatesAsync(cancellationToken);
        var toolUpdatesTask = SafeGetToolUpdatesAsync(cancellationToken);

        await Task.WhenAll(localContextTask, pypiVersionsTask, pythonInstallerPackageTask, encoderUpdatesTask, toolUpdatesTask);

        return BuildStatusReport(
            readiness,
            localContextTask.Result,
            pypiVersionsTask.Result,
            pythonInstallerPackageTask.Result,
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
            _latestPythonInstallerPackage,
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
        var targetPython = pythonInstallations.FirstOrDefault(static item => PythonRuntimeCompatibility.IsSupportedRuntime(item.Version, item.Is64Bit));
        var compatiblePython = targetPython;
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
        PythonInstallerPackage pythonInstallerPackage,
        IReadOnlyList<EncoderUpdatePackage> encoderUpdates,
        IReadOnlyList<ExternalToolUpdatePackage> toolUpdates,
        DateTimeOffset checkedAt)
    {
        var dependencies = new List<SetupDependencyStatus>
        {
            BuildPythonStatus(readiness, localContext.TargetPython, localContext.HighestPython, pythonInstallerPackage),
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
                    ReleaseUrl = mergedReleaseUrl,
                    IsInstallSupported = previousStatus.IsInstallSupported,
                    IsInstallEnabled = previousStatus.IsInstallEnabled
                };
            })
            .ToList();

        return localReport with
        {
            Dependencies = mergedDependencies
        };
    }

    private SetupDependencyStatus BuildPythonStatus(
        EnvironmentReadinessReport readiness,
        PythonInstallation? targetPython,
        PythonInstallation? highestPython,
        PythonInstallerPackage installerPackage)
    {
        if (targetPython is not null)
        {
            var pip = GetToolResult(readiness, RegisteredToolKind.Pip);
            var state = pip.State switch
            {
                ReadinessState.Ready => ReadinessState.Ready,
                ReadinessState.Misconfigured => ReadinessState.Misconfigured,
                _ => ReadinessState.Partial
            };
            return new SetupDependencyStatus(
                SetupDependencyKind.Python312,
                state,
                targetPython.VersionText,
                installerPackage.Version.ToString(),
                targetPython.Version < installerPackage.Version,
                targetPython.ExecutablePath,
                installerPackage.ReleaseUrl,
                true,
                true,
                string.Join(
                    Environment.NewLine,
                    [targetPython.ExecutablePath, $"pip: {BuildToolDetail(pip)}"]));
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
                installerPackage.Version.ToString(),
                highestPython.Version < installerPackage.Version,
                highestPython.ExecutablePath,
                installerPackage.ReleaseUrl,
                true,
                true,
                highestPython.ExecutablePath);
        }

        return new SetupDependencyStatus(
            SetupDependencyKind.Python312,
            ReadinessState.Missing,
            string.Empty,
            installerPackage.Version.ToString(),
            false,
            string.Empty,
            installerPackage.ReleaseUrl,
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
        var cliProbe = GetToolResult(readiness, RegisteredToolKind.VsrepoCli);
        var latestVersion = GetValueOrEmpty(pypiVersions, "vsrepo");
        var state = ResolveModuleAndCliReadiness(probe.State, cliProbe.State);

        return new SetupDependencyStatus(
            SetupDependencyKind.Vsrepo,
            state,
            probe.State == ReadinessState.Ready ? probe.DetectedVersion : string.Empty,
            latestVersion,
            AreVersionsComparableAndDifferent(probe.DetectedVersion, latestVersion),
            string.IsNullOrWhiteSpace(cliProbe.ExecutablePath) ? probe.ExecutablePath : cliProbe.ExecutablePath,
            probe.ReleaseUrl,
            true,
            targetPythonReady,
            string.Join(
                Environment.NewLine,
                [$"module: {BuildToolDetail(probe)}", $"CLI: {BuildToolDetail(cliProbe)}"]));
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
            string.Empty,
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
        var automaticInstallEnabled = latestPackage?.IsAutomatic == true;
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
            automaticInstallEnabled,
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
        var automaticInstallEnabled = latestPackage?.IsAutomatic == true;

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
            automaticInstallEnabled,
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
        var automaticInstallEnabled = latestPackage?.IsAutomatic == true;
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
            automaticInstallEnabled,
            detail,
            probe.IsProtocolCompatible,
            probe.ProtocolVersion,
            compatibilityDetail);
    }

    private async Task InstallPythonAsync(
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installationsBeforeInstall = await DiscoverPythonInstallationsAsync(null, cancellationToken);
        var package = await SafeGetLatestPythonInstallerPackageAsync(cancellationToken);
        Directory.CreateDirectory(_paths.DownloadsRootPath);
        var installerPath = Path.Combine(_paths.DownloadsRootPath, package.FileName);

        ReportProgress(progress, SetupDependencyKind.Python312, 5, $"Preparing Python {package.Version} installer...");
        await DownloadFileAsync(package.DownloadUrl, installerPath, SetupDependencyKind.Python312, progress, cancellationToken);

        ReportProgress(progress, SetupDependencyKind.Python312, 72, "Verifying Python installer integrity...");
        if (!string.IsNullOrWhiteSpace(package.Sha256))
        {
            await PackageIntegrityVerifier.VerifySha256Async(
                installerPath,
                package.Sha256,
                cancellationToken,
                "Python 安装器");
        }

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

        ReportProgress(progress, SetupDependencyKind.Python312, 92, $"Verifying Python {package.Version}...");
        var installations = await DiscoverPythonInstallationsAsync(null, cancellationToken);
        var installedPython = installations
            .Where(static item => item.Is64Bit)
            .OrderByDescending(static item => item.Version)
            .FirstOrDefault(item => item.Version >= package.Version);
        if (installedPython is null)
        {
            throw new InvalidOperationException($"Python {package.Version} x64 installation finished, but the interpreter could not be detected afterwards.");
        }

        await EnsurePipCliLauncherAsync(installedPython.ExecutablePath, cancellationToken);
        var wasPreexisting = installationsBeforeInstall.Any(item =>
            string.Equals(item.ExecutablePath, installedPython.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        _cliEnvironmentIntegrationService.RecordComponentOwnership(
            CliEnvironmentIntegrationService.GetPythonComponentKey(),
            ownsComponent: !wasPreexisting,
            installedPython.ExecutablePath,
            installedPython.Version.ToString(),
            [installerPath]);
        ReportProgress(progress, SetupDependencyKind.Python312, 100, $"Python {installedPython.Version} is ready.");
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

        await EnsureVspipeCliLauncherAsync(pythonPath, cancellationToken);

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
        var vsrepoVersionBeforeInstall = await QueryPythonDistributionVersionAsync(
            pythonPath,
            "vsrepo",
            cancellationToken);

        await EnsurePipCliLauncherAsync(pythonPath, cancellationToken);
        await EnsureVsrepoCliLauncherAsync(pythonPath, cancellationToken);
        var vsrepoVersionAfterInstall = await QueryPythonDistributionVersionAsync(
            pythonPath,
            "vsrepo",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(vsrepoVersionAfterInstall))
        {
            throw new InvalidOperationException("vsrepo launcher repair completed, but the Python distribution could not be verified.");
        }

        _cliEnvironmentIntegrationService.RecordComponentOwnership(
            CliEnvironmentIntegrationService.GetPythonPackageComponentKey("vsrepo"),
            ownsComponent: string.IsNullOrWhiteSpace(vsrepoVersionBeforeInstall),
            pythonPath,
            vsrepoVersionAfterInstall,
            ["vsrepo"]);
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

        var packagesBeforeUpdate = await QueryVsrepoInstalledPackagesAsync(
            pythonPath,
            refreshPackageDefinitions: false,
            cancellationToken);
        var packagePlan = BuildVsPluginBundlePackagePlan(
            VsrepoPackageNames.Where(packageName =>
                packagesBeforeUpdate.TryGetValue(packageName, out var package)
                && !string.IsNullOrWhiteSpace(package.InstalledVersion)));
        var packageOperations = new (string Operation, IReadOnlyList<string> Packages)[]
        {
            ("install", packagePlan.InstallPackages),
            ("upgrade", packagePlan.UpgradePackages)
        };

        var operationIndex = 0;
        foreach (var (operation, packages) in packageOperations.Where(static item => item.Packages.Count > 0))
        {
            var operationStartPercent = 20 + (operationIndex * 35);
            ReportProgress(
                progress,
                SetupDependencyKind.VsPluginBundle,
                operationStartPercent,
                $"Running vsrepo {operation} {string.Join(" ", packages)}...");

            await RunProcessAsync(
                pythonPath,
                ["-m", "vsrepo.vsrepo", "-t", GetVsrepoTarget(), operation, .. packages],
                cancellationToken,
                timeoutMs: 600_000,
                onOutput: line => ReportProgress(
                    progress,
                    SetupDependencyKind.VsPluginBundle,
                    operationStartPercent + 25,
                    line),
                onError: line => ReportProgress(
                    progress,
                    SetupDependencyKind.VsPluginBundle,
                    operationStartPercent + 10,
                    line));

            operationIndex++;
        }

        var installedPackages = await QueryVsrepoInstalledPackagesAsync(
            pythonPath,
            refreshPackageDefinitions: false,
            cancellationToken);
        var unresolvedPackages = GetUnresolvedVsPluginBundlePackages(installedPackages);
        if (unresolvedPackages.Count > 0)
        {
            throw new InvalidOperationException(
                $"VS plugin bundle update did not complete: {string.Join(", ", unresolvedPackages)}.");
        }

        _cliEnvironmentIntegrationService.RecordComponentOwnership(
            CliEnvironmentIntegrationService.GetVsPluginBundleComponentKey(),
            ownsComponent: packagePlan.InstallPackages.Count > 0,
            GetVsrepoTarget(),
            string.Join(",", installedPackages.Values.Select(static package => package.InstalledVersion).Distinct()),
            packagePlan.InstallPackages);

        ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 100, "VS plugin bundle is ready.");
    }

    internal static VsPluginBundlePackagePlan BuildVsPluginBundlePackagePlan(
        IEnumerable<string> installedPackageNames)
    {
        var installed = installedPackageNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new VsPluginBundlePackagePlan(
            VsrepoPackageNames.Where(packageName => !installed.Contains(packageName)).ToArray(),
            VsrepoPackageNames.ToArray());
    }

    private static IReadOnlyList<string> GetUnresolvedVsPluginBundlePackages(
        IReadOnlyDictionary<string, VsrepoInstalledPackage> installedPackages)
    {
        return VsrepoPackageNames
            .Where(packageName =>
            {
                if (!installedPackages.TryGetValue(packageName, out var package)
                    || string.IsNullOrWhiteSpace(package.InstalledVersion))
                {
                    return true;
                }

                return !string.IsNullOrWhiteSpace(package.LatestVersion)
                    && !string.Equals(
                        package.InstalledVersion,
                        package.LatestVersion,
                        StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
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
        var downloadProgress = new Progress<PackageDownloadProgress>(download =>
            ReportPackageDownloadProgress(progress, dependencyKind, package.ReleaseName, download));
        var installedPath = await _encoderUpdateService.InstallUpdateAsync(package, cancellationToken, downloadProgress);
        ReportProgress(progress, dependencyKind, 92, "Verifying and deploying encoder files...");
        VerifyInstalledEncoder(package, installedPath);
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
        var downloadProgress = new Progress<PackageDownloadProgress>(download =>
            ReportPackageDownloadProgress(progress, dependencyKind, package.ReleaseName, download));
        var installedPath = await _externalToolService.InstallUpdateAsync(package, cancellationToken, downloadProgress);
        ReportProgress(progress, dependencyKind, 92, "Verifying and deploying tool files...");
        await VerifyInstalledToolAsync(package, installedPath, cancellationToken);
        ReportProgress(progress, dependencyKind, 100, $"{toolKind.ToDisplayName()} is ready.");
    }

    private static void VerifyInstalledEncoder(EncoderUpdatePackage package, string installedPath)
    {
        if (string.IsNullOrWhiteSpace(installedPath) || !File.Exists(installedPath))
        {
            throw new InvalidOperationException($"{package.Kind.ToDisplayName()} update completed, but the managed executable was not found.");
        }

        if (EncoderBinaryProbe.DetectArchitecture(installedPath) != package.Architecture)
        {
            throw new InvalidOperationException($"{package.Kind.ToDisplayName()} update installed an unexpected executable architecture.");
        }

        var detectedVersion = EncoderBinaryProbe.ProbeVersion(installedPath, package.Kind);
        var dependencyKind = package.Kind switch
        {
            EncoderKind.X264 => SetupDependencyKind.X264,
            EncoderKind.X265 => SetupDependencyKind.X265,
            EncoderKind.SvtAv1 => SetupDependencyKind.SvtAv1,
            _ => throw new ArgumentOutOfRangeException(nameof(package), package.Kind, null)
        };
        if (!IsInstalledVersionVerified(dependencyKind, detectedVersion, package.ReleaseName))
        {
            throw new InvalidOperationException(
                $"{package.Kind.ToDisplayName()} update verification failed. Installed: {detectedVersion}; expected: {package.ReleaseName}.");
        }
    }

    private async Task VerifyInstalledToolAsync(
        ExternalToolUpdatePackage package,
        string installedPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installedPath) || !File.Exists(installedPath))
        {
            throw new InvalidOperationException($"{package.Kind.ToDisplayName()} update completed, but the managed executable was not found.");
        }

        var versionArguments = package.Kind == ExternalToolKind.Ffmpeg ? "-version" : "--version";
        var versionResult = await RunProcessAsync(
            installedPath,
            [versionArguments],
            cancellationToken,
            timeoutMs: 20_000);
        var detectedVersion = FirstMeaningfulLine(versionResult.CombinedOutput);
        var dependencyKind = package.Kind == ExternalToolKind.Ffmpeg
            ? SetupDependencyKind.FfmpegBundle
            : SetupDependencyKind.Av1an;
        if (!IsInstalledVersionVerified(dependencyKind, detectedVersion, package.ReleaseName))
        {
            throw new InvalidOperationException(
                $"{package.Kind.ToDisplayName()} update verification failed. Installed: {detectedVersion}; expected: {package.ReleaseName}.");
        }

        if (package.Kind == ExternalToolKind.Ffmpeg)
        {
            var ffprobePath = Path.Combine(Path.GetDirectoryName(installedPath) ?? string.Empty, "ffprobe.exe");
            if (!File.Exists(ffprobePath))
            {
                throw new InvalidOperationException("FFmpeg update completed, but ffprobe.exe was not deployed.");
            }

            await RunProcessAsync(ffprobePath, ["-version"], cancellationToken, timeoutMs: 20_000);
            return;
        }

        var protocolResult = await RunProcessAsync(
            installedPath,
            ["--protocol-version"],
            cancellationToken,
            timeoutMs: 20_000);
        if (string.IsNullOrWhiteSpace(FirstMeaningfulLine(protocolResult.CombinedOutput)))
        {
            throw new InvalidOperationException("Av1an update completed, but protocol compatibility could not be verified.");
        }
    }

    private async Task UninstallPythonAsync(
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var componentKey = CliEnvironmentIntegrationService.GetPythonComponentKey();
        var ownership = RequireOwnedComponent(componentKey, "Python");
        if (!Version.TryParse(ownership.InstalledVersion, out var installedVersion))
        {
            throw new InvalidOperationException("The managed Python version record is invalid; uninstall was refused.");
        }

        ReportProgress(progress, SetupDependencyKind.Python312, 10, $"Locating Python {installedVersion} uninstall entry...");
        var installerPath = ownership.OwnedItems.FirstOrDefault(path =>
            File.Exists(path)
            && Path.GetFileName(path).Contains(installedVersion.ToString(), StringComparison.OrdinalIgnoreCase));
        SilentCommand command;
        if (!string.IsNullOrWhiteSpace(installerPath))
        {
            command = new SilentCommand(installerPath, ["/uninstall", "/quiet"]);
        }
        else
        {
            var uninstallEntry = FindPythonUninstallEntry(installedVersion, ownership.InstallationPath)
                ?? throw new InvalidOperationException("The exact FlowEncode-managed Python uninstall entry was not found; uninstall was refused.");
            command = BuildSilentUninstallCommand(uninstallEntry);
        }

        ReportProgress(progress, SetupDependencyKind.Python312, 35, "Running Python uninstaller...");
        await RunProcessAsync(command.FileName, command.Arguments, cancellationToken, timeoutMs: 600_000);

        ReportProgress(progress, SetupDependencyKind.Python312, 90, $"Verifying Python {installedVersion} removal...");
        if (File.Exists(ownership.InstallationPath))
        {
            throw new InvalidOperationException($"Python {installedVersion} is still present after uninstall completed.");
        }

        _cliEnvironmentIntegrationService.RemoveComponentOwnership(componentKey);
        _cliEnvironmentIntegrationService.Synchronize();
        ReportProgress(progress, SetupDependencyKind.Python312, 100, $"Python {installedVersion} was removed.");
    }

    private async Task UninstallPythonPackageAsync(
        SetupDependencyKind kind,
        string packageName,
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var componentKey = CliEnvironmentIntegrationService.GetPythonPackageComponentKey(packageName);
        var ownership = RequireOwnedComponent(componentKey, packageName);
        var pythonPath = ownership.InstallationPath;
        if (!File.Exists(pythonPath))
        {
            throw new InvalidOperationException($"The Python interpreter recorded for {packageName} no longer exists; uninstall was refused.");
        }

        ReportProgress(progress, kind, 15, $"Uninstalling {packageName}...");
        await RunProcessAsync(
            pythonPath,
            ["-m", "pip", "uninstall", "-y", packageName],
            cancellationToken,
            timeoutMs: 600_000,
            onOutput: line => ReportProgress(progress, kind, 50, line),
            onError: line => ReportProgress(progress, kind, 50, line));
        _cliEnvironmentIntegrationService.RemoveComponentOwnership(componentKey);
        if (packageName.Equals("vsrepo", StringComparison.OrdinalIgnoreCase))
        {
            RemoveOwnedVsrepoExtractor();
        }

        ReportProgress(progress, kind, 100, $"{packageName} was removed.");
    }

    private async Task UninstallVsPluginBundleAsync(
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var componentKey = CliEnvironmentIntegrationService.GetVsPluginBundleComponentKey();
        var ownership = RequireOwnedComponent(componentKey, "VS plugin bundle");
        var packages = ownership.OwnedItems
            .Where(packageName => VsrepoPackageNames.Contains(packageName, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (packages.Length == 0)
        {
            throw new InvalidOperationException("No FlowEncode-owned VS plugin packages were recorded; uninstall was refused.");
        }

        var pythonPath = await ResolveVsrepoPythonAsync(cancellationToken);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await EnsureVsrepoUserSiteDirectoryAsync(pythonPath, cancellationToken);
        ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 10, "Removing VS plugin bundle...");
        await RunProcessAsync(
            pythonPath,
            ["-m", "vsrepo.vsrepo", "-t", GetVsrepoTarget(), "uninstall", .. packages],
            cancellationToken,
            timeoutMs: 600_000,
            onOutput: line =>
            {
                foreach (var packageName in packages)
                {
                    if (!line.Contains(packageName, StringComparison.OrdinalIgnoreCase) || !completed.Add(packageName))
                    {
                        continue;
                    }

                    var percent = 10 + (completed.Count * 80.0 / packages.Length);
                    ReportProgress(progress, SetupDependencyKind.VsPluginBundle, percent, line);
                }
            },
            onError: line => ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 30, line));

        RemoveOwnedVsrepoExtractor();
        _cliEnvironmentIntegrationService.RemoveComponentOwnership(componentKey);
        ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 100, "VS plugin bundle was removed.");
    }

    private async Task<string> ResolveVsrepoPythonAsync(CancellationToken cancellationToken)
    {
        var installations = await DiscoverPythonInstallationsAsync(null, cancellationToken);
        foreach (var installation in installations
                     .Where(static item => PythonRuntimeCompatibility.IsSupportedRuntime(item.Version, item.Is64Bit))
                     .OrderByDescending(static item => item.Version))
        {
            var vsrepoVersion = await QueryPythonDistributionVersionAsync(
                installation.ExecutablePath,
                "vsrepo",
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(vsrepoVersion))
            {
                return installation.ExecutablePath;
            }
        }

        throw new InvalidOperationException(
            "No supported Python interpreter with the vsrepo module was found; plugin uninstall was refused.");
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
        var distributionName = ExtractPythonDistributionName(packageSpec);
        var versionBeforeInstall = await QueryPythonDistributionVersionAsync(
            pythonPath,
            distributionName,
            cancellationToken);
        await EnsurePipCliLauncherAsync(pythonPath, cancellationToken);
        ReportProgress(progress, kind, 10, "Preparing pip install...");
        await RunProcessAsync(
            pythonPath,
            ["-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "--progress-bar", "off", packageSpec],
            cancellationToken,
            timeoutMs: 600_000,
            onOutput: line => ReportProgress(progress, kind, MapPipProgress(line, finishPercent), line),
            onError: line => ReportProgress(progress, kind, MapPipProgress(line, finishPercent), line));

        if (distributionName.Equals("vsrepo", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureVsrepoCliLauncherAsync(pythonPath, cancellationToken);
        }

        var installedVersion = await QueryPythonDistributionVersionAsync(
            pythonPath,
            distributionName,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            throw new InvalidOperationException($"pip completed, but distribution '{distributionName}' could not be verified.");
        }

        _cliEnvironmentIntegrationService.RecordComponentOwnership(
            CliEnvironmentIntegrationService.GetPythonPackageComponentKey(distributionName),
            ownsComponent: string.IsNullOrWhiteSpace(versionBeforeInstall),
            pythonPath,
            installedVersion,
            [distributionName]);
    }

    private static string ExtractPythonDistributionName(string packageSpec)
    {
        var match = Regex.Match(packageSpec, @"^[A-Za-z0-9_.-]+");
        return match.Success
            ? match.Value
            : throw new InvalidOperationException($"Unable to determine the Python distribution name from '{packageSpec}'.");
    }

    private async Task<string> ResolveCompatiblePythonAsync(CancellationToken cancellationToken)
    {
        var installations = await DiscoverPythonInstallationsAsync(null, cancellationToken);
        var target = installations
            .OrderByDescending(static item => item.Version)
            .FirstOrDefault(static item => PythonRuntimeCompatibility.IsSupportedRuntime(item.Version, item.Is64Bit));

        return target?.ExecutablePath
            ?? throw new InvalidOperationException("Python 3.12 or newer x64 was not detected. Install a supported x64 Python first.");
    }

    private async Task<string> ResolvePreferredPythonAsync(CancellationToken cancellationToken)
    {
        var installations = await DiscoverPythonInstallationsAsync(null, cancellationToken);
        var ordered = installations
            .OrderByDescending(static item => item.Version)
            .ToList();
        var target = ordered.FirstOrDefault(static item => PythonRuntimeCompatibility.IsSupportedRuntime(item.Version, item.Is64Bit));
        return target?.ExecutablePath
            ?? ordered.FirstOrDefault()?.ExecutablePath
            ?? throw new InvalidOperationException("No Python interpreter was detected.");
    }

    private PythonUninstallEntry? FindPythonUninstallEntry(Version expectedVersion, string expectedExecutablePath)
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
                if (IsMatchingPythonUninstallEntry(entry, expectedVersion, expectedExecutablePath))
                {
                    return entry;
                }
            }

            using var localMachine = Registry.LocalMachine.OpenSubKey(path);
            foreach (var entry in EnumeratePythonUninstallEntries(localMachine))
            {
                if (IsMatchingPythonUninstallEntry(entry, expectedVersion, expectedExecutablePath))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private static bool IsMatchingPythonUninstallEntry(
        PythonUninstallEntry entry,
        Version expectedVersion,
        string expectedExecutablePath)
    {
        if (entry.Version != expectedVersion)
        {
            return false;
        }

        var expectedDirectory = Path.GetDirectoryName(expectedExecutablePath);
        return !string.IsNullOrWhiteSpace(entry.InstallLocation)
            && !string.IsNullOrWhiteSpace(expectedDirectory)
            && string.Equals(
                Path.GetFullPath(entry.InstallLocation).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(expectedDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
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
                subKey.GetValue("QuietUninstallString") as string,
                subKey.GetValue("InstallLocation") as string);
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

    private ManagedComponentOwnership RequireOwnedComponent(string componentKey, string displayName)
    {
        var ownership = _cliEnvironmentIntegrationService.GetComponentOwnership(componentKey);
        return ownership is { OwnsComponent: true }
            ? ownership
            : throw new InvalidOperationException($"{displayName} was not installed by FlowEncode; uninstall was refused to protect the user's environment.");
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

    private async Task<PythonInstallerPackage> SafeGetLatestPythonInstallerPackageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var package = await GetLatestPythonInstallerPackageAsync(cancellationToken);
            _latestPythonInstallerPackage = package;
            return package;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppDiagnosticsLog.Write(
                _paths,
                nameof(SetupBootstrapService),
                $"Failed to resolve the latest Python Windows installer. {ex.GetType().Name}: {ex.Message}",
                AppDiagnosticSeverity.Warning,
                exception: ex);
            return _latestPythonInstallerPackage;
        }
    }

    private async Task<PythonInstallerPackage> GetLatestPythonInstallerPackageAsync(CancellationToken cancellationToken)
    {
        using var releasesResponse = await _apiHttpClient.GetAsync(PythonReleasesApiUrl, cancellationToken);
        releasesResponse.EnsureSuccessStatusCode();
        await using var releasesStream = await releasesResponse.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<PythonReleaseResponse>>(
                releasesStream,
                JsonOptions,
                cancellationToken)
            ?? [];

        var candidates = releases
            .Where(static release => release.IsPublished && !release.PreRelease)
            .Select(release => new
            {
                Release = release,
                Version = Version.TryParse(ExtractVersionFromText(release.Name), out var version) ? version : null
            })
            .Where(static candidate => IsPythonReleaseEligible(
                candidate.Version,
                candidate.Release.IsPublished,
                candidate.Release.PreRelease))
            .OrderByDescending(static candidate => candidate.Version)
            .ToArray();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var releaseId = ExtractTrailingResourceId(candidate.Release.ResourceUri);
            if (string.IsNullOrWhiteSpace(releaseId))
            {
                continue;
            }

            using var filesResponse = await _apiHttpClient.GetAsync(PythonReleaseFilesApiUrl + releaseId, cancellationToken);
            filesResponse.EnsureSuccessStatusCode();
            await using var filesStream = await filesResponse.Content.ReadAsStreamAsync(cancellationToken);
            var files = await JsonSerializer.DeserializeAsync<List<PythonReleaseFileResponse>>(
                    filesStream,
                    JsonOptions,
                    cancellationToken)
                ?? [];
            var installer = files.FirstOrDefault(static file => IsPythonInstallerFileEligible(
                file.Name,
                file.Url,
                file.Sha256Sum));
            if (installer is null || string.IsNullOrWhiteSpace(installer.Url))
            {
                continue;
            }

            return new PythonInstallerPackage(
                candidate.Version!,
                Path.GetFileName(new Uri(installer.Url).LocalPath),
                installer.Url,
                PackageIntegrityVerifier.NormalizeSha256Digest(installer.Sha256Sum) ?? string.Empty,
                string.IsNullOrWhiteSpace(candidate.Release.Slug)
                    ? "https://www.python.org/downloads/windows/"
                    : $"https://www.python.org/downloads/release/{candidate.Release.Slug}/");
        }

        throw new InvalidOperationException("Python.org did not return a supported Windows x64 installer.");
    }

    internal static bool IsPythonReleaseEligible(Version? version, bool isPublished, bool isPreRelease)
        => version is not null
            && isPublished
            && !isPreRelease
            && PythonRuntimeCompatibility.IsSupportedVersion(version)
            && version.Major == 3;

    internal static bool IsPythonInstallerFileEligible(string name, string url, string? sha256)
        => name.Equals("Windows installer (64-bit)", StringComparison.OrdinalIgnoreCase)
            && url.EndsWith("-amd64.exe", StringComparison.OrdinalIgnoreCase)
            && PackageIntegrityVerifier.NormalizeSha256Digest(sha256) is not null;

    internal static ReadinessState ResolveModuleAndCliReadiness(
        ReadinessState moduleState,
        ReadinessState cliState)
    {
        if (moduleState == ReadinessState.Ready && cliState == ReadinessState.Ready)
        {
            return ReadinessState.Ready;
        }

        if (moduleState == ReadinessState.Misconfigured || cliState == ReadinessState.Misconfigured)
        {
            return ReadinessState.Misconfigured;
        }

        return moduleState == ReadinessState.Ready || cliState == ReadinessState.Ready
            ? ReadinessState.Partial
            : ReadinessState.Missing;
    }

    private static string ExtractTrailingResourceId(string? resourceUri)
    {
        if (string.IsNullOrWhiteSpace(resourceUri))
        {
            return string.Empty;
        }

        return Regex.Match(resourceUri, @"/(\d+)/?$").Groups[1].Value;
    }

    private async Task EnsurePipCliLauncherAsync(string pythonPath, CancellationToken cancellationToken)
    {
        var scriptsDirectory = Path.Combine(Path.GetDirectoryName(pythonPath) ?? string.Empty, "Scripts");
        var pipPath = Path.Combine(scriptsDirectory, "pip.exe");
        if (await IsCliCommandHealthyAsync(pipPath, ["--version"], cancellationToken))
        {
            return;
        }

        await RunProcessAsync(
            pythonPath,
            ["-m", "pip", "install", "--upgrade", "--force-reinstall", "--disable-pip-version-check", "--progress-bar", "off", "pip"],
            cancellationToken,
            timeoutMs: 600_000);
        if (!await IsCliCommandHealthyAsync(pipPath, ["--version"], cancellationToken))
        {
            throw new InvalidOperationException($"pip was installed for '{pythonPath}', but the pip command-line launcher is not usable.");
        }
    }

    private async Task EnsureVsrepoCliLauncherAsync(string pythonPath, CancellationToken cancellationToken)
    {
        var scriptsDirectory = Path.Combine(Path.GetDirectoryName(pythonPath) ?? string.Empty, "Scripts");
        var vsrepoPath = Path.Combine(scriptsDirectory, "vsrepo.exe");
        if (await IsCliCommandHealthyAsync(vsrepoPath, ["installed"], cancellationToken))
        {
            return;
        }

        await RunProcessAsync(
            pythonPath,
            ["-m", "pip", "install", "--upgrade", "--force-reinstall", "--no-deps", "--disable-pip-version-check", "--progress-bar", "off", "vsrepo"],
            cancellationToken,
            timeoutMs: 600_000);
        if (!await IsCliCommandHealthyAsync(vsrepoPath, ["installed"], cancellationToken))
        {
            throw new InvalidOperationException($"vsrepo was installed for '{pythonPath}', but the vsrepo command-line launcher is not usable.");
        }
    }

    private async Task EnsureVspipeCliLauncherAsync(string pythonPath, CancellationToken cancellationToken)
    {
        var scriptsDirectory = Path.Combine(Path.GetDirectoryName(pythonPath) ?? string.Empty, "Scripts");
        var vspipePath = Path.Combine(scriptsDirectory, "vspipe.exe");
        if (await IsCliCommandHealthyAsync(vspipePath, ["--version"], cancellationToken))
        {
            return;
        }

        await RunProcessAsync(
            pythonPath,
            ["-m", "pip", "install", "--upgrade", "--force-reinstall", "--no-deps", "--disable-pip-version-check", "--progress-bar", "off", "vapoursynth"],
            cancellationToken,
            timeoutMs: 600_000);
        await RunProcessAsync(
            pythonPath,
            ["-m", "vapoursynth", "config"],
            cancellationToken,
            timeoutMs: 120_000);
        if (!await IsCliCommandHealthyAsync(vspipePath, ["--version"], cancellationToken))
        {
            throw new InvalidOperationException($"VapourSynth was installed for '{pythonPath}', but the vspipe command-line launcher is not usable.");
        }
    }

    private async Task<bool> IsCliCommandHealthyAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        try
        {
            var result = await RunProcessAsync(executablePath, arguments, cancellationToken, timeoutMs: 30_000);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
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
            if (await IsCliCommandHealthyAsync(bundledExtractorPath, ["i"], cancellationToken))
            {
                return;
            }

            throw new InvalidOperationException(
                $"The existing vsrepo extractor is not usable and was left unchanged: {bundledExtractorPath}");
        }

        var extractorPath = Find7ZipExecutablePath();
        if (string.IsNullOrWhiteSpace(extractorPath))
        {
            extractorPath = await EnsurePortable7ZipAsync(progress, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(extractorPath))
        {
            throw new InvalidOperationException(
                "VS plugin installation requires 7-Zip, and the managed portable extractor could not be prepared.");
        }

        File.Copy(extractorPath, bundledExtractorPath, overwrite: true);
        var ownedExtractorPaths = new List<string> { bundledExtractorPath };
        var extractorLibraryPath = Path.Combine(Path.GetDirectoryName(extractorPath)!, "7z.dll");
        if (File.Exists(extractorLibraryPath))
        {
            var bundledExtractorLibraryPath = Path.Combine(vsrepoDirectory, "7z.dll");
            File.Copy(extractorLibraryPath, bundledExtractorLibraryPath, overwrite: true);
            ownedExtractorPaths.Add(bundledExtractorLibraryPath);
        }

        if (!await IsCliCommandHealthyAsync(bundledExtractorPath, ["i"], cancellationToken))
        {
            foreach (var ownedPath in ownedExtractorPaths)
            {
                if (File.Exists(ownedPath))
                {
                    File.Delete(ownedPath);
                }
            }

            throw new InvalidOperationException("The prepared vsrepo 7-Zip extractor could not be launched.");
        }

        _cliEnvironmentIntegrationService.RecordComponentOwnership(
            CliEnvironmentIntegrationService.GetVsrepoExtractorComponentKey(),
            ownsComponent: true,
            bundledExtractorPath,
            "7-Zip 26.02",
            ownedExtractorPaths);
    }

    private void RemoveOwnedVsrepoExtractor()
    {
        var componentKey = CliEnvironmentIntegrationService.GetVsrepoExtractorComponentKey();
        var ownership = _cliEnvironmentIntegrationService.GetComponentOwnership(componentKey);
        if (ownership is not { OwnsComponent: true })
        {
            return;
        }

        var removedAll = true;
        foreach (var path in ownership.OwnedItems.Where(IsSafeVsrepoExtractorPath))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                removedAll = false;
                AppDiagnosticsLog.Write(
                    _paths,
                    nameof(SetupBootstrapService),
                    $"Failed to remove FlowEncode-owned vsrepo extractor '{path}'. {ex.GetType().Name}: {ex.Message}",
                    AppDiagnosticSeverity.Warning,
                    exception: ex);
            }
        }

        if (removedAll)
        {
            _cliEnvironmentIntegrationService.RemoveComponentOwnership(componentKey);
        }
    }

    internal static bool IsSafeVsrepoExtractorPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        var parentName = Path.GetFileName(Path.GetDirectoryName(path));
        return string.Equals(parentName, "vsrepo", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(fileName, "7z.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "7z.dll", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> EnsurePortable7ZipAsync(
        IProgress<SetupInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var managedDirectory = Path.Combine(_paths.ToolsRootPath, "7zip");
        var managedExecutable = Path.Combine(managedDirectory, "7z.exe");
        if (File.Exists(managedExecutable))
        {
            try
            {
                await PackageIntegrityVerifier.VerifySha256Async(
                    managedExecutable,
                    Portable7ZipExecutableSha256,
                    cancellationToken,
                    "managed 7-Zip executable");
                await RunProcessAsync(managedExecutable, ["i"], cancellationToken, timeoutMs: 20_000);
                return managedExecutable;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var ownershipKey = CliEnvironmentIntegrationService.GetPortable7ZipComponentKey();
                var ownership = _cliEnvironmentIntegrationService.GetComponentOwnership(ownershipKey);
                var ownsManagedExecutable = ownership is { OwnsComponent: true }
                    && string.Equals(
                        Path.GetFullPath(ownership.InstallationPath),
                        Path.GetFullPath(managedExecutable),
                        StringComparison.OrdinalIgnoreCase);
                if (!ownsManagedExecutable)
                {
                    throw new InvalidOperationException(
                        $"The existing portable 7-Zip cache is invalid and was left unchanged: {managedExecutable}",
                        ex);
                }

                AppDiagnosticsLog.Write(
                    _paths,
                    nameof(SetupBootstrapService),
                    $"Replacing an invalid managed 7-Zip cache. {ex.GetType().Name}: {ex.Message}",
                    AppDiagnosticSeverity.Warning,
                    exception: ex);
                Directory.Delete(managedDirectory, recursive: true);
                _cliEnvironmentIntegrationService.RemoveComponentOwnership(ownershipKey);
            }
        }

        Directory.CreateDirectory(_paths.DownloadsRootPath);
        var archivePath = Path.Combine(_paths.DownloadsRootPath, Portable7ZipFileName);
        var extractRoot = Path.Combine(_paths.DownloadsRootPath, $"7zip-{Guid.NewGuid():N}");
        try
        {
            ReportProgress(progress, SetupDependencyKind.VsPluginBundle, 2, "Preparing managed portable 7-Zip x64...");
            await DownloadFileAsync(
                Portable7ZipDownloadUrl,
                archivePath,
                SetupDependencyKind.VsPluginBundle,
                progress,
                cancellationToken);
            await PackageIntegrityVerifier.VerifySha256Async(
                archivePath,
                Portable7ZipSha256,
                cancellationToken,
                "7-Zip portable package");

            Directory.CreateDirectory(extractRoot);
            await ExtractPortable7ZipExecutableAsync(archivePath, extractRoot, cancellationToken);
            var stagedExecutable = Path.Combine(extractRoot, "7z.exe");
            await PackageIntegrityVerifier.VerifySha256Async(
                stagedExecutable,
                Portable7ZipExecutableSha256,
                cancellationToken,
                "7-Zip portable executable");
            await RunProcessAsync(stagedExecutable, ["i"], cancellationToken, timeoutMs: 20_000);
            ManagedDirectoryInstaller.ReplaceDirectoryContents(extractRoot, managedDirectory);
            _cliEnvironmentIntegrationService.RecordComponentOwnership(
                CliEnvironmentIntegrationService.GetPortable7ZipComponentKey(),
                ownsComponent: true,
                managedExecutable,
                "7-Zip 26.02",
                [managedExecutable]);
            return managedExecutable;
        }
        finally
        {
            BestEffortCleanup.DeleteFile(archivePath, "7-Zip portable package", message =>
                AppDiagnosticsLog.Write(_paths, nameof(SetupBootstrapService), message));
            if (Directory.Exists(extractRoot))
            {
                try
                {
                    Directory.Delete(extractRoot, recursive: true);
                }
                catch (Exception ex)
                {
                    AppDiagnosticsLog.Write(
                        _paths,
                        nameof(SetupBootstrapService),
                        $"Failed to remove portable 7-Zip staging directory. {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    internal static async Task ExtractPortable7ZipExecutableAsync(
        string archivePath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = File.OpenRead(archivePath);
        using var archive = SevenZipArchive.OpenArchive(archiveStream);
        var executableEntry = archive.Entries.FirstOrDefault(entry =>
            !entry.IsDirectory && IsPortable7ZipExecutableEntry(entry.Key));
        if (executableEntry is null)
        {
            throw new FileNotFoundException("The verified 7-Zip package does not contain x64/7za.exe.", archivePath);
        }

        Directory.CreateDirectory(targetDirectory);
        var destinationPath = Path.Combine(targetDirectory, "7z.exe");
        await using var entryStream = executableEntry.OpenEntryStream();
        await using var destinationStream = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await entryStream.CopyToAsync(destinationStream, cancellationToken);
    }

    internal static bool IsPortable7ZipExecutableEntry(string? entryKey)
        => string.Equals(
            entryKey?.Replace('\\', '/').TrimStart('/'),
            "x64/7za.exe",
            StringComparison.OrdinalIgnoreCase);

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

    internal static bool IsInstalledVersionVerified(
        SetupDependencyKind kind,
        string installedVersion,
        string expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion)
            || string.IsNullOrWhiteSpace(expectedVersion)
            || installedVersion.Contains("probe failed", StringComparison.OrdinalIgnoreCase)
            || installedVersion.Contains("probe timed out", StringComparison.OrdinalIgnoreCase)
            || installedVersion.Contains("version string unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (kind is SetupDependencyKind.X264 or SetupDependencyKind.X265 or SetupDependencyKind.SvtAv1
            && TryResolveEncoderVersionUpdate(installedVersion, expectedVersion, out var encoderUpdateAvailable))
        {
            return !encoderUpdateAvailable;
        }

        if (kind == SetupDependencyKind.FfmpegBundle
            && TryExtractFfmpegBuildDate(installedVersion, out var installedBuildDate)
            && TryExtractFfmpegBuildDate(expectedVersion, out var expectedBuildDate))
        {
            return installedBuildDate.Date >= expectedBuildDate.Date;
        }

        var installed = ExtractComparableVersion(installedVersion);
        var expected = ExtractComparableVersion(expectedVersion);
        return installed is not null && expected is not null && installed >= expected;
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

    private static void ReportPackageDownloadProgress(
        IProgress<SetupInstallProgress>? progress,
        SetupDependencyKind kind,
        string packageName,
        PackageDownloadProgress download)
    {
        var downloadedMegabytes = download.BytesReceived / 1024d / 1024d;
        if (download.TotalBytes is not > 0)
        {
            ReportProgress(progress, kind, 35, $"Downloading {packageName}... {downloadedMegabytes:F1} MB");
            return;
        }

        var totalMegabytes = download.TotalBytes.Value / 1024d / 1024d;
        var ratio = Math.Clamp(download.BytesReceived / (double)download.TotalBytes.Value, 0, 1);
        var percent = 35 + (ratio * 55);
        ReportProgress(
            progress,
            kind,
            percent,
            $"Downloading {packageName}... {downloadedMegabytes:F1}/{totalMegabytes:F1} MB");
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
        string? QuietUninstallString,
        string? InstallLocation);

    private sealed record VsrepoInstalledPackage(
        string Name,
        string Namespace,
        string InstalledVersion,
        string LatestVersion,
        string Identifier);

    internal sealed record VsPluginBundlePackagePlan(
        IReadOnlyList<string> InstallPackages,
        IReadOnlyList<string> UpgradePackages);

    private sealed record SilentCommand(string FileName, IReadOnlyList<string> Arguments);

    private sealed record ProcessExecutionResult(int ExitCode, string Output, string Error)
    {
        public string CombinedOutput => string.Join(Environment.NewLine, new[] { Output, Error }.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private sealed record PyPiPackageResponse(PyPiPackageInfo? Info);

    private sealed record PyPiPackageInfo(string? Version);

    private sealed record PythonInstallerPackage(
        Version Version,
        string FileName,
        string DownloadUrl,
        string Sha256,
        string ReleaseUrl);

    private sealed record PythonReleaseResponse(
        string Name,
        string Slug,
        [property: JsonPropertyName("is_published")] bool IsPublished,
        [property: JsonPropertyName("pre_release")] bool PreRelease,
        [property: JsonPropertyName("resource_uri")] string ResourceUri);

    private sealed record PythonReleaseFileResponse(
        string Name,
        string Url,
        [property: JsonPropertyName("sha256_sum")] string? Sha256Sum);
}

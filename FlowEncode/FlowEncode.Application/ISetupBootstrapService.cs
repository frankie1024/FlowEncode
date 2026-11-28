using FlowEncode.Domain;

namespace FlowEncode.Application;

public interface ISetupBootstrapService
{
    Task<SetupDependencyStatusReport> GetLocalStatusReportAsync(
        EnvironmentReadinessReport readiness,
        SetupDependencyStatusReport? previousReport = null,
        CancellationToken cancellationToken = default);

    Task<SetupDependencyStatusReport> GetStatusReportAsync(
        EnvironmentReadinessReport readiness,
        CancellationToken cancellationToken = default);

    Task InstallAsync(
        SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task UpdateBeyondBaselineAsync(SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Updating beyond the tested baseline is not supported by this provider."));

    Task DownloadDeewComponentsAsync(string executablePath,
        IProgress<SetupInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Downloading deew components is not supported by this provider."));

    Task InstallVapourSynthPluginAsync(string packageName, string pythonPath,
        IProgress<SetupInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Installing VapourSynth plugins is not supported by this provider."));

    Task UpdateVapourSynthPluginAsync(string packageName, string pythonPath,
        IProgress<SetupInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Updating VapourSynth plugins is not supported by this provider."));

    Task UninstallVapourSynthPluginAsync(string packageName, string pythonPath,
        IProgress<SetupInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Uninstalling VapourSynth plugins is not supported by this provider."));

    Task UninstallAsync(
        SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    bool CanUninstall(SetupDependencyKind kind);
}

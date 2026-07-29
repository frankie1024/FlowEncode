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

    Task RefreshVsPluginPackageDefinitionsAsync(
        EnvironmentReadinessReport? readiness = null,
        CancellationToken cancellationToken = default);

    Task InstallAsync(
        SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task UninstallAsync(
        SetupDependencyKind kind,
        IProgress<SetupInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    bool CanUninstall(SetupDependencyKind kind);
}

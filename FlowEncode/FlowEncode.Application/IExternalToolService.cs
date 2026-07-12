using FlowEncode.Domain;

namespace FlowEncode.Application;

public interface IExternalToolService
{
    IReadOnlyList<DiscoveredExternalToolBinary> DiscoverSystemBinaries();

    DiscoveredExternalToolBinary? ResolveTool(ExternalToolKind kind);

    Task ImportBinaryAsync(
        ExternalToolKind kind,
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExternalToolUpdatePackage>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default);

    Task<string> InstallUpdateAsync(
        ExternalToolUpdatePackage package,
        CancellationToken cancellationToken = default,
        IProgress<PackageDownloadProgress>? downloadProgress = null);

    Task RemoveManagedBinaryAsync(
        ExternalToolKind kind,
        CancellationToken cancellationToken = default);

    string GetToolsRootPath();
}

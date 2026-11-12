using FlowEncode.Domain;

namespace FlowEncode.Application;

public interface IEncoderUpdateService
{
    Task<IReadOnlyList<EncoderUpdatePackage>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EncoderUpdatePackage>> GetBaselinePackagesAsync(CancellationToken cancellationToken = default)
        => GetAvailableUpdatesAsync(cancellationToken);

    Task<string> InstallUpdateAsync(
        EncoderUpdatePackage package,
        CancellationToken cancellationToken = default,
        IProgress<PackageDownloadProgress>? downloadProgress = null);
}

using FlowEncode.Domain;

namespace FlowEncode.Application;

public interface IEnvironmentReadinessService
{
    EnvironmentReadinessReport? TryGetCachedReport() => null;

    Task<EnvironmentReadinessReport> CheckAsync(CancellationToken cancellationToken = default);
}

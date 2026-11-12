using FlowEncode.Domain;

namespace FlowEncode.Application;

public interface IDeewPlanningService
{
    Task<DeewReadinessSnapshot> InspectAsync(string sourcePath, DeewOptions options, CancellationToken cancellationToken = default);
}

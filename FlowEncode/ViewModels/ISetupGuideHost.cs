using System.Collections.Generic;
using System.Threading.Tasks;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;

namespace FlowEncode.ViewModels;

public interface ISetupGuideHost
{
    AppText Texts { get; }

    string StatusText { get; set; }

    bool IsBusy { get; }

    EnvironmentReadinessReport? EnvironmentReadinessReport { get; }

    IReadOnlyDictionary<string, string> ManualToolPaths { get; set; }

    bool HasCompletedSetupGuide { get; set; }

    Task RefreshAsync(string? statusOverride = null, bool includeUpdates = false, bool refreshEnvironmentReadiness = true);

    string? SaveSettings(bool updateStatusText = true);

    ToolProbeResult GetToolResult(RegisteredToolKind kind);

    CapabilityReadiness GetCapabilityReadiness(EnvironmentCapabilityKind kind);

    void NotifyEnvironmentReadinessChanged();

    void NotifyBusyChanged();
}

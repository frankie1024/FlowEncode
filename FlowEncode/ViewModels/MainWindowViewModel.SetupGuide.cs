using System;
using System.Linq;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;

namespace FlowEncode.ViewModels;

public partial class MainWindowViewModel
{
    private bool _hasCompletedSetupGuide;

    internal string AppRootPath => SetupGuideModule.AppRootPath;

    private void RaiseSetupGuidePropertyChanges()
    {
        SetupGuideModule.RefreshLocalizedState();
    }

    public ToolProbeResult GetToolResult(RegisteredToolKind kind)
    {
        return _environmentReadinessReport?.Tools.FirstOrDefault(result => result.Kind == kind)
            ?? new ToolProbeResult(
                kind,
                ReadinessState.Unknown,
                ToolDetectionSource.None,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
    }

    public CapabilityReadiness GetCapabilityReadiness(EnvironmentCapabilityKind kind)
    {
        return _environmentReadinessReport?.Capabilities.FirstOrDefault(result => result.Kind == kind)
            ?? new CapabilityReadiness(kind, ReadinessState.Unknown, System.Array.Empty<CapabilityRequirementReadiness>());
    }
}

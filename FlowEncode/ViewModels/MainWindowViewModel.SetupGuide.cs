using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public partial class MainWindowViewModel
{
    private bool _hasCompletedSetupGuide;

    internal ObservableCollection<SetupGuideCardViewModel> SetupGuideCards => SetupGuideModule.SetupGuideCards;

    internal bool IsSetupGuideOpen => SetupGuideModule.IsSetupGuideOpen;

    internal int SelectedSetupGuideCardIndex
    {
        get => SetupGuideModule.SelectedSetupGuideCardIndex;
        set => SetupGuideModule.SelectedSetupGuideCardIndex = value;
    }

    internal bool CanMoveSetupGuidePrevious => SetupGuideModule.CanMoveSetupGuidePrevious;

    internal bool CanMoveSetupGuideNext => SetupGuideModule.CanMoveSetupGuideNext;

    internal bool IsOnLastSetupGuideCard => SetupGuideModule.IsOnLastSetupGuideCard;

    internal bool CanAdvanceOrDismissSetupGuide => SetupGuideModule.CanAdvanceOrDismissSetupGuide;

    internal string SetupGuideForwardButtonText => SetupGuideModule.SetupGuideForwardButtonText;

    internal string SetupGuidePositionText => SetupGuideModule.SetupGuidePositionText;

    internal Visibility SetupGuidePositionVisibility => SetupGuideModule.SetupGuidePositionVisibility;

    internal Visibility SetupGuideVisibility => SetupGuideModule.SetupGuideVisibility;

    internal string AppRootPath => SetupGuideModule.AppRootPath;

    internal string EncodersRootPath => SetupGuideModule.EncodersRootPath;

    internal string ToolsRootPath => SetupGuideModule.ToolsRootPath;

    internal string SettingsRootPath => SetupGuideModule.SettingsRootPath;

    internal string TemplatesRootPath => SetupGuideModule.TemplatesRootPath;

    internal string LocalizationRootPath => SetupGuideModule.LocalizationRootPath;

    internal string DownloadsRootPath => SetupGuideModule.DownloadsRootPath;

    internal string SetupGuideSummary => SetupGuideModule.SetupGuideSummary;

    internal string EnvironmentCheckedAtText => SetupGuideModule.EnvironmentCheckedAtText;

    internal string SetupGuideRemoteCheckedAtText => SetupGuideModule.SetupGuideRemoteCheckedAtText;

    internal Visibility SetupGuideRemoteCheckedAtVisibility => SetupGuideModule.SetupGuideRemoteCheckedAtVisibility;

    internal bool IsRefreshingSetupGuide => SetupGuideModule.IsRefreshingSetupGuide;

    internal bool IsCheckingSetupDependencyUpdates => SetupGuideModule.IsCheckingSetupDependencyUpdates;

    internal Visibility SetupGuideActionProgressVisibility => SetupGuideModule.SetupGuideActionProgressVisibility;

    internal string SetupGuideRefreshActionText => SetupGuideModule.SetupGuideRefreshActionText;

    internal string SetupGuideUpdateCheckActionText => SetupGuideModule.SetupGuideUpdateCheckActionText;

    internal bool CanExecuteSetupGuideRefreshAction => SetupGuideModule.CanExecuteSetupGuideRefreshAction;

    internal bool CanExecuteSetupGuideUpdateCheckAction => SetupGuideModule.CanExecuteSetupGuideUpdateCheckAction;

    internal void OpenSetupGuide() => SetupGuideModule.OpenSetupGuide();

    internal Task OpenSetupGuideAsync() => SetupGuideModule.OpenSetupGuideAsync();

    internal Task EnsureSetupGuideCardsAsync() => SetupGuideModule.EnsureSetupGuideCardsAsync();

    internal void MoveSetupGuidePrevious() => SetupGuideModule.MoveSetupGuidePrevious();

    internal void MoveSetupGuideNext() => SetupGuideModule.MoveSetupGuideNext();

    internal string? AdvanceOrDismissSetupGuide() => SetupGuideModule.AdvanceOrDismissSetupGuide();

    internal string? DismissSetupGuide() => SetupGuideModule.DismissSetupGuide();

    internal Task RefreshSetupGuideAsync(bool openWhenFinished = false) => SetupGuideModule.RefreshSetupGuideAsync(openWhenFinished);

    internal Task CheckSetupDependencyUpdatesAsync(bool openWhenFinished = false) => SetupGuideModule.CheckSetupDependencyUpdatesAsync(openWhenFinished);

    internal Task<string?> InstallSetupDependencyAsync(SetupDependencyKind kind) => SetupGuideModule.InstallSetupDependencyAsync(kind);

    internal bool RequiresSetupDependencyManualImport(SetupDependencyKind kind) => SetupGuideModule.RequiresSetupDependencyManualImport(kind);

    internal bool HasManualPinnedSetupDependency(SetupDependencyKind kind) => SetupGuideModule.HasManualPinnedSetupDependency(kind);

    internal string GetSetupDependencyDisplayName(SetupDependencyKind kind) => SetupGuideModule.GetSetupDependencyDisplayName(kind);

    internal string GetSetupDependencyCurrentPath(SetupDependencyKind kind) => SetupGuideModule.GetSetupDependencyCurrentPath(kind);

    internal Task<string?> ImportSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath) => SetupGuideModule.ImportSetupDependencyBinaryAsync(kind, sourcePath);

    internal Task<string?> PinSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath) => SetupGuideModule.PinSetupDependencyBinaryAsync(kind, sourcePath);

    internal Task<string?> ClearManualPinnedSetupDependencyAsync(SetupDependencyKind kind, bool refreshAfterClear = true) => SetupGuideModule.ClearManualPinnedSetupDependencyAsync(kind, refreshAfterClear);

    internal Task<string?> UninstallSetupDependencyAsync(SetupDependencyKind kind) => SetupGuideModule.UninstallSetupDependencyAsync(kind);

    internal bool CanManuallySelectSetupDependency(SetupDependencyKind kind) => SetupGuideModule.CanManuallySelectSetupDependency(kind);

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

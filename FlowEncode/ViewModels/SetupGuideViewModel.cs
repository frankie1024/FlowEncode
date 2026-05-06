using System.Collections.ObjectModel;
using System.Threading.Tasks;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class SetupGuideViewModel : ModuleViewModelBase, ISetupDependencyModuleViewModel
{
    public SetupGuideViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public Visibility SetupGuideActionProgressVisibility => Owner.SetupGuideActionProgressVisibility;

    public string SetupGuideRefreshActionText => Owner.SetupGuideRefreshActionText;

    public bool CanExecuteSetupGuideRefreshAction => Owner.CanExecuteSetupGuideRefreshAction;

    public string SetupGuideUpdateCheckActionText => Owner.SetupGuideUpdateCheckActionText;

    public bool CanExecuteSetupGuideUpdateCheckAction => Owner.CanExecuteSetupGuideUpdateCheckAction;

    public ObservableCollection<SetupGuideCardViewModel> SetupGuideCards => Owner.SetupGuideCards;

    public int SelectedSetupGuideCardIndex
    {
        get => Owner.SelectedSetupGuideCardIndex;
        set => Owner.SelectedSetupGuideCardIndex = value;
    }

    public string SetupGuidePositionText => Owner.SetupGuidePositionText;

    public Visibility SetupGuidePositionVisibility => Owner.SetupGuidePositionVisibility;

    public Visibility SetupGuideVisibility => Owner.SetupGuideVisibility;

    public bool CanMoveSetupGuidePrevious => Owner.CanMoveSetupGuidePrevious;

    public bool CanMoveSetupGuideNext => Owner.CanMoveSetupGuideNext;

    public bool CanAdvanceOrDismissSetupGuide => Owner.CanAdvanceOrDismissSetupGuide;

    public string SetupGuideForwardButtonText => Owner.SetupGuideForwardButtonText;

    public bool IsSetupGuideOpen => Owner.IsSetupGuideOpen;

    public Task EnsureCardsAsync()
    {
        return Owner.EnsureSetupGuideCardsAsync();
    }

    public Task OpenAsync()
    {
        return Owner.OpenSetupGuideAsync();
    }

    public Task RefreshSetupGuideAsync()
    {
        return Owner.RefreshSetupGuideAsync();
    }

    public Task CheckSetupDependencyUpdatesAsync(bool openWhenFinished = false)
    {
        return Owner.CheckSetupDependencyUpdatesAsync(openWhenFinished);
    }

    public void MoveSetupGuidePrevious()
    {
        Owner.MoveSetupGuidePrevious();
    }

    public void MoveSetupGuideNext()
    {
        Owner.MoveSetupGuideNext();
    }

    public string? AdvanceOrDismissSetupGuide()
    {
        return Owner.AdvanceOrDismissSetupGuide();
    }

    public string? DismissSetupGuide()
    {
        return Owner.DismissSetupGuide();
    }

    public bool RequiresSetupDependencyManualImport(SetupDependencyKind kind)
    {
        return Owner.RequiresSetupDependencyManualImport(kind);
    }

    public bool HasManualPinnedSetupDependency(SetupDependencyKind kind)
    {
        return Owner.HasManualPinnedSetupDependency(kind);
    }

    public string GetSetupDependencyDisplayName(SetupDependencyKind kind)
    {
        return Owner.GetSetupDependencyDisplayName(kind);
    }

    public string GetSetupDependencyCurrentPath(SetupDependencyKind kind)
    {
        return Owner.GetSetupDependencyCurrentPath(kind);
    }

    public Task<string?> InstallSetupDependencyAsync(SetupDependencyKind kind)
    {
        return Owner.InstallSetupDependencyAsync(kind);
    }

    public Task<string?> ImportSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath)
    {
        return Owner.ImportSetupDependencyBinaryAsync(kind, sourcePath);
    }

    public Task<string?> PinSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath)
    {
        return Owner.PinSetupDependencyBinaryAsync(kind, sourcePath);
    }

    public Task<string?> ClearManualPinnedSetupDependencyAsync(SetupDependencyKind kind, bool refreshAfterClear = true)
    {
        return Owner.ClearManualPinnedSetupDependencyAsync(kind, refreshAfterClear);
    }

    public Task<string?> UninstallSetupDependencyAsync(SetupDependencyKind kind)
    {
        return Owner.UninstallSetupDependencyAsync(kind);
    }
}

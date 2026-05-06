using System.Collections.ObjectModel;
using System.Threading.Tasks;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class SettingsDependenciesViewModel : ModuleViewModelBase, ISetupDependencyModuleViewModel
{
    public SettingsDependenciesViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public string EnvironmentCheckedAtText => Owner.EnvironmentCheckedAtText;

    public string SetupGuideRemoteCheckedAtText => Owner.SetupGuideRemoteCheckedAtText;

    public Visibility SetupGuideActionProgressVisibility => Owner.SetupGuideActionProgressVisibility;

    public string SetupGuideRefreshActionText => Owner.SetupGuideRefreshActionText;

    public bool CanExecuteSetupGuideRefreshAction => Owner.CanExecuteSetupGuideRefreshAction;

    public string SetupGuideUpdateCheckActionText => Owner.SetupGuideUpdateCheckActionText;

    public bool CanExecuteSetupGuideUpdateCheckAction => Owner.CanExecuteSetupGuideUpdateCheckAction;

    public ObservableCollection<SetupGuideCardViewModel> SetupGuideCards => Owner.SetupGuideCards;

    public bool IsSetupGuideOpen => Owner.IsSetupGuideOpen;

    public Task RefreshSetupGuideAsync()
    {
        return Owner.RefreshSetupGuideAsync();
    }

    public Task CheckSetupDependencyUpdatesAsync(bool openWhenFinished = false)
    {
        return Owner.CheckSetupDependencyUpdatesAsync(openWhenFinished);
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

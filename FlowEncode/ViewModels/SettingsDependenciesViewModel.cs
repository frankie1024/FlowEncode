using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class SettingsDependenciesViewModel : ModuleViewModelBase, ISetupDependencyModuleViewModel
{
    public SettingsDependenciesViewModel(MainWindowViewModel owner)
        : base(owner)
    {
        owner.SetupGuideModule.PropertyChanged += SetupGuide_PropertyChanged;
    }

    private SetupGuideViewModel SetupGuide => Owner.SetupGuideModule;

    public AppText Texts => Owner.Texts;

    public string EnvironmentCheckedAtText => SetupGuide.EnvironmentCheckedAtText;

    public string SetupGuideRemoteCheckedAtText => SetupGuide.SetupGuideRemoteCheckedAtText;

    public Visibility SetupGuideActionProgressVisibility => SetupGuide.SetupGuideActionProgressVisibility;

    public string SetupGuideRefreshActionText => SetupGuide.SetupGuideRefreshActionText;

    public bool CanExecuteSetupGuideRefreshAction => SetupGuide.CanExecuteSetupGuideRefreshAction;

    public string SetupGuideUpdateCheckActionText => SetupGuide.SetupGuideUpdateCheckActionText;

    public bool CanExecuteSetupGuideUpdateCheckAction => SetupGuide.CanExecuteSetupGuideUpdateCheckAction;

    public ObservableCollection<SetupGuideCardViewModel> SetupGuideCards => SetupGuide.SetupGuideCards;

    public bool IsSetupGuideOpen => SetupGuide.IsSetupGuideOpen;

    public Task RefreshSetupGuideAsync()
    {
        return SetupGuide.RefreshSetupGuideAsync();
    }

    public Task CheckSetupDependencyUpdatesAsync(bool openWhenFinished = false)
    {
        return SetupGuide.CheckSetupDependencyUpdatesAsync(openWhenFinished);
    }

    public bool RequiresSetupDependencyManualImport(SetupDependencyKind kind)
    {
        return SetupGuide.RequiresSetupDependencyManualImport(kind);
    }

    public bool HasManualPinnedSetupDependency(SetupDependencyKind kind)
    {
        return SetupGuide.HasManualPinnedSetupDependency(kind);
    }

    public string GetSetupDependencyDisplayName(SetupDependencyKind kind)
    {
        return SetupGuide.GetSetupDependencyDisplayName(kind);
    }

    public string GetSetupDependencyCurrentPath(SetupDependencyKind kind)
    {
        return SetupGuide.GetSetupDependencyCurrentPath(kind);
    }

    public Task<string?> InstallSetupDependencyAsync(SetupDependencyKind kind)
    {
        return SetupGuide.InstallSetupDependencyAsync(kind);
    }

    public Task<string?> ImportSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath)
    {
        return SetupGuide.ImportSetupDependencyBinaryAsync(kind, sourcePath);
    }

    public Task<string?> PinSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath)
    {
        return SetupGuide.PinSetupDependencyBinaryAsync(kind, sourcePath);
    }

    public Task<string?> ClearManualPinnedSetupDependencyAsync(SetupDependencyKind kind, bool refreshAfterClear = true)
    {
        return SetupGuide.ClearManualPinnedSetupDependencyAsync(kind, refreshAfterClear);
    }

    public Task<string?> UninstallSetupDependencyAsync(SetupDependencyKind kind)
    {
        return SetupGuide.UninstallSetupDependencyAsync(kind);
    }

    private void SetupGuide_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
    }

    public override void Dispose()
    {
        Owner.SetupGuideModule.PropertyChanged -= SetupGuide_PropertyChanged;
        base.Dispose();
    }
}

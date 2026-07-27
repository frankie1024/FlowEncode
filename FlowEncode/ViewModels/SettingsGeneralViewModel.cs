using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.ViewModels;

public sealed class SettingsGeneralViewModel : ModuleViewModelBase
{
    public SettingsGeneralViewModel(MainWindowViewModel owner)
        : base(owner)
    {
    }

    public AppText Texts => Owner.Texts;

    public string AppCurrentVersionText => Owner.AppCurrentVersionText;

    public string AppLatestVersionText => Owner.AppLatestVersionText;

    public Visibility AppLatestVersionVisibility => Owner.AppLatestVersionVisibility;

    public bool IsAppUpdateActionInProgress => Owner.IsAppUpdateActionInProgress;

    public Visibility AppUpdateProgressVisibility => Owner.AppUpdateProgressVisibility;

    public bool CanExecuteAppUpdateAction => Owner.CanExecuteAppUpdateAction;

    public string AppUpdateActionText => Owner.AppUpdateActionText;

    public Symbol AppUpdateActionIcon => Owner.AppUpdateActionIcon;

    public ObservableCollection<ThemeOption> ThemeOptions => Owner.ThemeOptions;

    public ThemeOption? SelectedTheme
    {
        get => Owner.SelectedTheme;
        set => Owner.SelectedTheme = value;
    }

    public ObservableCollection<LanguageOption> LanguageOptions => Owner.LanguageOptions;

    public LanguageOption? SelectedLanguage
    {
        get => Owner.SelectedLanguage;
        set => Owner.SelectedLanguage = value;
    }

    public bool PreferSystemEncoders
    {
        get => Owner.PreferSystemEncoders;
        set => Owner.PreferSystemEncoders = value;
    }

    public bool AutoCheckUpdatesOnStartup
    {
        get => Owner.AutoCheckUpdatesOnStartup;
        set => Owner.AutoCheckUpdatesOnStartup = value;
    }

    public string WorkspaceRootPath => Owner.WorkspaceRootPath;

    public string AppRootPath => Owner.AppRootPath;

    public Task<string?> PrepareWorkspaceRootChangeAsync(string proposedWorkspaceRootPath)
    {
        return Owner.PrepareWorkspaceRootChangeAsync(proposedWorkspaceRootPath);
    }

    public void CancelPreparedWorkspaceRootChange()
    {
        Owner.CancelPreparedWorkspaceRootChange();
    }
}

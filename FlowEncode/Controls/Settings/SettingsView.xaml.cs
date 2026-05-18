using System;
using System.Threading.Tasks;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Settings;

public sealed partial class SettingsView : UserControl
{
    internal ISettingsViewHost? Host { get; set; }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;
    private SettingsGeneralViewModel? GeneralViewModel => ViewModel?.General;
    private SettingsDependenciesViewModel? DependenciesViewModel => ViewModel?.Dependencies;

    public SettingsView()
    {
        InitializeComponent();
    }

    public void ApplyLayout(bool compactLayout, Thickness contentPadding)
    {
        ContentStack.Padding = contentPadding;
        SettingsOverviewGrid.ColumnSpacing = compactLayout ? 0 : UiTokens.SpacingXXL;
        SettingsControlsGrid.ColumnSpacing = compactLayout ? 0 : UiTokens.SpacingL;
        SettingsControlsGrid.RowSpacing = UiTokens.SpacingM;

        SettingsOverviewSecondaryColumn.Width = compactLayout
            ? new GridLength(0)
            : GridLength.Auto;
        Grid.SetRow(SettingsActionPanel, compactLayout ? 1 : 0);
        Grid.SetColumn(SettingsActionPanel, compactLayout ? 0 : 1);
        Grid.SetColumnSpan(SettingsActionPanel, compactLayout ? 2 : 1);
        SettingsActionPanel.HorizontalAlignment = compactLayout ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        SettingsActionPanel.Margin = compactLayout ? new Thickness(0, 2, 0, 0) : new Thickness(0);

        SettingsControlsSecondaryColumn.Width = compactLayout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(SettingsControlsGrid, compactLayout ? 2 : 1);
        Grid.SetColumn(SettingsControlsGrid, 0);
        Grid.SetColumnSpan(SettingsControlsGrid, 2);

        Grid.SetColumn(SettingsSelectorsPanel, 0);
        Grid.SetRow(SettingsSelectorsPanel, 0);
        Grid.SetRowSpan(SettingsSelectorsPanel, 1);

        Grid.SetColumn(SettingsTogglePanel, compactLayout ? 0 : 1);
        Grid.SetRow(SettingsTogglePanel, compactLayout ? 1 : 0);
        Grid.SetRowSpan(SettingsTogglePanel, 1);
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        var generalViewModel = GeneralViewModel;
        if (Host is null || generalViewModel is null)
        {
            return;
        }

        await RunGeneralGuardedAsync(
            "Failed to handle app update action",
            generalViewModel.Texts.ErrorSaveFailedTitle,
            Host.HandleAppUpdateAsync);
    }

    private void OpenToolsetFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (GeneralViewModel is not null)
        {
            SetupDependencyInteractionHelper.OpenPath(GeneralViewModel.AppRootPath);
        }
    }

    private async void OpenSetupGuideButton_Click(object sender, RoutedEventArgs e)
    {
        var generalViewModel = GeneralViewModel;
        if (Host is null || generalViewModel is null)
        {
            return;
        }

        await RunGeneralGuardedAsync(
            "Failed to open setup guide",
            generalViewModel.Texts.ErrorSaveFailedTitle,
            Host.OpenSetupGuideAsync);
    }

    private async void RefreshSetupGuideButton_Click(object sender, RoutedEventArgs e)
    {
        if (DependenciesViewModel is not null)
        {
            await SetupDependencyInteractionHelper.RunGuardedAsync(
                DependenciesViewModel,
                this,
                nameof(SettingsView),
                "Failed to refresh setup guide",
                DependenciesViewModel.Texts.ErrorSaveFailedTitle,
                () => DependenciesViewModel.RefreshSetupGuideAsync());
        }
    }

    private async void CheckSetupDependencyUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (DependenciesViewModel is not null)
        {
            await SetupDependencyInteractionHelper.RunGuardedAsync(
                DependenciesViewModel,
                this,
                nameof(SettingsView),
                "Failed to check setup dependency updates",
                DependenciesViewModel.Texts.ErrorSaveFailedTitle,
                () => DependenciesViewModel.CheckSetupDependencyUpdatesAsync(DependenciesViewModel.IsSetupGuideOpen));
        }
    }

    private async void InstallSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SetupDependencyKind kind } || DependenciesViewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.InstallSetupDependencyAsync(DependenciesViewModel, this, kind, nameof(SettingsView));
    }

    private async void ManualSelectSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SetupDependencyKind kind } || DependenciesViewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.ManualSelectSetupDependencyAsync(DependenciesViewModel, this, kind, nameof(SettingsView));
    }

    private async void ClearManualPinnedSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SetupDependencyKind kind } || DependenciesViewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.ClearManualPinnedSetupDependencyAsync(DependenciesViewModel, this, kind, nameof(SettingsView));
    }

    private async void UninstallSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SetupDependencyKind kind } || DependenciesViewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.UninstallSetupDependencyAsync(DependenciesViewModel, this, kind, nameof(SettingsView));
    }

    private void OpenTaggedFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && !string.IsNullOrWhiteSpace(path))
        {
            SetupDependencyInteractionHelper.OpenPath(path);
        }
    }

    private async void BrowseWorkspaceFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var generalViewModel = GeneralViewModel;
        if (generalViewModel is null || Host is null)
        {
            return;
        }

        try
        {
            var folderPath = WindowInteractionHelper.PickFolderPath(
                WindowInteractionHelper.GetMainWindowHandle(),
                generalViewModel.Texts.ChooseFolderButton,
                generalViewModel.WorkspaceRootPath);
            if (folderPath is null)
            {
                return;
            }

            var error = await generalViewModel.PrepareWorkspaceRootChangeAsync(folderPath);
            if (!string.IsNullOrWhiteSpace(error))
            {
                await ShowGeneralMessageAsync(generalViewModel.Texts.ErrorSaveSettingsFailedTitle, error);
                return;
            }

            await Host.PersistSettingsAsync(refreshTemplateLibrary: false);
        }
        catch (Exception ex)
        {
            SetupDependencyInteractionHelper.TryWriteDiagnostic(nameof(SettingsView), $"Failed to browse workspace folder. {ex.GetType().Name}: {ex.Message}");
            await ShowGeneralMessageAsync(generalViewModel.Texts.ErrorSaveSettingsFailedTitle, ex.Message);
        }
    }

    private async void ThemeSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var generalViewModel = GeneralViewModel;
        if (Host is not null && generalViewModel is not null)
        {
            await RunGeneralGuardedAsync(
                "Failed to persist theme selection",
                generalViewModel.Texts.ErrorSaveSettingsFailedTitle,
                () => Host.PersistSettingsAsync(refreshTemplateLibrary: true));
        }
    }

    private async void LanguageSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var generalViewModel = GeneralViewModel;
        if (Host is not null && generalViewModel is not null)
        {
            await RunGeneralGuardedAsync(
                "Failed to persist language selection",
                generalViewModel.Texts.ErrorSaveSettingsFailedTitle,
                () => Host.PersistSettingsAsync(refreshTemplateLibrary: true));
        }
    }

    private async void SettingsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        var generalViewModel = GeneralViewModel;
        if (Host is not null && generalViewModel is not null)
        {
            await RunGeneralGuardedAsync(
                "Failed to persist settings toggle state",
                generalViewModel.Texts.ErrorSaveSettingsFailedTitle,
                () => Host.PersistSettingsAsync(refreshTemplateLibrary: false));
        }
    }

    private void OpenUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            SetupDependencyInteractionHelper.OpenUrl(url);
        }
    }

    private async Task RunGeneralGuardedAsync(string diagnosticAction, string errorTitle, Func<Task> action)
    {
        await UiActionGuard.RunAsync(
            this,
            nameof(SettingsView),
            diagnosticAction,
            errorTitle,
            GeneralViewModel?.Texts.OkButton ?? "OK",
            action);
    }

    private async Task ShowGeneralMessageAsync(string title, string message)
    {
        if (GeneralViewModel is null)
        {
            return;
        }

        await WindowInteractionHelper.ShowMessageAsync(
            XamlRoot,
            ActualTheme,
            GeneralViewModel.Texts.OkButton,
            title,
            message);
    }
}

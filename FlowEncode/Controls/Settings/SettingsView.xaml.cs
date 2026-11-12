using System;
using System.Threading.Tasks;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;

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
        Loaded += (_, _) => SyncMcpHelpVisibility();
        SettingsPathStatusText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            SettingsPathStatusText.Visibility = string.IsNullOrWhiteSpace(SettingsPathStatusText.Text) ? Visibility.Collapsed : Visibility.Visible);
    }

    public void ApplyLayout(double availableWidth, bool compactLayout, Thickness contentPadding)
    {
        ContentStack.Padding = contentPadding;
        SettingsOverviewGrid.ColumnSpacing = compactLayout ? 0 : UiTokens.SpacingXXL;
        SettingsControlsGrid.ColumnSpacing = compactLayout ? 0 : UiTokens.SpacingL;
        SettingsControlsGrid.RowSpacing = compactLayout ? UiTokens.SpacingM : 0;
        ConfigureAutoRows(SettingsOverviewGrid, compactLayout ? 3 : 2);
        ConfigureAutoRows(SettingsControlsGrid, compactLayout ? 3 : 1);
        // The host applies layout before this view is measured on first navigation.
        var stackDependencyHeader = compactLayout || availableWidth < 1000;
        ConfigureAutoRows(SettingsDependencyHeaderGrid, stackDependencyHeader ? 2 : 1);
        SettingsDependencyHeaderGrid.RowSpacing = stackDependencyHeader ? UiTokens.SpacingM : 0;

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
        SettingsControlsTertiaryColumn.Width = compactLayout ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(SettingsWorkspacePanel, compactLayout ? 0 : 2);
        Grid.SetColumnSpan(SettingsWorkspacePanel, 1);
        Grid.SetRow(SettingsWorkspacePanel, compactLayout ? 2 : 0);
        Grid.SetRow(SettingsDependencyActions, stackDependencyHeader ? 1 : 0);
        Grid.SetColumn(SettingsDependencyActions, stackDependencyHeader ? 0 : 1);
        Grid.SetColumnSpan(SettingsDependencyActions, stackDependencyHeader ? 2 : 1);
        SettingsDependencyActions.HorizontalAlignment = stackDependencyHeader ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        SettingsDependencyActions.Orientation = compactLayout ? Orientation.Vertical : Orientation.Horizontal;
    }

    private static void ConfigureAutoRows(Grid grid, int rowCount)
    {
        if (grid.RowDefinitions.Count == rowCount) return;
        grid.RowDefinitions.Clear();
        for (var index = 0; index < rowCount; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
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

    private async void WriteSystemPathButton_Click(object sender, RoutedEventArgs e)
    {
        var dependencies = DependenciesViewModel;
        if (dependencies is not null)
        {
            await SetupDependencyInteractionHelper.RunGuardedAsync(
                dependencies,
                this,
                nameof(SettingsView),
                "Write tool directories to the system PATH",
                dependencies.Texts.SystemPathErrorTitle,
                dependencies.WriteSystemPathAsync);
        }
    }

    private void MoreDependencyActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SetupGuideDependencyItemViewModel item } button) return;
        if (DependenciesViewModel is null) return;
        SetupDependencyInteractionHelper.ShowMoreDependencyActions(DependenciesViewModel, this, button, item, nameof(SettingsView));
    }

    private async void InstallSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SetupDependencyKind kind } || DependenciesViewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.InstallSetupDependencyAsync(DependenciesViewModel, this, kind, nameof(SettingsView));
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

            var persisted = await Host.PersistSettingsAsync(refreshTemplateLibrary: true);
            if (!persisted)
            {
                generalViewModel.CancelPreparedWorkspaceRootChange();
                return;
            }

            if (DependenciesViewModel is not null)
            {
                await DependenciesViewModel.RefreshSetupGuideAsync();
            }
        }
        catch (Exception ex)
        {
            GeneralViewModel?.CancelPreparedWorkspaceRootChange();
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

    private void SyncMcpHelpVisibility()
    {
        if (McpHelpButton is null || McpAutomationToggle is null) return;
        McpHelpButton.Visibility = McpAutomationToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (!McpAutomationToggle.IsOn) McpConnectionFlyout?.Hide();
    }

    private void McpAutomationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        SyncMcpHelpVisibility();
        SettingsToggle_Toggled(sender, e);
    }

    private void McpConnectionFlyout_Opening(object sender, object e)
    {
        McpHelpContent.DataContext = GeneralViewModel;
        McpCopyLabel.Text = GeneralViewModel?.Texts.McpCopyConfigButton ?? "Copy connection configuration";
        McpCopyErrorText.Visibility = Visibility.Collapsed;
    }

    private void CopyMcpConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var general = GeneralViewModel;
        if (general is null || string.IsNullOrWhiteSpace(general.McpConnectionConfig)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(general.McpConnectionConfig);
            Clipboard.SetContent(package);
            McpCopyLabel.Text = general.Texts.McpConfigCopied;
            McpCopyErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            McpCopyErrorText.Text = general.Texts.McpConfigCopyFailed;
            McpCopyErrorText.Visibility = Visibility.Visible;
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
                async () =>
                {
                    var persisted = await Host.PersistSettingsAsync(refreshTemplateLibrary: false);
                    if (persisted
                        && ReferenceEquals(sender, PreferSystemEncodersToggle)
                        && DependenciesViewModel is not null)
                    {
                        await DependenciesViewModel.RefreshSetupGuideAsync();
                    }
                });
        }
    }

    private void SetupGuideCardExpander_CollapsedCompleted(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement expander)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (SettingsScrollViewer.Content is not FrameworkElement scrollContent)
            {
                return;
            }

            var transform = expander.TransformToVisual(scrollContent);
            var expanderTop = transform.TransformPoint(new Point(0, 0)).Y;
            var targetOffset = Math.Max(0, expanderTop - UiTokens.SpacingS);
            if (SettingsScrollViewer.VerticalOffset <= targetOffset)
            {
                return;
            }

            SettingsScrollViewer.ChangeView(null, targetOffset, null, true);
        });
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

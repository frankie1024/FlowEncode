using System;
using System.Threading.Tasks;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FlowEncode.Controls.AutoCompression;

public sealed partial class AutoCompressionView : UserControl
{
    private bool _interactionsInitialized;
    private bool _isAutoApplyingAdvancedExpanded;
    private bool _userToggledAdvancedExpanded;
    private bool _stackedWorkspace;

    private AutoCompressionViewModel? ViewModel => DataContext as AutoCompressionViewModel;
    private AutoCompressionFormViewModel? FormViewModel => ViewModel?.Form;

    public AutoCompressionView()
    {
        InitializeComponent();
        Loaded += AutoCompressionView_Loaded;
        RootScrollViewer.SizeChanged += (_, _) => ScheduleWorkspaceHeightRefresh();
        AutoAdvancedParametersExpander.Expanded += (_, _) => MarkAdvancedExpanderUserToggled();
        AutoAdvancedParametersExpander.Collapsed += (_, _) => MarkAdvancedExpanderUserToggled();
    }

    public void ApplyLayout(double availableWidth, bool stackedWorkspace, bool compactForms, Thickness contentPadding)
    {
        _stackedWorkspace = stackedWorkspace;
        ContentStack.Padding = contentPadding;

        AutoWorkspaceGrid.ColumnSpacing = stackedWorkspace ? 0 : UiTokens.SpacingL;
        AutoWorkspaceGrid.RowSpacing = stackedWorkspace ? UiTokens.SpacingL : 0;
        AutoPrimaryColumn.Width = stackedWorkspace
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(52, GridUnitType.Star);
        AutoSecondaryColumn.Width = stackedWorkspace
            ? new GridLength(0)
            : new GridLength(48, GridUnitType.Star);
        AutoWorkspacePrimaryRow.Height = stackedWorkspace ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        AutoWorkspaceSecondaryRow.Height = stackedWorkspace ? GridLength.Auto : new GridLength(0);

        Grid.SetRow(AutoFormCard, 0);
        Grid.SetColumn(AutoFormCard, 0);
        Grid.SetColumnSpan(AutoFormCard, 1);
        Grid.SetRow(AutoMonitorCard, stackedWorkspace ? 1 : 0);
        Grid.SetColumn(AutoMonitorCard, stackedWorkspace ? 0 : 1);
        Grid.SetColumnSpan(AutoMonitorCard, stackedWorkspace ? 2 : 1);

        AutoFormScrollViewer.VerticalScrollBarVisibility = stackedWorkspace
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;

        ConfigureThreeItemGrid(
            AutoCompressionTargetGrid,
            AutoCompressionMetricColumn,
            AutoCompressionTargetColumn,
            AutoCompressionMetricComboBox,
            AutoCompressionTargetVmafBox,
            compactForms);

        ConfigureTwoItemGrid(
            AutoCompressionBudgetGrid,
            AutoCompressionWorkersColumn,
            AutoCompressionWorkersBox,
            compactForms);

        ConfigureFourAdvancedItemsGrid(AutoCompressionAdvancedOptionsGrid, compactForms ? 1 : 2);
        AutoCompressionActionBar.IsStacked = compactForms;
        if (!_userToggledAdvancedExpanded)
        {
            _isAutoApplyingAdvancedExpanded = true;
            try
            {
                AutoAdvancedParametersExpander.IsExpanded = !stackedWorkspace;
            }
            finally
            {
                _isAutoApplyingAdvancedExpanded = false;
            }
        }

        ScheduleWorkspaceHeightRefresh();
    }

    private void MarkAdvancedExpanderUserToggled()
    {
        if (!_isAutoApplyingAdvancedExpanded)
        {
            _userToggledAdvancedExpanded = true;
        }
    }

    private void ScheduleWorkspaceHeightRefresh() => DispatcherQueue.TryEnqueue(UpdateWorkspaceHeight);

    private void UpdateWorkspaceHeight()
    {
        if (_stackedWorkspace || RootScrollViewer.ActualHeight <= 0)
        {
            AutoWorkspaceGrid.Height = double.NaN;
            return;
        }

        var availableHeight = RootScrollViewer.ActualHeight
            - ContentStack.Padding.Top
            - ContentStack.Padding.Bottom;

        AutoWorkspaceGrid.Height = availableHeight >= 460
            ? Math.Floor(availableHeight)
            : double.NaN;
    }

    private void AutoCompressionView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_interactionsInitialized)
        {
            _interactionsInitialized = true;
            AutoSourcePathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(AutoSourcePathTextBox_DoubleTapped), true);
            AutoOutputPathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(AutoOutputPathTextBox_DoubleTapped), true);
        }

        ScheduleWorkspaceHeightRefresh();
    }

    private async void BrowseAutoSourceButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(BrowseAutoSourceButton_Click), PickAutoSourceFileAsync);
    }

    private async void AutoSourcePathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await RunGuardedAsync(nameof(AutoSourcePathTextBox_DoubleTapped), PickAutoSourceFileAsync);
    }

    private async Task PickAutoSourceFileAsync()
    {
        var formViewModel = FormViewModel;
        if (formViewModel is null)
        {
            return;
        }

        var filePath = WindowInteractionHelper.PickFilteredFilePath(
            WindowInteractionHelper.GetMainWindowHandle(),
            formViewModel.Texts.SourceHeader,
            formViewModel.AutoCompressionSourcePath,
            formViewModel.Texts.SupportedSourceFileTypeDescription(InputSourceSupport.PreferredPickerPattern),
            InputSourceSupport.PreferredPickerPattern,
            formViewModel.Texts.AllFilesTypeDescription);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            await WindowInteractionHelper.ApplyPickedPathAsync(AutoSourcePathTextBox, filePath, path => formViewModel.AutoCompressionSourcePath = path);
        }
    }

    private async void BrowseAutoOutputButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(BrowseAutoOutputButton_Click), PickAutoOutputFolderAsync);
    }

    private async void AutoOutputPathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await RunGuardedAsync(nameof(AutoOutputPathTextBox_DoubleTapped), PickAutoOutputFolderAsync);
    }

    private async Task PickAutoOutputFolderAsync()
    {
        var formViewModel = FormViewModel;
        if (formViewModel is null)
        {
            return;
        }

        var dialogPath = string.IsNullOrWhiteSpace(formViewModel.AutoCompressionOutputPath)
            ? formViewModel.AutoCompressionSourcePath
            : formViewModel.AutoCompressionOutputPath;
        var folderPath = WindowInteractionHelper.PickFolderPath(
            WindowInteractionHelper.GetMainWindowHandle(),
            formViewModel.Texts.ChooseFolderButton,
            dialogPath);
        if (folderPath is not null)
        {
            await WindowInteractionHelper.ApplyPickedPathAsync(AutoOutputPathTextBox, folderPath, path => formViewModel.AutoCompressionOutputPath = path);
        }
    }

    private async void StartAutoCompressionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(StartAutoCompressionButton_Click), StartAutoCompressionAsync);
    }

    private async Task StartAutoCompressionAsync()
    {
        var formViewModel = FormViewModel;
        if (formViewModel is null)
        {
            return;
        }

        var validationError = formViewModel.ValidateAutoCompressionForStart(out var existingOutputPath);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            await WindowInteractionHelper.ShowMessageAsync(
                XamlRoot,
                ActualTheme,
                formViewModel.Texts.OkButton,
                formViewModel.Texts.ErrorCannotStartAutoCompressionTitle,
                validationError);
            return;
        }

        if (!string.IsNullOrWhiteSpace(existingOutputPath))
        {
            var overwriteConfirmed = await WindowInteractionHelper.ShowConfirmationAsync(
                XamlRoot,
                ActualTheme,
                formViewModel.Texts.OverwriteOutputTitle,
                formViewModel.Texts.OverwriteOutputMessage(existingOutputPath),
                formViewModel.Texts.OverwriteButton,
                formViewModel.Texts.CancelButton,
                ContentDialogButton.Close);

            if (!overwriteConfirmed)
            {
                return;
            }
        }

        var error = await formViewModel.StartAutoCompressionAsync();
        if (!string.IsNullOrWhiteSpace(error))
        {
            await WindowInteractionHelper.ShowMessageAsync(
                XamlRoot,
                ActualTheme,
                formViewModel.Texts.OkButton,
                formViewModel.Texts.ErrorCannotStartAutoCompressionTitle,
                error);
        }
    }

    private Task RunGuardedAsync(string actionName, Func<Task> action)
    {
        var texts = FormViewModel?.Texts;
        return UiActionGuard.RunAsync(
            this,
            nameof(AutoCompressionView),
            actionName,
            texts?.ErrorCannotStartAutoCompressionTitle ?? "无法启动自动压制",
            texts?.OkButton ?? "确定",
            action);
    }

    private void CancelAutoCompressionButton_Click(object sender, RoutedEventArgs e)
    {
        FormViewModel?.CancelAutoCompression();
    }

    private static void ConfigureTwoItemGrid(
        Grid grid,
        ColumnDefinition secondColumn,
        FrameworkElement secondItem,
        bool stacked)
    {
        grid.ColumnSpacing = stacked ? 0 : UiTokens.SpacingS;
        grid.RowSpacing = stacked ? UiTokens.SpacingS : 0;
        secondColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
    }

    private static void ConfigureThreeItemGrid(
        Grid grid,
        ColumnDefinition secondColumn,
        ColumnDefinition thirdColumn,
        FrameworkElement secondItem,
        FrameworkElement thirdItem,
        bool stacked)
    {
        grid.ColumnSpacing = stacked ? 0 : UiTokens.SpacingS;
        grid.RowSpacing = stacked ? UiTokens.SpacingS : 0;
        secondColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        thirdColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
        Grid.SetRow(thirdItem, stacked ? 2 : 0);
        Grid.SetColumn(thirdItem, stacked ? 0 : 2);
    }

    private void ConfigureFourAdvancedItemsGrid(Grid grid, int columnCount)
    {
        var rowCount = columnCount >= 2 ? 2 : 4;
        if (grid.RowDefinitions.Count != rowCount)
        {
            grid.RowDefinitions.Clear();
            for (var index = 0; index < rowCount; index++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
        }

        grid.ColumnSpacing = columnCount == 1 ? 0 : UiTokens.SpacingS;
        if (grid.ColumnDefinitions.Count != columnCount)
        {
            grid.ColumnDefinitions.Clear();
            for (var index = 0; index < columnCount; index++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
        }

        var items = new FrameworkElement[]
        {
            AutoCompressionProbingRateBox,
            AutoCompressionProbingStatisticComboBox,
            AutoCompressionProbeResolutionTextBox,
            AutoCompressionInterpolationMethodComboBox
        };

        for (var index = 0; index < items.Length; index++)
        {
            Grid.SetRow(items[index], index / columnCount);
            Grid.SetColumn(items[index], index % columnCount);
        }
    }
}

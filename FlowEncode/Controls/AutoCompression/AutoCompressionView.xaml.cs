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

    private AutoCompressionViewModel? ViewModel => DataContext as AutoCompressionViewModel;
    private AutoCompressionFormViewModel? FormViewModel => ViewModel?.Form;

    public AutoCompressionView()
    {
        InitializeComponent();
        Loaded += AutoCompressionView_Loaded;
    }

    public void ApplyLayout(bool compactForms, double width, Thickness contentPadding)
    {
        ContentStack.Padding = contentPadding;
        ConfigureTwoItemGrid(AutoSourcePathGrid, AutoSourcePathActionColumn, AutoSourceBrowseButton, false, GridLength.Auto);
        ConfigureTwoItemGrid(AutoOutputPathGrid, AutoOutputPathActionColumn, AutoOutputBrowseButton, false, GridLength.Auto);
        AutoSourcePathGrid.RowSpacing = 0;

        var optionColumnCount = width >= 900
            ? 5
            : width >= 720
                ? 3
                : width >= 560
                    ? 2
                    : 1;
        RebuildAutoRows(
            AutoCompressionOptionsGrid,
            optionColumnCount switch
            {
                >= 5 => 1,
                3 => 2,
                2 => 3,
                _ => 5
            });
        ConfigureFiveItemGrid(
            AutoCompressionOptionsGrid,
            AutoCompressionMetricColumn,
            AutoCompressionTargetColumn,
            AutoCompressionProbesColumn,
            AutoCompressionWorkersColumn,
            AutoCompressionMetricComboBox,
            AutoCompressionTargetVmafBox,
            AutoCompressionProbesBox,
            AutoCompressionWorkersBox,
            optionColumnCount);

        ConfigureFourAdvancedItemsGrid(AutoCompressionAdvancedOptionsGrid, width >= 1080 ? 4 : width >= 760 ? 2 : 1);

        ConfigureTwoItemGrid(AutoCompressionActionGrid, AutoCompressionCancelColumn, CancelAutoCompressionButton, compactForms, new GridLength(1, GridUnitType.Star));
    }

    private void AutoCompressionView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_interactionsInitialized)
        {
            return;
        }

        _interactionsInitialized = true;
        AutoSourcePathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(AutoSourcePathTextBox_DoubleTapped), true);
        AutoOutputPathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(AutoOutputPathTextBox_DoubleTapped), true);
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
        bool stacked,
        GridLength expandedSecondColumnWidth)
    {
        grid.ColumnSpacing = stacked ? 0 : UiTokens.SpacingM;
        secondColumn.Width = stacked ? new GridLength(0) : expandedSecondColumnWidth;
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
    }

    private static void RebuildAutoRows(Grid grid, int rowCount)
    {
        grid.RowDefinitions.Clear();
        for (var index = 0; index < rowCount; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
    }

    private void ConfigureFourAdvancedItemsGrid(Grid grid, int columnCount)
    {
        RebuildAutoRows(
            grid,
            columnCount switch
            {
                >= 4 => 1,
                2 => 2,
                _ => 4
            });

        grid.ColumnSpacing = columnCount == 1 ? 0 : UiTokens.SpacingM;

        var columns = new[]
        {
            grid.ColumnDefinitions[0],
            grid.ColumnDefinitions[1],
            grid.ColumnDefinitions[2],
            grid.ColumnDefinitions[3]
        };

        for (var index = 0; index < columns.Length; index++)
        {
            columns[index].Width = index < columnCount
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
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
            var item = items[index];
            var row = index / columnCount;
            var column = index % columnCount;
            Grid.SetRow(item, row);
            Grid.SetColumn(item, column);
        }
    }

    private static void ConfigureFiveItemGrid(
        Grid grid,
        ColumnDefinition secondColumn,
        ColumnDefinition thirdColumn,
        ColumnDefinition fourthColumn,
        ColumnDefinition fifthColumn,
        FrameworkElement secondItem,
        FrameworkElement thirdItem,
        FrameworkElement fourthItem,
        FrameworkElement fifthItem,
        int columnCount)
    {
        grid.ColumnSpacing = columnCount == 1 ? 0 : UiTokens.SpacingM;
        secondColumn.Width = columnCount >= 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        thirdColumn.Width = columnCount >= 3 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        fourthColumn.Width = columnCount >= 4 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        fifthColumn.Width = columnCount >= 5 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        var items = new[] { secondItem, thirdItem, fourthItem, fifthItem };
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            int row;
            int column;

            if (columnCount >= 5)
            {
                row = 0;
                column = index + 1;
            }
            else if (columnCount == 3)
            {
                row = (index + 1) / 3;
                column = (index + 1) % 3;
            }
            else if (columnCount == 2)
            {
                row = (index + 1) / 2;
                column = (index + 1) % 2;
            }
            else
            {
                row = index + 1;
                column = 0;
            }

            Grid.SetRow(item, row);
            Grid.SetColumn(item, column);
        }
    }
}

using System.Threading.Tasks;
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
        ConfigureTwoItemGrid(AutoSourcePathGrid, AutoSourcePathActionColumn, AutoSourceBrowseButton, compactForms, GridLength.Auto);
        ConfigureTwoItemGrid(AutoOutputPathGrid, AutoOutputPathActionColumn, AutoOutputBrowseButton, compactForms, GridLength.Auto);

        var optionColumnCount = width >= 900
            ? 4
            : width >= 640
                ? 2
                : 1;
        ConfigureFourItemGrid(
            AutoCompressionOptionsGrid,
            AutoCompressionTargetColumn,
            AutoCompressionProbesColumn,
            AutoCompressionWorkersColumn,
            AutoCompressionTargetVmafBox,
            AutoCompressionProbesBox,
            AutoCompressionWorkersBox,
            optionColumnCount);
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
        await PickAutoSourceFileAsync();
    }

    private async void AutoSourcePathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await PickAutoSourceFileAsync();
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
        await PickAutoOutputFolderAsync();
    }

    private async void AutoOutputPathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await PickAutoOutputFolderAsync();
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
        grid.ColumnSpacing = stacked ? 0 : 12;
        secondColumn.Width = stacked ? new GridLength(0) : expandedSecondColumnWidth;
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
    }

    private static void ConfigureFourItemGrid(
        Grid grid,
        ColumnDefinition secondColumn,
        ColumnDefinition thirdColumn,
        ColumnDefinition fourthColumn,
        FrameworkElement secondItem,
        FrameworkElement thirdItem,
        FrameworkElement fourthItem,
        int columnCount)
    {
        grid.ColumnSpacing = columnCount == 1 ? 0 : 12;
        secondColumn.Width = columnCount >= 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        thirdColumn.Width = columnCount >= 4 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        fourthColumn.Width = columnCount >= 4 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        Grid.SetRow(secondItem, columnCount == 1 ? 1 : 0);
        Grid.SetColumn(secondItem, columnCount == 1 ? 0 : 1);

        Grid.SetRow(thirdItem, columnCount >= 4 ? 0 : columnCount == 2 ? 1 : 2);
        Grid.SetColumn(thirdItem, columnCount >= 4 ? 2 : 0);

        Grid.SetRow(fourthItem, columnCount >= 4 ? 0 : columnCount == 2 ? 1 : 3);
        Grid.SetColumn(fourthItem, columnCount >= 4 ? 3 : columnCount == 2 ? 1 : 0);
    }
}

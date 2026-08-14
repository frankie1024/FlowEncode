using System;
using System.Threading.Tasks;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FlowEncode.Controls.AudioProcessing;

public sealed partial class AudioProcessingView : UserControl
{
    private bool _interactionsInitialized;

    private AudioProcessingViewModel? ViewModel => DataContext as AudioProcessingViewModel;
    private AudioProcessingFormViewModel? FormViewModel => ViewModel?.Form;

    public AudioProcessingView()
    {
        InitializeComponent();
        Loaded += AudioProcessingView_Loaded;
    }

    public void ApplyLayout(bool compactForms, double width, Thickness contentPadding)
    {
        ContentStack.Padding = contentPadding;
        const bool stackPathActions = false;
        ConfigureTwoItemGrid(AudioSourcePathGrid, AudioSourcePathActionColumn, AudioSourceBrowseButton, stackPathActions, GridLength.Auto);
        ConfigureTwoItemGrid(AudioOutputPathGrid, AudioOutputPathActionColumn, AudioOutputBrowseButton, stackPathActions, GridLength.Auto);
        ConfigureOutputPathGrid();
        ConfigureThreeItemGrid(AudioProcessingActionGrid, AudioProcessingCancelColumn, AudioProcessingDeleteColumn, CancelAudioProcessingButton, DeleteAudioProcessingButton, compactForms);
        ConfigureAudioOptionsGrid(width >= 900 ? 3 : 1);
    }

    private void AudioProcessingView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_interactionsInitialized)
        {
            return;
        }

        _interactionsInitialized = true;
        AudioSourcePathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(AudioSourcePathTextBox_DoubleTapped), true);
        AudioOutputPathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(AudioOutputPathTextBox_DoubleTapped), true);
    }

    private async void BrowseAudioSourceButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(BrowseAudioSourceButton_Click), PickAudioSourceFileAsync);
    }

    private async void AudioSourcePathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await RunGuardedAsync(nameof(AudioSourcePathTextBox_DoubleTapped), PickAudioSourceFileAsync);
    }

    private async Task PickAudioSourceFileAsync()
    {
        var formViewModel = FormViewModel;
        if (formViewModel is null)
        {
            return;
        }

        var selectedWorkflow = formViewModel.SelectedAudioWorkflow?.Value;
        var preferredPattern = AudioSourceSupport.GetPreferredPickerPattern(selectedWorkflow);
        var filePath = WindowInteractionHelper.PickFilteredFilePath(
            WindowInteractionHelper.GetMainWindowHandle(),
            formViewModel.Texts.SourceHeader,
            formViewModel.AudioProcessingSourcePath,
            formViewModel.Texts.SupportedAudioFileTypeDescription(preferredPattern),
            preferredPattern,
            formViewModel.Texts.AllFilesTypeDescription);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            await WindowInteractionHelper.ApplyPickedPathAsync(AudioSourcePathTextBox, filePath, path => formViewModel.AudioProcessingSourcePath = path);
        }
    }

    private async void BrowseAudioOutputButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(BrowseAudioOutputButton_Click), PickAudioOutputAsync);
    }

    private async void AudioOutputPathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await RunGuardedAsync(nameof(AudioOutputPathTextBox_DoubleTapped), PickAudioOutputAsync);
    }

    private async Task PickAudioOutputAsync()
    {
        var formViewModel = FormViewModel;
        if (formViewModel is null)
        {
            return;
        }

        var dialogPath = string.IsNullOrWhiteSpace(formViewModel.AudioProcessingOutputPath)
            ? formViewModel.AudioProcessingSourcePath
            : formViewModel.AudioProcessingOutputPath;
        var folderPath = WindowInteractionHelper.PickFolderPath(
            WindowInteractionHelper.GetMainWindowHandle(),
            formViewModel.Texts.ChooseFolderButton,
            dialogPath);
        if (folderPath is not null)
        {
            await WindowInteractionHelper.ApplyPickedPathAsync(AudioOutputPathTextBox, folderPath, path => formViewModel.AudioProcessingOutputPath = path);
        }
    }

    private async void StartAudioProcessingButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(StartAudioProcessingButton_Click), StartAudioProcessingAsync);
    }

    private async Task StartAudioProcessingAsync()
    {
        var formViewModel = FormViewModel;
        if (formViewModel is null)
        {
            return;
        }

        var validationError = formViewModel.ValidateAudioProcessingForStart(out var existingOutputPath);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            await WindowInteractionHelper.ShowMessageAsync(
                XamlRoot,
                ActualTheme,
                formViewModel.Texts.OkButton,
                formViewModel.Texts.ErrorCannotStartAudioProcessingTitle,
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

        var error = await formViewModel.StartAudioProcessingAsync();
        if (!string.IsNullOrWhiteSpace(error))
        {
            await WindowInteractionHelper.ShowMessageAsync(
                XamlRoot,
                ActualTheme,
                formViewModel.Texts.OkButton,
                formViewModel.Texts.ErrorCannotStartAudioProcessingTitle,
                error);
        }
    }

    private Task RunGuardedAsync(string actionName, Func<Task> action)
    {
        var texts = ViewModel?.Texts;
        return UiActionGuard.RunAsync(
            this,
            nameof(AudioProcessingView),
            actionName,
            texts?.ErrorCannotStartAudioProcessingTitle ?? "无法启动音频处理",
            texts?.OkButton ?? "确定",
            action);
    }

    private void CancelAudioProcessingButton_Click(object sender, RoutedEventArgs e)
    {
        FormViewModel?.CancelAudioProcessing();
    }

    private void DeleteAudioProcessingButton_Click(object sender, RoutedEventArgs e)
    {
        FormViewModel?.ClearAudioProcessingTask();
    }

    private static void ConfigureTwoItemGrid(
        Grid grid,
        ColumnDefinition secondColumn,
        FrameworkElement secondItem,
        bool stacked,
        GridLength expandedSecondColumnWidth)
    {
        grid.ColumnSpacing = stacked ? 0 : UiTokens.SpacingM;
        grid.RowSpacing = stacked ? UiTokens.SpacingS : 0;
        secondColumn.Width = stacked ? new GridLength(0) : expandedSecondColumnWidth;
        secondItem.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
    }

    private void ConfigureOutputPathGrid()
    {
        AudioOutputPathGrid.RowSpacing = UiTokens.SpacingM;
        Grid.SetRow(AudioOutputPreviewTextBlock, 1);
        Grid.SetColumn(AudioOutputPreviewTextBlock, 0);
        Grid.SetColumnSpan(AudioOutputPreviewTextBlock, 2);
    }

    private static void ConfigureThreeItemGrid(
        Grid grid,
        ColumnDefinition secondColumn,
        ColumnDefinition thirdColumn,
        FrameworkElement secondItem,
        FrameworkElement thirdItem,
        bool stacked)
    {
        grid.ColumnSpacing = stacked ? 0 : UiTokens.SpacingM;
        grid.RowSpacing = stacked ? UiTokens.SpacingM : 0;
        secondColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        thirdColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
        Grid.SetRow(thirdItem, stacked ? 2 : 0);
        Grid.SetColumn(thirdItem, stacked ? 0 : 2);
    }

    private void ConfigureAudioOptionsGrid(int columnCount)
    {
        var stacked = columnCount == 1;
        RebuildAutoRows(AudioProcessingOptionsGrid, stacked ? 3 : 1);
        AudioProcessingOptionsGrid.ColumnSpacing = columnCount == 1 ? 0 : UiTokens.SpacingM;
        AudioSecondaryOptionColumn.Width = columnCount >= 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AudioTertiaryOptionColumn.Width = columnCount >= 3 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        Grid.SetRow(AudioWorkflowComboBox, 0);
        Grid.SetColumn(AudioWorkflowComboBox, 0);

        Grid.SetRow(AudioEac3ToOutputFormatComboBox, stacked ? 1 : 0);
        Grid.SetColumn(AudioEac3ToOutputFormatComboBox, stacked ? 0 : 1);
        Grid.SetRow(AudioEac3ToAdditionalArgumentsTextBox, stacked ? 2 : 0);
        Grid.SetColumn(AudioEac3ToAdditionalArgumentsTextBox, stacked ? 0 : 2);

        Grid.SetRow(AudioOpusBitrateComboBox, stacked ? 1 : 0);
        Grid.SetColumn(AudioOpusBitrateComboBox, stacked ? 0 : 1);
        Grid.SetRow(AudioOpusMappingFamilyToggle, stacked ? 2 : 0);
        Grid.SetColumn(AudioOpusMappingFamilyToggle, stacked ? 0 : 2);
    }

    private static void RebuildAutoRows(Grid grid, int rowCount)
    {
        grid.RowDefinitions.Clear();
        for (var index = 0; index < rowCount; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
    }

}

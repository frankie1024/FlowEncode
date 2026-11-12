using System;
using System.Linq;
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
        RootScrollViewer.SizeChanged += (_, e) =>
        {
            ContentStack.MinHeight = Math.Max(0, Math.Floor(e.NewSize.Height));
            AudioTaskStatusPanel.ProgressHeight = Math.Clamp(e.NewSize.Height * 0.1, 64, 96);
            ScheduleDiagnosticsLayout();
        };
        Loaded += (_, _) => ScheduleDiagnosticsLayout();
        AudioFormPanel.SizeChanged += (_, _) => ScheduleDiagnosticsLayout();
        AudioTaskStatusPanel.SizeChanged += (_, _) => ScheduleDiagnosticsLayout();
        AudioResultsPanel.SizeChanged += (_, _) => ScheduleDiagnosticsLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        foreach (var grid in new[] { AudioProcessingOptionsGrid, AudioAdvancedOptionsGrid, DeewEncodingGrid, DeewSignalGrid, DeewFlagsGrid, DeewTempGrid })
            foreach (var child in grid.Children.OfType<FrameworkElement>())
                child.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => ApplyResponsiveLayout());
        AudioResultPathsTextBox.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) =>
            AudioResultsPanel.Visibility = string.IsNullOrWhiteSpace(AudioResultPathsTextBox.Text) ? Visibility.Collapsed : Visibility.Visible);
    }

    public void ApplyLayout(bool compactForms, int optionColumnCount, Thickness contentPadding)
    {
        ContentStack.Padding = contentPadding;
        const bool stackPathActions = false;
        ConfigureTwoItemGrid(AudioSourcePathGrid, AudioSourcePathActionColumn, AudioSourceBrowseButton, stackPathActions, GridLength.Auto);
        ConfigureTwoItemGrid(AudioOutputPathGrid, AudioOutputPathActionColumn, AudioOutputBrowseButton, stackPathActions, GridLength.Auto);
        ConfigureOutputPathGrid();
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        var width = AudioFormCard.ActualWidth - AudioFormCard.Padding.Left - AudioFormCard.Padding.Right;
        if (width <= 0) return;
        var splitPaths = width >= 840;
        AudioPathsGrid.RowSpacing = splitPaths ? 0 : UiTokens.SpacingM;
        AudioPathsGrid.ColumnDefinitions[1].Width = splitPaths ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(AudioOutputPathGrid, splitPaths ? 0 : 1);
        Grid.SetColumn(AudioOutputPathGrid, splitPaths ? 1 : 0);
        ConfigureVisibleFields(AudioProcessingOptionsGrid, width, 3, 190);
        ConfigureVisibleFields(AudioAdvancedOptionsGrid, width, 3, 190);
        ConfigureVisibleFields(DeewEncodingGrid, width, 3, 190);
        ConfigureVisibleFields(DeewSignalGrid, width, 3, 190);
        ConfigureVisibleFields(DeewFlagsGrid, width, 4, 200);
        ConfigureVisibleFields(DeewTempGrid, width, 2, 260);
        AudioProcessingActionBar.IsStacked = width < 480;
        var stackedStatus = width < 680;
        AudioStatusGrid.RowSpacing = stackedStatus ? UiTokens.SpacingM : 0;
        AudioStatusGrid.ColumnDefinitions[1].Width = stackedStatus ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (AudioStatusGrid.RowDefinitions.Count != (stackedStatus ? 2 : 1))
        {
            AudioStatusGrid.RowDefinitions.Clear();
            AudioStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (stackedStatus) AudioStatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        Grid.SetRow(AudioLogExpander, stackedStatus ? 1 : 0);
        Grid.SetColumn(AudioLogExpander, stackedStatus ? 0 : 1);
        ScheduleDiagnosticsLayout();
    }

    private void ScheduleDiagnosticsLayout() => DispatcherQueue.TryEnqueue(UpdateDiagnosticsLayout);

    private void UpdateDiagnosticsLayout()
    {
        if (!IsLoaded || AudioFormPanel.ActualHeight <= 0 || AudioAdvancedParametersExpander.IsExpanded) return;

        // Fit the default form. Expanding advanced options simply pushes these panes down.
        var stacked = AudioStatusGrid.ColumnDefinitions[1].Width.Value == 0;
        var top = AudioStatusGrid.TransformToVisual(ContentStack).TransformPoint(new Windows.Foundation.Point()).Y;
        var chrome = Math.Max(60, Math.Max(
            AudioCommandPreviewBox.ActualHeight - AudioCommandPreviewBox.PreviewHeight,
            AudioLogExpander.ActualHeight - AudioLogTextBox.ActualHeight));
        var resultsHeight = AudioResultsPanel.Visibility == Visibility.Visible ? AudioResultsPanel.ActualHeight : 0;
        var remaining = RootScrollViewer.ActualHeight - top - ContentStack.Padding.Bottom
            - AudioFormCard.Padding.Bottom - 2 - AudioWorkspaceGrid.RowSpacing - resultsHeight;
        var bodyHeight = stacked ? 120 : Math.Max(80, Math.Floor(remaining - chrome));
        AudioCommandPreviewBox.PreviewHeight = bodyHeight;
        AudioLogTextBox.Height = bodyHeight;
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

    private async void OpenAudioArtifact_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(OpenAudioArtifact_Click), () =>
        {
            var path = ViewModel?.Status.ArtifactPath((sender as FrameworkElement)?.Tag as string ?? "");
            if (path is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return Task.CompletedTask;
        });
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

    private static void ConfigureVisibleFields(Grid grid, double width, int maxColumns, double minFieldWidth)
    {
        var fields = grid.Children.OfType<FrameworkElement>().Where(field => field.Visibility == Visibility.Visible).ToArray();
        var columns = Math.Max(1, Math.Min(fields.Length, Math.Min(maxColumns, (int)((width + grid.ColumnSpacing) / (minFieldWidth + grid.ColumnSpacing)))));
        if (grid.ColumnDefinitions.Count != columns)
        {
            grid.ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var rowCount = Math.Max(1, (fields.Length + columns - 1) / columns);
        if (grid.RowDefinitions.Count != rowCount)
        {
            grid.RowDefinitions.Clear();
            for (var i = 0; i < rowCount; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var i = 0; i < fields.Length; i++)
        {
            Grid.SetRow(fields[i], i / columns);
            Grid.SetColumn(fields[i], i % columns);
        }
        // Collapsed controls must not leave an implicit row or column behind.
        foreach (var field in grid.Children.OfType<FrameworkElement>().Where(field => field.Visibility == Visibility.Collapsed))
        {
            Grid.SetRow(field, 0);
            Grid.SetColumn(field, 0);
        }
    }
}

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
    private bool _isAutoApplyingAdvancedExpanded;
    private bool _userToggledAdvancedExpanded;
    private double _availableWidth;
    private bool _stackedWorkspace;
    private bool _compactForms;

    private AudioProcessingViewModel? ViewModel => DataContext as AudioProcessingViewModel;
    private AudioProcessingFormViewModel? FormViewModel => ViewModel?.Form;

    public AudioProcessingView()
    {
        InitializeComponent();
        Loaded += AudioProcessingView_Loaded;
        RootScrollViewer.SizeChanged += (_, _) => ScheduleWorkspaceHeightRefresh();
        AudioAdvancedParametersExpander.Expanded += (_, _) => MarkAdvancedExpanderUserToggled();
        AudioAdvancedParametersExpander.Collapsed += (_, _) => MarkAdvancedExpanderUserToggled();
        foreach (var grid in new[] { AudioProcessingOptionsGrid, AudioAdvancedOptionsGrid, DeewEncodingGrid, DeewSignalGrid, DeewTrueHddGrid, DeewNamingGrid, DeewFlagsGrid, DeewTempGrid })
        {
            foreach (var child in grid.Children.OfType<FrameworkElement>())
            {
                child.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => ApplyResponsiveLayout());
            }
        }

        DeewPrimaryBitrateComboBox.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => ApplyResponsiveLayout());
    }

    public void ApplyLayout(double availableWidth, bool stackedWorkspace, bool compactForms, Thickness contentPadding)
    {
        _availableWidth = availableWidth;
        _stackedWorkspace = stackedWorkspace;
        _compactForms = compactForms;
        ContentStack.Padding = contentPadding;

        AudioWorkspaceGrid.ColumnSpacing = stackedWorkspace ? 0 : UiTokens.SpacingL;
        AudioWorkspaceGrid.RowSpacing = stackedWorkspace ? UiTokens.SpacingL : 0;
        AudioPrimaryColumn.Width = stackedWorkspace
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(52, GridUnitType.Star);
        AudioSecondaryColumn.Width = stackedWorkspace
            ? new GridLength(0)
            : new GridLength(48, GridUnitType.Star);
        AudioWorkspacePrimaryRow.Height = stackedWorkspace ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        AudioWorkspaceSecondaryRow.Height = stackedWorkspace ? GridLength.Auto : new GridLength(0);

        Grid.SetRow(AudioFormCard, 0);
        Grid.SetColumn(AudioFormCard, 0);
        Grid.SetColumnSpan(AudioFormCard, 1);
        Grid.SetRow(AudioMonitorCard, stackedWorkspace ? 1 : 0);
        Grid.SetColumn(AudioMonitorCard, stackedWorkspace ? 0 : 1);
        Grid.SetColumnSpan(AudioMonitorCard, stackedWorkspace ? 2 : 1);

        AudioFormScrollViewer.VerticalScrollBarVisibility = stackedWorkspace
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;

        if (!_userToggledAdvancedExpanded)
        {
            _isAutoApplyingAdvancedExpanded = true;
            try
            {
                AudioAdvancedParametersExpander.IsExpanded = !stackedWorkspace;
            }
            finally
            {
                _isAutoApplyingAdvancedExpanded = false;
            }
        }

        ApplyResponsiveLayout();
        ScheduleWorkspaceHeightRefresh();
    }

    private void MarkAdvancedExpanderUserToggled()
    {
        if (!_isAutoApplyingAdvancedExpanded)
        {
            _userToggledAdvancedExpanded = true;
        }
    }

    private void ApplyResponsiveLayout()
    {
        var totalWidth = _availableWidth - ContentStack.Padding.Left - ContentStack.Padding.Right;
        if (totalWidth <= 0)
        {
            return;
        }

        var formCardWidth = _stackedWorkspace
            ? totalWidth
            : (totalWidth - UiTokens.SpacingL) * 0.52;
        var innerWidth = Math.Max(220, formCardWidth - 34);

        ConfigureVisibleFields(AudioProcessingOptionsGrid, innerWidth, 3, 150);
        ConfigureVisibleFields(AudioAdvancedOptionsGrid, innerWidth, 3, 150);
        ConfigureVisibleFields(DeewEncodingGrid, innerWidth, 3, 150);
        ConfigureVisibleFields(DeewSignalGrid, innerWidth, 3, 150);
        ConfigureVisibleFields(DeewTrueHddGrid, innerWidth, 2, 180);
        ConfigureVisibleFields(DeewNamingGrid, innerWidth, 2, 180);
        ConfigureVisibleFields(DeewFlagsGrid, innerWidth, 3, 150);
        ConfigureVisibleFields(DeewTempGrid, innerWidth, 2, 180);
        AudioProcessingActionBar.IsStacked = _compactForms || innerWidth < 380;
    }

    private void ScheduleWorkspaceHeightRefresh() => DispatcherQueue.TryEnqueue(UpdateWorkspaceHeight);

    private void UpdateWorkspaceHeight()
    {
        if (_stackedWorkspace || RootScrollViewer.ActualHeight <= 0)
        {
            AudioWorkspaceGrid.Height = double.NaN;
            return;
        }

        var availableHeight = RootScrollViewer.ActualHeight
            - ContentStack.Padding.Top
            - ContentStack.Padding.Bottom;

        AudioWorkspaceGrid.Height = availableHeight >= 460
            ? Math.Floor(availableHeight)
            : double.NaN;
    }

    private void AudioProcessingView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_interactionsInitialized)
        {
            _interactionsInitialized = true;
            AudioSourcePathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(AudioSourcePathTextBox_DoubleTapped), true);
            AudioOutputPathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(AudioOutputPathTextBox_DoubleTapped), true);
        }

        ScheduleWorkspaceHeightRefresh();
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

    private void ConfigureVisibleFields(Grid grid, double width, int maxColumns, double minFieldWidth)
    {
        bool IsFieldVisible(FrameworkElement field)
        {
            if (field.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (ReferenceEquals(field, DeewPrimaryBitrateHost))
            {
                return DeewPrimaryBitrateComboBox.Visibility == Visibility.Visible;
            }

            return true;
        }

        var fields = grid.Children.OfType<FrameworkElement>().Where(IsFieldVisible).ToArray();
        var columns = Math.Max(1, Math.Min(fields.Length, Math.Min(maxColumns, (int)((width + grid.ColumnSpacing) / (minFieldWidth + grid.ColumnSpacing)))));
        if (grid.ColumnDefinitions.Count != columns)
        {
            grid.ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
        }

        var rowCount = Math.Max(1, (fields.Length + columns - 1) / columns);
        if (grid.RowDefinitions.Count != rowCount)
        {
            grid.RowDefinitions.Clear();
            for (var i = 0; i < rowCount; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
        }

        for (var i = 0; i < fields.Length; i++)
        {
            Grid.SetRow(fields[i], i / columns);
            Grid.SetColumn(fields[i], i % columns);
        }

        foreach (var field in grid.Children.OfType<FrameworkElement>().Where(field => !IsFieldVisible(field)))
        {
            Grid.SetRow(field, 0);
            Grid.SetColumn(field, 0);
        }
    }
}

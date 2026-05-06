using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FlowEncode.Controls.Overview;

public sealed partial class OverviewView : UserControl
{
    private bool _interactionsInitialized;
    private bool _isLoaded;
    private bool _selectionSyncInProgress;
    private double _lastWidth;

    internal IOverviewViewHost? Host { get; set; }

    private OverviewViewModel? ViewModel => DataContext as OverviewViewModel;
    private OverviewComposerViewModel? ComposerViewModel => ViewModel?.Composer;
    private OverviewQueueViewModel? QueueViewModel => ViewModel?.Queue;

    public OverviewView()
    {
        InitializeComponent();
        Loaded += OverviewView_Loaded;
        OverviewScrollViewer.SizeChanged += OverviewScrollViewer_SizeChanged;
    }

    public void ApplyLayout(double width, Thickness contentPadding)
    {
        if (width <= 0)
        {
            return;
        }

        _lastWidth = width;
        var stackedWorkspace = width < 1000;
        var compactForms = width < 700;

        OverviewContentStack.Padding = contentPadding;
        OverviewWorkspaceGrid.ColumnSpacing = stackedWorkspace ? 0 : 20;
        OverviewWorkspaceGrid.RowSpacing = stackedWorkspace ? 20 : 0;
        OverviewPrimaryColumn.Width = new GridLength(stackedWorkspace ? 1 : 0.85, GridUnitType.Star);
        OverviewSecondaryColumn.Width = stackedWorkspace
            ? new GridLength(0)
            : new GridLength(1.15, GridUnitType.Star);
        OverviewWorkspacePrimaryRow.Height = GridLength.Auto;
        OverviewWorkspaceSecondaryRow.Height = stackedWorkspace ? GridLength.Auto : new GridLength(0);

        Grid.SetRow(OverviewComposerPanel, 0);
        Grid.SetColumn(OverviewComposerPanel, 0);
        Grid.SetColumnSpan(OverviewComposerPanel, 1);
        Grid.SetRow(OverviewQueuePanel, stackedWorkspace ? 1 : 0);
        Grid.SetColumn(OverviewQueuePanel, stackedWorkspace ? 0 : 1);
        Grid.SetColumnSpan(OverviewQueuePanel, stackedWorkspace ? 2 : 1);

        if (stackedWorkspace)
        {
            ClearOverviewWorkspaceHeight();
        }
        else
        {
            OverviewComposerPanel.Height = double.NaN;
            OverviewQueuePanel.Height = double.NaN;
            ScheduleOverviewWorkspaceHeightRefresh(stackedWorkspace);
        }

        ConfigureTwoItemGrid(SourcePathGrid, SourcePathActionColumn, SourcePathBrowseButton, compactForms, GridLength.Auto);
        ConfigureTwoItemGrid(OutputPathGrid, OutputPathActionColumn, OutputPathBrowseButton, compactForms, GridLength.Auto);
        ConfigureTwoItemGrid(QueueActionGrid, QueueActionSecondaryColumn, QueueAndStartButton, compactForms, new GridLength(1, GridUnitType.Star));
        ConfigureThreeItemGrid(DraftEncoderRatePresetGrid, DraftRateColumn, DraftPresetColumn, DraftRateControlComboBox, DraftPresetComboBox, compactForms);
        ConfigureThreeItemGrid(DraftTuneProfileFormatValueGrid, DraftProfileColumn, DraftOutputFormatColumn, DraftProfileComboBox, DraftOutputFormatComboBox, compactForms);
        ConfigureTwoItemGrid(DraftRateValueGrid, DraftRateValueInputColumn, DraftRateValueEditorHost, compactForms || width < 1240, new GridLength(220));
        ConfigureTwoItemGrid(OverviewTemplateActionGrid, OverviewTemplateActionSecondaryColumn, SaveCurrentConfigurationButton, compactForms, GridLength.Auto);
    }

    public void SetOverviewTemplateSelection(TemplateLibraryItemViewModel? templateItem)
    {
        RunWithTemplateSelectionSync(() => OverviewTemplatePicker.SelectedItem = templateItem);
    }

    public void SetSavedTemplateQuickSelection(SavedTemplate? template)
    {
        RunWithTemplateSelectionSync(() => SavedTemplatesQuickSelect.SelectedItem = template);
    }

    private void OverviewView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_interactionsInitialized)
        {
            _interactionsInitialized = true;
            SourcePathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(SourcePathTextBox_DoubleTapped), true);
            OutputPathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(OutputPathTextBox_DoubleTapped), true);
        }

        _isLoaded = true;
        if (_lastWidth > 0)
        {
            ScheduleOverviewWorkspaceHeightRefresh(_lastWidth < 1000);
        }
    }

    private void OverviewScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isLoaded || _lastWidth <= 0)
        {
            return;
        }

        ScheduleOverviewWorkspaceHeightRefresh(_lastWidth < 1000);
    }

    private async void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        await PickSourceFileAsync();
    }

    private async void SourcePathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await PickSourceFileAsync();
    }

    private async Task PickSourceFileAsync()
    {
        var composerViewModel = ComposerViewModel;
        if (composerViewModel is null)
        {
            return;
        }

        var filePath = WindowInteractionHelper.PickFilteredFilePath(
            WindowInteractionHelper.GetMainWindowHandle(),
            composerViewModel.Texts.SourceHeader,
            composerViewModel.SourcePath,
            composerViewModel.Texts.SupportedSourceFileTypeDescription(InputSourceSupport.PreferredPickerPattern),
            InputSourceSupport.PreferredPickerPattern,
            composerViewModel.Texts.AllFilesTypeDescription);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            await WindowInteractionHelper.ApplyPickedPathAsync(SourcePathTextBox, filePath, path => composerViewModel.SourcePath = path);
        }
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        await PickOutputFolderAsync();
    }

    private async void OutputPathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await PickOutputFolderAsync();
    }

    private async Task PickOutputFolderAsync()
    {
        var composerViewModel = ComposerViewModel;
        if (composerViewModel is null)
        {
            return;
        }

        var dialogPath = string.IsNullOrWhiteSpace(composerViewModel.OutputPath)
            ? composerViewModel.SourcePath
            : composerViewModel.OutputPath;
        var folderPath = WindowInteractionHelper.PickFolderPath(
            WindowInteractionHelper.GetMainWindowHandle(),
            composerViewModel.Texts.ChooseFolderButton,
            dialogPath);
        if (folderPath is not null)
        {
            await WindowInteractionHelper.ApplyPickedPathAsync(OutputPathTextBox, folderPath, path => composerViewModel.OutputPath = path);
        }
    }

    private async void QueueOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        await QueueCurrentJobWithConfirmationAsync(startImmediately: false);
    }

    private async void QueueJobButton_Click(object sender, RoutedEventArgs e)
    {
        await QueueCurrentJobWithConfirmationAsync(startImmediately: true);
    }

    private async void OverviewTemplatePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var composerViewModel = ComposerViewModel;
        if (_selectionSyncInProgress || composerViewModel is null)
        {
            return;
        }

        if (OverviewTemplatePicker.SelectedItem is not TemplateLibraryItemViewModel templateItem
            || templateItem.UserTemplate is null)
        {
            return;
        }

        RunWithTemplateSelectionSync(() =>
        {
            SavedTemplatesQuickSelect.SelectedItem = templateItem.UserTemplate;
        });

        await composerViewModel.ApplyUserTemplateToEncodingDraftAsync(templateItem.UserTemplate);
    }

    private async void SaveCurrentConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        var composerViewModel = ComposerViewModel;
        if (composerViewModel is null || Host is null)
        {
            return;
        }

        var nameTextBox = new TextBox
        {
            Header = composerViewModel.Texts.TemplateNameHeader,
            Text = composerViewModel.DraftTemplateName ?? string.Empty
        };

        var notesTextBox = new TextBox
        {
            Header = composerViewModel.Texts.TemplateNotesHeader,
            AcceptsReturn = true,
            MinHeight = 96,
            Text = composerViewModel.DraftTemplateNotes ?? string.Empty,
            TextWrapping = TextWrapping.Wrap
        };

        var dialog = new ContentDialog
        {
            Title = composerViewModel.Texts.SaveCurrentConfigurationButton,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    nameTextBox,
                    notesTextBox
                }
            },
            PrimaryButtonText = composerViewModel.Texts.SaveButton,
            CloseButtonText = composerViewModel.Texts.CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };

        if (await WindowInteractionHelper.ShowContentDialogAsync(dialog, nameof(OverviewView)) != ContentDialogResult.Primary)
        {
            return;
        }

        composerViewModel.DraftTemplateName = nameTextBox.Text;
        composerViewModel.DraftTemplateNotes = notesTextBox.Text;

        try
        {
            await Host.SaveCurrentTemplateAsync();
        }
        catch (Exception ex)
        {
            TryWriteDiagnostic($"Failed to save template from save-as dialog. {ex.GetType().Name}: {ex.Message}");
            await ShowMessageAsync(composerViewModel.Texts.ErrorSaveFailedTitle, ex.Message);
        }
    }

    private async void ImportHdrButton_Click(object sender, RoutedEventArgs e)
    {
        var composerViewModel = ComposerViewModel;
        if (composerViewModel is null)
        {
            return;
        }

        var inputTextBox = new TextBox
        {
            Header = composerViewModel.Texts.ImportHdrDialogDescription,
            AcceptsReturn = true,
            MinHeight = 180,
            PlaceholderText = composerViewModel.Texts.ImportHdrDialogPlaceholder,
            TextWrapping = TextWrapping.Wrap
        };

        var dialog = new ContentDialog
        {
            Title = composerViewModel.Texts.ImportHdrDialogTitle,
            Content = inputTextBox,
            PrimaryButtonText = composerViewModel.Texts.ImportButton,
            CloseButtonText = composerViewModel.Texts.CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };

        if (await WindowInteractionHelper.ShowContentDialogAsync(dialog, nameof(OverviewView)) != ContentDialogResult.Primary)
        {
            return;
        }

        var error = composerViewModel.ImportHdrParametersFromText(inputTextBox.Text);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(composerViewModel.Texts.ErrorImportFailedTitle, error);
        }
    }

    private async void SavedTemplatesQuickSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var composerViewModel = ComposerViewModel;
        if (_selectionSyncInProgress || composerViewModel is null)
        {
            return;
        }

        if (SavedTemplatesQuickSelect.SelectedItem is not SavedTemplate template)
        {
            return;
        }

        var templateItem = composerViewModel.TemplateLibraryItems
            .FirstOrDefault(item => string.Equals(item.TemplateId, template.Id, StringComparison.OrdinalIgnoreCase));
        RunWithTemplateSelectionSync(() => OverviewTemplatePicker.SelectedItem = templateItem);
        Host?.SetTemplateLibrarySelection(templateItem);
        await composerViewModel.SelectUserTemplateAsync(template);
    }

    private async void StartJobMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (!TryGetJobFromMenu(sender, out var job) || queueViewModel is null)
        {
            return;
        }

        SelectQueueJobForSingleAction(job);
        var error = queueViewModel.PrioritizeJob(job);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotReorderTitle, error);
        }
    }

    private async void StartQueuedJobMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (!TryGetJobFromMenu(sender, out var job) || queueViewModel is null)
        {
            return;
        }

        SelectQueueJobForSingleAction(job);
        var error = queueViewModel.StartJobNow(job);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotStartTitle, error);
        }
    }

    private async void MoveJobToTopMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (!TryGetJobFromMenu(sender, out var job) || queueViewModel is null)
        {
            return;
        }

        SelectQueueJobForSingleAction(job);
        var error = queueViewModel.MoveJobToTop(job);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotReorderTitle, error);
        }
    }

    private async void MoveJobUpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (!TryGetJobFromMenu(sender, out var job) || queueViewModel is null)
        {
            return;
        }

        SelectQueueJobForSingleAction(job);
        var error = queueViewModel.MoveJobUp(job);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotReorderTitle, error);
        }
    }

    private async void MoveJobDownMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (!TryGetJobFromMenu(sender, out var job) || queueViewModel is null)
        {
            return;
        }

        SelectQueueJobForSingleAction(job);
        var error = queueViewModel.MoveJobDown(job);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotReorderTitle, error);
        }
    }

    private async void MoveJobToBottomMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (!TryGetJobFromMenu(sender, out var job) || queueViewModel is null)
        {
            return;
        }

        SelectQueueJobForSingleAction(job);
        var error = queueViewModel.MoveJobToBottom(job);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotReorderTitle, error);
        }
    }

    private async void AbortJobMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (!TryGetJobFromMenu(sender, out var job) || queueViewModel is null || !job.CanCancel)
        {
            return;
        }

        SelectQueueJobForSingleAction(job);
        var confirmed = await ShowConfirmationAsync(
            queueViewModel.Texts.ConfirmCancelJobTitle,
            queueViewModel.Texts.ConfirmCancelJobMessage(job.SourceFileName, job.State),
            queueViewModel.Texts.ConfirmCancelJobButton,
            queueViewModel.Texts.CancelButton,
            ContentDialogButton.Close);

        if (!confirmed)
        {
            return;
        }

        await queueViewModel.CancelJobAsync(job);
    }

    private async void RestartJobMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (!TryGetJobFromMenu(sender, out var job) || queueViewModel is null)
        {
            return;
        }

        SelectQueueJobForSingleAction(job);
        var error = await queueViewModel.RestartJobAsync(job);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotRestartTitle, error);
        }
    }

    private async void DeleteJobMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (!TryGetJobFromMenu(sender, out var job) || queueViewModel is null)
        {
            return;
        }

        SelectQueueJobForSingleAction(job);
        var error = queueViewModel.RemoveJob(job);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotDeleteTitle, error);
            return;
        }

        SyncListSelectionFromViewModel();
    }

    private void SelectAllQueueJobsButton_Click(object sender, RoutedEventArgs e)
    {
        JobsList.SelectAll();
        SyncSelectedQueueJobs();
    }

    private void InvertQueueSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedJobs = JobsList.SelectedItems
            .OfType<EncodingJobItemViewModel>()
            .ToHashSet();

        JobsList.SelectedItems.Clear();
        var queueViewModel = QueueViewModel;
        if (queueViewModel is null)
        {
            return;
        }

        foreach (var job in queueViewModel.Jobs)
        {
            if (!selectedJobs.Contains(job))
            {
                JobsList.SelectedItems.Add(job);
            }
        }

        SyncSelectedQueueJobs();
    }

    private async void StartSelectedJobsButton_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (queueViewModel is null)
        {
            return;
        }

        SyncSelectedQueueJobs();
        var error = queueViewModel.StartSelectedJobsNow();
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotStartTitle, error);
        }
    }

    private async void CancelSelectedJobsButton_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (queueViewModel is null)
        {
            return;
        }

        SyncSelectedQueueJobs();
        if (queueViewModel.SelectedQueueJobCount == 0)
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotCancelTitle, queueViewModel.Texts.NoSelectedJobsError);
            return;
        }

        var confirmed = await ShowConfirmationAsync(
            queueViewModel.Texts.ConfirmCancelSelectedJobsTitle,
            queueViewModel.Texts.ConfirmCancelSelectedJobsMessage(
                queueViewModel.SelectedQueueJobCount,
                queueViewModel.SelectedCancelableQueueJobCount,
                queueViewModel.SelectedRunningJobCount,
                queueViewModel.SelectedQueuedJobCount),
            queueViewModel.Texts.ConfirmCancelSelectedJobsButton,
            queueViewModel.Texts.CancelButton,
            ContentDialogButton.Close);

        if (!confirmed)
        {
            return;
        }

        var error = queueViewModel.CancelSelectedJobs();
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotCancelTitle, error);
        }
    }

    private async void DeleteSelectedJobsButton_Click(object sender, RoutedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (queueViewModel is null)
        {
            return;
        }

        SyncSelectedQueueJobs();
        if (queueViewModel.SelectedQueueJobCount == 0)
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotDeleteTitle, queueViewModel.Texts.NoSelectedJobsError);
            return;
        }

        var confirmed = await ShowConfirmationAsync(
            queueViewModel.Texts.ConfirmDeleteSelectedJobsTitle,
            queueViewModel.Texts.ConfirmDeleteSelectedJobsMessage(
                queueViewModel.SelectedQueueJobCount,
                queueViewModel.SelectedRemovableQueueJobCount,
                queueViewModel.SelectedRunningJobCount),
            queueViewModel.Texts.ConfirmDeleteSelectedJobsButton,
            queueViewModel.Texts.CancelButton,
            ContentDialogButton.Close);

        if (!confirmed)
        {
            return;
        }

        var error = queueViewModel.RemoveSelectedJobs();
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(queueViewModel.Texts.ErrorCannotDeleteTitle, error);
            return;
        }

        SyncListSelectionFromViewModel();
    }

    private void JobsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var queueViewModel = QueueViewModel;
        if (queueViewModel is null)
        {
            return;
        }

        SyncSelectedQueueJobs();
        var activeJob = e.AddedItems
            .OfType<EncodingJobItemViewModel>()
            .LastOrDefault()
            ?? JobsList.SelectedItem as EncodingJobItemViewModel
            ?? JobsList.SelectedItems
                .OfType<EncodingJobItemViewModel>()
                .LastOrDefault();

        queueViewModel.SelectJob(activeJob);
    }

    private async void QueueHeaderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: not null } || Host is null)
        {
            return;
        }

        await Host.PersistSettingsAsync(refreshTemplateLibrary: false);
    }

    private void SyncSelectedQueueJobs()
    {
        if (QueueViewModel is not null)
        {
            QueueViewModel.UpdateSelectedQueueJobs(JobsList.SelectedItems.OfType<EncodingJobItemViewModel>());
        }
    }

    private void SelectQueueJobForSingleAction(EncodingJobItemViewModel job)
    {
        if (!JobsList.SelectedItems.Contains(job))
        {
            JobsList.SelectedItems.Add(job);
        }

        SyncSelectedQueueJobs();
        QueueViewModel?.SelectJob(job);
    }

    private void SyncListSelectionFromViewModel()
    {
        var queueViewModel = QueueViewModel;
        if (queueViewModel is null)
        {
            return;
        }

        var selectedJobs = JobsList.SelectedItems
            .OfType<EncodingJobItemViewModel>()
            .Where(job => queueViewModel.Jobs.Contains(job))
            .ToList();

        if (selectedJobs.Count == JobsList.SelectedItems.Count)
        {
            return;
        }

        JobsList.SelectedItems.Clear();
        foreach (var job in selectedJobs)
        {
            JobsList.SelectedItems.Add(job);
        }

        SyncSelectedQueueJobs();
    }

    private async Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText,
        ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        return await WindowInteractionHelper.ShowConfirmationAsync(
            XamlRoot,
            ActualTheme,
            title,
            message,
            primaryButtonText,
            closeButtonText,
            defaultButton);
    }

    private async Task QueueCurrentJobWithConfirmationAsync(bool startImmediately)
    {
        var composerViewModel = ComposerViewModel;
        var queueViewModel = QueueViewModel;
        if (composerViewModel is null || queueViewModel is null)
        {
            return;
        }

        var preflight = composerViewModel.AnalyzeCurrentJobForQueue();
        if (!string.IsNullOrWhiteSpace(preflight.ValidationError))
        {
            await ShowMessageAsync(composerViewModel.Texts.ErrorCannotQueueTitle, preflight.ValidationError);
            return;
        }

        if (preflight.RunningOutputConflict is not null)
        {
            await ShowMessageAsync(
                composerViewModel.Texts.ErrorCannotQueueTitle,
                composerViewModel.Texts.QueueOutputPathRunningConflictMessage(
                    preflight.RunningOutputConflict.SourceFileName,
                    preflight.BaseOutputPath));
            return;
        }

        if (preflight.DuplicateJob is not null)
        {
            var duplicateConfirmed = await ShowConfirmationAsync(
                composerViewModel.Texts.ConfirmDuplicateQueueJobTitle,
                composerViewModel.Texts.ConfirmDuplicateQueueJobMessage(
                    preflight.DuplicateJob.SourceFileName,
                    preflight.BaseOutputPath,
                    preflight.FinalOutputPath),
                composerViewModel.Texts.ConfirmDuplicateQueueJobButton,
                composerViewModel.Texts.CancelButton,
                ContentDialogButton.Close);

            if (!duplicateConfirmed)
            {
                return;
            }
        }

        var error = await composerViewModel.QueueCurrentJobAsync(startImmediately, preflight);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await ShowMessageAsync(composerViewModel.Texts.ErrorCannotQueueTitle, error);
            return;
        }

        if (queueViewModel.SelectedJob is not null)
        {
            JobsList.SelectedItems.Clear();
            JobsList.SelectedItems.Add(queueViewModel.SelectedJob);
        }

        SyncSelectedQueueJobs();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        await WindowInteractionHelper.ShowMessageAsync(
            XamlRoot,
            ActualTheme,
            viewModel.Texts.OkButton,
            title,
            message);
    }

    private void RunWithTemplateSelectionSync(Action action)
    {
        _selectionSyncInProgress = true;

        try
        {
            action();
        }
        finally
        {
            _selectionSyncInProgress = false;
        }
    }

    private void ScheduleOverviewWorkspaceHeightRefresh(bool stackedWorkspace)
    {
        DispatcherQueue.TryEnqueue(() => UpdateOverviewWorkspaceHeight(stackedWorkspace));
    }

    private void UpdateOverviewWorkspaceHeight(bool stackedWorkspace)
    {
        if (stackedWorkspace || OverviewScrollViewer.Visibility != Visibility.Visible)
        {
            ClearOverviewWorkspaceHeight();
            return;
        }

        if (OverviewScrollViewer.ActualHeight <= 0)
        {
            return;
        }

        var availableHeight = OverviewScrollViewer.ActualHeight
            - OverviewContentStack.Padding.Top
            - OverviewContentStack.Padding.Bottom;

        if (availableHeight <= 0)
        {
            return;
        }

        var naturalPanelHeight = Math.Max(OverviewComposerPanel.ActualHeight, OverviewQueuePanel.ActualHeight);
        var targetHeight = Math.Max(Math.Ceiling(availableHeight), Math.Ceiling(naturalPanelHeight));
        OverviewComposerPanel.Height = targetHeight;
        OverviewQueuePanel.Height = targetHeight;
    }

    private void ClearOverviewWorkspaceHeight()
    {
        OverviewComposerPanel.Height = double.NaN;
        OverviewQueuePanel.Height = double.NaN;
    }

    private void TryWriteDiagnostic(string message)
    {
        try
        {
            var paths = App.GetService<LocalAppPaths>();
            AppDiagnosticsLog.Write(paths, nameof(OverviewView), message);
        }
        catch
        {
        }
    }

    private static bool TryGetJobFromMenu(object sender, out EncodingJobItemViewModel job)
    {
        job = null!;

        if (sender is MenuFlyoutItem { CommandParameter: EncodingJobItemViewModel parameter })
        {
            job = parameter;
            return true;
        }

        return false;
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

    private static void ConfigureThreeItemGrid(
        Grid grid,
        ColumnDefinition secondColumn,
        ColumnDefinition thirdColumn,
        FrameworkElement secondItem,
        FrameworkElement thirdItem,
        bool stacked)
    {
        grid.ColumnSpacing = stacked ? 0 : 12;
        secondColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        thirdColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
        Grid.SetRow(thirdItem, stacked ? 2 : 0);
        Grid.SetColumn(thirdItem, stacked ? 0 : 2);
    }
}

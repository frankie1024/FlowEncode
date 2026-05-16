using System;
using System.Threading.Tasks;
using FlowEncode.Controls.Shared;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FlowEncode.Controls.BluRayDemux;

public sealed partial class BluRayDemuxView : UserControl
{
    private const double BluRayPlaylistListMinHeight = 280;
    private const double BluRayPlaylistListMaxHeightCompact = 560;
    private const double BluRayPlaylistListMaxHeightWide = 1120;
    private const double BluRayTrackListMinHeight = 260;
    private const double BluRayTrackListMaxHeightCompact = 420;
    private const double BluRayTrackListMaxHeightWide = 620;

    private bool _interactionsInitialized;
    private bool _isStackedWorkspaceLayout;

    private BluRayDemuxViewModel? ViewModel => DataContext as BluRayDemuxViewModel;
    private BluRayDemuxDiscViewModel? DiscViewModel => ViewModel?.Disc;
    private BluRayDemuxTaskViewModel? TaskViewModel => ViewModel?.Task;

    public BluRayDemuxView()
    {
        InitializeComponent();
        Loaded += BluRayDemuxView_Loaded;
    }

    public void ApplyLayout(bool stackedWorkspace, bool compactForms, Thickness contentPadding)
    {
        _isStackedWorkspaceLayout = stackedWorkspace;
        ContentStack.Padding = contentPadding;
        WorkspaceGrid.ColumnSpacing = stackedWorkspace ? 0 : 20;
        WorkspaceGrid.RowSpacing = stackedWorkspace ? 20 : 0;
        PrimaryColumn.Width = new GridLength(stackedWorkspace ? 1 : 0.92, GridUnitType.Star);
        SecondaryColumn.Width = stackedWorkspace ? new GridLength(0) : new GridLength(1.08, GridUnitType.Star);
        WorkspacePrimaryRow.Height = GridLength.Auto;
        WorkspaceSecondaryRow.Height = stackedWorkspace ? GridLength.Auto : new GridLength(0);

        Grid.SetRow(PrimaryPanel, 0);
        Grid.SetColumn(PrimaryPanel, 0);
        Grid.SetColumnSpan(PrimaryPanel, 1);
        Grid.SetRow(SecondaryPanel, stackedWorkspace ? 1 : 0);
        Grid.SetColumn(SecondaryPanel, stackedWorkspace ? 0 : 1);
        Grid.SetColumnSpan(SecondaryPanel, stackedWorkspace ? 2 : 1);

        ConfigureTwoItemGrid(BluRaySourcePathGrid, BluRaySourcePathActionColumn, BluRaySourceBrowseButton, compactForms, GridLength.Auto);
        ConfigureTwoItemGrid(BluRayOutputPathGrid, BluRayOutputPathActionColumn, BluRayOutputBrowseButton, compactForms, GridLength.Auto);
        ConfigureTwoItemGrid(BluRayBackendActionGrid, BluRayBackendActionColumn, ScanBluRayButton, compactForms, GridLength.Auto);
        ConfigureTwoItemGrid(BluRayTrackHeaderGrid, BluRayTrackHeaderActionColumn, BluRayTrackSelectionActionsPanel, compactForms, GridLength.Auto);
        ConfigureThreeItemGrid(BluRayDemuxActionGrid, BluRayDemuxActionCancelColumn, BluRayDemuxActionClearColumn, CancelBluRayDemuxButton, ClearBluRayDemuxButton, compactForms);
        ApplyScrollableRegions(stackedWorkspace);
        ScheduleWorkspaceHeightRefresh();
    }

    private void BluRayDemuxView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_interactionsInitialized)
        {
            return;
        }

        _interactionsInitialized = true;
        BluRaySourcePathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(BluRaySourcePathTextBox_DoubleTapped), true);
        BluRayOutputPathTextBox.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(BluRayOutputPathTextBox_DoubleTapped), true);
        SecondaryPanel.SizeChanged += SecondaryPanel_SizeChanged;
    }

    private void ApplyScrollableRegions(bool stackedWorkspace)
    {
        var rootHeight = RootScrollViewer.ActualHeight > 0 ? RootScrollViewer.ActualHeight : ActualHeight;
        var playlistMaxHeight = stackedWorkspace
            ? (rootHeight > 0
                ? Math.Clamp(rootHeight * 0.44, BluRayPlaylistListMinHeight, BluRayPlaylistListMaxHeightCompact)
                : BluRayPlaylistListMaxHeightCompact)
            : double.PositiveInfinity;
        var trackMaxHeight = rootHeight > 0
            ? Math.Clamp(rootHeight * (stackedWorkspace ? 0.34 : 0.42), BluRayTrackListMinHeight, stackedWorkspace ? BluRayTrackListMaxHeightCompact : BluRayTrackListMaxHeightWide)
            : (stackedWorkspace ? BluRayTrackListMaxHeightCompact : BluRayTrackListMaxHeightWide);

        BluRayPlaylistListView.MaxHeight = playlistMaxHeight;
        BluRayTrackListView.MaxHeight = trackMaxHeight;
    }

    private void SecondaryPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleWorkspaceHeightRefresh();
    }

    private void ScheduleWorkspaceHeightRefresh()
    {
        DispatcherQueue.TryEnqueue(UpdateWorkspaceHeight);
    }

    private void UpdateWorkspaceHeight()
    {
        if (_isStackedWorkspaceLayout || Visibility != Visibility.Visible)
        {
            ClearWorkspaceHeight();
            return;
        }

        if (SecondaryPanel.ActualHeight <= 0)
        {
            return;
        }

        PrimaryPanel.Height = Math.Ceiling(SecondaryPanel.ActualHeight);
    }

    private void ClearWorkspaceHeight()
    {
        PrimaryPanel.Height = double.NaN;
    }

    private async void BrowseBluRaySourceButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(BrowseBluRaySourceButton_Click), PickBluRaySourceFolderAsync);
    }

    private async void BluRaySourcePathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await RunGuardedAsync(nameof(BluRaySourcePathTextBox_DoubleTapped), PickBluRaySourceFolderAsync);
    }

    private async Task PickBluRaySourceFolderAsync()
    {
        var discViewModel = DiscViewModel;
        if (discViewModel is null)
        {
            return;
        }

        var folderPath = WindowInteractionHelper.PickFolderPath(
            WindowInteractionHelper.GetMainWindowHandle(),
            discViewModel.Texts.ChooseFolderButton,
            discViewModel.BluRayDemuxSourcePath);
        if (folderPath is not null)
        {
            await WindowInteractionHelper.ApplyPickedPathAsync(BluRaySourcePathTextBox, folderPath, path => discViewModel.BluRayDemuxSourcePath = path);
        }
    }

    private async void BrowseBluRayOutputButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(BrowseBluRayOutputButton_Click), PickBluRayOutputFolderAsync);
    }

    private async void BluRayOutputPathTextBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await RunGuardedAsync(nameof(BluRayOutputPathTextBox_DoubleTapped), PickBluRayOutputFolderAsync);
    }

    private async Task PickBluRayOutputFolderAsync()
    {
        var discViewModel = DiscViewModel;
        if (discViewModel is null)
        {
            return;
        }

        var folderPath = WindowInteractionHelper.PickFolderPath(
            WindowInteractionHelper.GetMainWindowHandle(),
            discViewModel.Texts.ChooseFolderButton,
            discViewModel.BluRayDemuxOutputPath);
        if (folderPath is not null)
        {
            await WindowInteractionHelper.ApplyPickedPathAsync(BluRayOutputPathTextBox, folderPath, path => discViewModel.BluRayDemuxOutputPath = path);
        }
    }

    private async void ScanBluRayButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(
            nameof(ScanBluRayButton_Click),
            () => DiscViewModel?.ScanBluRayDiscAsync() ?? Task.CompletedTask);
    }

    private async void BluRayPlaylistListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunGuardedAsync(
            nameof(BluRayPlaylistListView_SelectionChanged),
            () => DiscViewModel?.LoadSelectedBluRayPlaylistAsync() ?? Task.CompletedTask);
    }

    private async void StartBluRayDemuxButton_Click(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(nameof(StartBluRayDemuxButton_Click), StartBluRayDemuxAsync);
    }

    private async Task StartBluRayDemuxAsync()
    {
        var taskViewModel = TaskViewModel;
        if (taskViewModel is null)
        {
            return;
        }

        var validationError = taskViewModel.ValidateBluRayDemuxForStart();
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            await WindowInteractionHelper.ShowMessageAsync(
                XamlRoot,
                ActualTheme,
                taskViewModel.Texts.OkButton,
                taskViewModel.Texts.ErrorCannotStartBluRayDemuxTitle,
                validationError);
            return;
        }

        var error = await taskViewModel.StartBluRayDemuxAsync();
        if (!string.IsNullOrWhiteSpace(error))
        {
            await WindowInteractionHelper.ShowMessageAsync(
                XamlRoot,
                ActualTheme,
                taskViewModel.Texts.OkButton,
                taskViewModel.Texts.ErrorCannotStartBluRayDemuxTitle,
                error);
        }
    }

    private Task RunGuardedAsync(string actionName, Func<Task> action)
    {
        var texts = ViewModel?.Texts;
        return UiActionGuard.RunAsync(
            this,
            nameof(BluRayDemuxView),
            actionName,
            texts?.ErrorCannotStartBluRayDemuxTitle ?? "Blu-ray demux failed",
            texts?.OkButton ?? "OK",
            action);
    }

    private void CancelBluRayDemuxButton_Click(object sender, RoutedEventArgs e)
    {
        TaskViewModel?.CancelBluRayDemux();
    }

    private void ClearBluRayDemuxButton_Click(object sender, RoutedEventArgs e)
    {
        TaskViewModel?.ClearBluRayDemuxTask();
    }

    private void SelectAllBluRayTracksButton_Click(object sender, RoutedEventArgs e)
    {
        TaskViewModel?.SelectAllBluRayTracks();
    }

    private void InvertBluRayTracksButton_Click(object sender, RoutedEventArgs e)
    {
        TaskViewModel?.InvertBluRayTrackSelection();
    }

    private void BluRayTrackListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        TaskViewModel?.ToggleBluRayTrackSelection(e.ClickedItem as BluRayTrackItemViewModel);
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

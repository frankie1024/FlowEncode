using System;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Controls.Shared;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FlowEncode.Controls.BluRayDemux;

public sealed partial class BluRayDemuxView : UserControl
{
    private const double BluRayPlaylistListMinHeight = 140;
    private const double BluRayPlaylistListMaxHeightCompact = 360;
    private const double BluRayTrackListMinHeight = 140;
    private const double BluRayTrackListMaxHeightCompact = 420;
    private const double BluRayTrackListMaxHeightWide = 620;

    private bool _interactionsInitialized;
    private bool _isStackedWorkspaceLayout;
    private bool _heightRefreshQueued;

    private BluRayDemuxViewModel? ViewModel => DataContext as BluRayDemuxViewModel;
    private BluRayDemuxDiscViewModel? DiscViewModel => ViewModel?.Disc;
    private BluRayDemuxTaskViewModel? TaskViewModel => ViewModel?.Task;

    public BluRayDemuxView()
    {
        InitializeComponent();
        Loaded += BluRayDemuxView_Loaded;
        RootScrollViewer.SizeChanged += (_, _) => ScheduleWorkspaceHeightRefresh();
    }

    public void ApplyLayout(bool stackedWorkspace, bool compactForms, Thickness contentPadding)
    {
        _isStackedWorkspaceLayout = stackedWorkspace;
        ContentStack.Padding = contentPadding;
        WorkspaceGrid.ColumnSpacing = stackedWorkspace ? 0 : UiTokens.SpacingL;
        WorkspaceGrid.RowSpacing = stackedWorkspace ? UiTokens.SpacingL : 0;
        PrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        SecondaryColumn.Width = stackedWorkspace ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        WorkspacePrimaryRow.Height = GridLength.Auto;
        WorkspaceSecondaryRow.Height = stackedWorkspace ? GridLength.Auto : new GridLength(0);

        Grid.SetRow(PrimaryPanel, 0);
        Grid.SetColumn(PrimaryPanel, 0);
        Grid.SetColumnSpan(PrimaryPanel, 1);
        Grid.SetRow(SecondaryPanel, stackedWorkspace ? 1 : 0);
        Grid.SetColumn(SecondaryPanel, stackedWorkspace ? 0 : 1);
        Grid.SetColumnSpan(SecondaryPanel, stackedWorkspace ? 2 : 1);

        ConfigureTwoItemGrid(BluRaySourcePathGrid, BluRaySourcePathActionColumn, BluRaySourceBrowseButton, false, GridLength.Auto);
        ConfigureTwoItemGrid(BluRayOutputPathGrid, BluRayOutputPathActionColumn, BluRayOutputBrowseButton, false, GridLength.Auto);
        BluRaySourcePathGrid.RowSpacing = 0;
        BluRayOutputPathGrid.RowSpacing = UiTokens.SpacingXS;
        ConfigureTwoItemGrid(BluRayBackendActionGrid, BluRayBackendActionColumn, ScanBluRayButton, compactForms, GridLength.Auto);
        ConfigureTwoItemGrid(BluRayTrackHeaderGrid, BluRayTrackHeaderActionColumn, BluRayTrackSelectionActionsPanel, compactForms, GridLength.Auto);
        BluRayBackendActionGrid.RowSpacing = compactForms ? UiTokens.SpacingM : 0;
        BluRayTrackHeaderGrid.RowSpacing = compactForms ? UiTokens.SpacingM : 0;
        BluRayDemuxActionBar.IsStacked = compactForms;
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
        BluRayPlaylistListView.MaxHeight = playlistMaxHeight;
        if (stackedWorkspace)
        {
            BluRayTrackListView.Height = double.NaN;
            BluRayTrackListView.MaxHeight = Math.Clamp(rootHeight * 0.34, BluRayTrackListMinHeight, BluRayTrackListMaxHeightCompact);
            return;
        }

        var visibleSections = BluRayExportStack.Children.OfType<FrameworkElement>()
            .Where(section => section.Visibility == Visibility.Visible).ToArray();
        var chromeHeight = SecondaryPanel.Padding.Top + SecondaryPanel.Padding.Bottom + 2
            + (Math.Max(0, visibleSections.Length - 1) * BluRayExportStack.Spacing)
            + visibleSections.Where(section => section != BluRayTrackListView).Sum(section => section.ActualHeight);
        var availableHeight = rootHeight - ContentStack.Padding.Top - ContentStack.Padding.Bottom;
        var listHeight = Math.Clamp(Math.Floor(availableHeight - chromeHeight - 2), BluRayTrackListMinHeight, BluRayTrackListMaxHeightWide);
        BluRayTrackListView.MaxHeight = BluRayTrackListMaxHeightWide;
        if (double.IsNaN(BluRayTrackListView.Height) || Math.Abs(BluRayTrackListView.Height - listHeight) > 1)
        {
            BluRayTrackListView.Height = listHeight;
        }
    }

    private void SecondaryPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleWorkspaceHeightRefresh();
    }

    private void ScheduleWorkspaceHeightRefresh()
    {
        if (_heightRefreshQueued) return;
        _heightRefreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _heightRefreshQueued = false;
            UpdateWorkspaceHeight();
        });
    }

    private void UpdateWorkspaceHeight()
    {
        if (RootScrollViewer.ActualHeight <= 0) return;
        ApplyScrollableRegions(_isStackedWorkspaceLayout);
        if (_isStackedWorkspaceLayout || Visibility != Visibility.Visible)
        {
            ClearWorkspaceHeight();
            return;
        }

        if (SecondaryPanel.ActualHeight <= 0)
        {
            return;
        }

        var availableHeight = RootScrollViewer.ActualHeight - ContentStack.Padding.Top - ContentStack.Padding.Bottom;
        var targetHeight = Math.Max(Math.Floor(availableHeight), Math.Ceiling(SecondaryPanel.ActualHeight));
        if (double.IsNaN(PrimaryPanel.Height) || Math.Abs(PrimaryPanel.Height - targetHeight) > 1)
        {
            PrimaryPanel.Height = targetHeight;
        }
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
            texts?.ErrorCannotStartBluRayDemuxTitle ?? "无法启动蓝光解复用",
            texts?.OkButton ?? "确定",
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
        grid.ColumnSpacing = stacked ? 0 : UiTokens.SpacingM;
        secondColumn.Width = stacked ? new GridLength(0) : expandedSecondColumnWidth;
        Grid.SetRow(secondItem, stacked ? 1 : 0);
        Grid.SetColumn(secondItem, stacked ? 0 : 1);
    }

}

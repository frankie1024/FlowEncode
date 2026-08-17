using System;
using System.Threading.Tasks;
using FlowEncode.Controls.Shared;
using FlowEncode.Domain;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FlowEncode.Controls.Settings;

public sealed partial class SetupGuideOverlayView : UserControl
{
    private const double SetupGuideScrollEdgeTolerance = 24.0;
    private static readonly TimeSpan SetupGuideWheelPageTurnCooldown = TimeSpan.FromMilliseconds(280);

    private bool _isInteractionInitialized;
    private SetupGuideScrollAnchor _pendingSetupGuideScrollAnchor;
    private SetupGuideWheelDirection _armedSetupGuideWheelDirection;
    private int _armedSetupGuideWheelCardIndex = -1;
    private int _lastSelectedSetupGuideCardIndex = -1;
    private DateTimeOffset _suppressSetupGuideWheelUntilUtc = DateTimeOffset.MinValue;

    private enum SetupGuideScrollAnchor
    {
        None,
        Top,
        Bottom
    }

    private enum SetupGuideWheelDirection
    {
        None,
        Up,
        Down
    }

    internal IShellNavigationHost? Host { get; set; }

    private SetupGuideViewModel? ViewModel => DataContext as SetupGuideViewModel;

    public SetupGuideOverlayView()
    {
        InitializeComponent();
        Loaded += SetupGuideOverlayView_Loaded;
        SizeChanged += SetupGuideOverlayView_SizeChanged;
        KeyDown += SetupGuideOverlayView_KeyDown;
        RegisterPropertyChangedCallback(VisibilityProperty, SetupGuideOverlayView_VisibilityChanged);
    }

    public void RefreshLayout()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var overlayPadding = ActualWidth < 960 ? UiTokens.SpacingL : UiTokens.SpacingXL;
        SetupGuideDialogCard.MaxHeight = Math.Max(360.0, ActualHeight - (overlayPadding * 2));
        SetupGuideDialogCard.Padding = ActualWidth < 960
            ? UiTokens.CardPadding
            : UiTokens.UniformThickness(UiTokens.SpacingXXL);

        var headerHeight = SetupGuideHeaderPanel.ActualHeight;
        var footerHeight = SetupGuideFooterPanel.ActualHeight;
        var dialogPadding = SetupGuideDialogCard.Padding.Top + SetupGuideDialogCard.Padding.Bottom;
        var chromeHeight = headerHeight + footerHeight + dialogPadding + 36.0;
        SetupGuideFlipView.MaxHeight = Math.Max(220.0, SetupGuideDialogCard.MaxHeight - chromeHeight);
    }

    private void SetupGuideOverlayView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInteractionInitialized)
        {
            ScheduleLayoutRefresh();
            return;
        }

        _isInteractionInitialized = true;
        SetupGuideFlipView.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(SetupGuideCardScrollViewer_PointerWheelChanged), true);
        ScheduleLayoutRefresh();
        FocusSetupGuideCloseButton();
    }

    private void SetupGuideOverlayView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshLayout();
    }

    private void SetupGuideOverlayView_VisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        if (Visibility == Visibility.Visible)
        {
            DispatcherQueue.TryEnqueue(FocusSetupGuideCloseButton);
        }
    }

    private async void SetupGuideOverlayView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || ViewModel is not { IsSetupGuideOpen: true })
        {
            return;
        }

        e.Handled = true;
        await DismissSetupGuideAsync();
    }

    private async void RefreshSetupGuideButton_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.RunGuardedAsync(
            viewModel,
            this,
            nameof(SetupGuideOverlayView),
            "Failed to refresh setup guide",
            viewModel.Texts.ErrorSaveFailedTitle,
            () => viewModel.RefreshSetupGuideAsync());
    }

    private async void CheckSetupDependencyUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.RunGuardedAsync(
            viewModel,
            this,
            nameof(SetupGuideOverlayView),
            "Failed to check setup dependency updates",
            viewModel.Texts.ErrorSaveFailedTitle,
            () => viewModel.CheckSetupDependencyUpdatesAsync(viewModel.IsSetupGuideOpen));
    }

    private async void CloseSetupGuideButton_Click(object sender, RoutedEventArgs e)
    {
        await DismissSetupGuideAsync();
    }

    private async Task DismissSetupGuideAsync()
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.RunGuardedAsync(
            viewModel,
            this,
            nameof(SetupGuideOverlayView),
            "Failed to close setup guide",
            viewModel.Texts.ErrorSaveSettingsFailedTitle,
            async () =>
            {
                var error = viewModel.DismissSetupGuide();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    await SetupDependencyInteractionHelper.ShowMessageAsync(
                        viewModel,
                        this,
                        viewModel.Texts.ErrorSaveSettingsFailedTitle,
                        error);
                    return;
                }

                Host?.NavigateToShellSection(MainShellSections.Dashboard);
            });
    }

    private void SetupGuidePreviousButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.MoveSetupGuidePrevious();
    }

    private async void SetupGuideNextButton_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.RunGuardedAsync(
            viewModel,
            this,
            nameof(SetupGuideOverlayView),
            "Failed to advance setup guide",
            viewModel.Texts.ErrorSaveSettingsFailedTitle,
            async () =>
            {
                var error = viewModel.AdvanceOrDismissSetupGuide();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    await SetupDependencyInteractionHelper.ShowMessageAsync(
                        viewModel,
                        this,
                        viewModel.Texts.ErrorSaveSettingsFailedTitle,
                        error);
                    return;
                }

                if (!viewModel.IsSetupGuideOpen)
                {
                    Host?.NavigateToShellSection(MainShellSections.Dashboard);
                }
            });
    }

    private void SetupGuideCardScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is not { IsSetupGuideOpen: true } viewModel || viewModel.SetupGuideCards.Count == 0)
        {
            return;
        }

        var scrollViewer = ResolveSetupGuideScrollViewer(sender as DependencyObject, e.OriginalSource as DependencyObject);
        if (scrollViewer is null)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _suppressSetupGuideWheelUntilUtc)
        {
            e.Handled = true;
            return;
        }

        var delta = e.GetCurrentPoint(scrollViewer).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var currentCardIndex = viewModel.SelectedSetupGuideCardIndex;
        var wheelDirection = delta < 0
            ? SetupGuideWheelDirection.Down
            : SetupGuideWheelDirection.Up;
        var isAtEdge = wheelDirection == SetupGuideWheelDirection.Down
            ? IsScrollViewerAtBottom(scrollViewer)
            : IsScrollViewerAtTop(scrollViewer);

        if (!isAtEdge)
        {
            ResetSetupGuideWheelArmState();
            return;
        }

        var isArmedForCurrentPage = _armedSetupGuideWheelDirection == wheelDirection
            && _armedSetupGuideWheelCardIndex == currentCardIndex;

        if (!isArmedForCurrentPage)
        {
            ArmSetupGuideWheel(wheelDirection, currentCardIndex);
            return;
        }

        if (wheelDirection == SetupGuideWheelDirection.Down && viewModel.CanMoveSetupGuideNext)
        {
            _pendingSetupGuideScrollAnchor = SetupGuideScrollAnchor.Top;
            ResetSetupGuideWheelArmState();
            _suppressSetupGuideWheelUntilUtc = DateTimeOffset.UtcNow + SetupGuideWheelPageTurnCooldown;
            viewModel.MoveSetupGuideNext();
            e.Handled = true;
            return;
        }

        if (wheelDirection == SetupGuideWheelDirection.Up && viewModel.CanMoveSetupGuidePrevious)
        {
            _pendingSetupGuideScrollAnchor = SetupGuideScrollAnchor.Bottom;
            ResetSetupGuideWheelArmState();
            _suppressSetupGuideWheelUntilUtc = DateTimeOffset.UtcNow + SetupGuideWheelPageTurnCooldown;
            viewModel.MoveSetupGuidePrevious();
            e.Handled = true;
        }
    }

    private async void SetupGuideFlipView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ResetSetupGuideWheelArmState();

        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.RunGuardedAsync(
            viewModel,
            this,
            nameof(SetupGuideOverlayView),
            "Failed to synchronize setup guide page scroll",
            viewModel.Texts.ErrorSaveFailedTitle,
            async () =>
            {
                await Task.Yield();
                AnimateSelectedSetupGuideCard();

                if (_pendingSetupGuideScrollAnchor == SetupGuideScrollAnchor.None || SetupGuideFlipView.SelectedIndex < 0)
                {
                    ScheduleLayoutRefresh();
                    return;
                }

                var targetAnchor = _pendingSetupGuideScrollAnchor;
                _pendingSetupGuideScrollAnchor = SetupGuideScrollAnchor.None;

                await Task.Yield();
                SetupGuideFlipView.UpdateLayout();

                if (SetupGuideFlipView.ContainerFromIndex(SetupGuideFlipView.SelectedIndex) is not DependencyObject container)
                {
                    ScheduleLayoutRefresh();
                    return;
                }

                var scrollViewer = FindDescendant<ScrollViewer>(container);
                if (scrollViewer is null)
                {
                    ScheduleLayoutRefresh();
                    return;
                }

                var verticalOffset = targetAnchor == SetupGuideScrollAnchor.Bottom
                    ? scrollViewer.ScrollableHeight
                    : 0;
                scrollViewer.ChangeView(null, verticalOffset, null, true);
                ScheduleLayoutRefresh();
            });
    }

    private async void InstallSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SetupDependencyKind kind } || ViewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.InstallSetupDependencyAsync(ViewModel, this, kind, nameof(SetupGuideOverlayView));
    }

    private async void ManualSelectSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SetupDependencyKind kind } || ViewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.ManualSelectSetupDependencyAsync(ViewModel, this, kind, nameof(SetupGuideOverlayView));
    }

    private async void ClearManualPinnedSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SetupDependencyKind kind } || ViewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.ClearManualPinnedSetupDependencyAsync(ViewModel, this, kind, nameof(SetupGuideOverlayView));
    }

    private async void UninstallSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SetupDependencyKind kind } || ViewModel is null)
        {
            return;
        }

        await SetupDependencyInteractionHelper.UninstallSetupDependencyAsync(ViewModel, this, kind, nameof(SetupGuideOverlayView));
    }

    private void OpenUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            SetupDependencyInteractionHelper.OpenUrl(url);
        }
    }

    private void ScheduleLayoutRefresh()
    {
        DispatcherQueue.TryEnqueue(RefreshLayout);
    }

    private static bool IsScrollViewerAtTop(ScrollViewer scrollViewer)
    {
        return scrollViewer.VerticalOffset <= SetupGuideScrollEdgeTolerance;
    }

    private static bool IsScrollViewerAtBottom(ScrollViewer scrollViewer)
    {
        return scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - SetupGuideScrollEdgeTolerance;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T matchedChild)
            {
                return matchedChild;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void ArmSetupGuideWheel(SetupGuideWheelDirection direction, int cardIndex)
    {
        _armedSetupGuideWheelDirection = direction;
        _armedSetupGuideWheelCardIndex = cardIndex;
    }

    private void AnimateSelectedSetupGuideCard()
    {
        var selectedIndex = SetupGuideFlipView.SelectedIndex;
        if (!_isInteractionInitialized || selectedIndex < 0
            || SetupGuideFlipView.ContainerFromIndex(selectedIndex) is not DependencyObject container)
        {
            return;
        }

        var cardContent = FindDescendantByName(container, "SetupGuideCardContent");
        if (cardContent is null)
        {
            return;
        }

        var offsetX = _lastSelectedSetupGuideCardIndex >= 0 && selectedIndex < _lastSelectedSetupGuideCardIndex
            ? -UiTokens.MotionFieldOffsetY * 2
            : UiTokens.MotionFieldOffsetY * 2;
        _lastSelectedSetupGuideCardIndex = selectedIndex;
        UiMotion.PlayHorizontalEntrance(cardContent, offsetX);
    }

    private void FocusSetupGuideCloseButton()
    {
        if (ViewModel is { IsSetupGuideOpen: true })
        {
            SetupGuideCloseButton.Focus(FocusState.Programmatic);
        }
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement { Name: var currentName } element
            && string.Equals(currentName, name, StringComparison.Ordinal))
        {
            return element;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var match = FindDescendantByName(VisualTreeHelper.GetChild(root, index), name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void ResetSetupGuideWheelArmState()
    {
        _armedSetupGuideWheelDirection = SetupGuideWheelDirection.None;
        _armedSetupGuideWheelCardIndex = -1;
    }

    private ScrollViewer? ResolveSetupGuideScrollViewer(DependencyObject? sender, DependencyObject? originalSource)
    {
        if (sender is ScrollViewer directScrollViewer)
        {
            return directScrollViewer;
        }

        var sourceScrollViewer = FindAncestor<ScrollViewer>(originalSource);
        if (sourceScrollViewer is not null)
        {
            return sourceScrollViewer;
        }

        if (SetupGuideFlipView.SelectedIndex < 0)
        {
            return null;
        }

        return SetupGuideFlipView.ContainerFromIndex(SetupGuideFlipView.SelectedIndex) is DependencyObject container
            ? FindDescendant<ScrollViewer>(container)
            : null;
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}

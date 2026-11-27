using System;
using System.Diagnostics;
using System.Globalization;
using FlowEncode.Application;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FlowEncode.Controls.Shared;

public sealed partial class TaskDiagnosticsPanel : UserControl
{
    public static readonly DependencyProperty CommandTitleProperty = DependencyProperty.Register(
        nameof(CommandTitle), typeof(string), typeof(TaskDiagnosticsPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LogTitleProperty = DependencyProperty.Register(
        nameof(LogTitle), typeof(string), typeof(TaskDiagnosticsPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CommandTextProperty = DependencyProperty.Register(
        nameof(CommandText), typeof(string), typeof(TaskDiagnosticsPanel), new PropertyMetadata(string.Empty, OnCommandStateChanged));

    public static readonly DependencyProperty CommandPlaceholderTextProperty = DependencyProperty.Register(
        nameof(CommandPlaceholderText), typeof(string), typeof(TaskDiagnosticsPanel), new PropertyMetadata(string.Empty, OnBindingPropertyChanged));

    public static readonly DependencyProperty LogTextProperty = DependencyProperty.Register(
        nameof(LogText), typeof(string), typeof(TaskDiagnosticsPanel), new PropertyMetadata(string.Empty, OnLogStateChanged));

    public static readonly DependencyProperty LogPlaceholderTextProperty = DependencyProperty.Register(
        nameof(LogPlaceholderText), typeof(string), typeof(TaskDiagnosticsPanel), new PropertyMetadata(string.Empty, OnLogStateChanged));

    public static readonly DependencyProperty IsCopyEnabledProperty = DependencyProperty.Register(
        nameof(IsCopyEnabled), typeof(bool), typeof(TaskDiagnosticsPanel), new PropertyMetadata(true, OnCommandStateChanged));

    public static readonly DependencyProperty TextsProperty = DependencyProperty.Register(
        nameof(Texts), typeof(AppText), typeof(TaskDiagnosticsPanel), new PropertyMetadata(null, OnCommandStateChanged));

    public static readonly DependencyProperty IsCollapsibleProperty = DependencyProperty.Register(
        nameof(IsCollapsible), typeof(bool), typeof(TaskDiagnosticsPanel), new PropertyMetadata(false, OnExpansionPropertyChanged));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(TaskDiagnosticsPanel), new PropertyMetadata(true, OnExpansionPropertyChanged));

    public static readonly DependencyProperty DefaultToLogTabProperty = DependencyProperty.Register(
        nameof(DefaultToLogTab), typeof(bool), typeof(TaskDiagnosticsPanel), new PropertyMetadata(false, OnDefaultTabChanged));

    public static readonly DependencyProperty PresentationStateProperty = DependencyProperty.Register(
        nameof(PresentationState), typeof(TaskPresentationState), typeof(TaskDiagnosticsPanel), new PropertyMetadata(TaskPresentationState.Idle, OnPresentationStateChanged));

    public static readonly DependencyProperty AutoSwitchToLogOnRunProperty = DependencyProperty.Register(
        nameof(AutoSwitchToLogOnRun), typeof(bool), typeof(TaskDiagnosticsPanel), new PropertyMetadata(true));

    public static readonly DependencyProperty BodyMinHeightProperty = DependencyProperty.Register(
        nameof(BodyMinHeight), typeof(double), typeof(TaskDiagnosticsPanel), new PropertyMetadata(96d));

    public static readonly DependencyProperty BodyMaxHeightProperty = DependencyProperty.Register(
        nameof(BodyMaxHeight), typeof(double), typeof(TaskDiagnosticsPanel), new PropertyMetadata(double.PositiveInfinity));

    private bool _isLogSelected;
    private int _copyStatusVersion;

    public TaskDiagnosticsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SyncTabVisibility();
            SyncExpansionState();
            SyncCopyState();
            Bindings.Update();
        };
    }

    public string CommandTitle { get => (string)GetValue(CommandTitleProperty); set => SetValue(CommandTitleProperty, value); }
    public string LogTitle { get => (string)GetValue(LogTitleProperty); set => SetValue(LogTitleProperty, value); }
    public string CommandText { get => (string)GetValue(CommandTextProperty); set => SetValue(CommandTextProperty, value); }
    public string CommandPlaceholderText { get => (string)GetValue(CommandPlaceholderTextProperty); set => SetValue(CommandPlaceholderTextProperty, value); }
    public string LogText { get => (string)GetValue(LogTextProperty); set => SetValue(LogTextProperty, value); }
    public string LogPlaceholderText { get => (string)GetValue(LogPlaceholderTextProperty); set => SetValue(LogPlaceholderTextProperty, value); }
    public bool IsCopyEnabled { get => (bool)GetValue(IsCopyEnabledProperty); set => SetValue(IsCopyEnabledProperty, value); }
    public AppText? Texts { get => (AppText?)GetValue(TextsProperty); set => SetValue(TextsProperty, value); }
    public bool IsCollapsible { get => (bool)GetValue(IsCollapsibleProperty); set => SetValue(IsCollapsibleProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public bool DefaultToLogTab { get => (bool)GetValue(DefaultToLogTabProperty); set => SetValue(DefaultToLogTabProperty, value); }
    public TaskPresentationState PresentationState { get => (TaskPresentationState)GetValue(PresentationStateProperty); set => SetValue(PresentationStateProperty, value); }
    public bool AutoSwitchToLogOnRun { get => (bool)GetValue(AutoSwitchToLogOnRunProperty); set => SetValue(AutoSwitchToLogOnRunProperty, value); }
    public double BodyMinHeight { get => (double)GetValue(BodyMinHeightProperty); set => SetValue(BodyMinHeightProperty, value); }
    public double BodyMaxHeight { get => (double)GetValue(BodyMaxHeightProperty); set => SetValue(BodyMaxHeightProperty, value); }

    public string ResolvedCommandPlaceholder =>
        !string.IsNullOrWhiteSpace(CommandPlaceholderText)
            ? CommandPlaceholderText
            : Texts?.CommandPreviewEmptyPlaceholder ?? "当前还没有可显示的命令。";

    private static void OnCommandStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (TaskDiagnosticsPanel)d;
        panel.HideCopyStatus();
        panel.SyncCopyState();
        panel.SyncExpansionState();
        panel.SyncEmptyState();
        panel.Bindings.Update();
    }

    private static void OnLogStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (TaskDiagnosticsPanel)d;
        panel.SyncEmptyState();
    }

    private static void OnBindingPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (TaskDiagnosticsPanel)d;
        panel.SyncEmptyState();
        panel.Bindings.Update();
    }

    private static void OnPresentationStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (TaskDiagnosticsPanel)d;
        if (!panel.AutoSwitchToLogOnRun)
        {
            return;
        }

        var oldState = (TaskPresentationState)e.OldValue;
        var newState = (TaskPresentationState)e.NewValue;
        if (newState == TaskPresentationState.Running && oldState != TaskPresentationState.Running)
        {
            panel._isLogSelected = true;
            panel.SyncTabVisibility();
        }
        else if (newState == TaskPresentationState.Idle
            && oldState != TaskPresentationState.Idle
            && !panel.DefaultToLogTab
            && string.IsNullOrWhiteSpace(panel.LogText))
        {
            panel._isLogSelected = false;
            panel.SyncTabVisibility();
        }
    }

    private static void OnExpansionPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TaskDiagnosticsPanel)d).SyncExpansionState();
    }

    private static void OnDefaultTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (TaskDiagnosticsPanel)d;
        panel._isLogSelected = (bool)e.NewValue;
        panel.SyncTabVisibility();
    }

    private void CommandTabButton_Click(object sender, RoutedEventArgs e)
    {
        _isLogSelected = false;
        if (IsCollapsible && !IsExpanded)
        {
            IsExpanded = true;
        }

        SyncTabVisibility();
    }

    private void LogTabButton_Click(object sender, RoutedEventArgs e)
    {
        _isLogSelected = true;
        if (IsCollapsible && !IsExpanded)
        {
            IsExpanded = true;
        }

        SyncTabVisibility();
    }

    private void CollapseToggleButton_Click(object sender, RoutedEventArgs e)
    {
        IsExpanded = !IsExpanded;
    }

    private void SyncTabVisibility()
    {
        if (CommandTabButton is null || LogTabButton is null || CommandTextBox is null || LogTextBox is null)
        {
            return;
        }

        CommandTabButton.IsChecked = !_isLogSelected;
        LogTabButton.IsChecked = _isLogSelected;
        CommandTextBox.Visibility = _isLogSelected ? Visibility.Collapsed : Visibility.Visible;
        LogTextBox.Visibility = _isLogSelected ? Visibility.Visible : Visibility.Collapsed;
        SyncEmptyState();
    }

    private void SyncEmptyState()
    {
        if (EmptyStateOverlay is null || EmptyStateIcon is null || EmptyStateTextBlock is null)
        {
            return;
        }

        var activeText = _isLogSelected ? LogText : CommandText;
        var isEmpty = string.IsNullOrWhiteSpace(activeText);
        EmptyStateOverlay.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (!isEmpty)
        {
            return;
        }

        EmptyStateIcon.Glyph = _isLogSelected ? "\uE9F9" : "\uE756";
        EmptyStateTextBlock.Text = _isLogSelected
            ? (!string.IsNullOrWhiteSpace(LogPlaceholderText) ? LogPlaceholderText : string.Empty)
            : ResolvedCommandPlaceholder;
    }

    private void SyncExpansionState()
    {
        if (BodyContainer is null || CollapseToggleButton is null || CollapseChevronIcon is null || BodyRow is null)
        {
            return;
        }

        CollapseToggleButton.Visibility = IsCollapsible ? Visibility.Visible : Visibility.Collapsed;
        var expanded = !IsCollapsible || IsExpanded;
        RootGrid.RowSpacing = expanded ? UiTokens.SpacingS : 0;
        BodyContainer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        BodyRow.Height = expanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        CollapseChevronIcon.Glyph = expanded ? "\uE70E" : "\uE70D";

        var isChinese = Texts?.IsChinese ?? CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var label = expanded
            ? (isChinese ? "折叠" : "Collapse")
            : (isChinese ? "展开" : "Expand");
        ToolTipService.SetToolTip(CollapseToggleButton, label);
        AutomationProperties.SetName(CollapseToggleButton, label);
    }

    private void SyncCopyState()
    {
        if (CopyCommandButton is null || CopyCommandLabel is null)
        {
            return;
        }

        var copyText = Texts?.CommandPreviewCopyButton ?? "复制命令";
        CopyCommandLabel.Text = copyText;
        CopyCommandButton.IsEnabled = IsCopyEnabled && !string.IsNullOrWhiteSpace(CommandText);
        ToolTipService.SetToolTip(CopyCommandButton, copyText);
        AutomationProperties.SetName(CopyCommandButton, copyText);
    }

    private async void CopyCommandButton_Click(object sender, RoutedEventArgs e)
    {
        var command = CommandText;
        if (string.IsNullOrWhiteSpace(command))
        {
            ShowCopyStatus(Texts?.CommandPreviewCopyUnavailableStatus ?? "当前没有可复制的命令。");
            SyncCopyState();
            return;
        }

        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(command);
            Clipboard.SetContent(dataPackage);
        }
        catch (Exception ex)
        {
            TryWriteClipboardFailure("SetContent", ex);
            ShowCopyStatus(Texts?.CommandPreviewCopyFailedStatus(ex.Message) ?? $"复制命令失败：{ex.Message}");
            return;
        }

        try
        {
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            TryWriteClipboardFailure("Flush", ex, AppDiagnosticSeverity.Warning);
        }

        var version = ++_copyStatusVersion;
        ShowCopyStatus(Texts?.CommandPreviewCopiedStatus ?? "命令已复制。");
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2.5));
        if (version == _copyStatusVersion)
        {
            HideCopyStatus();
        }
    }

    private void ShowCopyStatus(string status)
    {
        if (CopyStatusBadge is null)
        {
            return;
        }

        CopyStatusBadge.Text = status;
        CopyStatusBadge.Visibility = Visibility.Visible;
    }

    private void HideCopyStatus()
    {
        if (CopyStatusBadge is null)
        {
            return;
        }

        CopyStatusBadge.Text = string.Empty;
        CopyStatusBadge.Visibility = Visibility.Collapsed;
    }

    private static void TryWriteClipboardFailure(
        string operationName,
        Exception exception,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error)
    {
        try
        {
            App.GetService<IAppDiagnostics>().WriteException(
                nameof(TaskDiagnosticsPanel),
                $"Copy command: {operationName}",
                exception,
                severity);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write diagnostics clipboard error. {ex}");
        }
    }
}

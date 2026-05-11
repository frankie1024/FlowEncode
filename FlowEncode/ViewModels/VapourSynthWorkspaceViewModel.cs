using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public enum VapourSynthWorkspacePaneKind
{
    Left,
    Right
}

public sealed class VapourSynthWorkspaceViewModel : ObservableObject
{
    private readonly IVapourSynthWorkspaceService _workspaceService;
    private readonly AppLaunchActivation _launchActivation;
    private readonly ObservableCollection<VapourSynthWorkspaceTabViewModel> _tabs = [];
    private CancellationTokenSource? _sessionSaveCancellationTokenSource;
    private AppText _texts;
    private bool _isInitialized;
    private bool _isCompareMode;
    private VapourSynthWorkspacePaneKind _activePane = VapourSynthWorkspacePaneKind.Left;
    private VapourSynthWorkspaceTabViewModel? _activeTab;
    private VapourSynthWorkspaceTabViewModel? _leftTab;
    private VapourSynthWorkspaceTabViewModel? _rightTab;

    public VapourSynthWorkspaceViewModel(
        IVapourSynthWorkspaceService workspaceService,
        IAppSettingsService settingsService,
        AppLaunchActivation launchActivation)
    {
        _workspaceService = workspaceService;
        _launchActivation = launchActivation;
        _texts = new AppText(settingsService.Load().Language);
        Tabs = new ReadOnlyObservableCollection<VapourSynthWorkspaceTabViewModel>(_tabs);
    }

    public AppText Texts
    {
        get => _texts;
        private set => SetProperty(ref _texts, value);
    }

    public ReadOnlyObservableCollection<VapourSynthWorkspaceTabViewModel> Tabs { get; }

    public VapourSynthWorkspaceTabViewModel? ActiveTab
    {
        get => _activeTab;
        private set => SetProperty(ref _activeTab, value);
    }

    public VapourSynthWorkspaceTabViewModel? LeftTab
    {
        get => _leftTab;
        private set => SetProperty(ref _leftTab, value);
    }

    public VapourSynthWorkspaceTabViewModel? RightTab
    {
        get => _rightTab;
        private set => SetProperty(ref _rightTab, value);
    }

    public VapourSynthWorkspacePaneKind ActivePane
    {
        get => _activePane;
        private set => SetProperty(ref _activePane, value);
    }

    public bool IsCompareMode
    {
        get => _isCompareMode;
        private set => SetProperty(ref _isCompareMode, value);
    }

    public string EditorAssetsRootPath => _workspaceService.EditorAssetsRootPath;

    public bool IsInitialized => _isInitialized;

    public string DocumentTitle => ActiveTab?.TabTitle ?? Texts.VapourSynthWorkspaceTitle;

    public string DocumentPathText => ActiveTab?.DocumentPathText ?? Texts.VapourSynthPathPlaceholder;

    public string WorkspaceStatusText => ActiveTab?.WorkspaceStatusText ?? Texts.VapourSynthEditorReadyStatus;

    public Visibility WorkspaceStatusVisibility => ActiveTab?.WorkspaceStatusVisibility ?? Visibility.Collapsed;

    public string LogText => ActiveTab?.LogText ?? Texts.VapourSynthLogEmptyPlaceholder;

    public string EditorStatusText => ActiveTab?.EditorStatusText ?? Texts.VapourSynthEditorCursorStatus(1, 1, 1, 0, false);

    public string HeaderStatusText => WorkspaceStatusVisibility == Visibility.Visible
        ? $"{WorkspaceStatusText}   |   {EditorStatusText}"
        : EditorStatusText;

    public bool CanReload => ActiveTab?.CanReload ?? false;

    public Visibility DirtyBadgeVisibility => ActiveTab?.DirtyBadgeVisibility ?? Visibility.Collapsed;

    public string? CurrentFilePath => ActiveTab?.CurrentFilePath;

    public string CurrentContent => ActiveTab?.CurrentContent ?? string.Empty;

    public bool HasUnsavedChanges => ActiveTab?.HasUnsavedChanges ?? false;

    public bool HasAnyUnsavedChanges => _tabs.Any(static tab => tab.HasUnsavedChanges);

    public bool CanCompareTabs => _tabs.Count >= 2;

    public IReadOnlyList<VapourSynthWorkspaceTabViewModel> DirtyTabs =>
        _tabs.Where(static tab => tab.HasUnsavedChanges).ToArray();

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;

        var session = await _workspaceService.LoadSessionAsync();
        if (session is not null && session.Tabs.Count > 0)
        {
            await RestoreSessionAsync(session);
        }
        else if (_tabs.Count == 0)
        {
            var initialTab = await CreateNewTabAsync();
            ActivateTab(initialTab);
        }

        var launchFilePath = _launchActivation.RequestedVapourSynthFilePath;
        if (!string.IsNullOrWhiteSpace(launchFilePath))
        {
            await OpenDocumentAsync(launchFilePath);
        }
    }

    public async Task<VapourSynthWorkspaceTabViewModel> CreateNewTabAsync()
    {
        var tab = new VapourSynthWorkspaceTabViewModel(_workspaceService, new ShellSettingsAdapter(Texts.Language));
        await tab.CreateNewDocumentAsync();
        AddTab(tab);
        ActivateTab(tab);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
        return tab;
    }

    public async Task<VapourSynthWorkspaceTabViewModel> OpenDocumentAsync(string filePath)
    {
        var normalizedPath = NormalizePath(filePath);
        var existingTab = FindTabByPath(normalizedPath);
        if (existingTab is not null)
        {
            ActivateTab(existingTab);
            ScheduleSessionSave();
            return existingTab;
        }

        var tab = new VapourSynthWorkspaceTabViewModel(_workspaceService, new ShellSettingsAdapter(Texts.Language));
        await tab.OpenDocumentAsync(normalizedPath);
        AddTab(tab);
        ActivateTab(tab);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
        return tab;
    }

    public void ActivateTab(VapourSynthWorkspaceTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_tabs.Contains(tab))
        {
            return;
        }

        if (IsCompareMode)
        {
            if (ReferenceEquals(LeftTab, tab))
            {
                ActiveTab = tab;
                ActivePane = VapourSynthWorkspacePaneKind.Left;
            }
            else if (ReferenceEquals(RightTab, tab))
            {
                ActiveTab = tab;
                ActivePane = VapourSynthWorkspacePaneKind.Right;
            }
            else
            {
                var companion = ActivePane == VapourSynthWorkspacePaneKind.Left ? RightTab : LeftTab;
                SetCompareTabs(tab, companion ?? FindAdjacentTab(tab), tab);
            }
        }
        else
        {
            ActiveTab = tab;
            LeftTab = tab;
            RightTab = null;
            ActivePane = VapourSynthWorkspacePaneKind.Left;
        }

        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void SetCompareMode(bool isCompareMode)
    {
        if (isCompareMode && !CanCompareTabs)
        {
            IsCompareMode = false;
            RightTab = null;
            ActivePane = VapourSynthWorkspacePaneKind.Left;
            ActiveTab ??= LeftTab ?? _tabs.FirstOrDefault();
            SetWorkspaceStatus(static texts => texts.VapourSynthCompareNeedsTwoTabsStatus);
            RefreshActiveTabBindings();
            ScheduleSessionSave();
            return;
        }

        if (!isCompareMode)
        {
            var activeTab = ActiveTab ?? LeftTab ?? RightTab ?? _tabs.FirstOrDefault();
            IsCompareMode = false;
            ActiveTab = activeTab;
            LeftTab = activeTab;
            RightTab = null;
            ActivePane = VapourSynthWorkspacePaneKind.Left;

            RefreshActiveTabBindings();
            ScheduleSessionSave();
            return;
        }

        if (IsCompareMode)
        {
            return;
        }

        var primaryTab = ActiveTab ?? LeftTab ?? _tabs.FirstOrDefault();
        SetCompareTabs(primaryTab, primaryTab is null ? null : FindAdjacentTab(primaryTab), primaryTab);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void ActivatePane(VapourSynthWorkspacePaneKind paneKind)
    {
        ActivePane = paneKind;
        if (paneKind == VapourSynthWorkspacePaneKind.Left && LeftTab is not null)
        {
            ActiveTab = LeftTab;
        }
        else if (paneKind == VapourSynthWorkspacePaneKind.Right && RightTab is not null)
        {
            ActiveTab = RightTab;
        }

        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void ShowTabSideBySide(VapourSynthWorkspaceTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_tabs.Contains(tab))
        {
            return;
        }

        if (!CanCompareTabs)
        {
            SetWorkspaceStatus(static texts => texts.VapourSynthCompareNeedsTwoTabsStatus);
            RefreshActiveTabBindings();
            return;
        }

        var companion = ActiveTab is not null && !ReferenceEquals(ActiveTab, tab)
            ? ActiveTab
            : FindAdjacentTab(tab);
        SetCompareTabs(tab, companion, tab);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    private void SetCompareTabs(
        VapourSynthWorkspaceTabViewModel? firstTab,
        VapourSynthWorkspaceTabViewModel? secondTab,
        VapourSynthWorkspaceTabViewModel? activeTab)
    {
        if (firstTab is null || !_tabs.Contains(firstTab))
        {
            return;
        }

        if (secondTab is null || !_tabs.Contains(secondTab) || ReferenceEquals(secondTab, firstTab))
        {
            secondTab = FindAdjacentTab(firstTab);
        }

        if (secondTab is null)
        {
            return;
        }

        LeftTab = firstTab;
        RightTab = secondTab;
        IsCompareMode = true;
        NormalizeCompareTabsByOrder(activeTab);
    }

    public void PinTab(VapourSynthWorkspaceTabViewModel tab, bool isPinned)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_tabs.Contains(tab))
        {
            return;
        }

        tab.SetPinned(isPinned);
        ReorderPinnedTabs(tab);
        NormalizeCompareTabsByOrder(ActiveTab);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void CloseTab(VapourSynthWorkspaceTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_tabs.Remove(tab))
        {
            return;
        }

        if (ReferenceEquals(LeftTab, tab))
        {
            LeftTab = null;
        }

        if (ReferenceEquals(RightTab, tab))
        {
            RightTab = null;
        }

        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = LeftTab ?? RightTab ?? _tabs.LastOrDefault();
        }

        if (_tabs.Count == 0)
        {
            ActiveTab = null;
            LeftTab = null;
            RightTab = null;
            IsCompareMode = false;
            ActivePane = VapourSynthWorkspacePaneKind.Left;
            RefreshActiveTabBindings();
            ScheduleSessionSave();
            return;
        }

        if (!IsCompareMode)
        {
            LeftTab = ActiveTab ?? _tabs.Last();
        }

        if (LeftTab is null)
        {
            LeftTab = _tabs.FirstOrDefault(item => !ReferenceEquals(item, RightTab)) ?? _tabs.FirstOrDefault();
        }

        if (IsCompareMode)
        {
            if (ReferenceEquals(RightTab, LeftTab))
            {
                RightTab = null;
            }

            RightTab ??= _tabs.FirstOrDefault(item => !ReferenceEquals(item, LeftTab));
            if (!CanCompareTabs || RightTab is null)
            {
                IsCompareMode = false;
                RightTab = null;
                ActivePane = VapourSynthWorkspacePaneKind.Left;
            }
        }

        if (IsCompareMode)
        {
            NormalizeCompareTabsByOrder(ActiveTab);
        }
        else if (ActivePane == VapourSynthWorkspacePaneKind.Right && RightTab is null)
        {
            ActivePane = VapourSynthWorkspacePaneKind.Left;

            ActiveTab = LeftTab ?? RightTab;
        }
        else
        {
            ActiveTab = ActivePane == VapourSynthWorkspacePaneKind.Right
                ? RightTab ?? LeftTab
                : LeftTab ?? RightTab;
        }

        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public VapourSynthWorkspaceTabViewModel? GetPaneTab(VapourSynthWorkspacePaneKind paneKind)
    {
        return paneKind == VapourSynthWorkspacePaneKind.Left ? LeftTab : RightTab;
    }

    private void NormalizeCompareTabsByOrder(VapourSynthWorkspaceTabViewModel? preferredActiveTab)
    {
        if (!IsCompareMode)
        {
            return;
        }

        if (!CanCompareTabs)
        {
            IsCompareMode = false;
            RightTab = null;
            ActivePane = VapourSynthWorkspacePaneKind.Left;
            ActiveTab ??= LeftTab ?? _tabs.FirstOrDefault();
            LeftTab = ActiveTab;
            return;
        }

        LeftTab = LeftTab is not null && _tabs.Contains(LeftTab)
            ? LeftTab
            : _tabs.FirstOrDefault(tab => !ReferenceEquals(tab, RightTab));
        RightTab = RightTab is not null && _tabs.Contains(RightTab) && !ReferenceEquals(RightTab, LeftTab)
            ? RightTab
            : _tabs.FirstOrDefault(tab => !ReferenceEquals(tab, LeftTab));

        if (LeftTab is null || RightTab is null)
        {
            IsCompareMode = false;
            RightTab = null;
            ActivePane = VapourSynthWorkspacePaneKind.Left;
            ActiveTab = LeftTab ?? _tabs.FirstOrDefault();
            LeftTab = ActiveTab;
            return;
        }

        if (_tabs.IndexOf(LeftTab) > _tabs.IndexOf(RightTab))
        {
            var previousLeftTab = LeftTab;
            LeftTab = RightTab;
            RightTab = previousLeftTab;
        }

        ActiveTab = ReferenceEquals(preferredActiveTab, LeftTab) || ReferenceEquals(preferredActiveTab, RightTab)
            ? preferredActiveTab
            : LeftTab;
        ActivePane = ReferenceEquals(ActiveTab, RightTab)
            ? VapourSynthWorkspacePaneKind.Right
            : VapourSynthWorkspacePaneKind.Left;
    }

    private VapourSynthWorkspaceTabViewModel? FindAdjacentTab(VapourSynthWorkspaceTabViewModel tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return _tabs.FirstOrDefault();
        }

        if (index + 1 < _tabs.Count)
        {
            return _tabs[index + 1];
        }

        return index > 0 ? _tabs[index - 1] : null;
    }

    public async Task ReloadDocumentAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        await ActiveTab.ReloadDocumentAsync();
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public async Task SaveAsync()
    {
        if (ActiveTab is null)
        {
            return;
        }

        await ActiveTab.SaveAsync();
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public async Task SaveAsAsync(string filePath)
    {
        if (ActiveTab is null)
        {
            return;
        }

        await ActiveTab.SaveAsAsync(filePath);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public async Task SaveTabAsync(VapourSynthWorkspaceTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        await tab.SaveAsync();
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public async Task SaveTabAsAsync(VapourSynthWorkspaceTabViewModel tab, string filePath)
    {
        ArgumentNullException.ThrowIfNull(tab);
        await tab.SaveAsAsync(filePath);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void ApplyEditorBuffer(string content, int line, int column, int lineCount, int charCount)
    {
        if (ActiveTab is null)
        {
            return;
        }

        ActiveTab.ApplyEditorBuffer(content, line, column, lineCount, charCount);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void ApplyCursorState(int line, int column, int lineCount, int charCount)
    {
        if (ActiveTab is null)
        {
            return;
        }

        ActiveTab.ApplyCursorState(line, column, lineCount, charCount);
        RefreshActiveTabBindings();
    }

    public void SetWorkspaceStatus(string statusText)
    {
        if (ActiveTab is null)
        {
            return;
        }

        ActiveTab.SetWorkspaceStatus(statusText);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void SetWorkspaceStatus(Func<AppText, string> statusFormatter)
    {
        if (ActiveTab is null)
        {
            return;
        }

        ActiveTab.SetWorkspaceStatus(statusFormatter);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void SetWorkspaceStatus(VapourSynthWorkspaceTabViewModel tab, Func<AppText, string> statusFormatter)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_tabs.Contains(tab))
        {
            return;
        }

        tab.SetWorkspaceStatus(statusFormatter);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void AppendPreviewLog(VapourSynthPreviewLogEntry entry)
    {
        if (ActiveTab is null)
        {
            return;
        }

        ActiveTab.AppendPreviewLog(entry);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void AppendPreviewLog(VapourSynthWorkspaceTabViewModel tab, VapourSynthPreviewLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_tabs.Contains(tab))
        {
            return;
        }

        tab.AppendPreviewLog(entry);
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void ClearPreviewLog()
    {
        ActiveTab?.ClearPreviewLog();
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public void ClearPreviewLog(VapourSynthWorkspaceTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_tabs.Contains(tab))
        {
            return;
        }

        tab.ClearPreviewLog();
        RefreshActiveTabBindings();
        ScheduleSessionSave();
    }

    public async Task FlushSessionAsync(bool discardUnsavedChanges = false)
    {
        CancelScheduledSessionSave();
        await _workspaceService.SaveSessionAsync(BuildSession(discardUnsavedChanges));
    }

    public void ApplyLanguage(AppLanguage language)
    {
        if (Texts.Language == language)
        {
            return;
        }

        Texts = new AppText(language);
        foreach (var tab in _tabs)
        {
            tab.ApplyLanguage(language);
        }

        RefreshActiveTabBindings();
    }

    private async Task RestoreSessionAsync(VapourSynthWorkspaceSession session)
    {
        _tabs.Clear();

        foreach (var tabSession in session.Tabs)
        {
            var tab = new VapourSynthWorkspaceTabViewModel(_workspaceService, new ShellSettingsAdapter(Texts.Language));
            if (await tab.RestoreSessionSnapshotAsync(tabSession))
            {
                AddTab(tab);
            }
        }

        if (_tabs.Count == 0)
        {
            var initialTab = await CreateNewTabAsync();
            ActivateTab(initialTab);
            return;
        }

        LeftTab = FindTabById(session.LeftTabId) ?? _tabs.FirstOrDefault();
        RightTab = FindTabById(session.RightTabId);
        ActiveTab = FindTabById(session.ActiveTabId) ?? LeftTab ?? RightTab ?? _tabs.FirstOrDefault();

        if (session.IsCompareMode && LeftTab is not null)
        {
            IsCompareMode = true;
            ActivePane = ParsePaneKind(session.ActivePane) ?? VapourSynthWorkspacePaneKind.Left;
            NormalizeCompareTabsByOrder(ActiveTab);
        }
        else
        {
            IsCompareMode = false;
            RightTab = null;
            ActivePane = VapourSynthWorkspacePaneKind.Left;
        }

        RefreshActiveTabBindings();
    }

    private VapourSynthWorkspaceSession BuildSession(bool discardUnsavedChanges)
    {
        var tabSnapshots = _tabs
            .Select(tab =>
            {
                var snapshot = tab.CreateSessionSnapshot();
                if (!discardUnsavedChanges || !snapshot.IsDirty)
                {
                    return snapshot;
                }

                var savedContent = snapshot.SavedContent ?? snapshot.Content ?? string.Empty;
                return snapshot with
                {
                    Content = savedContent,
                    SavedContent = savedContent,
                    IsDirty = false
                };
            })
            .ToArray();

        return new VapourSynthWorkspaceSession(
            tabSnapshots,
            ActiveTab?.Id,
            LeftTab?.Id,
            RightTab?.Id,
            IsCompareMode,
            ActivePane.ToString());
    }

    private void ScheduleSessionSave()
    {
        if (!_isInitialized)
        {
            return;
        }

        CancelScheduledSessionSave();
        _sessionSaveCancellationTokenSource = new CancellationTokenSource();
        _ = PersistSessionAfterDelayAsync(_sessionSaveCancellationTokenSource.Token);
    }

    private async Task PersistSessionAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            await _workspaceService.SaveSessionAsync(BuildSession(false), cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void CancelScheduledSessionSave()
    {
        if (_sessionSaveCancellationTokenSource is null)
        {
            return;
        }

        _sessionSaveCancellationTokenSource.Cancel();
        _sessionSaveCancellationTokenSource.Dispose();
        _sessionSaveCancellationTokenSource = null;
    }

    private void RefreshActiveTabBindings()
    {
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(DocumentPathText));
        OnPropertyChanged(nameof(WorkspaceStatusText));
        OnPropertyChanged(nameof(WorkspaceStatusVisibility));
        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(EditorStatusText));
        OnPropertyChanged(nameof(HeaderStatusText));
        OnPropertyChanged(nameof(CanReload));
        OnPropertyChanged(nameof(DirtyBadgeVisibility));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CurrentContent));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(HasAnyUnsavedChanges));
        OnPropertyChanged(nameof(CanCompareTabs));
        OnPropertyChanged(nameof(DirtyTabs));
    }

    private void AddTab(VapourSynthWorkspaceTabViewModel tab)
    {
        if (tab.IsPinned)
        {
            var pinnedCount = _tabs.Count(static item => item.IsPinned);
            _tabs.Insert(pinnedCount, tab);
            return;
        }

        _tabs.Add(tab);
    }

    private void ReorderPinnedTabs(VapourSynthWorkspaceTabViewModel tab)
    {
        _tabs.Remove(tab);
        if (tab.IsPinned)
        {
            var pinnedCount = _tabs.Count(static item => item.IsPinned);
            _tabs.Insert(pinnedCount, tab);
            return;
        }

        _tabs.Add(tab);
    }

    private VapourSynthWorkspaceTabViewModel? FindTabByPath(string filePath)
    {
        return _tabs.FirstOrDefault(tab =>
            !string.IsNullOrWhiteSpace(tab.CurrentFilePath)
            && string.Equals(NormalizePath(tab.CurrentFilePath), filePath, StringComparison.OrdinalIgnoreCase));
    }

    private VapourSynthWorkspaceTabViewModel? FindTabById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _tabs.FirstOrDefault(tab => string.Equals(tab.Id, id, StringComparison.Ordinal));
    }

    private static VapourSynthWorkspacePaneKind? ParsePaneKind(string? value)
    {
        return Enum.TryParse<VapourSynthWorkspacePaneKind>(value, ignoreCase: true, out var result)
            ? result
            : null;
    }

    private static string NormalizePath(string filePath)
    {
        return Path.GetFullPath(filePath);
    }

    private sealed class ShellSettingsAdapter : IAppSettingsService
    {
        private readonly AppLanguage _language;

        public ShellSettingsAdapter(AppLanguage language)
        {
            _language = language;
        }

        public AppSettings Load()
        {
            return AppSettings.Default with { Language = _language };
        }

        public void Save(AppSettings settings)
        {
        }
    }
}

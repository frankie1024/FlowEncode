using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;

namespace FlowEncode.ViewModels;

public sealed class VapourSynthWorkspaceTabViewModel : ObservableObject
{
    private const int MaxPreviewLogLines = 500;
    private readonly IVapourSynthWorkspaceService _workspaceService;
    private readonly VapourSynthDocumentSaveCoordinator _saveCoordinator;
    private readonly Queue<string> _previewLogLines = [];
    private AppText _texts;
    private string _id = Guid.NewGuid().ToString("N");
    private string? _currentFilePath;
    private string _currentContent;
    private string _savedContent;
    private string _preferredLineEnding;
    private string _logText;
    private bool _isDirty;
    private bool _forceDirtyUntilSave;
    private string _workspaceStatusText;
    private Func<AppText, string>? _workspaceStatusFormatter;
    private int _caretLine = 1;
    private int _caretColumn = 1;
    private int _lineCount = 1;
    private int _charCount;
    private bool _isPinned;

    public VapourSynthWorkspaceTabViewModel(
        IVapourSynthWorkspaceService workspaceService,
        IAppSettingsService settingsService)
    {
        _workspaceService = workspaceService;
        _saveCoordinator = new VapourSynthDocumentSaveCoordinator(workspaceService);
        _texts = new AppText(settingsService.Load().Language);
        _currentContent = string.Empty;
        _savedContent = string.Empty;
        _preferredLineEnding = Environment.NewLine;
        _workspaceStatusFormatter = static texts => texts.VapourSynthEditorReadyStatus;
        _workspaceStatusText = _workspaceStatusFormatter(_texts);
        _logText = _texts.VapourSynthLogEmptyPlaceholder;
    }

    public string Id => _id;

    public AppText Texts
    {
        get => _texts;
        private set => SetProperty(ref _texts, value);
    }

    public string? CurrentFilePath => _currentFilePath;

    public string CurrentContent => _currentContent;

    public bool HasUnsavedChanges => _isDirty;

    public bool IsPinned
    {
        get => _isPinned;
        private set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(TabTitle));
                OnPropertyChanged(nameof(PinMenuText));
                OnPropertyChanged(nameof(PinIconVisibility));
            }
        }
    }

    public bool CanReload => !string.IsNullOrWhiteSpace(_currentFilePath);

    public string TabTitle
    {
        get
        {
            var fileName = string.IsNullOrWhiteSpace(_currentFilePath)
                ? Texts.VapourSynthUntitledDocument
                : Path.GetFileName(_currentFilePath);
            return fileName;
        }
    }

    public string DocumentPathText => string.IsNullOrWhiteSpace(_currentFilePath)
        ? Texts.VapourSynthPathPlaceholder
        : _currentFilePath;

    public string WorkspaceStatusText
    {
        get => _workspaceStatusText;
        private set
        {
            if (SetProperty(ref _workspaceStatusText, value))
            {
                OnPropertyChanged(nameof(WorkspaceStatusVisibility));
            }
        }
    }

    public Visibility WorkspaceStatusVisibility => ShouldShowWorkspaceStatus(WorkspaceStatusText)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public string EditorStatusText => Texts.VapourSynthEditorCursorStatus(
        _caretLine,
        _caretColumn,
        _lineCount,
        _charCount,
        _isDirty);

    public Visibility DirtyBadgeVisibility => _isDirty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PinIconVisibility => _isPinned ? Visibility.Visible : Visibility.Collapsed;

    public string ShowSideBySideMenuText => Texts.VapourSynthShowSideBySideButton;

    public string ExitSideBySideMenuText => Texts.VapourSynthExitSideBySideButton;

    public string CloseOtherTabsMenuText => Texts.VapourSynthCloseOtherTabsButton;

    public string PinMenuText => IsPinned ? Texts.VapourSynthUnpinTabButton : Texts.VapourSynthPinTabButton;

    public void ApplyLanguage(AppLanguage language)
    {
        if (Texts.Language == language)
        {
            return;
        }

        Texts = new AppText(language);

        if (_workspaceStatusFormatter is not null)
        {
            WorkspaceStatusText = _workspaceStatusFormatter(Texts);
        }

        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(ShowSideBySideMenuText));
        OnPropertyChanged(nameof(ExitSideBySideMenuText));
        OnPropertyChanged(nameof(CloseOtherTabsMenuText));
        OnPropertyChanged(nameof(PinMenuText));
        OnPropertyChanged(nameof(DocumentPathText));
        OnPropertyChanged(nameof(EditorStatusText));
        OnPropertyChanged(nameof(WorkspaceStatusVisibility));
        UpdateLogText();
    }

    public void SetPinned(bool isPinned)
    {
        IsPinned = isPinned;
    }

    public async Task CreateNewDocumentAsync()
    {
        var document = await _workspaceService.CreateNewDocumentAsync();
        ApplyDocumentState(document.FilePath, document.Content, document.Content, false);
        SetWorkspaceStatus(static texts => texts.VapourSynthNewDocumentStatus);
    }

    public async Task OpenDocumentAsync(string filePath)
    {
        var document = await _workspaceService.OpenDocumentAsync(filePath);
        ApplyDocumentState(document.FilePath, document.Content, document.Content, false);
        SetWorkspaceStatus(texts => texts.VapourSynthOpenedStatus(filePath));
    }

    public async Task ReloadDocumentAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            return;
        }

        var document = await _workspaceService.OpenDocumentAsync(_currentFilePath);
        ApplyDocumentState(document.FilePath, document.Content, document.Content, false);
        SetWorkspaceStatus(texts => texts.VapourSynthReloadedStatus(_currentFilePath));
    }

    public async Task SaveAsync()
    {
        await SaveCoreAsync(null);
    }

    public async Task SaveAsAsync(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await SaveCoreAsync(filePath);
    }

    public void ApplyEditorBuffer(string content, int line, int column, int lineCount, int charCount)
    {
        _currentContent = NormalizeLineEndings(content);
        _caretLine = Math.Max(1, line);
        _caretColumn = Math.Max(1, column);
        _lineCount = Math.Max(1, lineCount);
        _charCount = Math.Max(0, charCount);

        var previousDirty = _isDirty;
        _isDirty = _forceDirtyUntilSave
            || !string.Equals(_currentContent, _savedContent, StringComparison.Ordinal);

        if (previousDirty != _isDirty)
        {
            OnPropertyChanged(nameof(TabTitle));
            OnPropertyChanged(nameof(DirtyBadgeVisibility));
        }

        OnPropertyChanged(nameof(EditorStatusText));
    }

    public void ApplyCursorState(int line, int column, int lineCount, int charCount)
    {
        _caretLine = Math.Max(1, line);
        _caretColumn = Math.Max(1, column);
        _lineCount = Math.Max(1, lineCount);
        _charCount = Math.Max(0, charCount);
        OnPropertyChanged(nameof(EditorStatusText));
    }

    public void SetWorkspaceStatus(string statusText)
    {
        _workspaceStatusFormatter = null;
        WorkspaceStatusText = statusText;
    }

    public void SetWorkspaceStatus(Func<AppText, string> statusFormatter)
    {
        _workspaceStatusFormatter = statusFormatter;
        WorkspaceStatusText = statusFormatter(Texts);
    }

    public void AppendPreviewLog(VapourSynthPreviewLogEntry entry)
    {
        var timestamp = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        var level = entry.Level switch
        {
            VapourSynthPreviewLogLevel.Warning => "WARN",
            VapourSynthPreviewLogLevel.Error => "ERROR",
            _ => "INFO"
        };
        var source = string.IsNullOrWhiteSpace(entry.Source)
            ? "preview"
            : entry.Source.Trim();
        var message = NormalizeLineEndings(entry.Message);

        foreach (var line in message.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            _previewLogLines.Enqueue($"[{timestamp}] [{level}] [{source}] {line}");
        }

        while (_previewLogLines.Count > MaxPreviewLogLines)
        {
            _previewLogLines.Dequeue();
        }

        UpdateLogText();
    }

    public void ClearPreviewLog()
    {
        _previewLogLines.Clear();
        UpdateLogText();
    }

    public VapourSynthWorkspaceTabSession CreateSessionSnapshot()
    {
        return new VapourSynthWorkspaceTabSession(
            Id,
            _currentFilePath,
            _currentContent,
            _savedContent,
            _isDirty,
            _isPinned,
            WorkspaceStatusText,
            LogText,
            _caretLine,
            _caretColumn,
            _lineCount,
            _charCount);
    }

    public async Task<bool> RestoreSessionSnapshotAsync(VapourSynthWorkspaceTabSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        ApplySessionIdentity(session);

        var filePath = NormalizeFilePath(session.FilePath);
        var sessionContent = session.Content ?? string.Empty;
        var sessionSavedContent = session.SavedContent ?? sessionContent;
        var diskReadFailed = false;
        var displayPath = filePath ?? Texts.VapourSynthUntitledDocument;

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try
            {
                var document = await _workspaceService.OpenDocumentAsync(filePath);
                if (session.IsDirty)
                {
                    var isStillDirty = !string.Equals(
                        NormalizeLineEndings(sessionContent),
                        NormalizeLineEndings(document.Content),
                        StringComparison.Ordinal);
                    ApplyDocumentState(document.FilePath, sessionContent, document.Content, isStillDirty);
                    SetWorkspaceStatus(
                        !string.Equals(
                            NormalizeLineEndings(sessionSavedContent),
                            NormalizeLineEndings(document.Content),
                            StringComparison.Ordinal)
                            ? texts => texts.VapourSynthRecoveredExternalChangeDraftStatus(document.FilePath ?? filePath)
                            : texts => texts.VapourSynthRecoveredUnsavedDraftStatus(document.FilePath ?? filePath));
                }
                else
                {
                    ApplyDocumentState(document.FilePath, document.Content, document.Content, false);
                    SetWorkspaceStatus(texts => texts.VapourSynthRestoredFromDiskStatus(document.FilePath ?? filePath));
                }

                ApplySessionViewState(session, keepWorkspaceStatus: true);
                return true;
            }
            catch
            {
                diskReadFailed = true;
            }
        }

        if (diskReadFailed)
        {
            ApplyDocumentState(filePath, sessionContent, sessionSavedContent, session.IsDirty, forceDirtyUntilSave: string.IsNullOrWhiteSpace(session.SavedContent));
            SetWorkspaceStatus(texts => texts.VapourSynthRestoredFromSessionSnapshotStatus(displayPath));
            ApplySessionViewState(session, keepWorkspaceStatus: true);
            return true;
        }

        if (!session.IsDirty)
        {
            return false;
        }

        ApplyDocumentState(filePath, sessionContent, sessionSavedContent, true, forceDirtyUntilSave: string.IsNullOrWhiteSpace(session.SavedContent));
        SetWorkspaceStatus(texts => texts.VapourSynthRecoveredMissingFileDraftStatus(displayPath));
        ApplySessionViewState(session, keepWorkspaceStatus: true);
        return true;
    }

    private void ApplyDocumentState(string? filePath, string content, string savedContent, bool isDirty, bool forceDirtyUntilSave = false)
    {
        var normalizedContent = NormalizeLineEndings(content);
        var normalizedSavedContent = NormalizeLineEndings(savedContent);

        _currentFilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        _currentContent = normalizedContent;
        _savedContent = normalizedSavedContent;
        _preferredLineEnding = DetectLineEnding(string.IsNullOrEmpty(savedContent) ? content : savedContent);
        _forceDirtyUntilSave = forceDirtyUntilSave;
        _isDirty = isDirty || _forceDirtyUntilSave;

        _lineCount = CountLines(_currentContent);
        _charCount = _currentContent.Length;
        _caretLine = 1;
        _caretColumn = 1;

        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CurrentContent));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(DocumentPathText));
        OnPropertyChanged(nameof(EditorStatusText));
        OnPropertyChanged(nameof(DirtyBadgeVisibility));
        OnPropertyChanged(nameof(CanReload));
    }

    private async Task SaveCoreAsync(string? requestedFilePath)
    {
        await _saveCoordinator.SaveAsync(
            () =>
            {
                var filePath = requestedFilePath ?? _currentFilePath;
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    throw new InvalidOperationException("Current document has no file path.");
                }

                return new VapourSynthDocumentSaveRequest(
                    filePath,
                    RestorePreferredLineEndings(_currentContent));
            },
            result =>
            {
                ApplySavedDocumentState(result.Document);
                SetWorkspaceStatus(texts => texts.VapourSynthSavedStatus(result.Request.FilePath));
            });
    }

    private void ApplySavedDocumentState(VapourSynthWorkspaceDocument document)
    {
        var savedContent = NormalizeLineEndings(document.Content);
        _currentFilePath = string.IsNullOrWhiteSpace(document.FilePath) ? null : document.FilePath;
        _savedContent = savedContent;
        _preferredLineEnding = DetectLineEnding(document.Content);
        _forceDirtyUntilSave = false;
        _isDirty = !string.Equals(_currentContent, savedContent, StringComparison.Ordinal);

        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(DocumentPathText));
        OnPropertyChanged(nameof(EditorStatusText));
        OnPropertyChanged(nameof(DirtyBadgeVisibility));
        OnPropertyChanged(nameof(CanReload));
    }

    private void ApplySessionIdentity(VapourSynthWorkspaceTabSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Id))
        {
            _id = session.Id;
        }

        IsPinned = session.IsPinned;
    }

    private void ApplySessionViewState(VapourSynthWorkspaceTabSession session, bool keepWorkspaceStatus = false)
    {
        _caretLine = Math.Max(1, session.CaretLine);
        _caretColumn = Math.Max(1, session.CaretColumn);

        if (!keepWorkspaceStatus && !string.IsNullOrWhiteSpace(session.WorkspaceStatusText))
        {
            _workspaceStatusFormatter = null;
            WorkspaceStatusText = session.WorkspaceStatusText;
        }

        _previewLogLines.Clear();
        if (!string.IsNullOrWhiteSpace(session.LogText))
        {
            foreach (var line in session.LogText.Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.None))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _previewLogLines.Enqueue(line);
                }
            }
        }

        UpdateLogText();
        OnPropertyChanged(nameof(EditorStatusText));
    }

    private static string? NormalizeFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 1;
        }

        return content.Count(static character => character == '\n') + 1;
    }

    private static string NormalizeLineEndings(string? content)
    {
        return (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string DetectLineEnding(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Environment.NewLine;
        }

        var firstLfIndex = content.IndexOf('\n');
        if (firstLfIndex > 0 && content[firstLfIndex - 1] == '\r')
        {
            return "\r\n";
        }

        if (firstLfIndex >= 0)
        {
            return "\n";
        }

        return content.IndexOf('\r') >= 0 ? "\r" : Environment.NewLine;
    }

    private string RestorePreferredLineEndings(string content)
    {
        var normalized = NormalizeLineEndings(content);
        return _preferredLineEnding == "\n"
            ? normalized
            : normalized.Replace("\n", _preferredLineEnding, StringComparison.Ordinal);
    }

    private bool ShouldShowWorkspaceStatus(string? statusText)
    {
        return !string.IsNullOrWhiteSpace(statusText)
            && !string.Equals(statusText, Texts.VapourSynthEditorReadyStatus, StringComparison.Ordinal);
    }

    private void UpdateLogText()
    {
        LogText = _previewLogLines.Count == 0
            ? Texts.VapourSynthLogEmptyPlaceholder
            : string.Join(Environment.NewLine, _previewLogLines);
    }
}

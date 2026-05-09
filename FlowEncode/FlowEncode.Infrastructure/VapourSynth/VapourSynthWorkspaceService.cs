using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;

namespace FlowEncode.Infrastructure;

public sealed class VapourSynthWorkspaceService : IVapourSynthWorkspaceService
{
    private static readonly JsonSerializerOptions SessionSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _sessionPath;

    public VapourSynthWorkspaceService(LocalAppPaths appPaths)
    {
        var workspaceRootPath = Path.Combine(appPaths.DataRootPath, "vapoursynth-workspace");
        Directory.CreateDirectory(workspaceRootPath);

        _sessionPath = Path.Combine(workspaceRootPath, "editor-session.json");
        EditorAssetsRootPath = Path.Combine(AppContext.BaseDirectory, "Assets", "VapourSynthEditor");
    }

    public string EditorAssetsRootPath { get; }

    public Task<VapourSynthWorkspaceDocument> CreateNewDocumentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new VapourSynthWorkspaceDocument(null, string.Empty));
    }

    public async Task<VapourSynthWorkspaceDocument> OpenDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));
        }

        filePath = Path.GetFullPath(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
        return new VapourSynthWorkspaceDocument(filePath, content);
    }

    public async Task<VapourSynthWorkspaceDocument> SaveDocumentAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));
        }

        filePath = Path.GetFullPath(filePath);
        var directoryPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(filePath, content ?? string.Empty, new UTF8Encoding(false), cancellationToken);
        return new VapourSynthWorkspaceDocument(filePath, content ?? string.Empty);
    }

    public async Task<VapourSynthWorkspaceSession?> LoadSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_sessionPath))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var json = await File.ReadAllTextAsync(_sessionPath, Encoding.UTF8, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var dto = JsonSerializer.Deserialize<WorkspaceSessionDto>(json, SessionSerializerOptions);
        if (dto is null)
        {
            return null;
        }

        var tabs = dto.Tabs
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.Id))
            .Select(tab => new VapourSynthWorkspaceTabSession(
                tab.Id,
                string.IsNullOrWhiteSpace(tab.FilePath) ? null : tab.FilePath,
                tab.Content,
                tab.SavedContent,
                tab.IsDirty,
                tab.IsPinned,
                tab.WorkspaceStatusText,
                tab.LogText,
                tab.CaretLine,
                tab.CaretColumn,
                tab.LineCount,
                tab.CharCount))
            .ToArray();

        return new VapourSynthWorkspaceSession(
            tabs,
            string.IsNullOrWhiteSpace(dto.ActiveTabId) ? tabs.FirstOrDefault()?.Id : dto.ActiveTabId,
            string.IsNullOrWhiteSpace(dto.LeftTabId) ? null : dto.LeftTabId,
            string.IsNullOrWhiteSpace(dto.RightTabId) ? null : dto.RightTabId,
            dto.IsCompareMode,
            string.IsNullOrWhiteSpace(dto.ActivePane) ? null : dto.ActivePane);
    }

    public async Task SaveSessionAsync(VapourSynthWorkspaceSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var tabDtos = session.Tabs
            .Select(tab => new WorkspaceTabSessionDto
            {
                Id = tab.Id,
                FilePath = tab.FilePath,
                Content = tab.Content,
                SavedContent = tab.SavedContent,
                IsDirty = tab.IsDirty,
                IsPinned = tab.IsPinned,
                WorkspaceStatusText = tab.WorkspaceStatusText,
                LogText = tab.LogText,
                CaretLine = tab.CaretLine,
                CaretColumn = tab.CaretColumn,
                LineCount = tab.LineCount,
                CharCount = tab.CharCount
            })
            .ToList();

        var dto = new WorkspaceSessionDto
        {
            Tabs = tabDtos,
            ActiveTabId = session.ActiveTabId,
            LeftTabId = session.LeftTabId,
            RightTabId = session.RightTabId,
            IsCompareMode = session.IsCompareMode,
            ActivePane = session.ActivePane,
        };

        var json = JsonSerializer.Serialize(dto, SessionSerializerOptions);
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(_sessionPath, json, new UTF8Encoding(false), cancellationToken);
    }

    private sealed class WorkspaceSessionDto
    {
        public List<WorkspaceTabSessionDto> Tabs { get; set; } = [];

        public string? ActiveTabId { get; set; }

        public string? LeftTabId { get; set; }

        public string? RightTabId { get; set; }

        public bool IsCompareMode { get; set; }

        public string? ActivePane { get; set; }
    }

    private sealed class WorkspaceTabSessionDto
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string? FilePath { get; set; }

        public string? Content { get; set; }

        public string? SavedContent { get; set; }

        public bool IsDirty { get; set; }

        public bool IsPinned { get; set; }

        public string? WorkspaceStatusText { get; set; }

        public string? LogText { get; set; }

        public int CaretLine { get; set; }

        public int CaretColumn { get; set; }

        public int LineCount { get; set; }

        public int CharCount { get; set; }
    }
}

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

public sealed class VapourSynthWorkspaceService : IVapourSynthWorkspaceService, IDisposable
{
    private static readonly JsonSerializerOptions SessionSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly LocalAppPaths _paths;
    private readonly string _sessionRootPath;
    private readonly string _sessionPath;
    private readonly SemaphoreSlim _sessionFileGate = new(1, 1);

    public VapourSynthWorkspaceService(LocalAppPaths appPaths)
    {
        _paths = appPaths;
        _sessionRootPath = Path.Combine(appPaths.DataRootPath, "vapoursynth-workspace");
        Directory.CreateDirectory(_sessionRootPath);

        _sessionPath = Path.Combine(_sessionRootPath, "editor-session.json");
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

        await _sessionFileGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_sessionPath))
            {
                return null;
            }

            try
            {
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecoverBrokenSessionFile(ex);
                return null;
            }
        }
        finally
        {
            _sessionFileGate.Release();
        }
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
        await _sessionFileGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_sessionRootPath);
            var tempPath = _sessionPath + ".tmp";

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), cancellationToken);
                File.Move(tempPath, _sessionPath, true);
            }
            finally
            {
                TryDeleteTemporarySessionFile(tempPath);
            }
        }
        finally
        {
            _sessionFileGate.Release();
        }
    }

    private void RecoverBrokenSessionFile(Exception exception)
    {
        var backupPath = BuildBrokenSessionBackupPath();

        try
        {
            Directory.CreateDirectory(_sessionRootPath);
            File.Move(_sessionPath, backupPath, overwrite: false);
            WriteDiagnostic(
                $"Failed to load workspace session from '{_sessionPath}'. {exception.GetType().Name}: {exception.Message}. " +
                $"Backed up to '{backupPath}'.");
        }
        catch (Exception backupException)
        {
            WriteDiagnostic(
                $"Failed to load workspace session from '{_sessionPath}'. {exception.GetType().Name}: {exception.Message}. " +
                $"Backup also failed. {backupException.GetType().Name}: {backupException.Message}");
        }
    }

    private string BuildBrokenSessionBackupPath()
    {
        var fileName = Path.GetFileName(_sessionPath);

        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var suffix = attempt == 0
                ? DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : $"{DateTime.Now:yyyyMMdd_HHmmss}_{attempt + 1}";
            var candidate = Path.Combine(_sessionRootPath, $"{fileName}.broken-{suffix}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(_sessionRootPath, $"{fileName}.broken-{Guid.NewGuid():N}");
    }

    private void TryDeleteTemporarySessionFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"Failed to delete temporary workspace session file '{path}'. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void WriteDiagnostic(string message)
    {
        AppDiagnosticsLog.Write(_paths, nameof(VapourSynthWorkspaceService), message);
    }

    public void Dispose()
    {
        _sessionFileGate.Dispose();
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

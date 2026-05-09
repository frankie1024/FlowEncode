using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FlowEncode.Application;

public interface IVapourSynthWorkspaceService
{
    string EditorAssetsRootPath { get; }

    Task<VapourSynthWorkspaceDocument> CreateNewDocumentAsync(CancellationToken cancellationToken = default);

    Task<VapourSynthWorkspaceDocument> OpenDocumentAsync(string filePath, CancellationToken cancellationToken = default);

    Task<VapourSynthWorkspaceDocument> SaveDocumentAsync(string filePath, string content, CancellationToken cancellationToken = default);

    Task<VapourSynthWorkspaceSession?> LoadSessionAsync(CancellationToken cancellationToken = default);

    Task SaveSessionAsync(VapourSynthWorkspaceSession session, CancellationToken cancellationToken = default);
}

public sealed record VapourSynthWorkspaceDocument(
    string? FilePath,
    string Content);

public sealed record VapourSynthWorkspaceSession(
    IReadOnlyList<VapourSynthWorkspaceTabSession> Tabs,
    string? ActiveTabId,
    string? LeftTabId,
    string? RightTabId,
    bool IsCompareMode,
    string? ActivePane);

public sealed record VapourSynthWorkspaceTabSession(
    string Id,
    string? FilePath,
    string? Content,
    string? SavedContent,
    bool IsDirty,
    bool IsPinned,
    string? WorkspaceStatusText,
    string? LogText,
    int CaretLine,
    int CaretColumn,
    int LineCount,
    int CharCount);

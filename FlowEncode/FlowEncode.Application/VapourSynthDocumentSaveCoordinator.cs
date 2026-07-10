using System;
using System.Threading;
using System.Threading.Tasks;

namespace FlowEncode.Application;

public sealed record VapourSynthDocumentSaveRequest(
    string FilePath,
    string Content);

public sealed record VapourSynthDocumentSaveResult(
    VapourSynthDocumentSaveRequest Request,
    VapourSynthWorkspaceDocument Document);

public sealed class VapourSynthDocumentSaveCoordinator
{
    private readonly IVapourSynthWorkspaceService _workspaceService;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public VapourSynthDocumentSaveCoordinator(IVapourSynthWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    public async Task<VapourSynthDocumentSaveResult> SaveAsync(
        Func<VapourSynthDocumentSaveRequest> createRequest,
        Action<VapourSynthDocumentSaveResult>? applyResult = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createRequest);

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var request = createRequest();
            var document = await _workspaceService.SaveDocumentAsync(
                request.FilePath,
                request.Content,
                cancellationToken);
            var result = new VapourSynthDocumentSaveResult(request, document);
            applyResult?.Invoke(result);
            return result;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}

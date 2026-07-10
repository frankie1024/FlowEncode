using FlowEncode.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class VapourSynthDocumentSaveCoordinatorTests
{
    [TestMethod]
    public async Task SaveAsync_SerializesSavesAndCapturesQueuedContentAfterPreviousSave()
    {
        var service = new ControlledWorkspaceService();
        var coordinator = new VapourSynthDocumentSaveCoordinator(service);
        var currentContent = "first";
        var appliedContent = new List<string>();

        var firstSave = coordinator.SaveAsync(
            () => new VapourSynthDocumentSaveRequest("test.vpy", currentContent),
            result => appliedContent.Add(result.Document.Content));
        await service.WaitForSaveCountAsync(1);

        currentContent = "second";
        var secondSave = coordinator.SaveAsync(
            () => new VapourSynthDocumentSaveRequest("test.vpy", currentContent),
            result => appliedContent.Add(result.Document.Content));

        Assert.AreEqual(1, service.SaveCount);
        service.CompleteSave(0);
        await service.WaitForSaveCountAsync(2);

        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            service.GetSavedContents());
        CollectionAssert.AreEqual(new[] { "first" }, appliedContent);

        service.CompleteSave(1);
        await Task.WhenAll(firstSave, secondSave);
        CollectionAssert.AreEqual(new[] { "first", "second" }, appliedContent);
    }

    [TestMethod]
    public async Task SaveAsync_WhenSaveFails_ReleasesGateForNextSave()
    {
        var service = new ControlledWorkspaceService();
        var coordinator = new VapourSynthDocumentSaveCoordinator(service);

        var failedSave = coordinator.SaveAsync(
            static () => new VapourSynthDocumentSaveRequest("first.vpy", "first"));
        await service.WaitForSaveCountAsync(1);
        service.FailSave(0, new IOException("save failed"));
        await Assert.ThrowsExactlyAsync<IOException>(async () => await failedSave);

        var nextSave = coordinator.SaveAsync(
            static () => new VapourSynthDocumentSaveRequest("second.vpy", "second"));
        await service.WaitForSaveCountAsync(2);
        service.CompleteSave(1);

        var result = await nextSave;
        Assert.AreEqual("second", result.Document.Content);
    }

    private sealed class ControlledWorkspaceService : IVapourSynthWorkspaceService
    {
        private readonly object _syncRoot = new();
        private readonly List<VapourSynthDocumentSaveRequest> _saveRequests = [];
        private readonly List<TaskCompletionSource<VapourSynthWorkspaceDocument>> _saveCompletions = [];

        public string EditorAssetsRootPath => string.Empty;

        public int SaveCount
        {
            get
            {
                lock (_syncRoot)
                {
                    return _saveRequests.Count;
                }
            }
        }

        public Task<VapourSynthWorkspaceDocument> CreateNewDocumentAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<VapourSynthWorkspaceDocument> OpenDocumentAsync(string filePath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<VapourSynthWorkspaceDocument> SaveDocumentAsync(
            string filePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<VapourSynthWorkspaceDocument>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_syncRoot)
            {
                _saveRequests.Add(new VapourSynthDocumentSaveRequest(filePath, content));
                _saveCompletions.Add(completion);
            }

            return completion.Task;
        }

        public Task<VapourSynthWorkspaceSession?> LoadSessionAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SaveSessionAsync(VapourSynthWorkspaceSession session, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void CompleteSave(int index)
        {
            VapourSynthDocumentSaveRequest request;
            TaskCompletionSource<VapourSynthWorkspaceDocument> completion;
            lock (_syncRoot)
            {
                request = _saveRequests[index];
                completion = _saveCompletions[index];
            }

            completion.SetResult(new VapourSynthWorkspaceDocument(request.FilePath, request.Content));
        }

        public void FailSave(int index, Exception exception)
        {
            TaskCompletionSource<VapourSynthWorkspaceDocument> completion;
            lock (_syncRoot)
            {
                completion = _saveCompletions[index];
            }

            completion.SetException(exception);
        }

        public string[] GetSavedContents()
        {
            lock (_syncRoot)
            {
                return _saveRequests.Select(static request => request.Content).ToArray();
            }
        }

        public async Task WaitForSaveCountAsync(int count)
        {
            await WaitForConditionAsync(() => SaveCount >= count);
        }

        private static async Task WaitForConditionAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}

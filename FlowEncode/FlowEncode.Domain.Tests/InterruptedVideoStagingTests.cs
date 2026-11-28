using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class InterruptedVideoStagingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Startup_RemovesInterruptedVideoStagingAndLeavesOtherFiles(bool parallel)
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var store = new TaskHistoryStore(new LocalAppPaths(root.Path));
        var request = root.Video().Video! with { UseAv1anParallelVideoEncoding = parallel };
        var failed = request with { JobId = Guid.NewGuid() };
        var (staging, logs) = CreateStaging(request);
        var (retained, _) = CreateStaging(failed);
        SaveHistory(request, EncodingJobState.Running);
        SaveHistory(failed, EncodingJobState.Failed);
        File.WriteAllText(request.OutputPath, "published output");

        var tasks = new TaskCoordinator(new SharedTaskCoordinatorTests.Settings(), store);
        Assert.IsTrue(tasks.HasInterruptedVideos);
        tasks.CleanupInterruptedVideos(_ => false, CancellationToken.None);
        Assert.IsTrue(new TaskCoordinator(new SharedTaskCoordinatorTests.Settings(), store).HasInterruptedVideos);

        tasks.CleanupInterruptedVideos(video => InterruptedVideoStaging.Cleanup(video, message => Assert.Fail(message)),
            CancellationToken.None);

        Assert.AreEqual(EncodingJobState.Failed, tasks.Get(request.JobId).State);
        var nextLaunch = new TaskCoordinator(new SharedTaskCoordinatorTests.Settings(), store);
        Assert.IsFalse(nextLaunch.HasInterruptedVideos);
        nextLaunch.CleanupInterruptedVideos(_ => throw new AssertFailedException("Normal startup must not check staging."),
            CancellationToken.None);
        Assert.IsFalse(Directory.Exists(staging));
        Assert.IsFalse(Directory.Exists(logs));
        Assert.AreEqual("published output", File.ReadAllText(request.OutputPath));
        Assert.IsTrue(File.Exists(Path.Combine(retained, "partial-video")));

        void SaveHistory(EncodingJobRequest video, EncodingJobState state)
        {
            var workload = new TaskWorkload(TaskKind.Video, Video: video);
            var historyTasks = new TaskCoordinator(new SharedTaskCoordinatorTests.Settings());
            historyTasks.Reserve(workload);
            var snapshot = historyTasks.Get(video.JobId) with { State = state, StartedAt = DateTimeOffset.UtcNow };
            store.Save(new(snapshot, workload, null));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Cleanup_RemovesEmptySharedTemporaryRoot(bool parallel)
    {
        using var root = new SharedTaskCoordinatorTests.TestRoot();
        var request = root.Video().Video! with { UseAv1anParallelVideoEncoding = parallel };
        CreateStaging(request);

        Assert.IsTrue(InterruptedVideoStaging.Cleanup(request, message => Assert.Fail(message)));

        Assert.IsFalse(Directory.Exists(Path.Combine(root.Path, ".flowencode-temp")));
        Assert.IsTrue(File.Exists(request.SourcePath));
    }

    private static (string Staging, string Logs) CreateStaging(EncodingJobRequest request)
    {
        var staging = request.UseAv1anParallelVideoEncoding
            ? ParallelVideoEncodingAv1anRunner.GetTempDirectory(request)
            : LocalEncodingJobRunner.GetJobTempDirectory(request);
        var logs = request.UseAv1anParallelVideoEncoding
            ? ParallelVideoEncodingAv1anRunner.GetTemporaryLogDirectory(request, staging)
            : Path.Combine(staging, "logs");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "partial-video"), "unfinished encoder output");
        File.WriteAllText(Path.Combine(logs, "raw.log"), "encoder diagnostics");
        return (staging, logs);
    }
}

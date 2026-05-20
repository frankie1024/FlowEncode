using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class CliBluRayDemuxBackendAdapterBaseTests
{
    [TestMethod]
    public async Task RunProcessAsync_WhenCancelled_ReturnsCancelledResult()
    {
        var scriptPath = WriteTempCommandScript(
            """
            @echo off
            echo process: 1%%
            ping -n 6 127.0.0.1 > nul
            echo process: 2%%
            ping -n 6 127.0.0.1 > nul
            """);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            var adapter = new TestAdapter();
            var request = CreateRequest();

            var result = await adapter.RunScriptAsync(request, scriptPath, cancellation.Token);

            Assert.AreEqual(EncodingJobState.Cancelled, result.State);
            Assert.AreEqual(request.JobId, result.JobId);
            CollectionAssert.AreEqual(
                request.Selections.Select(static selection => selection.OutputPath).ToList(),
                result.OutputPaths.ToList());
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [TestMethod]
    public void CreateStagedOutputDirectory_CreatesStageBesideFinalOutputDirectory()
    {
        var finalOutputDirectory = @"D:\demux\movie_out";
        var stagedDirectory = TestAdapter.ExposeCreateStagedOutputDirectory(
            finalOutputDirectory,
            "bluray-demux",
            Guid.Parse("11111111-1111-1111-1111-111111111111"));

        StringAssert.Contains(stagedDirectory, ".flowencode-temp");
        Assert.IsFalse(
            stagedDirectory.StartsWith(finalOutputDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Demux staging should not be created inside the final output directory.");
    }

    private static BluRayDemuxRequest CreateRequest()
    {
        var playlist = new BluRayPlaylistItem(
            "00001",
            "00001.mpls",
            "test playlist",
            @"D:\disc\BDMV\PLAYLIST\00001.mpls",
            @"D:\disc\BDMV\PLAYLIST\00001.mpls",
            "00:01:00",
            TimeSpan.FromMinutes(1),
            1);
        var track = new BluRayTrackItem(
            "1011",
            1,
            "1011",
            BluRayTrackKind.Audio,
            "Audio",
            "Test track",
            "eng");

        return new BluRayDemuxRequest(
            Guid.NewGuid(),
            BluRayDemuxBackend.DgDemux,
            @"D:\disc",
            Path.Combine(Path.GetTempPath(), "FlowEncodeDemuxCancel"),
            Path.Combine(Path.GetTempPath(), "FlowEncodeDemuxCancel", "00001"),
            playlist,
            [new BluRayTrackSelection(track, Path.Combine(Path.GetTempPath(), "FlowEncodeDemuxCancel", "00001.audio.*"))]);
    }

    private static string WriteTempCommandScript(string contents)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"flowencode-demux-test-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(scriptPath, contents);
        return scriptPath;
    }

    private sealed class TestAdapter : CliBluRayDemuxBackendAdapterBase
    {
        public TestAdapter()
            : base(new StubToolProbeService())
        {
        }

        public override BluRayDemuxBackend Backend => BluRayDemuxBackend.DgDemux;

        public Task<BluRayDemuxResult> RunScriptAsync(
            BluRayDemuxRequest request,
            string scriptPath,
            CancellationToken cancellationToken)
        {
            var startInfo = CreateStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(scriptPath);

            return RunProcessAsync(
                request,
                "cmd.exe /c",
                startInfo,
                static line => line.Contains('%', StringComparison.Ordinal) ? 0.5 : null,
                successLineDetector: null,
                startSummary: "running",
                completedSummary: "completed",
                cancelledSummary: "cancelled",
                failedSummary: "failed",
                progress: null,
                cancellationToken);
        }

        public static string ExposeCreateStagedOutputDirectory(string finalOutputDirectory, string scope, Guid jobId)
            => CreateStagedOutputDirectory(finalOutputDirectory, scope, jobId);

        public override Task<IReadOnlyList<BluRayPlaylistItem>> ScanDiscAsync(string discPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<BluRayPlaylistScanResult> ScanPlaylistAsync(string discPath, BluRayPlaylistItem playlist, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<BluRayDemuxResult> RunAsync(BluRayDemuxRequest request, IProgress<BluRayDemuxProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override string BuildDisplayCommand(BluRayDemuxRequest request)
            => "cmd.exe /c";
    }

    private sealed class StubToolProbeService : IToolProbeService
    {
        public Task<IReadOnlyList<ToolProbeResult>> ProbeAllAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ToolProbeResult> ProbeAsync(RegisteredToolKind kind, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void InvalidateCache()
        {
        }
    }
}

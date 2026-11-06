using System.Collections.Concurrent;
using System.Text.Json;
using FlowEncode.Infrastructure.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class DesktopAutomationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Client_ReceivesTypedResultFromServer()
    {
        await using var server = StartServer((method, parameters) => method switch
        {
            "echo" => Task.FromResult<object?>(new JobIdParams(parameters!.Value.Deserialize<JobIdParams>(DesktopAutomationProtocol.JsonOptions)!.JobId)),
            _ => throw new DesktopAutomationException("unknown")
        });
        var client = new DesktopAutomationClient(server.PipeName);
        var id = Guid.NewGuid();

        var result = await client.SendAsync<JobIdParams>("echo", new JobIdParams(id)).WaitAsync(Timeout);

        Assert.AreEqual(id, result.JobId);
    }

    [TestMethod]
    public async Task Client_SurfacesHandlerErrorMessages()
    {
        await using var server = StartServer((_, _) => throw new DesktopAutomationException("输出文件已存在"));
        var client = new DesktopAutomationClient(server.PipeName);

        var error = await Assert.ThrowsExactlyAsync<DesktopAutomationException>(() => client.SendRawAsync("anything"));

        Assert.AreEqual("输出文件已存在", error.Message);
    }

    [TestMethod]
    public async Task Client_WhenNoDesktopIsListening_ReportsUnavailable()
    {
        var client = new DesktopAutomationClient($"FlowEncode.Automation.Test.{Guid.NewGuid():N}");

        await Assert.ThrowsExactlyAsync<DesktopUnavailableException>(
            () => client.SendRawAsync(DesktopAutomationMethods.AppStatus, connectTimeout: TimeSpan.FromMilliseconds(200)));
    }

    [TestMethod]
    public void PipeName_IsStableAndUserScoped()
    {
        var name = DesktopAutomationProtocol.GetPipeName();

        Assert.AreEqual(name, DesktopAutomationProtocol.GetPipeName());
        StringAssert.StartsWith(name, $"FlowEncode.Automation.v{DesktopAutomationProtocol.Version}.");
        Assert.IsFalse(name.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "the user name is hashed, not embedded");
    }

    [TestMethod]
    public async Task Enqueue_WhenConcurrentClientsShareAnOutput_TheDesktopAcceptsExactlyOne()
    {
        // The desktop serializes requests on its UI thread; a lock stands in for it here.
        var gate = new object();
        var queue = new List<EncodingJobRequest>();
        await using var server = StartServer((method, parameters) =>
        {
            var request = parameters!.Value.Deserialize<EnqueueEncodingJobParams>(DesktopAutomationProtocol.JsonOptions)!.Request;
            lock (gate)
            {
                AutomationQueuePolicy.ValidateEnqueue(request, queue, static _ => false);
                queue.Add(request);
            }

            return Task.FromResult<object?>(new JobIdParams(request.JobId));
        });

        var client = new DesktopAutomationClient(server.PipeName);
        var template = CreateRequest();
        var accepted = new ConcurrentBag<Guid>();
        var rejected = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            try
            {
                var result = await client.SendAsync<JobIdParams>(
                    DesktopAutomationMethods.QueueEnqueue,
                    new EnqueueEncodingJobParams(template with { JobId = Guid.NewGuid() }, true));
                accepted.Add(result.JobId);
            }
            catch (DesktopAutomationException ex)
            {
                rejected.Add(ex.Message);
            }
        })).WaitAsync(Timeout);

        Assert.AreEqual(1, accepted.Count);
        Assert.AreEqual(15, rejected.Count);
        Assert.IsTrue(rejected.All(static message => message.Contains("Another active job", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void QueuePolicy_WhenOverwriteIsDisabledAndOutputExists_Rejects()
    {
        var request = CreateRequest() with { OverwriteOutput = false };

        var error = Assert.ThrowsExactly<DesktopAutomationException>(
            () => AutomationQueuePolicy.ValidateEnqueue(request, [], static _ => true));

        StringAssert.Contains(error.Message, "Output file already exists");
        AutomationQueuePolicy.ValidateEnqueue(request with { OverwriteOutput = true }, [], static _ => true);
    }

    [TestMethod]
    public void QueuePolicy_RejectsDuplicateIdsAndSourceAsOutput()
    {
        var request = CreateRequest();

        Assert.ThrowsExactly<DesktopAutomationException>(
            () => AutomationQueuePolicy.ValidateEnqueue(request, [request with { OutputPath = @"C:\media\other.264" }], static _ => false));
        Assert.ThrowsExactly<DesktopAutomationException>(
            () => AutomationQueuePolicy.ValidateEnqueue(request with { OutputPath = request.SourcePath }, [], static _ => false));
    }

    [TestMethod]
    public void QueuePolicy_AllowsReusingTheOutputOfFinishedJobs()
    {
        // Finished jobs are not passed as active requests, so their output path is free again.
        AutomationQueuePolicy.ValidateEnqueue(CreateRequest(), [], static _ => false);
    }

    [TestMethod]
    public void TaskRequests_RoundTripThroughTheWireFormat()
    {
        var playlist = new BluRayPlaylistItem("00800", "00800.mpls", "2:01:00", @"D:\BD\BDMV\PLAYLIST\00800.mpls", "1)", "2:01:00", TimeSpan.FromHours(2), 24);
        var track = new BluRayTrackItem("3", 3, "3:", BluRayTrackKind.Audio, "TrueHD", "7.1", "English", true);
        object[] requests =
        [
            CreateRequest() with { OverwriteOutput = false, UseAv1anParallelVideoEncoding = true, Av1anParallelWorkers = 6 },
            new AudioProcessingRequest(Guid.NewGuid(), @"C:\a.thd", @"C:\a.opus", AudioProcessingMode.Opus, null, ["-down16"], 12.5, 8, "7.1", 384, true),
            new BluRayDemuxRequest(Guid.NewGuid(), BluRayDemuxBackend.Eac3To, @"D:\BD", @"D:\out", @"D:\out\00800", playlist, [new BluRayTrackSelection(track, @"D:\out\00800_T03_audio_english.*")]),
            new AutoCompressionRequest(Guid.NewGuid(), @"C:\s.mkv", @"C:\s.svtav1.vmaf95.mkv", EncoderKind.SvtAv1, AutoCompressionMetric.Ssimulacra2, 80, 4, "--preset 4", "", 8,
                new AutoCompressionSearchProfile("linear-pchip", "median", "1280x720", 2, false)),
        ];

        foreach (var request in requests)
        {
            var json = JsonSerializer.Serialize(request, request.GetType(), DesktopAutomationProtocol.JsonOptions);
            var restored = JsonSerializer.Deserialize(json, request.GetType(), DesktopAutomationProtocol.JsonOptions);

            Assert.AreEqual(json, JsonSerializer.Serialize(restored, request.GetType(), DesktopAutomationProtocol.JsonOptions), request.GetType().Name);
            StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("\"(mode|backend|metric|kind)\":\\d"), "enums travel as names");
        }
    }

    [TestMethod]
    public void SettingsPatch_ChangesOnlyGivenFields()
    {
        var current = AppSettings.Default with { MaxConcurrentEncodingJobs = 2, WorkspaceRootPath = @"D:\ws" };
        var patch = new AutomationSettingsPatch(MaxConcurrentEncodingJobs: 99, QueueCompletionAction: QueueCompletionAction.Shutdown);

        var updated = patch.ApplyTo(current);

        Assert.IsFalse(patch.IsEmpty);
        Assert.IsTrue(new AutomationSettingsPatch().IsEmpty);
        Assert.AreEqual(RequestValidation.MaxConcurrentEncodingJobs, updated.MaxConcurrentEncodingJobs, "normalized to the allowed range");
        Assert.AreEqual(QueueCompletionAction.Shutdown, updated.QueueCompletionAction);
        Assert.AreEqual(@"D:\ws", updated.WorkspaceRootPath);
        Assert.AreEqual(current.Theme, updated.Theme);
    }

    internal static EncodingJobRequest CreateRequest() => new(
        Guid.NewGuid(),
        DefaultEncodingProfiles.GetDefault(EncoderKind.X264),
        @"C:\media\source.mkv",
        $@"C:\media\out-{Guid.NewGuid():N}.264",
        InputPipelineKind.FfmpegPipe,
        EncoderArchitecture.X64);

    internal static DesktopAutomationServer StartServer(Func<string, JsonElement?, Task<object?>> handler)
    {
        var server = new DesktopAutomationServer(
            $"FlowEncode.Automation.Test.{Guid.NewGuid():N}",
            (method, parameters, _) => handler(method, parameters));
        server.Start();
        return server;
    }
}

using System.Text.Json;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using FlowEncode.Application;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class VapourSynthPreviewServiceTests
{
    [TestMethod]
    public async Task OpenSessionAsync_WhenReadyResponseReceived_ReturnsSortedOutputs()
    {
        using var context = CreateContext();
        context.Session.EnqueueResponse(CreateReadyResponseJson(
            (1, "Output B", 1280, 720, 200),
            (0, "Output A", 1920, 1080, 100)));

        var result = await context.Service.OpenSessionAsync(CreateOpenRequest());

        CollectionAssert.AreEqual(new[] { 0, 1 }, result.Outputs.Select(static output => output.Index).ToArray());
        Assert.AreEqual("Output A", result.Outputs[0].Name);
        Assert.AreEqual("Output B", result.Outputs[1].Name);
    }

    [TestMethod]
    public async Task OpenSessionAsync_WhenLogResponsePrecedesReady_EmitsLogAndSucceeds()
    {
        using var context = CreateContext();
        var emittedLogs = new List<VapourSynthPreviewLogEntry>();
        context.Service.LogEmitted += (_, args) => emittedLogs.Add(args.Entry);
        context.Session.EnqueueResponse(CreateLogResponseJson("warning", "helper", "warming up"));
        context.Session.EnqueueResponse(CreateReadyResponseJson((0, "Preview", 1920, 1080, 100)));

        var result = await context.Service.OpenSessionAsync(CreateOpenRequest());

        Assert.AreEqual(1, result.Outputs.Count);
        Assert.AreEqual(1, emittedLogs.Count);
        Assert.AreEqual(VapourSynthPreviewLogLevel.Warning, emittedLogs[0].Level);
        Assert.AreEqual("helper", emittedLogs[0].Source);
        Assert.AreEqual("warming up", emittedLogs[0].Message);
    }

    [TestMethod]
    public async Task OpenSessionAsync_WhenStartupStderrArrivesBeforeHostReturns_PreservesDiagnosticContext()
    {
        using var context = CreateContext();
        var emittedLogs = new List<VapourSynthPreviewLogEntry>();
        context.Service.LogEmitted += (_, args) => emittedLogs.Add(args.Entry);
        context.Factory.StartupStderrLine = "Traceback: helper failed before ready";

        var exception = await AssertThrowsAsync<InvalidOperationException>(
            () => context.Service.OpenSessionAsync(CreateOpenRequest()));

        Assert.AreEqual(1, emittedLogs.Count);
        Assert.AreEqual(VapourSynthPreviewLogLevel.Error, emittedLogs[0].Level);
        StringAssert.Contains(emittedLogs[0].Message, "Traceback");
        StringAssert.Contains(exception.Message, "Traceback");
    }

    [TestMethod]
    public async Task RenderFrameAsync_WhenRequestIdMismatch_Throws()
    {
        using var context = CreateContext();
        context.Session.EnqueueResponse(CreateReadyResponseJson((0, "Preview", 1920, 1080, 100)));
        context.Session.EnqueueResponse(CreateFrameResponseJson(
            requestId: 999,
            outputIndex: 0,
            frameNumber: 12,
            width: 2,
            height: 1,
            pixelBytes: 8));
        await context.Service.OpenSessionAsync(CreateOpenRequest());

        var exception = await AssertThrowsAsync<InvalidOperationException>(
            () => context.Service.RenderFrameAsync(0, 12));

        StringAssert.Contains(exception.Message, "mismatched frame response");
    }

    [TestMethod]
    public async Task RenderFrameAsync_WhenPixelBytesMissing_Throws()
    {
        using var context = CreateContext();
        context.Session.EnqueueResponse(CreateReadyResponseJson((0, "Preview", 1920, 1080, 100)));
        context.Session.EnqueueResponse(CreateFrameResponseJson(
            requestId: 1,
            outputIndex: 0,
            frameNumber: 5,
            width: 2,
            height: 1,
            pixelBytes: 0));
        await context.Service.OpenSessionAsync(CreateOpenRequest());

        var exception = await AssertThrowsAsync<InvalidOperationException>(
            () => context.Service.RenderFrameAsync(0, 5));

        StringAssert.Contains(exception.Message, "did not produce a frame buffer");
    }

    [TestMethod]
    public async Task RenderFrameAsync_WhenFrameResponseValid_ReturnsSharedMemoryPixels()
    {
        using var context = CreateContext();
        var expectedPixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        context.Session.EnqueueResponse(CreateReadyResponseJson((0, "Preview", 1920, 1080, 100)));
        context.Session.EnqueueFramePixels(expectedPixels);
        context.Session.EnqueueResponse(CreateFrameResponseJson(
            requestId: 1,
            outputIndex: 0,
            frameNumber: 5,
            width: 2,
            height: 1,
            pixelBytes: expectedPixels.Length));
        await context.Service.OpenSessionAsync(CreateOpenRequest());

        var reusableBuffer = new byte[expectedPixels.Length];
        var frame = await context.Service.RenderFrameAsync(0, 5, reusableBuffer);

        Assert.AreSame(reusableBuffer, frame.Pixels);
        CollectionAssert.AreEqual(expectedPixels, frame.Pixels);
        Assert.IsTrue(frame.HelperRenderElapsed >= TimeSpan.Zero);
        Assert.IsTrue(frame.TransportReadElapsed >= TimeSpan.Zero);
    }

    [TestMethod]
    public async Task CloseSessionAsync_WhenGracefulCloseTimesOut_KillsHostAndCleansSession()
    {
        using var context = CreateContext();
        context.Session.EnqueueResponse(CreateReadyResponseJson((0, "Preview", 1920, 1080, 100)));
        context.Session.EnqueueCancellationWaitBehavior(static cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken));
        context.Session.EnqueueWaitBehavior(session =>
        {
            session.HasExited = true;
            return Task.CompletedTask;
        });

        await context.Service.OpenSessionAsync(CreateOpenRequest());
        var sessionDirectory = Path.GetDirectoryName(context.Factory.StartupPath)!;
        Assert.IsTrue(Directory.Exists(sessionDirectory));

        await context.Service.CloseSessionAsync();

        Assert.IsTrue(context.Session.KillCalled);
        Assert.IsTrue(context.Session.WrittenLines.Any(static line => line.Contains("\"command\":\"close\"", StringComparison.Ordinal)));
        Assert.IsFalse(Directory.Exists(sessionDirectory));
    }

    [TestMethod]
    public async Task Dispose_WhenCloseSessionIsInProgress_WaitsForCloseInsteadOfClosingHostConcurrently()
    {
        using var context = CreateContext();
        var gracefulCloseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowGracefulClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Session.EnqueueResponse(CreateReadyResponseJson((0, "Preview", 1920, 1080, 100)));
        context.Session.EnqueueWaitBehavior(async session =>
        {
            gracefulCloseStarted.SetResult();
            await allowGracefulClose.Task;
            session.HasExited = true;
        });

        await context.Service.OpenSessionAsync(CreateOpenRequest());

        var closeTask = context.Service.CloseSessionAsync();
        await gracefulCloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTask = Task.Run(context.Service.Dispose);
        await Task.Delay(100);

        Assert.IsFalse(disposeTask.IsCompleted);
        allowGracefulClose.SetResult();
        await closeTask.WaitAsync(TimeSpan.FromSeconds(5));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(context.Session.KillCalled);
        Assert.AreEqual(1, context.Session.WrittenLines.Count(static line => line.Contains("\"command\":\"close\"", StringComparison.Ordinal)));
    }

    private static TestContext CreateContext()
    {
        var tempRootPath = Path.Combine(Path.GetTempPath(), "FlowEncodePreviewTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);

        var session = new FakePreviewHostSession();
        var factory = new FakePreviewHostFactory(session);
        var service = new VapourSynthPreviewService(
            new LocalAppPaths(),
            factory,
            sessionRootPath: tempRootPath);

        return new TestContext(tempRootPath, service, factory, session);
    }

    private static VapourSynthPreviewOpenRequest CreateOpenRequest()
    {
        return new VapourSynthPreviewOpenRequest(
            SourceFilePath: @"D:\preview\script.vpy",
            DisplayName: "script.vpy",
            Content: "clip = core.std.BlankClip()",
            WorkingDirectory: Path.GetTempPath());
    }

    private static string CreateReadyResponseJson(params (int Index, string Name, int Width, int Height, int TotalFrames)[] outputs)
    {
        return JsonSerializer.Serialize(new
        {
            type = "ready",
            outputs = outputs.Select(output => new
            {
                index = output.Index,
                name = output.Name,
                width = output.Width,
                height = output.Height,
                totalFrames = output.TotalFrames,
                fpsNumerator = 24000,
                fpsDenominator = 1001,
                formatName = "YUV420P8",
                bitsPerSample = 8
            }).ToArray()
        });
    }

    private static string CreateLogResponseJson(string level, string source, string message)
    {
        return JsonSerializer.Serialize(new
        {
            type = "log",
            level,
            source,
            message
        });
    }

    private static string CreateFrameResponseJson(
        int requestId,
        int outputIndex,
        int frameNumber,
        int width,
        int height,
        int pixelBytes)
    {
        return JsonSerializer.Serialize(new
        {
            type = "frame",
            requestId,
            outputIndex,
            frameNumber,
            width,
            height,
            pixelBytes,
            frameType = "I",
            properties = Array.Empty<object>()
        });
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            return ex;
        }

        Assert.Fail($"Expected exception of type {typeof(TException).Name} was not thrown.");
        throw new InvalidOperationException("Unreachable assertion path.");
    }

    private sealed class TestContext : IDisposable
    {
        public TestContext(
            string tempRootPath,
            VapourSynthPreviewService service,
            FakePreviewHostFactory factory,
            FakePreviewHostSession session)
        {
            TempRootPath = tempRootPath;
            Service = service;
            Factory = factory;
            Session = session;
        }

        public string TempRootPath { get; }

        public VapourSynthPreviewService Service { get; }

        public FakePreviewHostFactory Factory { get; }

        public FakePreviewHostSession Session { get; }

        public void Dispose()
        {
            Service.Dispose();

            if (Directory.Exists(TempRootPath))
            {
                Directory.Delete(TempRootPath, recursive: true);
            }
        }
    }

    private sealed class FakePreviewHostFactory : IVapourSynthPreviewHostFactory
    {
        private readonly FakePreviewHostSession _session;

        public FakePreviewHostFactory(FakePreviewHostSession session)
        {
            _session = session;
        }

        public string? StartupPath { get; private set; }

        public string? StartupStderrLine { get; set; }

        public Task<IVapourSynthPreviewHostSession> StartAsync(
            string workingDirectory,
            string startupPath,
            Action<string>? stderrLineHandler,
            CancellationToken cancellationToken = default)
        {
            StartupPath = startupPath;
            _session.SetStderrLineHandler(stderrLineHandler);

            if (!string.IsNullOrWhiteSpace(StartupStderrLine))
            {
                _session.EmitStderrLine(StartupStderrLine);
            }

            return Task.FromResult<IVapourSynthPreviewHostSession>(_session);
        }
    }

    private sealed class FakePreviewHostSession : IVapourSynthPreviewHostSession
    {
        private readonly Queue<byte[]?> _framePixels = new();
        private readonly Queue<string?> _responses = new();
        private readonly Queue<Func<FakePreviewHostSession, CancellationToken, Task>> _waitBehaviors = new();
        private Action<string>? _stderrLineHandler;

        public List<string> WrittenLines { get; } = [];

        public bool HasExited { get; set; }

        public int ProcessId { get; set; } = 4242;

        public bool KillCalled { get; private set; }

        public void EnqueueResponse(string response)
        {
            _responses.Enqueue(response);
        }

        public void EnqueueFramePixels(byte[] framePixels)
        {
            _framePixels.Enqueue(framePixels);
        }

        public void SetStderrLineHandler(Action<string>? stderrLineHandler)
        {
            _stderrLineHandler = stderrLineHandler;
        }

        public void EnqueueWaitBehavior(Func<FakePreviewHostSession, Task> behavior)
        {
            _waitBehaviors.Enqueue((session, _) => behavior(session));
        }

        public void EnqueueCancellationWaitBehavior(Func<CancellationToken, Task> behavior)
        {
            _waitBehaviors.Enqueue((_, cancellationToken) => behavior(cancellationToken));
        }

        public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            WrittenLines.Add(line);
            TryWriteFramePixelsToSharedMemory(line);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : null);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            if (_waitBehaviors.Count > 0)
            {
                return _waitBehaviors.Dequeue()(this, cancellationToken);
            }

            HasExited = true;
            return Task.CompletedTask;
        }

        public void Kill(bool entireProcessTree = true)
        {
            KillCalled = true;
        }

        public void Dispose()
        {
        }

        public void EmitStderrLine(string line)
        {
            _stderrLineHandler?.Invoke(line);
        }

        private void TryWriteFramePixelsToSharedMemory(string line)
        {
            if (_framePixels.Count == 0)
            {
                return;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("command", out var commandElement)
                || !string.Equals(commandElement.GetString(), "renderFrame", StringComparison.Ordinal)
                || !root.TryGetProperty("sharedMemoryName", out var sharedMemoryNameElement)
                || !root.TryGetProperty("sharedMemoryCapacity", out var sharedMemoryCapacityElement))
            {
                return;
            }

            var pixels = _framePixels.Dequeue();
            if (pixels is null)
            {
                return;
            }

            var sharedMemoryName = sharedMemoryNameElement.GetString();
            var sharedMemoryCapacity = sharedMemoryCapacityElement.GetInt32();
            using var memoryMappedFile = MemoryMappedFile.OpenExisting(sharedMemoryName!);
            using var stream = memoryMappedFile.CreateViewStream(0, sharedMemoryCapacity, MemoryMappedFileAccess.Write);
            stream.Write(pixels, 0, pixels.Length);
            stream.Flush();
        }
    }
}

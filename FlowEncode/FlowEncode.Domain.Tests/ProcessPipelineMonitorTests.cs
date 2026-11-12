using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ProcessPipelineMonitorTests
{
    [TestMethod]
    public async Task WaitForFirstCompletionAsync_WhenPipeEndsBeforeProducerFails_StillObservesProducerExit()
    {
        var producerExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = ProcessPipelineMonitor.WaitForFirstCompletionAsync(
            producerExit.Task, consumerExit.Task, Task.FromResult(PipeCopyCompletion.Completed));

        Assert.IsFalse(monitor.IsCompleted, "EOF must not stop monitoring the producer's exit.");
        producerExit.SetResult();
        Assert.AreEqual(PipelineFirstCompletion.ProducerExited, await monitor.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task WaitForFirstCompletionAsync_WhenPipeEndsBeforeConsumerExits_StillObservesConsumerExit()
    {
        var producerExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = ProcessPipelineMonitor.WaitForFirstCompletionAsync(
            producerExit.Task, consumerExit.Task, Task.FromResult(PipeCopyCompletion.Completed));

        Assert.IsFalse(monitor.IsCompleted);
        consumerExit.SetResult();
        Assert.AreEqual(PipelineFirstCompletion.ConsumerExited, await monitor.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ObservePipeCopyAsync_WhenCopyThrowsIOException_ReturnsBrokenPipe()
    {
        var result = await ProcessPipelineMonitor.ObservePipeCopyAsync(Task.FromException(new IOException("pipe closed")));

        Assert.AreEqual(PipeCopyCompletion.BrokenPipe, result);
    }

    [TestMethod]
    public async Task WaitForFirstCompletionAsync_WhenPipeBreaksFirst_ReturnsPipeBroken()
    {
        var producerExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeCopy = Task.FromResult(PipeCopyCompletion.BrokenPipe);

        var result = await ProcessPipelineMonitor.WaitForFirstCompletionAsync(
            producerExit.Task,
            consumerExit.Task,
            pipeCopy);

        Assert.AreEqual(PipelineFirstCompletion.PipeBroken, result);
    }

    [TestMethod]
    public async Task WaitForFirstCompletionAsync_WhenConsumerExitsFirst_ReturnsConsumerExited()
    {
        var producerExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerExit = Task.CompletedTask;
        var pipeCopy = new TaskCompletionSource<PipeCopyCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await ProcessPipelineMonitor.WaitForFirstCompletionAsync(
            producerExit.Task,
            consumerExit,
            pipeCopy.Task);

        Assert.AreEqual(PipelineFirstCompletion.ConsumerExited, result);
    }
}

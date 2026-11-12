using System.Collections.Concurrent;
using FlowEncode.Infrastructure.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class DesktopAutomationShutdownTests
{
    [TestMethod]
    public async Task Shutdown_DoesNotRequireTheDesktopDispatcherToKeepPumping()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new DesktopAutomationServer("FlowEncode.Shutdown." + Guid.NewGuid().ToString("N"),
            async (_, _, _) => { entered.TrySetResult(); await release.Task.ConfigureAwait(false); return null; });
        server.Start();
        var client = new DesktopAutomationClient(server.PipeName).SendRawAsync("hold");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var context = new PausedDispatcher();
        var previous = SynchronizationContext.Current;
        Task shutdown;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            shutdown = server.DisposeAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        release.TrySetResult();
        var finished = await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(3))) == shutdown;
        // Also clean up on the failing implementation without leaving a pipe behind.
        context.Drain();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        try { await client; } catch (DesktopUnavailableException) { }
        Assert.IsTrue(finished, "Synchronous desktop shutdown must not wait for a dispatcher continuation.");
    }

    private sealed class PausedDispatcher : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void Drain() { while (_callbacks.TryDequeue(out var item)) item.Callback(item.State); }
    }
}

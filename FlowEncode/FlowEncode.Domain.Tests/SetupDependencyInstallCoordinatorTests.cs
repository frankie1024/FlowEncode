using FlowEncode.Application;
using FlowEncode.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class SetupDependencyInstallCoordinatorTests
{
    [TestMethod]
    public async Task IndependentTools_RunThreeAtATime_AndQueueTheFourth()
    {
        var coordinator = new SetupDependencyInstallCoordinator();
        var release = Signal();
        var fourthStarted = Signal();
        var running = 0;
        var maximum = 0;
        Task Install(CancellationToken _)
        {
            var count = Interlocked.Increment(ref running);
            maximum = Math.Max(maximum, count);
            return FinishAsync();
        }
        async Task FinishAsync()
        {
            await release.Task;
            Interlocked.Decrement(ref running);
        }

        var first = coordinator.RunAsync(SetupDependencyKind.FfmpegBundle, Install);
        var second = coordinator.RunAsync(SetupDependencyKind.X264, Install);
        var third = coordinator.RunAsync(SetupDependencyKind.X265, Install);
        var fourth = coordinator.RunAsync(SetupDependencyKind.SvtAv1, _ =>
        {
            fourthStarted.SetResult();
            return Install(_);
        });

        Assert.AreEqual(3, running);
        Assert.IsFalse(fourthStarted.Task.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second, third, fourth).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(3, maximum);
        Assert.IsTrue(fourthStarted.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task PythonWaiters_DoNotBlockIndependentTools_OrModifyPythonTogether()
    {
        var coordinator = new SetupDependencyInstallCoordinator();
        var releasePython = Signal();
        var releaseTools = Signal();
        var vsStarted = Signal();
        var vsrepoStarted = Signal();
        var python = coordinator.RunAsync(SetupDependencyKind.Python312, _ => releasePython.Task);
        var vs = coordinator.RunAsync(SetupDependencyKind.VapourSynth, _ =>
        {
            vsStarted.SetResult();
            return Task.CompletedTask;
        });
        var vsrepo = coordinator.RunAsync(SetupDependencyKind.Vsrepo, _ =>
        {
            vsrepoStarted.SetResult();
            return Task.CompletedTask;
        });
        var toolStarts = 0;
        Task InstallTool(CancellationToken _)
        {
            toolStarts++;
            return releaseTools.Task;
        }
        var ffmpeg = coordinator.RunAsync(SetupDependencyKind.FfmpegBundle, InstallTool);
        var x264 = coordinator.RunAsync(SetupDependencyKind.X264, InstallTool);

        Assert.AreEqual(2, toolStarts);
        Assert.IsFalse(vsStarted.Task.IsCompleted);
        Assert.IsFalse(vsrepoStarted.Task.IsCompleted);
        releasePython.SetResult();
        releaseTools.SetResult();
        await Task.WhenAll(python, vs, vsrepo, ffmpeg, x264).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task PythonPackages_SerializeEvenWithoutAnActivePrerequisite()
    {
        var coordinator = new SetupDependencyInstallCoordinator();
        var release = Signal();
        var secondStarted = Signal();
        var first = coordinator.RunAsync(SetupDependencyKind.Awsmfunc, _ => release.Task);
        var second = coordinator.RunAsync(SetupDependencyKind.Vsjetpack, _ =>
        {
            secondStarted.SetResult();
            return Task.CompletedTask;
        });

        Assert.IsFalse(secondStarted.Task.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(secondStarted.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task FailedPrerequisite_StopsDependent_ButAllowsIndependentToolAndRetry()
    {
        var coordinator = new SetupDependencyInstallCoordinator();
        var release = Signal();
        var dependentStarted = false;
        var python = coordinator.RunAsync(SetupDependencyKind.Python312, async _ =>
        {
            await release.Task;
            throw new InvalidOperationException("Python failed");
        });
        var dependent = coordinator.RunAsync(SetupDependencyKind.VapourSynth, _ =>
        {
            dependentStarted = true;
            return Task.CompletedTask;
        });
        await coordinator.RunAsync(SetupDependencyKind.X264, _ => Task.CompletedTask);
        release.SetResult();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await python);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await dependent);
        Assert.IsFalse(dependentStarted);
        await coordinator.RunAsync(SetupDependencyKind.Python312, _ => Task.CompletedTask);
        await coordinator.RunAsync(SetupDependencyKind.VapourSynth, _ => Task.CompletedTask);
    }

    [TestMethod]
    public async Task PluginBundle_WaitsForBothRuntimeAndVsrepo()
    {
        var coordinator = new SetupDependencyInstallCoordinator();
        var releaseRuntime = Signal();
        var releaseVsrepo = Signal();
        var vsrepoStarted = Signal();
        var bundleStarted = Signal();
        var runtime = coordinator.RunAsync(SetupDependencyKind.VapourSynth, _ => releaseRuntime.Task);
        var vsrepo = coordinator.RunAsync(SetupDependencyKind.Vsrepo, _ =>
        {
            vsrepoStarted.SetResult();
            return releaseVsrepo.Task;
        });
        var bundle = coordinator.RunAsync(SetupDependencyKind.VsPluginBundle, _ =>
        {
            bundleStarted.SetResult();
            return Task.CompletedTask;
        });

        Assert.IsFalse(bundleStarted.Task.IsCompleted);
        releaseRuntime.SetResult();
        await vsrepoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(bundleStarted.Task.IsCompleted);
        releaseVsrepo.SetResult();
        await Task.WhenAll(runtime, vsrepo, bundle).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(bundleStarted.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task DuplicateDependency_IsRejectedUntilOperationCompletes()
    {
        var coordinator = new SetupDependencyInstallCoordinator();
        var release = Signal();
        var first = coordinator.RunAsync(SetupDependencyKind.X264, _ => release.Task);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            coordinator.RunAsync(SetupDependencyKind.X264, _ => Task.CompletedTask));
        release.SetResult();
        await first;
        await coordinator.RunAsync(SetupDependencyKind.X264, _ => Task.CompletedTask);
    }

    [TestMethod]
    public async Task CanceledWaiter_DoesNotRun_AndCanBeRetried()
    {
        var coordinator = new SetupDependencyInstallCoordinator();
        var release = Signal();
        var python = coordinator.RunAsync(SetupDependencyKind.Python312, _ => release.Task);
        using var cancellation = new CancellationTokenSource();
        var started = false;
        var waiter = coordinator.RunAsync(SetupDependencyKind.VapourSynth, _ =>
        {
            started = true;
            return Task.CompletedTask;
        }, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiter);
        Assert.IsFalse(started);
        release.SetResult();
        await python;
        await coordinator.RunAsync(SetupDependencyKind.VapourSynth, _ => Task.CompletedTask);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

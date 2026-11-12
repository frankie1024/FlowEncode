using System.Diagnostics;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ProcessExecutionMonitorTests
{
    [TestMethod]
    public async Task ReaderFailure_InterruptsAProcessThatWouldOtherwiseWaitForever()
    {
        using var f = new BusinessExecutionSafetyTests.Fixture("probe-hang");
        using var process = new Process { StartInfo = new(Path.Combine(f.Root, "av1an.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        }};
        process.Start();
        var processId = process.Id;
        using (var execution = new ManagedProcessExecution(process))
        {
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => ProcessExecutionMonitor.WaitForExitAsync(process,
                CancellationToken.None, Task.FromException(new IOException("reader failed"))).WaitAsync(TimeSpan.FromSeconds(3)));
            StringAssert.Contains(error.Message, "reader failed");
        }
        try
        {
            using var remaining = Process.GetProcessById(processId);
            Assert.IsTrue(remaining.HasExited);
        }
        catch (ArgumentException) { /* The process has already been reaped. */ }
    }

    [TestMethod]
    public async Task ProbeReaderCallbackFailure_StopsTheProcessPromptly()
    {
        var script = Path.Combine(Path.GetTempPath(), "FlowEncodeReaderFailure-" + Guid.NewGuid().ToString("N") + ".cmd");
        File.WriteAllText(script, "@echo off\necho source failure 1>&2\nping -n 20 127.0.0.1 >nul\n");
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                ArgumentList = { "/d", "/c", script }
            };
            var timer = Stopwatch.StartNew();
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => ProcessProbeRunner.RunAsync(info, TimeSpan.FromSeconds(30),
                "timeout", standardErrorProgress: _ => throw new IOException("progress callback failed")));
            StringAssert.Contains(error.Message, "progress callback failed");
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5), timer.Elapsed.ToString());
        }
        finally { File.Delete(script); }
    }
}

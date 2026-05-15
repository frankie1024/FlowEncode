using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ProcessProbeRunnerTests
{
    [TestMethod]
    public void Run_ReadsStdoutAndStderrConcurrently()
    {
        var scriptPath = WriteTempCommandScript(
            """
            @echo off
            for /l %%i in (1,1,4000) do (
              echo out%%i
              echo err%%i 1>&2
            )
            """);

        try
        {
            var result = ProcessProbeRunner.Run(
                CreateCmdStartInfo(scriptPath),
                TimeSpan.FromSeconds(10),
                "test command timed out");

            Assert.AreEqual(0, result.ExitCode);
            StringAssert.Contains(result.StandardOutput, "out4000");
            StringAssert.Contains(result.StandardError, "err4000");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [TestMethod]
    public void Run_WhenTimedOut_KillsProcessAndThrows()
    {
        var scriptPath = WriteTempCommandScript(
            """
            @echo off
            ping -n 6 127.0.0.1 > nul
            """);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                ProcessProbeRunner.Run(
                    CreateCmdStartInfo(scriptPath),
                    TimeSpan.FromMilliseconds(100),
                    "test command timed out"));

            stopwatch.Stop();
            StringAssert.Contains(exception.Message, "test command timed out");
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Timeout took too long: {stopwatch.Elapsed}");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [TestMethod]
    public void Run_WhenCancelled_KillsProcessAndPropagatesCancellation()
    {
        var scriptPath = WriteTempCommandScript(
            """
            @echo off
            ping -n 6 127.0.0.1 > nul
            """);

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            var stopwatch = Stopwatch.StartNew();
            Assert.ThrowsExactly<TaskCanceledException>(() =>
                ProcessProbeRunner.Run(
                    CreateCmdStartInfo(scriptPath),
                    TimeSpan.FromSeconds(10),
                    "test command timed out",
                    cancellationTokenSource.Token));

            stopwatch.Stop();
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Cancellation took too long: {stopwatch.Elapsed}");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static ProcessStartInfo CreateCmdStartInfo(string scriptPath)
    {
        return new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList =
            {
                "/d",
                "/c",
                scriptPath
            }
        };
    }

    private static string WriteTempCommandScript(string contents)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"flowencode-probe-test-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(scriptPath, contents);
        return scriptPath;
    }
}

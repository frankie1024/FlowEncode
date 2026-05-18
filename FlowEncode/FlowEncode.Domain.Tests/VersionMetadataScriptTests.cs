using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class VersionMetadataScriptTests
{
    [TestMethod]
    public void SyncVersionMetadataCheck_Succeeds_ForCurrentRepositoryState()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "sync-version-metadata.ps1");
        Assert.IsTrue(File.Exists(scriptPath), $"Version sync script was not found: {scriptPath}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolvePowerShellExecutable(),
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -Check",
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        var exited = process.WaitForExit(30000);

        Assert.IsTrue(exited, "Version sync script timed out.");
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Version sync script failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{standardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{standardError}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "sync-version-metadata.ps1");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate repository root from test output directory.");
        return string.Empty;
    }

    private static string ResolvePowerShellExecutable()
    {
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-NoLogo -NoProfile -Command \"$PSVersionTable.PSVersion.ToString()\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });

                if (process is null)
                {
                    continue;
                }

                if (process.WaitForExit(5000) && process.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        Assert.Fail("Neither 'pwsh' nor 'powershell' is available for script regression tests.");
        return string.Empty;
    }
}

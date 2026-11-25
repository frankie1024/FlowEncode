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

    [TestMethod]
    public void SanitizeReleaseLayout_PreservesRequiredAssetsAndThirdPartyNotices()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "release-artifact-common.ps1");
        Assert.IsTrue(File.Exists(scriptPath), $"Release artifact common script was not found: {scriptPath}");

        var tempRoot = Path.Combine(Path.GetTempPath(), $"flowencode-sanitize-{Guid.NewGuid():N}");
        try
        {
            var assetsRoot = Path.Combine(tempRoot, "Assets");
            var thirdPartyNoticesDir = Path.Combine(assetsRoot, "ThirdPartyNotices");
            var editorDir = Path.Combine(assetsRoot, "VapourSynthEditor");
            var extraDir = Path.Combine(assetsRoot, "UnusedDir");

            Directory.CreateDirectory(thirdPartyNoticesDir);
            Directory.CreateDirectory(editorDir);
            Directory.CreateDirectory(extraDir);

            var hammerPath = Path.Combine(assetsRoot, "FlowEncodeHammer.png");
            var templatePath = Path.Combine(assetsRoot, "VapourSynthScript.vpy");
            var unusedLogoPath = Path.Combine(assetsRoot, "StoreLogo.png");
            var yacompNoticePath = Path.Combine(thirdPartyNoticesDir, "yacomp.txt");
            var editorIndexPath = Path.Combine(editorDir, "index.html");
            var extraFilePath = Path.Combine(extraDir, "junk.txt");

            File.WriteAllText(hammerPath, "icon");
            File.WriteAllText(templatePath, string.Empty);
            File.WriteAllText(unusedLogoPath, "unused");
            File.WriteAllText(yacompNoticePath, "MIT");
            File.WriteAllText(editorIndexPath, "<html/>");
            File.WriteAllText(extraFilePath, "junk");

            var command = $". '{scriptPath.Replace("'", "''")}'; Sanitize-ReleaseLayout -RootPath '{tempRoot.Replace("'", "''")}'";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolvePowerShellExecutable(),
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
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

            Assert.IsTrue(exited, "Sanitize-ReleaseLayout timed out.");
            Assert.AreEqual(
                0,
                process.ExitCode,
                $"Sanitize-ReleaseLayout failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{standardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{standardError}");

            Assert.IsTrue(File.Exists(hammerPath), "FlowEncodeHammer.png must be preserved.");
            Assert.IsTrue(File.Exists(templatePath), "VapourSynthScript.vpy must be preserved.");
            Assert.IsTrue(File.Exists(yacompNoticePath), "Assets\\ThirdPartyNotices\\yacomp.txt must be preserved.");
            Assert.IsTrue(File.Exists(editorIndexPath), "Assets\\VapourSynthEditor\\index.html must be preserved.");
            Assert.IsFalse(File.Exists(unusedLogoPath), "Unused StoreLogo.png should be removed.");
            Assert.IsFalse(Directory.Exists(extraDir), "Unused asset directory should be removed.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
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

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class SystemPathIntegrationTests
{
    [TestMethod]
    public void BuildPlan_AppendsMissingDirectoriesWithoutChangingExistingPath()
    {
        const string current = @"%SystemRoot%\system32;;C:\Other App\bin;";
        var plan = SystemPathIntegration.BuildPlan(current, [], [@"D:\FlowEncode\tools", @"D:\FlowEncode\encoders\x265\x64"]);

        Assert.AreEqual(current + @"D:\FlowEncode\tools;D:\FlowEncode\encoders\x265\x64", plan.Path);
        CollectionAssert.AreEqual(new[] { @"D:\FlowEncode\tools", @"D:\FlowEncode\encoders\x265\x64" }, plan.OwnedDirectories.ToArray());
    }

    [TestMethod]
    public void BuildPlan_PreexistingDirectoryIsNotOwnedOrDuplicated()
    {
        const string current = "C:\\Windows;\"D:\\Tools\\\";C:\\Other";
        var first = SystemPathIntegration.BuildPlan(current, [], [@"d:\tools", @"D:\TOOLS\"]);
        var second = SystemPathIntegration.BuildPlan(first.Path, first.OwnedDirectories, [@"D:\Tools"]);

        Assert.AreEqual(current, first.Path);
        Assert.AreEqual(current, second.Path);
        Assert.AreEqual(0, second.OwnedDirectories.Count);
    }

    [TestMethod]
    public void BuildPlan_RepeatedRegistrationDoesNotChangePathOrLoseOwnership()
    {
        var desired = new[] { @"D:\FlowEncode\tools\ffmpeg", @"D:\FlowEncode\tools\av1an" };
        var first = SystemPathIntegration.BuildPlan(@"C:\Windows", [], desired);
        var second = SystemPathIntegration.BuildPlan(first.Path, first.OwnedDirectories, desired);

        Assert.AreEqual(first.Path, second.Path);
        CollectionAssert.AreEqual(first.OwnedDirectories.ToArray(), second.OwnedDirectories.ToArray());
    }

    [TestMethod]
    public void BuildPlan_AfterWorkspaceMoveRemovesOnlyPreviouslyAddedDirectories()
    {
        var plan = SystemPathIntegration.BuildPlan(
            @"C:\Windows;D:\OldWorkspace\tools;D:\OldWorkspace\tools\ffmpeg;C:\Python;C:\Other",
            [@"D:\OldWorkspace\tools\ffmpeg", @"C:\Python"],
            [@"E:\NewWorkspace\tools", @"E:\NewWorkspace\tools\ffmpeg", @"C:\Python"]);

        Assert.AreEqual(@"C:\Windows;D:\OldWorkspace\tools;C:\Python;C:\Other;E:\NewWorkspace\tools;E:\NewWorkspace\tools\ffmpeg", plan.Path);
        CollectionAssert.AreEqual(new[] { @"E:\NewWorkspace\tools", @"E:\NewWorkspace\tools\ffmpeg", @"C:\Python" }, plan.OwnedDirectories.ToArray());
    }

    [TestMethod]
    public void BuildPlan_RemovedToolsDoNotLeaveOwnedDirectoriesBehind()
    {
        var plan = SystemPathIntegration.BuildPlan(
            @"C:\Windows;D:\FlowEncode\tools\ffmpeg;C:\Other",
            [@"D:\FlowEncode\tools\ffmpeg"],
            []);

        Assert.AreEqual(@"C:\Windows;C:\Other", plan.Path);
        Assert.AreEqual(0, plan.OwnedDirectories.Count);
    }

    [TestMethod]
    public void BuildPlan_RejectsDirectoriesThatCannotBeRepresentedInPath()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => SystemPathIntegration.BuildPlan(@"C:\Windows", [], [@"D:\Tools;Other"]));
        Assert.ThrowsExactly<InvalidOperationException>(() => SystemPathIntegration.BuildPlan(@"C:\Windows", [], [string.Empty]));
        Assert.ThrowsExactly<InvalidOperationException>(() => SystemPathIntegration.BuildPlan(new string('a', 32766), [], [@"D:\Tools"]));
    }

    [TestMethod]
    public void ExistingComponentUpdatesPreserveMachinePathOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SystemPathIntegrationTests), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LocalAppPaths(root, root, [Path.Combine(root, "workspace")]);
            File.WriteAllText(paths.EnvironmentOwnershipPath, """
                {"schemaVersion":1,"managedUserPathEntries":[],"previousUserEnvironmentVariables":{},"components":{},"managedMachinePathEntries":["D:\\FlowEncode\\tools\\ffmpeg"]}
                """);
            var service = new CliEnvironmentIntegrationService(paths);
            service.RecordComponentOwnership("python-package:vsrepo", false, @"C:\Python\python.exe", "1.0");
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(paths.EnvironmentOwnershipPath));

            Assert.AreEqual(@"D:\FlowEncode\tools\ffmpeg", saved.RootElement.GetProperty("managedMachinePathEntries")[0].GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildSystemPathEntries_IncludesInstalledEncodersAndPinnedToolsButSkipsEmptyAndMissingPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(SystemPathIntegrationTests), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new LocalAppPaths(root, root, [Path.Combine(root, "workspace")]);
            var ffmpegDirectory = paths.GetManagedExternalToolDirectory(ExternalToolKind.Ffmpeg);
            var x265Directory = paths.GetBinaryDirectory(EncoderKind.X265, EncoderArchitecture.X64);
            var pinnedDirectory = Path.Combine(root, "Pinned tool's 中文 $bin");
            var pythonDirectory = Path.Combine(root, "Python");
            foreach (var directory in new[] { ffmpegDirectory, x265Directory, pinnedDirectory, pythonDirectory, Path.Combine(pythonDirectory, "Scripts") })
            {
                Directory.CreateDirectory(directory);
            }

            var pinnedPath = Path.Combine(pinnedDirectory, "dee.exe");
            var pythonPath = Path.Combine(pythonDirectory, "python.exe");
            foreach (var executable in new[] { Path.Combine(ffmpegDirectory, "ffmpeg.exe"), Path.Combine(x265Directory, "x265.exe"), pinnedPath, pythonPath, Path.Combine(pythonDirectory, "Scripts", "vspipe.exe") })
            {
                File.WriteAllText(executable, "test");
            }

            var report = new EnvironmentReadinessReport(DateTimeOffset.Now,
                [Probe(RegisteredToolKind.Dee, pinnedPath), Probe(RegisteredToolKind.Python, pythonPath), Probe(RegisteredToolKind.Ffprobe, Path.Combine(ffmpegDirectory, "missing.exe"))], []);
            var directories = new CliEnvironmentIntegrationService(paths).BuildSystemPathEntries(report);

            CollectionAssert.AreEquivalent(new[] { ffmpegDirectory, x265Directory, pinnedDirectory, pythonDirectory, Path.Combine(pythonDirectory, "Scripts") }, directories.ToArray());
            Assert.IsFalse(directories.Contains(paths.GetManagedExternalToolDirectory(ExternalToolKind.Av1an)));
            Assert.IsFalse(directories.Contains(paths.ToolsRootPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void ElevatedUpdateScript_TreatsUnusualPathValuesAsDataAndParsesInWindowsPowerShell()
    {
        const string expected = @"%SystemRoot%\system32;C:\O'Brien\$(exit 99)";
        const string next = expected + @";D:\中文工具\bin";
        var startInfo = SystemPathIntegration.BuildElevatedUpdateStartInfo(expected, next);
        Assert.AreEqual("runas", startInfo.Verb);
        Assert.AreEqual(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        var encoded = startInfo.Arguments.Split(' ')[^1];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.IsFalse(script.Contains(expected, StringComparison.Ordinal));
        Assert.IsFalse(script.Contains(next, StringComparison.Ordinal));
        StringAssert.Contains(script, Convert.ToBase64String(Encoding.Unicode.GetBytes(next)));

        // Parse the helper without executing it or modifying any registry value.
        var parserInfo = new ProcessStartInfo(startInfo.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        parserInfo.ArgumentList.Add("-NoProfile");
        parserInfo.ArgumentList.Add("-NonInteractive");
        parserInfo.ArgumentList.Add("-Command");
        parserInfo.ArgumentList.Add("$source = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + encoded + "')); $tokens = $null; $errors = $null; [void][Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors); if ($errors.Count) { $errors | Out-String | Write-Error; exit 1 }");
        using var parser = Process.Start(parserInfo)!;
        Assert.IsTrue(parser.WaitForExit(10000), "PowerShell parser did not finish.");
        Assert.AreEqual(0, parser.ExitCode, parser.StandardError.ReadToEnd());
    }

    private static ToolProbeResult Probe(RegisteredToolKind kind, string executablePath)
        => new(kind, ReadinessState.Ready, ToolDetectionSource.None, string.Empty, executablePath, string.Empty, string.Empty, string.Empty);
}

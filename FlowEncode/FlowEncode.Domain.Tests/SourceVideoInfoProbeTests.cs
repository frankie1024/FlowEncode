using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class SourceVideoInfoProbeTests
{
    private string? _testRoot;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeSourceVideoInfoProbeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (!string.IsNullOrWhiteSpace(_testRoot) && Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [TestMethod]
    public void Probe_WhenCacheLimitIsReached_ClearsOldCachedEntries()
    {
        var probe = new SourceVideoInfoProbe(CreateToolLocator(), maxCachedEntries: 3);

        for (var index = 0; index < 5; index++)
        {
            var sourcePath = WriteY4mFile(index);
            var sourceInfo = probe.Probe(
                sourcePath,
                InputPipelineKind.Y4mFile,
                allowCached: true);

            Assert.IsNotNull(sourceInfo);
            Assert.IsTrue(
                probe.CacheCountForTesting <= 3,
                $"Cache count exceeded limit after item {index}: {probe.CacheCountForTesting}");
        }
    }

    [TestMethod]
    public void VapourSynthFramePropertyProbeScriptResource_CanBeRead()
    {
        var script = SourceVideoInfoProbe.ReadVapourSynthFramePropertyProbeScriptForTesting();

        StringAssert.Contains(script, "import vapoursynth as vs");
        StringAssert.Contains(script, "json.dumps");
        StringAssert.Contains(script, "vs.get_output(0)");
    }

    [TestMethod]
    public void CleanupStaleFramePropertyProbeScriptsForTesting_DeletesOnlyOldMatchingScripts()
    {
        var now = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);
        var oldMatchingScript = Path.Combine(_testRoot!, "flowencode-vs-props-old.py");
        var freshMatchingScript = Path.Combine(_testRoot!, "flowencode-vs-props-fresh.py");
        var oldNonMatchingScript = Path.Combine(_testRoot!, "other.py");

        File.WriteAllText(oldMatchingScript, string.Empty);
        File.WriteAllText(freshMatchingScript, string.Empty);
        File.WriteAllText(oldNonMatchingScript, string.Empty);
        File.SetLastWriteTimeUtc(oldMatchingScript, now.AddDays(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(freshMatchingScript, now.AddMinutes(-10).UtcDateTime);
        File.SetLastWriteTimeUtc(oldNonMatchingScript, now.AddDays(-2).UtcDateTime);

        var deletedCount = SourceVideoInfoProbe.CleanupStaleFramePropertyProbeScriptsForTesting(
            _testRoot!,
            now,
            TimeSpan.FromDays(1));

        Assert.AreEqual(1, deletedCount);
        Assert.IsFalse(File.Exists(oldMatchingScript));
        Assert.IsTrue(File.Exists(freshMatchingScript));
        Assert.IsTrue(File.Exists(oldNonMatchingScript));
    }

    private ExternalToolLocator CreateToolLocator()
    {
        var localStatePath = Path.Combine(_testRoot!, "local-state");
        var installRootPath = Path.Combine(_testRoot!, "install-root");
        var workspaceRootPath = Path.Combine(_testRoot!, "workspace");
        Directory.CreateDirectory(localStatePath);
        Directory.CreateDirectory(installRootPath);

        return new ExternalToolLocator(new LocalAppPaths(localStatePath, installRootPath, [workspaceRootPath]));
    }

    private string WriteY4mFile(int index)
    {
        var sourcePath = Path.Combine(_testRoot!, $"source-{index}.y4m");
        File.WriteAllText(sourcePath, "YUV4MPEG2 W16 H16 F24000:1001 C420\n");
        return sourcePath;
    }
}

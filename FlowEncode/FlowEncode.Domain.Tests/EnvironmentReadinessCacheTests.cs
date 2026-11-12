using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class EnvironmentReadinessCacheTests
{
    [TestMethod]
    public async Task Startup_RestoresToolsAndCapabilitiesWithoutProbingOrExpiringByAge()
    {
        using var fixture = new Fixture();
        var report = await fixture.Service.CheckAsync();
        var cache = fixture.Cache.Load()!;
        var oldCheck = report.CheckedAt.AddMonths(-6);
        fixture.Cache.Save(cache with { EnvironmentCheckedAt = oldCheck });

        var probe = new CountingProbe(fixture.Result);
        var restarted = fixture.Restart(probe).TryGetCachedReport();

        Assert.IsNotNull(restarted);
        Assert.AreEqual(0, probe.CallCount);
        Assert.AreEqual(oldCheck, restarted.CheckedAt);
        Assert.AreEqual(fixture.Result, restarted.Tools.Single());
        Assert.AreEqual(ReadinessState.Ready, restarted.Capabilities.Single().State);
        Assert.IsTrue(restarted.Capabilities.Single().Requirements.Single().IsSatisfied);
    }

    [TestMethod]
    public async Task Startup_ChangedOrDeletedExecutableInvalidatesCache()
    {
        using var fixture = new Fixture();
        await fixture.Service.CheckAsync();
        File.AppendAllText(fixture.ToolPath, "updated executable");
        Assert.IsNull(fixture.Restart().TryGetCachedReport());

        await fixture.Service.CheckAsync();
        File.Delete(fixture.ToolPath);
        Assert.IsNull(fixture.Restart().TryGetCachedReport());
    }

    [TestMethod]
    public async Task Startup_ChangedToolSettingsInvalidateCache()
    {
        using var fixture = new Fixture();
        await fixture.Service.CheckAsync();
        var original = fixture.Settings.Value;
        fixture.Settings.Value = original with { PreferSystemEncoders = !original.PreferSystemEncoders };
        Assert.IsNull(fixture.Restart().TryGetCachedReport());

        fixture.Settings.Value = original with
        {
            ManualToolPaths = new Dictionary<string, string> { ["ffmpeg"] = fixture.ToolPath }
        };
        Assert.IsNull(fixture.Restart().TryGetCachedReport());
    }

    [TestMethod]
    public async Task ExplicitCheck_ProbesAgainAndPreservesDependencyReport()
    {
        using var fixture = new Fixture();
        var status = new SetupGuideCacheStatusReport(DateTimeOffset.Now, []);
        fixture.Cache.Save(new SetupGuideCacheSnapshot(
            SetupGuideCacheSnapshot.CurrentSchemaVersion, status.CheckedAt, status.CheckedAt, null, status));
        await fixture.Service.CheckAsync();
        var probe = new CountingProbe(fixture.Result with { State = ReadinessState.Missing });
        var restarted = fixture.Restart(probe);

        var report = await restarted.CheckAsync();

        Assert.AreEqual(1, probe.CallCount);
        Assert.AreEqual(ReadinessState.Missing, report.Tools.Single().State);
        Assert.AreEqual(ReadinessState.Missing, fixture.Restart().TryGetCachedReport()!.Tools.Single().State);
        Assert.AreEqual(status.CheckedAt, fixture.Cache.Load()!.StatusReport!.CheckedAt);
    }

    [TestMethod]
    public void Startup_OldSnapshotWithoutToolsFallsBackToScan()
    {
        using var fixture = new Fixture();
        fixture.Cache.Save(new SetupGuideCacheSnapshot(
            SetupGuideCacheSnapshot.CurrentSchemaVersion, DateTimeOffset.Now, null, null, null));
        Assert.IsNull(fixture.Restart().TryGetCachedReport());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SharedTaskCoordinatorTests.TestRoot _root = new();
        public readonly SharedTaskCoordinatorTests.Settings Settings = new();
        public LocalAppPaths Paths { get; }
        public LocalSetupGuideCacheService Cache { get; }
        public EnvironmentReadinessService Service { get; }
        public string ToolPath { get; }
        public ToolProbeResult Result { get; }

        public Fixture()
        {
            Paths = new LocalAppPaths(_root.Path, _root.Path);
            ToolPath = Path.Combine(_root.Path, "ffmpeg.exe");
            File.WriteAllText(ToolPath, "test executable");
            Result = new ToolProbeResult(RegisteredToolKind.Ffmpeg, ReadinessState.Ready,
                ToolDetectionSource.LocalToolset, "tools", ToolPath, "9.0.2", "", "");
            Cache = new LocalSetupGuideCacheService(Paths);
            Service = new EnvironmentReadinessService(new Registry(), new CountingProbe(Result), Cache, Settings, Paths);
        }

        public EnvironmentReadinessService Restart(CountingProbe? probe = null) => new(
            new Registry(), probe ?? new CountingProbe(Result), new LocalSetupGuideCacheService(Paths), Settings, Paths);

        public void Dispose() => _root.Dispose();
    }

    private sealed class Registry : IToolRegistryService
    {
        public ToolDefinition GetTool(RegisteredToolKind kind) => new(kind, ToolProbeMode.ProcessVersion,
            ["ffmpeg.exe"], [], ToolSearchLocation.None, "-version", "");
        public IReadOnlyList<ToolDefinition> GetTools() => [GetTool(RegisteredToolKind.Ffmpeg)];
        public IReadOnlyList<CapabilityDefinition> GetCapabilities() =>
            [new(EnvironmentCapabilityKind.VideoBasic, [new CapabilityToolRequirement(RegisteredToolKind.Ffmpeg)])];
    }

    private sealed class CountingProbe(ToolProbeResult result) : IToolProbeService
    {
        public int CallCount { get; private set; }
        public Task<IReadOnlyList<ToolProbeResult>> ProbeAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult<IReadOnlyList<ToolProbeResult>>([result]);
        }

        public Task<ToolProbeResult> ProbeAsync(RegisteredToolKind kind, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void InvalidateCache() { }
    }
}

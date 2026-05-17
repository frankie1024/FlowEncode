using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class ProcessToolProbeServiceTests
{
    private string _testRoot = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeProcessToolProbeServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [TestMethod]
    public async Task ProbeAsync_WhenManualCandidateExists_LoadsSettingsOncePerCall()
    {
        var toolPath = Path.Combine(_testRoot, "DGDemux.exe");
        await File.WriteAllTextAsync(toolPath, "stub");

        var settings = new CountingSettingsService(new AppSettings(
            PreferSystemEncoders: true,
            AutoCheckUpdatesOnStartup: true,
            Theme: AppThemePreference.Default,
            Language: AppLanguage.Chinese,
            ManualToolPaths: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.DgDemux)] = toolPath
            }));
        var definition = new ToolDefinition(
            RegisteredToolKind.DgDemux,
            ToolProbeMode.ExistenceOnly,
            ["DGDemux.exe"],
            [],
            ToolSearchLocation.None,
            string.Empty,
            "https://example.invalid/dgdemux");
        var registry = new StubToolRegistryService(definition);
        var service = new ProcessToolProbeService(
            registry,
            new LocalAppPaths(_testRoot, _testRoot),
            new StubEncoderDiscoveryService(),
            settings);

        var result = await service.ProbeAsync(RegisteredToolKind.DgDemux);

        Assert.AreEqual(ReadinessState.Ready, result.State);
        Assert.AreEqual(1, settings.LoadCount);
    }

    private sealed class CountingSettingsService : IAppSettingsService
    {
        private readonly AppSettings _settings;

        public CountingSettingsService(AppSettings settings)
        {
            _settings = settings;
        }

        public int LoadCount { get; private set; }

        public AppSettings Load()
        {
            LoadCount++;
            return _settings;
        }

        public void Save(AppSettings settings)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubToolRegistryService : IToolRegistryService
    {
        private readonly ToolDefinition _definition;

        public StubToolRegistryService(ToolDefinition definition)
        {
            _definition = definition;
        }

        public IReadOnlyList<ToolDefinition> GetTools() => [_definition];

        public ToolDefinition GetTool(RegisteredToolKind kind)
        {
            if (kind != _definition.Kind)
            {
                throw new KeyNotFoundException();
            }

            return _definition;
        }

        public IReadOnlyList<CapabilityDefinition> GetCapabilities() => [];
    }

    private sealed class StubEncoderDiscoveryService : IEncoderDiscoveryService
    {
        public IReadOnlyList<DiscoveredEncoderBinary> DiscoverSystemBinaries() => [];

        public DiscoveredEncoderBinary? ResolveEncoder(
            EncoderKind kind,
            EncoderArchitecture preferredArchitecture,
            bool preferSystemEncoders) => null;

        public void InvalidateCache()
        {
        }
    }
}

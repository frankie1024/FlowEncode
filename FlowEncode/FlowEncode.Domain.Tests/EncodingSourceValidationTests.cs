using System.Text.Json;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class EncodingSourceValidationTests
{
    private string _root = string.Empty;
    private LocalEncodingJobRunner _runner = null!;
    private string _script = string.Empty;
    private string _media = string.Empty;
    private string _encoder = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), nameof(EncodingSourceValidationTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ToolStub")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
        var stub = Path.Combine(_root, "FlowEncode.ToolStub.exe");
        _encoder = Path.Combine(_root, "x264.exe");
        var vspipe = Path.Combine(_root, "vspipe.exe");
        File.Copy(stub, _encoder);
        File.Copy(stub, vspipe);
        var av1an = Path.Combine(_root, "av1an.exe");
        File.Copy(stub, av1an);
        _script = Path.Combine(_root, "source.vpy");
        _media = Path.Combine(_root, "源文件.avc");
        File.WriteAllText(_media, "media");
        File.WriteAllText(_script, JsonSerializer.Serialize(new { source = _media }));
        var paths = new LocalAppPaths(Path.Combine(_root, "state"), _root, [Path.Combine(_root, "workspace")]);
        var settings = new Settings(AppSettings.Default with
        {
            Language = AppLanguage.English,
            ManualToolPaths = new Dictionary<string, string>
            {
                [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Vspipe)] = vspipe,
                [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Av1an)] = av1an
            }
        });
        _runner = new LocalEncodingJobRunner(paths, new Encoders(_encoder), settings);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task ValidateSourceAsync_WhenReferencedMediaIsMissing_ReportsFullScriptError()
    {
        File.Delete(_media);
        var lines = new List<string>();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _runner.ValidateSourceAsync(_script, new InlineProgress<string>(lines.Add)));

        StringAssert.Contains(error.Message, "FileNotFoundError");
        StringAssert.Contains(error.Message, _media);
        Assert.IsTrue(lines.Any(line => line.Contains(_media)));
        AssertEncoderWasNotStarted();
    }

    [TestMethod]
    public async Task ValidateSourceAsync_WhenPreviewWasCached_RechecksScriptDependencies()
    {
        _runner.BuildDisplayCommand(Request());
        File.Delete(_media);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => _runner.ValidateSourceAsync(_script));
        StringAssert.Contains(error.Message, _media);
        AssertEncoderWasNotStarted();
    }

    [TestMethod]
    public async Task RunAsync_WhenPreviewWasCachedButMediaDisappeared_FailsBeforeStartingEncoder()
    {
        var request = Request();
        _runner.BuildDisplayCommand(request);
        File.Delete(_media);
        var updates = new List<EncodingJobProgress>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await _runner.RunAsync(request, new InlineProgress<EncodingJobProgress>(updates.Add), timeout.Token);

        Assert.AreEqual(EncodingJobState.Failed, result.State, result.Log);
        StringAssert.Contains(result.Log, _media);
        StringAssert.Contains(await File.ReadAllTextAsync(result.LogFilePath!), "FileNotFoundError");
        Assert.AreEqual(EncodingJobState.Failed, updates.Last().State);
        Assert.IsFalse(File.Exists(request.OutputPath));
        AssertEncoderWasNotStarted();
    }

    [TestMethod]
    [DataRow(EncoderKind.X264)]
    [DataRow(EncoderKind.SvtAv1)]
    public async Task RunAsync_WhenParallelPreviewWasCachedButMediaDisappeared_RejectsStaleMetadata(EncoderKind kind)
    {
        var request = Request() with
        {
            Profile = Request().Profile with { Kind = kind },
            UseAv1anParallelVideoEncoding = true,
            OutputPath = Path.Combine(_root, "parallel.mkv")
        };
        _runner.BuildDisplayCommand(request);
        File.Delete(_media);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _runner.RunAsync(request, cancellationToken: timeout.Token));

        StringAssert.Contains(error.Message, "FileNotFoundError");
        StringAssert.Contains(error.Message, _media);
        Assert.IsFalse(File.Exists(request.OutputPath));
        AssertEncoderWasNotStarted();
    }

    [TestMethod]
    public async Task ValidateSourceAsync_WhenScriptWasDeleted_RejectsIt()
    {
        File.Delete(_script);
        var error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _runner.ValidateSourceAsync(_script));
        Assert.AreEqual(_script, error.FileName);
        AssertEncoderWasNotStarted();
    }

    [TestMethod]
    public async Task ValidateSourceAsync_WhenScriptIsValid_CompletesWithoutEncoding()
    {
        await _runner.ValidateSourceAsync(_script);
        AssertEncoderWasNotStarted();
    }

    private void AssertEncoderWasNotStarted() => Assert.IsFalse(File.Exists(Path.Combine(_root, "x264-calls.log")));

    private EncodingJobRequest Request() => new(Guid.NewGuid(),
        new EncodingProfile(EncoderKind.X264, "Test", "", "medium", "", "", RateControlMode.Crf, 18, null, "264", "", ""),
        _script, Path.Combine(_root, "output.264"), InputPipelineKind.Auto, EncoderArchitecture.X64);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class Settings(AppSettings settings) : IAppSettingsService
    {
        public AppSettings Load() => settings;
        public void Save(AppSettings value) => settings = value;
    }

    private sealed class Encoders(string path) : IEncoderDiscoveryService
    {
        public IReadOnlyList<DiscoveredEncoderBinary> DiscoverSystemBinaries() => [];
        public DiscoveredEncoderBinary? ResolveEncoder(EncoderKind kind, EncoderArchitecture architecture, bool preferSystem) =>
            new(kind, architecture, path, EncoderBinarySource.Path, "test", "test");
        public void InvalidateCache() { }
    }
}

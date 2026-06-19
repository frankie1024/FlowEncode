using System.Text;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class LocalEncodingJobRunnerDisplayCommandTests
{
    private string _testRoot = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "FlowEncodeLocalEncodingJobRunnerDisplayCommandTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [TestMethod]
    public void BuildDisplayCommand_WhenSerialX265Y4mSourceCannotBeProbed_DoesNotProbeSourceMetadata()
    {
        var sourcePath = WriteBrokenY4mSource();
        var outputPath = Path.Combine(_testRoot, "output.hevc");
        var runner = CreateRunner(EncoderKind.X265);

        var command = runner.BuildDisplayCommand(CreateRequest(
            EncoderKind.X265,
            sourcePath,
            outputPath,
            useAv1anParallelVideoEncoding: false));

        StringAssert.Contains(command, "--input");
        Assert.IsFalse(command.Contains("--range", StringComparison.Ordinal));
        Assert.IsFalse(command.Contains("--colorprim", StringComparison.Ordinal));
        Assert.IsFalse(command.Contains("--transfer", StringComparison.Ordinal));
        Assert.IsFalse(command.Contains("--colormatrix", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildDisplayCommand_WhenParallelX265Y4mSourceHasColorMetadata_IncludesVideoMetadataParameters()
    {
        var sourcePath = WriteY4mSourceWithColorMetadata();
        var outputPath = Path.Combine(_testRoot, "output-parallel.hevc");
        var runner = CreateRunner(EncoderKind.X265);

        var command = runner.BuildDisplayCommand(CreateRequest(
            EncoderKind.X265,
            sourcePath,
            outputPath,
            useAv1anParallelVideoEncoding: true));

        StringAssert.Contains(command, "--range limited");
        StringAssert.Contains(command, "--colorprim bt2020");
        StringAssert.Contains(command, "--transfer smpte2084");
        StringAssert.Contains(command, "--colormatrix bt2020nc");
    }

    [TestMethod]
    public void BuildDisplayCommand_WhenSvtAv1Y4mSourceHasColorMetadata_IncludesVideoMetadataParameters()
    {
        var sourcePath = WriteY4mSourceWithColorMetadata();
        var outputPath = Path.Combine(_testRoot, "output.ivf");
        var runner = CreateRunner(EncoderKind.SvtAv1);

        var command = runner.BuildDisplayCommand(CreateRequest(
            EncoderKind.SvtAv1,
            sourcePath,
            outputPath,
            useAv1anParallelVideoEncoding: false));

        StringAssert.Contains(command, "--color-range 0");
        StringAssert.Contains(command, "--color-primaries 9");
        StringAssert.Contains(command, "--transfer-characteristics 16");
        StringAssert.Contains(command, "--matrix-coefficients 9");
        StringAssert.Contains(command, "--chroma-sample-position left");
    }

    [TestMethod]
    public void BuildDisplayCommand_WhenParallelSvtAv1Y4mSourceHasColorMetadata_IncludesVideoMetadataParameters()
    {
        var sourcePath = WriteY4mSourceWithColorMetadata();
        var outputPath = Path.Combine(_testRoot, "output-parallel.ivf");
        var runner = CreateRunner(EncoderKind.SvtAv1);

        var command = runner.BuildDisplayCommand(CreateRequest(
            EncoderKind.SvtAv1,
            sourcePath,
            outputPath,
            useAv1anParallelVideoEncoding: true));

        StringAssert.Contains(command, "--color-range 0");
        StringAssert.Contains(command, "--color-primaries 9");
        StringAssert.Contains(command, "--transfer-characteristics 16");
        StringAssert.Contains(command, "--matrix-coefficients 9");
        StringAssert.Contains(command, "--chroma-sample-position left");
    }

    [TestMethod]
    public void BuildDisplayCommand_WhenParallelSvtAv1Y4mSourceCannotBeProbed_DoesNotThrow()
    {
        var sourcePath = WriteBrokenY4mSource();
        var outputPath = Path.Combine(_testRoot, "broken-preview.ivf");
        var runner = CreateRunner(EncoderKind.SvtAv1);

        var command = runner.BuildDisplayCommand(CreateRequest(
            EncoderKind.SvtAv1,
            sourcePath,
            outputPath,
            useAv1anParallelVideoEncoding: true));

        StringAssert.Contains(command, "--encoder svt-av1");
        Assert.IsFalse(command.Contains("--width", StringComparison.Ordinal));
        Assert.IsFalse(command.Contains("--height", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RunAsync_WhenParallelSvtAv1AutoSourceCannotBeProbed_ThrowsClearMetadataMessage()
    {
        var sourcePath = WriteBrokenY4mSource();
        var outputPath = Path.Combine(_testRoot, "broken-run.ivf");
        var runner = CreateRunner(EncoderKind.SvtAv1);

        try
        {
            await runner.RunAsync(CreateRequest(
                EncoderKind.SvtAv1,
                sourcePath,
                outputPath,
                useAv1anParallelVideoEncoding: true,
                pipelineKind: InputPipelineKind.Auto));
        }
        catch (InvalidOperationException exception)
        {
            StringAssert.Contains(exception.Message, "SVT-AV1 requires detectable source metadata");
            return;
        }

        Assert.Fail("Expected source metadata probing to fail for SVT-AV1 Auto pipeline.");
    }

    private LocalEncodingJobRunner CreateRunner(EncoderKind encoderKind)
    {
        var localStatePath = Path.Combine(_testRoot, "local-state");
        var installRootPath = Path.Combine(_testRoot, "install-root");
        var workspaceRootPath = Path.Combine(_testRoot, "workspace");
        Directory.CreateDirectory(localStatePath);
        Directory.CreateDirectory(installRootPath);
        var paths = new LocalAppPaths(localStatePath, installRootPath, [workspaceRootPath]);
        var encoderPath = Path.Combine(_testRoot, $"{encoderKind.ToShortName()}.exe");
        var av1anPath = Path.Combine(_testRoot, "av1an.cmd");
        File.WriteAllText(encoderPath, string.Empty);
        File.WriteAllText(av1anPath, "@exit /b 0");
        var manualToolPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Av1an)] = av1anPath
        };
        var settings = new FakeAppSettingsService(AppSettings.Default with
        {
            Language = AppLanguage.English,
            PreferSystemEncoders = false,
            ManualToolPaths = manualToolPaths
        });

        return new LocalEncodingJobRunner(
            paths,
            new FakeEncoderDiscoveryService(encoderKind, encoderPath),
            settings);
    }

    private static EncodingJobRequest CreateRequest(
        EncoderKind encoderKind,
        string sourcePath,
        string outputPath,
        bool useAv1anParallelVideoEncoding,
        InputPipelineKind pipelineKind = InputPipelineKind.Y4mFile)
    {
        return new EncodingJobRequest(
            Guid.NewGuid(),
            new EncodingProfile(
                encoderKind,
                "Test",
                string.Empty,
                "slow",
                string.Empty,
                string.Empty,
                RateControlMode.Crf,
                20,
                null,
                "hevc",
                string.Empty,
                string.Empty),
            sourcePath,
            outputPath,
            pipelineKind,
            EncoderArchitecture.X64,
            useAv1anParallelVideoEncoding);
    }

    private string WriteY4mSourceWithColorMetadata()
    {
        var sourcePath = Path.Combine(_testRoot, "source.y4m");
        var header = "YUV4MPEG2 W16 H16 F24000:1001 C420p10 "
            + "XRANGE=limited XCOLORPRIM=bt2020 XTRANSFER=smpte2084 XMATRIX=bt2020nc XCHROMALOC=left\n";
        File.WriteAllText(sourcePath, header, Encoding.ASCII);
        return sourcePath;
    }

    private string WriteBrokenY4mSource()
    {
        var sourcePath = Path.Combine(_testRoot, "broken-source.y4m");
        File.WriteAllText(sourcePath, "not a y4m stream", Encoding.ASCII);
        return sourcePath;
    }

    private sealed class FakeEncoderDiscoveryService : IEncoderDiscoveryService
    {
        private readonly EncoderKind _kind;
        private readonly string _executablePath;

        public FakeEncoderDiscoveryService(EncoderKind kind, string executablePath)
        {
            _kind = kind;
            _executablePath = executablePath;
        }

        public IReadOnlyList<DiscoveredEncoderBinary> DiscoverSystemBinaries()
        {
            return
            [
                new DiscoveredEncoderBinary(
                    _kind,
                    EncoderArchitecture.X64,
                    _executablePath,
                    EncoderBinarySource.Path,
                    "test",
                    "test")
            ];
        }

        public DiscoveredEncoderBinary? ResolveEncoder(
            EncoderKind kind,
            EncoderArchitecture preferredArchitecture,
            bool preferSystemEncoders)
        {
            return kind == _kind
                ? new DiscoveredEncoderBinary(
                    _kind,
                    EncoderArchitecture.X64,
                    _executablePath,
                    EncoderBinarySource.Path,
                    "test",
                    "test")
                : null;
        }

        public void InvalidateCache()
        {
        }
    }

    private sealed class FakeAppSettingsService : IAppSettingsService
    {
        private AppSettings _settings;

        public FakeAppSettingsService(AppSettings settings)
        {
            _settings = settings;
        }

        public AppSettings Load() => _settings;

        public void Save(AppSettings settings)
        {
            _settings = settings;
        }
    }
}

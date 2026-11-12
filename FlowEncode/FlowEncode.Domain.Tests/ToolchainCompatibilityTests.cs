using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass, SupportedOSPlatform("windows")]
public sealed class ToolchainCompatibilityTests
{
    internal static EncodingProfile Profile(EncoderKind kind = EncoderKind.SvtAv1) =>
        new(kind, "compatibility", "", "8", "", "", RateControlMode.Qp, 32, null, "ivf", "", "");

    private static JsonObject Release() => new()
    {
        ["tag_name"] = FfmpegReleasePolicy.Version, ["draft"] = false, ["prerelease"] = false,
        ["published_at"] = "2026-10-01T00:00:00Z",
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = FfmpegReleasePolicy.AssetName,
            ["browser_download_url"] = "https://example.invalid/latest/master.zip",
            ["digest"] = "sha256:" + FfmpegReleasePolicy.Sha256
        })
    };

    [TestMethod]
    public void StablePackage_UsesOnlyTaggedPinnedArchiveAndRequiresDigest()
    {
        var release = Release();
        var package = FfmpegReleasePolicy.SelectPackage(release.ToJsonString())!;
        Assert.IsTrue(package.IsAutomatic);
        StringAssert.Contains(package.DownloadUrl, "/download/" + FfmpegReleasePolicy.Version + "/");
        Assert.IsFalse(package.DownloadUrl.Contains("latest"));
        release["assets"]![0]!.AsObject().Remove("digest");
        Assert.IsFalse(FfmpegReleasePolicy.SelectPackage(release.ToJsonString())!.IsAutomatic);
    }

    [TestMethod]
    [DataRow("latest", false, false)]
    [DataRow("master", false, false)]
    [DataRow("9.0.2", true, false)]
    [DataRow("9.0.2", false, true)]
    public void StablePackage_RejectsFloatingOrUnstableRelease(string tag, bool draft, bool prerelease)
    {
        var release = Release(); release["tag_name"] = tag; release["draft"] = draft; release["prerelease"] = prerelease;
        Assert.IsNull(FfmpegReleasePolicy.SelectPackage(release.ToJsonString()));
    }

    [TestMethod]
    public void StablePackage_RejectsDailyBranchArchiveEvenInStableRelease()
    {
        var release = Release(); release["assets"]![0]!["name"] = "ffmpeg-n9.0-latest-win64-gpl-shared.zip";
        Assert.IsNull(FfmpegReleasePolicy.SelectPackage(release.ToJsonString()));
    }

    [TestMethod]
    public void Baseline_RejectsReplacedArchiveDigestAndAllowsExplicitStableUpgrade()
    {
        var release = Release();
        release["assets"]![0]!["digest"] = "sha256:" + new string('a', 64);
        Assert.IsFalse(FfmpegReleasePolicy.SelectPackage(release.ToJsonString())!.IsAutomatic);
        Assert.IsTrue(FfmpegReleasePolicy.SelectPackage(release.ToJsonString(), useBaseline: false)!.IsAutomatic);
        Assert.IsFalse(GitHubReleaseEncoderUpdateService.IsBaselineDigest(EncoderKind.X265, new string('a', 64)));
        Assert.IsTrue(GitHubReleaseEncoderUpdateService.IsBaselineDigest(EncoderKind.X265, "499913e1e898b64078a795c9edcd0ea8805fe05417091ce9abad0572f70f0b3d"));
    }

    [TestMethod]
    public void PinnedVsManifest_ContainsCompleteDependencyClosureAndFileHashes()
    {
        using var stream = typeof(SetupBootstrapService).Assembly.GetManifestResourceStream("FlowEncode.Infrastructure.Resources.VsrepoBaseline.json")!;
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        var packages = document.RootElement.GetProperty("packages").EnumerateArray().ToArray();
        var identifiers = packages.Select(package => package.GetProperty("identifier").GetString()!).ToHashSet();
        Assert.AreEqual(packages.Length, identifiers.Count);
        foreach (var package in packages)
        {
            if (package.TryGetProperty("dependencies", out var dependencies))
                foreach (var dependency in dependencies.EnumerateArray())
                    if (dependency.ValueKind == System.Text.Json.JsonValueKind.String)
                        Assert.IsTrue(identifiers.Contains(dependency.GetString()!), dependency.ToString());
            var releases = package.GetProperty("releases").EnumerateArray().ToArray();
            Assert.HasCount(1, releases);
            var artifact = releases[0].GetProperty(package.GetProperty("type").GetString() == "PyScript" ? "script" : "win64");
            Assert.IsFalse(artifact.GetProperty("url").GetString()!.Contains("/latest/"));
            foreach (var file in artifact.GetProperty("files").EnumerateObject())
                Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(file.Value[1].GetString()!, "^[a-f0-9]{64}$"));
        }
    }

    [TestMethod]
    public void AviSynthMetadata_Preserves10BitFormatAndExactFrameCount()
    {
        var source = SourceVideoInfoProbe.ParseAviSynthInfo("v:width 128\nv:height 72\nv:pixel_type YUV420P10\nv:bit_depth 10\nv:fps 24000/1001\nv:frames 240\n");
        Assert.AreEqual(10, source.BitDepth);
        Assert.AreEqual(240L, source.TotalFrames);
        Assert.AreEqual(24000, source.FpsNumerator);
        Assert.IsFalse(source.IsFrameCountEstimated);
    }

    [TestMethod]
    [DataRow("ffmpeg version N-126175-g0056dd32fd-20260816", true, false)]
    [DataRow("ffmpeg version n9.0-14-g123-20261111", true, false)]
    [DataRow("ffmpeg version 9.0.2-full_build-www.gyan.dev", false, true)]
    [DataRow("9.0.3", true, false)]
    public void StableVersion_RequiresExactApprovedPointRelease(string installed, bool update, bool verified)
    {
        Assert.AreEqual(update, SetupBootstrapService.HasSetupDependencyVersionUpdate(SetupDependencyKind.FfmpegBundle, installed, FfmpegReleasePolicy.Version));
        Assert.AreEqual(verified, SetupBootstrapService.IsInstalledVersionVerified(SetupDependencyKind.FfmpegBundle, installed, FfmpegReleasePolicy.Version));
    }

    [TestMethod]
    [DataRow(32.0, "--rc 0 --aq-mode 0 --qp 32")]
    [DataRow(32.25, "--cqp 32.25")]
    [DataRow(70.0, "--cqp 70")]
    public void SvtQp_GeneratesValidCqpSemanticsInExecutionAndPreview(double quality, string expected)
    {
        var profile = Profile() with { Quality = quality };
        Assert.AreEqual(expected, EncoderCliParameters.BuildSvtQpArguments(quality));
        StringAssert.Contains(EncodingCommandPreviewBuilder.Build(profile).CommandLine, expected);
        RequestValidation.ValidateEncodingProfile(profile);
    }

    [TestMethod]
    [DataRow(0.0)] [DataRow(70.25)] [DataRow(32.1)]
    public void SvtQuality_InvalidValueIsRejectedWithoutRounding(double quality) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RequestValidation.ValidateEncodingProfile(Profile() with { Quality = quality }));

    [TestMethod]
    [DataRow("--aq-mode 2")] [DataRow("--svtav1-params aq-mode=1")]
    [DataRow("--crf 32")] [DataRow("--rc 1")] [DataRow("--qp 32.0")]
    [DataRow("--qp 31")] [DataRow("--cqp 32.25")] [DataRow("--lp 20")]
    public void SvtOverrides_RejectConflictsAndLegacyThreadCount(string arguments)
    {
        // Integer-looking decimal text is also rejected by EncApp's --qp parser.
        Assert.IsNotNull(SvtAv1ProfileConstraints.GetParameterError(Profile() with { AdditionalArguments = arguments }));
    }

    [TestMethod]
    public void PackedOptions_UseFinalOverrideInArgumentOrder()
    {
        var options = EncoderCliParameters.Read("--aq-mode 2 --svtav1-params aq-mode=0:lp=4 --lp=6 --keyint -1");
        Assert.AreEqual("0", options["--aq-mode"]); Assert.AreEqual("6", options["--lp"]); Assert.AreEqual("-1", options["--keyint"]);
    }

    [TestMethod]
    public void SvtIq_RequiresAllIntraAndSvtProfilesRequire420Main()
    {
        Assert.IsNotNull(SvtAv1ProfileConstraints.GetParameterError(Profile() with { Tune = "3" }));
        Assert.IsNull(SvtAv1ProfileConstraints.GetParameterError(Profile() with { Tune = "3", AdditionalArguments = "--keyint 0" }));
        var source = new SourceVideoInfo(64, 64, 24, 8, 24, 1, "yuv420p");
        Assert.ThrowsExactly<ArgumentException>(() => VideoOutputContract.Resolve(Profile() with { Profile = "high" }, source));
        Assert.ThrowsExactly<ArgumentException>(() => VideoOutputContract.Resolve(Profile(), source with { PixelFormat = "yuv422p" }));
    }

    [TestMethod]
    public void VideoContract_PreservesDepthAndHonorsExplicit8BitTarget()
    {
        var source = new SourceVideoInfo(64, 64, 24, 10, 24, 1, "yuv420p10le");
        var profile = Profile(EncoderKind.X264) with { Preset = "ultrafast" };
        Assert.AreEqual(10, VideoOutputContract.Resolve(profile, source).BitDepth);
        Assert.AreEqual(8, VideoOutputContract.Resolve(profile with { Profile = "high" }, source).BitDepth);
        Assert.AreEqual(10, VideoOutputContract.Resolve(profile with { Profile = "high444" }, source).BitDepth);
        Assert.ThrowsExactly<ArgumentException>(() => VideoOutputContract.Resolve(profile with { Profile = "high", AdditionalArguments = "--output-depth 10" }, source));
        Assert.AreEqual(8, VideoOutputContract.Resolve(profile with { AdditionalArguments = "--output-depth 8" }, source).BitDepth);
        StringAssert.Contains(EncodingCommandPreviewBuilder.Build(profile with { Profile = "high" }).CommandLine, "--output-depth 8");
    }

    [TestMethod]
    public void FrameContract_DoesNotTreatDurationEstimatesAsExactCounts()
    {
        var source = new SourceVideoInfo(64, 64, 24, 8, 24, 1, "yuv420p");
        Assert.AreEqual(24L, VideoOutputContract.Resolve(Profile(), source).Frames);
        Assert.IsNull(VideoOutputContract.Resolve(Profile(), source with { IsFrameCountEstimated = true }).Frames);
        Assert.AreEqual(10L, VideoOutputContract.Resolve(Profile() with { AdditionalArguments = "--frames 10" }, source).Frames);
    }

    [TestMethod]
    public void OutputVerification_RejectsSilentBitDepthFallbackWrongCodecChromaAndTruncation()
    {
        var contract = new VideoOutputContract(EncoderKind.X265, 10, "420", 24);
        const string valid = """{"streams":[{"codec_name":"hevc","pix_fmt":"yuv420p10le","nb_read_frames":"24"}]}""";
        EncodedMediaVerifier.ValidateVideo(valid, contract);
        foreach (var invalid in new[] { valid.Replace("p10le", "p"), valid.Replace("hevc", "h264"), valid.Replace("420", "422"), valid.Replace("24", "23"), valid.Replace("24", "N/A") })
            Assert.ThrowsExactly<InvalidOperationException>(() => EncodedMediaVerifier.ValidateVideo(invalid, contract));
    }
}

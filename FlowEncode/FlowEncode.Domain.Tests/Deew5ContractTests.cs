using System.Text;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class Deew5ContractTests
{
    internal static string Config => """
        # 用户配置
        temp_path = '' # temp
        logo = 1 # keep this comment
        max_instances = '50%'
        [output]
        path = ''
        to_input = true # do not write beside source
        [default_bitrates]
        dd_1_0 = 128
        dd_2_0 = 256
        dd_5_1 = 640
        ddp_1_0 = 128
        ddp_2_0 = 256
        ddp_5_1 = 1024
        ddp_7_1_standard = 1536
        ddp_7_1_bluray = 1536
        ddp_atmos_streaming = 1024
        ddp_atmos_standard = 1536
        ddp_atmos_bluray = 1536
        ac4_2_0 = 256
        ac4_5_1 = 448
        ac4_atmos = 448
        aac_2_0 = 256
        [summary_sections]
        deew_info = true
        binaries = true
        input_info = true
        output_info = true
        other = true
        """;

    [TestMethod]
    public void DefaultArguments_AreExplicitAndPreviewMatchesExecutionExceptOutput()
    {
        var args = DeewArguments.Build("中文 input.wav", "output", new());
        CollectionAssert.AreEqual(new[] { "-i", "中文 input.wav", "-o", "output", "-f", "ddp", "-np" }, args.ToArray());
        var stagingArgs = DeewArguments.Build("中文 input.wav", "staging", new()).ToArray();
        stagingArgs[3] = "output";
        CollectionAssert.AreEqual(args.ToArray(), stagingArgs);
    }

    [TestMethod]
    [DataRow("--output=elsewhere")][DataRow("-gc")][DataRow("--generate-config")]
    [DataRow("-gt")][DataRow("--get-tools")][DataRow("--help")][DataRow("--version")]
    [DataRow("-oi")][DataRow("--output-to-input")][DataRow("--input=x")][DataRow("-f=aac")]
    [DataRow("-np")][DataRow("unknown")][DataRow("--future-option")][DataRow("-b")]
    [DataRow("-k=true")][DataRow("-b 'unterminated")]
    public void AdvancedArguments_RejectNonEncodingAndMalformedTokens(string text) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(AdditionalArguments: text)));

    [TestMethod]
    public void AdvancedArguments_MergeAliasesLastWinsAndPreserveKeepFlags()
    {
        var warnings = new List<string>();
        var options = DeewArguments.Normalize(new(BitrateKbps: 1024, AdditionalArguments: "--bitrate=768 -b 640 -k --keep-log -mo -d=-5ms"), warnings);
        Assert.AreEqual(640, options.BitrateKbps);
        Assert.IsTrue(options.KeepTemp && options.KeepLog && options.MeasureOnly);
        Assert.AreEqual(2, warnings.Count);
        var tokens = DeewArguments.Build("i", "o", options);
        Assert.AreEqual(1, tokens.Count(t => t == "-b"));
        CollectionAssert.Contains(tokens.ToList(), "-d=-5ms");
    }

    [TestMethod]
    [DataRow("dd", ".ac3", "dee_ddp_encoder")]
    [DataRow("ddp", ".ec3", "dee_ddp_encoder")]
    [DataRow("ddp-atmos", ".ec3", "dee_ddpjoc_encoder")]
    [DataRow("thd", ".thd", "dee_dthd_encoder")]
    [DataRow("thd-atmos", ".thd", "dee_dthd_encoder")]
    [DataRow("ac4", ".ac4", "dee_ac4_encoder")]
    [DataRow("ac4-atmos", ".ac4", "dee_ac4ajoc_encoder")]
    [DataRow("aac", ".aac", "dee_aac_encoder")]
    public void Formats_SelectSpecificEncoderAndExtension(string format, string extension, string tool)
    {
        var profile = DeewArguments.Resolve(new(Format: format), 6);
        Assert.AreEqual(extension, profile.Extension);
        CollectionAssert.Contains(profile.RequiredTools.ToList(), tool);
        if (format.EndsWith("atmos")) Assert.IsFalse(profile.RequiredTools.Contains("dee_ddp_encoder"));
        if (format == "ac4-atmos") Assert.IsFalse(profile.RequiredTools.Contains("dee_shim"));
    }

    [TestMethod]
    public void DdpLayout_UsesEffectiveSlotsModeAndLfeForShimAndExtension()
    {
        var standard = DeewArguments.Resolve(new(), 8);
        Assert.AreEqual(".ec3", standard.Extension); Assert.IsTrue(standard.RequiredTools.Contains("dee_shim"));
        var bluray = DeewArguments.Resolve(new(DdpMode: "bluray"), 8);
        Assert.AreEqual(".eb3", bluray.Extension); Assert.IsFalse(bluray.RequiredTools.Contains("dee_shim"));
        foreach (var profile in new[] { DeewArguments.Resolve(new(DdpMode: "bluray"), 7), DeewArguments.Resolve(new(DdpMode: "bluray", DisableLfe: true), 8) })
            Assert.IsTrue(profile.RequiredTools.Contains("dee_shim"));
        var downmix = DeewArguments.Resolve(new(DdpMode: "bluray", DownmixChannels: 6), 8);
        Assert.AreEqual(".ec3", downmix.Extension); Assert.IsFalse(downmix.RequiredTools.Contains("dee_shim"));
        Assert.IsNull(downmix.Options.DdpMode);
        Assert.AreEqual(6, DeewArguments.Resolve(new(), 5).OutputSlots);
        Assert.AreEqual(6, DeewArguments.Resolve(new(Format: "dd"), 8).OutputSlots);
        Assert.AreEqual(6, DeewArguments.Resolve(new(Format: "ac4"), 8).OutputSlots);
    }

    [TestMethod]
    [DataRow("dd")][DataRow("ddp")][DataRow("ddp-atmos")][DataRow("thd")][DataRow("thd-atmos")]
    public void DirectDialnorm_DoesNotRequireMeasurementTool(string format)
    {
        var p = DeewArguments.Resolve(new(Format: format, Dialnorm: -27), 8);
        Assert.AreEqual(DeewDbSource.SpecifiedDialnorm, p.DbSource);
        Assert.IsFalse(p.RequiredTools.Contains("dee_measure_loudness"));
    }

    [TestMethod]
    public void Measurement_ChoosesToolFromSourceSlotsRegardlessOfOutputFormatAndDownmix()
    {
        foreach (var format in new[] { "ddp", "aac", "thd", "ac4" })
        {
            var p = DeewArguments.Resolve(new(Format: format, MeasureOnly: true, DownmixChannels: 2, Dialnorm: -27), 7);
            CollectionAssert.AreEqual(new[] { "dee_ddp_encoder" }, p.RequiredTools.ToArray());
            Assert.IsNull(p.Extension); Assert.IsNull(p.Options.Dialnorm);
            var six = DeewArguments.Resolve(new(Format: format, MeasureOnly: true), 5);
            CollectionAssert.AreEqual(new[] { "dee_measure_loudness" }, six.RequiredTools.ToArray());
        }
        var ac4 = DeewArguments.Resolve(new(Format: "ac4", Dialnorm: -27), 6);
        Assert.IsTrue(ac4.RequiredTools.Contains("dee_measure_loudness")); Assert.AreEqual(DeewDbSource.Measured, ac4.DbSource);
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "ac4", Dialnorm: -27, DownmixChannels: 6), 8));
    }

    [TestMethod]
    [DataRow("ddp-atmos", "-dm 2")][DataRow("thd-atmos", "-mo")]
    [DataRow("ac4", "-r none")][DataRow("ac4-atmos", "-dn -27")]
    [DataRow("aac", "-dn -27")][DataRow("thd", "-dm 1")][DataRow("thd", "-b 640")]
    [DataRow("ddp", "-dm 8")]
    public void InvalidCombinations_AreRejectedBeforeStarting(string format, string arguments) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: format, AdditionalArguments: arguments), 8));

    [TestMethod]
    [DataRow("5.1.0", true)][DataRow("5.1.1", true)][DataRow("5.0.3", false)][DataRow("4.0.0", false)][DataRow("not found", false)]
    public void VersionGate_RejectsLegacyAndUnreadableVersions(string version, bool expected) => Assert.AreEqual(expected, DeewToolchain.IsSupportedVersion(version));

    [TestMethod]
    [DataRow("\n")][DataRow("\r\n")]
    public void TomlEditor_PreservesCommentsAndSupportsQuotedPathsAndNewlines(string newline)
    {
        foreach (var assignment in new[] { "temp_path = ''", "temp_path = \"D:\\\\原目录\"", "temp_path = '''D:\\原目录'''" })
        {
            var original = Config.Replace("temp_path = ''", assignment).Replace("\r\n", "\n").Replace("\n", newline);
            var edited = new DeewConfigEditor(Encoding.UTF8.GetBytes(original)).Edit("D:\\输出\\O'Brien\\临时");
            var parsed = new DeewConfigEditor(edited);
            Assert.AreEqual("D:\\输出\\O'Brien\\临时", parsed.TempPath);
            var text = Encoding.UTF8.GetString(edited);
            StringAssert.Contains(text, "logo = 0 # keep this comment");
            StringAssert.Contains(text, "to_input = false # do not write beside source");
            if (newline == "\r\n") Assert.IsFalse(text.Replace("\r\n", "").Contains('\n'));
        }
    }

    [TestMethod]
    public void TomlEditor_AddsMissingOutputAndRejectsMalformedExistingConfig()
    {
        var noOutput = Config.Replace("[output]\npath = ''\nto_input = true # do not write beside source\n", "");
        var edited = new DeewConfigEditor(Encoding.UTF8.GetBytes(noOutput)).Edit("D:\\temp");
        StringAssert.Contains(Encoding.UTF8.GetString(edited), "to_input = false");
        Assert.ThrowsExactly<InvalidDataException>(() => new DeewConfigEditor(Encoding.UTF8.GetBytes("logo = 0")));
        Assert.ThrowsExactly<InvalidDataException>(() => new DeewConfigEditor(Encoding.UTF8.GetBytes(Config.Replace("logo = 1", "logo = 'wrong'"))));
    }

    [TestMethod]
    public async Task Lease_RestoresExactBytesSerializesAndHonorsCancellation()
    {
        var root = NewRoot();
        try
        {
            var config = Path.Combine(root, "config.toml");
            var original = Encoding.UTF8.GetBytes(Config);
            await File.WriteAllBytesAsync(config, original);
            using var first = await DeewConfigLease.AcquireAsync(Path.Combine(root, "deew.exe"), Path.Combine(root, "temp"), _ => throw new Exception("Must not reset an existing config"), default);
            Assert.AreNotEqual(Config, await File.ReadAllTextAsync(config));
            using var cancelled = new CancellationTokenSource(150);
            await Assert.ThrowsAsync<OperationCanceledException>(() => DeewConfigLease.AcquireAsync(Path.Combine(root, "deew.exe"), root, _ => Task.CompletedTask, cancelled.Token));
            first.Dispose();
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(config));
            using var second = await DeewConfigLease.AcquireAsync(Path.Combine(root, "deew.exe"), root, _ => Task.CompletedTask, default);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Lease_ExternalEditPreservesBothFilesAndBlocksNextRewrite()
    {
        var root = NewRoot();
        try
        {
            var config = Path.Combine(root, "config.toml");
            await File.WriteAllTextAsync(config, Config);
            var lease = await DeewConfigLease.AcquireAsync(Path.Combine(root, "deew.exe"), root, _ => Task.CompletedTask, default);
            var external = Config + "\n# edited externally\n";
            await File.WriteAllTextAsync(config, external);
            lease.Dispose();
            Assert.IsNotNull(lease.RestorationError);
            Assert.AreEqual(external, await File.ReadAllTextAsync(config));
            Assert.IsTrue(File.Exists(config + ".flowencode-backup.json"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => DeewConfigLease.AcquireAsync(Path.Combine(root, "deew.exe"), root, _ => Task.CompletedTask, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Events_IncrementalUtf8HalfLinesNoDuplicatesAndTailErrorAfterDone()
    {
        var root = NewRoot();
        try
        {
            var file = Path.Combine(root, "events.jsonl");
            var logs = new List<string>(); var updates = new List<double?>();
            var reader = new DeewEventsReader(DeewArguments.Resolve(new(Dialnorm: -27), 2), logs.Add, (p, s, db, source) => updates.Add(p));
            var bytes = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"version\":\"5.1.0\"}\n{\"type\":\"warning\",\"text\":\"中文\"}\n{\"type\":\"progress\",\"job\":0,\"stage\":\"encode\",\"percent\":50,\"db\":-27}\n{\"type\":\"done\",\"job\":0}\n{\"type\":\"error\",\"text\":\"tail failure\"}\n");
            await using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                foreach (var b in bytes)
                {
                    await stream.WriteAsync(new byte[] { b }); await stream.FlushAsync();
                    await reader.ReadAvailableAsync(file, false, default);
                }
            await reader.ReadAvailableAsync(file, true, default);
            Assert.IsTrue(reader.HasHello); Assert.AreEqual("tail failure", reader.Error);
            Assert.AreEqual(1, updates.Count); Assert.AreEqual(.725, updates[0]!.Value, 0.000001);
            Assert.AreEqual(1, logs.Count(x => x == "warning: 中文"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Events_NullProgressAndSkippedStagesAreSupportedWithoutPrematureCompletion()
    {
        var updates = new List<(double? Fraction, double? Db, DeewDbSource Source)>();
        var reader = new DeewEventsReader(DeewArguments.Resolve(new(MeasureOnly: true), 8), _ => { }, (p, s, d, source) => updates.Add((p, d, source)));
        reader.Accept("""{"type":"progress","stage":"starting","percent":null,"db":null}""");
        reader.Accept("""{"type":"progress","stage":"measure","percent":80,"db":-27}""");
        reader.Accept("""{"type":"progress","stage":"measure","percent":5,"db":null}""");
        reader.Accept("""{"type":"progress","stage":"measure","percent":100,"db":null}""");
        reader.Accept("""{"type":"done","job":0}""");
        Assert.IsNull(updates[0].Fraction); Assert.AreEqual(.8, updates[1].Fraction!.Value);
        Assert.AreEqual(.8, updates[2].Fraction!.Value); Assert.AreEqual(.999, updates[3].Fraction!.Value);
        Assert.AreEqual(-27, reader.Db); Assert.AreEqual(DeewDbSource.Measured, updates[1].Source);
    }

    [TestMethod]
    public void Ac4MeasuredLoudness_IsNotReplacedByManifestDialnorm()
    {
        var updates = new List<(double? Db, DeewDbSource Source)>();
        var reader = new DeewEventsReader(DeewArguments.Resolve(new(Format: "ac4", Dialnorm: -27), 6), _ => { },
            (p, s, db, origin) => updates.Add((db, origin)));
        reader.Accept("""{"type":"progress","stage":"measure","percent":100,"db":-15}""");
        reader.Accept("""{"type":"progress","stage":"encode","percent":90,"db":-27}""");
        Assert.AreEqual(-15, reader.Db);
        Assert.AreEqual((-27.0, DeewDbSource.SpecifiedDialnorm), updates[1]);
    }

    [TestMethod]
    public void OlderConfigMissingOptionalNewBitrates_UsesUpstreamDefaults()
    {
        var optional = new[] { "ddp_7_1_standard", "ddp_7_1_bluray", "ddp_atmos_streaming", "ddp_atmos_standard", "ddp_atmos_bluray" };
        var old = string.Join("\n", Config.Split('\n').Where(line => !optional.Any(key => line.StartsWith(key + " ="))));
        var editor = new DeewConfigEditor(Encoding.UTF8.GetBytes(old));
        var profile = DeewBitrates.Apply(DeewArguments.Resolve(new(), 8), editor.Bitrates);
        Assert.AreEqual(1536, profile.EffectiveBitrateKbps);
    }

    [TestMethod]
    public void ContainerInput_IsLimitedToAtmosFormatsAndSupportedContainers()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => AudioSourceSupport.ValidateDeewInput("source.mkv", new()));
        AudioSourceSupport.ValidateDeewInput("source.mkv", new(Format: "ddp-atmos"));
        Assert.ThrowsExactly<InvalidOperationException>(() => AudioSourceSupport.ValidateDeewInput("source.mp4", new(Format: "ddp-atmos")));
    }

    internal static string NewRoot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "deew5-test-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
}

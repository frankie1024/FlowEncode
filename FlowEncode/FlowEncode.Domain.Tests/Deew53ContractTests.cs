using System.Text;
using System.Text.Json;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class Deew53ContractTests
{
    [TestMethod]
    [DataRow("dd")][DataRow("ddp")][DataRow("ddp-atmos")]
    public void NoPhaseShift_StoredAliasesEmitTheNewCliOption(string format)
    {
        foreach (var option in new[] { "-nps", "--no-phase-shift" })
        {
            var profile = DeewArguments.Resolve(new(Format: format, AdditionalArguments: option), 6);
            Assert.IsTrue(profile.Options.NoPhaseShift);
            var arguments = DeewArguments.Build("input", "output", profile.Options);
            CollectionAssert.AreEqual(new[] { "-sps", "off" }, arguments.TakeLast(2).ToArray());
            CollectionAssert.DoesNotContain(arguments.ToList(), "-nps");
            Assert.AreEqual(1, arguments.Count(a => a == "-np"));
        }
        Assert.IsTrue(DeewArguments.Normalize(new(NoPhaseShift: true)).NoPhaseShift);
    }

    [TestMethod]
    [DataRow("thd")][DataRow("thd-atmos")][DataRow("ac4")][DataRow("ac4-atmos")][DataRow("aac")]
    public void NoPhaseShift_RejectsUnsupportedEncodingButIsIgnoredForMeasurement(string format)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: format, NoPhaseShift: true), 6));
        Assert.IsFalse(DeewArguments.Resolve(new(Format: format, NoPhaseShift: true, MeasureOnly: true), 6).Options.NoPhaseShift);
    }

    [TestMethod]
    public void Ac4Atmos_DefaultsToNoneAndPlainAc4StillRequiresDrc()
    {
        Assert.AreEqual("none", DeewArguments.Resolve(new(Format: "ac4-atmos"), 8).Drc);
        Assert.AreEqual("none", DeewArguments.Resolve(new(Format: "ac4-atmos", Drc: "none"), 8).Drc);
        Assert.AreEqual("music_light", DeewArguments.Resolve(new(Format: "ac4"), 6).Drc);
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "ac4", Drc: "none"), 6));
    }

    [TestMethod]
    [DataRow(44100, false)][DataRow(48000, false)][DataRow(64000, false)][DataRow(72000, true)]
    [DataRow(88200, true)][DataRow(96000, true)][DataRow(176400, true)][DataRow(192000, true)]
    public void TrueHd_ShimFollowsEncodedSampleRateEvenWithDrc(int rate, bool needsShim)
    {
        var source = new AudioSourceInfo("flac", "", 6, "5.1", rate, 24, 10);
        var profile = DeewArguments.Resolve(new(Format: "thd", Drc: "film_light"), source);
        Assert.AreEqual(needsShim, profile.RequiredTools.Contains("dee_shim"));
        Assert.IsFalse(DeewArguments.Resolve(new(Format: "thd", MeasureOnly: true), source).RequiredTools.Contains("dee_shim"));
    }

    [TestMethod]
    public void AtmosMeasurement_UsesSourceMetadataInsteadOfSelectedOutputFormat()
    {
        var source = new AudioSourceInfo("truehd", "Dolby TrueHD + Dolby Atmos", 8, "7.1", 48000, 24, 10);
        foreach (var format in DeewArguments.Formats)
        {
            var warnings = new List<string>();
            var profile = DeewArguments.Resolve(new(Format: format, MeasureOnly: true, DownmixChannels: 2,
                Drc: "none", Dialnorm: -27, BitrateKbps: 640, NoPhaseShift: true, DisableLfe: true), source, warnings: warnings);
            CollectionAssert.AreEqual(new[] { "dee_measure_loudness", "truehdd" }, profile.RequiredTools.ToArray());
            Assert.IsNull(profile.Extension);
            Assert.IsTrue(warnings.Any(w => w.Contains("ignored")));
            var command = DeewArguments.Build("input.thd", "output", profile.Options);
            CollectionAssert.Contains(command.ToList(), "-mo");
            foreach (var ignored in new[] { "-dm", "-r", "-dn", "-b", "-sps", "-dl" })
                CollectionAssert.DoesNotContain(command.ToList(), ignored);
        }
        var ordinaryTrueHd = source with { ProfileName = "Dolby TrueHD" };
        CollectionAssert.AreEqual(new[] { "dee_ddp_encoder" },
            DeewArguments.Resolve(new(Format: "ddp-atmos", MeasureOnly: true), ordinaryTrueHd).RequiredTools.ToArray());
        var joc = source with { CodecName = "eac3", ProfileName = "Dolby Digital Plus + Dolby Atmos", Channels = 6 };
        CollectionAssert.AreEqual(new[] { "dee_measure_loudness" }, DeewArguments.Resolve(new(MeasureOnly: true), joc).RequiredTools.ToArray());
        AudioSourceSupport.ValidateDeewInput("source.mkv", new(MeasureOnly: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => AudioSourceSupport.ValidateDeewInput("source.mp4", new(MeasureOnly: true)));
    }

    [TestMethod]
    public void PipeAndPhaseConfig_AreOptionalTypedAndPreservedByTheLeaseEditor()
    {
        var old = new DeewConfigEditor(Encoding.UTF8.GetBytes(Deew5ContractTests.Config));
        Assert.IsTrue(old.Pipe && old.SurroundPhaseShift);
        var text = "pipe = false # keep WAV\nsurround_phase_shift = false # no filter\n" + Deew5ContractTests.Config;
        var editor = new DeewConfigEditor(Encoding.UTF8.GetBytes(text));
        Assert.IsFalse(editor.Pipe || editor.SurroundPhaseShift);
        var edited = new DeewConfigEditor(editor.Edit("D:\\temp"));
        Assert.IsFalse(edited.Pipe || edited.SurroundPhaseShift);
        StringAssert.Contains(Encoding.UTF8.GetString(editor.Edit(null)), "pipe = false # keep WAV");
        foreach (var key in new[] { "pipe", "surround_phase_shift" })
            Assert.ThrowsExactly<InvalidDataException>(() => new DeewConfigEditor(Encoding.UTF8.GetBytes(key + " = 'wrong'\n" + Deew5ContractTests.Config)));
    }

    [TestMethod]
    [DataRow("true", true)][DataRow("'auto'", true)][DataRow("'always'", true)][DataRow("'never'", false)]
    public void PipeModes_AreInterpretedForSingleInputAndPreservedInToml(string value, bool expectedPipe)
    {
        var line = $"pipe = {value} # user's preference";
        var editor = new DeewConfigEditor(Encoding.UTF8.GetBytes(line + "\n" + Deew5ContractTests.Config));
        Assert.AreEqual(expectedPipe, editor.Pipe);
        StringAssert.Contains(Encoding.UTF8.GetString(editor.Edit(null)), line);
    }

    [TestMethod]
    public void RichLoudnessEvents_PreserveUnitsAndPartialUpdatesAndIgnoreMalformedExtras()
    {
        var reader = new DeewEventsReader(DeewArguments.Resolve(new(MeasureOnly: true), 6), _ => { }, (_, _, _, _) => { });
        reader.Accept("""{"type":"hello","version":"5.5.3"}""");
        reader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":80,"db":-24,"gating":"dialogue","speech":16,"program":-18.9,"lra":13.1,"true_peak":-4.9}""");
        Assert.AreEqual(-24, reader.Db);
        Assert.AreEqual(new DeewLoudnessMetrics("dialogue", 16, -18.9, 13.1, -4.9), reader.LoudnessMetrics);
        reader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":100,"db":null,"gating":null,"speech":150,"program":null,"lra":-1,"true_peak":"bad"}""");
        reader.Accept("""{"type":"progress","job":1,"stage":"measure","db":-10,"lra":99}""");
        Assert.AreEqual(new DeewLoudnessMetrics("dialogue", 16, -18.9, 13.1, -4.9), reader.LoudnessMetrics);
        Assert.IsNull(reader.Error);
        var result = new AudioProcessingResult(Guid.NewGuid(), EncodingJobState.Completed, 0, "Measured", "", "", LoudnessDb: reader.Db, LoudnessMetrics: reader.LoudnessMetrics);
        Assert.AreEqual(result, JsonSerializer.Deserialize<AudioProcessingResult>(JsonSerializer.Serialize(result)));
    }

    [TestMethod]
    [DataRow(-33d)][DataRow(-45d)]
    public void MeasureOnly_PreservesLoudnessBelowTheDialnormRange(double loudness)
    {
        var updates = new List<double?>();
        var reader = new DeewEventsReader(DeewArguments.Resolve(new(MeasureOnly: true), 2), _ => { },
            (_, _, db, _) => updates.Add(db));
        reader.Accept(JsonSerializer.Serialize(new { type = "progress", stage = "measure", percent = 100, db = loudness }));
        reader.Accept("""{"type":"progress","stage":"measure","percent":100,"db":null}""");
        Assert.AreEqual(loudness, reader.Db);
        Assert.AreEqual(loudness, updates[0]);
        Assert.IsNull(updates[1]);

        var encoder = new DeewEventsReader(DeewArguments.Resolve(new(), 2), _ => { }, (_, _, _, _) => { });
        encoder.Accept(JsonSerializer.Serialize(new { type = "progress", stage = "encode", db = loudness }));
        Assert.IsNull(encoder.Db);
    }

    [TestMethod]
    public void EventsHello_RejectsVersionsOlderThanMinimum()
    {
        var reader = new DeewEventsReader(DeewArguments.Resolve(new(MeasureOnly: true), 2), _ => { }, (_, _, _, _) => { });
        reader.Accept("""{"type":"hello","version":"5.5.2"}""");
        StringAssert.Contains(reader.Error!, DeewArguments.MinimumVersion);
    }

    [TestMethod]
    public void Ac4ManifestTarget_DoesNotOverwriteSourceLoudnessMetrics()
    {
        var reader = new DeewEventsReader(DeewArguments.Resolve(new(Format: "ac4", Dialnorm: -27), 6), _ => { }, (_, _, _, _) => { });
        reader.Accept("""{"type":"progress","stage":"measure","db":-15,"gating":"level","lra":8.3,"true_peak":-0.1}""");
        reader.Accept("""{"type":"progress","stage":"encode","db":-27,"gating":"dialogue","lra":0,"true_peak":-4}""");
        Assert.AreEqual(-15, reader.Db);
        Assert.AreEqual(new DeewLoudnessMetrics("level", LoudnessRangeLu: 8.3, TruePeakDbtp: -0.1), reader.LoudnessMetrics);
    }
}

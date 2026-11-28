using System.Text;
using FlowEncode.Domain;
using FlowEncode.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class Deew55ContractTests
{
    [TestMethod]
    public void PipeMode_AcceptsAutoAlwaysNeverInAllFormatsAndMeasureOnly()
    {
        foreach (var mode in new[] { "auto", "always", "never", "ALWAYS" })
        {
            var profile = DeewArguments.Resolve(new(AdditionalArguments: $"-p {mode}"), 6);
            Assert.AreEqual(mode.ToLowerInvariant(), profile.Options.PipeMode);
            var cmd = DeewArguments.Build("in.wav", "out", profile.Options).ToList();
            var idx = cmd.IndexOf("-p");
            Assert.IsTrue(idx >= 0);
            Assert.AreEqual(mode.ToLowerInvariant(), cmd[idx + 1]);

            var mo = DeewArguments.Resolve(new(MeasureOnly: true, PipeMode: mode), 6);
            Assert.AreEqual(mode.ToLowerInvariant(), mo.Options.PipeMode);
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(PipeMode: "invalid")));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(AdditionalArguments: "--pipe=sometimes")));
    }

    [TestMethod]
    public void TrueHddOptions_AllowedForAtmosAc4ImsAndMeasureOnlyAndRejectedElsewhere()
    {
        foreach (var format in new[] { "ddp-atmos", "thd-atmos", "ac4-atmos", "ac4-ims" })
        {
            var profile = DeewArguments.Resolve(new(Format: format, AdditionalArguments: "-wm LoRo -bc ON"), 6, sourceIsTrueHdAtmos: true);
            Assert.AreEqual("loro", profile.Options.WarpMode);
            Assert.AreEqual(true, profile.Options.BedConform);
            var cmd = DeewArguments.Build("in.thd", "out", profile.Options).ToList();
            CollectionAssert.Contains(cmd, "-wm");
            Assert.AreEqual("loro", cmd[cmd.IndexOf("-wm") + 1]);
            CollectionAssert.Contains(cmd, "-bc");
            Assert.AreEqual("on", cmd[cmd.IndexOf("-bc") + 1]);
        }

        // MeasureOnly preserves -wm and -bc even when selected format is non-Atmos.
        var mo = DeewArguments.Resolve(new(Format: "ddp", MeasureOnly: true, WarpMode: "warping", BedConform: false), 8, sourceIsTrueHdAtmos: true);
        Assert.AreEqual("warping", mo.Options.WarpMode);
        Assert.AreEqual(false, mo.Options.BedConform);
        var moCmd = DeewArguments.Build("in.thd", "out", mo.Options).ToList();
        Assert.AreEqual("warping", moCmd[moCmd.IndexOf("-wm") + 1]);
        Assert.AreEqual("off", moCmd[moCmd.IndexOf("-bc") + 1]);

        foreach (var format in new[] { "dd", "ddp", "thd", "ac4", "aac" })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: format, WarpMode: "normal"), 6));
            Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: format, BedConform: true), 6));
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(WarpMode: "invalid")));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(AdditionalArguments: "-bc maybe")));
    }

    [TestMethod]
    public void FilterSwitches_SpsLpfS3aValidateFormatsAndOverrideLegacyAlias()
    {
        var spsOn = DeewArguments.Resolve(new(Format: "ddp", NoPhaseShift: true, AdditionalArguments: "-sps on"), 6);
        Assert.IsTrue(spsOn.Options.SurroundPhaseShift == true && !spsOn.Options.NoPhaseShift);
        var cmd = DeewArguments.Build("in.wav", "out", spsOn.Options).ToList();
        Assert.AreEqual("on", cmd[cmd.IndexOf("-sps") + 1]);

        var lpf = DeewArguments.Resolve(new(Format: "dd", AdditionalArguments: "-lpf off -s3a on"), 6);
        Assert.AreEqual(false, lpf.Options.LowPassFilter);
        Assert.AreEqual(true, lpf.Options.Surround3dbAttenuation);
        var lpfCmd = DeewArguments.Build("in.wav", "out", lpf.Options).ToList();
        Assert.AreEqual("off", lpfCmd[lpfCmd.IndexOf("-lpf") + 1]);
        Assert.AreEqual("on", lpfCmd[lpfCmd.IndexOf("-s3a") + 1]);

        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "thd", LowPassFilter: false), 6));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "ddp-atmos", Surround3dbAttenuation: true), 6));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "aac", SurroundPhaseShift: true), 6));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(AdditionalArguments: "-lpf maybe")));

        // TrueHD allows -s3a on/off.
        var thd = DeewArguments.Resolve(new(Format: "thd", Surround3dbAttenuation: false), 6);
        Assert.AreEqual(false, thd.Options.Surround3dbAttenuation);
    }

    [TestMethod]
    public void Ac4Ims_ValidatesModesDrcBitratesLayoutsAndTools()
    {
        var general = DeewBitrates.Apply(DeewArguments.Resolve(new(Format: "ac4-ims"), 6));
        Assert.AreEqual(".ac4", general.Extension);
        Assert.AreEqual("general", general.Mode);
        Assert.AreEqual("none", general.Drc);
        Assert.AreEqual(256, general.EffectiveBitrateKbps);
        CollectionAssert.AreEqual(new[] { "dee_ac4ims_encoder" }, general.RequiredTools.ToArray());

        var music = DeewBitrates.Apply(DeewArguments.Resolve(new(Format: "ac4-ims", Ac4ImsMode: "music", BitrateKbps: 200), 8));
        Assert.AreEqual("music", music.Mode);
        Assert.AreEqual("music_light", music.Drc);
        Assert.AreEqual(144, music.EffectiveBitrateKbps);

        var fromConfigMusic = DeewArguments.Resolve(new(Format: "ac4-ims"), 6, defaultAc4ImsMode: "music");
        Assert.AreEqual("music_light", fromConfigMusic.Drc);

        var atmos = DeewArguments.Resolve(new(Format: "ac4-ims"), 8, sourceIsTrueHdAtmos: true);
        CollectionAssert.AreEqual(new[] { "dee_ac4ims_encoder", "truehdd" }, atmos.RequiredTools.ToArray());
        AudioSourceSupport.ValidateDeewInput("movie.mkv", new(Format: "ac4-ims"));

        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "ac4-ims", Ac4ImsMode: "music", Drc: "none"), 6));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "ac4-ims"), 2));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "ac4-ims"), 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "ac4-ims", DownmixChannels: 2), 6));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "ac4-ims", DisableLfe: true), 6));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Resolve(new(Format: "ac4-ims", Dialnorm: -27), 6));
    }

    [TestMethod]
    public void OutputNaming_RendersAllPlaceholdersCleansEmptySeparatorsAndValidatesExtensions()
    {
        var values = new DeewOutputNaming.Values(
            Stem: "Movie",
            Name: "Movie DELAY -5ms",
            Format: "ddp",
            Codec: "DDP",
            Channels: "5.1",
            Bitrate: "1024",
            Lang: "eng",
            Title: "Main",
            Track: "0");

        Assert.AreEqual("Movie Movie DELAY -5ms ddp DDP 5.1 1024 eng Main 0.ec3",
            DeewOutputNaming.RenderTemplate("{stem} {name} {format} {codec} {channels} {bitrate} {lang} {title} {track}", values, "ec3"));

        var noLang = values with { Lang = "" };
        Assert.AreEqual("Movie.ddp.ec3", DeewOutputNaming.RenderTemplate("{stem}.{lang}.ddp", noLang, "ec3"));
        Assert.AreEqual("Movie.ec3", DeewOutputNaming.RenderTemplate("{lang} - {stem}", noLang, "ec3"));
        Assert.AreEqual("Movie - Main.ec3", DeewOutputNaming.RenderTemplate("{stem} - {lang} - {title}", noLang, "ec3"));
        Assert.AreEqual("Movie.ec3", DeewOutputNaming.RenderTemplate("{stem}_{lang}", noLang, "ec3"));
        Assert.AreEqual("-Movie-.ec3", DeewOutputNaming.RenderTemplate("-{stem}-", values, "ec3"));

        Assert.AreEqual("Movie.ec3", DeewOutputNaming.RenderTemplate("{stem}.ec3", values, "ec3"));
        Assert.AreEqual("Movie.EC3", DeewOutputNaming.RenderTemplate("{stem}.EC3", values, "ec3"));
        Assert.AreEqual("Movie.commentary.ec3", DeewOutputNaming.RenderTemplate("{stem}.commentary", values, "ec3"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewOutputNaming.RenderTemplate("{stem}.ac3", values, "ec3"));

        Assert.AreEqual("a_b_c_d_e_f_g_h_i_j_ eng.ec3",
            DeewOutputNaming.RenderTemplate("{stem} {lang}", values with { Stem = "a:b/c\\d*e?f\"g<h>i|j\u0001" }, "ec3"));
        Assert.AreEqual("a<b>.ec3", DeewOutputNaming.RenderTemplate("{stem}", values with { Stem = "a<b>" }, "ec3"));

        Assert.AreEqual("Movie", DeewOutputNaming.FileStem(@"D:\media\Movie DELAY -5ms.flac", stripDelay: true));
        Assert.AreEqual("Movie DELAY -5ms", DeewOutputNaming.FileStem(@"D:\media\Movie DELAY -5ms.flac", stripDelay: false));

        // Literal names (-on) keep braces literal and check extension.
        Assert.AreEqual("Custom {1}.ec3", DeewOutputNaming.RenderLiteral("Custom {1}", values, "ec3"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewOutputNaming.RenderLiteral("sub/file", values, "ec3"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewOutputNaming.RenderLiteral("Custom.ac3", values, "ec3"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewOutputNaming.ValidateTemplate("{unknown}"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewOutputNaming.ValidateTemplate("{stem"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewOutputNaming.ValidateTemplate("dir/{stem}"));
    }

    [TestMethod]
    public void OutputNameAndTemplate_ValidateMutualExclusionAndMeasureOnlyConflict()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(OutputName: "a", OutputTemplate: "{stem}")));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(MeasureOnly: true, OutputName: "a")));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(MeasureOnly: true, OutputTemplate: "{stem}.{lang}")));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(AdditionalArguments: "-on dir/name")));
        Assert.ThrowsExactly<InvalidOperationException>(() => DeewArguments.Normalize(new(AdditionalArguments: "-ot {bad}")));

        var withName = DeewArguments.Normalize(new(AdditionalArguments: "--output-name=-lead"));
        Assert.AreEqual("-lead", withName.OutputName);
        CollectionAssert.Contains(DeewArguments.Build("in.wav", "out", withName).ToList(), "--output-name=-lead");
    }

    [TestMethod]
    public void ConfigEditor_ParsesValidatesAndPreservesFullDeew553Config()
    {
        const string full553 = """
            # deew 5.5.3 config
            temp_path = ''
            logo = 1 # logo comment
            max_instances = '50%'
            surround_phase_shift = true
            low_pass_filter = false # user lpf
            surround_3db_attenuation = true
            truehdd_warp_mode = 'prologiciix' # warp
            truehdd_bed_conform = true
            pipe = 'auto'
            [output]
            path = ''
            to_input = true # override on lease
            template = '{stem}.{lang}.{codec}' # keep template
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
            ac4_ims = 144
            aac_2_0 = 256
            [default_modes]
            ddp = 'standard'
            ddp_atmos = 'streaming'
            ac4_ims = 'music'
            [summary_sections]
            deew_info = true
            binaries = true
            input_info = true
            output_info = true
            other = true
            [gui]
            progress = 'cli'
            scaling = '100%'
            use_system_font = false
            theme = 'system'
            """;

        var editor = new DeewConfigEditor(Encoding.UTF8.GetBytes(full553));
        Assert.IsTrue(editor.Pipe);
        Assert.IsFalse(editor.LowPassFilter);
        Assert.AreEqual("prologiciix", editor.TrueHdWarpMode);
        Assert.IsTrue(editor.TrueHdBedConform);
        Assert.AreEqual("{stem}.{lang}.{codec}", editor.OutputTemplate);
        Assert.AreEqual("music", editor.Ac4ImsMode);
        Assert.AreEqual(144, editor.Bitrates["ac4_ims"]);

        // Explicit -s3a off suppresses the TrueHD surround attenuation warning even when config is true.
        var thdDefault = DeewArguments.Resolve(new(Format: "thd"), 6);
        var thdOff = DeewArguments.Resolve(new(Format: "thd", Surround3dbAttenuation: false), 6);
        Assert.IsTrue(editor.AttenuatesTrueHdSurround(thdDefault));
        Assert.IsFalse(editor.AttenuatesTrueHdSurround(thdOff));

        var editedText = Encoding.UTF8.GetString(editor.Edit(@"D:\temp"));
        StringAssert.Contains(editedText, "template = '{stem}.{lang}.{codec}' # keep template");
        StringAssert.Contains(editedText, "truehdd_warp_mode = 'prologiciix' # warp");
        StringAssert.Contains(editedText, "theme = 'system'");
        StringAssert.Contains(editedText, "to_input = false # override on lease");

        Assert.ThrowsExactly<InvalidDataException>(() => new DeewConfigEditor(Encoding.UTF8.GetBytes("truehdd_warp_mode = 'bad'\n" + Deew5ContractTests.Config)));
        Assert.ThrowsExactly<InvalidDataException>(() => new DeewConfigEditor(Encoding.UTF8.GetBytes("truehdd_bed_conform = 'yes'\n" + Deew5ContractTests.Config)));
        Assert.ThrowsExactly<InvalidDataException>(() => new DeewConfigEditor(Encoding.UTF8.GetBytes("low_pass_filter = 'yes'\n" + Deew5ContractTests.Config)));
        Assert.ThrowsExactly<InvalidDataException>(() => new DeewConfigEditor(Encoding.UTF8.GetBytes(Deew5ContractTests.Config.Replace("[output]", "[output]\ntemplate = '{unknown}'"))));
    }

    [TestMethod]
    public void EventsReader_HandlesDeew552PipedProgressPulseAndWritingClamped99()
    {
        var fractions = new List<double?>();
        var reader = new DeewEventsReader(DeewArguments.Resolve(new(Format: "ddp"), 6), _ => { },
            (fraction, _, _, _) => fractions.Add(fraction), piped: true);
        reader.Accept($$"""{"type":"hello","version":"{{DeewArguments.MinimumVersion}}"}""");
        // Unknown duration starts with pulse (null percent), then piped measure (0..40%), then piped encode (40..99.9%).
        reader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":null,"db":null}""");
        reader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":0,"db":null}""");
        reader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":50,"db":null}""");
        reader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":100,"db":-24}""");
        reader.Accept("""{"type":"progress","job":0,"stage":"encode","percent":0,"db":-24}""");
        reader.Accept("""{"type":"progress","job":0,"stage":"encode","percent":50,"db":-24}""");
        reader.Accept("""{"type":"progress","job":0,"stage":"encode","percent":99,"db":-24}""");
        reader.Accept("""{"type":"progress","job":0,"stage":"encode","percent":100,"db":-24}""");

        Assert.IsNull(reader.Error);
        Assert.IsNull(fractions[0]);
        Assert.AreEqual(0.0, fractions[1]!.Value, 0.0001);
        Assert.AreEqual(0.20, fractions[2]!.Value, 0.0001);
        Assert.AreEqual(0.40, fractions[3]!.Value, 0.0001);
        Assert.AreEqual(0.40, fractions[4]!.Value, 0.0001);
        Assert.AreEqual(0.70, fractions[5]!.Value, 0.0001);
        Assert.IsTrue(fractions[6]!.Value > 0.99 && fractions[6]!.Value < 0.999);
        Assert.AreEqual(0.999, fractions[7]!.Value, 0.0001);
    }

    [TestMethod]
    public void EventsReader_AllocatesContiguousRangesWithoutJumpsOrStallsAcrossWorkflows()
    {
        // Piped single-stage encode (AAC or DDP with -dn): starts at 0% instead of jumping to 45%.
        var single = new List<double?>();
        var aacReader = new DeewEventsReader(DeewArguments.Resolve(new(Format: "aac"), 6), _ => { },
            (f, _, _, _) => single.Add(f), piped: true);
        aacReader.Accept("""{"type":"progress","job":0,"stage":"encode","percent":0}""");
        aacReader.Accept("""{"type":"progress","job":0,"stage":"encode","percent":50}""");
        Assert.AreEqual(0.0, single[0]!.Value, 0.0001);
        Assert.AreEqual(0.5, single[1]!.Value, 0.0001);

        // TrueHD Atmos measure-only: decode -> measure progresses continuously without stalling at 35%.
        var atmosMo = new List<double?>();
        var atmosSource = new AudioSourceInfo("truehd", "Dolby TrueHD + Dolby Atmos", 8, "7.1", 48000, 24, 60);
        var atmosReader = new DeewEventsReader(DeewArguments.Resolve(new(MeasureOnly: true), atmosSource), _ => { },
            (f, _, _, _) => atmosMo.Add(f), piped: false);
        atmosReader.Accept("""{"type":"progress","job":0,"stage":"decode","percent":0}""");
        atmosReader.Accept("""{"type":"progress","job":0,"stage":"decode","percent":100}""");
        atmosReader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":10}""");
        atmosReader.Accept("""{"type":"progress","job":0,"stage":"measure","percent":50}""");
        Assert.AreEqual(0.0, atmosMo[0]!.Value, 0.0001);
        Assert.IsTrue(atmosMo[2]!.Value > atmosMo[1]!.Value, "Measure stage must advance immediately after decode instead of stalling.");
        Assert.IsTrue(atmosMo[3]!.Value > atmosMo[2]!.Value);
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace FlowEncode.Domain;

// Runtime audio options deliberately remain separate from video encoding templates.
public sealed record DeewOptions(
    string Format = "ddp", int? BitrateKbps = null, string? Drc = null,
    string? DdpMode = null, string? DdpAtmosMode = null, int? DownmixChannels = null,
    bool DisableLfe = false, int? Dialnorm = null, string? Delay = null,
    int? TrackIndex = null, string? Instances = null, bool KeepTemp = false,
    bool KeepLog = false, bool MeasureOnly = false, bool LongArgument = false,
    string? AdditionalArguments = null, DeewTempMode TempMode = DeewTempMode.OutputVolume,
    string? CustomTempRoot = null, bool NoPhaseShift = false,
    string? Ac4ImsMode = null, bool? SurroundPhaseShift = null, bool? LowPassFilter = null,
    bool? Surround3dbAttenuation = null, string? WarpMode = null, bool? BedConform = null,
    string? PipeMode = null, string? OutputName = null, string? OutputTemplate = null);

public enum DeewTempMode { OutputVolume, Custom, DeewSettings }

public sealed record DeewEffectiveOutputProfile(
    DeewOptions Options, int SourceSlots, int OutputSlots, bool HasLfe,
    string Mode, string Drc, string? Extension, IReadOnlyList<string> RequiredTools,
    DeewDbSource DbSource, int? EffectiveBitrateKbps = null)
{
    public bool HasSpatialDecode => RequiredTools.Contains("truehdd") || RequiredTools.Contains("harletty");
    public bool HasEncodingMode => !Options.MeasureOnly
        && (Options.Format is "ddp-atmos" or "ac4-ims" || Options.Format == "ddp" && OutputSlots == 8);
}

public static class DeewArguments
{
    public const string MinimumVersion = "5.7.0";
    public const string RecommendedVersion = MinimumVersion;
    public static readonly string[] Formats = ["dd", "ddp", "ddp-atmos", "thd", "thd-atmos", "ac4", "ac4-atmos", "ac4-ims", "aac"];
    public static readonly string[] WarpModes = ["normal", "warping", "prologiciix", "loro"];
    public static readonly string[] PipeModes = ["auto", "always", "never"];
    public static readonly string[] Ac4ImsModes = ["general", "music"];
    public static bool HasPhaseShift(string format) => format is "dd" or "ddp" or "ddp-atmos";
    public static bool HasLowPassFilter(string format) => format is "dd" or "ddp";
    public static bool HasSurround3dbAttenuation(string format) => format is "dd" or "ddp" or "thd";
    public static bool IsAtmosFormat(string format) => format is "ddp-atmos" or "thd-atmos" or "ac4-atmos";
    public static bool HasTrueHddOptions(string format) => format is "ddp-atmos" or "thd-atmos" or "ac4-atmos" or "ac4-ims";

    private static bool UsesHarletty(DeewOptions options, AudioSourceInfo source) =>
        !options.MeasureOnly && HasTrueHddOptions(options.Format)
        && (source.IsDtsX || source.Auro3DLayout is not null || (source.IsDts && IsAtmosFormat(options.Format)));

    public static DeewOptions ApplySourceBedConform(DeewOptions options, AudioSourceInfo source, ICollection<string>? warnings = null)
    {
        if (options.BedConform is null || !UsesHarletty(options, source))
            return options;
        if (options.BedConform == false)
            warnings?.Add("Bed conform is always enabled when deew decodes DTS:X or Auro-3D with harletty.");
        return options with { BedConform = null };
    }

    private static readonly (string Short, string Long, bool Flag, string Canonical)[] Allowed =
    [
        ("-b", "--bitrate", false, "-b"), ("-r", "--drc", false, "-r"),
        ("--ddp-mode", "--ddp-mode", false, "--ddp-mode"), ("--ddp-atmos-mode", "--ddp-atmos-mode", false, "--ddp-atmos-mode"),
        ("--ac4-ims-mode", "--ac4-ims-mode", false, "--ac4-ims-mode"),
        ("-dm", "--downmix", false, "-dm"), ("-dl", "--disable-lfe", true, "-dl"),
        // Saved advanced arguments may still use -nps/--no-phase-shift. Build emits -sps on|off.
        ("-nps", "--no-phase-shift", true, "-sps"),
        ("-sps", "--surround-phase-shift", false, "-sps"),
        ("-lpf", "--low-pass-filter", false, "-lpf"),
        ("-s3a", "--surround-3db-attenuation", false, "-s3a"),
        ("-wm", "--warp-mode", false, "-wm"),
        ("-bc", "--bed-conform", false, "-bc"),
        ("-dn", "--dialnorm", false, "-dn"), ("-d", "--delay", false, "-d"),
        ("-ti", "--track-index", false, "-ti"), ("-in", "--instances", false, "-in"),
        ("-p", "--pipe", false, "-p"),
        ("-on", "--output-name", false, "-on"), ("-ot", "--output-template", false, "-ot"),
        ("-k", "--keeptemp", true, "-k"), ("-kl", "--keep-log", true, "-kl"),
        ("-mo", "--measure-only", true, "-mo"), ("-la", "--long-argument", true, "-la")
    ];

    public static DeewOptions Normalize(DeewOptions? input, ICollection<string>? warnings = null)
    {
        var o = input ?? new();
        var values = Values(o);
        var seen = new HashSet<string>();
        var tokens = CommandArgumentTokenizer.Tokenize(o.AdditionalArguments, throwOnUnclosedQuote: true);
        for (var i = 0; i < tokens.Count; i++)
        {
            var pair = tokens[i].Split('=', 2);
            var spec = Allowed.FirstOrDefault(x => x.Short == pair[0] || x.Long == pair[0]);
            if (spec.Short is null) throw new InvalidOperationException($"deew option is reserved or unsupported: {pair[0]}");
            string value;
            if (spec.Flag)
            {
                if (pair.Length != 1) throw new InvalidOperationException($"{pair[0]} takes no value.");
                value = spec.Short == "-nps" ? "off" : "true";
            }
            else
            {
                value = pair.Length == 2 ? pair[1] : ++i < tokens.Count ? tokens[i] : "";
                if (string.IsNullOrWhiteSpace(value) || (pair.Length == 1 && CommandArgumentTokenizer.IsOptionToken(value)))
                    throw new InvalidOperationException($"Missing value for {pair[0]}. Use -d=-5ms for a negative delay.");
            }
            if (values.TryGetValue(spec.Canonical, out var old) && old != value || !seen.Add(spec.Canonical))
                warnings?.Add($"{spec.Canonical}: advanced arguments override the previous value with {value}.");
            values[spec.Canonical] = value;
        }
        int? Number(string key) => values.TryGetValue(key, out var v)
            ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
                : throw new InvalidOperationException($"{key} requires an integer.") : null;
        string? Text(string key) => values.GetValueOrDefault(key);
        string? LowerText(string key) => values.TryGetValue(key, out var v) ? v.ToLowerInvariant() : null;
        bool Flag(string key) => values.ContainsKey(key);
        bool? OnOff(string key)
        {
            if (!values.TryGetValue(key, out var v)) return null;
            return v.ToLowerInvariant() switch
            {
                "on" => true,
                "off" => false,
                _ => throw new InvalidOperationException($"{key} can only be 'on' or 'off'.")
            };
        }

        var surroundPhaseShift = OnOff("-sps");
        var lowPassFilter = OnOff("-lpf");
        var surround3dbAttenuation = OnOff("-s3a");
        var bedConform = OnOff("-bc");

        o = o with
        {
            Format = o.Format.ToLowerInvariant(),
            BitrateKbps = Number("-b"),
            Drc = LowerText("-r"),
            DdpMode = LowerText("--ddp-mode"),
            DdpAtmosMode = LowerText("--ddp-atmos-mode"),
            Ac4ImsMode = LowerText("--ac4-ims-mode"),
            DownmixChannels = Number("-dm"),
            DisableLfe = Flag("-dl"),
            Dialnorm = Number("-dn"),
            Delay = Text("-d"),
            TrackIndex = Number("-ti"),
            Instances = Text("-in"),
            KeepTemp = Flag("-k"),
            KeepLog = Flag("-kl"),
            MeasureOnly = Flag("-mo"),
            LongArgument = Flag("-la"),
            NoPhaseShift = surroundPhaseShift == false,
            SurroundPhaseShift = surroundPhaseShift,
            LowPassFilter = lowPassFilter,
            Surround3dbAttenuation = surround3dbAttenuation,
            WarpMode = LowerText("-wm"),
            BedConform = bedConform,
            PipeMode = LowerText("-p"),
            OutputName = Text("-on"),
            OutputTemplate = Text("-ot"),
            AdditionalArguments = null
        };
        if (!Formats.Contains(o.Format)) throw new InvalidOperationException("Unsupported deew format.");
        if (o.PipeMode is not null && !PipeModes.Contains(o.PipeMode))
            throw new InvalidOperationException("Pipe mode (-p/--pipe) can only be auto, always or never.");
        if (o.WarpMode is not null && !WarpModes.Contains(o.WarpMode))
            throw new InvalidOperationException("Warp mode (-wm/--warp-mode) can only be normal, warping, prologiciix or loro.");
        if (o.MeasureOnly)
        {
            if (o.OutputName is not null || o.OutputTemplate is not null)
                throw new InvalidOperationException("Output name (-on) and output template (-ot) cannot be used with measure-only (-mo).");
            if (o.DownmixChannels.HasValue || o.Drc is not null || o.Dialnorm is not null and not 0
                || o.BitrateKbps.HasValue || o.NoPhaseShift || o.SurroundPhaseShift.HasValue
                || o.LowPassFilter.HasValue || o.Surround3dbAttenuation.HasValue || o.DisableLfe
                || o.DdpMode is not null || o.DdpAtmosMode is not null || o.Ac4ImsMode is not null)
                warnings?.Add("Measure-only measures the source as it is; encoding format, downmix, DRC, dialnorm and other encoding settings are ignored.");
            o = o with
            {
                DownmixChannels = null, Drc = null, Dialnorm = null, BitrateKbps = null,
                NoPhaseShift = false, SurroundPhaseShift = null, LowPassFilter = null, Surround3dbAttenuation = null,
                DisableLfe = false, DdpMode = null, DdpAtmosMode = null, Ac4ImsMode = null
            };
        }
        else
        {
            if (o.OutputName is not null && o.OutputTemplate is not null)
                throw new InvalidOperationException("Output name (-on) and output template (-ot) cannot be used together.");
            if (o.OutputName is not null) DeewOutputNaming.ValidateLiteralName(o.OutputName);
            if (o.OutputTemplate is not null)
            {
                if (string.IsNullOrEmpty(o.OutputTemplate))
                    throw new InvalidOperationException("Invalid deew output template: the template is empty.");
                DeewOutputNaming.ValidateTemplate(o.OutputTemplate);
            }
        }
        if (o.BitrateKbps is <= 0 || o.TrackIndex is < 0 || o.Dialnorm is < -31 or > 0)
            throw new InvalidOperationException("Invalid deew bitrate, track index or dialnorm (-31..0).");
        if (o.Drc is not null && !new[] { "none", "film_light", "film_standard", "music_light", "music_standard", "speech" }.Contains(o.Drc))
            throw new InvalidOperationException("Unsupported deew DRC.");
        if (o.DdpMode is not null and not ("standard" or "bluray")
            || o.DdpAtmosMode is not null and not ("streaming" or "standard" or "bluray")
            || o.Ac4ImsMode is not null and not ("general" or "music"))
            throw new InvalidOperationException("Unsupported deew mode.");
        if (o.Instances is not null && !Regex.IsMatch(o.Instances, @"^[1-9]\d*%?$"))
            throw new InvalidOperationException("Instances must be a positive integer or percentage.");
        if (o.Delay is not null && !Regex.IsMatch(o.Delay, @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:ms|s)?$"))
            throw new InvalidOperationException("Invalid deew delay.");
        return o;
    }

    public static DeewEffectiveOutputProfile Resolve(DeewOptions? input, AudioSourceInfo source,
        string defaultDdpMode = "standard", string defaultAtmosMode = "streaming", ICollection<string>? warnings = null,
        string defaultAc4ImsMode = "general")
    {
        var options = Normalize(input, warnings);
        if (!options.MeasureOnly && IsAtmosFormat(options.Format)
            && !source.IsTrueHdAtmos && !source.IsDts && source.Auro3DLayout is null)
            throw new InvalidOperationException("Atmos output requires TrueHD Atmos, DTS:X or Auro-3D input. DTS:X and Auro-3D require harletty 0.9.0 or newer.");

        options = ApplySourceBedConform(options, source, warnings);
        return ResolveCore(options, source.DeewChannelCount, defaultDdpMode, defaultAtmosMode,
            warnings, source.SampleRate, source.IsTrueHdAtmos, defaultAc4ImsMode, UsesHarletty(options, source));
    }

    public static DeewEffectiveOutputProfile Resolve(DeewOptions? input, int sourceChannels,
        string defaultDdpMode = "standard", string defaultAtmosMode = "streaming", ICollection<string>? warnings = null,
        int? sourceSampleRate = null, bool sourceIsTrueHdAtmos = false, string defaultAc4ImsMode = "general") =>
        ResolveCore(Normalize(input, warnings), sourceChannels, defaultDdpMode, defaultAtmosMode, warnings,
            sourceSampleRate, sourceIsTrueHdAtmos, defaultAc4ImsMode, usesHarletty: false);

    private static DeewEffectiveOutputProfile ResolveCore(DeewOptions o, int sourceChannels, string defaultDdpMode,
        string defaultAtmosMode, ICollection<string>? warnings, int? sourceSampleRate, bool sourceIsTrueHdAtmos,
        string defaultAc4ImsMode, bool usesHarletty)
    {
        var atmos = IsAtmosFormat(o.Format);
        var ac4Ims = o.Format == "ac4-ims";
        var imsAtmos = ac4Ims && (sourceIsTrueHdAtmos || usesHarletty);
        var slots = sourceChannels switch { 5 => 6, 7 => 8, _ => sourceChannels };
        if (o.MeasureOnly)
        {
            if (!sourceIsTrueHdAtmos && sourceChannels is not (1 or 2 or 5 or 6 or 7 or 8))
                throw new InvalidOperationException("deew requires a supported source layout (1, 2, 5, 6, 7 or 8 channels).");
            return new(o, slots, 0, false, "", "", null,
                sourceIsTrueHdAtmos ? ["dee_measure_loudness", "truehdd"]
                    : slots == 8 ? ["dee_measure_loudness", "dee_shim"] : ["dee_measure_loudness"], DeewDbSource.Measured);
        }
        if (!atmos && !imsAtmos && sourceChannels is not (1 or 2 or 5 or 6 or 7 or 8))
            throw new InvalidOperationException("deew requires a supported source layout (1, 2, 5, 6, 7 or 8 channels).");
        if (ac4Ims && !imsAtmos && sourceChannels is 1 or 2)
            throw new InvalidOperationException("AC4 IMS requires 5.0, 5.1, 7.0, 7.1, TrueHD Atmos, DTS:X or Auro-3D input.");
        if (ac4Ims && o.DownmixChannels.HasValue) throw new InvalidOperationException("AC4 IMS does not support downmix.");
        if (ac4Ims && o.DisableLfe) throw new InvalidOperationException("AC4 IMS does not support disable LFE.");
        if (atmos && o.DownmixChannels.HasValue) throw new InvalidOperationException("Atmos does not support downmix.");
        if (atmos && o.DisableLfe) { warnings?.Add("Disable LFE is ignored for Atmos."); o = o with { DisableLfe = false }; }
        if (sourceChannels == 1 && o.Format is "aac" or "thd" or "ac4") throw new InvalidOperationException("This format does not support mono input.");
        if (o.DownmixChannels is int dm && (dm >= sourceChannels || !(o.Format switch {
            "dd" or "ddp" => dm is 1 or 2 or 6, "thd" or "ac4" => dm is 2 or 6, "aac" => dm == 2, _ => false })))
            throw new InvalidOperationException("Unsupported downmix for this source and format.");
        if (o.Format.StartsWith("thd") && o.BitrateKbps.HasValue) throw new InvalidOperationException("TrueHD does not accept bitrate.");
        if (o.Dialnorm is not null and not 0 && o.Format is "aac" or "ac4-atmos" or "ac4-ims") throw new InvalidOperationException("This format does not support dialnorm.");
        if (o.Format == "ac4" && slots == 8 && o.Dialnorm is not null and not 0) throw new InvalidOperationException("AC4 dialnorm measurement does not support 7.0/7.1 sources.");
        var output = atmos || imsAtmos ? 0
            : ac4Ims ? 6
            : o.Format == "aac" ? 2
            : o.DownmixChannels ?? (slots == 8 && o.Format is "dd" or "ac4" ? 6 : slots);
        var hasLfe = output >= 6 && !o.DisableLfe && sourceChannels is 6 or 8;
        var mode = o.Format == "ddp-atmos" ? o.DdpAtmosMode ?? defaultAtmosMode
            : ac4Ims ? o.Ac4ImsMode ?? defaultAc4ImsMode
            : o.DdpMode ?? defaultDdpMode;
        if (ac4Ims && mode == "music" && o.Drc is not null and not "music_light")
            throw new InvalidOperationException("AC4 IMS music mode requires DRC music_light.");
        var drc = ac4Ims && mode == "music" ? "music_light" : o.Drc ?? (o.Format == "ac4" ? "music_light" : "none");
        if (o.Format == "ac4" && drc == "none") throw new InvalidOperationException("AC4 requires DRC.");
        if ((o.NoPhaseShift || o.SurroundPhaseShift.HasValue) && !HasPhaseShift(o.Format))
            throw new InvalidOperationException("Surround phase shift is supported only for DD, DDP and DDP Atmos.");
        if (o.LowPassFilter.HasValue && !HasLowPassFilter(o.Format))
            throw new InvalidOperationException("Low-pass filter is supported only for DD and DDP.");
        if (o.Surround3dbAttenuation.HasValue && !HasSurround3dbAttenuation(o.Format))
            throw new InvalidOperationException("Surround 3 dB attenuation is supported only for DD, DDP and TrueHD.");
        if (o.WarpMode is not null && !HasTrueHddOptions(o.Format))
            throw new InvalidOperationException("Warp mode is supported only for DDP Atmos, TrueHD Atmos, AC4 Atmos, AC4 IMS and measure-only.");
        if (o.BedConform.HasValue && !HasTrueHddOptions(o.Format))
            throw new InvalidOperationException("Bed conformance is supported only for DDP Atmos, TrueHD Atmos, AC4 Atmos, AC4 IMS and measure-only.");
        if (o.DdpMode is not null && (o.Format != "ddp" || output != 8)) { warnings?.Add("DDP mode is ignored."); o = o with { DdpMode = null }; }
        if (o.DdpAtmosMode is not null && o.Format != "ddp-atmos") { warnings?.Add("DDP Atmos mode is ignored."); o = o with { DdpAtmosMode = null }; }
        if (o.Ac4ImsMode is not null && !ac4Ims) { warnings?.Add("AC4 IMS mode is ignored."); o = o with { Ac4ImsMode = null }; }
        var tools = new List<string> { o.Format switch { "dd" or "ddp" => "dee_ddp_encoder", "ddp-atmos" => "dee_ddpjoc_encoder",
            "thd" or "thd-atmos" => "dee_dthd_encoder", "ac4" => "dee_ac4_encoder", "ac4-atmos" => "dee_ac4ajoc_encoder",
            "ac4-ims" => "dee_ac4ims_encoder", _ => "dee_aac_encoder" } };
        if (atmos || imsAtmos) tools.Add(usesHarletty ? "harletty" : "truehdd");
        // deew maps TrueHD input rates >=72 kHz to 96 kHz. All need --apodise off, regardless of DRC.
        if (o.Format is "ddp-atmos" or "thd-atmos" || o.Format == "thd" && (drc == "none" || sourceSampleRate is >= 72000)
            || o.Format == "ddp" && output == 8 && (mode == "standard" || !hasLfe)
            || o.Format is "dd" or "ddp" && sourceChannels == 5 && output == 6) tools.Add("dee_shim");
        if (o.Format == "ac4" && o.Dialnorm is not null and not 0) tools.Add("dee_measure_loudness");
        var ext = o.Format switch { "dd" => ".ac3", "ddp" when output == 8 && mode == "bluray" => ".eb3",
            "ddp-atmos" when mode == "bluray" => ".eb3", "ddp" or "ddp-atmos" => ".ec3",
            "thd" or "thd-atmos" => ".thd", "ac4" or "ac4-atmos" or "ac4-ims" => ".ac4", _ => ".aac" };
        var dbSource = o.Format == "ac4" && o.Dialnorm is not null and not 0 ? DeewDbSource.Measured
            : o.Dialnorm is not null and not 0 ? DeewDbSource.SpecifiedDialnorm : DeewDbSource.Unknown;
        return new(o, slots, output, hasLfe, mode, drc, ext, tools, dbSource);
    }

    public static IReadOnlyList<string> Build(string source, string outputDirectory, DeewOptions? options)
    {
        var o = Normalize(options);
        var result = new List<string> { "-i", source, "-o", outputDirectory, "-f", o.Format, "-np" };
        var values = Values(o);
        var emitted = new HashSet<string>();
        foreach (var spec in Allowed)
        {
            if (spec.Short == "-nps" || !emitted.Add(spec.Canonical)) continue;
            if (values.TryGetValue(spec.Canonical, out var value))
            {
                if (spec.Flag) result.Add(spec.Canonical);
                else if (spec.Canonical == "-d" && value.StartsWith('-')) result.Add("-d=" + value);
                else if (spec.Canonical == "-on" && value.StartsWith('-')) result.Add("--output-name=" + value);
                else if (spec.Canonical == "-ot" && value.StartsWith('-')) result.Add("--output-template=" + value);
                else { result.Add(spec.Canonical); result.Add(value); }
            }
        }
        return result;
    }

    private static Dictionary<string, string> Values(DeewOptions o)
    {
        var d = new Dictionary<string, string>();
        void Add(string k, object? v) { if (v is not null) d[k] = Convert.ToString(v, CultureInfo.InvariantCulture)!; }
        void Flag(string k, bool v) { if (v) d[k] = "true"; }
        void Switch(string k, bool? v) { if (v.HasValue) d[k] = v.Value ? "on" : "off"; }
        Add("-b", o.BitrateKbps); Add("-r", o.Drc); Add("--ddp-mode", o.DdpMode); Add("--ddp-atmos-mode", o.DdpAtmosMode);
        Add("--ac4-ims-mode", o.Ac4ImsMode);
        Add("-dm", o.DownmixChannels); Add("-dn", o.Dialnorm); Add("-d", o.Delay); Add("-ti", o.TrackIndex); Add("-in", o.Instances);
        Add("-wm", o.WarpMode); Add("-p", o.PipeMode); Add("-on", o.OutputName); Add("-ot", o.OutputTemplate);
        Flag("-dl", o.DisableLfe); Flag("-k", o.KeepTemp); Flag("-kl", o.KeepLog); Flag("-mo", o.MeasureOnly); Flag("-la", o.LongArgument);
        Switch("-sps", o.SurroundPhaseShift ?? (o.NoPhaseShift ? false : null));
        Switch("-lpf", o.LowPassFilter);
        Switch("-s3a", o.Surround3dbAttenuation);
        Switch("-bc", o.BedConform);
        return d;
    }
}

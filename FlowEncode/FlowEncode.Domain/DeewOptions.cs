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
    string? CustomTempRoot = null);

public enum DeewTempMode { OutputVolume, Custom, DeewSettings }

public sealed record DeewEffectiveOutputProfile(
    DeewOptions Options, int SourceSlots, int OutputSlots, bool HasLfe,
    string Mode, string Drc, string? Extension, IReadOnlyList<string> RequiredTools,
    DeewDbSource DbSource, int? EffectiveBitrateKbps = null);

public static class DeewArguments
{
    public static readonly string[] Formats = ["dd", "ddp", "ddp-atmos", "thd", "thd-atmos", "ac4", "ac4-atmos", "aac"];
    private static readonly (string Short, string Long, bool Flag)[] Allowed =
    [
        ("-b", "--bitrate", false), ("-r", "--drc", false),
        ("--ddp-mode", "--ddp-mode", false), ("--ddp-atmos-mode", "--ddp-atmos-mode", false),
        ("-dm", "--downmix", false), ("-dl", "--disable-lfe", true),
        ("-dn", "--dialnorm", false), ("-d", "--delay", false),
        ("-ti", "--track-index", false), ("-in", "--instances", false),
        ("-k", "--keeptemp", true), ("-kl", "--keep-log", true),
        ("-mo", "--measure-only", true), ("-la", "--long-argument", true)
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
                value = "true";
            }
            else
            {
                value = pair.Length == 2 ? pair[1] : ++i < tokens.Count ? tokens[i] : "";
                if (string.IsNullOrWhiteSpace(value) || CommandArgumentTokenizer.IsOptionToken(value))
                    throw new InvalidOperationException($"Missing value for {pair[0]}. Use -d=-5ms for a negative delay.");
            }
            if (values.TryGetValue(spec.Short, out var old) && old != value || !seen.Add(spec.Short))
                warnings?.Add($"{spec.Short}: advanced arguments override the previous value with {value}.");
            values[spec.Short] = value;
        }
        int? Number(string key) => values.TryGetValue(key, out var v)
            ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
                : throw new InvalidOperationException($"{key} requires an integer.") : null;
        string? Text(string key) => values.GetValueOrDefault(key);
        bool Flag(string key) => values.ContainsKey(key);
        o = o with { Format = o.Format.ToLowerInvariant(), BitrateKbps = Number("-b"), Drc = Text("-r"),
            DdpMode = Text("--ddp-mode"), DdpAtmosMode = Text("--ddp-atmos-mode"), DownmixChannels = Number("-dm"),
            DisableLfe = Flag("-dl"), Dialnorm = Number("-dn"), Delay = Text("-d"), TrackIndex = Number("-ti"),
            Instances = Text("-in"), KeepTemp = Flag("-k"), KeepLog = Flag("-kl"), MeasureOnly = Flag("-mo"),
            LongArgument = Flag("-la"), AdditionalArguments = null };
        if (!Formats.Contains(o.Format)) throw new InvalidOperationException("Unsupported deew format.");
        if (o.BitrateKbps is <= 0 || o.TrackIndex is < 0 || o.Dialnorm is < -31 or > 0)
            throw new InvalidOperationException("Invalid deew bitrate, track index or dialnorm (-31..0).");
        if (o.Drc is not null && !new[] { "none", "film_light", "film_standard", "music_light", "music_standard", "speech" }.Contains(o.Drc))
            throw new InvalidOperationException("Unsupported deew DRC.");
        if (o.DdpMode is not null and not ("standard" or "bluray") || o.DdpAtmosMode is not null and not ("streaming" or "standard" or "bluray"))
            throw new InvalidOperationException("Unsupported deew mode.");
        if (o.Instances is not null && !Regex.IsMatch(o.Instances, @"^[1-9]\d*%?$"))
            throw new InvalidOperationException("Instances must be a positive integer or percentage.");
        if (o.Delay is not null && !Regex.IsMatch(o.Delay, @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:ms|s)?$"))
            throw new InvalidOperationException("Invalid deew delay.");
        return o;
    }

    public static DeewEffectiveOutputProfile Resolve(DeewOptions? input, int sourceChannels,
        string defaultDdpMode = "standard", string defaultAtmosMode = "streaming", ICollection<string>? warnings = null)
    {
        var o = Normalize(input, warnings);
        var atmos = o.Format.EndsWith("-atmos", StringComparison.Ordinal);
        if (!atmos && sourceChannels is not (1 or 2 or 5 or 6 or 7 or 8)) throw new InvalidOperationException("deew requires a supported source layout (1, 2, 5, 6, 7 or 8 channels).");
        if (atmos && (o.DownmixChannels.HasValue || o.MeasureOnly)) throw new InvalidOperationException("Atmos does not support downmix or measure-only.");
        if (atmos && o.DisableLfe) { warnings?.Add("Disable LFE is ignored for Atmos."); o = o with { DisableLfe = false }; }
        var slots = sourceChannels switch { 5 => 6, 7 => 8, _ => sourceChannels };
        if (o.MeasureOnly)
        {
            if (o.Dialnorm is not null and not 0) warnings?.Add("Dialnorm is ignored in measure-only mode.");
            o = o with { Dialnorm = null };
            return new(o, slots, 0, false, "", "", null,
                [slots == 8 ? "dee_ddp_encoder" : "dee_measure_loudness"], DeewDbSource.Measured);
        }
        if (sourceChannels == 1 && o.Format is "aac" or "thd" or "ac4") throw new InvalidOperationException("This format does not support mono input.");
        if (o.DownmixChannels is int dm && (dm >= sourceChannels || !(o.Format switch {
            "dd" or "ddp" => dm is 1 or 2 or 6, "thd" or "ac4" => dm is 2 or 6, "aac" => dm == 2, _ => false })))
            throw new InvalidOperationException("Unsupported downmix for this source and format.");
        if (o.Format.StartsWith("thd") && o.BitrateKbps.HasValue) throw new InvalidOperationException("TrueHD does not accept bitrate.");
        if (o.Dialnorm is not null and not 0 && o.Format is "aac" or "ac4-atmos") throw new InvalidOperationException("This format does not support dialnorm.");
        if (o.Format == "ac4" && slots == 8 && o.Dialnorm is not null and not 0) throw new InvalidOperationException("AC4 dialnorm measurement does not support 7.0/7.1 sources.");
        var output = atmos ? 0 : o.Format == "aac" ? 2 : o.DownmixChannels ?? (slots == 8 && o.Format is "dd" or "ac4" ? 6 : slots);
        var hasLfe = output >= 6 && !o.DisableLfe && sourceChannels is 6 or 8;
        var drc = o.Drc ?? (o.Format.StartsWith("ac4") ? "music_light" : "none");
        if (o.Format.StartsWith("ac4") && drc == "none") throw new InvalidOperationException("AC4 requires DRC.");
        if (o.DdpMode is not null && (o.Format != "ddp" || output != 8)) { warnings?.Add("DDP mode is ignored."); o = o with { DdpMode = null }; }
        if (o.DdpAtmosMode is not null && o.Format != "ddp-atmos") { warnings?.Add("DDP Atmos mode is ignored."); o = o with { DdpAtmosMode = null }; }
        var mode = o.Format == "ddp-atmos" ? o.DdpAtmosMode ?? defaultAtmosMode : o.DdpMode ?? defaultDdpMode;
        var tools = new List<string> { o.Format switch { "dd" or "ddp" => "dee_ddp_encoder", "ddp-atmos" => "dee_ddpjoc_encoder",
            "thd" or "thd-atmos" => "dee_dthd_encoder", "ac4" => "dee_ac4_encoder", "ac4-atmos" => "dee_ac4ajoc_encoder", _ => "dee_aac_encoder" } };
        if (atmos) tools.Add("truehdd");
        if (o.Format is "ddp-atmos" or "thd-atmos" || o.Format == "thd" && drc == "none"
            || o.Format == "ddp" && output == 8 && (mode == "standard" || !hasLfe)) tools.Add("dee_shim");
        if (o.Format == "ac4" && o.Dialnorm is not null and not 0) tools.Add("dee_measure_loudness");
        var ext = o.Format switch { "dd" => ".ac3", "ddp" when output == 8 && mode == "bluray" => ".eb3",
            "ddp-atmos" when mode == "bluray" => ".eb3", "ddp" or "ddp-atmos" => ".ec3",
            "thd" or "thd-atmos" => ".thd", "ac4" or "ac4-atmos" => ".ac4", _ => ".aac" };
        var dbSource = o.Format == "ac4" && o.Dialnorm is not null and not 0 ? DeewDbSource.Measured
            : o.Dialnorm is not null and not 0 ? DeewDbSource.SpecifiedDialnorm : DeewDbSource.Unknown;
        return new(o, slots, output, hasLfe, mode, drc, ext, tools, dbSource);
    }

    public static IReadOnlyList<string> Build(string source, string outputDirectory, DeewOptions? options)
    {
        var o = Normalize(options);
        var result = new List<string> { "-i", source, "-o", outputDirectory, "-f", o.Format, "-np" };
        var values = Values(o);
        foreach (var spec in Allowed)
            if (values.TryGetValue(spec.Short, out var value))
            {
                if (spec.Flag) result.Add(spec.Short);
                else if (spec.Short == "-d" && value.StartsWith('-')) result.Add("-d=" + value);
                else { result.Add(spec.Short); result.Add(value); }
            }
        return result;
    }

    private static Dictionary<string, string> Values(DeewOptions o)
    {
        var d = new Dictionary<string, string>();
        void Add(string k, object? v) { if (v is not null) d[k] = Convert.ToString(v, CultureInfo.InvariantCulture)!; }
        void Flag(string k, bool v) { if (v) d[k] = "true"; }
        Add("-b", o.BitrateKbps); Add("-r", o.Drc); Add("--ddp-mode", o.DdpMode); Add("--ddp-atmos-mode", o.DdpAtmosMode);
        Add("-dm", o.DownmixChannels); Add("-dn", o.Dialnorm); Add("-d", o.Delay); Add("-ti", o.TrackIndex); Add("-in", o.Instances);
        Flag("-dl", o.DisableLfe); Flag("-k", o.KeepTemp); Flag("-kl", o.KeepLog); Flag("-mo", o.MeasureOnly); Flag("-la", o.LongArgument);
        return d;
    }
}

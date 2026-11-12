namespace FlowEncode.Domain;

// Fixed deew 5.1.0 bitrate contract (src/data.rs at 67f14b4), GPL-3.0.
public static class DeewBitrates
{
    private static readonly int[] Dd = [96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];
    private static readonly int[] DdpMono = [32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120, 128, 144, 160, 176, 192, 200, 208, 216, 224, 232, 240, 248, 256, 272, 288, 304, 320, 336, 352, 368, 384, 400, 448, 512, 576, 640, 704, 768, 832, 896, 960, 1008, 1024];
    public static IReadOnlyDictionary<string, int> Defaults { get; } = new Dictionary<string, int>
    {
        ["dd_1_0"] = 128, ["dd_2_0"] = 256, ["dd_5_1"] = 640,
        ["ddp_1_0"] = 128, ["ddp_2_0"] = 256, ["ddp_5_1"] = 1024,
        ["ddp_7_1_standard"] = 1536, ["ddp_7_1_bluray"] = 1536,
        ["ddp_atmos_streaming"] = 1024, ["ddp_atmos_standard"] = 1536, ["ddp_atmos_bluray"] = 1536,
        ["ac4_2_0"] = 256, ["ac4_5_1"] = 448, ["ac4_atmos"] = 448, ["aac_2_0"] = 256
    };

    public static string? ConfigKey(DeewEffectiveOutputProfile p) => p.Options.MeasureOnly ? null : p.Options.Format switch
    {
        "dd" => "dd_" + Layout(p.OutputSlots),
        "ddp" when p.OutputSlots == 8 => "ddp_7_1_" + p.Mode,
        "ddp" => "ddp_" + Layout(p.OutputSlots),
        "ddp-atmos" => "ddp_atmos_" + p.Mode,
        "ac4" => "ac4_" + Layout(p.OutputSlots), "ac4-atmos" => "ac4_atmos", "aac" => "aac_2_0", _ => null
    };

    public static IReadOnlyList<int> Allowed(DeewEffectiveOutputProfile p)
    {
        if (p.Options.MeasureOnly) return [];
        return p.Options.Format switch
        {
            "dd" => p.OutputSlots == 6 ? [224, 256, 320, 384, 448, 512, 576, 640] : Dd,
            "ddp" when p.OutputSlots == 8 && p.Mode == "bluray" => [768, 1024, 1280, 1536, 1664],
            "ddp" when p.OutputSlots == 8 => [384, 448, 504, 576, 640, 704, 768, 832, 896, 960, 1008, 1024, 1152, 1280, 1408, 1512, 1536, 1664, 1792],
            "ddp" => DdpMono.Where(n => n >= (p.OutputSlots == 1 ? 32 : p.OutputSlots == 2 ? 96 : 192)).ToArray(),
            "ddp-atmos" when p.Mode == "streaming" => [384, 448, 576, 640, 768, 1024],
            "ddp-atmos" when p.Mode == "bluray" => [1152, 1280, 1408, 1512, 1536, 1664],
            "ddp-atmos" => [768, 1024, 1152, 1280, 1408, 1536, 1664, 1792, 2048],
            "ac4" when p.OutputSlots == 2 => [48, 64, 96, 128, 144, 192, 256, 288, 320, 384, 448, 512, 768],
            "ac4" => [96, 128, 144, 192, 256, 288, 320, 384, 448, 512, 768],
            "ac4-atmos" => [256, 320, 448, 768],
            "aac" => [56, 64, 80, 96, 112, 128, 144, 160, 192, 224, 256, 288, 320, 384, 448, 512, 576],
            _ => []
        };
    }

    public static DeewEffectiveOutputProfile Apply(DeewEffectiveOutputProfile profile, IReadOnlyDictionary<string, int>? configured = null)
    {
        var key = ConfigKey(profile);
        if (key is null) return profile;
        var requested = profile.Options.BitrateKbps ?? (configured ?? Defaults).GetValueOrDefault(key, Defaults[key]);
        // Upstream preserves the first (lower) candidate on an equal-distance tie.
        var actual = Allowed(profile).OrderBy(n => Math.Abs((long)n - requested)).First();
        return profile with { EffectiveBitrateKbps = actual };
    }

    private static string Layout(int slots) => slots switch { 1 => "1_0", 2 => "2_0", 6 => "5_1", _ => throw new InvalidOperationException("Unsupported bitrate layout.") };
}

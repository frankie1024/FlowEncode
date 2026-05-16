namespace FlowEncode.Application;

public sealed record VapourSynthSnippetDefinition(
    string Id,
    string InsertText,
    bool InsertOnNewLine = true);

public static class VapourSynthSnippetLibrary
{
    public const string LsmasSourceId = "lsmas-source";
    public const string CropAndFillMarginsId = "crop-fill-margins";
    public const string ResizeFormatId = "resize-format";
    public const string CompareOutputsId = "compare-outputs";
    public const string VivtcIvtcId = "vivtc-ivtc";

    public static IReadOnlyList<VapourSynthSnippetDefinition> All { get; } =
    [
        new(
            LsmasSourceId,
            """
            import vapoursynth as vs

            core = vs.core
            file = r"${1:C:\path\to\source.mkv}"
            clip = core.lsmas.LWLibavSource(file)

            clip.set_output()
            """),
        new(
            CropAndFillMarginsId,
            """
            clip = core.std.Crop(clip, left=${1:0}, right=${2:0}, top=${3:0}, bottom=${4:0})

            # Optional dirty edge fill. Requires awsmfunc imported as awf.
            # clip = awf.fb(clip, left=${5:0}, right=${6:0}, top=${7:0}, bottom=${8:0}, mode="fillmargins")
            # clip = awf.zresize(clip, left=${5:0}, right=${6:0}, top=${7:0}, bottom=${8:0})
            """),
        new(
            ResizeFormatId,
            """
            clip = core.resize.Spline36(
                clip,
                width=${1:1920},
                height=${2:1080},
                format=vs.YUV420P10,
                matrix_in_s="${3:709}",
                matrix_s="${4:709}",
                range_s="${5:limited}",
                dither_type="error_diffusion")
            """),
        new(
            CompareOutputsId,
            """
            import vapoursynth as vs

            core = vs.core
            source_a = r"${1:C:\path\to\source.mkv}"
            source_b = r"${2:C:\path\to\encode.mkv}"

            clip_a = core.lsmas.LWLibavSource(source_a)
            clip_b = core.lsmas.LWLibavSource(source_b)

            clip_a.set_output(0)
            clip_b.set_output(1)
            """),
        new(
            VivtcIvtcId,
            """
            # IVTC for NTSC/DVD-style sources. Requires the vivtc plugin.
            clip = core.vivtc.VFM(clip, order=${1:1}, mode=${2:5})
            clip = core.vivtc.VDecimate(clip)
            """)
    ];

    public static VapourSynthSnippetDefinition? FindById(string id)
    {
        return All.FirstOrDefault(snippet => string.Equals(snippet.Id, id, StringComparison.Ordinal));
    }
}

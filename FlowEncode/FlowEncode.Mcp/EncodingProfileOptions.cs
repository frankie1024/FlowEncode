using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using ModelContextProtocol;

namespace FlowEncode.Mcp;

/// <summary>Optional per-field overrides applied on top of a template or an encoder's default profile.</summary>
public sealed class EncodingProfileOptions
{
    [Description("Encoder preset, e.g. 'veryslow' for x264/x265 or '0'-'13' for SVT-AV1.")]
    public string? Preset { get; init; }

    [Description("Encoder tune, e.g. 'film' for x264 or 'VQ'/'PSNR'/'SSIM' for SVT-AV1. Empty string clears it.")]
    public string? Tune { get; init; }

    [Description("Encoder profile, e.g. 'High' for x264, 'main10' for x265, 'main' for SVT-AV1.")]
    public string? Profile { get; init; }

    [Description("Rate control mode: crf, cq, qp, abr, vbr or 2pass.")]
    public string? RateControl { get; init; }

    [Description("Quality value for crf/cq/qp rate control (e.g. 18).")]
    public double? Quality { get; init; }

    [Description("Target bitrate in kbps for abr/vbr/2pass rate control.")]
    public int? Bitrate { get; init; }

    [Description("Output container / extension, e.g. '264', 'hevc', 'ivf', 'mkv'.")]
    public string? OutputContainer { get; init; }

    [Description("Extra encoder CLI arguments. Replaces the base profile's additional arguments entirely.")]
    public string? AdditionalArguments { get; init; }

    [Description("x265 only: HDR / UHD parameters (e.g. mastering display, max-cll).")]
    public string? UhdParameters { get; init; }
}

internal static class EncodingProfileResolver
{
    public static async Task<EncodingProfile> ResolveAsync(
        IProfileLibraryService profileLibrary,
        string? templateId,
        string? encoder,
        EncodingProfileOptions? options,
        CancellationToken cancellationToken)
    {
        EncodingProfile profile;
        if (!string.IsNullOrWhiteSpace(templateId))
        {
            var template = await FindTemplateAsync(profileLibrary, templateId, cancellationToken)
                ?? throw new McpException($"Template '{templateId}' was not found. Call list_templates to see the available ids.");
            profile = template.Profile;

            if (!string.IsNullOrWhiteSpace(encoder) && ParseEncoder(encoder) != profile.Kind)
            {
                throw new McpException($"Template '{template.Name}' is a {profile.Kind.ToDisplayName()} template; it cannot be used with encoder '{encoder}'.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(encoder))
        {
            profile = DefaultEncodingProfiles.GetDefault(ParseEncoder(encoder));
        }
        else
        {
            throw new McpException("Specify either 'templateId' or 'encoder' (x264, x265 or svt-av1).");
        }

        profile = ApplyOverrides(profile, options);

        try
        {
            RequestValidation.ValidateEncodingProfile(profile);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message, ex);
        }

        var conflict = EncoderArgumentConflictValidator.FindFirstConflict(profile.Kind, profile.AdditionalArguments, profile.UhdParameters);
        if (conflict is not null)
        {
            throw new McpException(
                $"Conflicting encoder arguments: '{conflict.FirstOptionName}' ({conflict.FirstValue ?? "switch"}, {conflict.FirstSource}) "
                + $"vs '{conflict.SecondOptionName}' ({conflict.SecondValue ?? "switch"}, {conflict.SecondSource}).");
        }

        return profile;
    }

    public static async Task<SavedTemplate?> FindTemplateAsync(
        IProfileLibraryService profileLibrary,
        string idOrName,
        CancellationToken cancellationToken)
    {
        var templates = await profileLibrary.GetUserTemplatesAsync(cancellationToken);
        return templates.FirstOrDefault(template => string.Equals(template.Id, idOrName, StringComparison.OrdinalIgnoreCase))
            ?? templates.FirstOrDefault(template => string.Equals(template.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public static EncoderKind ParseEncoder(string value)
    {
        return Normalize(value) switch
        {
            "x264" or "avc" or "h264" => EncoderKind.X264,
            "x265" or "hevc" or "h265" => EncoderKind.X265,
            "svtav1" or "av1" => EncoderKind.SvtAv1,
            _ => throw new McpException($"Unknown encoder '{value}'. Use x264, x265 or svt-av1.")
        };
    }

    public static RateControlMode ParseRateControl(string value)
    {
        return Normalize(value) switch
        {
            "crf" => RateControlMode.Crf,
            "cq" => RateControlMode.Cq,
            "qp" or "cqp" => RateControlMode.Qp,
            "abr" => RateControlMode.Abr,
            "vbr" => RateControlMode.Vbr,
            "2pass" or "twopass" => RateControlMode.TwoPass,
            _ => throw new McpException($"Unknown rate control '{value}'. Use crf, cq, qp, abr, vbr or 2pass.")
        };
    }

    public static InputPipelineKind ParsePipeline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return InputPipelineKind.Auto;
        }

        return Normalize(value) switch
        {
            "auto" => InputPipelineKind.Auto,
            "y4m" or "y4mfile" => InputPipelineKind.Y4mFile,
            "yuv" or "rawyuv" or "rawyuvfile" => InputPipelineKind.RawYuvFile,
            "vapoursynth" or "vpy" => InputPipelineKind.VapourSynth,
            "avisynth" or "avs" => InputPipelineKind.AviSynth,
            "ffmpeg" or "ffmpegpipe" => InputPipelineKind.FfmpegPipe,
            _ => throw new McpException($"Unknown pipeline '{value}'. Use auto, vapoursynth, avisynth, ffmpeg, y4m or yuv.")
        };
    }

    public static string ToRateControlToken(RateControlMode mode) => mode switch
    {
        RateControlMode.TwoPass => "2pass",
        _ => mode.ToString().ToLowerInvariant()
    };

    public static string ToEncoderToken(EncoderKind kind) => kind switch
    {
        EncoderKind.SvtAv1 => "svt-av1",
        _ => kind.ToShortName()
    };

    private static EncodingProfile ApplyOverrides(EncodingProfile profile, EncodingProfileOptions? options)
    {
        if (options is null)
        {
            return profile;
        }

        return profile with
        {
            Preset = options.Preset?.Trim() ?? profile.Preset,
            Tune = options.Tune?.Trim() ?? profile.Tune,
            Profile = options.Profile?.Trim() ?? profile.Profile,
            RateControl = string.IsNullOrWhiteSpace(options.RateControl) ? profile.RateControl : ParseRateControl(options.RateControl),
            Quality = options.Quality ?? profile.Quality,
            Bitrate = options.Bitrate ?? profile.Bitrate,
            OutputContainer = string.IsNullOrWhiteSpace(options.OutputContainer) ? profile.OutputContainer : options.OutputContainer.Trim().TrimStart('.'),
            AdditionalArguments = options.AdditionalArguments?.Trim() ?? profile.AdditionalArguments,
            UhdParameters = options.UhdParameters?.Trim() ?? profile.UhdParameters
        };
    }

    private static string Normalize(string value) =>
        new(value.Trim().ToLowerInvariant().Where(static ch => ch is not ('-' or '_' or ' ')).ToArray());
}

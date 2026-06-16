namespace FlowEncode.Domain;

public static class RequestValidation
{
    public const int MinConcurrentEncodingJobs = 1;
    public const int MaxConcurrentEncodingJobs = 5;

    public static void ValidateEncodingJobRequest(EncodingJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateEncodingProfile(request.Profile);
        RequirePath(request.SourcePath, "Encoding source path is required.");
        RequirePath(request.OutputPath, "Encoding output path is required.");

        if (request.UseAv1anParallelVideoEncoding)
        {
            ValidateParallelVideoEncodingRequest(CreateParallelVideoEncodingRequest(request));
        }
        else if (request.Av1anParallelWorkers is not null)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Av1anParallelWorkers), request.Av1anParallelWorkers, "Av1an worker count can only be specified when Av1an parallel video encoding is enabled.");
        }
    }

    public static void ValidateParallelVideoEncodingRequest(ParallelVideoEncodingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePath(request.SourcePath, "Parallel video encoding source path is required.");
        RequirePath(request.OutputPath, "Parallel video encoding output path is required.");

        if (request.EncoderKind is not (EncoderKind.X264 or EncoderKind.X265 or EncoderKind.SvtAv1))
        {
            throw new ArgumentOutOfRangeException(nameof(request.EncoderKind), request.EncoderKind, "Parallel video encoding supports only x264, x265, and SVT-AV1.");
        }

        if (!IsFinite(request.Crf) || request.Crf < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Crf), request.Crf, "Parallel video encoding CRF must be a finite non-negative value.");
        }

        if (request.Workers is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Workers), request.Workers, "Worker count must be greater than 0 when specified.");
        }

        if (ContainsLineBreak(request.VideoParameters))
        {
            throw new ArgumentException("Video parameters must be a single line.", nameof(request.VideoParameters));
        }

        if (ContainsLineBreak(request.UhdParameters))
        {
            throw new ArgumentException("UHD parameters must be a single line.", nameof(request.UhdParameters));
        }
    }

    public static void ValidateAutoCompressionRequest(AutoCompressionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePath(request.SourcePath, "Auto compression source path is required.");
        RequirePath(request.OutputPath, "Auto compression output path is required.");

        if (!IsFinite(request.TargetQuality) || request.TargetQuality <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TargetQuality), request.TargetQuality, "Target quality must be a finite positive value.");
        }

        if (request.Probes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Probes), request.Probes, "Probe count must be greater than 0.");
        }

        if (request.VideoParameters.Contains('\r') || request.VideoParameters.Contains('\n'))
        {
            throw new ArgumentException("Encoder arguments must be a single line.", nameof(request.VideoParameters));
        }

        if (request.BackendArguments.Contains('\r') || request.BackendArguments.Contains('\n'))
        {
            throw new ArgumentException("Backend arguments must be a single line.", nameof(request.BackendArguments));
        }

        if (request.Workers is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Workers), request.Workers, "Worker count must be greater than 0 when specified.");
        }

        if (request.SearchProfile is { ProbingRate: <= 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(request.SearchProfile), request.SearchProfile, "Probing rate must be greater than 0 when specified.");
        }
    }

    public static void ValidateAudioProcessingRequest(AudioProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePath(request.SourcePath, "Audio source path is required.");
        RequirePath(request.OutputPath, "Audio output path is required.");

        if (request.Mode == AudioProcessingMode.Opus && request.OpusBitrateKbps is not > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.OpusBitrateKbps), request.OpusBitrateKbps, "Opus bitrate must be greater than 0.");
        }
    }

    public static AppSettings NormalizeAppSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            MaxConcurrentEncodingJobs = NormalizeConcurrentEncodingJobs(settings.MaxConcurrentEncodingJobs)
        };
    }

    public static int NormalizeConcurrentEncodingJobs(double value)
    {
        if (!IsFinite(value))
        {
            return MinConcurrentEncodingJobs;
        }

        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded <= MinConcurrentEncodingJobs)
        {
            return MinConcurrentEncodingJobs;
        }

        if (rounded >= MaxConcurrentEncodingJobs)
        {
            return MaxConcurrentEncodingJobs;
        }

        return (int)rounded;
    }

    public static void ValidateEncodingProfile(EncodingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!IsFinite(profile.Quality) || profile.Quality < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profile.Quality), profile.Quality, "Profile quality must be a finite non-negative value.");
        }

        if (profile.Bitrate is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profile.Bitrate), profile.Bitrate, "Profile bitrate must be greater than 0 when specified.");
        }
    }

    public static ParallelVideoEncodingRequest CreateParallelVideoEncodingRequest(EncodingJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UseAv1anParallelVideoEncoding)
        {
            throw new InvalidOperationException("Av1an parallel video encoding is not enabled for this encoding request.");
        }

        if (request.Profile.RateControl != RateControlMode.Crf)
        {
            throw new InvalidOperationException("Av1an parallel video encoding supports only CRF mode.");
        }

        return new ParallelVideoEncodingRequest(
            request.JobId,
            request.SourcePath,
            request.OutputPath,
            request.Profile.Kind,
            request.Profile.Quality,
            request.Profile.Preset,
            request.Profile.Tune,
            request.Profile.Profile,
            request.Profile.AdditionalArguments,
            request.Profile.UhdParameters,
            request.Av1anParallelWorkers,
            request.PipelineKind,
            request.PreferredArchitecture);
    }

    private static void RequirePath(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(message);
        }
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool ContainsLineBreak(string? value)
    {
        return value?.Contains('\r') == true || value?.Contains('\n') == true;
    }
}

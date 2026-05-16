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
    }

    public static void ValidateAutoCompressionRequest(AutoCompressionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePath(request.SourcePath, "Auto compression source path is required.");
        RequirePath(request.OutputPath, "Auto compression output path is required.");

        if (!IsFinite(request.TargetVmaf) || request.TargetVmaf <= 0 || request.TargetVmaf > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TargetVmaf), request.TargetVmaf, "Target VMAF must be greater than 0 and no more than 100.");
        }

        if (request.Probes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Probes), request.Probes, "Probe count must be greater than 0.");
        }

        if (request.Workers is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Workers), request.Workers, "Worker count must be greater than 0 when specified.");
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
}

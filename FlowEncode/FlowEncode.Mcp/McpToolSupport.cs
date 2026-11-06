using FlowEncode.Domain;
using ModelContextProtocol;

namespace FlowEncode.Mcp;

/// <summary>
/// The MCP SDK only forwards the message of an <see cref="McpException"/> to the client; any other
/// exception becomes a generic error. FlowEncode's services report actionable problems (missing
/// encoder, unsupported input, ...) through ordinary exceptions, so surface their messages.
/// </summary>
internal static class McpToolGuard
{
    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsReportable(ex))
        {
            throw new McpException(ex.Message, ex);
        }
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (IsReportable(ex))
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private static bool IsReportable(Exception ex) =>
        ex is not McpException and not OperationCanceledException;
}

internal static class McpPaths
{
    public static string RequireAbsolute(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException($"'{parameterName}' is required.");
        }

        var trimmed = path.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new McpException($"'{parameterName}' must be an absolute path: {path}");
        }

        return Path.GetFullPath(trimmed);
    }

    public static string RequireExistingFile(string? path, string parameterName)
    {
        var fullPath = RequireAbsolute(path, parameterName);
        return File.Exists(fullPath)
            ? fullPath
            : throw new McpException($"File not found: {fullPath}");
    }

    public static string RequireExistingDirectory(string? path, string parameterName)
    {
        var fullPath = RequireAbsolute(path, parameterName);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new McpException($"Directory not found: {fullPath}");
    }
}

public sealed record ProfileView(
    string Encoder,
    string Name,
    string Description,
    string Preset,
    string Tune,
    string Profile,
    string RateControl,
    double Quality,
    int? Bitrate,
    string OutputContainer,
    string AdditionalArguments,
    string UhdParameters)
{
    public static ProfileView From(EncodingProfile profile) => new(
        EncodingProfileResolver.ToEncoderToken(profile.Kind),
        profile.Name,
        profile.Description,
        profile.Preset,
        profile.Tune,
        profile.Profile,
        EncodingProfileResolver.ToRateControlToken(profile.RateControl),
        profile.Quality,
        profile.Bitrate,
        profile.OutputContainer,
        profile.AdditionalArguments,
        profile.UhdParameters);
}

internal static class McpServerText
{
    public const string Instructions =
        "FlowEncode drives local x264 / x265 / SVT-AV1 / Av1an / FFmpeg / VapourSynth toolchains on Windows. "
        + "Typical flow: get_environment_status (or list_encoders) to confirm the encoder is installed; "
        + "list_templates or get_default_profile to pick parameters; preview_encoding_command to verify the exact command; "
        + "start_encoding_job, then poll get_encoding_job until the state is Completed, Failed or Cancelled. "
        + "All paths must be absolute Windows paths. Toolchain installation and updates are done in the FlowEncode desktop app.";
}

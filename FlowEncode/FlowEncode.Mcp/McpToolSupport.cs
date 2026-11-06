using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowEncode.Domain;
using ModelContextProtocol;

namespace FlowEncode.Mcp;

internal static class McpJson
{
    /// <summary>
    /// Tool results carry Chinese paths and messages (kept readable instead of escaped) and enums
    /// from the desktop contracts (written as names, not numbers).
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

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

    public static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
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

internal static class McpEnum
{
    /// <summary>
    /// Parses an enum by name, ignoring case, '-', '_' and spaces, so "auto_compression" or "Ddp" both work.
    /// </summary>
    public static T Parse<T>(string? value, string parameterName)
        where T : struct, Enum
    {
        if (TryParse<T>(value, out var parsed))
        {
            return parsed;
        }

        throw new McpException(
            $"Unknown {parameterName} '{value}'. Use one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    public static T? ParseOptional<T>(string? value, string parameterName)
        where T : struct, Enum
    {
        return string.IsNullOrWhiteSpace(value) ? null : Parse<T>(value, parameterName);
    }

    private static bool TryParse<T>(string? value, out T parsed)
        where T : struct, Enum
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = Normalize(value);
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(Normalize(candidate.ToString()), normalized, StringComparison.Ordinal))
            {
                parsed = candidate;
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value) =>
        new(value.Where(static character => char.IsLetterOrDigit(character)).Select(char.ToLowerInvariant).ToArray());
}

internal static class McpJobIds
{
    public static Guid Parse(string? value, string parameterName = "jobId")
    {
        return Guid.TryParse(value?.Trim(), out var id)
            ? id
            : throw new McpException($"'{value}' is not a valid {parameterName}.");
    }
}

internal static class McpServerText
{
    public const string DesktopNotRunning =
        "The FlowEncode desktop app is not running, so there is no task queue to inspect. "
        + "Starting a task launches it; call launch_desktop_app to open it explicitly.";

    public const string Instructions =
        "FlowEncode drives local x264 / x265 / SVT-AV1 / Av1an / FFmpeg / VapourSynth / eac3to / DGDemux toolchains on Windows. "
        + "Every task (video encodes, audio conversion, Blu-ray demux, target-quality auto compression), settings change, "
        + "dependency install and app update runs inside the FlowEncode desktop app, which this server starts automatically "
        + "when needed, so tasks show up in its queue and share its concurrency limit. "
        + "Typical video flow: get_environment_status -> list_templates or get_default_profile -> preview_encoding_command -> "
        + "start_encoding_job -> poll get_encoding_job (or list_encoding_jobs, which also shows jobs the user queued in the app). "
        + "Audio: probe_audio_source -> start_audio_task. Blu-ray: scan_bluray_disc with a playlistId -> start_bluray_demux with trackIds. "
        + "Target quality: start_auto_compression. Poll get_task_status for audio/bluray/autocompression. "
        + "VapourSynth: vapoursynth_* tools create, read, save, diagnose and render frames (PNG) of scripts. "
        + "Long dependency/app-update actions return an operationId; poll get_operation. All paths must be absolute Windows paths.";
}

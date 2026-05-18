using System;
using System.IO;
using System.Linq;
using FlowEncode.Domain;

namespace FlowEncode;

public enum DragDropPayloadType
{
    Unsupported,
    Script,
    Video,
    Audio,
    BluRayFolder
}

public sealed class AppLaunchActivation
{
    public string? RequestedVapourSynthFilePath { get; private set; }

    public bool HasRequestedVapourSynthFile
        => !string.IsNullOrWhiteSpace(RequestedVapourSynthFilePath);

    public void SetRequestedVapourSynthFilePath(string? filePath)
    {
        RequestedVapourSynthFilePath = NormalizeSupportedScriptPath(filePath);
    }

    public static string? NormalizeSupportedScriptPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        if (!IsSupportedScriptExtension(filePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsSupportedScriptExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".vpy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedVideoExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return InputSourceSupport.PreferredPickerExtensions
            .Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSupportedAudioExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return AudioSourceSupport.PreferredPickerExtensions
            .Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsBluRayFolder(string folderPath)
    {
        try
        {
            return Directory.Exists(Path.Combine(folderPath, "BDMV"));
        }
        catch
        {
            return false;
        }
    }

    public static DragDropPayloadType ClassifyFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return DragDropPayloadType.Unsupported;

        if (IsSupportedScriptExtension(filePath))
            return DragDropPayloadType.Script;

        if (IsSupportedVideoExtension(filePath))
            return DragDropPayloadType.Video;

        if (IsSupportedAudioExtension(filePath))
            return DragDropPayloadType.Audio;

        return DragDropPayloadType.Unsupported;
    }
}

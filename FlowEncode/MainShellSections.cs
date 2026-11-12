using System;

namespace FlowEncode;

internal static class MainShellSections
{
    public const string Dashboard = "dashboard";
    public const string BluRayDemux = "bluray-demux";
    public const string VapourSynthWorkspace = "vapoursynth-workspace";
    public const string Overview = "overview";
    public const string Templates = "templates";
    public const string AudioProcessing = "audio-process";
    public const string AutoCompression = "auto-compress";
    public const string Settings = "settings";

    public static string Normalize(string? tag)
    {
        return tag switch
        {
            Dashboard => Dashboard,
            BluRayDemux => BluRayDemux,
            VapourSynthWorkspace => VapourSynthWorkspace,
            Overview => Overview,
            Templates => Templates,
            AudioProcessing => AudioProcessing,
            AutoCompression => AutoCompression,
            Settings => Settings,
            _ => Dashboard
        };
    }

}

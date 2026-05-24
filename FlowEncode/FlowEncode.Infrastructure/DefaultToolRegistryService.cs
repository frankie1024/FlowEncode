using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class DefaultToolRegistryService : IToolRegistryService
{
    private const string ManagedAv1anReleasesUrl = "https://github.com/frankie1024/Av1an/releases";
    private static readonly IReadOnlyList<string> PythonExecutables = ["python.exe", "py.exe"];
    private static readonly IReadOnlyList<string> PythonEnvironmentVariables = ["FLOWENCODE_PYTHON", "PYTHON_PATH", "PYTHON_EXE", "PYTHON"];
    private const ToolSearchLocation PythonSearchLocations =
        ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.Path;

    private static readonly IReadOnlyList<ToolDefinition> Tools = Array.AsReadOnly(
    [
        new ToolDefinition(RegisteredToolKind.X264, ToolProbeMode.EncoderBinary, [], [], ToolSearchLocation.None, string.Empty, "https://github.com/frankie1024/x264-windows-builds/releases"),
        new ToolDefinition(RegisteredToolKind.X265, ToolProbeMode.EncoderBinary, [], [], ToolSearchLocation.None, string.Empty, "https://github.com/frankie1024/x265-windows-builds/releases"),
        new ToolDefinition(RegisteredToolKind.SvtAv1, ToolProbeMode.EncoderBinary, [], [], ToolSearchLocation.None, string.Empty, "https://github.com/frankie1024/svt-av1-windows-builds/releases"),
        new ToolDefinition(RegisteredToolKind.Ffmpeg, ToolProbeMode.ProcessVersion, ["ffmpeg.exe", "ffmpeg64.exe"], ["FLOWENCODE_FFMPEG", "FFMPEG_PATH", "FFMPEG_EXE", "FFMPEG"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path, "-version", "https://github.com/BtbN/FFmpeg-Builds/releases/latest", ExternalToolKind.Ffmpeg),
        new ToolDefinition(RegisteredToolKind.Ffprobe, ToolProbeMode.ProcessVersion, ["ffprobe.exe", "ffprobe64.exe"], ["FLOWENCODE_FFPROBE", "FFPROBE_PATH", "FFPROBE_EXE", "FFPROBE"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path, "-version", "https://github.com/BtbN/FFmpeg-Builds/releases/latest"),
        new ToolDefinition(RegisteredToolKind.Vspipe, ToolProbeMode.ProcessVersion, ["vspipe.exe", "VSPipe.exe"], ["FLOWENCODE_VSPIPE", "VSPIPE_PATH", "VSPIPE_EXE", "VSPIPE"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path | ToolSearchLocation.VspipeSidecar | ToolSearchLocation.ProgramFilesVapourSynth | ToolSearchLocation.PythonScripts, "--version", "https://www.vapoursynth.com/"),
        new ToolDefinition(RegisteredToolKind.Python, ToolProbeMode.ProcessVersion, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, "--version", "https://www.python.org/downloads/windows/"),
        new ToolDefinition(RegisteredToolKind.Vsrepo, ToolProbeMode.PythonModuleImport, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/vapoursynth/vsrepo", ProbeValue: "vsrepo.vsrepo"),
        new ToolDefinition(RegisteredToolKind.VsrepoPackageFfms2, ToolProbeMode.VsrepoInstalledPackage, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/vapoursynth/vsrepo", ProbeValue: "ffms2"),
        new ToolDefinition(RegisteredToolKind.VsrepoPackageFpng, ToolProbeMode.VsrepoInstalledPackage, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/vapoursynth/vsrepo", ProbeValue: "fpng"),
        new ToolDefinition(RegisteredToolKind.VsrepoPackageLibp2p, ToolProbeMode.VsrepoInstalledPackage, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/vapoursynth/vsrepo", ProbeValue: "libp2p"),
        new ToolDefinition(RegisteredToolKind.VsrepoPackageLsmas, ToolProbeMode.VsrepoInstalledPackage, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/vapoursynth/vsrepo", ProbeValue: "lsmas"),
        new ToolDefinition(RegisteredToolKind.VsrepoPackagePlacebo, ToolProbeMode.VsrepoInstalledPackage, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/vapoursynth/vsrepo", ProbeValue: "placebo"),
        new ToolDefinition(RegisteredToolKind.VsrepoPackageMvsfunc, ToolProbeMode.VsrepoInstalledPackage, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/vapoursynth/vsrepo", ProbeValue: "mvsfunc"),
        new ToolDefinition(RegisteredToolKind.VsrepoPackageHavsfunc, ToolProbeMode.VsrepoInstalledPackage, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/vapoursynth/vsrepo", ProbeValue: "havsfunc"),
        new ToolDefinition(RegisteredToolKind.PythonModuleAwsmfunc, ToolProbeMode.PythonModuleImport, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/OpusGang/awsmfunc", ProbeValue: "awsmfunc"),
        new ToolDefinition(RegisteredToolKind.PythonModuleVsjetpack, ToolProbeMode.PythonModuleImport, PythonExecutables, PythonEnvironmentVariables, PythonSearchLocations, string.Empty, "https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack", ProbeValue: "vsjetpack"),
        new ToolDefinition(RegisteredToolKind.Avs2PipeMod, ToolProbeMode.FileVersionInfo, ["avs2pipemod64.exe", "avs2pipemod.exe", "Avs2Pipemod.exe"], ["FLOWENCODE_AVS2PIPEMOD", "AVS2PIPEMOD_PATH", "AVS2PIPEMOD_EXE", "AVS2PIPEMOD"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path, string.Empty, "https://github.com/chikuzen/avs2pipemod"),
        new ToolDefinition(RegisteredToolKind.Av1an, ToolProbeMode.ProcessVersion, ["av1an.exe", "Av1an.exe"], ["FLOWENCODE_AV1AN", "AV1AN_PATH", "AV1AN_EXE", "AV1AN"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path, "--version", ManagedAv1anReleasesUrl, ExternalToolKind.Av1an),
        new ToolDefinition(RegisteredToolKind.DgDemux, ToolProbeMode.FileVersionInfo, ["DGDemux.exe", "dgdemux.exe"], ["FLOWENCODE_DGDEMUX", "DGDEMUX_PATH", "DGDEMUX_EXE", "DGDEMUX"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path, string.Empty, "https://www.rationalqm.us/dgdemux/binaries/"),
        new ToolDefinition(RegisteredToolKind.Eac3To, ToolProbeMode.FileVersionInfo, ["eac3to.exe", "Eac3to.exe"], ["FLOWENCODE_EAC3TO", "EAC3TO_PATH", "EAC3TO_EXE", "EAC3TO"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path, string.Empty, "https://www.rationalqm.us/eac3to/"),
        new ToolDefinition(RegisteredToolKind.Deew, ToolProbeMode.ProcessVersion, ["deew.exe", "Deew.exe"], ["FLOWENCODE_DEEW", "DEEW_PATH", "DEEW_EXE", "DEEW"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path, "--version", "https://github.com/pcroland/deew"),
        new ToolDefinition(RegisteredToolKind.Dee, ToolProbeMode.FileVersionInfo, ["dee.exe", "DEE.exe", "DolbyEncodingEngine.exe"], ["FLOWENCODE_DEE", "DEE_PATH", "DEE_EXE", "DEE"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path, string.Empty, "https://github.com/pcroland/deew"),
        new ToolDefinition(RegisteredToolKind.OpusExt, ToolProbeMode.ProcessVersion, ["opusenc.exe", "OpusEnc.exe", "opusext.exe", "OpusExt.exe"], ["FLOWENCODE_OPUSEXT", "OPUSEXT_PATH", "OPUSEXT_EXE", "OPUSEXT", "FLOWENCODE_OPUSENC", "OPUSENC_PATH", "OPUSENC_EXE", "OPUSENC"], ToolSearchLocation.EnvironmentVariables | ToolSearchLocation.LocalToolsRoot | ToolSearchLocation.Path, "--version", "https://www.opus-codec.org/downloads/")
    ]);

    private static readonly IReadOnlyList<CapabilityDefinition> Capabilities = Array.AsReadOnly(
    [
        new CapabilityDefinition(EnvironmentCapabilityKind.VideoBasic, [new CapabilityToolRequirement(RegisteredToolKind.X264, RegisteredToolKind.X265, RegisteredToolKind.SvtAv1), new CapabilityToolRequirement(RegisteredToolKind.Ffmpeg), new CapabilityToolRequirement(RegisteredToolKind.Ffprobe)]),
        new CapabilityDefinition(EnvironmentCapabilityKind.VapourSynthInput, [new CapabilityToolRequirement(RegisteredToolKind.Vspipe)]),
        new CapabilityDefinition(EnvironmentCapabilityKind.VapourSynthPluginStack, [new CapabilityToolRequirement(RegisteredToolKind.Vsrepo), new CapabilityToolRequirement(RegisteredToolKind.VsrepoPackageFfms2), new CapabilityToolRequirement(RegisteredToolKind.VsrepoPackageFpng), new CapabilityToolRequirement(RegisteredToolKind.VsrepoPackageLibp2p), new CapabilityToolRequirement(RegisteredToolKind.VsrepoPackageLsmas), new CapabilityToolRequirement(RegisteredToolKind.VsrepoPackagePlacebo), new CapabilityToolRequirement(RegisteredToolKind.VsrepoPackageMvsfunc), new CapabilityToolRequirement(RegisteredToolKind.VsrepoPackageHavsfunc), new CapabilityToolRequirement(RegisteredToolKind.PythonModuleAwsmfunc), new CapabilityToolRequirement(RegisteredToolKind.PythonModuleVsjetpack)]),
        new CapabilityDefinition(EnvironmentCapabilityKind.AviSynthInput, [new CapabilityToolRequirement(RegisteredToolKind.Avs2PipeMod)]),
        new CapabilityDefinition(EnvironmentCapabilityKind.AutoEncode, [new CapabilityToolRequirement(RegisteredToolKind.Av1an), new CapabilityToolRequirement(RegisteredToolKind.Ffmpeg), new CapabilityToolRequirement(RegisteredToolKind.Ffprobe), new CapabilityToolRequirement(RegisteredToolKind.X264, RegisteredToolKind.X265, RegisteredToolKind.SvtAv1)]),
        new CapabilityDefinition(EnvironmentCapabilityKind.AudioFlac, [new CapabilityToolRequirement(RegisteredToolKind.Eac3To)]),
        new CapabilityDefinition(EnvironmentCapabilityKind.AudioDdp, [new CapabilityToolRequirement(RegisteredToolKind.Deew), new CapabilityToolRequirement(RegisteredToolKind.Dee), new CapabilityToolRequirement(RegisteredToolKind.Ffmpeg), new CapabilityToolRequirement(RegisteredToolKind.Ffprobe)]),
        new CapabilityDefinition(EnvironmentCapabilityKind.AudioOpus, [new CapabilityToolRequirement(RegisteredToolKind.Ffmpeg), new CapabilityToolRequirement(RegisteredToolKind.OpusExt)])
    ]);

    private static readonly IReadOnlyDictionary<RegisteredToolKind, ToolDefinition> ToolMap = Tools.ToDictionary(static tool => tool.Kind, static tool => tool);

    public IReadOnlyList<ToolDefinition> GetTools() => Tools;

    public ToolDefinition GetTool(RegisteredToolKind kind)
    {
        return ToolMap.TryGetValue(kind, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Tool definition not found: {kind}");
    }

    public IReadOnlyList<CapabilityDefinition> GetCapabilities() => Capabilities;
}

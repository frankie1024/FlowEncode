namespace FlowEncode.Domain;

public static class RegisteredToolKindExtensions
{
    public static string ToDisplayName(this RegisteredToolKind kind)
    {
        return kind switch
        {
            RegisteredToolKind.X264 => "x264",
            RegisteredToolKind.X265 => "x265",
            RegisteredToolKind.SvtAv1 => "SVT-AV1",
            RegisteredToolKind.Ffmpeg => "FFmpeg",
            RegisteredToolKind.Ffprobe => "FFprobe",
            RegisteredToolKind.Vspipe => "VSPipe",
            RegisteredToolKind.Python => "Python",
            RegisteredToolKind.Pip => "pip",
            RegisteredToolKind.Vsrepo => "vsrepo",
            RegisteredToolKind.VsrepoCli => "vsrepo CLI",
            RegisteredToolKind.VsrepoPackageFfms2 => "ffms2",
            RegisteredToolKind.VsrepoPackageFpng => "fpng",
            RegisteredToolKind.VsrepoPackageLibp2p => "libp2p",
            RegisteredToolKind.VsrepoPackageLsmas => "lsmas",
            RegisteredToolKind.VsrepoPackagePlacebo => "placebo",
            RegisteredToolKind.VsrepoPackageMvsfunc => "mvsfunc",
            RegisteredToolKind.VsrepoPackageHavsfunc => "havsfunc",
            RegisteredToolKind.PythonModuleAwsmfunc => "awsmfunc",
            RegisteredToolKind.PythonModuleVsjetpack => "vsjetpack",
            RegisteredToolKind.Avs2PipeMod => "Avs2Pipemod",
            RegisteredToolKind.Av1an => "Av1an",
            RegisteredToolKind.DgDemux => "DGDemux",
            RegisteredToolKind.Eac3To => "eac3to",
            RegisteredToolKind.Deew => "deew",
            RegisteredToolKind.Dee => "DEE",
            RegisteredToolKind.OpusExt => "Opus Encoder",
            _ => kind.ToString()
        };
    }
}

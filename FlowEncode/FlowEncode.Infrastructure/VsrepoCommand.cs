using System.Text;

namespace FlowEncode.Infrastructure;

internal static class VsrepoCommand
{
    internal const string LauncherProbeArgument = "--help";
    internal const string BaselineResourceName = "FlowEncode.Infrastructure.Resources.VsrepoBaseline.json";
    internal static string Target => Environment.Is64BitOperatingSystem ? "win64" : "win32";

    private static readonly object BaselineWriteGate = new();

    internal static string PrepareBaseline(LocalAppPaths paths)
    {
        var root = Path.Combine(paths.DownloadsRootPath, "vsrepo-baseline-2026-11-12");
        using var stream = typeof(VsrepoCommand).Assembly.GetManifestResourceStream(BaselineResourceName)
            ?? throw new InvalidOperationException("The pinned VS plugin manifest is missing.");
        using var reader = new StreamReader(stream);
        var manifest = reader.ReadToEnd();
        var encoding = new UTF8Encoding(false);
        var manifestPath = Path.Combine(root, "vsrepo", "vspackages3.json");

        lock (BaselineWriteGate)
        {
            if (!File.Exists(manifestPath) || !File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(encoding.GetBytes(manifest)))
            {
                PersistentFileWriter.WriteAllText(manifestPath, manifest, encoding);
            }
        }

        return root;
    }

    internal static IReadOnlyList<string> CreateArguments(string operation, IReadOnlyList<string> packages, string? baselineRoot = null)
    {
        if (baselineRoot is null)
        {
            return ["-m", "vsrepo.vsrepo", "-t", Target, operation, .. packages];
        }

        // Select the private manifest after Python and VapourSynth resolve their user directories.
        const string runner = "import os,runpy,sys,vapoursynth; os.environ['APPDATA']=sys.argv[1]; sys.argv=['vsrepo']+sys.argv[2:]; runpy.run_module('vsrepo.vsrepo',run_name='__main__')";
        return operation == "upgrade"
            ? ["-c", runner, baselineRoot, "-t", Target, "-f", "upgrade-all"]
            : ["-c", runner, baselineRoot, "-t", Target, operation, .. packages];
    }
}

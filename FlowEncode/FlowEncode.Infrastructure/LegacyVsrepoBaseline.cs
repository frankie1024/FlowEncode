namespace FlowEncode.Infrastructure;

internal static class LegacyVsrepoBaseline
{
    private static string Root(LocalAppPaths paths) => Path.Combine(paths.DownloadsRootPath, "legacy-vsrepo-baseline");

    internal static void Prepare(LocalAppPaths paths)
    {
        using var stream = typeof(LegacyVsrepoBaseline).Assembly.GetManifestResourceStream("FlowEncode.Infrastructure.Resources.VsrepoBaseline.json")
            ?? throw new InvalidOperationException("The pinned VS plugin manifest is missing.");
        using var reader = new StreamReader(stream);
        PersistentFileWriter.WriteAllText(Path.Combine(Root(paths), "vsrepo", "vspackages3.json"), reader.ReadToEnd(), new System.Text.UTF8Encoding(false));
    }

    internal static IReadOnlyList<string> Arguments(LocalAppPaths paths, string target, string operation, IReadOnlyList<string> packages)
    {
        Prepare(paths);
        // Change APPDATA after Python/site initialization, only in the child. Preserve user definitions and install paths.
        const string runner = "import os,runpy,sys; os.environ['APPDATA']=sys.argv[1]; sys.argv=['vsrepo']+sys.argv[2:]; runpy.run_module('vsrepo.vsrepo',run_name='__main__')";
        return operation == "upgrade"
            ? ["-c", runner, Root(paths), "-t", target, "-f", "upgrade-all"]
            : ["-c", runner, Root(paths), "-t", target, operation, .. packages];
    }
}

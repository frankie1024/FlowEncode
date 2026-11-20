using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal sealed record ManualToolPackage(
    SetupDependencyKind DependencyKind,
    RegisteredToolKind ToolKind,
    IReadOnlyList<string> ExecutableNames)
{
    private static readonly ManualToolPackage[] Packages =
    [
        new(SetupDependencyKind.Avs2PipeMod, RegisteredToolKind.Avs2PipeMod, ["avs2pipemod64.exe", "avs2pipemod.exe", "Avs2Pipemod.exe"]),
        new(SetupDependencyKind.DgDemux, RegisteredToolKind.DgDemux, ["DGDemux.exe", "dgdemux.exe"]),
        new(SetupDependencyKind.Eac3To, RegisteredToolKind.Eac3To, ["eac3to.exe", "Eac3to.exe"]),
        new(SetupDependencyKind.Deew, RegisteredToolKind.Deew, ["deew.exe", "Deew.exe"]),
        new(SetupDependencyKind.Dee, RegisteredToolKind.Dee, ["dee.exe", "DEE.exe", "DolbyEncodingEngine.exe"]),
        new(SetupDependencyKind.OpusExt, RegisteredToolKind.OpusExt, ["opusenc.exe", "OpusEnc.exe", "opusext.exe", "OpusExt.exe"])
    ];

    public static ManualToolPackage? Find(SetupDependencyKind kind) => Packages.FirstOrDefault(package => package.DependencyKind == kind);

    public static ManualToolPackage? Find(RegisteredToolKind kind) => Packages.FirstOrDefault(package => package.ToolKind == kind);

    public string GetDirectory(string toolsRoot) => Path.Combine(toolsRoot, "imported", ToolKind.ToString().ToLowerInvariant());

    public IEnumerable<string> GetLegacyPaths(string toolsRoot) => ExecutableNames.Select(name => Path.Combine(toolsRoot, name));

    public string? FindExecutable(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var executable = ExecutableNames.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
        if (executable is not null)
        {
            return executable;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*.exe", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive
            })
            .Where(path => ExecutableNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void Import(string sourcePath, string toolsRoot)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        var target = Path.GetFullPath(GetDirectory(toolsRoot));
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            EnsureExecutableExists(source);
            return;
        }

        if (target.StartsWith(Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Select a tool package outside the managed installation folder.");
        }

        if (Directory.Exists(source))
        {
            EnsureExecutableExists(source);
            ManagedDirectoryInstaller.ReplaceDirectoryContents(source, target, EnsureExecutableExists, skipReparsePoints: true);
        }
        else
        {
            if (!File.Exists(source) || !string.Equals(Path.GetExtension(source), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException("The selected executable was not found.", source);
            }

            ManagedDirectoryInstaller.ReplaceDirectory(target,
                staging => File.Copy(source, Path.Combine(staging, ExecutableNames[0])));
        }
    }

    private void EnsureExecutableExists(string directory)
    {
        if (FindExecutable(directory) is null)
        {
            throw new FileNotFoundException($"The selected folder must contain one of these executables: {string.Join(", ", ExecutableNames)}.");
        }
    }
}

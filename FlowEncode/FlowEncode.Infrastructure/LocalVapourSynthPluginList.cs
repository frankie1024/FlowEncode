using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

internal static class LocalVapourSynthPluginList
{
    private static readonly object Gate = new();

    private static string GetPath(LocalAppPaths paths) => Path.Combine(paths.SettingsRootPath, "vapoursynth-plugins.txt");

    internal static IReadOnlyList<string> Read(LocalAppPaths paths)
        => [.. VapourSynthPluginNames.DefaultPlugins, .. ReadCustom(paths)];

    private static IReadOnlyList<string> ReadCustom(LocalAppPaths paths)
    {
        var path = GetPath(paths);
        return File.Exists(path)
            ? File.ReadAllLines(path).Select(VapourSynthPluginNames.Normalize).OfType<string>()
                .Where(static name => !VapourSynthPluginNames.IsManagedSeparately(name))
                .Except(VapourSynthPluginNames.DefaultPlugins).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    internal static void Track(LocalAppPaths paths, string name)
    {
        if (VapourSynthPluginNames.DefaultPlugins.Contains(name))
            return;

        lock (Gate)
        {
            var names = ReadCustom(paths);
            if (!names.Contains(name))
            {
                PersistentFileWriter.WriteAllText(GetPath(paths),
                    string.Join(Environment.NewLine, names.Append(name).Order(StringComparer.Ordinal)),
                    System.Text.Encoding.UTF8);
            }
        }
    }
}

namespace FlowEncode.Infrastructure;

internal static class ExecutablePathResolver
{
    public static string? ResolveFromInput(
        string? value,
        IReadOnlyList<string> executableNames,
        IEnumerable<string>? pathRoots = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Trim('"');
        if (File.Exists(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        if (Directory.Exists(normalized))
        {
            foreach (var fileName in executableNames)
            {
                var candidate = Path.Combine(normalized, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        if (!normalized.Contains(Path.DirectorySeparatorChar)
            && !normalized.Contains(Path.AltDirectorySeparatorChar)
            && pathRoots is not null)
        {
            foreach (var root in pathRoots)
            {
                var candidate = Path.Combine(root, normalized);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }
}

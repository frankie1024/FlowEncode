using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class LocalEncoderDiscoveryService : IEncoderDiscoveryService
{
    private static readonly IReadOnlyDictionary<EncoderKind, string[]> EnvironmentVariableNames = new Dictionary<EncoderKind, string[]>
    {
        [EncoderKind.X264] = ["FLOWENCODE_X264", "X264_PATH", "X264_EXE", "X264"],
        [EncoderKind.X265] = ["FLOWENCODE_X265", "X265_PATH", "X265_EXE", "X265"],
        [EncoderKind.SvtAv1] = ["FLOWENCODE_AV1", "SVT_AV1_PATH", "SVTAV1_PATH", "SVTAV1", "AV1_ENCODER", "AV1"]
    };

    private static readonly IReadOnlyDictionary<EncoderKind, string[]> ExecutableNames = new Dictionary<EncoderKind, string[]>
    {
        [EncoderKind.X264] = ["x264.exe"],
        [EncoderKind.X265] = ["x265.exe"],
        [EncoderKind.SvtAv1] = ["SvtAv1EncApp.exe", "svt-av1.exe"]
    };

    private readonly LocalAppPaths _paths;
    private readonly IAppSettingsService _settingsService;
    private readonly object _systemDiscoveryCacheGate = new();
    private SystemDiscoveryCacheSnapshot? _systemDiscoveryCache;

    public LocalEncoderDiscoveryService(LocalAppPaths paths, IAppSettingsService settingsService)
    {
        _paths = paths;
        _settingsService = settingsService;
    }

    public IReadOnlyList<DiscoveredEncoderBinary> DiscoverSystemBinaries()
    {
        var candidates = DiscoverSystemCandidateDescriptors();
        var signature = BuildSystemDiscoverySignature(candidates);

        lock (_systemDiscoveryCacheGate)
        {
            if (_systemDiscoveryCache is { } cached
                && string.Equals(cached.Signature, signature, StringComparison.Ordinal))
            {
                return cached.Results;
            }
        }

        var results = candidates
            .Select(static candidate => CreateCandidate(
                candidate.Kind,
                candidate.ExecutablePath,
                candidate.Source,
                candidate.SourceLabel))
            .ToArray();

        lock (_systemDiscoveryCacheGate)
        {
            if (_systemDiscoveryCache is { } cached
                && string.Equals(cached.Signature, signature, StringComparison.Ordinal))
            {
                return cached.Results;
            }

            _systemDiscoveryCache = new SystemDiscoveryCacheSnapshot(signature, results);
        }

        return results;
    }

    public void InvalidateCache()
    {
        lock (_systemDiscoveryCacheGate)
        {
            _systemDiscoveryCache = null;
        }
    }

    private IReadOnlyList<EncoderCandidateDescriptor> DiscoverSystemCandidateDescriptors()
    {
        var results = new List<EncoderCandidateDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in Enum.GetValues<EncoderKind>())
        {
            foreach (var resolvedPath in EnumerateManualMatches(kind))
            {
                if (!seen.Add($"{kind}:{resolvedPath}"))
                {
                    continue;
                }

                results.Add(CreateCandidateDescriptor(kind, resolvedPath, EncoderBinarySource.ManualSelection, "manual"));
            }

            foreach (var variableName in EnvironmentVariableNames[kind])
            {
                var value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process)
                    ?? Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine);

                var resolvedPath = ExecutablePathResolver.ResolveFromInput(value, ExecutableNames[kind], EnumerateCurrentPathRoots());
                if (string.IsNullOrWhiteSpace(resolvedPath) || !seen.Add($"{kind}:{resolvedPath}"))
                {
                    continue;
                }

                results.Add(CreateCandidateDescriptor(kind, resolvedPath, EncoderBinarySource.EnvironmentVariable, variableName));
            }

            foreach (var resolvedPath in EnumeratePathMatches(kind))
            {
                if (!seen.Add($"{kind}:{resolvedPath}"))
                {
                    continue;
                }

                results.Add(CreateCandidateDescriptor(kind, resolvedPath, EncoderBinarySource.Path, "PATH"));
            }
        }

        return results
            .OrderBy(static item => item.Kind)
            .ThenBy(static item => item.Source)
            .ThenBy(static item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DiscoveredEncoderBinary? ResolveEncoder(
        EncoderKind kind,
        EncoderArchitecture preferredArchitecture,
        bool preferSystemEncoders)
    {
        var manualCandidate = PickBest(GetManualCandidates(kind), preferredArchitecture);
        if (manualCandidate is not null)
        {
            return manualCandidate;
        }

        var localCandidate = PickBest(GetLocalCandidates(kind), preferredArchitecture);
        if (localCandidate is not null)
        {
            return localCandidate;
        }

        if (!preferSystemEncoders)
        {
            return null;
        }

        return PickBest(DiscoverSystemBinaries().Where(candidate => candidate.Kind == kind), preferredArchitecture);
    }

    private IEnumerable<DiscoveredEncoderBinary> GetLocalCandidates(EncoderKind kind)
    {
        foreach (var architecture in Enum.GetValues<EncoderArchitecture>())
        {
            var path = _paths.GetBinaryPath(kind, architecture);
            if (!File.Exists(path))
            {
                continue;
            }

                yield return CreateCandidate(kind, path, EncoderBinarySource.LocalToolset, "encoders");
        }
    }

    private IEnumerable<DiscoveredEncoderBinary> GetManualCandidates(EncoderKind kind)
    {
        foreach (var path in EnumerateManualMatches(kind))
        {
            yield return CreateCandidate(kind, path, EncoderBinarySource.ManualSelection, "manual");
        }
    }

    private static DiscoveredEncoderBinary? PickBest(IEnumerable<DiscoveredEncoderBinary> candidates, EncoderArchitecture preferredArchitecture)
    {
        return candidates
            .OrderByDescending(candidate => candidate.Architecture == preferredArchitecture)
            .ThenBy(candidate => candidate.Source)
            .FirstOrDefault();
    }

    private static DiscoveredEncoderBinary CreateCandidate(EncoderKind kind, string executablePath, EncoderBinarySource source, string sourceLabel)
    {
        return new DiscoveredEncoderBinary(
            kind,
            EncoderBinaryProbe.DetectArchitecture(executablePath),
            executablePath,
            source,
            sourceLabel,
            EncoderBinaryProbe.ProbeVersion(executablePath, kind));
    }

    private static EncoderCandidateDescriptor CreateCandidateDescriptor(
        EncoderKind kind,
        string executablePath,
        EncoderBinarySource source,
        string sourceLabel)
    {
        var normalizedPath = Path.GetFullPath(executablePath);
        var fileInfo = new FileInfo(normalizedPath);
        return new EncoderCandidateDescriptor(
            kind,
            normalizedPath,
            source,
            sourceLabel,
            fileInfo.Exists ? fileInfo.Length : -1,
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : -1);
    }

    private string BuildSystemDiscoverySignature(IReadOnlyList<EncoderCandidateDescriptor> candidates)
    {
        var builder = new System.Text.StringBuilder();
        var settings = _settingsService.Load();

        foreach (var kind in Enum.GetValues<EncoderKind>())
        {
            var manualKey = ManualToolPathKeys.ForEncoder(kind);
            settings.EffectiveManualToolPaths.TryGetValue(manualKey, out var manualValue);
            builder.Append("manual:")
                .Append(manualKey)
                .Append('=')
                .Append(manualValue)
                .AppendLine();

            foreach (var variableName in EnvironmentVariableNames[kind])
            {
                AppendEnvironmentValue(builder, variableName, EnvironmentVariableTarget.Process);
                AppendEnvironmentValue(builder, variableName, EnvironmentVariableTarget.User);
                AppendEnvironmentValue(builder, variableName, EnvironmentVariableTarget.Machine);
            }
        }

        builder.Append("path=")
            .Append(Environment.GetEnvironmentVariable("PATH"))
            .AppendLine();

        foreach (var candidate in candidates)
        {
            builder.Append("candidate:")
                .Append(candidate.Kind)
                .Append('|')
                .Append(candidate.Source)
                .Append('|')
                .Append(candidate.SourceLabel)
                .Append('|')
                .Append(candidate.ExecutablePath)
                .Append('|')
                .Append(candidate.Length)
                .Append('|')
                .Append(candidate.LastWriteTimeUtcTicks)
                .AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendEnvironmentValue(
        System.Text.StringBuilder builder,
        string variableName,
        EnvironmentVariableTarget target)
    {
        builder.Append("env:")
            .Append(target)
            .Append(':')
            .Append(variableName)
            .Append('=')
            .Append(Environment.GetEnvironmentVariable(variableName, target))
            .AppendLine();
    }

    private static IEnumerable<string> EnumeratePathMatches(EncoderKind kind)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var root in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var fileName in ExecutableNames[kind])
            {
                var candidate = Path.Combine(root, fileName);
                if (File.Exists(candidate))
                {
                    yield return Path.GetFullPath(candidate);
                }
            }
        }
    }

    private IEnumerable<string> EnumerateManualMatches(EncoderKind kind)
    {
        var settings = _settingsService.Load();
        if (!settings.EffectiveManualToolPaths.TryGetValue(ManualToolPathKeys.ForEncoder(kind), out var value))
        {
            yield break;
        }

        var resolvedPath = ExecutablePathResolver.ResolveFromInput(value, ExecutableNames[kind], EnumerateCurrentPathRoots());
        if (!string.IsNullOrWhiteSpace(resolvedPath))
        {
            yield return resolvedPath;
        }
    }

    private static IEnumerable<string> EnumerateCurrentPathRoots()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            yield break;
        }

        foreach (var root in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return root;
        }
    }

    private sealed record SystemDiscoveryCacheSnapshot(
        string Signature,
        IReadOnlyList<DiscoveredEncoderBinary> Results);

    private sealed record EncoderCandidateDescriptor(
        EncoderKind Kind,
        string ExecutablePath,
        EncoderBinarySource Source,
        string SourceLabel,
        long Length,
        long LastWriteTimeUtcTicks);
}

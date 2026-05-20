using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class ProcessToolProbeService : IToolProbeService
{
    private const int ProbeTimeoutMilliseconds = 5000;
    private const int VsrepoInstalledProbeTimeoutMilliseconds = 30000;

    private readonly IToolRegistryService _toolRegistryService;
    private readonly LocalAppPaths _paths;
    private readonly IEncoderDiscoveryService _encoderDiscoveryService;
    private readonly IAppSettingsService _settingsService;
    private readonly ConcurrentDictionary<ToolProbeCacheKey, ToolProbeResult> _probeCache = new();

    public ProcessToolProbeService(
        IToolRegistryService toolRegistryService,
        LocalAppPaths paths,
        IEncoderDiscoveryService encoderDiscoveryService,
        IAppSettingsService settingsService)
    {
        _toolRegistryService = toolRegistryService;
        _paths = paths;
        _encoderDiscoveryService = encoderDiscoveryService;
        _settingsService = settingsService;
    }

    public Task<IReadOnlyList<ToolProbeResult>> ProbeAllAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ToolProbeResult>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vsrepoInstalledCache = new Dictionary<string, VsrepoInstalledProbeResult>(StringComparer.OrdinalIgnoreCase);

            return _toolRegistryService
                .GetTools()
                .Select(definition => ProbeCached(definition, cancellationToken, vsrepoInstalledCache))
                .ToList();
        }, cancellationToken);
    }

    public Task<ToolProbeResult> ProbeAsync(RegisteredToolKind kind, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var definition = _toolRegistryService.GetTool(kind);
            var vsrepoInstalledCache = new Dictionary<string, VsrepoInstalledProbeResult>(StringComparer.OrdinalIgnoreCase);
            return ProbeCached(definition, cancellationToken, vsrepoInstalledCache);
        }, cancellationToken);
    }

    public void InvalidateCache()
    {
        _probeCache.Clear();
    }

    private ToolProbeResult ProbeCached(
        ToolDefinition definition,
        CancellationToken cancellationToken,
        IDictionary<string, VsrepoInstalledProbeResult> vsrepoInstalledCache)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _settingsService.Load();
        var manualToolPaths = settings.EffectiveManualToolPaths;
        var key = new ToolProbeCacheKey(definition.Kind, BuildProbeSignature(definition, manualToolPaths));
        if (_probeCache.TryGetValue(key, out var cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        var result = Probe(definition, settings, cancellationToken, vsrepoInstalledCache, manualToolPaths);

        cancellationToken.ThrowIfCancellationRequested();
        _probeCache.TryAdd(key, result);
        return result;
    }

    private ToolProbeResult Probe(
        ToolDefinition definition,
        AppSettings settings,
        CancellationToken cancellationToken,
        IDictionary<string, VsrepoInstalledProbeResult> vsrepoInstalledCache,
        IReadOnlyDictionary<string, string> manualToolPaths)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return definition.ProbeMode switch
        {
            ToolProbeMode.EncoderBinary => ProbeEncoder(definition, settings),
            ToolProbeMode.ProcessVersion => ProbeCandidateTools(definition, cancellationToken, manualToolPaths, ProbeWithProcessVersion),
            ToolProbeMode.FileVersionInfo => ProbeCandidateTools(definition, cancellationToken, manualToolPaths, ProbeWithFileVersion),
            ToolProbeMode.ExistenceOnly => ProbeCandidateTools(definition, cancellationToken, manualToolPaths, ProbeWithExistenceOnly),
            ToolProbeMode.VsrepoInstalledPackage => ProbeCandidateTools(definition, cancellationToken, manualToolPaths, (tool, candidate) =>
                ProbeWithVsrepoInstalledPackage(tool, candidate, vsrepoInstalledCache)),
            ToolProbeMode.PythonModuleImport => ProbeCandidateTools(definition, cancellationToken, manualToolPaths, ProbeWithPythonModuleImport),
            _ => CreateUnknownResult(definition, "Unsupported probe mode.")
        };
    }

    private ToolProbeResult ProbeEncoder(ToolDefinition definition, AppSettings settings)
    {
        var encoderKind = definition.Kind switch
        {
            RegisteredToolKind.X264 => EncoderKind.X264,
            RegisteredToolKind.X265 => EncoderKind.X265,
            RegisteredToolKind.SvtAv1 => EncoderKind.SvtAv1,
            _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Kind, null)
        };

        var resolved = _encoderDiscoveryService.ResolveEncoder(
            encoderKind,
            EncoderArchitecture.X64,
            settings.PreferSystemEncoders);

        if (resolved is null)
        {
            return CreateMissingResult(definition);
        }

        return new ToolProbeResult(
            definition.Kind,
            ReadinessState.Ready,
            resolved.Source switch
            {
                EncoderBinarySource.LocalToolset => ToolDetectionSource.LocalToolset,
                EncoderBinarySource.ManualSelection => ToolDetectionSource.ManualSelection,
                EncoderBinarySource.EnvironmentVariable => ToolDetectionSource.EnvironmentVariable,
                EncoderBinarySource.Path => ToolDetectionSource.SystemEncoder,
                _ => ToolDetectionSource.None
            },
            resolved.SourceLabel,
            resolved.ExecutablePath,
            resolved.DetectedVersion,
            string.Empty,
            definition.ReleaseUrl);
    }

    private ToolProbeResult ProbeCandidateTools(
        ToolDefinition definition,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> manualToolPaths,
        Func<ToolDefinition, ToolCandidate, ToolProbeResult> probeAction)
    {
        ToolProbeResult? fallbackFailure = null;

        foreach (var candidate in EnumerateCandidates(definition, manualToolPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = probeAction(definition, candidate);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.State == ReadinessState.Ready)
            {
                return result;
            }

            fallbackFailure ??= result;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return fallbackFailure ?? CreateMissingResult(definition);
    }

    private ToolProbeResult ProbeWithProcessVersion(ToolDefinition definition, ToolCandidate candidate)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = candidate.Path,
                Arguments = definition.VersionArguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(candidate.Path) ?? AppContext.BaseDirectory
            };

            VapourSynthRuntimePathResolver.EnrichProcessPath(startInfo);

            using var _ = ErrorDialogSuppression.Enter();
            var result = ProcessProbeRunner.Run(
                startInfo,
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                "Version probe timed out.");

            var output = string.Concat(
                result.StandardOutput,
                Environment.NewLine,
                result.StandardError).Trim();

            if (result.ExitCode != 0)
            {
                var failureReason = FirstMeaningfulLine(output);
                return CreateMisconfiguredResult(
                    definition,
                    candidate,
                    string.IsNullOrWhiteSpace(failureReason)
                        ? $"Version probe failed with exit code {result.ExitCode}."
                        : failureReason);
            }

            var ready = CreateReadyResult(definition, candidate, ResolveVersionText(definition, candidate.Path, output));
            return definition.Kind == RegisteredToolKind.Av1an
                ? EnrichAv1anBackendCompatibility(ready, candidate.Path)
                : ready;
        }
        catch (Exception ex)
        {
            return CreateMisconfiguredResult(definition, candidate, ex.Message);
        }
    }

    private ToolProbeResult ProbeWithFileVersion(ToolDefinition definition, ToolCandidate candidate)
    {
        try
        {
            var version = ResolveVersionText(definition, candidate.Path, string.Empty);
            return CreateReadyResult(definition, candidate, version);
        }
        catch (Exception ex)
        {
            return CreateMisconfiguredResult(definition, candidate, ex.Message);
        }
    }

    private ToolProbeResult ProbeWithExistenceOnly(ToolDefinition definition, ToolCandidate candidate)
    {
        return CreateReadyResult(definition, candidate, "Present");
    }

    private ToolProbeResult ProbeWithVsrepoInstalledPackage(
        ToolDefinition definition,
        ToolCandidate candidate,
        IDictionary<string, VsrepoInstalledProbeResult> vsrepoInstalledCache)
    {
        if (string.IsNullOrWhiteSpace(definition.ProbeValue))
        {
            return CreateUnknownResult(definition, "Missing VSRepo package probe value.");
        }

        try
        {
            var snapshot = GetVsrepoInstalledProbeResult(candidate, vsrepoInstalledCache);
            if (snapshot.IsTimedOut)
            {
                return CreateMisconfiguredResult(definition, candidate, snapshot.FailureReason);
            }

            if (snapshot.IsMissingVsrepoModule)
            {
                return CreateMissingResult(definition);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.FailureReason))
            {
                return CreateMisconfiguredResult(definition, candidate, snapshot.FailureReason);
            }

            if (!snapshot.PackageKeys.Contains(definition.ProbeValue))
            {
                return CreateMissingResult(definition);
            }

            return new ToolProbeResult(
                definition.Kind,
                ReadinessState.Ready,
                ToolDetectionSource.None,
                "vsrepo installed",
                candidate.Path,
                "Installed",
                string.Empty,
                definition.ReleaseUrl,
                definition.ManagedExternalToolKind);
        }
        catch (Exception ex)
        {
            return CreateMisconfiguredResult(definition, candidate, ex.Message);
        }
    }

    private VsrepoInstalledProbeResult GetVsrepoInstalledProbeResult(
        ToolCandidate candidate,
        IDictionary<string, VsrepoInstalledProbeResult> vsrepoInstalledCache)
    {
        if (vsrepoInstalledCache.TryGetValue(candidate.Path, out var cached))
        {
            return cached;
        }

        using var process = new Process
        {
            StartInfo = CreatePythonModuleProcessStartInfo(
                candidate.Path,
                ["-m", "vsrepo.vsrepo", "-t", GetVsrepoTarget(), "installed"])
        };

        using var _ = ErrorDialogSuppression.Enter();
        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        VsrepoInstalledProbeResult result;
        if (!process.WaitForExit(VsrepoInstalledProbeTimeoutMilliseconds))
        {
            process.Kill(true);
            result = new VsrepoInstalledProbeResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                true,
                false,
                "VSRepo installed probe timed out.");
        }
        else
        {
            var output = string.Concat(
                stdOutTask.GetAwaiter().GetResult(),
                Environment.NewLine,
                stdErrTask.GetAwaiter().GetResult()).Trim();

            if (process.ExitCode != 0)
            {
                result = ContainsMissingVsrepoModule(output)
                    ? new VsrepoInstalledProbeResult(
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        false,
                        true,
                        string.Empty)
                    : new VsrepoInstalledProbeResult(
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        false,
                        false,
                        string.IsNullOrWhiteSpace(FirstMeaningfulLine(output))
                            ? $"VSRepo installed probe failed with exit code {process.ExitCode}."
                            : FirstMeaningfulLine(output));
            }
            else
            {
                result = new VsrepoInstalledProbeResult(
                    ParseVsrepoInstalledPackageKeys(output),
                    false,
                    false,
                    string.Empty);
            }
        }

        vsrepoInstalledCache[candidate.Path] = result;
        return result;
    }

    private ToolProbeResult ProbeWithPythonModuleImport(ToolDefinition definition, ToolCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(definition.ProbeValue))
        {
            return CreateUnknownResult(definition, "Missing Python module probe value.");
        }

        var moduleNameLiteral = EscapePythonSingleQuotedString(definition.ProbeValue);
        var distributionCandidates = GetPythonDistributionCandidates(definition)
            .Select(EscapePythonSingleQuotedString)
            .ToArray();
        var distributionCandidatesLiteral = string.Join(", ", distributionCandidates.Select(static item => $"'{item}'"));
        var script = string.Join(
            "\n",
            "import importlib.metadata, importlib.util, sys",
            $"module_name = '{moduleNameLiteral}'",
            $"distribution_names = [{distributionCandidatesLiteral}]",
            "spec = importlib.util.find_spec(module_name)",
            "if spec is None:",
            "    print('missing')",
            "    sys.exit(3)",
            "for distribution_name in distribution_names:",
            "    if not distribution_name:",
            "        continue",
            "    try:",
            "        print(importlib.metadata.version(distribution_name))",
            "        sys.exit(0)",
            "    except importlib.metadata.PackageNotFoundError:",
            "        pass",
            "print('Detected')",
            "sys.exit(0)");

        try
        {
            var startInfo = CreatePythonModuleProcessStartInfo(candidate.Path, ["-c", script]);

            using var _ = ErrorDialogSuppression.Enter();
            var result = ProcessProbeRunner.Run(
                startInfo,
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                "Python module probe timed out.");

            var output = string.Concat(
                result.StandardOutput,
                Environment.NewLine,
                result.StandardError).Trim();

            return result.ExitCode switch
            {
                0 => new ToolProbeResult(
                    definition.Kind,
                    ReadinessState.Ready,
                    ToolDetectionSource.None,
                    "python import",
                    candidate.Path,
                    FirstMeaningfulLine(output, "Detected"),
                    string.Empty,
                    definition.ReleaseUrl,
                    definition.ManagedExternalToolKind),
                3 => CreateMissingResult(definition),
                _ => CreateMisconfiguredResult(
                    definition,
                    candidate,
                    string.IsNullOrWhiteSpace(FirstMeaningfulLine(output))
                        ? $"Python module probe failed with exit code {result.ExitCode}."
                        : FirstMeaningfulLine(output))
            };
        }
        catch (Exception ex)
        {
            return CreateMisconfiguredResult(definition, candidate, ex.Message);
        }
    }

    private static ProcessStartInfo CreatePythonModuleProcessStartInfo(string pythonPath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(pythonPath) ?? AppContext.BaseDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        VapourSynthRuntimePathResolver.EnrichProcessPath(startInfo);
        return startInfo;
    }

    private ToolProbeResult CreateReadyResult(ToolDefinition definition, ToolCandidate candidate, string version)
    {
        return new ToolProbeResult(
            definition.Kind,
            ReadinessState.Ready,
            candidate.Source,
            candidate.SourceLabel,
            candidate.Path,
            version,
            string.Empty,
            definition.ReleaseUrl,
            definition.ManagedExternalToolKind);
    }

    private ToolProbeResult EnrichAv1anBackendCompatibility(ToolProbeResult baseResult, string executablePath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--protocol-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
                }
            };

            VapourSynthRuntimePathResolver.EnrichProcessPath(process.StartInfo);

            using var _ = ErrorDialogSuppression.Enter();
            var result = ProcessProbeRunner.Run(
                process.StartInfo,
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                "Protocol probe timed out.");

            var output = string.Concat(
                result.StandardOutput,
                Environment.NewLine,
                result.StandardError).Trim();

            if (result.ExitCode == 0)
            {
                var protocolVersion = FirstMeaningfulLine(output);
                return baseResult with
                {
                    IsProtocolCompatible = !string.IsNullOrWhiteSpace(protocolVersion),
                    ProtocolVersion = protocolVersion,
                    BackendCompatibilityDetail = !string.IsNullOrWhiteSpace(protocolVersion)
                        ? $"Protocol {protocolVersion}"
                        : "Protocol probe returned no version"
                };
            }

            return baseResult with
            {
                IsProtocolCompatible = false,
                ProtocolVersion = string.Empty,
                BackendCompatibilityDetail = string.IsNullOrWhiteSpace(output)
                    ? "Executable detected, protocol compatibility pending"
                    : $"Protocol probe failed: {FirstMeaningfulLine(output)}"
            };
        }
        catch (Exception ex)
        {
            return baseResult with
            {
                IsProtocolCompatible = false,
                ProtocolVersion = string.Empty,
                BackendCompatibilityDetail = $"Protocol probe unavailable: {ex.Message}"
            };
        }
    }

    private ToolProbeResult CreateMissingResult(ToolDefinition definition)
    {
        return new ToolProbeResult(
            definition.Kind,
            ReadinessState.Missing,
            ToolDetectionSource.None,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            definition.ReleaseUrl,
            definition.ManagedExternalToolKind);
    }

    private ToolProbeResult CreateMisconfiguredResult(ToolDefinition definition, ToolCandidate candidate, string failureReason)
    {
        return new ToolProbeResult(
            definition.Kind,
            ReadinessState.Misconfigured,
            candidate.Source,
            candidate.SourceLabel,
            candidate.Path,
            string.Empty,
            failureReason,
            definition.ReleaseUrl,
            definition.ManagedExternalToolKind);
    }

    private ToolProbeResult CreateUnknownResult(ToolDefinition definition, string failureReason)
    {
        return new ToolProbeResult(
            definition.Kind,
            ReadinessState.Unknown,
            ToolDetectionSource.None,
            string.Empty,
            string.Empty,
            string.Empty,
            failureReason,
            definition.ReleaseUrl,
            definition.ManagedExternalToolKind);
    }

    private string BuildProbeSignature(ToolDefinition definition, IReadOnlyDictionary<string, string> manualToolPaths)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(definition.Kind)
            .Append('|')
            .Append(definition.ProbeMode)
            .Append('|')
            .Append(definition.VersionArguments)
            .Append('|')
            .Append(definition.ProbeValue)
            .AppendLine();

        AppendManualToolPath(builder, ManualToolPathKeys.ForRegisteredTool(definition.Kind), manualToolPaths);

        foreach (var variableName in definition.EnvironmentVariableNames)
        {
            AppendEnvironmentValue(builder, variableName, EnvironmentVariableTarget.Process);
            AppendEnvironmentValue(builder, variableName, EnvironmentVariableTarget.User);
            AppendEnvironmentValue(builder, variableName, EnvironmentVariableTarget.Machine);
        }

        AppendEnvironmentValue(builder, "PATH", EnvironmentVariableTarget.Process);
        AppendEnvironmentValue(builder, "PATH", EnvironmentVariableTarget.User);
        AppendEnvironmentValue(builder, "PATH", EnvironmentVariableTarget.Machine);
        builder.Append("toolsRoot=")
            .Append(_paths.ToolsRootPath)
            .AppendLine();

        if (TryGetEncoderKind(definition.Kind) is { } encoderKind)
        {
            AppendEncoderProbeInputs(builder, encoderKind, manualToolPaths);
        }

        foreach (var candidate in EnumerateCandidates(definition, manualToolPaths).OrderBy(static item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            AppendCandidateFingerprint(builder, candidate.Path, candidate.Source, candidate.SourceLabel);
        }

        return builder.ToString();
    }

    private static void AppendManualToolPath(
        System.Text.StringBuilder builder,
        string key,
        IReadOnlyDictionary<string, string> manualToolPaths)
    {
        manualToolPaths.TryGetValue(key, out var value);
        builder.Append("manual:")
            .Append(key)
            .Append('=')
            .Append(value)
            .AppendLine();
    }

    private void AppendEncoderProbeInputs(
        System.Text.StringBuilder builder,
        EncoderKind encoderKind,
        IReadOnlyDictionary<string, string> manualToolPaths)
    {
        var executableNames = GetEncoderExecutableNames(encoderKind);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AppendManualToolPath(builder, ManualToolPathKeys.ForEncoder(encoderKind), manualToolPaths);
        AppendResolvedManualToolFingerprint(
            builder,
            ManualToolPathKeys.ForEncoder(encoderKind),
            executableNames,
            seen,
            manualToolPaths);

        foreach (var variableName in GetEncoderEnvironmentVariableNames(encoderKind))
        {
            AppendEnvironmentValueAndResolvedFingerprint(
                builder,
                variableName,
                EnvironmentVariableTarget.Process,
                executableNames,
                seen);
            AppendEnvironmentValueAndResolvedFingerprint(
                builder,
                variableName,
                EnvironmentVariableTarget.User,
                executableNames,
                seen);
            AppendEnvironmentValueAndResolvedFingerprint(
                builder,
                variableName,
                EnvironmentVariableTarget.Machine,
                executableNames,
                seen);
        }

        foreach (var architecture in Enum.GetValues<EncoderArchitecture>())
        {
            AppendCandidateFingerprintIfNew(
                builder,
                _paths.GetBinaryPath(encoderKind, architecture),
                ToolDetectionSource.LocalToolset,
                architecture.ToString(),
                seen);
        }

        foreach (var root in EnumeratePathRoots())
        {
            foreach (var executableName in executableNames)
            {
                var pathCandidate = Path.Combine(root, executableName);
                if (File.Exists(pathCandidate))
                {
                    AppendCandidateFingerprintIfNew(
                        builder,
                        pathCandidate,
                        ToolDetectionSource.Path,
                        "PATH",
                        seen);
                }
            }
        }
    }

    private void AppendResolvedManualToolFingerprint(
        System.Text.StringBuilder builder,
        string key,
        IReadOnlyList<string> executableNames,
        ISet<string> seen,
        IReadOnlyDictionary<string, string> manualToolPaths)
    {
        if (!manualToolPaths.TryGetValue(key, out var value))
        {
            return;
        }

        var resolved = ExecutablePathResolver.ResolveFromInput(value, executableNames, EnumeratePathRoots());
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            AppendCandidateFingerprintIfNew(
                builder,
                resolved,
                ToolDetectionSource.ManualSelection,
                "manual",
                seen);
        }
    }

    private static void AppendEnvironmentValueAndResolvedFingerprint(
        System.Text.StringBuilder builder,
        string variableName,
        EnvironmentVariableTarget target,
        IReadOnlyList<string> executableNames,
        ISet<string> seen)
    {
        var value = Environment.GetEnvironmentVariable(variableName, target);
        builder.Append("env:")
            .Append(target)
            .Append(':')
            .Append(variableName)
            .Append('=')
            .Append(value)
            .AppendLine();

        var resolved = ExecutablePathResolver.ResolveFromInput(value, executableNames, EnumeratePathRoots());
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            AppendCandidateFingerprintIfNew(
                builder,
                resolved,
                ToolDetectionSource.EnvironmentVariable,
                variableName,
                seen);
        }
    }

    private static void AppendCandidateFingerprintIfNew(
        System.Text.StringBuilder builder,
        string path,
        ToolDetectionSource source,
        string sourceLabel,
        ISet<string> seen)
    {
        var normalizedPath = Path.GetFullPath(path);
        if (!seen.Add(normalizedPath))
        {
            return;
        }

        AppendCandidateFingerprint(builder, normalizedPath, source, sourceLabel);
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

    private static void AppendCandidateFingerprint(
        System.Text.StringBuilder builder,
        string path,
        ToolDetectionSource source,
        string sourceLabel)
    {
        var normalizedPath = Path.GetFullPath(path);
        var fileInfo = new FileInfo(normalizedPath);
        builder.Append("candidate:")
            .Append(source)
            .Append('|')
            .Append(sourceLabel)
            .Append('|')
            .Append(normalizedPath)
            .Append('|')
            .Append(fileInfo.Exists ? fileInfo.Length : -1)
            .Append('|')
            .Append(fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : -1)
            .AppendLine();
    }

    private static EncoderKind? TryGetEncoderKind(RegisteredToolKind kind)
    {
        return kind switch
        {
            RegisteredToolKind.X264 => EncoderKind.X264,
            RegisteredToolKind.X265 => EncoderKind.X265,
            RegisteredToolKind.SvtAv1 => EncoderKind.SvtAv1,
            _ => null
        };
    }

    private static IReadOnlyList<string> GetEncoderEnvironmentVariableNames(EncoderKind kind)
    {
        return kind switch
        {
            EncoderKind.X264 => ["FLOWENCODE_X264", "X264_PATH", "X264_EXE", "X264"],
            EncoderKind.X265 => ["FLOWENCODE_X265", "X265_PATH", "X265_EXE", "X265"],
            EncoderKind.SvtAv1 => ["FLOWENCODE_AV1", "SVT_AV1_PATH", "SVTAV1_PATH", "SVTAV1", "AV1_ENCODER", "AV1"],
            _ => []
        };
    }

    private static IReadOnlyList<string> GetEncoderExecutableNames(EncoderKind kind)
    {
        return kind switch
        {
            EncoderKind.X264 => ["x264.exe"],
            EncoderKind.X265 => ["x265.exe"],
            EncoderKind.SvtAv1 => ["SvtAv1EncApp.exe", "svt-av1.exe"],
            _ => []
        };
    }

    private IEnumerable<ToolCandidate> EnumerateCandidates(
        ToolDefinition definition,
        IReadOnlyDictionary<string, string> manualToolPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EnumerateManualCandidates(definition, manualToolPaths))
        {
            if (seen.Add(candidate.Path))
            {
                yield return candidate;
            }
        }

        if (definition.SearchLocations.HasFlag(ToolSearchLocation.LocalToolsRoot))
        {
            foreach (var fileName in definition.ExecutableNames)
            {
                var candidatePath = Path.Combine(_paths.ToolsRootPath, fileName);
                if (File.Exists(candidatePath) && seen.Add(candidatePath))
                {
                    yield return new ToolCandidate(candidatePath, ToolDetectionSource.LocalTools, "tools");
                }
            }
        }

        foreach (var candidate in EnumerateEnvironmentVariableCandidates(definition))
        {
            if (seen.Add(candidate.Path))
            {
                yield return candidate;
            }
        }

        if (definition.SearchLocations.HasFlag(ToolSearchLocation.ProgramFilesVapourSynth))
        {
            foreach (var root in EnumerateProgramFilesVapourSynthRoots())
            {
                foreach (var fileName in definition.ExecutableNames)
                {
                    var candidatePath = Path.Combine(root, fileName);
                    if (File.Exists(candidatePath) && seen.Add(candidatePath))
                    {
                        yield return new ToolCandidate(candidatePath, ToolDetectionSource.SpecialLocation, root);
                    }
                }
            }
        }

        if (definition.SearchLocations.HasFlag(ToolSearchLocation.PythonScripts))
        {
            foreach (var root in VapourSynthRuntimePathResolver.CollectPythonScriptDirectories())
            {
                foreach (var fileName in definition.ExecutableNames)
                {
                    var candidatePath = Path.Combine(root, fileName);
                    if (File.Exists(candidatePath) && seen.Add(candidatePath))
                    {
                        yield return new ToolCandidate(candidatePath, ToolDetectionSource.SpecialLocation, root);
                    }
                }
            }
        }

        foreach (var candidate in EnumeratePathCandidates(definition))
        {
            if (seen.Add(candidate.Path))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<ToolCandidate> EnumerateManualCandidates(
        ToolDefinition definition,
        IReadOnlyDictionary<string, string> manualToolPaths)
    {
        if (!manualToolPaths.TryGetValue(ManualToolPathKeys.ForRegisteredTool(definition.Kind), out var value))
        {
            yield break;
        }

        var resolved = ExecutablePathResolver.ResolveFromInput(value, definition.ExecutableNames, EnumeratePathRoots());
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            yield return new ToolCandidate(resolved, ToolDetectionSource.ManualSelection, "manual");
        }
    }

    private IEnumerable<ToolCandidate> EnumerateEnvironmentVariableCandidates(ToolDefinition definition)
    {
        if (!definition.SearchLocations.HasFlag(ToolSearchLocation.EnvironmentVariables))
        {
            yield break;
        }

        foreach (var variableName in definition.EnvironmentVariableNames)
        {
            var value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process)
                ?? Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Machine);

            var resolved = ExecutablePathResolver.ResolveFromInput(value, definition.ExecutableNames, EnumeratePathRoots());
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                yield return new ToolCandidate(resolved, ToolDetectionSource.EnvironmentVariable, variableName);
            }
        }
    }

    private IEnumerable<ToolCandidate> EnumeratePathCandidates(ToolDefinition definition)
    {
        if (!definition.SearchLocations.HasFlag(ToolSearchLocation.Path))
        {
            yield break;
        }

        foreach (var root in EnumeratePathRoots())
        {
            foreach (var fileName in definition.ExecutableNames)
            {
                var candidatePath = Path.Combine(root, fileName);
                if (File.Exists(candidatePath))
                {
                    yield return new ToolCandidate(candidatePath, ToolDetectionSource.Path, "PATH");
                }
            }

            if (definition.SearchLocations.HasFlag(ToolSearchLocation.VspipeSidecar))
            {
                var sidecarCandidate = ResolvePythonSidecarVspipe(root);
                if (!string.IsNullOrWhiteSpace(sidecarCandidate))
                {
                    yield return new ToolCandidate(sidecarCandidate, ToolDetectionSource.SpecialLocation, "Python sidecar");
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateProgramFilesVapourSynthRoots()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VapourSynth"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VapourSynth", "core64")
        };

        return roots.Where(Directory.Exists);
    }

    private static string? ResolvePythonSidecarVspipe(string root)
    {
        return VapourSynthRuntimePathResolver.ResolvePythonSidecarVspipe(root);
    }

    private static IEnumerable<string> EnumeratePathRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathVariables = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        };

        foreach (var pathVariable in pathVariables)
        {
            if (string.IsNullOrWhiteSpace(pathVariable))
            {
                continue;
            }

            foreach (var root in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(root))
                {
                    yield return root;
                }
            }
        }
    }

    private static string ResolveVersionText(ToolDefinition definition, string executablePath, string processOutput)
    {
        var parsedVersion = TryResolveKnownProcessVersion(definition, processOutput);
        if (!string.IsNullOrWhiteSpace(parsedVersion))
        {
            return parsedVersion;
        }

        var versionLine = FirstMeaningfulLine(processOutput);
        if (!string.IsNullOrWhiteSpace(versionLine))
        {
            return versionLine;
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        return versionInfo.ProductVersion
            ?? versionInfo.FileVersion
            ?? "Present";
    }

    private static string FirstMeaningfulLine(string output, string fallback = "")
    {
        return EnumerateMeaningfulLines(output).FirstOrDefault() ?? fallback;
    }

    private static IEnumerable<string> EnumerateMeaningfulLines(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line));
    }

    private static string GetVsrepoTarget()
    {
        return Environment.Is64BitOperatingSystem ? "win64" : "win32";
    }

    private static string EscapePythonSingleQuotedString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    private static IReadOnlyList<string> GetPythonDistributionCandidates(ToolDefinition definition)
    {
        var candidates = new List<string>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)
                || candidates.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            candidates.Add(value);
        }

        switch (definition.Kind)
        {
            case RegisteredToolKind.Vsrepo:
                Add("vsrepo");
                break;
            case RegisteredToolKind.PythonModuleAwsmfunc:
                Add("awsmfunc");
                break;
            case RegisteredToolKind.PythonModuleVsjetpack:
                Add("vsjetpack");
                break;
        }

        Add(definition.ProbeValue);

        if (!string.IsNullOrWhiteSpace(definition.ProbeValue))
        {
            var rootModule = definition.ProbeValue.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            Add(rootModule);
        }

        return candidates;
    }

    private static string TryResolveKnownProcessVersion(ToolDefinition definition, string processOutput)
    {
        return definition.Kind switch
        {
            RegisteredToolKind.Vspipe => TryExtractVapourSynthCoreVersion(processOutput),
            RegisteredToolKind.OpusExt => TryExtractOpusEncoderVersion(processOutput),
            _ => string.Empty
        };
    }

    private static string TryExtractVapourSynthCoreVersion(string output)
    {
        foreach (var line in EnumerateMeaningfulLines(output))
        {
            var match = Regex.Match(line, @"\bCore\s+(R\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpperInvariant();
            }
        }

        return string.Empty;
    }

    private static string TryExtractOpusEncoderVersion(string output)
    {
        foreach (var line in EnumerateMeaningfulLines(output))
        {
            var opusToolsMatch = Regex.Match(line, @"\bopus-tools\s+(?<version>[^\s(]+)", RegexOptions.IgnoreCase);
            if (opusToolsMatch.Success)
            {
                return opusToolsMatch.Groups["version"].Value;
            }

            var genericVersionMatch = Regex.Match(line, @"\b(?<version>\d+(?:\.\d+)+(?:-[^\s(]+)?)\b");
            if (genericVersionMatch.Success)
            {
                return genericVersionMatch.Groups["version"].Value;
            }
        }

        return string.Empty;
    }

    private static bool ContainsMissingVsrepoModule(string output)
    {
        return output.Contains("No module named vsrepo", StringComparison.OrdinalIgnoreCase)
               || output.Contains("No module named 'vsrepo'", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ParseVsrepoInstalledPackageKeys(string output)
    {
        var packageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)
                || line.StartsWith("Name", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryParseVsrepoInstalledLine(line, out var package))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(package.DisplayName))
            {
                packageKeys.Add(package.DisplayName);
            }

            if (!string.IsNullOrWhiteSpace(package.Namespace))
            {
                packageKeys.Add(package.Namespace);
            }

            if (!string.IsNullOrWhiteSpace(package.Identifier))
            {
                packageKeys.Add(package.Identifier);
            }
        }

        return packageKeys;
    }

    private static bool TryParseVsrepoInstalledLine(string line, out VsrepoInstalledLine package)
    {
        var parts = Regex.Split(line.Trim(), "\\s{2,}");
        if (parts.Length < 3)
        {
            package = new VsrepoInstalledLine(string.Empty, string.Empty, string.Empty);
            return false;
        }

        var displayName = parts[0].Trim().TrimStart('*', '+');
        var packageNamespace = parts[1].Trim();
        string identifier;

        if (parts.Length >= 5)
        {
            identifier = parts[4].Trim();
        }
        else
        {
            var tailTokens = parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tailTokens.Length < 3)
            {
                package = new VsrepoInstalledLine(string.Empty, string.Empty, string.Empty);
                return false;
            }

            identifier = tailTokens[^1].Trim();
        }

        package = new VsrepoInstalledLine(displayName, packageNamespace, identifier);
        return true;
    }

    private sealed record VsrepoInstalledProbeResult(
        HashSet<string> PackageKeys,
        bool IsTimedOut,
        bool IsMissingVsrepoModule,
        string FailureReason);

    private sealed record VsrepoInstalledLine(string DisplayName, string Namespace, string Identifier);

    private sealed record ToolProbeCacheKey(RegisteredToolKind Kind, string Signature);

    private sealed record ToolCandidate(string Path, ToolDetectionSource Source, string SourceLabel);
}

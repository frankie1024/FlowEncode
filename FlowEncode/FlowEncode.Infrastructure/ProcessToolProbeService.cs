using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class ProcessToolProbeService : IToolProbeService
{
    private const int ProbeTimeoutMilliseconds = 5000;
    private const string PythonPackageListPrefix = "FLOWENCODE_PACKAGES ";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            using var discoveryScope = PythonRuntimeCompatibility.BeginDiscoveryScope();
            cancellationToken.ThrowIfCancellationRequested();

            return _toolRegistryService
                .GetTools()
                .Select(definition => ProbeCached(definition, cancellationToken))
                .ToList();
        }, cancellationToken);
    }

    public Task<ToolProbeResult> ProbeAsync(RegisteredToolKind kind, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var discoveryScope = PythonRuntimeCompatibility.BeginDiscoveryScope();
            var definition = _toolRegistryService.GetTool(kind);
            return ProbeCached(definition, cancellationToken);
        }, cancellationToken);
    }

    public void InvalidateCache()
    {
        _probeCache.Clear();
    }

    private ToolProbeResult ProbeCached(
        ToolDefinition definition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _settingsService.Load();
        var manualToolPaths = settings.EffectiveManualToolPaths;
        var key = new ToolProbeCacheKey(
            definition.Kind,
            BuildProbeSignature(definition, manualToolPaths, settings.PreferSystemEncoders));
        if (_probeCache.TryGetValue(key, out var cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        var result = Probe(definition, settings, cancellationToken, manualToolPaths);

        cancellationToken.ThrowIfCancellationRequested();
        _probeCache.TryAdd(key, result);
        return result;
    }

    private ToolProbeResult Probe(
        ToolDefinition definition,
        AppSettings settings,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> manualToolPaths)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return definition.ProbeMode switch
        {
            ToolProbeMode.EncoderBinary => ProbeEncoder(definition, settings),
            ToolProbeMode.ProcessVersion => ProbeCandidateTools(definition, cancellationToken, manualToolPaths, ProbeWithProcessVersion),
            ToolProbeMode.FileVersionInfo => ProbeCandidateTools(definition, cancellationToken, manualToolPaths, ProbeWithFileVersion),
            ToolProbeMode.ExistenceOnly => ProbeCandidateTools(definition, cancellationToken, manualToolPaths, ProbeWithExistenceOnly),
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

            if (definition.Kind == RegisteredToolKind.Python)
            {
                return EnrichPythonRuntimeCompatibility(definition, candidate);
            }

            var version = ResolveVersionText(definition, candidate.Path, output);
            var ready = CreateReadyResult(definition, candidate, version);
            if (definition.Kind == RegisteredToolKind.Deew)
            {
                var deewLine = output.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("deew ", StringComparison.OrdinalIgnoreCase)) ?? output;
                if (!DeewToolchain.IsSupportedVersion(deewLine))
                    return CreateMisconfiguredResult(definition, candidate, $"FlowEncode requires deew >={DeewArguments.MinimumVersion}. Upgrade deew.exe in the writable portable directory.");
                return ready with { BackendCompatibilityDetail = $"deew {DeewArguments.RecommendedVersion} recommended. Each request checks the required DEE >=6.7.0 components and the writable adjacent config." };
            }
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
        var script = definition.Kind switch
        {
            RegisteredToolKind.PythonModuleVsjetpack => VsjetpackFullProbeScript,
            RegisteredToolKind.PythonModuleAwsmfunc => AwsmfuncProbeScript,
            RegisteredToolKind.VapourSynthUtilityPlugins => VapourSynthUtilityPluginsProbeScript,
            _ => string.Join(
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
            "sys.exit(0)")
        };

        try
        {
            var startInfo = CreatePythonModuleProcessStartInfo(candidate.Path, definition.Kind == RegisteredToolKind.VapourSynthUtilityPlugins
                ? ["-c", script, .. LocalVapourSynthPluginList.Read(_paths)]
                : ["-c", script]);

            using var _ = ErrorDialogSuppression.Enter();
            var result = ProcessProbeRunner.Run(
                startInfo,
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                "Python module probe timed out.");

            var output = string.Concat(
                result.StandardOutput,
                Environment.NewLine,
                result.StandardError).Trim();

            PythonPackageStatus[]? packages = null;
            if (definition.Kind is RegisteredToolKind.PythonModuleVsjetpack or RegisteredToolKind.VapourSynthUtilityPlugins)
            {
                var packageLine = EnumerateMeaningfulLines(result.StandardOutput)
                    .FirstOrDefault(static line => line.StartsWith(PythonPackageListPrefix, StringComparison.Ordinal));
                if (packageLine is not null)
                    packages = JsonSerializer.Deserialize<PythonPackageStatus[]>(packageLine[PythonPackageListPrefix.Length..], JsonOptions);
            }

            if (definition.Kind == RegisteredToolKind.VapourSynthUtilityPlugins && packages is not null)
            {
                var state = result.ExitCode switch
                {
                    0 => ReadinessState.Ready,
                    3 => ReadinessState.Missing,
                    4 => ReadinessState.Partial,
                    _ => ReadinessState.Misconfigured
                };
                return new ToolProbeResult(definition.Kind, state, candidate.Source, candidate.SourceLabel, candidate.Path,
                    string.Empty, FirstMeaningfulLine(result.StandardError), definition.ReleaseUrl, PythonPackages: packages);
            }
            if (definition.Kind == RegisteredToolKind.VapourSynthUtilityPlugins && result.ExitCode == 0)
                return CreateMisconfiguredResult(definition, candidate, "The VapourSynth plugin probe returned no package information.");

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
                    definition.ManagedExternalToolKind,
                    PythonPackages: packages),
                3 => CreateMissingResult(definition),
                4 when definition.Kind == RegisteredToolKind.PythonModuleVsjetpack => new ToolProbeResult(
                    definition.Kind,
                    ReadinessState.Partial,
                    candidate.Source,
                    candidate.SourceLabel,
                    candidate.Path,
                    FirstMeaningfulLine(result.StandardOutput),
                    FirstMeaningfulLine(result.StandardError, "The Python package dependencies are incomplete."),
                    definition.ReleaseUrl,
                    definition.ManagedExternalToolKind,
                    PythonPackages: packages),
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

    internal const string AwsmfuncProbeScript = """
        import importlib.metadata as metadata
        import importlib.util
        import sys

        if importlib.util.find_spec('awsmfunc') is None:
            sys.exit(3)

        print(metadata.version('awsmfunc'))
        import awsmfunc
        """;

    internal const string VapourSynthUtilityPluginsProbeScript = """
        import importlib.metadata as metadata
        import json
        import sys

        try:
            import vapoursynth as vs
            core = vs.core
        except (ImportError, OSError):
            core = None

        required = {'vapoursynth-fillborders': ('fb', 'FillBorders'), 'vsfpng': ('fpng', 'Write')}
        only = sys.argv[2] if len(sys.argv) == 3 and sys.argv[1] == '--only' else None
        packages = []
        for name in [only] if only else dict.fromkeys([*required, *sys.argv[1:]]):
            try:
                version = metadata.version(name)
            except metadata.PackageNotFoundError:
                version = ''

            if name in required:
                namespace, function = required[name]
                loaded = callable(getattr(getattr(core, namespace, None), function, None))
            else:
                if not version:
                    continue
                loaded = True
            packages.append(dict(name=name, installedVersion=version, isReady=loaded))

        print('FLOWENCODE_PACKAGES ' + json.dumps(packages))
        required_packages = [package for package in packages if package['name'] in required]
        missing = [package['name'] for package in required_packages if not package['isReady']]
        if missing:
            print('VapourSynth plugins not loaded: ' + ', '.join(missing), file=sys.stderr)
            sys.exit(4 if any(package['installedVersion'] or package['isReady'] for package in required_packages) else 3)
        """;

    // Package metadata declares extras but does not record which extras were installed.
    internal const string VsjetpackFullProbeScript = """
        import importlib.metadata as metadata
        import importlib.util
        import json
        import sys

        if importlib.util.find_spec('vsjetpack') is None:
            sys.exit(3)

        try:
            jet = metadata.distribution('vsjetpack')
        except metadata.PackageNotFoundError:
            print('The vsjetpack distribution metadata is missing.', file=sys.stderr)
            sys.exit(4)

        from pip._vendor.packaging.requirements import Requirement
        from pip._vendor.packaging.utils import canonicalize_name

        print(jet.version)
        distributions = {'vsjetpack': jet}
        pending = {('vsjetpack', ''), ('vsjetpack', 'full')}
        checked = set()
        missing = set()
        packages = {}
        while pending:
            name, extra = pending.pop()
            checked.add((name, extra))
            package = distributions[name]
            if extra and extra not in {canonicalize_name(value) for value in package.metadata.get_all('Provides-Extra', [])}:
                missing.add(f'{name}[{extra}]')
                packages[f'{name}[{extra}]'] = dict(name=f'{name}[{extra}]', installedVersion=package.version, isReady=False)
                continue

            for value in package.requires or []:
                requirement = Requirement(value)
                if requirement.marker and not requirement.marker.evaluate({'extra': extra}):
                    continue
                dependency = canonicalize_name(requirement.name)
                if dependency not in distributions:
                    try:
                        distributions[dependency] = metadata.distribution(dependency)
                    except metadata.PackageNotFoundError:
                        distributions[dependency] = None
                installed = distributions[dependency]
                matches = installed is not None and requirement.specifier.contains(installed.version, prereleases=True)
                if dependency != 'vsjetpack':
                    entry = packages.setdefault(dependency, dict(name=dependency, installedVersion=installed.version if installed else '', isReady=True))
                    entry['isReady'] &= matches
                if not matches:
                    missing.add(str(requirement).partition(';')[0])
                    continue
                wanted = {(dependency, canonicalize_name(extra)) for extra in requirement.extras}
                wanted.add((dependency, ''))
                pending.update(wanted - checked)

        print('FLOWENCODE_PACKAGES ' + json.dumps([packages[name] for name in sorted(packages)]))
        if missing:
            print('Missing or incompatible vsjetpack[full] dependencies: ' + ', '.join(sorted(missing)), file=sys.stderr)
            sys.exit(4)
        """;

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

    private ToolProbeResult EnrichPythonRuntimeCompatibility(ToolDefinition definition, ToolCandidate candidate)
    {
        try
        {
            var startInfo = CreatePythonModuleProcessStartInfo(
                candidate.Path,
                ["-c", PythonRuntimeCompatibility.BuildProbeScript()]);
            var result = ProcessProbeRunner.Run(
                startInfo,
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                "Python compatibility probe timed out.");
            var output = string.Concat(
                result.StandardOutput,
                Environment.NewLine,
                result.StandardError).Trim();
            var lines = EnumerateMeaningfulLines(output).ToList();
            if (result.ExitCode != 0
                || lines.Count < 3
                || !Version.TryParse(lines[0], out var version)
                || !int.TryParse(lines[2], out var pointerSize))
            {
                return CreateMisconfiguredResult(
                    definition,
                    candidate,
                    string.IsNullOrWhiteSpace(FirstMeaningfulLine(output))
                        ? $"Python compatibility probe failed with exit code {result.ExitCode}."
                        : FirstMeaningfulLine(output));
            }

            var is64Bit = pointerSize == 64;
            if (!PythonRuntimeCompatibility.IsSupportedRuntime(version, is64Bit))
            {
                return CreateMisconfiguredResult(
                    definition,
                    candidate,
                    $"Python {version} {(is64Bit ? "x64" : "x86")} is not compatible with the VapourSynth stack. Python 3.13+ x64 is required.");
            }

            var executablePath = lines.Count > 1 && File.Exists(lines[1])
                ? Path.GetFullPath(lines[1])
                : candidate.Path;
            return CreateReadyResult(definition, candidate with { Path = executablePath }, $"{version} x64");
        }
        catch (Exception ex)
        {
            return CreateMisconfiguredResult(definition, candidate, ex.Message);
        }
    }

    private ToolProbeResult EnrichAv1anBackendCompatibility(ToolProbeResult baseResult, string executablePath)
    {
        try
        {
            var protocolResult = RunSimpleProbe(
                executablePath,
                "--protocol-version",
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                "Protocol probe timed out.");
            var output = string.Concat(
                protocolResult.StandardOutput,
                Environment.NewLine,
                protocolResult.StandardError).Trim();

            if (protocolResult.ExitCode == 0)
            {
                var protocolVersion = FirstMeaningfulLine(output);
                var capabilities = TryProbeAv1anCapabilities(executablePath);
                var compatibilityDetail = !string.IsNullOrWhiteSpace(protocolVersion)
                    ? $"Protocol {protocolVersion}"
                    : "Protocol probe returned no version";
                if (capabilities is { SupportedMetrics.Count: > 0 } snapshot)
                {
                    compatibilityDetail = $"{compatibilityDetail} · {snapshot.SupportedMetrics.Count} metrics";
                }

                return baseResult with
                {
                    IsProtocolCompatible = !string.IsNullOrWhiteSpace(protocolVersion),
                    ProtocolVersion = protocolVersion,
                    BackendCompatibilityDetail = compatibilityDetail,
                    Av1anCapabilities = capabilities
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

    private Av1anCapabilitiesSnapshot? TryProbeAv1anCapabilities(string executablePath)
    {
        try
        {
            var result = RunSimpleProbe(
                executablePath,
                "--capabilities",
                TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds),
                "Capabilities probe timed out.");
            if (result.ExitCode != 0)
            {
                return null;
            }

            var output = string.Concat(
                result.StandardOutput,
                Environment.NewLine,
                result.StandardError).Trim();
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (!root.TryGetProperty("supported_metrics", out var supportedMetrics)
                || supportedMetrics.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("supported_encoders", out var supportedEncoders)
                || supportedEncoders.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var protocol = root.TryGetProperty("protocol", out var protocolProperty) && protocolProperty.TryGetInt32(out var parsedProtocol)
                ? parsedProtocol
                : 0;
            var backendVersion = root.TryGetProperty("backend_version", out var backendVersionProperty)
                ? backendVersionProperty.GetString() ?? string.Empty
                : string.Empty;

            return new Av1anCapabilitiesSnapshot(
                protocol,
                backendVersion,
                ParseSupportedMetrics(supportedMetrics),
                ParseSupportedEncoders(supportedEncoders),
                ParseStringArray(root, "interp_methods"),
                ParseStringArray(root, "probing_stats"));
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<AutoCompressionMetric> ParseSupportedMetrics(JsonElement metricsElement)
    {
        var result = new List<AutoCompressionMetric>();
        foreach (var item in metricsElement.EnumerateArray())
        {
            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (TryMapMetric(value, out var metric))
            {
                result.Add(metric);
            }
        }

        return result;
    }

    private static IReadOnlyList<EncoderKind> ParseSupportedEncoders(JsonElement encodersElement)
    {
        var result = new List<EncoderKind>();
        foreach (var item in encodersElement.EnumerateArray())
        {
            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (TryMapEncoder(value, out var encoder))
            {
                result.Add(encoder);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value
            .EnumerateArray()
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }

    private static bool TryMapMetric(string raw, out AutoCompressionMetric metric)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "vmaf":
                metric = AutoCompressionMetric.Vmaf;
                return true;
            case "ssimulacra2":
                metric = AutoCompressionMetric.Ssimulacra2;
                return true;
            case "butteraugli-inf":
                metric = AutoCompressionMetric.ButteraugliInf;
                return true;
            case "butteraugli-3":
                metric = AutoCompressionMetric.Butteraugli3;
                return true;
            case "xpsnr":
                metric = AutoCompressionMetric.Xpsnr;
                return true;
            case "xpsnr-weighted":
                metric = AutoCompressionMetric.XpsnrWeighted;
                return true;
            default:
                metric = default;
                return false;
        }
    }

    private static bool TryMapEncoder(string raw, out EncoderKind encoder)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "x264":
                encoder = EncoderKind.X264;
                return true;
            case "x265":
                encoder = EncoderKind.X265;
                return true;
            case "svt-av1":
                encoder = EncoderKind.SvtAv1;
                return true;
            default:
                encoder = default;
                return false;
        }
    }

    private static ProcessProbeResult RunSimpleProbe(
        string executablePath,
        string arguments,
        TimeSpan timeout,
        string timeoutMessage)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
        };

        VapourSynthRuntimePathResolver.EnrichProcessPath(startInfo);

        using var _ = ErrorDialogSuppression.Enter();
        return ProcessProbeRunner.Run(startInfo, timeout, timeoutMessage);
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

    private string BuildProbeSignature(
        ToolDefinition definition,
        IReadOnlyDictionary<string, string> manualToolPaths,
        bool preferSystemEncoders)
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
        builder.Append("preferSystemEncoders=")
            .Append(preferSystemEncoders)
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

        const EncoderArchitecture architecture = EncoderArchitecture.X64;
        foreach (var path in new[]
                 {
                     _paths.GetBinaryPath(encoderKind, architecture),
                     _paths.GetLegacyBinaryPath(encoderKind, architecture)
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AppendCandidateFingerprintIfNew(
                builder,
                path,
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

        if (definition.SearchLocations.HasFlag(ToolSearchLocation.KnownPythonInstallations))
        {
            // Module probes share the explicitly selected interpreter, including
            // a 2.0 environment retained when a newer Python is installed.
            if (manualToolPaths.TryGetValue(ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Python), out var manualPython))
            {
                var resolvedPython = PythonRuntimeCompatibility.ResolvePythonExecutablePath(manualPython);
                if (resolvedPython is not null)
                {
                    if (seen.Add(resolvedPython)) yield return new ToolCandidate(resolvedPython, ToolDetectionSource.ManualSelection, "Python binding");
                    yield break;
                }
            }
            if (manualToolPaths.TryGetValue(ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Vspipe), out var boundVspipe)
                && PythonRuntimeCompatibility.FindOwningPython(boundVspipe) is { } owner)
            {
                if (seen.Add(owner)) yield return new ToolCandidate(owner, ToolDetectionSource.ManualSelection, "VapourSynth environment binding");
                yield break;
            }
            var python = PythonRuntimeCompatibility.EnumerateCompatiblePythonExecutablePaths().FirstOrDefault();
            if (python is not null)
            {
                if (seen.Add(python)) yield return new ToolCandidate(python, ToolDetectionSource.SpecialLocation, "Retained Python environment");
                yield break;
            }
        }

        if (definition.Kind is RegisteredToolKind.Vspipe or RegisteredToolKind.Pip)
        {
            foreach (var candidate in EnumerateEnvironmentVariableCandidates(definition))
                if (seen.Add(candidate.Path)) yield return candidate;
            var python = manualToolPaths.TryGetValue(ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Python), out var manual)
                ? PythonRuntimeCompatibility.ResolvePythonExecutablePath(manual)
                : manualToolPaths.TryGetValue(ManualToolPathKeys.ForRegisteredTool(RegisteredToolKind.Vspipe), out var vspipe)
                ? PythonRuntimeCompatibility.FindOwningPython(vspipe)
                : PythonRuntimeCompatibility.EnumerateCompatiblePythonExecutablePaths().FirstOrDefault();
            if (python is not null)
                foreach (var directory in PythonRuntimeCompatibility.EnumeratePythonRuntimeDirectories(python))
                    foreach (var name in definition.ExecutableNames)
                    {
                        var executable = Path.Combine(directory, name);
                        if (File.Exists(executable) && seen.Add(executable))
                            yield return new ToolCandidate(executable, ToolDetectionSource.SpecialLocation, "Selected Python environment");
                    }
        }

        if (definition.SearchLocations.HasFlag(ToolSearchLocation.LocalToolsRoot))
        {
            var package = ManualToolPackage.Find(definition.Kind);
            if (package?.FindExecutable(package.GetDirectory(_paths.ToolsRootPath)) is { } packageExecutable
                && seen.Add(packageExecutable))
            {
                yield return new ToolCandidate(packageExecutable, ToolDetectionSource.LocalTools, "tools");
            }

            var localRoots = definition.Kind switch
            {
                RegisteredToolKind.Ffmpeg or RegisteredToolKind.Ffprobe =>
                    new[] { _paths.GetManagedExternalToolDirectory(ExternalToolKind.Ffmpeg), _paths.ToolsRootPath },
                RegisteredToolKind.Av1an =>
                    new[] { _paths.GetManagedExternalToolDirectory(ExternalToolKind.Av1an), _paths.ToolsRootPath },
                _ => new[] { _paths.ToolsRootPath }
            };
            foreach (var fileName in definition.ExecutableNames)
            {
                foreach (var localRoot in localRoots)
                {
                    var candidatePath = Path.Combine(localRoot, fileName);
                    if (File.Exists(candidatePath) && seen.Add(candidatePath))
                    {
                        yield return new ToolCandidate(candidatePath, ToolDetectionSource.LocalTools, "tools");
                    }
                }
            }
        }

        if (definition.SearchLocations.HasFlag(ToolSearchLocation.KnownPythonInstallations))
        {
            foreach (var candidatePath in PythonRuntimeCompatibility.EnumerateKnownPythonExecutablePaths())
            {
                if (File.Exists(candidatePath) && seen.Add(candidatePath))
                {
                    yield return new ToolCandidate(candidatePath, ToolDetectionSource.SpecialLocation, "Python install");
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

    private IEnumerable<ToolCandidate> EnumerateManualCandidates(
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
            // Pins to the old managed executable follow a reimport into the package directory.
            var package = ManualToolPackage.Find(definition.Kind);
            if (package is not null && package.GetLegacyPaths(_paths.ToolsRootPath).Contains(resolved, StringComparer.OrdinalIgnoreCase))
            {
                resolved = package.FindExecutable(package.GetDirectory(_paths.ToolsRootPath)) ?? resolved;
            }

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
            RegisteredToolKind.Deew => DeewToolchain.ReadReportedVersion(processOutput),
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

    private sealed record ToolProbeCacheKey(RegisteredToolKind Kind, string Signature);

    private sealed record ToolCandidate(string Path, ToolDetectionSource Source, string SourceLabel);
}

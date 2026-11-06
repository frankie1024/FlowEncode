using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class EnvironmentTools
{
    [McpServerTool(Name = "get_environment_status", ReadOnly = true)]
    [Description("Checks which external tools (encoders, FFmpeg, VSPipe, Python, Av1an, demuxers...) are installed "
        + "and which FlowEncode capabilities are ready. Probing runs the tools, so it can take several seconds.")]
    public static async Task<EnvironmentStatusResult> GetEnvironmentStatus(
        IEnvironmentReadinessService readinessService,
        [Description("Include tools that are not installed. Defaults to true.")] bool includeMissingTools = true,
        CancellationToken cancellationToken = default)
    {
        var report = await McpToolGuard.RunAsync(() => readinessService.CheckAsync(cancellationToken));

        var tools = report.Tools
            .Where(tool => includeMissingTools || tool.IsReady)
            .Select(static tool => new ToolStatusView(
                tool.DisplayName,
                tool.State.ToString(),
                NullIfEmpty(tool.DetectedVersion),
                NullIfEmpty(tool.ExecutablePath),
                NullIfEmpty(tool.SourceLabel),
                NullIfEmpty(tool.FailureReason)))
            .ToList();

        var capabilities = report.Capabilities
            .Select(static capability => new CapabilityStatusView(
                capability.Kind.ToString(),
                capability.State.ToString(),
                capability.Requirements
                    .Where(static requirement => !requirement.IsSatisfied)
                    .Select(static requirement => string.Join(
                        " or ",
                        requirement.Requirement.CandidateTools.Select(static tool => tool.ToDisplayName())))
                    .ToList()))
            .ToList();

        return new EnvironmentStatusResult(report.CheckedAt, report.ReadyCapabilityCount, capabilities, tools);
    }

    [McpServerTool(Name = "list_encoders", ReadOnly = true)]
    [Description("Lists the x264 / x265 / SVT-AV1 executables FlowEncode can find and which one each encoder kind "
        + "resolves to for new jobs (respecting the 'prefer system encoders' setting).")]
    public static EncoderListResult ListEncoders(
        IEncoderDiscoveryService discoveryService,
        IAppSettingsService settingsService)
    {
        var preferSystemEncoders = settingsService.Load().PreferSystemEncoders;

        var resolved = Enum.GetValues<EncoderKind>()
            .Select(kind =>
            {
                var binary = discoveryService.ResolveEncoder(kind, EncoderArchitecture.X64, preferSystemEncoders);
                return new ResolvedEncoderView(
                    EncodingProfileResolver.ToEncoderToken(kind),
                    binary is not null && File.Exists(binary.ExecutablePath),
                    binary?.ExecutablePath,
                    NullIfEmpty(binary?.DetectedVersion),
                    binary?.Source.ToString());
            })
            .ToList();

        var discovered = discoveryService.DiscoverSystemBinaries()
            .Select(static binary => new DiscoveredEncoderView(
                EncodingProfileResolver.ToEncoderToken(binary.Kind),
                binary.Architecture.ToString(),
                binary.ExecutablePath,
                binary.Source.ToString(),
                NullIfEmpty(binary.DetectedVersion)))
            .ToList();

        return new EncoderListResult(preferSystemEncoders, resolved, discovered);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed record EnvironmentStatusResult(
    DateTimeOffset CheckedAt,
    int ReadyCapabilityCount,
    IReadOnlyList<CapabilityStatusView> Capabilities,
    IReadOnlyList<ToolStatusView> Tools);

public sealed record CapabilityStatusView(
    string Capability,
    string State,
    IReadOnlyList<string> MissingRequirements);

public sealed record ToolStatusView(
    string Tool,
    string State,
    string? Version,
    string? ExecutablePath,
    string? Source,
    string? FailureReason);

public sealed record EncoderListResult(
    bool PreferSystemEncoders,
    IReadOnlyList<ResolvedEncoderView> Resolved,
    IReadOnlyList<DiscoveredEncoderView> Discovered);

public sealed record ResolvedEncoderView(
    string Encoder,
    bool Available,
    string? ExecutablePath,
    string? Version,
    string? Source);

public sealed record DiscoveredEncoderView(
    string Encoder,
    string Architecture,
    string ExecutablePath,
    string Source,
    string? Version);

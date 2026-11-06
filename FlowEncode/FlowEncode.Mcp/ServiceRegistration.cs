using FlowEncode.Application;
using FlowEncode.Infrastructure;
using FlowEncode.Infrastructure.Automation;
using Microsoft.Extensions.DependencyInjection;

namespace FlowEncode.Mcp;

internal static class ServiceRegistration
{
    /// <summary>
    /// Registers the headless services used for read-only work (probing, command previews, templates,
    /// VapourSynth diagnostics and frame rendering) plus the bridge to the desktop app, which executes
    /// every task and owns settings and dependencies. Both read the same %LOCALAPPDATA%\FlowEncode state.
    /// </summary>
    public static IServiceCollection AddFlowEncodeServices(this IServiceCollection services)
    {
        services.AddSingleton(static _ => new LocalAppPaths(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DesktopAppLocator.ResolveInstallRoot()));
        services.AddSingleton<IFlowEncodeHttpClientFactory, FlowEncodeHttpClientFactory>();
        services.AddSingleton<IAppDiagnostics, LocalAppDiagnostics>();
        services.AddSingleton<LocalAppSettingsService>();
        services.AddSingleton<IAppSettingsService>(static provider => provider.GetRequiredService<LocalAppSettingsService>());
        services.AddSingleton<IToolRegistryService, DefaultToolRegistryService>();
        services.AddSingleton<IToolProbeService, ProcessToolProbeService>();
        services.AddSingleton<IEnvironmentReadinessService, EnvironmentReadinessService>();
        services.AddSingleton<IEncoderDiscoveryService, LocalEncoderDiscoveryService>();
        services.AddSingleton<IAudioSourceInfoService, FfprobeAudioSourceInfoService>();
        services.AddSingleton<IAudioProcessingRunner, CliAudioProcessingRunner>();
        services.AddSingleton<IBluRayDemuxBackendAdapter, DgDemuxBackendAdapter>();
        services.AddSingleton<IBluRayDemuxBackendAdapter, Eac3ToBackendAdapter>();
        services.AddSingleton<IBluRayDiscProbeService, CliBluRayDiscProbeService>();
        services.AddSingleton<IBluRayDemuxRunner, CliBluRayDemuxRunner>();
        services.AddSingleton<IProfileLibraryService, LocalProfileLibraryService>();
        services.AddSingleton<IEncodingJobRunner, LocalEncodingJobRunner>();
        services.AddSingleton<IAutoCompressionRunner, StructuredAv1anRunner>();
        services.AddSingleton<IAppUpdateService, GitHubAppUpdateService>();
        services.AddSingleton<IVapourSynthWorkspaceService, VapourSynthWorkspaceService>();
        services.AddSingleton<IVapourSynthWorkspaceLanguageService, VapourSynthWorkspaceLanguageService>();
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IVapourSynthPreviewService, VapourSynthPreviewService>();
        }

        services.AddSingleton<PreviewFrameComposer>();
        services.AddSingleton<VapourSynthPreviewGate>();
        services.AddSingleton<WorkspaceRootSync>();

        services.AddSingleton(static _ => new DesktopAutomationClient());
        services.AddSingleton<DesktopBridge>();

        return services;
    }
}

/// <summary>
/// The preview service holds one VapourSynth session at a time; tool calls take turns.
/// </summary>
public sealed class VapourSynthPreviewGate
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

using FlowEncode.Application;
using FlowEncode.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FlowEncode.Mcp;

internal static class ServiceRegistration
{
    /// <summary>
    /// Registers the headless subset of the services the desktop app wires up in App.BuildServices.
    /// Settings, toolchain and templates live under %LOCALAPPDATA%\FlowEncode, so the MCP server
    /// sees the same environment the GUI configured.
    /// </summary>
    public static IServiceCollection AddFlowEncodeServices(this IServiceCollection services)
    {
        services.AddSingleton<LocalAppPaths>();
        services.AddSingleton<IAppDiagnostics, LocalAppDiagnostics>();
        services.AddSingleton<LocalAppSettingsService>();
        services.AddSingleton<IAppSettingsService>(static provider => provider.GetRequiredService<LocalAppSettingsService>());
        services.AddSingleton<IToolRegistryService, DefaultToolRegistryService>();
        services.AddSingleton<IToolProbeService, ProcessToolProbeService>();
        services.AddSingleton<IEnvironmentReadinessService, EnvironmentReadinessService>();
        services.AddSingleton<IEncoderDiscoveryService, LocalEncoderDiscoveryService>();
        services.AddSingleton<IAudioSourceInfoService, FfprobeAudioSourceInfoService>();
        services.AddSingleton<IBluRayDemuxBackendAdapter, DgDemuxBackendAdapter>();
        services.AddSingleton<IBluRayDemuxBackendAdapter, Eac3ToBackendAdapter>();
        services.AddSingleton<IBluRayDiscProbeService, CliBluRayDiscProbeService>();
        services.AddSingleton<IProfileLibraryService, LocalProfileLibraryService>();
        services.AddSingleton<IEncodingJobRunner, LocalEncodingJobRunner>();
        services.AddSingleton<EncodingJobManager>();
        services.AddHostedService(static provider => provider.GetRequiredService<EncodingJobManager>());

        return services;
    }
}

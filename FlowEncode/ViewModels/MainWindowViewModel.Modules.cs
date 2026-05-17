namespace FlowEncode.ViewModels;

public partial class MainWindowViewModel
{
    public DashboardViewModel DashboardModule { get; private set; } = null!;

    public OverviewViewModel OverviewModule { get; private set; } = null!;

    public SettingsViewModel SettingsModule { get; private set; } = null!;

    public SetupGuideViewModel SetupGuideModule { get; private set; } = null!;

    public TemplatesViewModel TemplatesModule { get; private set; } = null!;

    public AutoCompressionViewModel AutoCompressionModule { get; private set; } = null!;

    public AudioProcessingViewModel AudioProcessingModule { get; private set; } = null!;

    public BluRayDemuxViewModel BluRayDemuxModule { get; private set; } = null!;

    private void InitializeModuleViewModels()
    {
        DashboardModule = new DashboardViewModel(this);
        OverviewModule = new OverviewViewModel(this);
        SettingsModule = new SettingsViewModel(this);
        SetupGuideModule = new SetupGuideViewModel(this, _setupBootstrapService, _setupGuideCacheService, _toolRegistryService, _appPaths);
        TemplatesModule = new TemplatesViewModel(this);
        AutoCompressionModule = new AutoCompressionViewModel(this);
        AudioProcessingModule = new AudioProcessingViewModel(this);
        BluRayDemuxModule = new BluRayDemuxViewModel(this);
    }

    private void DisposeModuleViewModels()
    {
        DashboardModule?.Dispose();
        OverviewModule?.Dispose();
        SettingsModule?.Dispose();
        SetupGuideModule?.Dispose();
        TemplatesModule?.Dispose();
        AutoCompressionModule?.Dispose();
        AudioProcessingModule?.Dispose();
        BluRayDemuxModule?.Dispose();
    }
}

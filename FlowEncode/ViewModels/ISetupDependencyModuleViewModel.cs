using System.Threading.Tasks;
using FlowEncode.Domain;

namespace FlowEncode.ViewModels;

public interface ISetupDependencyModuleViewModel
{
    AppText Texts { get; }

    bool IsSetupGuideOpen { get; }

    Task RefreshSetupGuideAsync();

    Task CheckSetupDependencyUpdatesAsync(bool openWhenFinished = false);

    bool RequiresSetupDependencyManualImport(SetupDependencyKind kind);

    bool HasManualPinnedSetupDependency(SetupDependencyKind kind);

    string GetSetupDependencyDisplayName(SetupDependencyKind kind);

    string GetSetupDependencyCurrentPath(SetupDependencyKind kind);

    Task<string?> InstallSetupDependencyAsync(SetupDependencyKind kind);

    Task<string?> ImportSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath);

    Task<string?> PinSetupDependencyBinaryAsync(SetupDependencyKind kind, string sourcePath);

    Task<string?> ClearManualPinnedSetupDependencyAsync(SetupDependencyKind kind, bool refreshAfterClear = true);

    Task<string?> UninstallSetupDependencyAsync(SetupDependencyKind kind);
}

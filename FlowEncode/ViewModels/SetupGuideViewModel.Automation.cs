using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;

namespace FlowEncode.ViewModels;

public sealed partial class SetupGuideViewModel
{
    internal bool HasAutomationStatusReport => _setupGuideStatusReport is not null;

    internal IReadOnlyList<AutomationDependencyStatus> GetAutomationDependencyStatuses()
    {
        var statuses = _setupGuideStatusReport?.Dependencies
            .GroupBy(static status => status.Kind)
            .ToDictionary(static group => group.Key, static group => group.First())
            ?? new Dictionary<SetupDependencyKind, SetupDependencyStatus>();

        return Enum.GetValues<SetupDependencyKind>()
            .Select(kind =>
            {
                statuses.TryGetValue(kind, out var status);
                var item = FindSetupGuideDependency(kind);
                return new AutomationDependencyStatus(
                    kind,
                    GetSetupDependencyTitle(kind),
                    (status?.State ?? item?.State)?.ToString() ?? "Unknown",
                    status?.InstalledVersion ?? string.Empty,
                    status?.LatestVersion ?? string.Empty,
                    status?.UpdateAvailable == true,
                    !string.IsNullOrWhiteSpace(status?.ExecutablePath) ? status!.ExecutablePath : item?.ExecutablePath ?? string.Empty,
                    HasManualPinnedSetupDependency(kind),
                    status is { IsInstallSupported: true, IsInstallEnabled: true },
                    _setupBootstrapService.CanUninstall(kind),
                    RequiresSetupDependencyManualImport(kind),
                    item?.IsBusy == true,
                    status?.Detail ?? item?.DetailText ?? string.Empty);
            })
            .ToList();
    }

    /// <summary>
    /// Runs one dependency action the same way the setup page buttons do and returns its error, if any.
    /// </summary>
    internal Task<string?> RunAutomationDependencyOperationAsync(SetupDependencyKind kind, string operation, string? sourcePath)
    {
        return operation switch
        {
            DesktopDependencyOperations.Install => InstallSetupDependencyAsync(kind),
            DesktopDependencyOperations.Uninstall => _setupBootstrapService.CanUninstall(kind)
                ? UninstallSetupDependencyAsync(kind)
                : Task.FromResult<string?>($"{GetSetupDependencyTitle(kind)} cannot be uninstalled by FlowEncode."),
            DesktopDependencyOperations.Import => ImportSetupDependencyBinaryAsync(kind, sourcePath ?? string.Empty),
            DesktopDependencyOperations.Pin => PinSetupDependencyBinaryAsync(kind, sourcePath ?? string.Empty),
            DesktopDependencyOperations.ClearPin => ClearManualPinnedSetupDependencyAsync(kind),
            _ => Task.FromResult<string?>($"Unknown dependency operation '{operation}'.")
        };
    }
}

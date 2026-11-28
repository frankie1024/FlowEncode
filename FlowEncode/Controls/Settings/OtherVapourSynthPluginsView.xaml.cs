using System.Threading.Tasks;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Settings;

public sealed partial class OtherVapourSynthPluginsView : UserControl
{
    public static readonly DependencyProperty ModuleProperty = DependencyProperty.Register(
        nameof(Module), typeof(ISetupDependencyModuleViewModel), typeof(OtherVapourSynthPluginsView), new PropertyMetadata(null));

    public ISetupDependencyModuleViewModel? Module
    {
        get => (ISetupDependencyModuleViewModel?)GetValue(ModuleProperty);
        set => SetValue(ModuleProperty, value);
    }

    public OtherVapourSynthPluginsView()
    {
        InitializeComponent();
    }

    private void PluginList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ItemsRepeater { Layout: UniformGridLayout layout })
        {
            var twoColumnWidth = 2 * layout.MinItemWidth + layout.MinColumnSpacing;
            ColumnDivider.Visibility = e.NewSize.Width >= twoColumnWidth ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupGuideDependencyItemViewModel { CanInstallOtherPlugins: true })
            await ChangePluginAsync(PackageName.Text, VapourSynthPluginAction.Install);
    }

    private async void InstallListedPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            await ChangePluginAsync(name, VapourSynthPluginAction.Install);
    }

    private async void UpdatePlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            await ChangePluginAsync(name, VapourSynthPluginAction.Update);
    }

    private async void UninstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
            await ChangePluginAsync(name, VapourSynthPluginAction.Uninstall);
    }

    private Task ChangePluginAsync(string name, VapourSynthPluginAction action)
    {
        if (Module is not { } module)
            return Task.CompletedTask;

        var texts = module.Texts;
        var (operation, errorTitle) = action switch
        {
            VapourSynthPluginAction.Update => ("Update VapourSynth plugin", texts.ErrorUpdateFailedTitle),
            VapourSynthPluginAction.Uninstall => ("Uninstall VapourSynth plugin", texts.ErrorUninstallFailedTitle),
            _ => ("Install VapourSynth plugin", texts.ErrorInstallFailedTitle)
        };
        return SetupDependencyInteractionHelper.RunGuardedAsync(
            module, this, nameof(OtherVapourSynthPluginsView), operation, errorTitle, async () =>
            {
                if (action == VapourSynthPluginAction.Uninstall && !await SetupDependencyInteractionHelper.ShowConfirmationAsync(
                    module, this, texts.OtherPluginUninstallLabel(name), texts.OtherPluginUninstallMessage(name),
                    texts.UninstallButton, texts.CancelButton, ContentDialogButton.Close))
                {
                    return;
                }

                var error = await module.ChangeVapourSynthPluginAsync(name, action);
                if (!string.IsNullOrWhiteSpace(error))
                    await SetupDependencyInteractionHelper.ShowMessageAsync(module, this, errorTitle, error);
            });
    }
}

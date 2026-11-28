using FlowEncode.Domain;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Settings;

public sealed partial class SetupDependencyCardView : UserControl
{
    public static readonly DependencyProperty ModuleProperty = DependencyProperty.Register(
        nameof(Module), typeof(ISetupDependencyModuleViewModel), typeof(SetupDependencyCardView), new PropertyMetadata(null));

    public static readonly DependencyProperty DeewDownloadVisibilityProperty = DependencyProperty.Register(
        nameof(DeewDownloadVisibility), typeof(Visibility), typeof(SetupDependencyCardView), new PropertyMetadata(Visibility.Collapsed));

    public ISetupDependencyModuleViewModel? Module
    {
        get => (ISetupDependencyModuleViewModel?)GetValue(ModuleProperty);
        set => SetValue(ModuleProperty, value);
    }

    public Visibility DeewDownloadVisibility
    {
        get => (Visibility)GetValue(DeewDownloadVisibilityProperty);
        set => SetValue(DeewDownloadVisibilityProperty, value);
    }

    public SetupDependencyCardView() => InitializeComponent();

    private Visibility ResolveDeewDownloadVisibility(Visibility availability, object? dataContext)
        => availability == Visibility.Visible && dataContext is SetupGuideDependencyItemViewModel { Kind: SetupDependencyKind.Deew }
            ? Visibility.Visible : Visibility.Collapsed;

    private void MoreDependencyActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Module is { } module && DataContext is SetupGuideDependencyItemViewModel item && sender is FrameworkElement button)
            SetupDependencyInteractionHelper.ShowMoreDependencyActions(module, this, button, item, nameof(SetupDependencyCardView));
    }

    private async void JetpackPackagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (Module is { } module && DataContext is SetupGuideDependencyItemViewModel item)
            await SetupDependencyInteractionHelper.ShowJetpackPackagesAsync(this, module.Texts, item, nameof(SetupDependencyCardView));
    }

    private async void InstallSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (Module is { } module && sender is FrameworkElement { Tag: SetupDependencyKind kind })
            await SetupDependencyInteractionHelper.InstallSetupDependencyAsync(module, this, kind, nameof(SetupDependencyCardView));
    }

    private async void UninstallSetupDependencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (Module is { } module && sender is FrameworkElement { Tag: SetupDependencyKind kind })
            await SetupDependencyInteractionHelper.UninstallSetupDependencyAsync(module, this, kind, nameof(SetupDependencyCardView));
    }

    private async void DownloadDeewComponentsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Module is not SettingsDependenciesViewModel { CanModifyDependencies: true } dependencies)
            return;

        await SetupDependencyInteractionHelper.RunGuardedAsync(dependencies, this, nameof(SetupDependencyCardView),
            "Download deew components", dependencies.Texts.DeewComponentsDownloadFailed, async () =>
            {
                var error = await dependencies.DownloadDeewComponentsAsync();
                await SetupDependencyInteractionHelper.ShowMessageAsync(dependencies, this,
                    error is null ? dependencies.Texts.DeewComponentsDownloadButton : dependencies.Texts.DeewComponentsDownloadFailed,
                    error ?? dependencies.Texts.DeewComponentsDownloadComplete);
            });
    }
}

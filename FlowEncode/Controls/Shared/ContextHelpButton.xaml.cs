using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Shared;

public sealed partial class ContextHelpButton : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ContextHelpButton), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty HelpTextProperty = DependencyProperty.Register(
        nameof(HelpText), typeof(string), typeof(ContextHelpButton), new PropertyMetadata(string.Empty, OnHelpTextChanged));

    public ContextHelpButton()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncVisibility();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string HelpText { get => (string)GetValue(HelpTextProperty); set => SetValue(HelpTextProperty, value); }

    private static void OnHelpTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((ContextHelpButton)sender).SyncVisibility();

    private void SyncVisibility() => Visibility = string.IsNullOrWhiteSpace(HelpText) ? Visibility.Collapsed : Visibility.Visible;
    private void HelpToolTip_Opened(object sender, RoutedEventArgs e) => HelpToolTipText.Text = HelpText;
    private void HelpFlyout_Opening(object sender, object e)
    {
        HelpTitleText.Text = Title;
        HelpBodyText.Text = HelpText;
    }
}

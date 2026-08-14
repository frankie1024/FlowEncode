using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Shared;

public sealed partial class TaskActionBar : UserControl
{
    public static readonly DependencyProperty PrimaryContentProperty = RegisterContentProperty(nameof(PrimaryContent));
    public static readonly DependencyProperty SecondaryContentProperty = RegisterContentProperty(nameof(SecondaryContent));
    public static readonly DependencyProperty TertiaryContentProperty = RegisterContentProperty(nameof(TertiaryContent));
    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.Register(nameof(IsStacked), typeof(bool), typeof(TaskActionBar), new PropertyMetadata(false, OnLayoutPropertyChanged));

    public TaskActionBar()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateLayoutMode();
    }

    public object? PrimaryContent { get => GetValue(PrimaryContentProperty); set => SetValue(PrimaryContentProperty, value); }
    public object? SecondaryContent { get => GetValue(SecondaryContentProperty); set => SetValue(SecondaryContentProperty, value); }
    public object? TertiaryContent { get => GetValue(TertiaryContentProperty); set => SetValue(TertiaryContentProperty, value); }
    public bool IsStacked { get => (bool)GetValue(IsStackedProperty); set => SetValue(IsStackedProperty, value); }

    private static DependencyProperty RegisterContentProperty(string name) => DependencyProperty.Register(name, typeof(object), typeof(TaskActionBar), new PropertyMetadata(null, OnLayoutPropertyChanged));

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TaskActionBar actionBar)
        {
            actionBar.UpdateLayoutMode();
        }
    }

    private void UpdateLayoutMode()
    {
        var hasPrimary = PrimaryContent is not null;
        var hasSecondary = SecondaryContent is not null;
        var hasTertiary = TertiaryContent is not null;
        var actionCount = (hasPrimary ? 1 : 0) + (hasSecondary ? 1 : 0) + (hasTertiary ? 1 : 0);

        PrimaryPresenter.Visibility = hasPrimary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryPresenter.Visibility = hasSecondary ? Visibility.Visible : Visibility.Collapsed;
        TertiaryPresenter.Visibility = hasTertiary ? Visibility.Visible : Visibility.Collapsed;

        ActionGrid.ColumnSpacing = !IsStacked && actionCount > 1 ? UiTokens.SpacingM : 0;
        ActionGrid.RowSpacing = IsStacked && actionCount > 1 ? UiTokens.SpacingM : 0;
        PrimaryColumn.Width = hasPrimary ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SecondaryColumn.Width = !IsStacked && hasSecondary ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TertiaryColumn.Width = !IsStacked && hasTertiary ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        SetPresenterLayout(PrimaryPresenter, 0, 0);
        SetPresenterLayout(SecondaryPresenter, IsStacked ? 1 : 0, IsStacked ? 0 : 1);
        SetPresenterLayout(TertiaryPresenter, IsStacked ? 2 : 0, IsStacked ? 0 : 2);
    }

    private void SetPresenterLayout(FrameworkElement presenter, int row, int column)
    {
        Grid.SetRow(presenter, row);
        Grid.SetColumn(presenter, column);
    }
}

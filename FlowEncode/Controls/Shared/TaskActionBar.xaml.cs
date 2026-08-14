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

    private static DependencyProperty RegisterContentProperty(string name) => DependencyProperty.Register(name, typeof(object), typeof(TaskActionBar), new PropertyMetadata(null));

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TaskActionBar actionBar)
        {
            actionBar.UpdateLayoutMode();
        }
    }

    private void UpdateLayoutMode()
    {
        ActionGrid.ColumnSpacing = IsStacked ? 0 : UiTokens.SpacingM;
        ActionGrid.RowSpacing = IsStacked ? UiTokens.SpacingM : 0;
        SecondaryColumn.Width = IsStacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        TertiaryColumn.Width = IsStacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

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

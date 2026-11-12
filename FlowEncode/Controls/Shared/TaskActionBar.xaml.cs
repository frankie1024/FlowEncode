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
        // A zero-width column still contributes ColumnSpacing in WinUI.
        // Only create slots for actions that are actually present.
        var columnCount = IsStacked ? 1 : System.Math.Max(1, actionCount);
        var rowCount = IsStacked ? System.Math.Max(1, actionCount) : 1;
        if (ActionGrid.ColumnDefinitions.Count != columnCount)
        {
            ActionGrid.ColumnDefinitions.Clear();
            for (var index = 0; index < columnCount; index++)
                ActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        if (ActionGrid.RowDefinitions.Count != rowCount)
        {
            ActionGrid.RowDefinitions.Clear();
            for (var index = 0; index < rowCount; index++)
                ActionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var slot = 0;
        foreach (var presenter in new[] { PrimaryPresenter, SecondaryPresenter, TertiaryPresenter })
        {
            var position = presenter.Visibility == Visibility.Visible ? slot++ : 0;
            SetPresenterLayout(presenter, IsStacked ? position : 0, IsStacked ? 0 : position);
        }
    }

    private void SetPresenterLayout(FrameworkElement presenter, int row, int column)
    {
        Grid.SetRow(presenter, row);
        Grid.SetColumn(presenter, column);
    }
}

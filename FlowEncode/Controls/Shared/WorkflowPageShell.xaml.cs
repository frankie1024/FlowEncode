using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Shared;

public sealed partial class WorkflowPageShell : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(WorkflowPageShell),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(WorkflowPageShell),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText),
        typeof(string),
        typeof(WorkflowPageShell),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HeaderActionsContentProperty = DependencyProperty.Register(
        nameof(HeaderActionsContent),
        typeof(object),
        typeof(WorkflowPageShell),
        new PropertyMetadata(null));

    public static readonly DependencyProperty PrimaryContentProperty = DependencyProperty.Register(
        nameof(PrimaryContent),
        typeof(object),
        typeof(WorkflowPageShell),
        new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryContentProperty = DependencyProperty.Register(
        nameof(SecondaryContent),
        typeof(object),
        typeof(WorkflowPageShell),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsStackedWorkspaceProperty = DependencyProperty.Register(
        nameof(IsStackedWorkspace),
        typeof(bool),
        typeof(WorkflowPageShell),
        new PropertyMetadata(false, OnLayoutPropertyChanged));

    public static readonly DependencyProperty IsHeaderStackedProperty = DependencyProperty.Register(
        nameof(IsHeaderStacked),
        typeof(bool),
        typeof(WorkflowPageShell),
        new PropertyMetadata(false));

    public static readonly DependencyProperty PagePaddingProperty = DependencyProperty.Register(
        nameof(PagePadding),
        typeof(Thickness),
        typeof(WorkflowPageShell),
        new PropertyMetadata(new Thickness(24, 20, 24, 24)));

    public static readonly DependencyProperty PrimaryColumnWeightProperty = DependencyProperty.Register(
        nameof(PrimaryColumnWeight),
        typeof(double),
        typeof(WorkflowPageShell),
        new PropertyMetadata(0.55d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SecondaryColumnWeightProperty = DependencyProperty.Register(
        nameof(SecondaryColumnWeight),
        typeof(double),
        typeof(WorkflowPageShell),
        new PropertyMetadata(0.45d, OnLayoutPropertyChanged));

    public WorkflowPageShell()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateLayoutMode();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public object? HeaderActionsContent
    {
        get => GetValue(HeaderActionsContentProperty);
        set => SetValue(HeaderActionsContentProperty, value);
    }

    public object? PrimaryContent
    {
        get => GetValue(PrimaryContentProperty);
        set => SetValue(PrimaryContentProperty, value);
    }

    public object? SecondaryContent
    {
        get => GetValue(SecondaryContentProperty);
        set => SetValue(SecondaryContentProperty, value);
    }

    public bool IsStackedWorkspace
    {
        get => (bool)GetValue(IsStackedWorkspaceProperty);
        set => SetValue(IsStackedWorkspaceProperty, value);
    }

    public bool IsHeaderStacked
    {
        get => (bool)GetValue(IsHeaderStackedProperty);
        set => SetValue(IsHeaderStackedProperty, value);
    }

    public Thickness PagePadding
    {
        get => (Thickness)GetValue(PagePaddingProperty);
        set => SetValue(PagePaddingProperty, value);
    }

    public double PrimaryColumnWeight
    {
        get => (double)GetValue(PrimaryColumnWeightProperty);
        set => SetValue(PrimaryColumnWeightProperty, value);
    }

    public double SecondaryColumnWeight
    {
        get => (double)GetValue(SecondaryColumnWeightProperty);
        set => SetValue(SecondaryColumnWeightProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WorkflowPageShell shell)
        {
            shell.UpdateLayoutMode();
        }
    }

    private void UpdateLayoutMode()
    {
        WorkspaceGrid.ColumnSpacing = IsStackedWorkspace ? 0 : UiTokens.SpacingL;
        WorkspaceGrid.RowSpacing = IsStackedWorkspace ? UiTokens.SpacingL : 0;
        PrimaryColumn.Width = new GridLength(IsStackedWorkspace ? 1 : PrimaryColumnWeight, GridUnitType.Star);
        SecondaryColumn.Width = IsStackedWorkspace
            ? new GridLength(0)
            : new GridLength(SecondaryColumnWeight, GridUnitType.Star);
        WorkspacePrimaryRow.Height = GridLength.Auto;
        WorkspaceSecondaryRow.Height = IsStackedWorkspace ? GridLength.Auto : new GridLength(0);

        Grid.SetRow(PrimaryPresenter, 0);
        Grid.SetColumn(PrimaryPresenter, 0);
        Grid.SetColumnSpan(PrimaryPresenter, 1);
        Grid.SetRow(SecondaryPresenter, IsStackedWorkspace ? 1 : 0);
        Grid.SetColumn(SecondaryPresenter, IsStackedWorkspace ? 0 : 1);
        Grid.SetColumnSpan(SecondaryPresenter, IsStackedWorkspace ? 2 : 1);
    }
}

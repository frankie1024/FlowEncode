using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Shared;

public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnHeaderContentChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnHeaderContentChanged));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText),
        typeof(string),
        typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnHeaderContentChanged));

    public static readonly DependencyProperty ActionsContentProperty = DependencyProperty.Register(
        nameof(ActionsContent),
        typeof(object),
        typeof(PageHeader),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.Register(
        nameof(IsStacked),
        typeof(bool),
        typeof(PageHeader),
        new PropertyMetadata(false, OnLayoutPropertyChanged));

    public PageHeader()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateContentVisibility();
            UpdateLayoutMode();
        };
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

    public object? ActionsContent
    {
        get => GetValue(ActionsContentProperty);
        set => SetValue(ActionsContentProperty, value);
    }

    public bool IsStacked
    {
        get => (bool)GetValue(IsStackedProperty);
        set => SetValue(IsStackedProperty, value);
    }

    private static void OnHeaderContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PageHeader header)
        {
            header.UpdateContentVisibility();
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PageHeader header)
        {
            header.UpdateLayoutMode();
        }
    }

    private void UpdateContentVisibility()
    {
        DescriptionTextBlock.Visibility = string.IsNullOrWhiteSpace(Description)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusTextBlock.Visibility = string.IsNullOrWhiteSpace(StatusText)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateLayoutMode()
    {
        if (IsStacked)
        {
            Grid.SetRow(ActionsPresenter, 1);
            Grid.SetColumn(ActionsPresenter, 0);
            Grid.SetColumnSpan(ActionsPresenter, 2);
            ActionsPresenter.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            Grid.SetRow(ActionsPresenter, 0);
            Grid.SetColumn(ActionsPresenter, 1);
            Grid.SetColumnSpan(ActionsPresenter, 1);
            ActionsPresenter.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FlowEncode.Controls.Shared;

public sealed partial class TaskStatusPanel : UserControl
{
    public static readonly DependencyProperty PresentationStateProperty = DependencyProperty.Register(nameof(PresentationState), typeof(TaskPresentationState), typeof(TaskStatusPanel), new PropertyMetadata(TaskPresentationState.Idle, OnPresentationStateChanged));
    public static readonly DependencyProperty TitleProperty = RegisterStringProperty(nameof(Title));
    public static readonly DependencyProperty StatusTextProperty = RegisterStringProperty(nameof(StatusText));
    public static readonly DependencyProperty ProgressTrackBrushProperty = RegisterBrushProperty(nameof(ProgressTrackBrush));
    public static readonly DependencyProperty ProgressBorderBrushProperty = RegisterBrushProperty(nameof(ProgressBorderBrush));
    public static readonly DependencyProperty ProgressFillBrushProperty = RegisterBrushProperty(nameof(ProgressFillBrush));
    public static readonly DependencyProperty ProgressValueProperty = DependencyProperty.Register(nameof(ProgressValue), typeof(double), typeof(TaskStatusPanel), new PropertyMetadata(0d));
    public static readonly DependencyProperty ProgressTextProperty = RegisterStringProperty(nameof(ProgressText));
    public static readonly DependencyProperty SecondaryTextProperty = RegisterStringProperty(nameof(SecondaryText));
    public static readonly DependencyProperty SecondaryVisibilityProperty = DependencyProperty.Register(nameof(SecondaryVisibility), typeof(Visibility), typeof(TaskStatusPanel), new PropertyMetadata(Visibility.Collapsed));
    public static readonly DependencyProperty FailureDetailsContentProperty = DependencyProperty.Register(nameof(FailureDetailsContent), typeof(object), typeof(TaskStatusPanel), new PropertyMetadata(null, OnFailureDetailsContentChanged));

    public TaskStatusPanel()
    {
        InitializeComponent();
        UpdatePresentationState();
    }

    public TaskPresentationState PresentationState { get => (TaskPresentationState)GetValue(PresentationStateProperty); set => SetValue(PresentationStateProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string StatusText { get => (string)GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }
    public Brush? ProgressTrackBrush { get => (Brush?)GetValue(ProgressTrackBrushProperty); set => SetValue(ProgressTrackBrushProperty, value); }
    public Brush? ProgressBorderBrush { get => (Brush?)GetValue(ProgressBorderBrushProperty); set => SetValue(ProgressBorderBrushProperty, value); }
    public Brush? ProgressFillBrush { get => (Brush?)GetValue(ProgressFillBrushProperty); set => SetValue(ProgressFillBrushProperty, value); }
    public double ProgressValue { get => (double)GetValue(ProgressValueProperty); set => SetValue(ProgressValueProperty, value); }
    public string ProgressText { get => (string)GetValue(ProgressTextProperty); set => SetValue(ProgressTextProperty, value); }
    public string SecondaryText { get => (string)GetValue(SecondaryTextProperty); set => SetValue(SecondaryTextProperty, value); }
    public Visibility SecondaryVisibility { get => (Visibility)GetValue(SecondaryVisibilityProperty); set => SetValue(SecondaryVisibilityProperty, value); }
    public object? FailureDetailsContent { get => GetValue(FailureDetailsContentProperty); set => SetValue(FailureDetailsContentProperty, value); }

    private static DependencyProperty RegisterStringProperty(string name) => DependencyProperty.Register(name, typeof(string), typeof(TaskStatusPanel), new PropertyMetadata(string.Empty));

    private static DependencyProperty RegisterBrushProperty(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(TaskStatusPanel), new PropertyMetadata(null));

    private static void OnPresentationStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TaskStatusPanel panel)
        {
            panel.UpdatePresentationState();
        }
    }

    private static void OnFailureDetailsContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TaskStatusPanel panel)
        {
            panel.UpdateFailureDetailsVisibility();
        }
    }

    private void UpdatePresentationState()
    {
        CompletedIcon.Visibility = PresentationState == TaskPresentationState.Completed
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateFailureDetailsVisibility();
    }

    private void UpdateFailureDetailsVisibility()
    {
        FailureDetailsPresenter.Visibility = PresentationState == TaskPresentationState.Failed && FailureDetailsContent is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

}

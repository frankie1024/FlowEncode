using System;
using System.Diagnostics;
using FlowEncode.Application;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FlowEncode.Controls.Shared;

public sealed partial class CommandPreviewBox : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header),
        typeof(string),
        typeof(CommandPreviewBox),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(CommandPreviewBox),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText),
        typeof(string),
        typeof(CommandPreviewBox),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PreviewHeightProperty = DependencyProperty.Register(
        nameof(PreviewHeight),
        typeof(double),
        typeof(CommandPreviewBox),
        new PropertyMetadata(double.NaN));

    public static readonly DependencyProperty PreviewMinHeightProperty = DependencyProperty.Register(
        nameof(PreviewMinHeight),
        typeof(double),
        typeof(CommandPreviewBox),
        new PropertyMetadata(120d));

    public static readonly DependencyProperty IsCopyEnabledProperty = DependencyProperty.Register(
        nameof(IsCopyEnabled),
        typeof(bool),
        typeof(CommandPreviewBox),
        new PropertyMetadata(true, OnCopyStateChanged));

    public static readonly DependencyProperty TextsProperty = DependencyProperty.Register(
        nameof(Texts),
        typeof(AppText),
        typeof(CommandPreviewBox),
        new PropertyMetadata(null, OnCopyStateChanged));

    public CommandPreviewBox()
    {
        InitializeComponent();
        Loaded += CommandPreviewBox_Loaded;
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public double PreviewHeight
    {
        get => (double)GetValue(PreviewHeightProperty);
        set => SetValue(PreviewHeightProperty, value);
    }

    public double PreviewMinHeight
    {
        get => (double)GetValue(PreviewMinHeightProperty);
        set => SetValue(PreviewMinHeightProperty, value);
    }

    public bool IsCopyEnabled
    {
        get => (bool)GetValue(IsCopyEnabledProperty);
        set => SetValue(IsCopyEnabledProperty, value);
    }

    public AppText? Texts
    {
        get => (AppText?)GetValue(TextsProperty);
        set => SetValue(TextsProperty, value);
    }

    private static void OnTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var previewBox = (CommandPreviewBox)dependencyObject;
        previewBox.HideStatus();
        previewBox.SyncCopyState();
    }

    private static void OnCopyStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((CommandPreviewBox)dependencyObject).SyncCopyState();
    }

    private void CommandPreviewBox_Loaded(object sender, RoutedEventArgs e)
    {
        SyncCopyState();
    }

    private void CopyCommandButton_Click(object sender, RoutedEventArgs e)
    {
        var command = Text;
        if (string.IsNullOrWhiteSpace(command))
        {
            ShowStatus(Texts?.CommandPreviewCopyUnavailableStatus ?? "No command is available to copy.");
            SyncCopyState();
            return;
        }

        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(command);
            Clipboard.SetContent(dataPackage);
        }
        catch (Exception ex)
        {
            TryWriteClipboardFailure("SetContent", ex);
            ShowStatus(Texts?.CommandPreviewCopyFailedStatus(ex.Message) ?? $"Copying the command failed: {ex.Message}");
            return;
        }

        try
        {
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            TryWriteClipboardFailure("Flush", ex, AppDiagnosticSeverity.Warning);
        }

        ShowStatus(Texts?.CommandPreviewCopiedStatus ?? "Command copied.");
    }

    private void SyncCopyState()
    {
        if (CopyCommandButton is null || CopyCommandButtonLabelTextBlock is null)
        {
            return;
        }

        var copyButtonText = Texts?.CommandPreviewCopyButton ?? "Copy Command";
        CopyCommandButtonLabelTextBlock.Text = copyButtonText;
        CopyCommandButton.IsEnabled = IsCopyEnabled && !string.IsNullOrWhiteSpace(Text);
        ToolTipService.SetToolTip(CopyCommandButton, copyButtonText);
        AutomationProperties.SetName(CopyCommandButton, copyButtonText);
    }

    private void ShowStatus(string status)
    {
        if (StatusTextBlock is null)
        {
            return;
        }

        StatusTextBlock.Text = status;
        StatusTextBlock.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        if (StatusTextBlock is null)
        {
            return;
        }

        StatusTextBlock.Text = string.Empty;
        StatusTextBlock.Visibility = Visibility.Collapsed;
    }

    private static void TryWriteClipboardFailure(
        string operationName,
        Exception exception,
        AppDiagnosticSeverity severity = AppDiagnosticSeverity.Error)
    {
        try
        {
            App.GetService<IAppDiagnostics>().WriteException(
                nameof(CommandPreviewBox),
                $"Copy command preview: {operationName}",
                exception,
                severity);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write command preview clipboard diagnostic. {ex}");
        }
    }
}

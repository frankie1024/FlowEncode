using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlowEncode.Application;
using FlowEncode.Infrastructure;
using FlowEncode.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FlowEncode.Controls.VapourSynth;

public sealed partial class VapourSynthWorkspaceView
{
    private CancellationTokenSource? _repairAnalysisCancellation;

    private static string? FindRepairMediaSource(VapourSynthPreviewOpenRequest request)
    {
        var path = VapourSynthSmartScriptEditor.FindGeneratedVideoSourcePath(request.Content);
        if (path is null)
        {
            return null;
        }
        var resolvedPath = Path.GetFullPath(path, request.WorkingDirectory);
        return File.Exists(resolvedPath) ? resolvedPath : null;
    }

    private async void AnalyzeRepairsButton_Click(SplitButton sender, SplitButtonClickEventArgs e)
    {
        await RunUiActionAsync(() => AnalyzeRepairsAsync(false));
    }

    private async void DeepScanRepairsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(() => AnalyzeRepairsAsync(true));
    }

    private async void ScanPlaylistSeamsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var texts = ViewModel.Texts;
            var playlist = WindowInteractionHelper.PickOpenFilePath(GetWindowHandle(), texts.VapourSynthRepairPlaylistButton, string.Empty,
                new NativeFileDialogHelper.FileDialogFilter(texts.VapourSynthRepairPlaylistType, "*.mpls"));
            if (playlist is not null)
            {
                await AnalyzeRepairsAsync(true, playlist);
            }
        });
    }

    private void CancelRepairScanButton_Click(object sender, RoutedEventArgs e) => _repairAnalysisCancellation?.Cancel();

    private async Task AnalyzeRepairsAsync(bool deep, string? playlist = null)
    {
        if (_repairAnalysisCancellation is not null || !TryGetActiveEditorContext(out var pane, out var tab)
            || !ViewModel.TryBeginScriptAction(false, texts => deep ? texts.VapourSynthRepairDeepStatus : texts.VapourSynthRepairScanningStatus))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _repairAnalysisCancellation = cancellation;
        CancelRepairScanButton.Visibility = Visibility.Visible;
        try
        {
            VapourSynthPreviewOpenRequest request;
            VapourSynthPreviewOutputInfo output;
            PreviewRepairAnalysisResult analysis;
            try
            {
                await CaptureEditorStateAsync(pane);
                request = CreatePreviewOpenRequest(tab, out _);
                using var analyzer = CreateRepairPreviewService();
                var session = await analyzer.OpenSessionAsync(request, cancellation.Token);
                output = session.Outputs.FirstOrDefault(static item => item.Index == 0) ?? session.Outputs[0];
                var mediaSource = FindRepairMediaSource(request);
                analysis = await analyzer.AnalyzeRepairsAsync(output.Index, cancellation.Token, mediaSource, deep, playlist);
                await analyzer.CloseSessionAsync(cancellation.Token);
            }
            finally
            {
                ViewModel.EndScriptAction();
                CancelRepairScanButton.Visibility = Visibility.Collapsed;
            }

            if (!cancellation.IsCancellationRequested && ViewModel.Tabs.Contains(tab))
            {
                await ReviewRepairsAsync(tab, request, output, analysis, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _repairAnalysisCancellation = null;
        }
    }

    private static IVapourSynthPreviewService CreateRepairPreviewService() =>
        new VapourSynthPreviewService(App.GetService<IToolProbeService>(), App.GetService<LocalAppPaths>());

    private async Task ReviewRepairsAsync(
        VapourSynthWorkspaceTabViewModel tab,
        VapourSynthPreviewOpenRequest request,
        VapourSynthPreviewOutputInfo output,
        PreviewRepairAnalysisResult analysis,
        CancellationToken cancellationToken)
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        var texts = ViewModel.Texts;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(RepairText(analysis.Deep
            ? texts.VapourSynthRepairDeepSummary(analysis.SampledCount, analysis.ChapterCount, analysis.PlaylistSeamCount)
            : texts.VapourSynthRepairSampleSummary(analysis.SampledCount, analysis.WindowCount, analysis.ChapterCount, output.Index)));
        if (analysis.Truncated)
        {
            body.Children.Add(RepairText(texts.VapourSynthRepairTruncated));
        }
        if (analysis.PlaylistMismatch)
        {
            body.Children.Add(RepairText(texts.VapourSynthRepairPlaylistMismatch));
        }
        if (analysis.HasPgs)
        {
            body.Children.Add(RepairText(texts.VapourSynthRepairPgs));
        }
        if (analysis.FieldScanFailure is { } fieldFailure)
        {
            var unavailable = RepairText(texts.VapourSynthRepairFieldsUnavailable);
            ToolTipService.SetToolTip(unavailable, fieldFailure);
            body.Children.Add(unavailable);
        }
        if (analysis.VariableCrop)
        {
            body.Children.Add(RepairText(texts.VapourSynthRepairVariableCrop));
        }

        if (analysis.BandingScanFailure is { } bandingFailure)
        {
            var unavailable = RepairText(texts.VapourSynthRepairBandingUnavailable);
            ToolTipService.SetToolTip(unavailable, bandingFailure);
            body.Children.Add(unavailable);
        }

        if (analysis.Suggestions.All(static repair => repair.Method == "manual"))
        {
            body.Children.Add(RepairText(texts.VapourSynthRepairNoSuggestions));
        }

        var draft = new TextBox { IsReadOnly = true, AcceptsReturn = true, MaxHeight = 165, TextWrapping = TextWrapping.NoWrap };
        AutomationProperties.SetName(draft, texts.VapourSynthRepairDraft);
        ScrollViewer.SetHorizontalScrollBarVisibility(draft, ScrollBarVisibility.Auto);
        var draftHint = RepairText(texts.VapourSynthRepairDraftEmpty);
        draftHint.FontSize = 12;
        draftHint.VerticalAlignment = VerticalAlignment.Center;
        var draftTitle = RepairText(texts.VapourSynthRepairDraft);
        draftTitle.VerticalAlignment = VerticalAlignment.Center;
        var draftHeader = new Grid { ColumnSpacing = 10 };
        draftHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        draftHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(draftHint, 1);
        draftHeader.Children.Add(draftTitle);
        draftHeader.Children.Add(draftHint);
        var draftExpander = new Expander
        {
            Header = draftHeader,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(draftExpander, texts.VapourSynthRepairDraft);
        var status = RepairText(string.Empty);
        var dialog = new ContentDialog
        {
            Title = texts.VapourSynthRepairTitle,
            XamlRoot = root,
            RequestedTheme = ActualTheme,
            Content = new ScrollViewer { Content = body, MaxHeight = Math.Clamp(root.Size.Height - 220, 160, 520) },
            PrimaryButtonText = texts.VapourSynthRepairInsert,
            SecondaryButtonText = texts.VapourSynthRepairCopy,
            CloseButtonText = texts.CancelButton,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false
        };

        var checks = new List<(PreviewRepairSuggestion Suggestion, CheckBox Check)>();
        var manual = new StackPanel { Spacing = 8 };
        var crop = analysis.Suggestions.FirstOrDefault(static repair => repair.Kind == "crop" && repair.CanApply);
        var previewButtons = new List<Button>();
        var previewBusy = false;
        var validating = false;
        var selected = Array.Empty<PreviewRepairSuggestion>();
        SmartScriptEditResult? verifiedDraft = null;
        foreach (var suggestion in analysis.Suggestions)
        {
            var row = new StackPanel { Spacing = 4 };
            var check = new CheckBox
            {
                Content = suggestion.Kind switch
                {
                    "crop" => texts.VapourSynthRepairCrop,
                    "edge" or "local-edge" => texts.VapourSynthRepairEdge,
                    "aa" => texts.VapourSynthRepairAa,
                    "deband" => texts.VapourSynthRepairDeband,
                    "cas" => texts.VapourSynthRepairCas,
                    "dedot" => texts.VapourSynthRepairDedot,
                    _ => texts.VapourSynthRepairFinding(suggestion.Kind)
                },
                IsEnabled = suggestion.CanApply,
                IsChecked = ReferenceEquals(suggestion, crop) && !analysis.VariableCrop && !analysis.HasPgs
            };
            checks.Add((suggestion, check));
            row.Children.Add(suggestion.CanApply ? check : RepairText((string)check.Content));
            if (suggestion.Edges is [var left, var top, var right, var bottom])
            {
                row.Children.Add(RepairText(texts.VapourSynthRepairEdges(left, top, right, bottom)));
            }

            if (suggestion.Remainder?.Any(static pixels => pixels > 0) == true)
            {
                row.Children.Add(RepairText(texts.VapourSynthRepairCropRemainder));
            }

            if (suggestion.UsesCroppedEdges && crop is not null && suggestion.CanApply)
            {
                row.Children.Add(RepairText(texts.VapourSynthRepairEdgeNeedsCrop));
            }

            if (suggestion.Ranges is { Length: > 0 } ranges)
            {
                var rangeText = string.Join(" · ", ranges.Take(3).Select(range => range.Start == range.End
                    ? FormatRepairTimestamp(range.Start, output)
                    : $"{FormatRepairTimestamp(range.Start, output)}–{FormatRepairTimestamp(range.End, output)}"));
                var intervals = RepairText(ranges.Length <= 3 ? rangeText : texts.VapourSynthRepairMoreRanges(rangeText, ranges.Length));
                if (ranges.Length > 3)
                {
                    ToolTipService.SetToolTip(intervals, string.Join(Environment.NewLine, ranges.Select(range =>
                        $"{FormatRepairTimestamp(range.Start, output)}–{FormatRepairTimestamp(range.End, output)}")));
                }
                row.Children.Add(intervals);
            }
            if (suggestion.Evidence is { } evidence)
            {
                row.Children.Add(RepairText(texts.VapourSynthRepairEvidence(evidence, suggestion.Value, suggestion.Total)));
            }
            if (suggestion.Scope is { } scope)
            {
                row.Children.Add(RepairText(texts.VapourSynthRepairScope(scope, suggestion.ScopeHits, suggestion.ScopeSamples)));
            }
            if (suggestion.Region is [var x1, var y1, var x2, var y2])
            {
                row.Children.Add(RepairText(texts.VapourSynthRepairRegion(x1, y1, x2, y2)));
            }

            if (suggestion.Method == "autocrop")
            {
                row.Children.Add(RepairText(texts.VapourSynthRepairCropVerified));
            }

            if (suggestion.Failure is { } failure)
            {
                var unavailable = RepairText(texts.VapourSynthRepairFilterUnavailable);
                ToolTipService.SetToolTip(unavailable, failure);
                row.Children.Add(unavailable);
            }
            if (suggestion.Frames.Length > 0)
            {
                var timestamps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var frame in suggestion.Frames)
                {
                    var button = new Button { Content = FormatRepairTimestamp(frame, output), Padding = new Thickness(7, 3, 7, 3) };
                    previewButtons.Add(button);
                    button.Click += async (_, _) =>
                    {
                        if (previewBusy || validating)
                        {
                            return;
                        }

                        previewBusy = true;
                        UpdateDraft();
                        status.Text = texts.VapourSynthRepairPreviewLoading;
                        try
                        {
                            PreviewRepairSuggestion[] repairs = suggestion.UsesCroppedEdges && crop is not null ? [crop, suggestion] : [suggestion];
                            var comparison = suggestion.CanApply
                                ? VapourSynthRepairScriptBuilder.Build(request.Content, output.Index, repairs, compare: true).UpdatedScript
                                : request.Content;
                            var labels = $"__flowencode_output_names__ = {{0: {JsonSerializer.Serialize(texts.VapourSynthRepairBefore)}, 1: {JsonSerializer.Serialize(texts.VapourSynthRepairAfter)}}}\n";
                            var previewWindow = GetOrCreatePreviewWindow();
                            _previewLogTab = tab;
                            previewWindow.SmartEditScriptAsync = null;
                            var main = App.GetService<MainWindow>().ViewModel.SettingsModule;
                            var opened = await previewWindow.OpenOrRefreshAsync(
                                request with { Content = suggestion.CanApply ? comparison + labels : comparison },
                                main.CurrentLanguagePreference, main.CurrentThemePreference,
                                suggestion.CanApply ? PreviewCompareMode.SideBySide : PreviewCompareMode.Single, repairFrameNumber: frame);
                            status.Text = opened ? string.Empty : texts.VapourSynthRepairPreviewFailed;
                        }
                        catch (Exception error)
                        {
                            TryWriteDiagnosticException("Open repair comparison", error);
                            status.Text = texts.VapourSynthRepairPreviewFailed;
                        }
                        finally
                        {
                            previewBusy = false;
                            UpdateDraft();
                        }
                    };
                    timestamps.Children.Add(button);
                }
                row.Children.Add(timestamps);
            }
            (suggestion.Method == "manual" ? manual : body).Children.Add(row);
        }

        if (manual.Children.Count > 0)
        {
            body.Children.Add(new Expander { Header = texts.VapourSynthRepairManual, Content = manual, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        body.Children.Add(draftExpander);
        body.Children.Add(status);
        foreach (var (_, check) in checks)
        {
            check.Checked += (_, _) => UpdateDraft();
            check.Unchecked += (_, _) => UpdateDraft();
        }
        dialog.PrimaryButtonClick += ValidateSelection;
        dialog.SecondaryButtonClick += ValidateSelection;
        UpdateDraft();
        var result = await dialog.ShowAsync();
        if (verifiedDraft is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (result == ContentDialogResult.Secondary)
        {
            var data = new DataPackage();
            data.SetText(verifiedDraft.UpdatedScript);
            Clipboard.SetContent(data);
            ViewModel.SetWorkspaceStatus(tab, static t => t.VapourSynthRepairCopied);
        }
        else if (result == ContentDialogResult.Primary)
        {
            await InsertRepairDraftAsync(tab, request.Content, verifiedDraft);
        }

        void UpdateDraft()
        {
            var cropEnabled = crop is null || checks.Any(item => ReferenceEquals(item.Suggestion, crop) && item.Check.IsChecked == true);
            foreach (var (suggestion, check) in checks)
            {
                var prerequisiteMet = !suggestion.UsesCroppedEdges || cropEnabled;
                check.IsEnabled = !validating && suggestion.CanApply && prerequisiteMet;
                if (!prerequisiteMet)
                {
                    check.IsChecked = false;
                }
            }

            var hadDraft = selected.Length > 0;
            selected = checks.Where(static item => item.Check.IsChecked == true).Select(static item => item.Suggestion).ToArray();
            var hasDraft = selected.Length > 0;
            draft.Text = hasDraft ? VapourSynthRepairScriptBuilder.Build(request.Content, output.Index, selected).UpdatedScript : string.Empty;
            draftHint.Visibility = hasDraft ? Visibility.Collapsed : Visibility.Visible;
            if (hasDraft != hadDraft)
            {
                draftExpander.Content = hasDraft ? draft : null;
                draftExpander.IsExpanded = hasDraft;
            }
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = hasDraft && !previewBusy && !validating;
            foreach (var button in previewButtons)
            {
                button.IsEnabled = !previewBusy && !validating;
            }
        }

        async void ValidateSelection(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (selected.Length == 0 || validating || previewBusy)
            {
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            var repairs = selected;
            validating = true;
            UpdateDraft();
            status.Text = texts.VapourSynthRepairValidatingStatus;
            try
            {
                var candidate = VapourSynthRepairScriptBuilder.Build(request.Content, output.Index, repairs);
                using var validator = CreateRepairPreviewService();
                await validator.OpenSessionAsync(request with { Content = candidate.UpdatedScript }, cancellationToken);
                byte[]? reusablePixels = null;
                foreach (var frame in repairs.Select(static repair => repair.Frames[0]).Distinct())
                {
                    var rendered = await validator.RenderFrameAsync(output.Index, frame, reusablePixels, cancellationToken);
                    reusablePixels = rendered.Pixels;
                }
                await validator.CloseSessionAsync(cancellationToken);
                verifiedDraft = candidate;
            }
            catch (Exception error)
            {
                args.Cancel = true;
                status.Text = error.Message;
                TryWriteDiagnosticException("Verify repair script", error);
            }
            finally
            {
                validating = false;
                UpdateDraft();
                deferral.Complete();
            }
        }
    }

    private async Task InsertRepairDraftAsync(VapourSynthWorkspaceTabViewModel tab, string original, SmartScriptEditResult draft)
    {
        var pane = IsPaneBoundToTab(LeftEditorPane, tab) ? LeftEditorPane
            : IsPaneBoundToTab(RightEditorPane, tab) ? RightEditorPane : null;
        if (pane is not null)
        {
            await CaptureEditorStateAsync(pane);
        }

        if (!ViewModel.Tabs.Contains(tab) || !string.Equals(original, tab.CurrentContent, StringComparison.Ordinal))
        {
            await ShowMessageAsync(ViewModel.Texts.VapourSynthRepairTitle, ViewModel.Texts.VapourSynthRepairScriptChanged, XamlRoot);
            return;
        }

        if (pane is not null && IsPaneBoundToTab(pane, tab))
        {
            if (!await pane.ApplySmartDocumentEditAsync(draft.UpdatedScript, draft.TargetLineNumber))
            {
                await ShowMessageAsync(ViewModel.Texts.VapourSynthRepairTitle, ViewModel.Texts.VapourSynthRepairScriptChanged, XamlRoot);
                return;
            }

            await CaptureEditorStateAsync(pane);
            ScheduleDiagnostics(pane);
            await FocusEditorIfStillBoundAsync(pane, tab);
        }
        else
        {
            tab.ApplyEditorBuffer(draft.UpdatedScript, draft.TargetLineNumber, 1,
                draft.UpdatedScript.Count(static character => character == '\n') + 1, draft.UpdatedScript.Length);
        }

        ViewModel.SetWorkspaceStatus(tab, static t => t.VapourSynthRepairInserted);
    }

    private static TextBlock RepairText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static string FormatRepairTimestamp(int frame, VapourSynthPreviewOutputInfo output)
    {
        if (output.FpsNumerator <= 0)
        {
            return $"#{frame}";
        }

        var time = TimeSpan.FromSeconds((double)frame * output.FpsDenominator / output.FpsNumerator);
        return FormattableString.Invariant($"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }
}

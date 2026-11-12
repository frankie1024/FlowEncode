using System;
using System.Linq;
using System.Numerics;
using FlowEncode.Controls.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.Controls.Dashboard;

public sealed partial class DashboardView : UserControl
{
    private const int DashboardCardCount = 6;
    private const float FallbackHeroElevationZ = 8f;
    private double _lastWidth;
    private bool _heroElevationThemeHookAttached;

    internal IDashboardViewHost? Host { get; set; }

    public DashboardView()
    {
        InitializeComponent();
        SizeChanged += DashboardView_SizeChanged;
        DashboardScroller.SizeChanged += DashboardScroller_SizeChanged;
        DashboardCardGrid.SizeChanged += (_, _) => UpdateCardDensity();
        Loaded += DashboardView_Loaded;
    }

    private void DashboardView_Loaded(object sender, RoutedEventArgs e)
    {
        // Re-apply on every load in case the theme changed while this view was detached.
        ApplyHeroElevation();

        if (_heroElevationThemeHookAttached)
        {
            return;
        }

        _heroElevationThemeHookAttached = true;
        ActualThemeChanged += (_, _) => ApplyHeroElevation();
    }

    private void ApplyHeroElevation()
    {
        var z = UiMotionPolicy.IsHighContrastEnabled() ? 0f : FallbackHeroElevationZ;
        if (UiTokens.TryGetThemeResource(DashboardHeroCard, "ThemeElevationZ_E1", out var resource)
            && resource is double resourceZ)
        {
            z = (float)resourceZ;
        }

        DashboardHeroCard.Translation = new Vector3(0f, 0f, z);
    }

    public void ApplyLayout(double width, Thickness contentPadding)
    {
        if (width <= 0)
        {
            return;
        }

        _lastWidth = width;
        DashboardContentStack.Padding = contentPadding;
        DashboardContentStack.RowSpacing = UiTokens.SpacingL;
        DashboardHeroCard.Padding = UiTokens.CardPadding;
        DashboardHeroCard.MinHeight = 104;
        DashboardHeaderTitle.FontSize = width < 1100 ? UiTokens.DisplayFontSizeCompact : UiTokens.DisplayFontSize;
        DashboardHeaderPanel.MaxWidth = width < 1100 ? 720 : 960;
        DashboardHeaderPanel.Margin = new Thickness(0);
        DashboardHeroGrid.ColumnSpacing = width < 1100 ? UiTokens.SpacingL : UiTokens.SpacingXL;

        var dashboardHeroIconSize = width < 1100 ? 72 : 80;
        DashboardHeroIconFrame.Width = dashboardHeroIconSize;
        DashboardHeroIconFrame.Height = dashboardHeroIconSize;
        DashboardHeroIconFrame.Visibility = width < 760 ? Visibility.Collapsed : Visibility.Visible;

        ApplyDashboardCardLayout(width);
    }

    private void DashboardView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshCardLayout();
    }

    private void DashboardScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshCardLayout();
    }

    private void RefreshCardLayout()
    {
        if (_lastWidth > 0)
        {
            ApplyDashboardCardLayout(_lastWidth);
        }
    }

    private void ApplyDashboardCardLayout(double width)
    {
        var columnCount = width >= 960
            ? 3
            : width >= 700
                ? 2
                : 1;
        var rowCount = (int)Math.Ceiling((double)DashboardCardCount / columnCount);
        var rowSpacing = UiTokens.SpacingL;

        DashboardCardGrid.ColumnSpacing = columnCount == 1 ? 0 : rowSpacing;
        DashboardCardGrid.RowSpacing = rowSpacing;
        if (DashboardCardGrid.ColumnDefinitions.Count != columnCount)
        {
            DashboardCardGrid.ColumnDefinitions.Clear();
            DashboardCardGrid.ColumnDefinitions.Add(DashboardPrimaryColumn);
            if (columnCount >= 2) DashboardCardGrid.ColumnDefinitions.Add(DashboardSecondaryColumn);
            if (columnCount >= 3) DashboardCardGrid.ColumnDefinitions.Add(DashboardTertiaryColumn);
        }
        DashboardPrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        DashboardSecondaryColumn.Width = columnCount >= 2
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        DashboardTertiaryColumn.Width = columnCount >= 3
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        var cardHeight = columnCount >= 3 ? 176 : 164;
        DashboardDemuxButton.MinHeight = cardHeight;
        DashboardVapourSynthButton.MinHeight = cardHeight;
        DashboardVideoEncodeButton.MinHeight = cardHeight;
        DashboardAudioButton.MinHeight = cardHeight;
        DashboardAutoCompressionButton.MinHeight = cardHeight;
        DashboardSettingsButton.MinHeight = cardHeight;

        // The Grid divides the current viewport between its star rows. Narrow
        // layouts retain their natural content height and scroll instead.
        DashboardContentStack.MinHeight = columnCount > 1 ? Math.Floor(DashboardScroller.ActualHeight) : 0;
        ConfigureDashboardRows(rowCount, columnCount > 1);

        ArrangeDashboardCard(DashboardDemuxButton, 0, columnCount);
        ArrangeDashboardCard(DashboardVapourSynthButton, 1, columnCount);
        ArrangeDashboardCard(DashboardVideoEncodeButton, 2, columnCount);
        ArrangeDashboardCard(DashboardAudioButton, 3, columnCount);
        ArrangeDashboardCard(DashboardAutoCompressionButton, 4, columnCount);
        ArrangeDashboardCard(DashboardSettingsButton, 5, columnCount);
        UpdateCardDensity();
    }

    private static void ArrangeDashboardCard(FrameworkElement card, int index, int columnCount)
    {
        Grid.SetRow(card, index / columnCount);
        Grid.SetColumn(card, index % columnCount);
    }

    private void UpdateCardDensity()
    {
        foreach (var card in new[] { DashboardDemuxButton, DashboardVapourSynthButton, DashboardVideoEncodeButton,
                     DashboardAudioButton, DashboardAutoCompressionButton, DashboardSettingsButton })
        {
            if (card.Content is not Grid content || card.ActualWidth <= 0 || card.ActualHeight <= 0) continue;

            var scale = Math.Clamp(Math.Min(card.ActualWidth / 360, card.ActualHeight / 220), 1, 1.6);
            var textScale = Math.Sqrt(scale);
            card.Padding = new Thickness(UiTokens.CardPadding.Left * scale);
            content.RowSpacing = UiTokens.SpacingM * scale;
            // Keep the icon/title group balanced within the card as it grows.
            content.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            content.RowDefinitions[1].Height = GridLength.Auto;
            content.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);

            var iconHost = content.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetRow(child) == 0);
            if (iconHost is not null)
            {
                iconHost.VerticalAlignment = VerticalAlignment.Bottom;
                var icon = iconHost as Viewbox ?? (iconHost as Grid)?.Children.OfType<Viewbox>().FirstOrDefault();
                if (icon is not null) icon.Width = icon.Height = 28 * scale;
            }
            var description = content.Children.OfType<StackPanel>().FirstOrDefault(child => Grid.GetRow(child) == 1);
            if (description is not null)
            {
                description.Spacing = UiTokens.SpacingS * scale;
                var text = description.Children.OfType<TextBlock>().ToArray();
                if (text.Length > 0) text[0].FontSize = 22 * textScale;
                if (text.Length > 1) text[1].FontSize = 14 * textScale;
            }
            var footer = content.Children.OfType<FrameworkElement>().FirstOrDefault(child => Grid.GetRow(child) == 2);
            if (footer is not null) footer.VerticalAlignment = VerticalAlignment.Bottom;
        }
    }

    private void ConfigureDashboardRows(int visibleRows, bool stretch)
    {
        var rowHeights = new[]
        {
            DashboardRow0,
            DashboardRow1,
            DashboardRow2,
            DashboardRow3,
            DashboardRow4,
            DashboardRow5
        };

        // Zero-height definitions still contribute Grid.RowSpacing in WinUI.
        if (DashboardCardGrid.RowDefinitions.Count != visibleRows)
        {
            DashboardCardGrid.RowDefinitions.Clear();
            for (var index = 0; index < visibleRows; index++)
            {
                DashboardCardGrid.RowDefinitions.Add(rowHeights[index]);
            }
        }

        for (var index = 0; index < rowHeights.Length; index++)
        {
            rowHeights[index].Height = index < visibleRows
                ? stretch
                    ? new GridLength(1, GridUnitType.Star)
                    : GridLength.Auto
                : new GridLength(0);
        }
    }

    private void DashboardCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            Host?.NavigateToShellSection(tag);
        }
    }
}

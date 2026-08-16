using System;
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
        DashboardContentStack.Spacing = width < 1100 ? UiTokens.SpacingXL : UiTokens.SpacingXXL;
        DashboardHeroCard.Padding = UiTokens.CardPadding;
        DashboardHeroCard.MinHeight = width < 1100 ? 132 : 148;
        DashboardHeaderTitle.FontSize = width < 1100 ? UiTokens.DisplayFontSizeCompact : UiTokens.DisplayFontSize;
        DashboardHeaderPanel.MaxWidth = width < 1100 ? 720 : 960;
        DashboardHeaderPanel.Margin = new Thickness(GetDashboardHeroTextOffset(width), 0, 0, 0);
        DashboardHeroGrid.ColumnSpacing = width < 1100 ? UiTokens.SpacingL : UiTokens.SpacingXL;

        var dashboardHeroIconSize = width < 1100 ? 108 : 136;
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

    private static double GetDashboardHeroTextOffset(double width)
    {
        if (width < 1100)
        {
            return 0;
        }

        return Math.Clamp((width - 1100) * 0.12, 0, 120);
    }

    private void ApplyDashboardCardLayout(double width)
    {
        var columnCount = width >= 1180
            ? 3
            : width >= 700
                ? 2
                : 1;
        var rowCount = (int)Math.Ceiling((double)DashboardCardCount / columnCount);
        var rowSpacing = width < 1100 ? UiTokens.SpacingL : UiTokens.SpacingXL;

        DashboardCardGrid.ColumnSpacing = columnCount == 1 ? 0 : rowSpacing;
        DashboardCardGrid.RowSpacing = rowSpacing;
        DashboardPrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        DashboardSecondaryColumn.Width = columnCount >= 2
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        DashboardTertiaryColumn.Width = columnCount >= 3
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        var cardHeight = width < 1100 ? 216 : 248;
        DashboardDemuxButton.MinHeight = cardHeight;
        DashboardVapourSynthButton.MinHeight = cardHeight;
        DashboardVideoEncodeButton.MinHeight = cardHeight;
        DashboardAudioButton.MinHeight = cardHeight;
        DashboardAutoCompressionButton.MinHeight = cardHeight;
        DashboardSettingsButton.MinHeight = cardHeight;

        var stretchedCardGridHeight = ResolveDashboardCardGridHeight(rowCount, rowSpacing, cardHeight, columnCount, width);
        DashboardCardGrid.Height = stretchedCardGridHeight ?? double.NaN;
        ConfigureDashboardRows(rowCount, stretchedCardGridHeight.HasValue);

        ArrangeDashboardCard(DashboardDemuxButton, 0, columnCount);
        ArrangeDashboardCard(DashboardVapourSynthButton, 1, columnCount);
        ArrangeDashboardCard(DashboardVideoEncodeButton, 2, columnCount);
        ArrangeDashboardCard(DashboardAudioButton, 3, columnCount);
        ArrangeDashboardCard(DashboardAutoCompressionButton, 4, columnCount);
        ArrangeDashboardCard(DashboardSettingsButton, 5, columnCount);
    }

    private static void ArrangeDashboardCard(FrameworkElement card, int index, int columnCount)
    {
        Grid.SetRow(card, index / columnCount);
        Grid.SetColumn(card, index % columnCount);
    }

    private double? ResolveDashboardCardGridHeight(
        int rowCount,
        double rowSpacing,
        double cardMinHeight,
        int columnCount,
        double width)
    {
        if (columnCount == 1 || DashboardScroller.ActualHeight <= 0 || DashboardHeroCard.ActualHeight <= 0)
        {
            return null;
        }

        var verticalChrome = DashboardContentStack.Padding.Top
            + DashboardContentStack.Padding.Bottom
            + DashboardHeroCard.ActualHeight
            + DashboardContentStack.Spacing;
        var availableHeight = DashboardScroller.ActualHeight - verticalChrome;
        if (availableHeight <= 0)
        {
            return null;
        }

        var minimumGridHeight = (rowCount * cardMinHeight) + ((rowCount - 1) * rowSpacing);
        if (availableHeight <= minimumGridHeight)
        {
            return null;
        }

        var maxRowHeight = columnCount >= 3
            ? width >= 1600 ? 328.0 : 304.0
            : 276.0;
        var maximumGridHeight = (rowCount * maxRowHeight) + ((rowCount - 1) * rowSpacing);

        return Math.Min(availableHeight, maximumGridHeight);
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

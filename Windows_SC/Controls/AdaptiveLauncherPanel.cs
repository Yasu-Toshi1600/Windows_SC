using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows_SC.ViewModels;

namespace Windows_SC.Controls;

public sealed class AdaptiveLauncherPanel : Panel
{
    private const double CellWidth = 110;

    protected override Size MeasureOverride(Size availableSize)
    {
        double availableWidth = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : CellWidth * 4;
        int columnCapacity = Math.Max(1, (int)Math.Floor(availableWidth / CellWidth));
        double currentRowHeight = 0;
        double desiredHeight = 0;
        int usedColumns = 0;

        foreach (UIElement child in Children)
        {
            LauncherItemViewModel? item = (child as FrameworkElement)?.DataContext
                as LauncherItemViewModel;
            int span = Math.Clamp(item?.LayoutColumnSpan ?? 2, 1, columnCapacity);
            double height = item?.TileHeight ?? 160;

            if (usedColumns > 0 && usedColumns + span > columnCapacity)
            {
                desiredHeight += currentRowHeight;
                currentRowHeight = 0;
                usedColumns = 0;
            }

            child.Measure(new Size(CellWidth * span, height));
            currentRowHeight = Math.Max(currentRowHeight, height);
            usedColumns += span;
        }

        desiredHeight += currentRowHeight;
        return new Size(Math.Min(availableWidth, columnCapacity * CellWidth), desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columnCapacity = Math.Max(1, (int)Math.Floor(finalSize.Width / CellWidth));
        double x = 0;
        double y = 0;
        double currentRowHeight = 0;
        int usedColumns = 0;

        foreach (UIElement child in Children)
        {
            LauncherItemViewModel? item = (child as FrameworkElement)?.DataContext
                as LauncherItemViewModel;
            int span = Math.Clamp(item?.LayoutColumnSpan ?? 2, 1, columnCapacity);
            double height = item?.TileHeight ?? 160;

            if (usedColumns > 0 && usedColumns + span > columnCapacity)
            {
                y += currentRowHeight;
                x = 0;
                currentRowHeight = 0;
                usedColumns = 0;
            }

            double width = CellWidth * span;
            child.Arrange(new Rect(x, y, width, height));
            x += width;
            currentRowHeight = Math.Max(currentRowHeight, height);
            usedColumns += span;
        }

        return finalSize;
    }
}

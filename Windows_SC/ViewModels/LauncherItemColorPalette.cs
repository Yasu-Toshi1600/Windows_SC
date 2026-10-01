using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows_SC.Models;

namespace Windows_SC.ViewModels;

internal static class LauncherItemColorPalette
{
    private static readonly SolidColorBrush DarkForegroundBrush =
        new(ColorHelper.FromArgb(0xFF, 0x1F, 0x1F, 0x1F));

    public static SolidColorBrush CreateBackgroundBrush(LauncherItemBackgroundColor color) =>
        color switch
        {
            LauncherItemBackgroundColor.PaleGreen => CreateBrush(0xF0, 0xF9, 0xF1),
            LauncherItemBackgroundColor.PaleYellow => CreateBrush(0xFF, 0xFA, 0xE7),
            LauncherItemBackgroundColor.PaleOrange => CreateBrush(0xFF, 0xF3, 0xEA),
            LauncherItemBackgroundColor.PalePink => CreateBrush(0xFC, 0xF0, 0xF2),
            LauncherItemBackgroundColor.PalePurple => CreateBrush(0xF6, 0xF0, 0xFC),
            LauncherItemBackgroundColor.PaleCyan => CreateBrush(0xF0, 0xFA, 0xFB),
            LauncherItemBackgroundColor.PaleGray => CreateBrush(0xF5, 0xF5, 0xF6),
            _ => CreateBrush(0xF1, 0xF6, 0xFE)
        };

    public static SolidColorBrush CreateBorderBrush(LauncherItemBackgroundColor color) =>
        color switch
        {
            LauncherItemBackgroundColor.PaleGreen => CreateBrush(0xCD, 0xEC, 0xCF),
            LauncherItemBackgroundColor.PaleYellow => CreateBrush(0xFF, 0xF0, 0xB3),
            LauncherItemBackgroundColor.PaleOrange => CreateBrush(0xFF, 0xDC, 0xC2),
            LauncherItemBackgroundColor.PalePink => CreateBrush(0xF7, 0xD1, 0xD9),
            LauncherItemBackgroundColor.PalePurple => CreateBrush(0xE6, 0xD5, 0xF7),
            LauncherItemBackgroundColor.PaleCyan => CreateBrush(0xCD, 0xEF, 0xF2),
            LauncherItemBackgroundColor.PaleGray => CreateBrush(0xE2, 0xE3, 0xE5),
            _ => CreateBrush(0xD3, 0xE3, 0xFD)
        };

    public static SolidColorBrush ForegroundBrush => DarkForegroundBrush;

    public static string GetHexCode(LauncherItemBackgroundColor color) => color switch
    {
        LauncherItemBackgroundColor.PaleGreen => "#CDECCF",
        LauncherItemBackgroundColor.PaleYellow => "#FFF0B3",
        LauncherItemBackgroundColor.PaleOrange => "#FFDCC2",
        LauncherItemBackgroundColor.PalePink => "#F7D1D9",
        LauncherItemBackgroundColor.PalePurple => "#E6D5F7",
        LauncherItemBackgroundColor.PaleCyan => "#CDEFF2",
        LauncherItemBackgroundColor.PaleGray => "#E2E3E5",
        _ => "#D3E3FD"
    };

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue) =>
        new(ColorHelper.FromArgb(0xFF, red, green, blue));
}

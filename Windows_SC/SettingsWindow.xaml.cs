using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows_SC.ViewModels;
using WinRT.Interop;

namespace Windows_SC;

public sealed partial class SettingsWindow : Window
{
    internal SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        RootGrid.DataContext = viewModel;

        WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(900, 620));
    }
}

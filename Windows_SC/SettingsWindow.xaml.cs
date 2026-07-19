using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows_SC.ViewModels;
using WinRT.Interop;

namespace Windows_SC;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    internal SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        RootGrid.DataContext = viewModel;

        WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(900, 620));
    }

    private void AudioDeviceMoveUp_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: RegisteredAudioDeviceEditorViewModel device })
        {
            _viewModel.MoveAudioDevice(device, -1);
        }
    }

    private void AudioDeviceMoveDown_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: RegisteredAudioDeviceEditorViewModel device })
        {
            _viewModel.MoveAudioDevice(device, 1);
        }
    }

    private void AudioDeviceRemove_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: RegisteredAudioDeviceEditorViewModel device })
        {
            _viewModel.RemoveAudioDevice(device);
        }
    }
}

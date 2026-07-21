using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
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
        TroubleshootingDialog.DataContext = viewModel;

        nint windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        ResizeAndCenter(appWindow, windowId, windowHandle);
    }

    private void LauncherItemMoveUp_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: LauncherItemEditorViewModel item })
        {
            _viewModel.MoveItem(item, -1);
            KeepItemVisible(LauncherItemsList, item);
        }
    }

    private void LauncherItemMoveDown_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: LauncherItemEditorViewModel item })
        {
            _viewModel.MoveItem(item, 1);
            KeepItemVisible(LauncherItemsList, item);
        }
    }

    private void LauncherItemRemove_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: LauncherItemEditorViewModel item })
        {
            _viewModel.RemoveItem(item);
        }
    }

    private void AudioDeviceMoveUp_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: RegisteredAudioDeviceEditorViewModel device })
        {
            _viewModel.MoveAudioDevice(device, -1);
            KeepItemVisible(AudioDeviceList, device);
        }
    }

    private void AudioDeviceMoveDown_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: RegisteredAudioDeviceEditorViewModel device })
        {
            _viewModel.MoveAudioDevice(device, 1);
            KeepItemVisible(AudioDeviceList, device);
        }
    }

    private void AudioDeviceRemove_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: RegisteredAudioDeviceEditorViewModel device })
        {
            _viewModel.RemoveAudioDevice(device);
        }
    }

    private void CommandStepMoveUp_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: CommandCycleStepEditorViewModel step })
        {
            _viewModel.MoveCommandStep(step, -1);
            KeepItemVisible(CommandStepList, step);
        }
    }

    private void CommandStepMoveDown_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: CommandCycleStepEditorViewModel step })
        {
            _viewModel.MoveCommandStep(step, 1);
            KeepItemVisible(CommandStepList, step);
        }
    }

    private void CommandStepRemove_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: CommandCycleStepEditorViewModel step })
        {
            _viewModel.RemoveCommandStep(step);
        }
    }

    private async void TroubleshootingButton_Click(object sender, RoutedEventArgs args)
    {
        TroubleshootingDialog.XamlRoot = RootGrid.XamlRoot;
        await TroubleshootingDialog.ShowAsync();
    }

    private void KeepItemVisible(ListView listView, object item)
    {
        listView.SelectedItem = item;
        DispatcherQueue.TryEnqueue(() => listView.ScrollIntoView(item));
    }

    private static void ResizeAndCenter(AppWindow appWindow, WindowId windowId, nint windowHandle)
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        RectInt32 workArea = displayArea.WorkArea;
        double scale = Math.Max(GetDpiForWindow(windowHandle), 96) / 96d;
        int margin = (int)Math.Round(48 * scale);
        int width = Math.Min(
            (int)Math.Round(1180 * scale),
            Math.Max(1, workArea.Width - margin));
        int height = Math.Min(
            (int)Math.Round(800 * scale),
            Math.Max(1, workArea.Height - margin));
        int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    private void OpenLogFolder_Click(object sender, RoutedEventArgs args) =>
        _viewModel.OpenLogFolder();

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs args) =>
        _viewModel.OpenSettingsFolder();

    private void OpenDataFolder_Click(object sender, RoutedEventArgs args) =>
        _viewModel.OpenDataFolder();

    private async void ApplyDetailedDiagnostics_Click(object sender, RoutedEventArgs args) =>
        await _viewModel.ApplyDetailedDiagnosticsAsync();

    private void CopyEnvironmentInfo_Click(object sender, RoutedEventArgs args)
    {
        DataPackage package = new();
        package.SetText(_viewModel.CreateEnvironmentInformation());
        Clipboard.SetContent(package);
        _viewModel.ReportEnvironmentInformationCopied();
    }

    private async void DeleteLogs_Click(object sender, RoutedEventArgs args)
    {
        TroubleshootingDialog.Hide();
        ContentDialog confirmation = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "ログを削除しますか？",
            Content = "通常ログ、詳細ログ、ローテーション済みログを削除します。この操作は元に戻せません。",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirmation.ShowAsync() == ContentDialogResult.Primary)
        {
            _viewModel.ClearLogs();
        }
    }
}

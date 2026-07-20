using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
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

        WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(1040, 720));
    }

    private void LauncherItemMoveUp_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: LauncherItemEditorViewModel item })
        {
            _viewModel.MoveItem(item, -1);
        }
    }

    private void LauncherItemMoveDown_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: LauncherItemEditorViewModel item })
        {
            _viewModel.MoveItem(item, 1);
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

    private void CommandStepMoveUp_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: CommandCycleStepEditorViewModel step })
        {
            _viewModel.MoveCommandStep(step, -1);
        }
    }

    private void CommandStepMoveDown_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: CommandCycleStepEditorViewModel step })
        {
            _viewModel.MoveCommandStep(step, 1);
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

    private void OpenLogFolder_Click(object sender, RoutedEventArgs args) =>
        _viewModel.OpenLogFolder();

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

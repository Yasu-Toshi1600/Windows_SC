using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows_SC.ViewModels;
using Windows_SC.Models;
using Windows_SC.Services;
using WinRT.Interop;

namespace Windows_SC;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _isDeleteLogsFlowActive;

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

    private void RecordShortcutKey_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.BeginShortcutKeyRecording();
        if (sender is Button button)
        {
            button.Focus(FocusState.Programmatic);
        }
    }

    private void MacroStepMoveUp_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: MacroStepEditorViewModel step })
        {
            _viewModel.MoveMacroStep(step, -1);
            KeepItemVisible(MacroStepList, step);
        }
    }

    private void MacroStepMoveDown_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: MacroStepEditorViewModel step })
        {
            _viewModel.MoveMacroStep(step, 1);
            KeepItemVisible(MacroStepList, step);
        }
    }

    private void MacroStepRemove_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: MacroStepEditorViewModel step })
        {
            _viewModel.RemoveMacroStep(step);
        }
    }

    private void ClearShortcutKey_Click(object sender, RoutedEventArgs args) =>
        _viewModel.ClearShortcutKey();

    private void RecordMacroShortcutKey_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.BeginMacroShortcutKeyRecording();
        if (sender is Button button)
        {
            button.Focus(FocusState.Programmatic);
        }
    }

    private void ClearMacroShortcutKey_Click(object sender, RoutedEventArgs args) =>
        _viewModel.ClearMacroShortcutKey();

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_viewModel.IsRecordingShortcutKey)
        {
            return;
        }

        args.Handled = true;
        uint virtualKey = (uint)args.Key;
        if (virtualKey == 0x1B)
        {
            _viewModel.CancelShortcutKeyRecording();
            return;
        }

        if (ShortcutKeyText.IsModifierKey(virtualKey))
        {
            return;
        }

        ShortcutKeyModifiers modifiers = ShortcutKeyModifiers.None;
        if (IsKeyDown(0x11))
        {
            modifiers |= ShortcutKeyModifiers.Control;
        }

        if (IsKeyDown(0x12))
        {
            modifiers |= ShortcutKeyModifiers.Alt;
        }

        if (IsKeyDown(0x10))
        {
            modifiers |= ShortcutKeyModifiers.Shift;
        }

        if (IsKeyDown(0x5B) || IsKeyDown(0x5C))
        {
            modifiers |= ShortcutKeyModifiers.Windows;
        }

        _viewModel.CompleteShortcutKeyRecording(new ShortcutKeyDefinition
        {
            Modifiers = modifiers,
            VirtualKey = virtualKey,
            ScanCode = args.KeyStatus.ScanCode,
            IsExtendedKey = args.KeyStatus.IsExtendedKey
        });
    }

    private static bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private async void SelectTargetFile_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedItem is not { IsButton: true } selectedItem)
        {
            return;
        }

        try
        {
            FileOpenPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
                SettingsIdentifier = "LauncherTargetFile"
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                selectedItem.Target = file.Path;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or UnauthorizedAccessException
            or IOException
            or COMException)
        {
            _viewModel.ReportTargetSelectionFailed("file", exception);
        }
    }

    private async void SelectTargetFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedItem is not { IsButton: true } selectedItem)
        {
            return;
        }

        try
        {
            FolderPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                ViewMode = PickerViewMode.List,
                SettingsIdentifier = "LauncherTargetFolder"
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                selectedItem.Target = folder.Path;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or UnauthorizedAccessException
            or IOException
            or COMException)
        {
            _viewModel.ReportTargetSelectionFailed("folder", exception);
        }
    }

    private async void TroubleshootingButton_Click(object sender, RoutedEventArgs args)
    {
        _viewModel.RefreshEnvironmentInformationLog();
        TroubleshootingScrollViewer.MaxHeight = Math.Max(
            280,
            RootGrid.ActualHeight - 180);
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

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
        if (_isDeleteLogsFlowActive)
        {
            return;
        }

        _isDeleteLogsFlowActive = true;
        try
        {
            System.Threading.Tasks.TaskCompletionSource<bool> closed = new(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

            void TroubleshootingDialog_Closed(
                ContentDialog sender,
                ContentDialogClosedEventArgs args)
            {
                TroubleshootingDialog.Closed -= TroubleshootingDialog_Closed;
                closed.TrySetResult(true);
            }

            TroubleshootingDialog.Closed += TroubleshootingDialog_Closed;
            TroubleshootingDialog.Hide();
            await closed.Task;

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
        finally
        {
            _isDeleteLogsFlowActive = false;
        }
    }
}

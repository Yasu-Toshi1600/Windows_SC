using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IStartupService _startupService;
    private readonly IAudioOutputService _audioOutputService;
    private readonly DiagnosticLogger _logger;
    private readonly Guid _pageId;
    private LauncherItemEditorViewModel? _selectedItem;
    private ActionKindOption? _selectedActionKind;
    private CycleKindOption? _selectedCycleKind;
    private AudioOutputDeviceOption? _audioDeviceToAdd;
    private RegisteredAudioDeviceEditorViewModel? _selectedRegisteredAudioDevice;
    private CommandCycleStepEditorViewModel? _selectedCommandStep;
    private bool _assumePhonePanelVisible;
    private bool _startWithWindows;
    private LayoutModeOption? _selectedLayoutMode;
    private string _statusMessage = string.Empty;
    private bool _isSaving;
    private bool _isDetailedDiagnosticsEnabled;
    private DateTimeOffset? _detailedLoggingExpiresAt;
    private bool _isApplyingDiagnosticsSetting;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        MainWindowViewModel mainWindowViewModel,
        IStartupService startupService,
        IAudioOutputService audioOutputService,
        DiagnosticLogger logger)
    {
        _settingsRepository = settingsRepository;
        _mainWindowViewModel = mainWindowViewModel;
        _startupService = startupService;
        _audioOutputService = audioOutputService;
        _logger = logger;

        foreach (AudioOutputDevice device in _audioOutputService.GetCachedDevices())
        {
            AvailableAudioOutputDevices.Add(new AudioOutputDeviceOption(
                device.Id,
                device.DisplayName,
                device.IsAvailable));
        }

        LauncherSettings settings = mainWindowViewModel.ExportSettings();
        _assumePhonePanelVisible = settings.AssumePhonePanelVisible;
        _startWithWindows = settings.StartWithWindows;
        _detailedLoggingExpiresAt = settings.DetailedLoggingExpiresAtUtc;
        _isDetailedDiagnosticsEnabled =
            _detailedLoggingExpiresAt is { } expiration && expiration > DateTimeOffset.UtcNow;
        _selectedLayoutMode = LayoutModes.First(option => option.Value == settings.LayoutMode);
        LauncherPageDefinition page = settings.Pages.FirstOrDefault()
            ?? new LauncherPageDefinition { Name = "メイン" };
        _pageId = page.Id;

        foreach (LauncherItemDefinition item in page.Items)
        {
            Items.Add(new LauncherItemEditorViewModel(item, AvailableAudioOutputDevices));
        }

        AddButtonCommand = new RelayCommand(() => AddItem(LauncherItemKind.Button));
        AddToggleCommand = new RelayCommand(() => AddItem(LauncherItemKind.Toggle));
        AddSliderCommand = new RelayCommand(() => AddItem(LauncherItemKind.Slider));
        DeleteItemCommand = new RelayCommand(DeleteSelectedItem, () => SelectedItem is not null);
        AddAudioDeviceCommand = new RelayCommand(AddAudioDevice, CanAddAudioDevice);
        AddCommandStepCommand = new RelayCommand(
            AddCommandStep,
            () => SelectedItem is { IsToggle: true, CycleKind: CycleActionKind.Commands });
        RemoveAudioDeviceCommand = new RelayCommand(
            RemoveAudioDevice,
            () => SelectedItem?.IsToggle == true && SelectedRegisteredAudioDevice is not null);
        MoveAudioDeviceUpCommand = new RelayCommand(
            () => MoveAudioDevice(-1),
            () => CanMoveAudioDevice(-1));
        MoveAudioDeviceDownCommand = new RelayCommand(
            () => MoveAudioDevice(1),
            () => CanMoveAudioDevice(1));
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !_isSaving);
        ExitApplicationCommand = new RelayCommand(
            () => ExitApplicationRequested?.Invoke(this, EventArgs.Empty));
        SelectedItem = Items.FirstOrDefault();
    }

    public event EventHandler? ExitApplicationRequested;

    public ObservableCollection<LauncherItemEditorViewModel> Items { get; } = [];

    public ObservableCollection<AudioOutputDeviceOption> AvailableAudioOutputDevices { get; } = [];

    public IReadOnlyList<ActionKindOption> ActionKinds { get; } =
    [
        new(
            LauncherActionKind.Application,
            "アプリ・ファイル・URLを開く"),
        new(
            LauncherActionKind.Command,
            "コマンドを実行")
    ];

    public IReadOnlyList<CycleKindOption> CycleKinds { get; } =
    [
        new(CycleActionKind.AudioOutput, "音声出力"),
        new(CycleActionKind.Commands, "コマンド")
    ];

    public IReadOnlyList<LayoutModeOption> LayoutModes { get; } =
    [
        new(LauncherLayoutMode.Standard, "標準（最大2列）"),
        new(LauncherLayoutMode.Compact, "コンパクト（最大4列）")
    ];

    public IReadOnlyList<PostExecutionBehaviorOption> PostExecutionBehaviors { get; } =
    [
        new(LauncherPostExecutionBehavior.CloseOnSuccess, "成功後に閉じる"),
        new(LauncherPostExecutionBehavior.KeepOpen, "表示したまま")
    ];

    public LauncherItemEditorViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                SelectedActionKind = value is null
                    ? null
                    : GetVisibleActionKind(value.ActionKind);
                SelectedCycleKind = value is { IsToggle: true }
                    ? CycleKinds.First(option => option.Value == value.CycleKind)
                    : null;
                SelectedRegisteredAudioDevice = null;
                SelectedCommandStep = value?.CommandSteps.FirstOrDefault();
                AudioDeviceToAdd = AvailableAudioOutputDevices.FirstOrDefault(device =>
                    value?.RegisteredAudioDevices.All(registered =>
                        !string.Equals(
                            registered.Id,
                            device.Id,
                            StringComparison.OrdinalIgnoreCase)) == true);
                DeleteItemCommand.NotifyCanExecuteChanged();
                NotifyAudioDeviceCommands();
                AddCommandStepCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ActionKindOption? SelectedActionKind
    {
        get => _selectedActionKind;
        set
        {
            if (SetProperty(ref _selectedActionKind, value)
                && value is not null
                && SelectedItem is { IsButton: true } selectedItem)
            {
                selectedItem.ActionKind = value.Value;
            }
        }
    }

    public CycleKindOption? SelectedCycleKind
    {
        get => _selectedCycleKind;
        set
        {
            if (SetProperty(ref _selectedCycleKind, value)
                && value is not null
                && SelectedItem is { IsToggle: true } selectedItem)
            {
                selectedItem.CycleKind = value.Value;
                SelectedCommandStep = selectedItem.CommandSteps.FirstOrDefault();
                AddCommandStepCommand.NotifyCanExecuteChanged();
                NotifyAudioDeviceCommands();
            }
        }
    }

    public AudioOutputDeviceOption? AudioDeviceToAdd
    {
        get => _audioDeviceToAdd;
        set
        {
            if (SetProperty(ref _audioDeviceToAdd, value))
            {
                AddAudioDeviceCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public RegisteredAudioDeviceEditorViewModel? SelectedRegisteredAudioDevice
    {
        get => _selectedRegisteredAudioDevice;
        set
        {
            if (SetProperty(ref _selectedRegisteredAudioDevice, value))
            {
                NotifyAudioDeviceCommands();
            }
        }
    }

    public CommandCycleStepEditorViewModel? SelectedCommandStep
    {
        get => _selectedCommandStep;
        set => SetProperty(ref _selectedCommandStep, value);
    }

    public bool AssumePhonePanelVisible
    {
        get => _assumePhonePanelVisible;
        set => SetProperty(ref _assumePhonePanelVisible, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public LayoutModeOption? SelectedLayoutMode
    {
        get => _selectedLayoutMode;
        set => SetProperty(ref _selectedLayoutMode, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsDetailedDiagnosticsEnabled
    {
        get => _isDetailedDiagnosticsEnabled;
        set
        {
            if (!SetProperty(ref _isDetailedDiagnosticsEnabled, value))
            {
                return;
            }

            _detailedLoggingExpiresAt = null;
            OnPropertyChanged(nameof(DetailedDiagnosticsStatus));
            StatusMessage = value
                ? "［適用］を押すと詳細診断ログが24時間有効になります。"
                : "［適用］を押すと詳細診断ログが無効になります。";
        }
    }

    public string DetailedDiagnosticsStatus
    {
        get
        {
            if (!IsDetailedDiagnosticsEnabled)
            {
                return "現在は無効です。通常ログのみ記録します。";
            }

            return _detailedLoggingExpiresAt is { } expiration
                && expiration > DateTimeOffset.UtcNow
                ? $"{expiration.ToLocalTime():yyyy/MM/dd HH:mm}まで有効です。"
                : "［適用］を押すと、その時点から24時間有効になります。";
        }
    }

    public string NormalLogPath => GetDisplayPath(_logger.LogFilePath);

    public string DetailedLogPath => GetDisplayPath(_logger.DetailedLogFilePath);

    public RelayCommand AddButtonCommand { get; }
    public RelayCommand AddToggleCommand { get; }
    public RelayCommand AddSliderCommand { get; }
    public RelayCommand DeleteItemCommand { get; }
    public RelayCommand AddAudioDeviceCommand { get; }
    public RelayCommand AddCommandStepCommand { get; }
    public RelayCommand RemoveAudioDeviceCommand { get; }
    public RelayCommand MoveAudioDeviceUpCommand { get; }
    public RelayCommand MoveAudioDeviceDownCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ExitApplicationCommand { get; }

    internal void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_logger.LogDirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = _logger.LogDirectoryPath,
                UseShellExecute = true
            });
            StatusMessage = "ログフォルダーを開きました。";
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException
            or UnauthorizedAccessException)
        {
            StatusMessage = $"ログフォルダーを開けませんでした: {exception.Message}";
        }
    }

    private static string GetDisplayPath(string path)
    {
        string localAppData = Environment
            .GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            .TrimEnd(Path.DirectorySeparatorChar);
        string localAppDataPrefix = localAppData + Path.DirectorySeparatorChar;
        return path.StartsWith(localAppDataPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"%LOCALAPPDATA%{Path.DirectorySeparatorChar}" + path[localAppDataPrefix.Length..]
            : path;
    }

    internal async System.Threading.Tasks.Task ApplyDetailedDiagnosticsAsync()
    {
        if (_isApplyingDiagnosticsSetting)
        {
            return;
        }

        _isApplyingDiagnosticsSetting = true;
        LauncherSettings settings = _mainWindowViewModel.ExportSettings();
        DateTimeOffset? previousExpiration = settings.DetailedLoggingExpiresAtUtc;
        DateTimeOffset? newExpiration = IsDetailedDiagnosticsEnabled
            ? DateTimeOffset.UtcNow.AddHours(24)
            : null;

        try
        {
            settings.DetailedLoggingExpiresAtUtc = newExpiration;
            await _settingsRepository.SaveAsync(settings);
            _detailedLoggingExpiresAt = newExpiration;
            _logger.ConfigureDetailedLogging(newExpiration);
            OnPropertyChanged(nameof(DetailedDiagnosticsStatus));
            StatusMessage = IsDetailedDiagnosticsEnabled
                ? "詳細診断ログを24時間有効にしました。"
                : "詳細診断ログを無効にしました。";
        }
        catch (Exception exception)
        {
            settings.DetailedLoggingExpiresAtUtc = previousExpiration;
            StatusMessage = $"詳細診断ログの設定を保存できませんでした: {exception.Message}";
        }
        finally
        {
            _isApplyingDiagnosticsSetting = false;
        }
    }

    internal string CreateEnvironmentInformation()
    {
        StringBuilder information = new();
        information.AppendLine("Windows_SC 診断情報");
        information.AppendLine($"作成日時: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        information.AppendLine($"アプリ: {typeof(SettingsViewModel).Assembly.GetName().Version}");
        information.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        information.AppendLine($"OSアーキテクチャ: {RuntimeInformation.OSArchitecture}");
        information.AppendLine($"プロセスアーキテクチャ: {RuntimeInformation.ProcessArchitecture}");
        information.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        AppendMonitorInformation(information);
        information.AppendLine(
            $"詳細診断ログ: {(_logger.IsDetailedLoggingEnabled ? "有効" : "無効")}");
        return information.ToString();
    }

    private static void AppendMonitorInformation(StringBuilder information)
    {
        IReadOnlyList<DisplayArea> displayAreas = DisplayArea.FindAll();
        information.AppendLine($"モニター数: {displayAreas.Count}");

        for (int index = 0; index < displayAreas.Count; index++)
        {
            DisplayArea displayArea = displayAreas[index];
            RectInt32 bounds = displayArea.OuterBounds;
            RectInt32 workArea = displayArea.WorkArea;
            (uint dpi, int refreshRate) = GetMonitorDetails(bounds);
            int scalePercent = (int)Math.Round(dpi * 100d / 96d);
            string refreshRateText = refreshRate > 1
                ? $", リフレッシュレート={refreshRate}Hz"
                : string.Empty;
            information.AppendLine(
                $"モニター {index + 1}: " +
                $"{(displayArea.IsPrimary ? "メイン, " : string.Empty)}" +
                $"解像度={bounds.Width}x{bounds.Height}, " +
                $"配置=({bounds.X},{bounds.Y}), " +
                $"作業領域={workArea.Width}x{workArea.Height}, " +
                $"DPI={dpi}, 拡大率={scalePercent}%" +
                refreshRateText);
        }
    }

    private static (uint Dpi, int RefreshRate) GetMonitorDetails(RectInt32 bounds)
    {
        NativePoint center = new()
        {
            X = bounds.X + (bounds.Width / 2),
            Y = bounds.Y + (bounds.Height / 2)
        };
        nint monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        uint dpi = monitor != nint.Zero
            && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0
                ? dpiX
                : 96;
        int refreshRate = 0;

        MonitorInfoEx monitorInfo = new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty
        };
        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            nint deviceContext = CreateDC(
                "DISPLAY",
                monitorInfo.DeviceName,
                null,
                nint.Zero);
            if (deviceContext != nint.Zero)
            {
                refreshRate = GetDeviceCaps(deviceContext, VerticalRefreshRate);
                _ = DeleteDC(deviceContext);
            }
        }

        return (dpi, refreshRate);
    }

    internal void ReportEnvironmentInformationCopied() =>
        StatusMessage = "環境情報をコピーしました。";

    internal void ClearLogs()
    {
        try
        {
            _logger.ClearLogs();
            StatusMessage = "ログを削除しました。";
        }
        catch (Exception exception) when (exception is System.IO.IOException
            or UnauthorizedAccessException)
        {
            StatusMessage = $"ログを削除できませんでした: {exception.Message}";
        }
    }

    private ActionKindOption GetVisibleActionKind(LauncherActionKind actionKind)
    {
        LauncherActionKind visibleKind = actionKind == LauncherActionKind.Command
            ? LauncherActionKind.Command
            : LauncherActionKind.Application;
        return ActionKinds.First(option => option.Value == visibleKind);
    }

    private void AddItem(LauncherItemKind kind)
    {
        string title = kind switch
        {
            LauncherItemKind.Toggle => "新しい循環切り替え",
            LauncherItemKind.Slider => "新しいスライダー",
            _ => "新しいショートカット"
        };
        LauncherItemEditorViewModel item = new(Guid.NewGuid(), kind, title);
        Items.Add(item);
        SelectedItem = item;
        StatusMessage = "未保存の変更があります。";
    }

    private bool CanAddAudioDevice() =>
        SelectedItem is { IsToggle: true, CycleKind: CycleActionKind.AudioOutput }
        && AudioDeviceToAdd is not null
        && SelectedItem.RegisteredAudioDevices.All(device =>
            !string.Equals(device.Id, AudioDeviceToAdd.Id, StringComparison.OrdinalIgnoreCase));

    private void AddAudioDevice()
    {
        if (!CanAddAudioDevice() || SelectedItem is null || AudioDeviceToAdd is null)
        {
            return;
        }

        RegisteredAudioDeviceEditorViewModel registered = new(
            AudioDeviceToAdd.Id,
            AudioDeviceToAdd.DisplayName,
            AudioDeviceToAdd.IsAvailable);
        SelectedItem.RegisteredAudioDevices.Add(registered);
        SelectedRegisteredAudioDevice = registered;
        AudioDeviceToAdd = AvailableAudioOutputDevices.FirstOrDefault(device =>
            SelectedItem.RegisteredAudioDevices.All(existing =>
                !string.Equals(existing.Id, device.Id, StringComparison.OrdinalIgnoreCase)));
        StatusMessage = "未保存の変更があります。";
        NotifyAudioDeviceCommands();
    }

    private void RemoveAudioDevice()
    {
        if (SelectedItem is null || SelectedRegisteredAudioDevice is null)
        {
            return;
        }

        int index = SelectedItem.RegisteredAudioDevices.IndexOf(SelectedRegisteredAudioDevice);
        SelectedItem.RegisteredAudioDevices.Remove(SelectedRegisteredAudioDevice);
        SelectedRegisteredAudioDevice = SelectedItem.RegisteredAudioDevices.Count == 0
            ? null
            : SelectedItem.RegisteredAudioDevices[Math.Min(
                index,
                SelectedItem.RegisteredAudioDevices.Count - 1)];
        AudioDeviceToAdd ??= AvailableAudioOutputDevices.FirstOrDefault(device =>
            SelectedItem.RegisteredAudioDevices.All(existing =>
                !string.Equals(existing.Id, device.Id, StringComparison.OrdinalIgnoreCase)));
        StatusMessage = "未保存の変更があります。";
        NotifyAudioDeviceCommands();
    }

    internal void RemoveAudioDevice(RegisteredAudioDeviceEditorViewModel device)
    {
        SelectedRegisteredAudioDevice = device;
        RemoveAudioDevice();
    }

    private bool CanMoveAudioDevice(int offset)
    {
        if (SelectedItem is null || SelectedRegisteredAudioDevice is null)
        {
            return false;
        }

        int currentIndex = SelectedItem.RegisteredAudioDevices.IndexOf(SelectedRegisteredAudioDevice);
        int targetIndex = currentIndex + offset;
        return currentIndex >= 0
            && targetIndex >= 0
            && targetIndex < SelectedItem.RegisteredAudioDevices.Count;
    }

    private void MoveAudioDevice(int offset)
    {
        if (!CanMoveAudioDevice(offset)
            || SelectedItem is null
            || SelectedRegisteredAudioDevice is null)
        {
            return;
        }

        int currentIndex = SelectedItem.RegisteredAudioDevices.IndexOf(SelectedRegisteredAudioDevice);
        SelectedItem.RegisteredAudioDevices.Move(currentIndex, currentIndex + offset);
        StatusMessage = "未保存の変更があります。";
        NotifyAudioDeviceCommands();
    }

    internal void MoveAudioDevice(
        RegisteredAudioDeviceEditorViewModel device,
        int offset)
    {
        SelectedRegisteredAudioDevice = device;
        MoveAudioDevice(offset);
    }

    private void NotifyAudioDeviceCommands()
    {
        AddAudioDeviceCommand.NotifyCanExecuteChanged();
        RemoveAudioDeviceCommand.NotifyCanExecuteChanged();
        MoveAudioDeviceUpCommand.NotifyCanExecuteChanged();
        MoveAudioDeviceDownCommand.NotifyCanExecuteChanged();
    }

    private void AddCommandStep()
    {
        if (SelectedItem is not { IsToggle: true, CycleKind: CycleActionKind.Commands } item)
        {
            return;
        }

        CommandCycleStepEditorViewModel step = new(
            Guid.NewGuid(),
            $"操作 {item.CommandSteps.Count + 1}");
        item.CommandSteps.Add(step);
        SelectedCommandStep = step;
        StatusMessage = "未保存の変更があります。";
    }

    internal void MoveCommandStep(CommandCycleStepEditorViewModel step, int offset)
    {
        if (SelectedItem is null)
        {
            return;
        }

        int currentIndex = SelectedItem.CommandSteps.IndexOf(step);
        int targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= SelectedItem.CommandSteps.Count)
        {
            return;
        }

        SelectedItem.CommandSteps.Move(currentIndex, targetIndex);
        SelectedCommandStep = step;
        StatusMessage = "未保存の変更があります。";
    }

    internal void RemoveCommandStep(CommandCycleStepEditorViewModel step)
    {
        if (SelectedItem is null)
        {
            return;
        }

        int index = SelectedItem.CommandSteps.IndexOf(step);
        if (index < 0)
        {
            return;
        }

        SelectedItem.CommandSteps.RemoveAt(index);
        SelectedCommandStep = SelectedItem.CommandSteps.Count == 0
            ? null
            : SelectedItem.CommandSteps[Math.Min(index, SelectedItem.CommandSteps.Count - 1)];
        StatusMessage = "未保存の変更があります。";
    }

    internal void MoveItem(LauncherItemEditorViewModel item, int offset)
    {
        int currentIndex = Items.IndexOf(item);
        int targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= Items.Count)
        {
            return;
        }

        Items.Move(currentIndex, targetIndex);
        SelectedItem = item;
        StatusMessage = "未保存の変更があります。";
    }

    internal void RemoveItem(LauncherItemEditorViewModel item)
    {
        SelectedItem = item;
        DeleteSelectedItem();
    }

    private void DeleteSelectedItem()
    {
        if (SelectedItem is null)
        {
            return;
        }

        int index = Items.IndexOf(SelectedItem);
        Items.Remove(SelectedItem);
        SelectedItem = Items.Count == 0
            ? null
            : Items[Math.Min(index, Items.Count - 1)];
        StatusMessage = "未保存の変更があります。";
    }

    private async System.Threading.Tasks.Task SaveAsync()
    {
        LauncherItemEditorViewModel? unnamedItem = Items.FirstOrDefault(item =>
            string.IsNullOrWhiteSpace(item.Title));
        if (unnamedItem is not null)
        {
            SelectedItem = unnamedItem;
            StatusMessage = "表示名を入力してください。";
            return;
        }

        LauncherItemEditorViewModel? incompleteAudioItem = Items.FirstOrDefault(item =>
            item.IsToggle
            && item.CycleKind == CycleActionKind.AudioOutput
            && item.RegisteredAudioDevices
                .Select(device => device.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() < 2);
        if (incompleteAudioItem is not null)
        {
            SelectedItem = incompleteAudioItem;
            StatusMessage = "音声切り替えにはデバイスを2台以上登録してください。";
            return;
        }

        LauncherItemEditorViewModel? incompleteCommandItem = Items.FirstOrDefault(item =>
            item.IsToggle
            && item.CycleKind == CycleActionKind.Commands
            && item.CommandSteps.Count < 2);
        if (incompleteCommandItem is not null)
        {
            SelectedItem = incompleteCommandItem;
            StatusMessage = "コマンド切り替えには操作を2つ以上登録してください。";
            return;
        }

        foreach (LauncherItemEditorViewModel item in Items.Where(item =>
                     item.IsToggle && item.CycleKind == CycleActionKind.Commands))
        {
            CommandCycleStepEditorViewModel? incompleteStep = item.CommandSteps.FirstOrDefault(step =>
                string.IsNullOrWhiteSpace(step.DisplayName)
                || string.IsNullOrWhiteSpace(step.Target));
            if (incompleteStep is not null)
            {
                SelectedItem = item;
                SelectedCommandStep = incompleteStep;
                StatusMessage = "各操作の表示名と実行対象を入力してください。";
                return;
            }
        }

        _isSaving = true;
        SaveCommand.NotifyCanExecuteChanged();
        StatusMessage = "保存しています…";

        try
        {
            LauncherSettings settings = new()
            {
                AssumePhonePanelVisible = AssumePhonePanelVisible,
                StartWithWindows = StartWithWindows,
                DetailedLoggingExpiresAtUtc = IsDetailedDiagnosticsEnabled
                    ? DateTimeOffset.UtcNow.AddHours(24)
                    : null,
                LayoutMode = SelectedLayoutMode?.Value ?? LauncherLayoutMode.Standard,
                Pages =
                [
                    new LauncherPageDefinition
                    {
                        Id = _pageId,
                        Name = "メイン",
                        Items = Items.Select(item => item.ToDefinition()).ToList()
                    }
                ]
            };
            _startupService.SetEnabled(settings.StartWithWindows);
            await _settingsRepository.SaveAsync(settings);
            _detailedLoggingExpiresAt = settings.DetailedLoggingExpiresAtUtc;
            _logger.ConfigureDetailedLogging(_detailedLoggingExpiresAt);
            _mainWindowViewModel.ApplySettings(settings);
            OnPropertyChanged(nameof(DetailedDiagnosticsStatus));
            StatusMessage = "保存しました。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"保存できませんでした: {exception.Message}";
        }
        finally
        {
            _isSaving = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int VerticalRefreshRate = 116;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateDC(
        string driver,
        string device,
        string? output,
        nint initializationData);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint deviceContext, int index);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}

internal sealed class LauncherItemEditorViewModel : ObservableObject
{
    private string _title;
    private LauncherActionKind _actionKind;
    private string _target = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDirectory = string.Empty;
    private bool _hideCommandWindow = true;
    private CycleActionKind _cycleKind = CycleActionKind.AudioOutput;
    private bool _retryFailedCommand = true;
    private LauncherPostExecutionBehavior _postExecutionBehavior =
        LauncherPostExecutionBehavior.CloseOnSuccess;
    private readonly VolumeSliderDefinition? _volumeSlider;

    public LauncherItemEditorViewModel(
        LauncherItemDefinition definition,
        IReadOnlyList<AudioOutputDeviceOption> availableAudioDevices)
        : this(definition.Id, definition.Kind, definition.Title)
    {
        LauncherActionDefinition action = definition.Action ?? new LauncherActionDefinition();
        _actionKind = action.Kind;
        _target = action.Target;
        _arguments = action.Arguments;
        _workingDirectory = action.WorkingDirectory;
        _hideCommandWindow = action.HideCommandWindow;
        _postExecutionBehavior = definition.PostExecutionBehavior;
        _volumeSlider = definition.VolumeSlider;

        CycleActionDefinition? cycleAction = definition.GetEffectiveCycleAction();
        _cycleKind = cycleAction?.Kind ?? CycleActionKind.AudioOutput;
        _retryFailedCommand = cycleAction?.RetryFailedCommand ?? true;

        foreach (string deviceId in cycleAction?.AudioDeviceIds ?? [])
        {
            string normalizedDeviceId = AudioDeviceId.Normalize(deviceId);
            AudioOutputDeviceOption? availableDevice = availableAudioDevices.FirstOrDefault(device =>
                string.Equals(
                    AudioDeviceId.Normalize(device.Id),
                    normalizedDeviceId,
                    StringComparison.OrdinalIgnoreCase));
            RegisteredAudioDevices.Add(new RegisteredAudioDeviceEditorViewModel(
                normalizedDeviceId,
                availableDevice?.DisplayName ?? "不明なデバイス",
                availableDevice?.IsAvailable == true));
        }

        foreach (CommandCycleStepDefinition step in cycleAction?.CommandSteps ?? [])
        {
            CommandSteps.Add(new CommandCycleStepEditorViewModel(step));
        }
    }

    public LauncherItemEditorViewModel(Guid id, LauncherItemKind kind, string title)
    {
        Id = id;
        Kind = kind;
        _title = title;
    }

    public Guid Id { get; }
    public LauncherItemKind Kind { get; }
    public bool IsButton => Kind == LauncherItemKind.Button;
    public bool IsToggle => Kind == LauncherItemKind.Toggle;
    public Visibility ButtonSettingsVisibility => IsButton
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility CycleSettingsVisibility => IsToggle
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility PostExecutionSettingsVisibility => Kind != LauncherItemKind.Slider
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility AudioDeviceSettingsVisibility => IsToggle
        && CycleKind == CycleActionKind.AudioOutput
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility CommandCycleSettingsVisibility => IsToggle
        && CycleKind == CycleActionKind.Commands
            ? Visibility.Visible
            : Visibility.Collapsed;
    public ObservableCollection<RegisteredAudioDeviceEditorViewModel> RegisteredAudioDevices { get; } = [];
    public ObservableCollection<CommandCycleStepEditorViewModel> CommandSteps { get; } = [];
    public string KindDisplayName => Kind switch
    {
        LauncherItemKind.Toggle => "循環切り替え",
        LauncherItemKind.Slider => "スライダー",
        _ => "ボタン"
    };
    public string KindSummary => $"種類：{KindDisplayName}";
    public Visibility CommandButtonSettingsVisibility => IsButton
        && ActionKind == LauncherActionKind.Command
            ? Visibility.Visible
            : Visibility.Collapsed;
    public string TargetHeader => ActionKind == LauncherActionKind.Command
        ? "コマンド"
        : "起動対象";
    public string TargetPlaceholderText => ActionKind == LauncherActionKind.Command
        ? "例：systeminfo"
        : "例：notepad.exe";

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public LauncherActionKind ActionKind
    {
        get => _actionKind;
        set
        {
            if (SetProperty(ref _actionKind, value))
            {
                OnPropertyChanged(nameof(CommandButtonSettingsVisibility));
                OnPropertyChanged(nameof(TargetHeader));
                OnPropertyChanged(nameof(TargetPlaceholderText));
            }
        }
    }

    public string Target
    {
        get => _target;
        set => SetProperty(ref _target, value);
    }

    public string Arguments
    {
        get => _arguments;
        set => SetProperty(ref _arguments, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public bool HideCommandWindow
    {
        get => _hideCommandWindow;
        set => SetProperty(ref _hideCommandWindow, value);
    }

    public CycleActionKind CycleKind
    {
        get => _cycleKind;
        set
        {
            if (SetProperty(ref _cycleKind, value))
            {
                OnPropertyChanged(nameof(AudioDeviceSettingsVisibility));
                OnPropertyChanged(nameof(CommandCycleSettingsVisibility));
            }
        }
    }

    public bool RetryFailedCommand
    {
        get => _retryFailedCommand;
        set => SetProperty(ref _retryFailedCommand, value);
    }

    public LauncherPostExecutionBehavior PostExecutionBehavior
    {
        get => _postExecutionBehavior;
        set => SetProperty(ref _postExecutionBehavior, value);
    }

    public LauncherItemDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        PostExecutionBehavior = PostExecutionBehavior,
        Action = Kind == LauncherItemKind.Button
            ? new LauncherActionDefinition
            {
                Kind = ActionKind,
                Target = Target,
                Arguments = Arguments,
                WorkingDirectory = WorkingDirectory,
                HideCommandWindow = HideCommandWindow
            }
            : null,
        AudioDeviceToggle = null,
        CycleAction = Kind == LauncherItemKind.Toggle
            ? new CycleActionDefinition
            {
                Kind = CycleKind,
                RetryFailedCommand = RetryFailedCommand,
                AudioDeviceIds = RegisteredAudioDevices.Select(device => device.Id).ToList(),
                CommandSteps = CommandSteps.Select(step => step.ToDefinition()).ToList()
            }
            : null,
        VolumeSlider = Kind == LauncherItemKind.Slider
            ? _volumeSlider ?? new VolumeSliderDefinition()
            : null
    };
}

internal sealed record ActionKindOption(
    LauncherActionKind Value,
    string DisplayName);

internal sealed record AudioOutputDeviceOption(
    string Id,
    string DisplayName,
    bool IsAvailable)
{
    public string DisplayLabel => IsAvailable
        ? DisplayName
        : $"{DisplayName}（利用不可）";
}

internal sealed class RegisteredAudioDeviceEditorViewModel(
    string id,
    string displayName,
    bool isAvailable)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public bool IsAvailable { get; } = isAvailable;
    public string DisplayLabel => IsAvailable
        ? DisplayName
        : $"{DisplayName}（利用不可）";
}

internal sealed class CommandCycleStepEditorViewModel : ObservableObject
{
    private string _displayName;
    private string _target;
    private string _arguments;
    private string _workingDirectory;
    private bool _hideCommandWindow;

    public CommandCycleStepEditorViewModel(CommandCycleStepDefinition definition)
    {
        Id = definition.Id;
        _displayName = definition.DisplayName;
        _target = definition.Action.Target;
        _arguments = definition.Action.Arguments;
        _workingDirectory = definition.Action.WorkingDirectory;
        _hideCommandWindow = definition.Action.HideCommandWindow;
    }

    public CommandCycleStepEditorViewModel(Guid id, string displayName)
    {
        Id = id;
        _displayName = displayName;
        _target = string.Empty;
        _arguments = string.Empty;
        _workingDirectory = string.Empty;
        _hideCommandWindow = true;
    }

    public Guid Id { get; }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string Target
    {
        get => _target;
        set => SetProperty(ref _target, value);
    }

    public string Arguments
    {
        get => _arguments;
        set => SetProperty(ref _arguments, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public bool HideCommandWindow
    {
        get => _hideCommandWindow;
        set => SetProperty(ref _hideCommandWindow, value);
    }

    public CommandCycleStepDefinition ToDefinition() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Action = new LauncherActionDefinition
        {
            Kind = LauncherActionKind.Command,
            Target = Target,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            HideCommandWindow = HideCommandWindow
        }
    };
}

internal sealed record CycleKindOption(
    CycleActionKind Value,
    string DisplayName);

internal sealed record PostExecutionBehaviorOption(
    LauncherPostExecutionBehavior Value,
    string DisplayName);

internal sealed record LayoutModeOption(
    LauncherLayoutMode Value,
    string DisplayName);

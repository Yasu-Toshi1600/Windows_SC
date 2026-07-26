using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IStartupService _startupService;
    private readonly IAudioOutputService _audioOutputService;
    private readonly DiagnosticLogger _logger;
    private readonly EnvironmentInformationService _environmentInformationService;
    private readonly IStartMenuMonitor _startMenuMonitor;
    private readonly Guid _pageId;
    private LauncherItemEditorViewModel? _selectedItem;
    private ActionKindOption? _selectedActionKind;
    private CycleKindOption? _selectedCycleKind;
    private PostExecutionBehaviorOption? _selectedPostExecutionBehavior;
    private AudioOutputDeviceOption? _audioDeviceToAdd;
    private RegisteredAudioDeviceEditorViewModel? _selectedRegisteredAudioDevice;
    private CommandCycleStepEditorViewModel? _selectedCommandStep;
    private bool _assumePhonePanelVisible;
    private bool _startWithWindows;
    private LayoutModeOption? _selectedLayoutMode;
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private string _troubleshootingStatusMessage = string.Empty;
    private InfoBarSeverity _troubleshootingStatusSeverity =
        InfoBarSeverity.Informational;
    private bool _isDirty;
    private bool _suppressDirtyTracking = true;
    private bool _isSaving;
    private bool _isDetailedDiagnosticsEnabled;
    private DateTimeOffset? _detailedLoggingExpiresAt;
    private bool _isApplyingDiagnosticsSetting;
    private bool _isRefreshingAudioDevices;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        MainWindowViewModel mainWindowViewModel,
        IStartupService startupService,
        IAudioOutputService audioOutputService,
        DiagnosticLogger logger,
        EnvironmentInformationService environmentInformationService,
        IStartMenuMonitor startMenuMonitor)
    {
        _settingsRepository = settingsRepository;
        _mainWindowViewModel = mainWindowViewModel;
        _startupService = startupService;
        _audioOutputService = audioOutputService;
        _logger = logger;
        _environmentInformationService = environmentInformationService;
        _startMenuMonitor = startMenuMonitor;
        _startMenuMonitor.ReadyChanged += StartMenuMonitor_ReadyChanged;

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

        Items.CollectionChanged += Items_CollectionChanged;
        foreach (LauncherItemEditorViewModel item in Items)
        {
            SubscribeToItem(item);
        }

        AddButtonCommand = new RelayCommand(() => AddItem(LauncherItemKind.Button));
        AddToggleCommand = new RelayCommand(() => AddItem(LauncherItemKind.Toggle));
        AddSliderCommand = new RelayCommand(() => AddItem(LauncherItemKind.Slider));
        DeleteItemCommand = new RelayCommand(DeleteSelectedItem, () => SelectedItem is not null);
        AddAudioDeviceCommand = new RelayCommand(AddAudioDevice, CanAddAudioDevice);
        RefreshAudioDevicesCommand = new RelayCommand(
            () => _ = RefreshAudioDevicesAsync(),
            () => !_isRefreshingAudioDevices
                && SelectedItem is { IsToggle: true, CycleKind: CycleActionKind.AudioOutput });
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
        _suppressDirtyTracking = false;
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
            "コマンド／batを実行")
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
                SelectedPostExecutionBehavior = value is { IsToggle: true }
                    ? PostExecutionBehaviors.First(option =>
                        option.Value == value.PostExecutionBehavior)
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

    public PostExecutionBehaviorOption? SelectedPostExecutionBehavior
    {
        get => _selectedPostExecutionBehavior;
        set
        {
            if (SetProperty(ref _selectedPostExecutionBehavior, value)
                && value is not null
                && SelectedItem is { IsToggle: true } selectedItem)
            {
                selectedItem.PostExecutionBehavior = value.Value;
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
        set
        {
            if (SetProperty(ref _assumePhonePanelVisible, value))
            {
                MarkDirty();
            }
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                MarkDirty();
            }
        }
    }

    public LayoutModeOption? SelectedLayoutMode
    {
        get => _selectedLayoutMode;
        set
        {
            if (SetProperty(ref _selectedLayoutMode, value))
            {
                MarkDirty();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(IsStatusMessageOpen));
            }
        }
    }

    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => SetProperty(ref _statusSeverity, value);
    }

    public bool IsStatusMessageOpen => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool IsDirty => _isDirty;

    public string TroubleshootingStatusMessage
    {
        get => _troubleshootingStatusMessage;
        private set
        {
            if (SetProperty(ref _troubleshootingStatusMessage, value))
            {
                OnPropertyChanged(nameof(IsTroubleshootingStatusMessageOpen));
            }
        }
    }

    public InfoBarSeverity TroubleshootingStatusSeverity
    {
        get => _troubleshootingStatusSeverity;
        private set => SetProperty(ref _troubleshootingStatusSeverity, value);
    }

    public bool IsTroubleshootingStatusMessageOpen =>
        !string.IsNullOrWhiteSpace(TroubleshootingStatusMessage);

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
            SetTroubleshootingStatus(
                value
                    ? "［適用］を押すと詳細診断ログが24時間有効になります。"
                    : "［適用］を押すと詳細診断ログが無効になります。",
                InfoBarSeverity.Informational);
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

    public string StartMenuMonitoringStatus => _startMenuMonitor.IsReady
        ? "スタートメニュー監視: 正常"
        : "スタートメニュー監視: 代替モードで動作中（UI Automationイベントを利用できません）";

    public string ApplicationVersionText => $"Windows_SC バージョン {ApplicationInformation.Version}";

    public RelayCommand AddButtonCommand { get; }
    public RelayCommand AddToggleCommand { get; }
    public RelayCommand AddSliderCommand { get; }
    public RelayCommand DeleteItemCommand { get; }
    public RelayCommand AddAudioDeviceCommand { get; }
    public RelayCommand RefreshAudioDevicesCommand { get; }
    public RelayCommand AddCommandStepCommand { get; }
    public RelayCommand RemoveAudioDeviceCommand { get; }
    public RelayCommand MoveAudioDeviceUpCommand { get; }
    public RelayCommand MoveAudioDeviceDownCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ExitApplicationCommand { get; }

    internal void OpenDataFolder()
        => OpenFolder(ApplicationDataPaths.RootDirectoryPath, "データフォルダー");

    private void OpenFolder(string folderPath, string displayName)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
            SetTroubleshootingStatus(
                $"{displayName}を開きました。",
                InfoBarSeverity.Informational);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException
            or UnauthorizedAccessException)
        {
            SetTroubleshootingStatus(
                $"{displayName}を開けませんでした: {exception.Message}",
                InfoBarSeverity.Error);
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
            _environmentInformationService.LogIfChanged("diagnostics-setting");
            OnPropertyChanged(nameof(DetailedDiagnosticsStatus));
            SetTroubleshootingStatus(
                IsDetailedDiagnosticsEnabled
                    ? "詳細診断ログを24時間有効にしました。"
                    : "詳細診断ログを無効にしました。",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            settings.DetailedLoggingExpiresAtUtc = previousExpiration;
            SetTroubleshootingStatus(
                $"詳細診断ログの設定を保存できませんでした: {exception.Message}",
                InfoBarSeverity.Error);
        }
        finally
        {
            _isApplyingDiagnosticsSetting = false;
        }
    }

    internal string CreateEnvironmentInformation() =>
        _environmentInformationService.CreateReport();

    internal void RefreshEnvironmentInformationLog() =>
        _environmentInformationService.LogIfChanged("troubleshooting");

    internal void ReportEnvironmentInformationCopied() =>
        SetTroubleshootingStatus(
            "環境情報をコピーしました。",
            InfoBarSeverity.Informational);

    internal void ReportTargetSelectionFailed(string message) =>
        SetStatus($"起動対象を選択できませんでした: {message}", InfoBarSeverity.Error);

    public void Dispose()
    {
        _startMenuMonitor.ReadyChanged -= StartMenuMonitor_ReadyChanged;
        Items.CollectionChanged -= Items_CollectionChanged;
        foreach (LauncherItemEditorViewModel item in Items)
        {
            UnsubscribeFromItem(item);
        }
    }

    private void StartMenuMonitor_ReadyChanged(object? sender, EventArgs args) =>
        OnPropertyChanged(nameof(StartMenuMonitoringStatus));

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
        {
            foreach (LauncherItemEditorViewModel item in args.OldItems)
            {
                UnsubscribeFromItem(item);
            }
        }

        if (args.NewItems is not null)
        {
            foreach (LauncherItemEditorViewModel item in args.NewItems)
            {
                SubscribeToItem(item);
            }
        }

        MarkDirty();
    }

    private void SubscribeToItem(LauncherItemEditorViewModel item)
    {
        item.PropertyChanged += EditableItem_PropertyChanged;
        item.RegisteredAudioDevices.CollectionChanged += EditableCollection_CollectionChanged;
        item.CommandSteps.CollectionChanged += CommandSteps_CollectionChanged;
        foreach (CommandCycleStepEditorViewModel step in item.CommandSteps)
        {
            step.PropertyChanged += EditableItem_PropertyChanged;
        }
    }

    private void UnsubscribeFromItem(LauncherItemEditorViewModel item)
    {
        item.PropertyChanged -= EditableItem_PropertyChanged;
        item.RegisteredAudioDevices.CollectionChanged -= EditableCollection_CollectionChanged;
        item.CommandSteps.CollectionChanged -= CommandSteps_CollectionChanged;
        foreach (CommandCycleStepEditorViewModel step in item.CommandSteps)
        {
            step.PropertyChanged -= EditableItem_PropertyChanged;
        }
    }

    private void CommandSteps_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
        {
            foreach (CommandCycleStepEditorViewModel step in args.OldItems)
            {
                step.PropertyChanged -= EditableItem_PropertyChanged;
            }
        }

        if (args.NewItems is not null)
        {
            foreach (CommandCycleStepEditorViewModel step in args.NewItems)
            {
                step.PropertyChanged += EditableItem_PropertyChanged;
            }
        }

        MarkDirty();
    }

    private void EditableCollection_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args) =>
        MarkDirty();

    private void EditableItem_PropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        MarkDirty();

    private void MarkDirty()
    {
        if (_suppressDirtyTracking)
        {
            return;
        }

        if (!_isDirty)
        {
            _isDirty = true;
            OnPropertyChanged(nameof(IsDirty));
        }

        SetStatus(
            "変更内容はまだ保存されていません。",
            InfoBarSeverity.Warning,
            includeUnsavedReminder: false);
    }

    private void MarkSaved()
    {
        if (!_isDirty)
        {
            return;
        }

        _isDirty = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void SetStatus(
        string message,
        InfoBarSeverity severity,
        bool includeUnsavedReminder = true)
    {
        if (_isDirty && includeUnsavedReminder)
        {
            const string unsavedReminder = "変更内容はまだ保存されていません。";
            if (!message.Contains(unsavedReminder, StringComparison.Ordinal))
            {
                message = $"{message} {unsavedReminder}";
            }

            if (severity is InfoBarSeverity.Informational or InfoBarSeverity.Success)
            {
                severity = InfoBarSeverity.Warning;
            }
        }

        StatusSeverity = severity;
        StatusMessage = message;
    }

    private void SetTroubleshootingStatus(string message, InfoBarSeverity severity)
    {
        TroubleshootingStatusSeverity = severity;
        TroubleshootingStatusMessage = message;
    }

    internal void ClearLogs()
    {
        try
        {
            _logger.ClearLogs();
            _environmentInformationService.LogIfChanged("logs-cleared", force: true);
            SetTroubleshootingStatus("ログを削除しました。", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is System.IO.IOException
            or UnauthorizedAccessException)
        {
            SetTroubleshootingStatus(
                $"ログを削除できませんでした: {exception.Message}",
                InfoBarSeverity.Error);
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
        MarkDirty();
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
        MarkDirty();
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
        MarkDirty();
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
        MarkDirty();
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
        RefreshAudioDevicesCommand.NotifyCanExecuteChanged();
        RemoveAudioDeviceCommand.NotifyCanExecuteChanged();
        MoveAudioDeviceUpCommand.NotifyCanExecuteChanged();
        MoveAudioDeviceDownCommand.NotifyCanExecuteChanged();
    }

    private async System.Threading.Tasks.Task RefreshAudioDevicesAsync()
    {
        if (_isRefreshingAudioDevices)
        {
            return;
        }

        _isRefreshingAudioDevices = true;
        RefreshAudioDevicesCommand.NotifyCanExecuteChanged();
        SetStatus("音声デバイスを更新しています…", InfoBarSeverity.Informational);

        try
        {
            string? selectedAddDeviceId = AudioDeviceToAdd?.Id;
            string? selectedRegisteredDeviceId = SelectedRegisteredAudioDevice?.Id;
            await _audioOutputService.RefreshAsync();

            IReadOnlyList<AudioOutputDeviceOption> refreshedDevices = _audioOutputService
                .GetCachedDevices()
                .Select(device => new AudioOutputDeviceOption(
                    device.Id,
                    device.DisplayName,
                    device.IsAvailable))
                .ToList();

            AvailableAudioOutputDevices.Clear();
            foreach (AudioOutputDeviceOption device in refreshedDevices)
            {
                AvailableAudioOutputDevices.Add(device);
            }

            _suppressDirtyTracking = true;
            foreach (LauncherItemEditorViewModel item in Items)
            {
                for (int index = 0; index < item.RegisteredAudioDevices.Count; index++)
                {
                    RegisteredAudioDeviceEditorViewModel registered =
                        item.RegisteredAudioDevices[index];
                    AudioOutputDeviceOption? available = refreshedDevices.FirstOrDefault(device =>
                        string.Equals(
                            AudioDeviceId.Normalize(device.Id),
                            AudioDeviceId.Normalize(registered.Id),
                            StringComparison.OrdinalIgnoreCase));
                    item.RegisteredAudioDevices[index] = new RegisteredAudioDeviceEditorViewModel(
                        registered.Id,
                        available?.DisplayName ?? registered.DisplayName,
                        available?.IsAvailable == true);
                }
            }

            SelectedRegisteredAudioDevice = SelectedItem?.RegisteredAudioDevices.FirstOrDefault(device =>
                string.Equals(
                    device.Id,
                    selectedRegisteredDeviceId,
                    StringComparison.OrdinalIgnoreCase));
            AudioDeviceToAdd = refreshedDevices.FirstOrDefault(device =>
                    string.Equals(device.Id, selectedAddDeviceId, StringComparison.OrdinalIgnoreCase)
                    && SelectedItem?.RegisteredAudioDevices.All(registered =>
                        !string.Equals(
                            registered.Id,
                            device.Id,
                            StringComparison.OrdinalIgnoreCase)) == true)
                ?? refreshedDevices.FirstOrDefault(device =>
                    SelectedItem?.RegisteredAudioDevices.All(registered =>
                        !string.Equals(
                            registered.Id,
                            device.Id,
                            StringComparison.OrdinalIgnoreCase)) == true);

            _suppressDirtyTracking = false;
            SetStatus(
                $"音声デバイスを更新しました（{refreshedDevices.Count}件）。",
                InfoBarSeverity.Success);
            _logger.Write($"[AudioOutput] action=manual-refresh result=success devices={refreshedDevices.Count}");
            _mainWindowViewModel.RefreshAudioOutputState();
        }
        catch (Exception exception)
        {
            SetStatus(
                $"音声デバイスを更新できませんでした: {exception.Message}",
                InfoBarSeverity.Error);
            _logger.Write(
                $"[AudioOutput] action=manual-refresh result=failed " +
                $"exception={exception.GetType().Name}");
        }
        finally
        {
            _suppressDirtyTracking = false;
            _isRefreshingAudioDevices = false;
            NotifyAudioDeviceCommands();
        }
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
        MarkDirty();
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
        MarkDirty();
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
        MarkDirty();
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
        MarkDirty();
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
        MarkDirty();
    }

    private async System.Threading.Tasks.Task SaveAsync()
    {
        LauncherItemEditorViewModel? unnamedItem = Items.FirstOrDefault(item =>
            string.IsNullOrWhiteSpace(item.Title));
        if (unnamedItem is not null)
        {
            SelectedItem = unnamedItem;
            SetStatus("表示名を入力してください。", InfoBarSeverity.Warning);
            return;
        }

        LauncherItemEditorViewModel? incompleteButton = Items.FirstOrDefault(item =>
            item.IsButton && string.IsNullOrWhiteSpace(item.Target));
        if (incompleteButton is not null)
        {
            SelectedItem = incompleteButton;
            SetStatus(
                "ボタンの起動対象またはコマンドを入力してください。",
                InfoBarSeverity.Warning);
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
            SetStatus(
                "音声切り替えにはデバイスを2台以上登録してください。",
                InfoBarSeverity.Warning);
            return;
        }

        LauncherItemEditorViewModel? incompleteCommandItem = Items.FirstOrDefault(item =>
            item.IsToggle
            && item.CycleKind == CycleActionKind.Commands
            && item.CommandSteps.Count < 2);
        if (incompleteCommandItem is not null)
        {
            SelectedItem = incompleteCommandItem;
            SetStatus(
                "コマンド切り替えには操作を2つ以上登録してください。",
                InfoBarSeverity.Warning);
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
                SetStatus(
                    "各操作の表示名と実行対象を入力してください。",
                    InfoBarSeverity.Warning);
                return;
            }
        }

        _isSaving = true;
        SaveCommand.NotifyCanExecuteChanged();
        SetStatus("保存しています…", InfoBarSeverity.Informational);

        LauncherSettings previousSettings = _mainWindowViewModel.ExportSettings();
        try
        {
            LauncherSettings settings = new()
            {
                AssumePhonePanelVisible = AssumePhonePanelVisible,
                StartWithWindows = StartWithWindows,
                DetailedLoggingExpiresAtUtc = previousSettings.DetailedLoggingExpiresAtUtc,
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
            await _settingsRepository.SaveAsync(settings);

            try
            {
                _startupService.SetEnabled(settings.StartWithWindows);
            }
            catch (Exception startupException)
            {
                bool startupRollbackSucceeded = TryRestoreStartupSetting(
                    previousSettings.StartWithWindows);
                bool settingsRollbackSucceeded = await TryRestoreSettingsAsync(previousSettings);
                string actualStartupState = GetActualStartupStateText();
                _logger.Write(
                    $"[Settings] action=apply-startup result=failed " +
                    $"exception={startupException.GetType().Name} " +
                    $"settings-rollback={settingsRollbackSucceeded.ToString().ToLowerInvariant()} " +
                    $"startup-rollback={startupRollbackSucceeded.ToString().ToLowerInvariant()} " +
                    $"actual-startup={actualStartupState}");
                SetStatus(
                    settingsRollbackSucceeded
                        ? $"自動起動を変更できなかったため、設定を保存前の状態へ戻しました。現在の自動起動: {actualStartupState}"
                        : $"自動起動を変更できず、設定ファイルも元に戻せませんでした。現在の自動起動: {actualStartupState}。アプリを再起動して状態を確認してください。",
                    InfoBarSeverity.Error);
                return;
            }

            _environmentInformationService.LogIfChanged("settings-save");
            _mainWindowViewModel.ApplySettings(settings);
            MarkSaved();
            SetStatus("保存しました。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"保存できませんでした: {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _isSaving = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private bool TryRestoreStartupSetting(bool enabled)
    {
        try
        {
            _startupService.SetEnabled(enabled);
            return true;
        }
        catch (Exception exception)
        {
            _logger.Write(
                $"[Settings] action=rollback-startup result=failed " +
                $"exception={exception.GetType().Name}");
            return false;
        }
    }

    private async System.Threading.Tasks.Task<bool> TryRestoreSettingsAsync(
        LauncherSettings settings)
    {
        try
        {
            await _settingsRepository.SaveAsync(settings);
            return true;
        }
        catch (Exception exception)
        {
            _logger.Write(
                $"[Settings] action=rollback-file result=failed " +
                $"exception={exception.GetType().Name}");
            return false;
        }
    }

    private string GetActualStartupStateText()
    {
        try
        {
            return _startupService.IsEnabled ? "有効" : "無効";
        }
        catch (Exception exception)
        {
            _logger.Write(
                $"[Settings] action=read-startup result=failed " +
                $"exception={exception.GetType().Name}");
            return "確認できません";
        }
    }

}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IStartupService _startupService;
    private readonly IAudioOutputService _audioOutputService;
    private readonly Guid _pageId;
    private LauncherItemEditorViewModel? _selectedItem;
    private ActionKindOption? _selectedActionKind;
    private AudioOutputDeviceOption? _audioDeviceToAdd;
    private RegisteredAudioDeviceEditorViewModel? _selectedRegisteredAudioDevice;
    private bool _assumePhonePanelVisible;
    private bool _startWithWindows;
    private string _statusMessage = string.Empty;
    private bool _isSaving;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        MainWindowViewModel mainWindowViewModel,
        IStartupService startupService,
        IAudioOutputService audioOutputService)
    {
        _settingsRepository = settingsRepository;
        _mainWindowViewModel = mainWindowViewModel;
        _startupService = startupService;
        _audioOutputService = audioOutputService;

        foreach (AudioOutputDevice device in _audioOutputService.GetDevices())
        {
            AvailableAudioOutputDevices.Add(new AudioOutputDeviceOption(
                device.Id,
                device.DisplayName,
                device.IsAvailable));
        }

        LauncherSettings settings = mainWindowViewModel.ExportSettings();
        _assumePhonePanelVisible = settings.AssumePhonePanelVisible;
        _startWithWindows = settings.StartWithWindows;
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
                SelectedRegisteredAudioDevice = null;
                AudioDeviceToAdd = AvailableAudioOutputDevices.FirstOrDefault(device =>
                    value?.RegisteredAudioDevices.All(registered =>
                        !string.Equals(
                            registered.Id,
                            device.Id,
                            StringComparison.OrdinalIgnoreCase)) == true);
                DeleteItemCommand.NotifyCanExecuteChanged();
                NotifyAudioDeviceCommands();
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

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand AddButtonCommand { get; }
    public RelayCommand AddToggleCommand { get; }
    public RelayCommand AddSliderCommand { get; }
    public RelayCommand DeleteItemCommand { get; }
    public RelayCommand AddAudioDeviceCommand { get; }
    public RelayCommand RemoveAudioDeviceCommand { get; }
    public RelayCommand MoveAudioDeviceUpCommand { get; }
    public RelayCommand MoveAudioDeviceDownCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ExitApplicationCommand { get; }

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
            LauncherItemKind.Toggle => "音声出力切り替え",
            LauncherItemKind.Slider => "新しいスライダー",
            _ => "新しいショートカット"
        };
        LauncherItemEditorViewModel item = new(Guid.NewGuid(), kind, title);
        Items.Add(item);
        SelectedItem = item;
        StatusMessage = "未保存の変更があります。";
    }

    private bool CanAddAudioDevice() =>
        SelectedItem?.IsToggle == true
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
        LauncherItemEditorViewModel? incompleteAudioItem = Items.FirstOrDefault(item =>
            item.IsToggle && item.RegisteredAudioDevices.Count < 2);
        if (incompleteAudioItem is not null)
        {
            SelectedItem = incompleteAudioItem;
            StatusMessage = "音声切り替えにはデバイスを2台以上登録してください。";
            return;
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
            _mainWindowViewModel.ApplySettings(settings);
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
}

internal sealed class LauncherItemEditorViewModel : ObservableObject
{
    private string _title;
    private LauncherActionKind _actionKind;
    private string _target = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDirectory = string.Empty;
    private bool _hideCommandWindow = true;
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
        _volumeSlider = definition.VolumeSlider;

        foreach (string deviceId in definition.AudioDeviceToggle?.GetOrderedDeviceIds() ?? [])
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
    public Visibility AudioDeviceSettingsVisibility => IsToggle
        ? Visibility.Visible
        : Visibility.Collapsed;
    public ObservableCollection<RegisteredAudioDeviceEditorViewModel> RegisteredAudioDevices { get; } = [];
    public string KindDisplayName => Kind switch
    {
        LauncherItemKind.Toggle => "音声切り替え",
        LauncherItemKind.Slider => "スライダー",
        _ => "ボタン"
    };

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public LauncherActionKind ActionKind
    {
        get => _actionKind;
        set => SetProperty(ref _actionKind, value);
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

    public LauncherItemDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
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
        AudioDeviceToggle = Kind == LauncherItemKind.Toggle
            ? new AudioDeviceToggleDefinition
            {
                DeviceIds = RegisteredAudioDevices.Select(device => device.Id).ToList()
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

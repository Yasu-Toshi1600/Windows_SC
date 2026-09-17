using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class LauncherItemEditorViewModel : ObservableObject
{
    private string _title;
    private LauncherActionKind _actionKind;
    private string _target = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDirectory = string.Empty;
    private bool _hideCommandWindow = true;
    private ShortcutKeyDefinition? _shortcutKey;
    private ShortcutKeyInputMode _shortcutKeyInputMode = ShortcutKeyInputMode.ScanCode;
    private CycleActionKind _cycleKind = CycleActionKind.AudioOutput;
    private LauncherPostExecutionBehavior _postExecutionBehavior =
        LauncherPostExecutionBehavior.CloseOnSuccess;
    private VolumeSliderKind _volumeSliderKind = Windows_SC.Models.VolumeSliderKind.Master;
    private ApplicationAudioTargetDefinition? _applicationAudioTarget;
    private SystemMonitorMetric _systemMonitorMetrics =
        SystemMonitorMetric.Cpu | SystemMonitorMetric.Memory;

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
        _shortcutKeyInputMode = action.ShortcutKey?.InputMode
            ?? ShortcutKeyInputMode.ScanCode;
        _shortcutKey = action.ShortcutKey is null
            ? null
            : new ShortcutKeyDefinition
            {
                Modifiers = action.ShortcutKey.Modifiers,
                VirtualKey = action.ShortcutKey.VirtualKey,
                ScanCode = action.ShortcutKey.ScanCode,
                IsExtendedKey = action.ShortcutKey.IsExtendedKey,
                InputMode = action.ShortcutKey.InputMode,
                DisplayText = action.ShortcutKey.DisplayText
            };
        _postExecutionBehavior = definition.PostExecutionBehavior;
        _volumeSliderKind = definition.VolumeSlider?.Type
            ?? Windows_SC.Models.VolumeSliderKind.Master;
        _applicationAudioTarget = CloneTarget(definition.VolumeSlider?.Application);
        _systemMonitorMetrics = NormalizeSystemMonitorMetrics(definition.Widget?.Metrics);

        CycleActionDefinition? cycleAction = definition.GetEffectiveCycleAction();
        _cycleKind = cycleAction?.Kind ?? CycleActionKind.AudioOutput;

        foreach (string deviceId in cycleAction?.AudioDeviceIds ?? [])
        {
            string normalizedDeviceId = AudioDeviceId.Normalize(deviceId);
            string? stableId = AudioDeviceIdentityResolver.GetStableId(normalizedDeviceId,
                cycleAction?.AudioDeviceStableIds);
            AudioOutputDevice? availableDevice = AudioDeviceIdentityResolver.Resolve(normalizedDeviceId,
                stableId, availableAudioDevices.Select(device => new AudioOutputDevice(
                    device.Id, device.DisplayName, device.IsAvailable, device.StableId)).ToList());
            RegisteredAudioDevices.Add(new RegisteredAudioDeviceEditorViewModel(
                availableDevice?.Id ?? normalizedDeviceId,
                availableDevice?.DisplayName
                    ?? cycleAction?.AudioDeviceNames?.FirstOrDefault(pair =>
                        string.Equals(AudioDeviceId.Normalize(pair.Key), normalizedDeviceId,
                            StringComparison.OrdinalIgnoreCase)).Value
                    ?? "不明なデバイス",
                availableDevice?.IsAvailable == true,
                availableDevice?.StableId ?? stableId));
        }

        foreach (CommandCycleStepDefinition step in cycleAction?.CommandSteps ?? [])
        {
            CommandSteps.Add(new CommandCycleStepEditorViewModel(step));
        }

        foreach (MacroStepDefinition step in action.Macro?.Steps ?? [])
        {
            MacroSteps.Add(new MacroStepEditorViewModel(step));
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
    public bool IsSlider => Kind == LauncherItemKind.Slider;
    public bool IsWidget => Kind == LauncherItemKind.Widget;
    public Visibility ButtonSettingsVisibility => IsButton
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility CycleSettingsVisibility => IsToggle
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility SliderSettingsVisibility => IsSlider
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility ApplicationVolumeSettingsVisibility => IsSlider
        && VolumeSliderKind == Windows_SC.Models.VolumeSliderKind.Application
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility PostExecutionSettingsVisibility => IsToggle
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
    public ObservableCollection<MacroStepEditorViewModel> MacroSteps { get; } = [];
    public string KindDisplayName => Kind switch
    {
        LauncherItemKind.Toggle => "循環切り替え",
        LauncherItemKind.Slider => "スライダー",
        LauncherItemKind.Widget => "ウィジェット",
        _ => "ボタン"
    };
    public string KindBadgeName => Kind switch
    {
        LauncherItemKind.Toggle => "循環",
        LauncherItemKind.Slider => "スライダー",
        LauncherItemKind.Widget => "モニター",
        _ => "ボタン"
    };
    public string KindSummary => $"種類：{KindDisplayName}";
    public Visibility CommandButtonSettingsVisibility => IsButton
        && ActionKind is LauncherActionKind.Command or LauncherActionKind.BatchFile
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility WidgetSettingsVisibility => IsWidget
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility StandardActionSettingsVisibility => IsButton
        && ActionKind is not LauncherActionKind.ShortcutKey
        and not LauncherActionKind.Macro
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility ShortcutKeySettingsVisibility => IsButton
        && ActionKind == LauncherActionKind.ShortcutKey
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility MacroSettingsVisibility => IsButton
        && ActionKind == LauncherActionKind.Macro
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility TargetFilePickerVisibility => IsButton
        && ActionKind is LauncherActionKind.Application
            or LauncherActionKind.File
            or LauncherActionKind.BatchFile
            ? Visibility.Visible
            : Visibility.Collapsed;
    public string TargetHeader => ActionKind switch
    {
        LauncherActionKind.Command or LauncherActionKind.BatchFile => "コマンド・bat",
        _ => "アプリ・ファイル・URL"
    };
    public string TargetPlaceholderText => ActionKind switch
    {
        LauncherActionKind.Command or LauncherActionKind.BatchFile =>
            "例：systeminfo または C:\\Scripts\\task.bat",
        _ => "例：notepad.exe、C:\\Documents\\manual.pdf、https://example.com"
    };
    public ShortcutKeyDefinition? ShortcutKey => _shortcutKey;
    public string ShortcutKeyDisplayText => _shortcutKey is null
        ? "未設定"
        : ShortcutKeyText.Format(_shortcutKey);

    public ShortcutKeyInputMode ShortcutKeyInputMode
    {
        get => _shortcutKeyInputMode;
        set
        {
            if (SetProperty(ref _shortcutKeyInputMode, value) && _shortcutKey is not null)
            {
                _shortcutKey.InputMode = value;
            }
        }
    }

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
                OnPropertyChanged(nameof(StandardActionSettingsVisibility));
                OnPropertyChanged(nameof(ShortcutKeySettingsVisibility));
                OnPropertyChanged(nameof(MacroSettingsVisibility));
                OnPropertyChanged(nameof(TargetFilePickerVisibility));
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

    public void SetShortcutKey(ShortcutKeyDefinition? shortcutKey)
    {
        if (shortcutKey is not null)
        {
            shortcutKey.InputMode = ShortcutKeyInputMode;
        }

        _shortcutKey = shortcutKey;
        OnPropertyChanged(nameof(ShortcutKey));
        OnPropertyChanged(nameof(ShortcutKeyDisplayText));
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

    public LauncherPostExecutionBehavior PostExecutionBehavior
    {
        get => _postExecutionBehavior;
        set => SetProperty(ref _postExecutionBehavior, value);
    }

    public VolumeSliderKind VolumeSliderKind
    {
        get => _volumeSliderKind;
        set
        {
            if (SetProperty(ref _volumeSliderKind, value))
            {
                OnPropertyChanged(nameof(ApplicationVolumeSettingsVisibility));
            }
        }
    }

    public ApplicationAudioTargetDefinition? ApplicationAudioTarget =>
        _applicationAudioTarget;

    public bool MonitorCpuSelected
    {
        get => _systemMonitorMetrics.HasFlag(SystemMonitorMetric.Cpu);
        set => SetSystemMonitorMetric(SystemMonitorMetric.Cpu, value);
    }

    public bool MonitorGpuSelected
    {
        get => _systemMonitorMetrics.HasFlag(SystemMonitorMetric.Gpu);
        set => SetSystemMonitorMetric(SystemMonitorMetric.Gpu, value);
    }

    public bool MonitorMemorySelected
    {
        get => _systemMonitorMetrics.HasFlag(SystemMonitorMetric.Memory);
        set => SetSystemMonitorMetric(SystemMonitorMetric.Memory, value);
    }

    public bool CanSelectMonitorCpu => MonitorCpuSelected || CountSystemMonitorMetrics() < 2;

    public bool CanSelectMonitorGpu => MonitorGpuSelected || CountSystemMonitorMetrics() < 2;

    public bool CanSelectMonitorMemory => MonitorMemorySelected || CountSystemMonitorMetrics() < 2;

    public void SetApplicationAudioTarget(ApplicationAudioTargetOption? option)
    {
        if ((_applicationAudioTarget is null && option is null)
            || (_applicationAudioTarget is not null
                && option is not null
                && _applicationAudioTarget.IdentifierType == option.IdentifierType
                && string.Equals(
                    _applicationAudioTarget.Identifier,
                    option.Identifier,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    _applicationAudioTarget.DisplayName,
                    option.DisplayName,
                    StringComparison.Ordinal)))
        {
            return;
        }

        _applicationAudioTarget = option is null
            ? null
            : new ApplicationAudioTargetDefinition
            {
                IdentifierType = option.IdentifierType,
                Identifier = option.Identifier,
                DisplayName = option.DisplayName
            };
        OnPropertyChanged(nameof(ApplicationAudioTarget));
    }

    public LauncherItemDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        PostExecutionBehavior = IsToggle
            ? PostExecutionBehavior
            : LauncherPostExecutionBehavior.CloseOnSuccess,
        Action = Kind == LauncherItemKind.Button
            ? new LauncherActionDefinition
            {
                Kind = ActionKind,
                Target = Target,
                Arguments = Arguments,
                WorkingDirectory = WorkingDirectory,
                HideCommandWindow = HideCommandWindow,
                ShortcutKey = _shortcutKey is null
                    ? null
                    : new ShortcutKeyDefinition
                    {
                        Modifiers = _shortcutKey.Modifiers,
                        VirtualKey = _shortcutKey.VirtualKey,
                        ScanCode = _shortcutKey.ScanCode,
                        IsExtendedKey = _shortcutKey.IsExtendedKey,
                        InputMode = ShortcutKeyInputMode,
                        DisplayText = ShortcutKeyText.Format(_shortcutKey)
                    },
                Macro = ActionKind == LauncherActionKind.Macro
                    ? new MacroDefinition
                    {
                        Steps = MacroSteps.Select(step => step.ToDefinition()).ToList()
                    }
                    : null
            }
            : null,
        AudioDeviceToggle = null,
        CycleAction = Kind == LauncherItemKind.Toggle
            ? new CycleActionDefinition
            {
                Kind = CycleKind,
                AudioDeviceIds = RegisteredAudioDevices.Select(device => device.Id).ToList(),
                AudioDeviceStableIds = RegisteredAudioDevices
                    .Where(device => !string.IsNullOrEmpty(device.StableId))
                    .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().StableId!,
                        StringComparer.OrdinalIgnoreCase),
                AudioDeviceNames = RegisteredAudioDevices
                    .Where(device => device.DisplayName != "不明なデバイス")
                    .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().DisplayName,
                        StringComparer.OrdinalIgnoreCase),
                CommandSteps = CommandSteps.Select(step => step.ToDefinition()).ToList()
            }
            : null,
        VolumeSlider = Kind == LauncherItemKind.Slider
            ? new VolumeSliderDefinition
            {
                Type = VolumeSliderKind,
                Application = VolumeSliderKind == Windows_SC.Models.VolumeSliderKind.Application
                    ? CloneTarget(_applicationAudioTarget)
                    : null
            }
            : null,
        Widget = Kind == LauncherItemKind.Widget
            ? new WidgetDefinition
            {
                Kind = WidgetKind.SystemMonitor,
                Metrics = _systemMonitorMetrics
            }
            : null
    };

    private void SetSystemMonitorMetric(SystemMonitorMetric metric, bool selected)
    {
        bool currentlySelected = _systemMonitorMetrics.HasFlag(metric);
        if (currentlySelected == selected)
        {
            return;
        }

        int selectedCount = CountSystemMonitorMetrics();
        if ((selected && selectedCount >= 2) || (!selected && selectedCount <= 1))
        {
            OnPropertyChanged(GetSystemMonitorSelectionProperty(metric));
            return;
        }

        _systemMonitorMetrics = selected
            ? _systemMonitorMetrics | metric
            : _systemMonitorMetrics & ~metric;
        OnPropertyChanged(GetSystemMonitorSelectionProperty(metric));
        OnPropertyChanged(nameof(CanSelectMonitorCpu));
        OnPropertyChanged(nameof(CanSelectMonitorGpu));
        OnPropertyChanged(nameof(CanSelectMonitorMemory));
    }

    private int CountSystemMonitorMetrics()
    {
        int count = 0;
        count += MonitorCpuSelected ? 1 : 0;
        count += MonitorGpuSelected ? 1 : 0;
        count += MonitorMemorySelected ? 1 : 0;
        return count;
    }

    private static string GetSystemMonitorSelectionProperty(SystemMonitorMetric metric) =>
        metric switch
        {
            SystemMonitorMetric.Cpu => nameof(MonitorCpuSelected),
            SystemMonitorMetric.Gpu => nameof(MonitorGpuSelected),
            _ => nameof(MonitorMemorySelected)
        };

    private static SystemMonitorMetric NormalizeSystemMonitorMetrics(
        SystemMonitorMetric? metrics)
    {
        SystemMonitorMetric value = metrics
            ?? (SystemMonitorMetric.Cpu | SystemMonitorMetric.Memory);
        const SystemMonitorMetric supported =
            SystemMonitorMetric.Cpu | SystemMonitorMetric.Gpu | SystemMonitorMetric.Memory;
        int count = 0;
        count += value.HasFlag(SystemMonitorMetric.Cpu) ? 1 : 0;
        count += value.HasFlag(SystemMonitorMetric.Gpu) ? 1 : 0;
        count += value.HasFlag(SystemMonitorMetric.Memory) ? 1 : 0;
        return (value & ~supported) == 0 && count is >= 1 and <= 2
            ? value
            : SystemMonitorMetric.Cpu | SystemMonitorMetric.Memory;
    }

    private static ApplicationAudioTargetDefinition? CloneTarget(
        ApplicationAudioTargetDefinition? source) =>
        source is null
            ? null
            : new ApplicationAudioTargetDefinition
            {
                IdentifierType = source.IdentifierType,
                Identifier = source.Identifier,
                DisplayName = source.DisplayName
            };
}

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
    private CycleActionKind _cycleKind = CycleActionKind.AudioOutput;
    private LauncherPostExecutionBehavior _postExecutionBehavior =
        LauncherPostExecutionBehavior.CloseOnSuccess;
    private VolumeSliderKind _volumeSliderKind = Windows_SC.Models.VolumeSliderKind.Master;
    private ApplicationAudioTargetDefinition? _applicationAudioTarget;

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
        _shortcutKey = action.ShortcutKey is null
            ? null
            : new ShortcutKeyDefinition
            {
                Modifiers = action.ShortcutKey.Modifiers,
                VirtualKey = action.ShortcutKey.VirtualKey,
                ScanCode = action.ShortcutKey.ScanCode,
                DisplayText = action.ShortcutKey.DisplayText
            };
        _postExecutionBehavior = definition.PostExecutionBehavior;
        _volumeSliderKind = definition.VolumeSlider?.Type
            ?? Windows_SC.Models.VolumeSliderKind.Master;
        _applicationAudioTarget = CloneTarget(definition.VolumeSlider?.Application);

        CycleActionDefinition? cycleAction = definition.GetEffectiveCycleAction();
        _cycleKind = cycleAction?.Kind ?? CycleActionKind.AudioOutput;

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
    public Visibility StandardActionSettingsVisibility => IsButton
        && ActionKind != LauncherActionKind.ShortcutKey
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
        LauncherActionKind.Command => "コマンド",
        LauncherActionKind.Url => "URL",
        LauncherActionKind.File => "ファイルまたはフォルダー",
        LauncherActionKind.BatchFile => "バッチファイル",
        _ => "アプリケーション"
    };
    public string TargetPlaceholderText => ActionKind switch
    {
        LauncherActionKind.Command => "例：systeminfo",
        LauncherActionKind.Url => "例：https://example.com",
        LauncherActionKind.File => "例：C:\\Documents\\manual.pdf",
        LauncherActionKind.BatchFile => "例：C:\\Scripts\\task.bat",
        _ => "例：notepad.exe"
    };
    public ShortcutKeyDefinition? ShortcutKey => _shortcutKey;
    public string ShortcutKeyDisplayText => _shortcutKey is null
        ? "未設定"
        : ShortcutKeyText.Format(_shortcutKey);

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
            ? new WidgetDefinition { Kind = WidgetKind.SystemMonitor }
            : null
    };

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

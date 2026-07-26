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
    private CycleActionKind _cycleKind = CycleActionKind.AudioOutput;
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
    public string KindDisplayName => Kind switch
    {
        LauncherItemKind.Toggle => "循環切り替え",
        LauncherItemKind.Slider => "スライダー",
        _ => "ボタン"
    };
    public string KindBadgeName => Kind switch
    {
        LauncherItemKind.Toggle => "循環",
        LauncherItemKind.Slider => "スライダー",
        _ => "ボタン"
    };
    public string KindSummary => $"種類：{KindDisplayName}";
    public Visibility CommandButtonSettingsVisibility => IsButton
        && ActionKind == LauncherActionKind.Command
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility TargetFilePickerVisibility => IsButton
        && ActionKind == LauncherActionKind.Application
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
                HideCommandWindow = HideCommandWindow
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
            ? _volumeSlider ?? new VolumeSliderDefinition()
            : null
    };
}

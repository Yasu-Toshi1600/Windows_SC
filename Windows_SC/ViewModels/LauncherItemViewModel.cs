using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class LauncherItemViewModel : ObservableObject
{
    private readonly IActionExecutionService _actionExecutionService;
    private readonly IAudioOutputService _audioOutputService;
    private readonly LauncherActionDefinition? _action;
    private readonly AudioDeviceToggleDefinition? _audioDeviceToggle;
    private readonly VolumeSliderDefinition? _volumeSlider;
    private bool _isOn;
    private double _sliderValue = 50;
    private bool _isExecuting;
    private string _currentAudioDeviceName = "現在の出力を取得できません";
    private bool _canCycleAudioOutput;
    private bool _canAdjustVolume;
    private bool _isRefreshingVolume;
    private bool _isSettingVolume;

    public LauncherItemViewModel(
        LauncherItemDefinition definition,
        IActionExecutionService actionExecutionService,
        IAudioOutputService audioOutputService)
    {
        Id = definition.Id;
        Kind = definition.Kind;
        Title = definition.Title;
        _action = definition.Action;
        _audioDeviceToggle = definition.AudioDeviceToggle;
        _volumeSlider = definition.VolumeSlider;
        _actionExecutionService = actionExecutionService;
        _audioOutputService = audioOutputService;
        ExecuteCommand = new RelayCommand(
            () => _ = ExecuteAsync(),
            () => Kind == LauncherItemKind.Button && !_isExecuting);
        CycleAudioOutputCommand = new RelayCommand(
            () => _ = CycleAudioOutputAsync(),
            () => Kind == LauncherItemKind.Toggle && _canCycleAudioOutput && !_isExecuting);
        RefreshAudioOutputState();
    }

    public event EventHandler<LauncherItemExecutedEventArgs>? Executed;

    public Guid Id { get; }

    public LauncherItemKind Kind { get; }

    public string Title { get; }

    public RelayCommand ExecuteCommand { get; }

    public RelayCommand CycleAudioOutputCommand { get; }

    public Visibility ButtonVisibility => Kind == LauncherItemKind.Button
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ToggleVisibility => Kind == LauncherItemKind.Toggle
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SliderVisibility => Kind == LauncherItemKind.Slider
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsOn
    {
        get => _isOn;
        set => SetProperty(ref _isOn, value);
    }

    public double SliderValue
    {
        get => _sliderValue;
        set
        {
            double clampedValue = Math.Clamp(value, SliderMinimum, SliderMaximum);
            if (!SetProperty(ref _sliderValue, clampedValue))
            {
                return;
            }

            OnPropertyChanged(nameof(SliderValueDisplay));
            if (Kind == LauncherItemKind.Slider && !_isRefreshingVolume)
            {
                _ = SetMasterVolumeAsync(clampedValue);
            }
        }
    }

    public string SliderValueDisplay => $"{SliderValue:F0}%";

    public double SliderMinimum => _volumeSlider?.Minimum ?? 0;

    public double SliderMaximum => _volumeSlider?.Maximum ?? 100;

    public bool CanAdjustVolume
    {
        get => _canAdjustVolume;
        private set => SetProperty(ref _canAdjustVolume, value);
    }

    public string CurrentAudioDeviceName
    {
        get => _currentAudioDeviceName;
        private set => SetProperty(ref _currentAudioDeviceName, value);
    }

    public void RefreshAudioOutputState()
    {
        if (Kind == LauncherItemKind.Slider)
        {
            AudioMasterVolumeResult volumeResult = _audioOutputService.GetMasterVolume();
            CanAdjustVolume = volumeResult.IsSuccess;
            if (volumeResult.IsSuccess)
            {
                _isRefreshingVolume = true;
                try
                {
                    SliderValue = volumeResult.VolumePercent;
                }
                finally
                {
                    _isRefreshingVolume = false;
                }
            }

            return;
        }

        if (Kind != LauncherItemKind.Toggle)
        {
            return;
        }

        AudioOutputDevice? currentDevice = _audioOutputService.GetDefaultDevice();
        CurrentAudioDeviceName = currentDevice?.DisplayName ?? "現在の出力を取得できません";

        IReadOnlyList<string> registeredIds = (_audioDeviceToggle?.GetOrderedDeviceIds() ?? [])
            .Select(AudioDeviceId.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        HashSet<string> availableIds = _audioOutputService.GetDevices()
            .Where(device => device.IsAvailable)
            .Select(device => device.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool currentIsRegistered = currentDevice is not null
            && registeredIds.Contains(currentDevice.Id, StringComparer.OrdinalIgnoreCase);
        bool canCycle = registeredIds.Count >= 2
            && registeredIds.Any(id => availableIds.Contains(id)
                && (!currentIsRegistered
                    || !string.Equals(id, currentDevice!.Id, StringComparison.OrdinalIgnoreCase)));

        if (_canCycleAudioOutput != canCycle)
        {
            _canCycleAudioOutput = canCycle;
            CycleAudioOutputCommand.NotifyCanExecuteChanged();
        }
    }

    private async System.Threading.Tasks.Task ExecuteAsync()
    {
        if (_action is null || _isExecuting)
        {
            Executed?.Invoke(
                this,
                new LauncherItemExecutedEventArgs(
                    ActionExecutionResult.Failure("このボタンには実行内容が設定されていません。")));
            return;
        }

        _isExecuting = true;
        ExecuteCommand.NotifyCanExecuteChanged();

        try
        {
            ActionExecutionResult result = await _actionExecutionService.ExecuteAsync(_action);
            Executed?.Invoke(this, new LauncherItemExecutedEventArgs(result));
        }
        finally
        {
            _isExecuting = false;
            ExecuteCommand.NotifyCanExecuteChanged();
        }
    }

    private async System.Threading.Tasks.Task CycleAudioOutputAsync()
    {
        if (_audioDeviceToggle is null || _isExecuting)
        {
            return;
        }

        _isExecuting = true;
        CycleAudioOutputCommand.NotifyCanExecuteChanged();
        try
        {
            AudioDeviceCycleResult result = await _audioOutputService.CycleAsync(
                _audioDeviceToggle.GetOrderedDeviceIds());
            if (result.IsSuccess && result.CurrentDevice is not null)
            {
                CurrentAudioDeviceName = result.CurrentDevice.DisplayName;
                Executed?.Invoke(
                    this,
                    new LauncherItemExecutedEventArgs(ActionExecutionResult.Success));
            }
            else
            {
                Executed?.Invoke(
                    this,
                    new LauncherItemExecutedEventArgs(
                        ActionExecutionResult.Failure(result.ErrorMessage)));
            }
        }
        finally
        {
            _isExecuting = false;
            RefreshAudioOutputState();
            CycleAudioOutputCommand.NotifyCanExecuteChanged();
        }
    }

    private async System.Threading.Tasks.Task SetMasterVolumeAsync(double volumePercent)
    {
        if (_isSettingVolume || !CanAdjustVolume)
        {
            return;
        }

        _isSettingVolume = true;
        try
        {
            AudioMasterVolumeResult result = await _audioOutputService.SetMasterVolumeAsync(
                volumePercent);
            if (!result.IsSuccess)
            {
                CanAdjustVolume = false;
                Executed?.Invoke(
                    this,
                    new LauncherItemExecutedEventArgs(
                        ActionExecutionResult.Failure(result.ErrorMessage)));
            }
        }
        finally
        {
            _isSettingVolume = false;
        }
    }
}

internal sealed class LauncherItemExecutedEventArgs(ActionExecutionResult result) : EventArgs
{
    public ActionExecutionResult Result { get; } = result;
}

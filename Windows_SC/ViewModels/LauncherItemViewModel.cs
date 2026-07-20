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
    private readonly CycleActionDefinition? _cycleAction;
    private readonly VolumeSliderDefinition? _volumeSlider;
    private bool _isOn;
    private double _sliderValue = 50;
    private bool _isExecuting;
    private string _cycleStatusText = "切り替え内容が設定されていません";
    private bool _canExecuteCycle;
    private int _nextCommandStepIndex;
    private bool _canAdjustVolume;
    private bool _isRefreshingVolume;
    private bool _isSettingVolume;
    private int _layoutColumnSpan = 2;
    private double _tileHeight = 160;

    public LauncherItemViewModel(
        LauncherItemDefinition definition,
        IActionExecutionService actionExecutionService,
        IAudioOutputService audioOutputService)
    {
        Id = definition.Id;
        Kind = definition.Kind;
        Title = definition.Title;
        _action = definition.Action;
        _cycleAction = definition.GetEffectiveCycleAction();
        _volumeSlider = definition.VolumeSlider;
        _actionExecutionService = actionExecutionService;
        _audioOutputService = audioOutputService;
        ExecuteCommand = new RelayCommand(
            () => _ = ExecuteAsync(),
            () => Kind == LauncherItemKind.Button && !_isExecuting);
        ExecuteCycleCommand = new RelayCommand(
            () => _ = ExecuteCycleAsync(),
            () => Kind == LauncherItemKind.Toggle && _canExecuteCycle && !_isExecuting);
        RefreshAudioOutputState();
    }

    public event EventHandler<LauncherItemExecutedEventArgs>? Executed;

    public Guid Id { get; }

    public LauncherItemKind Kind { get; }

    public string Title { get; }

    public RelayCommand ExecuteCommand { get; }

    public RelayCommand ExecuteCycleCommand { get; }

    public Visibility ButtonVisibility => Kind == LauncherItemKind.Button
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ToggleVisibility => Kind == LauncherItemKind.Toggle
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SliderVisibility => Kind == LauncherItemKind.Slider
        ? Visibility.Visible
        : Visibility.Collapsed;

    public int LayoutColumnSpan
    {
        get => _layoutColumnSpan;
        private set => SetProperty(ref _layoutColumnSpan, value);
    }

    public double TileHeight
    {
        get => _tileHeight;
        private set => SetProperty(ref _tileHeight, value);
    }

    public void ApplyLayoutMode(LauncherLayoutMode layoutMode)
    {
        bool compactButton = layoutMode == LauncherLayoutMode.Compact
            && Kind == LauncherItemKind.Button;
        LayoutColumnSpan = compactButton ? 1 : 2;
        TileHeight = compactButton ? 80 : 160;
    }

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

    public string CycleStatusText
    {
        get => _cycleStatusText;
        private set => SetProperty(ref _cycleStatusText, value);
    }

    public void RefreshAudioOutputState()
    {
        if (Kind == LauncherItemKind.Slider)
        {
            AudioMasterVolumeResult volumeResult = _audioOutputService.GetCachedMasterVolume();
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

        if (_cycleAction?.Kind == CycleActionKind.Commands)
        {
            UpdateCommandCycleStatus();
            return;
        }

        AudioOutputDevice? currentDevice = _audioOutputService.GetCachedDefaultDevice();
        CycleStatusText = currentDevice?.DisplayName ?? "現在の出力を取得できません";

        IReadOnlyList<string> registeredIds = (_cycleAction?.AudioDeviceIds ?? [])
            .Select(AudioDeviceId.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        HashSet<string> availableIds = _audioOutputService.GetCachedDevices()
            .Where(device => device.IsAvailable)
            .Select(device => device.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool currentIsRegistered = currentDevice is not null
            && registeredIds.Contains(currentDevice.Id, StringComparer.OrdinalIgnoreCase);
        bool canCycle = registeredIds.Count >= 2
            && registeredIds.Any(id => availableIds.Contains(id)
                && (!currentIsRegistered
                    || !string.Equals(id, currentDevice!.Id, StringComparison.OrdinalIgnoreCase)));

        if (_canExecuteCycle != canCycle)
        {
            _canExecuteCycle = canCycle;
            ExecuteCycleCommand.NotifyCanExecuteChanged();
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

    private async System.Threading.Tasks.Task ExecuteCycleAsync()
    {
        if (_cycleAction is null || _isExecuting)
        {
            return;
        }

        _isExecuting = true;
        ExecuteCycleCommand.NotifyCanExecuteChanged();
        try
        {
            if (_cycleAction.Kind == CycleActionKind.Commands)
            {
                await ExecuteNextCommandStepAsync();
            }
            else
            {
                await CycleAudioOutputAsync();
            }
        }
        finally
        {
            _isExecuting = false;
            RefreshAudioOutputState();
            ExecuteCycleCommand.NotifyCanExecuteChanged();
        }
    }

    private async System.Threading.Tasks.Task CycleAudioOutputAsync()
    {
        AudioDeviceCycleResult result = await _audioOutputService.CycleAsync(
            _cycleAction?.AudioDeviceIds ?? []);
        if (result.IsSuccess && result.CurrentDevice is not null)
        {
            CycleStatusText = result.CurrentDevice.DisplayName;
            Executed?.Invoke(
                this,
                new LauncherItemExecutedEventArgs(ActionExecutionResult.Success));
            return;
        }

        Executed?.Invoke(
            this,
            new LauncherItemExecutedEventArgs(
                ActionExecutionResult.Failure(result.ErrorMessage)));
    }

    private async System.Threading.Tasks.Task ExecuteNextCommandStepAsync()
    {
        IReadOnlyList<CommandCycleStepDefinition> steps = _cycleAction?.CommandSteps ?? [];
        if (steps.Count < 2)
        {
            return;
        }

        _nextCommandStepIndex %= steps.Count;
        CommandCycleStepDefinition step = steps[_nextCommandStepIndex];
        ActionExecutionResult result = await _actionExecutionService.ExecuteAsync(step.Action);
        if (result.IsSuccess || !_cycleAction!.RetryFailedCommand)
        {
            _nextCommandStepIndex = (_nextCommandStepIndex + 1) % steps.Count;
            UpdateCommandCycleStatus();
        }

        Executed?.Invoke(this, new LauncherItemExecutedEventArgs(result));
    }

    private void UpdateCommandCycleStatus()
    {
        IReadOnlyList<CommandCycleStepDefinition> steps = _cycleAction?.CommandSteps ?? [];
        bool canExecute = steps.Count >= 2;
        if (canExecute)
        {
            _nextCommandStepIndex %= steps.Count;
            CycleStatusText = $"次: {steps[_nextCommandStepIndex].DisplayName}";
        }
        else
        {
            CycleStatusText = "操作を2件以上登録してください";
        }

        if (_canExecuteCycle != canExecute)
        {
            _canExecuteCycle = canExecute;
            ExecuteCycleCommand.NotifyCanExecuteChanged();
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

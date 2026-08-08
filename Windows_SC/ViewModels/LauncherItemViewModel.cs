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
    private readonly IMacroExecutionService _macroExecutionService;
    private readonly IAudioOutputService _audioOutputService;
    private readonly IApplicationVolumeService _applicationVolumeService;
    private readonly ISystemMetricsService _systemMetricsService;
    private readonly LauncherActionDefinition? _action;
    private readonly CycleActionDefinition? _cycleAction;
    private readonly VolumeSliderDefinition? _volumeSlider;
    private readonly LauncherPostExecutionBehavior _postExecutionBehavior;
    private bool _isOn;
    private double _sliderValue = 50;
    private bool _isExecuting;
    private string _cycleStatusText = "切り替え内容が設定されていません";
    private bool _canExecuteCycle;
    private int _nextCommandStepIndex;
    private bool _canAdjustVolume;
    private bool _isRefreshingVolume;
    private bool _isSettingVolume;
    private bool _isMixedVolume;
    private string _volumeStatusText = string.Empty;
    private int _layoutColumnSpan = 2;
    private double _tileHeight = 160;
    private LauncherLayoutMode _layoutMode = LauncherLayoutMode.Standard;
    private string _cpuUsageText = "CPU  —";
    private string _gpuUsageText = "GPU  —";
    private string _memoryUsageText = "メモリ  —";

    public LauncherItemViewModel(
        LauncherItemDefinition definition,
        IActionExecutionService actionExecutionService,
        IMacroExecutionService macroExecutionService,
        IAudioOutputService audioOutputService,
        IApplicationVolumeService applicationVolumeService,
        ISystemMetricsService systemMetricsService)
    {
        Id = definition.Id;
        Kind = definition.Kind;
        Title = definition.Title;
        _action = definition.Action;
        _cycleAction = definition.GetEffectiveCycleAction();
        _volumeSlider = definition.VolumeSlider;
        _postExecutionBehavior = definition.PostExecutionBehavior;
        _actionExecutionService = actionExecutionService;
        _macroExecutionService = macroExecutionService;
        _audioOutputService = audioOutputService;
        _applicationVolumeService = applicationVolumeService;
        _systemMetricsService = systemMetricsService;
        ExecuteCommand = new RelayCommand(
            () => _ = ExecuteAsync(),
            () => Kind == LauncherItemKind.Button && !_isExecuting);
        ExecuteCycleCommand = new RelayCommand(
            () => _ = ExecuteCycleAsync(),
            () => Kind == LauncherItemKind.Toggle && _canExecuteCycle && !_isExecuting);
        RefreshAudioOutputState();
        RefreshSystemMetrics();
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

    public Visibility WidgetVisibility => Kind == LauncherItemKind.Widget
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string CpuUsageText
    {
        get => _cpuUsageText;
        private set => SetProperty(ref _cpuUsageText, value);
    }

    public string GpuUsageText
    {
        get => _gpuUsageText;
        private set => SetProperty(ref _gpuUsageText, value);
    }

    public string MemoryUsageText
    {
        get => _memoryUsageText;
        private set => SetProperty(ref _memoryUsageText, value);
    }

    public Visibility StandardToggleVisibility =>
        Kind == LauncherItemKind.Toggle && _layoutMode == LauncherLayoutMode.Standard
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CompactToggleVisibility =>
        Kind == LauncherItemKind.Toggle && _layoutMode == LauncherLayoutMode.Compact
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility StandardSliderVisibility =>
        Kind == LauncherItemKind.Slider && _layoutMode == LauncherLayoutMode.Standard
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CompactSliderVisibility =>
        Kind == LauncherItemKind.Slider && _layoutMode == LauncherLayoutMode.Compact
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
        _layoutMode = layoutMode;
        bool compactButton = layoutMode == LauncherLayoutMode.Compact
            && Kind == LauncherItemKind.Button;
        LayoutColumnSpan = compactButton ? 1 : 2;
        TileHeight = layoutMode == LauncherLayoutMode.Compact ? 80 : 160;
        OnPropertyChanged(nameof(StandardToggleVisibility));
        OnPropertyChanged(nameof(CompactToggleVisibility));
        OnPropertyChanged(nameof(StandardSliderVisibility));
        OnPropertyChanged(nameof(CompactSliderVisibility));
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
                _ = SetVolumeAsync(clampedValue);
            }
        }
    }

    public string SliderValueDisplay => !CanAdjustVolume
        ? _volumeStatusText
        : _isMixedVolume
            ? "混在"
            : $"{SliderValue:F0}%";

    public double SliderMinimum => _volumeSlider?.Minimum ?? 0;

    public double SliderMaximum => _volumeSlider?.Maximum ?? 100;

    public bool CanAdjustVolume
    {
        get => _canAdjustVolume;
        private set
        {
            if (SetProperty(ref _canAdjustVolume, value))
            {
                OnPropertyChanged(nameof(SliderValueDisplay));
            }
        }
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
            if (_volumeSlider?.Type == VolumeSliderKind.Application)
            {
                RefreshApplicationVolumeState();
                return;
            }

            AudioMasterVolumeResult volumeResult = _audioOutputService.GetCachedMasterVolume();
            CanAdjustVolume = volumeResult.IsSuccess;
            _isMixedVolume = false;
            _volumeStatusText = volumeResult.IsSuccess ? string.Empty : "利用不能";
            OnPropertyChanged(nameof(SliderValueDisplay));
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

        IReadOnlyList<string> registeredIds = (_cycleAction?.AudioDeviceIds ?? [])
            .Select(AudioDeviceId.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IReadOnlyDictionary<string, AudioOutputDevice> availableDevices = _audioOutputService
            .GetCachedDevices()
            .Where(device => device.IsAvailable)
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        AudioOutputDevice? currentDevice = _audioOutputService.GetCachedDefaultDevice();
        AudioOutputDevice? nextDevice = FindNextAudioOutputDevice(
            registeredIds,
            availableDevices,
            currentDevice?.Id);
        bool canCycle = registeredIds.Count >= 2 && nextDevice is not null;
        CycleStatusText = canCycle
            ? $"次: {nextDevice!.DisplayName}"
            : registeredIds.Count < 2
                ? "音声出力デバイスを2台以上登録してください"
                : "切り替え可能な音声出力デバイスがありません";

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
                    ActionExecutionResult.Failure("このボタンには実行内容が設定されていません。"),
                    shouldCloseOnSuccess: false));
            return;
        }

        _isExecuting = true;
        ExecuteCommand.NotifyCanExecuteChanged();

        try
        {
            ActionExecutionResult result = _action.Kind == LauncherActionKind.Macro
                ? await _macroExecutionService.ExecuteAsync(
                    Id,
                    _action.Macro ?? new MacroDefinition())
                : await _actionExecutionService.ExecuteAsync(_action);
            RaiseExecuted(result);
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

    public void RefreshSystemMetrics()
    {
        if (Kind != LauncherItemKind.Widget)
        {
            return;
        }

        SystemMetricsSnapshot snapshot = _systemMetricsService.GetCachedMetrics();
        CpuUsageText = snapshot.CpuPercent is { } cpu
            ? $"CPU  {cpu:F0}%"
            : "CPU  —";
        GpuUsageText = snapshot.GpuPercent is { } gpu
            ? $"GPU  {gpu:F0}%"
            : "GPU  —";
        MemoryUsageText = snapshot.TotalMemoryBytes == 0
            ? "メモリ  —"
            : $"メモリ  {snapshot.MemoryPercent:F0}%  " +
              $"{FormatBytes(snapshot.UsedMemoryBytes)} / {FormatBytes(snapshot.TotalMemoryBytes)}";
    }

    private static string FormatBytes(ulong bytes)
    {
        const double gibibyte = 1024d * 1024 * 1024;
        return $"{bytes / gibibyte:F1}GB";
    }

    private async System.Threading.Tasks.Task CycleAudioOutputAsync()
    {
        AudioDeviceCycleResult result = await _audioOutputService.CycleAsync(
            _cycleAction?.AudioDeviceIds ?? []);
        if (result.IsSuccess && result.CurrentDevice is not null)
        {
            Executed?.Invoke(
                this,
                new LauncherItemExecutedEventArgs(
                    ActionExecutionResult.Success,
                    ShouldCloseOnSuccess));
            return;
        }

        Executed?.Invoke(
            this,
            new LauncherItemExecutedEventArgs(
                ActionExecutionResult.Failure(result.ErrorMessage),
                shouldCloseOnSuccess: false));
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
        _nextCommandStepIndex = (_nextCommandStepIndex + 1) % steps.Count;
        UpdateCommandCycleStatus();

        RaiseExecuted(
            result,
            mayTransferFocus: result.IsSuccess && !step.Action.HideCommandWindow);
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

    private static AudioOutputDevice? FindNextAudioOutputDevice(
        IReadOnlyList<string> orderedIds,
        IReadOnlyDictionary<string, AudioOutputDevice> availableDevices,
        string? currentDeviceId)
    {
        int currentIndex = currentDeviceId is null
            ? -1
            : orderedIds
                .Select((id, index) => (id, index))
                .Where(entry => string.Equals(
                    entry.id,
                    currentDeviceId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.index)
                .DefaultIfEmpty(-1)
                .First();
        int startIndex = currentIndex < 0 ? 0 : currentIndex + 1;

        for (int offset = 0; offset < orderedIds.Count; offset++)
        {
            int index = (startIndex + offset) % orderedIds.Count;
            if (availableDevices.TryGetValue(
                    orderedIds[index],
                    out AudioOutputDevice? device)
                && !string.Equals(
                    device.Id,
                    currentDeviceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }

    private void RefreshApplicationVolumeState()
    {
        ApplicationAudioTarget? target = GetApplicationTarget();
        ApplicationAudioInfo? info = target is null
            ? null
            : _applicationVolumeService.GetCachedApplications().FirstOrDefault(candidate =>
                candidate.Target.IdentifierKind == target.IdentifierKind
                && string.Equals(
                    candidate.Target.Identifier,
                    target.Identifier,
                    StringComparison.OrdinalIgnoreCase));
        CanAdjustVolume = info?.IsAvailable == true;
        _isMixedVolume = info?.IsMixed == true;
        _volumeStatusText = info is null ? "利用不能" : string.Empty;
        if (info is not null)
        {
            _isRefreshingVolume = true;
            try
            {
                SliderValue = info.VolumePercent;
            }
            finally
            {
                _isRefreshingVolume = false;
            }
        }

        OnPropertyChanged(nameof(SliderValueDisplay));
    }

    private async System.Threading.Tasks.Task SetVolumeAsync(double volumePercent)
    {
        if (_isSettingVolume || !CanAdjustVolume)
        {
            return;
        }

        _isSettingVolume = true;
        try
        {
            if (_volumeSlider?.Type == VolumeSliderKind.Application)
            {
                ApplicationAudioTarget? target = GetApplicationTarget();
                ApplicationVolumeResult result = target is null
                    ? ApplicationVolumeResult.Failure("対象アプリが設定されていません。", 0, 0)
                    : await _applicationVolumeService.SetVolumeAsync(
                        target,
                        (int)Math.Round(volumePercent));
                if (!result.IsSuccess)
                {
                    CanAdjustVolume = false;
                    Executed?.Invoke(
                        this,
                        new LauncherItemExecutedEventArgs(
                            ActionExecutionResult.Failure(result.ErrorMessage),
                            shouldCloseOnSuccess: false));
                }
                else
                {
                    _isMixedVolume = false;
                    OnPropertyChanged(nameof(SliderValueDisplay));
                }
            }
            else
            {
                AudioMasterVolumeResult result = await _audioOutputService.SetMasterVolumeAsync(
                    volumePercent);
                if (!result.IsSuccess)
                {
                    CanAdjustVolume = false;
                    Executed?.Invoke(
                        this,
                        new LauncherItemExecutedEventArgs(
                            ActionExecutionResult.Failure(result.ErrorMessage),
                            shouldCloseOnSuccess: false));
                }
            }
        }
        finally
        {
            _isSettingVolume = false;
        }
    }

    private ApplicationAudioTarget? GetApplicationTarget()
    {
        ApplicationAudioTargetDefinition? definition = _volumeSlider?.Application;
        return definition is null || string.IsNullOrWhiteSpace(definition.Identifier)
            ? null
            : new ApplicationAudioTarget(
                definition.IdentifierType,
                definition.Identifier,
                definition.DisplayName);
    }

    private bool ShouldCloseOnSuccess =>
        _postExecutionBehavior == LauncherPostExecutionBehavior.CloseOnSuccess;

    private void RaiseExecuted(
        ActionExecutionResult result,
        bool mayTransferFocus = false) =>
        Executed?.Invoke(
            this,
            new LauncherItemExecutedEventArgs(
                result,
                ShouldCloseOnSuccess,
                mayTransferFocus));
}

internal sealed class LauncherItemExecutedEventArgs(
    ActionExecutionResult result,
    bool shouldCloseOnSuccess,
    bool mayTransferFocus = false) : EventArgs
{
    public ActionExecutionResult Result { get; } = result;

    public bool ShouldCloseOnSuccess { get; } = shouldCloseOnSuccess;

    public bool MayTransferFocus { get; } = mayTransferFocus;
}

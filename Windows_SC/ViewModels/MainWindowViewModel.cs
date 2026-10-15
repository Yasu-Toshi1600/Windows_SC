using System.Collections.ObjectModel;
using System;
using System.Linq;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private bool _assumePhonePanelVisible = true;
    private bool _startWithWindows;
    private LauncherLayoutMode _layoutMode = LauncherLayoutMode.Standard;

    private LauncherSettings _settings = LauncherSettings.CreateDefault();
    private readonly IActionExecutionService _actionExecutionService;
    private readonly IMacroExecutionService _macroExecutionService;
    private readonly IApplicationVolumeService _applicationVolumeService;
    private readonly ISystemMetricsService _systemMetricsService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly CommandCycleStateStore _commandCycleState;
    private readonly VisibleUiUpdateCoordinator _metricsUpdates;

    public MainWindowViewModel(
        IActionExecutionService actionExecutionService,
        IMacroExecutionService macroExecutionService,
        IAudioOutputService audioOutputService,
        IApplicationVolumeService applicationVolumeService,
        ISystemMetricsService systemMetricsService,
        IUiDispatcher uiDispatcher,
        CommandCycleStateStore commandCycleState)
    {
        _actionExecutionService = actionExecutionService;
        _macroExecutionService = macroExecutionService;
        _applicationVolumeService = applicationVolumeService;
        _systemMetricsService = systemMetricsService;
        _uiDispatcher = uiDispatcher;
        _commandCycleState = commandCycleState;
        _metricsUpdates = new VisibleUiUpdateCoordinator(_uiDispatcher, RefreshSystemMetrics);
        _applicationVolumeService.StateChanged += ApplicationVolumeService_StateChanged;
        _systemMetricsService.MetricsChanged += SystemMetricsService_MetricsChanged;
        AudioOutputService = audioOutputService;
        OpenSettingsCommand = new RelayCommand(
            () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ApplySettings(_settings);
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler<LauncherItemExecutedEventArgs>? LauncherItemExecuted;

    public string Title => "Windows_SC";

    public string HotKeyDescription => "Windowsキー または Ctrl + Alt + Space";

    public ObservableCollection<LauncherItemViewModel> Shortcuts { get; } = [];

    public RelayCommand OpenSettingsCommand { get; }

    internal IAudioOutputService AudioOutputService { get; }

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

    public LauncherLayoutMode LayoutMode
    {
        get => _layoutMode;
        private set
        {
            if (SetProperty(ref _layoutMode, value))
            {
                ApplyLayoutToShortcuts();
            }
        }
    }

    public void ApplySettings(LauncherSettings settings)
    {
        foreach (LauncherItemViewModel shortcut in Shortcuts)
        {
            shortcut.Executed -= Shortcut_Executed;
        }

        _settings = settings;
        AssumePhonePanelVisible = settings.AssumePhonePanelVisible;
        StartWithWindows = settings.StartWithWindows;
        LayoutMode = settings.LayoutMode;
        Shortcuts.Clear();

        LauncherPageDefinition? firstPage = settings.Pages.FirstOrDefault();
        if (firstPage is null)
        {
            _systemMetricsService.SetMetrics(SystemMonitorMetric.None);
            return;
        }

        foreach (LauncherItemDefinition item in firstPage.Items)
        {
            LauncherItemViewModel shortcut = new(
                item,
                _actionExecutionService,
                _macroExecutionService,
                AudioOutputService,
                _applicationVolumeService,
                _systemMetricsService,
                _commandCycleState);
            shortcut.Executed += Shortcut_Executed;
            shortcut.ApplyLayoutMode(LayoutMode);
            Shortcuts.Add(shortcut);
        }

        _systemMetricsService.SetMetrics(Shortcuts.Aggregate(SystemMonitorMetric.None,
            (metrics, shortcut) => metrics | shortcut.MonitoredMetrics));
        _metricsUpdates.RequestUpdate();
    }

    public void RefreshAudioOutputState()
    {
        foreach (LauncherItemViewModel shortcut in Shortcuts)
        {
            shortcut.RefreshAudioOutputState();
        }
    }

    public LauncherSettings ExportSettings()
    {
        _settings.AssumePhonePanelVisible = AssumePhonePanelVisible;
        _settings.StartWithWindows = StartWithWindows;
        _settings.LayoutMode = LayoutMode;
        return _settings;
    }

    private void ApplyLayoutToShortcuts()
    {
        foreach (LauncherItemViewModel shortcut in Shortcuts)
        {
            shortcut.ApplyLayoutMode(LayoutMode);
        }
    }

    private void ApplicationVolumeService_StateChanged(object? sender, EventArgs args) =>
        _uiDispatcher.TryEnqueue(RefreshAudioOutputState);

    public void SetLauncherVisible(bool visible) => _metricsUpdates.SetVisible(visible);

    private void SystemMetricsService_MetricsChanged(object? sender, EventArgs args) =>
        _metricsUpdates.RequestUpdate();

    private void RefreshSystemMetrics()
    {
        foreach (LauncherItemViewModel shortcut in Shortcuts)
        {
            shortcut.RefreshSystemMetrics();
        }
    }

    public void Dispose()
    {
        _metricsUpdates.Dispose();
        _systemMetricsService.SetMetrics(SystemMonitorMetric.None);
        _applicationVolumeService.StateChanged -= ApplicationVolumeService_StateChanged;
        _systemMetricsService.MetricsChanged -= SystemMetricsService_MetricsChanged;
        foreach (LauncherItemViewModel shortcut in Shortcuts)
        {
            shortcut.Executed -= Shortcut_Executed;
        }
    }

    private void Shortcut_Executed(object? sender, LauncherItemExecutedEventArgs args) =>
        LauncherItemExecuted?.Invoke(sender, args);
}

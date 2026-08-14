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

    public MainWindowViewModel(
        IActionExecutionService actionExecutionService,
        IMacroExecutionService macroExecutionService,
        IAudioOutputService audioOutputService,
        IApplicationVolumeService applicationVolumeService,
        ISystemMetricsService systemMetricsService,
        IUiDispatcher uiDispatcher)
    {
        _actionExecutionService = actionExecutionService;
        _macroExecutionService = macroExecutionService;
        _applicationVolumeService = applicationVolumeService;
        _systemMetricsService = systemMetricsService;
        _uiDispatcher = uiDispatcher;
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
            _systemMetricsService.SetActive(false);
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
                _systemMetricsService);
            shortcut.Executed += Shortcut_Executed;
            shortcut.ApplyLayoutMode(LayoutMode);
            Shortcuts.Add(shortcut);
        }

        _systemMetricsService.SetActive(
            Shortcuts.Any(shortcut => shortcut.Kind == LauncherItemKind.Widget));
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

    private void SystemMetricsService_MetricsChanged(object? sender, EventArgs args) =>
        _uiDispatcher.TryEnqueue(() =>
        {
            foreach (LauncherItemViewModel shortcut in Shortcuts)
            {
                shortcut.RefreshSystemMetrics();
            }
        });

    public void Dispose()
    {
        _systemMetricsService.SetActive(false);
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

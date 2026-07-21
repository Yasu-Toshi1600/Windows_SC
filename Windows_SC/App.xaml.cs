using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using System;
using Windows_SC.Models;
using Windows_SC.Services;
using Windows_SC.ViewModels;

namespace Windows_SC;

public partial class App : Application
{
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsViewModel;
    private ISingleInstanceService? _singleInstanceService;
    private ISettingsRepository? _settingsRepository;
    private MainWindowViewModel? _viewModel;
    private DiagnosticLogger? _logger;
    private IStartupService? _startupService;
    private IAudioOutputService? _audioOutputService;
    private ISystemTrayService? _systemTrayService;
    private bool _isShuttingDown;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DiagnosticLogger logger = new();
        _logger = logger;
        _singleInstanceService = new SingleInstanceService();
        if (!_singleInstanceService.TryAcquire())
        {
            logger.Write("[Application] startup=cancelled reason=another-instance-running");
            logger.Dispose();
            _logger = null;
            Exit();
            return;
        }

        _settingsRepository = new JsonSettingsRepository(logger);
        IActionExecutionService actionExecutionService = new ActionExecutionService(logger);
        _audioOutputService = new WindowsAudioOutputService(logger);
        _viewModel = new MainWindowViewModel(actionExecutionService, _audioOutputService);
        LauncherSettings settings = _settingsRepository.LoadAsync().GetAwaiter().GetResult();
        logger.ConfigureDetailedLogging(settings.DetailedLoggingExpiresAtUtc);
        _viewModel.ApplySettings(settings);
        _startupService = new RegistryStartupService(logger);
        _startupService.SetEnabled(settings.StartWithWindows);
        _viewModel.SettingsRequested += ViewModel_SettingsRequested;
        IStartMenuMonitor startMenuMonitor = new HybridStartMenuMonitor(
            DispatcherQueue.GetForCurrentThread(),
            logger);
        IGlobalInputService inputService = new GlobalInputService(logger);
        ILauncherPlacementService placementService = new LauncherPlacementService(logger);
        _systemTrayService = new WindowsSystemTrayService(logger);
        _systemTrayService.ShowLauncherRequested += SystemTrayService_ShowLauncherRequested;
        _systemTrayService.SettingsRequested += SystemTrayService_SettingsRequested;
        _systemTrayService.ExitRequested += SystemTrayService_ExitRequested;
        IWindowInteropService windowInteropService = new WindowInteropService(
            inputService,
            _systemTrayService);
        _window = new MainWindow(
            _viewModel,
            logger,
            startMenuMonitor,
            inputService,
            placementService,
            windowInteropService);
        _window.Closed += Window_Closed;
        _window.InitializeBackgroundWindow();
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            if (_settingsRepository is not null && _viewModel is not null)
            {
                _settingsRepository.SaveAsync(_viewModel.ExportSettings()).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"設定の終了保存に失敗しました: {exception}");
        }
        finally
        {
            if (_viewModel is not null)
            {
                _viewModel.SettingsRequested -= ViewModel_SettingsRequested;
            }

            if (_systemTrayService is not null)
            {
                _systemTrayService.ShowLauncherRequested -= SystemTrayService_ShowLauncherRequested;
                _systemTrayService.SettingsRequested -= SystemTrayService_SettingsRequested;
                _systemTrayService.ExitRequested -= SystemTrayService_ExitRequested;
                _systemTrayService.Dispose();
                _systemTrayService = null;
            }

            _singleInstanceService?.Dispose();
            _singleInstanceService = null;
            _audioOutputService?.Dispose();
            _audioOutputService = null;
            _logger?.Dispose();
            _logger = null;
            Exit();
        }
    }

    private void ViewModel_SettingsRequested(object? sender, EventArgs args)
    {
        ShowSettingsWindow();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        if (_settingsRepository is null || _viewModel is null)
        {
            return;
        }

        if (_startupService is null || _logger is null)
        {
            return;
        }

        _settingsViewModel = new SettingsViewModel(
            _settingsRepository,
            _viewModel,
            _startupService,
            _viewModel.AudioOutputService,
            _logger);
        _settingsViewModel.ExitApplicationRequested += SettingsViewModel_ExitApplicationRequested;
        _settingsWindow = new SettingsWindow(_settingsViewModel);
        _settingsWindow.Closed += SettingsWindow_Closed;
        _settingsWindow.Activate();
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Closed -= SettingsWindow_Closed;
            _settingsWindow = null;
        }

        if (_settingsViewModel is not null)
        {
            _settingsViewModel.ExitApplicationRequested -= SettingsViewModel_ExitApplicationRequested;
            _settingsViewModel = null;
        }
    }

    private void SettingsViewModel_ExitApplicationRequested(object? sender, EventArgs args)
    {
        RequestShutdown("settings");
    }

    private void SystemTrayService_ShowLauncherRequested(object? sender, EventArgs args)
    {
        _window?.DispatcherQueue.TryEnqueue(() => _window?.RequestManualShow());
    }

    private void SystemTrayService_SettingsRequested(object? sender, EventArgs args)
    {
        _window?.DispatcherQueue.TryEnqueue(ShowSettingsWindow);
    }

    private void SystemTrayService_ExitRequested(object? sender, EventArgs args)
    {
        _window?.DispatcherQueue.TryEnqueue(() => RequestShutdown("system-tray"));
    }

    private void RequestShutdown(string source)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _logger?.Write($"[Application] shutdown=requested source={source}");
        _settingsWindow?.Close();
        _window?.Close();
    }
}

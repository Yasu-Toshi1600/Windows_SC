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
    private EnvironmentInformationService? _environmentInformationService;
    private IStartMenuMonitor? _startMenuMonitor;
    private IStartupService? _startupService;
    private IAudioOutputService? _audioOutputService;
    private IApplicationVolumeStateStore? _applicationVolumeStateStore;
    private IApplicationVolumeService? _applicationVolumeService;
    private ISystemTrayService? _systemTrayService;
    private IGlobalInputService? _inputService;
    private IMacroExecutionService? _macroExecutionService;
    private bool _isShuttingDown;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException +=
            TaskScheduler_UnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DiagnosticLogger logger = new();
        _logger = logger;
        _singleInstanceService = new SingleInstanceService();
        if (!_singleInstanceService.TryAcquire())
        {
            logger.Write(
                "[Application] action=start result=cancelled reason=another-instance-running");
            logger.Dispose();
            _logger = null;
            Exit();
            return;
        }

        StartupCompatibilityResult compatibility = StartupCompatibilityChecker.Check();
        if (!compatibility.IsSupported)
        {
            logger.Write(
                $"[Compatibility] action=check result=failed state=unsupported " +
                compatibility.LogDetails);
            StartupCompatibilityChecker.ShowError(compatibility.UserMessage);
            _singleInstanceService.Dispose();
            _singleInstanceService = null;
            logger.Dispose();
            _logger = null;
            Exit();
            return;
        }

        logger.Write(
            $"[Compatibility] action=check result=success state=supported " +
            compatibility.LogDetails);
        if (!string.IsNullOrEmpty(compatibility.WarningMessage))
        {
            StartupCompatibilityChecker.ShowWarning(compatibility.WarningMessage);
            logger.Write(
                $"[Compatibility] action=show-warning result=acknowledged " +
                $"reason=display-below-1080p {compatibility.LogDetails}");
        }

        _settingsRepository = new JsonSettingsRepository(logger);
        IShortcutKeyExecutionService shortcutKeyExecutionService =
            new ShortcutKeyExecutionService(logger);
        IActionExecutionService actionExecutionService = new ActionExecutionService(
            logger,
            shortcutKeyExecutionService);
        _macroExecutionService = new MacroExecutionService(actionExecutionService, logger);
        _audioOutputService = new WindowsAudioOutputService(logger);
        _applicationVolumeStateStore = new ApplicationVolumeStateStore(logger);
        _applicationVolumeService = new WindowsApplicationVolumeService(
            logger,
            _applicationVolumeStateStore);
        _viewModel = new MainWindowViewModel(
            actionExecutionService,
            _macroExecutionService,
            _audioOutputService,
            _applicationVolumeService);
        LauncherSettings settings = _settingsRepository.LoadAsync().GetAwaiter().GetResult();
        logger.ConfigureDetailedLogging(
            settings.DetailedLoggingExpiresAtUtc,
            settings.DetailedLoggingAlwaysEnabled);
        _environmentInformationService = new EnvironmentInformationService(logger);
        _viewModel.ApplySettings(settings);
        _startupService = new RegistryStartupService(logger);
        try
        {
            _startupService.SetEnabled(settings.StartWithWindows);
        }
        catch (Exception exception)
        {
            logger.Write(
                $"[Startup] action=synchronize result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
        _viewModel.SettingsRequested += ViewModel_SettingsRequested;
        _startMenuMonitor = new HybridStartMenuMonitor(
            DispatcherQueue.GetForCurrentThread(),
            logger);
        _inputService = new GlobalInputService(logger);
        ILauncherPlacementService placementService = new LauncherPlacementService(logger);
        _systemTrayService = new WindowsSystemTrayService(logger);
        _systemTrayService.ShowLauncherRequested += SystemTrayService_ShowLauncherRequested;
        _systemTrayService.SettingsRequested += SystemTrayService_SettingsRequested;
        _systemTrayService.ExitRequested += SystemTrayService_ExitRequested;
        IWindowInteropService windowInteropService = new WindowInteropService(
            _inputService,
            _systemTrayService);
        _window = new MainWindow(
            _viewModel,
            logger,
            _startMenuMonitor,
            _inputService,
            placementService,
            windowInteropService,
            _environmentInformationService);
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
            _logger?.Write(
                $"[Application] action=shutdown-save result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            System.Diagnostics.Debug.WriteLine($"設定の終了保存に失敗しました: {exception}");
        }
        finally
        {
            if (_viewModel is not null)
            {
                _viewModel.SettingsRequested -= ViewModel_SettingsRequested;
                _viewModel.Dispose();
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
            _applicationVolumeService?.Dispose();
            _applicationVolumeService = null;
            _applicationVolumeStateStore?.Dispose();
            _applicationVolumeStateStore = null;
            _macroExecutionService?.Dispose();
            _macroExecutionService = null;
            UnhandledException -= App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException -=
                TaskScheduler_UnobservedTaskException;
            _logger?.Dispose();
            _logger = null;
            _environmentInformationService = null;
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

        if (_startupService is null
            || _logger is null
            || _environmentInformationService is null
            || _startMenuMonitor is null
            || _inputService is null
            || _applicationVolumeService is null)
        {
            return;
        }

        _settingsViewModel = new SettingsViewModel(
            _settingsRepository,
            _viewModel,
            _startupService,
            _viewModel.AudioOutputService,
            _applicationVolumeService,
            _logger,
            _environmentInformationService,
            _startMenuMonitor,
            _inputService);
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
            _settingsViewModel.Dispose();
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
        _logger?.Write(
            $"[Application] action=shutdown-request result=success source={source}");
        _settingsWindow?.Close();
        _window?.Close();
    }

    private void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        WriteUnhandledException("ui-thread", args.Exception, isTerminating: true);

    private void CurrentDomain_UnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs args) =>
        WriteUnhandledException(
            "app-domain",
            args.ExceptionObject as Exception,
            args.IsTerminating);

    private void TaskScheduler_UnobservedTaskException(
        object? sender,
        System.Threading.Tasks.UnobservedTaskExceptionEventArgs args) =>
        WriteUnhandledException("unobserved-task", args.Exception, isTerminating: false);

    private void WriteUnhandledException(
        string source,
        Exception? exception,
        bool isTerminating)
    {
        try
        {
            string exceptionType = exception?.GetType().Name ?? "unknown";
            string hresult = exception is null ? "unknown" : $"0x{exception.HResult:X8}";
            string message = LogValue.Normalize(
                exception?.Message ?? "例外情報を取得できませんでした。");
            DiagnosticLogger? logger = _logger;
            logger?.WriteCritical(
                $"[Application] action=unhandled-exception result=failed source={source} " +
                $"terminating={isTerminating.ToString().ToLowerInvariant()} " +
                $"exception={exceptionType} hresult={hresult} message=\"{message}\"");
        }
        catch (Exception loggingException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"未処理例外を診断ログへ記録できませんでした: {loggingException}");
        }
    }

}

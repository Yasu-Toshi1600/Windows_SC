using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using System;
using System.ComponentModel;
using Windows_SC.Services;
using Windows_SC.ViewModels;

namespace Windows_SC;

public partial class App : Application
{
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private ISingleInstanceService? _singleInstanceService;
    private ISettingsRepository? _settingsRepository;
    private MainWindowViewModel? _viewModel;
    private DiagnosticLogger? _logger;

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
            Exit();
            return;
        }

        _settingsRepository = new JsonSettingsRepository(logger);
        _viewModel = new MainWindowViewModel();
        _viewModel.ApplySettings(_settingsRepository.LoadAsync().GetAwaiter().GetResult());
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.SettingsRequested += ViewModel_SettingsRequested;
        IStartMenuMonitor startMenuMonitor = new HybridStartMenuMonitor(
            DispatcherQueue.GetForCurrentThread(),
            logger);
        IGlobalInputService inputService = new GlobalInputService(logger);
        ILauncherPlacementService placementService = new LauncherPlacementService(logger);
        _window = new MainWindow(
            _viewModel,
            logger,
            startMenuMonitor,
            inputService,
            placementService);
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
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.SettingsRequested -= ViewModel_SettingsRequested;
            }

            _singleInstanceService?.Dispose();
            _singleInstanceService = null;
        }
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainWindowViewModel.AssumePhonePanelVisible)
            || _settingsRepository is null
            || _viewModel is null)
        {
            return;
        }

        try
        {
            await _settingsRepository.SaveAsync(_viewModel.ExportSettings());
        }
        catch (Exception exception)
        {
            _logger?.Write(
                $"[Settings] action=autosave result=failed exception={exception.GetType().Name} " +
                $"message=\"{exception.Message}\"");
        }
    }

    private void ViewModel_SettingsRequested(object? sender, EventArgs args)
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

        SettingsViewModel settingsViewModel = new(_settingsRepository, _viewModel);
        _settingsWindow = new SettingsWindow(settingsViewModel);
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
    }
}

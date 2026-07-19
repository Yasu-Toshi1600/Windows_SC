using System.Collections.ObjectModel;
using System;
using System.Linq;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class MainWindowViewModel : ObservableObject
{
    private bool _assumePhonePanelVisible = true;
    private bool _startWithWindows;

    private LauncherSettings _settings = LauncherSettings.CreateDefault();
    private readonly IActionExecutionService _actionExecutionService;

    public MainWindowViewModel(IActionExecutionService actionExecutionService)
    {
        _actionExecutionService = actionExecutionService;
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

    public void ApplySettings(LauncherSettings settings)
    {
        foreach (LauncherItemViewModel shortcut in Shortcuts)
        {
            shortcut.Executed -= Shortcut_Executed;
        }

        _settings = settings;
        AssumePhonePanelVisible = settings.AssumePhonePanelVisible;
        StartWithWindows = settings.StartWithWindows;
        Shortcuts.Clear();

        LauncherPageDefinition? firstPage = settings.Pages.FirstOrDefault();
        if (firstPage is null)
        {
            return;
        }

        foreach (LauncherItemDefinition item in firstPage.Items)
        {
            LauncherItemViewModel shortcut = new(item, _actionExecutionService);
            shortcut.Executed += Shortcut_Executed;
            Shortcuts.Add(shortcut);
        }
    }

    public LauncherSettings ExportSettings()
    {
        _settings.AssumePhonePanelVisible = AssumePhonePanelVisible;
        _settings.StartWithWindows = StartWithWindows;
        return _settings;
    }

    private void Shortcut_Executed(object? sender, LauncherItemExecutedEventArgs args) =>
        LauncherItemExecuted?.Invoke(sender, args);
}

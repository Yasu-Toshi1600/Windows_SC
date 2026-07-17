using System.Collections.ObjectModel;
using System;
using System.Linq;
using Windows_SC.Models;

namespace Windows_SC.ViewModels;

internal sealed class MainWindowViewModel : ObservableObject
{
    private bool _assumePhonePanelVisible = true;

    private LauncherSettings _settings = LauncherSettings.CreateDefault();

    public MainWindowViewModel()
    {
        OpenSettingsCommand = new RelayCommand(
            () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ApplySettings(_settings);
    }

    public event EventHandler? SettingsRequested;

    public string Title => "Windows_SC";

    public string HotKeyDescription => "Windowsキー または Ctrl + Alt + Space";

    public ObservableCollection<LauncherItemViewModel> Shortcuts { get; } = [];

    public RelayCommand OpenSettingsCommand { get; }

    public bool AssumePhonePanelVisible
    {
        get => _assumePhonePanelVisible;
        set => SetProperty(ref _assumePhonePanelVisible, value);
    }

    public void ApplySettings(LauncherSettings settings)
    {
        _settings = settings;
        AssumePhonePanelVisible = settings.AssumePhonePanelVisible;
        Shortcuts.Clear();

        LauncherPageDefinition? firstPage = settings.Pages.FirstOrDefault();
        if (firstPage is null)
        {
            return;
        }

        foreach (LauncherItemDefinition item in firstPage.Items)
        {
            Shortcuts.Add(new LauncherItemViewModel(item.Id, item.Kind, item.Title));
        }
    }

    public LauncherSettings ExportSettings()
    {
        _settings.AssumePhonePanelVisible = AssumePhonePanelVisible;
        return _settings;
    }
}

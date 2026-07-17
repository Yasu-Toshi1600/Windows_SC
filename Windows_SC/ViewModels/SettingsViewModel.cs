using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly Guid _pageId;
    private LauncherItemEditorViewModel? _selectedItem;
    private bool _assumePhonePanelVisible;
    private string _statusMessage = string.Empty;
    private bool _isSaving;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        MainWindowViewModel mainWindowViewModel)
    {
        _settingsRepository = settingsRepository;
        _mainWindowViewModel = mainWindowViewModel;

        LauncherSettings settings = mainWindowViewModel.ExportSettings();
        _assumePhonePanelVisible = settings.AssumePhonePanelVisible;
        LauncherPageDefinition page = settings.Pages.FirstOrDefault()
            ?? new LauncherPageDefinition { Name = "メイン" };
        _pageId = page.Id;

        foreach (LauncherItemDefinition item in page.Items)
        {
            Items.Add(new LauncherItemEditorViewModel(item));
        }

        AddButtonCommand = new RelayCommand(() => AddItem(LauncherItemKind.Button));
        AddToggleCommand = new RelayCommand(() => AddItem(LauncherItemKind.Toggle));
        AddSliderCommand = new RelayCommand(() => AddItem(LauncherItemKind.Slider));
        DeleteItemCommand = new RelayCommand(DeleteSelectedItem, () => SelectedItem is not null);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !_isSaving);
        SelectedItem = Items.FirstOrDefault();
    }

    public ObservableCollection<LauncherItemEditorViewModel> Items { get; } = [];

    public LauncherItemEditorViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                DeleteItemCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool AssumePhonePanelVisible
    {
        get => _assumePhonePanelVisible;
        set => SetProperty(ref _assumePhonePanelVisible, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand AddButtonCommand { get; }
    public RelayCommand AddToggleCommand { get; }
    public RelayCommand AddSliderCommand { get; }
    public RelayCommand DeleteItemCommand { get; }
    public RelayCommand SaveCommand { get; }

    private void AddItem(LauncherItemKind kind)
    {
        string title = kind switch
        {
            LauncherItemKind.Toggle => "新しいトグル",
            LauncherItemKind.Slider => "新しいスライダー",
            _ => "新しいショートカット"
        };
        LauncherItemEditorViewModel item = new(Guid.NewGuid(), kind, title);
        Items.Add(item);
        SelectedItem = item;
        StatusMessage = "未保存の変更があります。";
    }

    private void DeleteSelectedItem()
    {
        if (SelectedItem is null)
        {
            return;
        }

        int index = Items.IndexOf(SelectedItem);
        Items.Remove(SelectedItem);
        SelectedItem = Items.Count == 0
            ? null
            : Items[Math.Min(index, Items.Count - 1)];
        StatusMessage = "未保存の変更があります。";
    }

    private async System.Threading.Tasks.Task SaveAsync()
    {
        _isSaving = true;
        SaveCommand.NotifyCanExecuteChanged();
        StatusMessage = "保存しています…";

        try
        {
            LauncherSettings settings = new()
            {
                AssumePhonePanelVisible = AssumePhonePanelVisible,
                Pages =
                [
                    new LauncherPageDefinition
                    {
                        Id = _pageId,
                        Name = "メイン",
                        Items = Items.Select(item => item.ToDefinition()).ToList()
                    }
                ]
            };
            await _settingsRepository.SaveAsync(settings);
            _mainWindowViewModel.ApplySettings(settings);
            StatusMessage = "保存しました。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"保存できませんでした: {exception.Message}";
        }
        finally
        {
            _isSaving = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }
}

internal sealed class LauncherItemEditorViewModel : ObservableObject
{
    private string _title;
    private readonly LauncherActionDefinition? _action;
    private readonly AudioDeviceToggleDefinition? _audioDeviceToggle;
    private readonly VolumeSliderDefinition? _volumeSlider;

    public LauncherItemEditorViewModel(LauncherItemDefinition definition)
        : this(definition.Id, definition.Kind, definition.Title)
    {
        _action = definition.Action;
        _audioDeviceToggle = definition.AudioDeviceToggle;
        _volumeSlider = definition.VolumeSlider;
    }

    public LauncherItemEditorViewModel(Guid id, LauncherItemKind kind, string title)
    {
        Id = id;
        Kind = kind;
        _title = title;
    }

    public Guid Id { get; }
    public LauncherItemKind Kind { get; }
    public string KindDisplayName => Kind switch
    {
        LauncherItemKind.Toggle => "トグル",
        LauncherItemKind.Slider => "スライダー",
        _ => "ボタン"
    };

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public LauncherItemDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        Action = Kind == LauncherItemKind.Button
            ? _action ?? new LauncherActionDefinition()
            : null,
        AudioDeviceToggle = Kind == LauncherItemKind.Toggle
            ? _audioDeviceToggle ?? new AudioDeviceToggleDefinition()
            : null,
        VolumeSlider = Kind == LauncherItemKind.Slider
            ? _volumeSlider ?? new VolumeSliderDefinition()
            : null
    };
}

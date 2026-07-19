using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IStartupService _startupService;
    private readonly Guid _pageId;
    private LauncherItemEditorViewModel? _selectedItem;
    private bool _assumePhonePanelVisible;
    private bool _startWithWindows;
    private string _statusMessage = string.Empty;
    private bool _isSaving;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        MainWindowViewModel mainWindowViewModel,
        IStartupService startupService)
    {
        _settingsRepository = settingsRepository;
        _mainWindowViewModel = mainWindowViewModel;
        _startupService = startupService;

        LauncherSettings settings = mainWindowViewModel.ExportSettings();
        _assumePhonePanelVisible = settings.AssumePhonePanelVisible;
        _startWithWindows = settings.StartWithWindows;
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
        ExitApplicationCommand = new RelayCommand(
            () => ExitApplicationRequested?.Invoke(this, EventArgs.Empty));
        SelectedItem = Items.FirstOrDefault();
    }

    public event EventHandler? ExitApplicationRequested;

    public ObservableCollection<LauncherItemEditorViewModel> Items { get; } = [];

    public IReadOnlyList<ActionKindOption> ActionKinds { get; } =
    [
        new(LauncherActionKind.Application, "アプリ"),
        new(LauncherActionKind.File, "ファイル"),
        new(LauncherActionKind.Url, "URL"),
        new(LauncherActionKind.Command, "コマンド"),
        new(LauncherActionKind.BatchFile, "batファイル")
    ];

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

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
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
    public RelayCommand ExitApplicationCommand { get; }

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
                StartWithWindows = StartWithWindows,
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
            _startupService.SetEnabled(settings.StartWithWindows);
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
    private LauncherActionKind _actionKind;
    private string _target = string.Empty;
    private string _arguments = string.Empty;
    private string _workingDirectory = string.Empty;
    private bool _hideCommandWindow = true;
    private readonly AudioDeviceToggleDefinition? _audioDeviceToggle;
    private readonly VolumeSliderDefinition? _volumeSlider;

    public LauncherItemEditorViewModel(LauncherItemDefinition definition)
        : this(definition.Id, definition.Kind, definition.Title)
    {
        LauncherActionDefinition action = definition.Action ?? new LauncherActionDefinition();
        _actionKind = action.Kind;
        _target = action.Target;
        _arguments = action.Arguments;
        _workingDirectory = action.WorkingDirectory;
        _hideCommandWindow = action.HideCommandWindow;
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
    public bool IsButton => Kind == LauncherItemKind.Button;
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

    public LauncherActionKind ActionKind
    {
        get => _actionKind;
        set => SetProperty(ref _actionKind, value);
    }

    public string Target
    {
        get => _target;
        set => SetProperty(ref _target, value);
    }

    public string Arguments
    {
        get => _arguments;
        set => SetProperty(ref _arguments, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public bool HideCommandWindow
    {
        get => _hideCommandWindow;
        set => SetProperty(ref _hideCommandWindow, value);
    }

    public LauncherItemDefinition ToDefinition() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        Action = Kind == LauncherItemKind.Button
            ? new LauncherActionDefinition
            {
                Kind = ActionKind,
                Target = Target,
                Arguments = Arguments,
                WorkingDirectory = WorkingDirectory,
                HideCommandWindow = HideCommandWindow
            }
            : null,
        AudioDeviceToggle = Kind == LauncherItemKind.Toggle
            ? _audioDeviceToggle ?? new AudioDeviceToggleDefinition()
            : null,
        VolumeSlider = Kind == LauncherItemKind.Slider
            ? _volumeSlider ?? new VolumeSliderDefinition()
            : null
    };
}

internal sealed record ActionKindOption(
    LauncherActionKind Value,
    string DisplayName);

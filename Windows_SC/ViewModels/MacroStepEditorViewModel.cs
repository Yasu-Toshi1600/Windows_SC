using Microsoft.UI.Xaml;
using System;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class MacroStepEditorViewModel : ObservableObject
{
    private string _displayName;
    private MacroStepKind _kind;
    private LauncherActionKind _actionKind;
    private string _target;
    private string _arguments;
    private string _workingDirectory;
    private bool _hideCommandWindow;
    private int _delayMilliseconds;
    private ShortcutKeyDefinition? _shortcutKey;
    private ShortcutKeyInputMode _shortcutKeyInputMode = ShortcutKeyInputMode.ScanCode;

    public MacroStepEditorViewModel(MacroStepDefinition definition)
    {
        Id = definition.Id;
        _displayName = definition.DisplayName;
        _kind = definition.Kind;
        LauncherActionDefinition action = definition.Action ?? new LauncherActionDefinition();
        _actionKind = action.Kind == LauncherActionKind.Macro
            ? LauncherActionKind.Application
            : action.Kind;
        _target = action.Target;
        _arguments = action.Arguments;
        _workingDirectory = action.WorkingDirectory;
        _hideCommandWindow = action.HideCommandWindow;
        _delayMilliseconds = definition.DelayMilliseconds;
        _shortcutKeyInputMode = action.ShortcutKey?.InputMode
            ?? ShortcutKeyInputMode.ScanCode;
        _shortcutKey = CloneShortcut(action.ShortcutKey);
    }

    public MacroStepEditorViewModel(Guid id, string displayName)
    {
        Id = id;
        _displayName = displayName;
        _kind = MacroStepKind.Action;
        _actionKind = LauncherActionKind.Application;
        _target = string.Empty;
        _arguments = string.Empty;
        _workingDirectory = string.Empty;
        _hideCommandWindow = true;
    }

    public Guid Id { get; }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public MacroStepKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                NotifyVisibility();
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public LauncherActionKind ActionKind
    {
        get => _actionKind;
        set
        {
            if (SetProperty(ref _actionKind, value))
            {
                NotifyVisibility();
                OnPropertyChanged(nameof(Summary));
            }
        }
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

    public int DelayMilliseconds
    {
        get => _delayMilliseconds;
        set
        {
            if (SetProperty(ref _delayMilliseconds, Math.Clamp(value, 0, 10_000)))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public ShortcutKeyDefinition? ShortcutKey => _shortcutKey;

    public string ShortcutKeyDisplayText => _shortcutKey is null
        ? "未設定"
        : ShortcutKeyText.Format(_shortcutKey);

    public ShortcutKeyInputMode ShortcutKeyInputMode
    {
        get => _shortcutKeyInputMode;
        set
        {
            if (SetProperty(ref _shortcutKeyInputMode, value) && _shortcutKey is not null)
            {
                _shortcutKey.InputMode = value;
            }
        }
    }

    public string Summary => Kind == MacroStepKind.Wait
        ? $"待機 {DelayMilliseconds}ms"
        : ActionKind.ToString();

    public Visibility ActionSettingsVisibility => Kind == MacroStepKind.Action
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility WaitSettingsVisibility => Kind == MacroStepKind.Wait
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility StandardActionSettingsVisibility => Kind == MacroStepKind.Action
        && ActionKind != LauncherActionKind.ShortcutKey
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ShortcutKeySettingsVisibility => Kind == MacroStepKind.Action
        && ActionKind == LauncherActionKind.ShortcutKey
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility CommandSettingsVisibility => Kind == MacroStepKind.Action
        && ActionKind is LauncherActionKind.Command or LauncherActionKind.BatchFile
            ? Visibility.Visible
            : Visibility.Collapsed;

    public void SetShortcutKey(ShortcutKeyDefinition? shortcutKey)
    {
        _shortcutKey = CloneShortcut(shortcutKey);
        if (_shortcutKey is not null)
        {
            _shortcutKey.InputMode = ShortcutKeyInputMode;
        }
        OnPropertyChanged(nameof(ShortcutKey));
        OnPropertyChanged(nameof(ShortcutKeyDisplayText));
    }

    public MacroStepDefinition ToDefinition() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Kind = Kind,
        DelayMilliseconds = Kind == MacroStepKind.Wait ? DelayMilliseconds : 0,
        Action = Kind == MacroStepKind.Action
            ? new LauncherActionDefinition
            {
                Kind = ActionKind,
                Target = Target,
                Arguments = Arguments,
                WorkingDirectory = WorkingDirectory,
                HideCommandWindow = HideCommandWindow,
                ShortcutKey = CloneShortcut(_shortcutKey)
            }
            : null
    };

    private void NotifyVisibility()
    {
        OnPropertyChanged(nameof(ActionSettingsVisibility));
        OnPropertyChanged(nameof(WaitSettingsVisibility));
        OnPropertyChanged(nameof(StandardActionSettingsVisibility));
        OnPropertyChanged(nameof(ShortcutKeySettingsVisibility));
        OnPropertyChanged(nameof(CommandSettingsVisibility));
    }

    private static ShortcutKeyDefinition? CloneShortcut(ShortcutKeyDefinition? source) =>
        source is null
            ? null
            : new ShortcutKeyDefinition
            {
                Modifiers = source.Modifiers,
                VirtualKey = source.VirtualKey,
                ScanCode = source.ScanCode,
                IsExtendedKey = source.IsExtendedKey,
                InputMode = source.InputMode,
                DisplayText = source.DisplayText
            };
}

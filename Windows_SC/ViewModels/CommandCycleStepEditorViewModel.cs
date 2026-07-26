using System;
using Windows_SC.Models;

namespace Windows_SC.ViewModels;

internal sealed class CommandCycleStepEditorViewModel : ObservableObject
{
    private string _displayName;
    private string _target;
    private string _arguments;
    private string _workingDirectory;
    private bool _hideCommandWindow;

    public CommandCycleStepEditorViewModel(CommandCycleStepDefinition definition)
    {
        Id = definition.Id;
        _displayName = definition.DisplayName;
        _target = definition.Action.Target;
        _arguments = definition.Action.Arguments;
        _workingDirectory = definition.Action.WorkingDirectory;
        _hideCommandWindow = definition.Action.HideCommandWindow;
    }

    public CommandCycleStepEditorViewModel(Guid id, string displayName)
    {
        Id = id;
        _displayName = displayName;
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

    public CommandCycleStepDefinition ToDefinition() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Action = new LauncherActionDefinition
        {
            Kind = LauncherActionKind.Command,
            Target = Target,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            HideCommandWindow = HideCommandWindow
        }
    };
}

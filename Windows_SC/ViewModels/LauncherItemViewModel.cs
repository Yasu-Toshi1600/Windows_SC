using System;
using Microsoft.UI.Xaml;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.ViewModels;

internal sealed class LauncherItemViewModel : ObservableObject
{
    private readonly IActionExecutionService _actionExecutionService;
    private readonly LauncherActionDefinition? _action;
    private bool _isOn;
    private double _sliderValue = 50;
    private bool _isExecuting;

    public LauncherItemViewModel(
        LauncherItemDefinition definition,
        IActionExecutionService actionExecutionService)
    {
        Id = definition.Id;
        Kind = definition.Kind;
        Title = definition.Title;
        _action = definition.Action;
        _actionExecutionService = actionExecutionService;
        ExecuteCommand = new RelayCommand(
            () => _ = ExecuteAsync(),
            () => Kind == LauncherItemKind.Button && !_isExecuting);
    }

    public event EventHandler<LauncherItemExecutedEventArgs>? Executed;

    public Guid Id { get; }

    public LauncherItemKind Kind { get; }

    public string Title { get; }

    public RelayCommand ExecuteCommand { get; }

    public Visibility ButtonVisibility => Kind == LauncherItemKind.Button
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ToggleVisibility => Kind == LauncherItemKind.Toggle
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility SliderVisibility => Kind == LauncherItemKind.Slider
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsOn
    {
        get => _isOn;
        set => SetProperty(ref _isOn, value);
    }

    public double SliderValue
    {
        get => _sliderValue;
        set => SetProperty(ref _sliderValue, value);
    }

    private async System.Threading.Tasks.Task ExecuteAsync()
    {
        if (_action is null || _isExecuting)
        {
            Executed?.Invoke(
                this,
                new LauncherItemExecutedEventArgs(
                    ActionExecutionResult.Failure("このボタンには実行内容が設定されていません。")));
            return;
        }

        _isExecuting = true;
        ExecuteCommand.NotifyCanExecuteChanged();

        try
        {
            ActionExecutionResult result = await _actionExecutionService.ExecuteAsync(_action);
            Executed?.Invoke(this, new LauncherItemExecutedEventArgs(result));
        }
        finally
        {
            _isExecuting = false;
            ExecuteCommand.NotifyCanExecuteChanged();
        }
    }
}

internal sealed class LauncherItemExecutedEventArgs(ActionExecutionResult result) : EventArgs
{
    public ActionExecutionResult Result { get; } = result;
}

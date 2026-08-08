using System;
using System.Windows.Input;

namespace Windows_SC.ViewModels;

internal sealed class RelayCommand<T>(
    Action<T> execute,
    Predicate<T>? canExecute = null) : ICommand
    where T : class
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value && (canExecute?.Invoke(value) ?? true))
        {
            execute(value);
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

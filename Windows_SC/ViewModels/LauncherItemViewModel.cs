using System;
using Microsoft.UI.Xaml;
using Windows_SC.Models;

namespace Windows_SC.ViewModels;

internal sealed class LauncherItemViewModel(Guid id, LauncherItemKind kind, string title)
    : ObservableObject
{
    private bool _isOn;
    private double _sliderValue = 50;

    public Guid Id { get; } = id;

    public LauncherItemKind Kind { get; } = kind;

    public string Title { get; } = title;

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
}

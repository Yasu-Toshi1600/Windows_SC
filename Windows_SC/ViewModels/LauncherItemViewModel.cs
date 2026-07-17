using System;
using Windows_SC.Models;

namespace Windows_SC.ViewModels;

internal sealed class LauncherItemViewModel(Guid id, LauncherItemKind kind, string title)
{
    public Guid Id { get; } = id;

    public LauncherItemKind Kind { get; } = kind;

    public string Title { get; } = title;
}

using Windows_SC.Models;

namespace Windows_SC.ViewModels;

internal sealed record ActionKindOption(
    LauncherActionKind Value,
    string DisplayName);

internal sealed record AudioOutputDeviceOption(
    string Id,
    string DisplayName,
    bool IsAvailable)
{
    public string DisplayLabel => IsAvailable
        ? DisplayName
        : $"{DisplayName}（利用不可）";
}

internal sealed record CycleKindOption(
    CycleActionKind Value,
    string DisplayName);

internal sealed record PostExecutionBehaviorOption(
    LauncherPostExecutionBehavior Value,
    string DisplayName);

internal sealed record LayoutModeOption(
    LauncherLayoutMode Value,
    string DisplayName);

internal sealed record MacroStepKindOption(
    MacroStepKind Value,
    string DisplayName);

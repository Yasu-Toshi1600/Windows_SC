using System;

namespace Windows_SC.Services;

internal enum ShortcutRecordingTargetKind
{
    LauncherItem,
    MacroStep
}

internal readonly record struct ShortcutRecordingTarget(
    ShortcutRecordingTargetKind Kind,
    Guid Id);

internal sealed class ShortcutRecordingSession(Action<bool> setInputSuppressed)
{
    private readonly Action<bool> _setInputSuppressed =
        setInputSuppressed ?? throw new ArgumentNullException(nameof(setInputSuppressed));

    public ShortcutRecordingTarget? ActiveTarget { get; private set; }

    public bool IsActive => ActiveTarget is not null;

    public void Begin(ShortcutRecordingTarget target)
    {
        ActiveTarget = target;
        _setInputSuppressed(true);
    }

    public ShortcutRecordingTarget? StopAndTakeTarget()
    {
        ShortcutRecordingTarget? target = ActiveTarget;
        ActiveTarget = null;
        _setInputSuppressed(false);
        return target;
    }

    public void Cancel()
    {
        ActiveTarget = null;
        _setInputSuppressed(false);
    }
}

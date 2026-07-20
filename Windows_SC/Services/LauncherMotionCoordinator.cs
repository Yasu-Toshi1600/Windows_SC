using System;

namespace Windows_SC.Services;

internal enum LauncherMotionState
{
    Hidden,
    AwaitingStartConfirmation,
    EnteringWithStart,
    EnteringManual,
    VisibleWithStart,
    VisibleInteractive,
    Exiting
}

internal sealed class LauncherMotionCoordinator(DiagnosticLogger logger)
{
    public LauncherMotionState State { get; private set; } = LauncherMotionState.Hidden;

    public bool IsWindowVisible => State is LauncherMotionState.EnteringWithStart
        or LauncherMotionState.EnteringManual
        or LauncherMotionState.VisibleWithStart
        or LauncherMotionState.VisibleInteractive
        or LauncherMotionState.Exiting;

    public bool IsInteractive => State == LauncherMotionState.VisibleInteractive;

    public void AwaitStartConfirmation() =>
        Transition(LauncherMotionState.AwaitingStartConfirmation, "windows-key");

    public void CancelStartConfirmation(string reason)
    {
        if (State == LauncherMotionState.AwaitingStartConfirmation)
        {
            Transition(LauncherMotionState.Hidden, reason);
        }
    }

    public void BeginEntrance(bool startLinked, string reason) =>
        Transition(
            startLinked
                ? LauncherMotionState.EnteringWithStart
                : LauncherMotionState.EnteringManual,
            reason);

    public void CompleteEntrance()
    {
        if (State == LauncherMotionState.EnteringWithStart)
        {
            Transition(LauncherMotionState.VisibleWithStart, "entrance-completed");
        }
        else if (State == LauncherMotionState.EnteringManual)
        {
            Transition(LauncherMotionState.VisibleInteractive, "manual-entrance-completed");
        }
    }

    public void MarkInteractive(string reason)
    {
        if (State is LauncherMotionState.EnteringWithStart
            or LauncherMotionState.EnteringManual
            or LauncherMotionState.VisibleWithStart)
        {
            Transition(LauncherMotionState.VisibleInteractive, reason);
        }
    }

    public bool BeginExit(string reason)
    {
        if (!IsWindowVisible || State == LauncherMotionState.Exiting)
        {
            return false;
        }

        Transition(LauncherMotionState.Exiting, reason);
        return true;
    }

    public void CompleteExit(string reason) =>
        Transition(LauncherMotionState.Hidden, reason);

    private void Transition(LauncherMotionState next, string reason)
    {
        if (State == next)
        {
            return;
        }

        LauncherMotionState previous = State;
        State = next;
        logger.Write($"[MotionState] from={previous} to={next} reason={reason}");
    }
}

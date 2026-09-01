namespace Windows_SC.Services;

internal sealed class StartLinkedPresentationRecoveryGuard
{
    public bool IsBlocked { get; private set; }

    public void BlockUntilStartMenuHidden() => IsBlocked = true;

    public void Reset() => IsBlocked = false;

    public bool ShouldAllowPresentation(bool isStartMenuVisible)
    {
        if (!isStartMenuVisible)
        {
            IsBlocked = false;
            return false;
        }

        return !IsBlocked;
    }
}

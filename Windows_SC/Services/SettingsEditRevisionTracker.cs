namespace Windows_SC.Services;

internal sealed class SettingsEditRevisionTracker
{
    private long _currentRevision;
    private long _savedRevision;

    public long CurrentRevision => _currentRevision;

    public bool IsDirty => _currentRevision != _savedRevision;

    public void MarkChanged()
    {
        _currentRevision++;
    }

    public void MarkSaved(long revision)
    {
        if (revision < 0 || revision > _currentRevision)
        {
            throw new System.ArgumentOutOfRangeException(nameof(revision));
        }

        _savedRevision = System.Math.Max(_savedRevision, revision);
    }
}

namespace Windows_SC.Services;

internal interface IStartupService
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

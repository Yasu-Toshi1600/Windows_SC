namespace Windows_SC.ViewModels;

internal sealed class RegisteredAudioDeviceEditorViewModel(
    string id,
    string displayName,
    bool isAvailable)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public bool IsAvailable { get; } = isAvailable;
    public string DisplayLabel => IsAvailable
        ? DisplayName
        : $"{DisplayName}（利用不可）";
}

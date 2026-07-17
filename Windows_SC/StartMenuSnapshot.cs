using Windows.Graphics;

namespace Windows_SC;

internal readonly record struct StartMenuSnapshot(
    bool IsVisible,
    RectInt32? Bounds,
    bool IsPhonePanelVisible,
    RectInt32? PhonePanelBounds)
{
    public static StartMenuSnapshot Hidden => new(false, null, false, null);
}

namespace Windows_SC.Services;


internal static class LogValue
{
    public static string Normalize(string value) =>
        value.Replace('"', '\'')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\0', ' ');
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Windows_SC.Services;

internal static partial class LogPrivacySanitizer
{
    private static readonly IReadOnlyList<(string Path, string Token)> KnownPaths =
        CreateKnownPaths();

    public static string Sanitize(string value)
    {
        string sanitized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\0', ' ');

        foreach ((string path, string token) in KnownPaths)
        {
            sanitized = sanitized.Replace(path, token, StringComparison.OrdinalIgnoreCase);
        }

        sanitized = SensitiveKnownPathRegex().Replace(sanitized, "${prefix}\\<redacted>");
        return UserProfilePathRegex().Replace(sanitized, "${root}<user>");
    }

    private static IReadOnlyList<(string Path, string Token)> CreateKnownPaths()
    {
        List<(string Path, string Token)> paths = [];
        AddPath(paths, Path.GetDirectoryName(Environment.ProcessPath), "%APPDIR%");
        AddPath(paths, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        AddPath(paths, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
        AddPath(paths, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "%TEMP%");
        AddPath(paths, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        return paths;
    }

    private static void AddPath(
        ICollection<(string Path, string Token)> paths,
        string? path,
        string token)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add((path.TrimEnd(Path.DirectorySeparatorChar), token));
        }
    }

    [GeneratedRegex("(?i)(?<root>[A-Z]:\\\\Users\\\\)[^\\\\\\r\\n\\\"']+")]
    private static partial Regex UserProfilePathRegex();

    [GeneratedRegex("(?i)(?<prefix>%(?:APPDIR|LOCALAPPDATA|APPDATA|TEMP|USERPROFILE)%)\\\\[^\\\"'\\r\\n]*")]
    private static partial Regex SensitiveKnownPathRegex();
}

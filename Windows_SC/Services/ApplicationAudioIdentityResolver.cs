using System;
using System.Diagnostics;
using System.IO;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal sealed class ApplicationAudioIdentityResolver : IApplicationAudioIdentityResolver
{
    public ApplicationAudioTarget? Resolve(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string identifier = NormalizeExecutablePath(path);
            string? displayName = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = Path.GetFileNameWithoutExtension(path);
            }

            return new ApplicationAudioTarget(
                ApplicationAudioIdentifierKind.ExecutablePath,
                identifier,
                displayName);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }

    internal static string NormalizeExecutablePath(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToLowerInvariant();
}

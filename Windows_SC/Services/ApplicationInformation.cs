using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Windows_SC.Services;

internal static class ApplicationInformation
{
    private const string WindowsCurrentVersionKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    internal static string Version
    {
        get
        {
            Assembly assembly = typeof(ApplicationInformation).Assembly;
            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            return string.IsNullOrWhiteSpace(informationalVersion)
                ? assembly.GetName().Version?.ToString() ?? "不明"
                : informationalVersion;
        }
    }

    internal static string WindowsVersion
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(WindowsCurrentVersionKey);
                if (key is null)
                {
                    return RuntimeInformation.OSDescription;
                }

                string? buildText = key.GetValue("CurrentBuildNumber")?.ToString();
                if (!int.TryParse(buildText, out int build))
                {
                    return RuntimeInformation.OSDescription;
                }

                string productName = key.GetValue("ProductName")?.ToString()
                    ?? "Microsoft Windows";
                if (build >= 22000)
                {
                    productName = productName.Replace(
                        "Windows 10",
                        "Windows 11",
                        StringComparison.OrdinalIgnoreCase);
                }

                string? displayVersion = key.GetValue("DisplayVersion")?.ToString();
                int? updateBuildRevision = key.GetValue("UBR") switch
                {
                    int value => value,
                    object value when int.TryParse(value.ToString(), out int parsed) => parsed,
                    _ => null
                };
                string fullBuild = updateBuildRevision is { } revision
                    ? $"{build}.{revision}"
                    : build.ToString();
                string versionText = string.IsNullOrWhiteSpace(displayVersion)
                    ? string.Empty
                    : $" {displayVersion}";

                return $"{productName}{versionText} (OS ビルド {fullBuild})";
            }
            catch (Exception exception) when (exception is SecurityException
                or UnauthorizedAccessException
                or IOException)
            {
                return RuntimeInformation.OSDescription;
            }
        }
    }
}

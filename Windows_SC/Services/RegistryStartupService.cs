using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace Windows_SC.Services;

internal sealed class RegistryStartupService(DiagnosticLogger logger) : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Windows_SC";

    public bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value
                && string.Equals(value, GetStartupCommand(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("自動起動設定を開けませんでした。");

        if (enabled)
        {
            string command = GetStartupCommand();
            key.SetValue(ValueName, command, RegistryValueKind.String);
            logger.Write($"[Startup] action=enable command=\"{command}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            logger.Write("[Startup] action=disable");
        }
    }

    private static string GetStartupCommand()
    {
        string executablePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("実行ファイルのパスを取得できませんでした。");
        return $"\"{executablePath}\"";
    }
}

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
            if (key.GetValue(ValueName) is not string actualCommand
                || !string.Equals(
                    actualCommand,
                    command,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("自動起動設定を登録後に確認できませんでした。");
            }

            logger.Write($"[Startup] action=enable result=success command=\"{command}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            if (key.GetValue(ValueName) is not null)
            {
                throw new InvalidOperationException("自動起動設定を解除後に確認できませんでした。");
            }

            logger.Write("[Startup] action=disable result=success");
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

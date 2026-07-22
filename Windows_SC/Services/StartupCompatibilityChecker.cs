using System;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

internal static class StartupCompatibilityChecker
{
    private const int MinimumWindowsBuild = 26200;
    private const uint AbmGetTaskbarPos = 0x00000005;
    private const uint AbeBottom = 3;
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;

    public static StartupCompatibilityResult Check()
    {
        Version osVersion = Environment.OSVersion.Version;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindowsBuild))
        {
            return StartupCompatibilityResult.Unsupported(
                $"Windows 11 バージョン25H2以降が必要です。現在のOSビルド: {osVersion.Build}",
                $"os-build={osVersion.Build}");
        }

        if (RuntimeInformation.OSArchitecture != Architecture.X64
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return StartupCompatibilityResult.Unsupported(
                "x64版Windowsとx64版Windows_SCが必要です。",
                $"os-architecture={RuntimeInformation.OSArchitecture} " +
                $"process-architecture={RuntimeInformation.ProcessArchitecture}");
        }

        AppBarData taskbar = new()
        {
            Size = (uint)Marshal.SizeOf<AppBarData>()
        };
        if (SHAppBarMessage(AbmGetTaskbarPos, ref taskbar) == 0)
        {
            return StartupCompatibilityResult.Unsupported(
                "タスクバーの位置を確認できませんでした。",
                "taskbar-edge=unknown");
        }

        if (taskbar.Edge != AbeBottom)
        {
            return StartupCompatibilityResult.Unsupported(
                "タスクバーを画面の下端に配置してください。",
                $"taskbar-edge={taskbar.Edge}");
        }

        return StartupCompatibilityResult.Supported(
            $"os-build={osVersion.Build} " +
            $"os-architecture={RuntimeInformation.OSArchitecture} " +
            $"process-architecture={RuntimeInformation.ProcessArchitecture} " +
            "taskbar-edge=bottom");
    }

    public static void ShowError(string reason)
    {
        string message =
            "Windows_SCを起動できません。\n\n" +
            "対応環境:\n" +
            "・Windows 11 バージョン25H2以降\n" +
            "・x64\n" +
            "・下端タスクバー\n\n" +
            $"理由: {reason}";
        _ = MessageBox(
            nint.Zero,
            message,
            "Windows_SC - 対応環境エラー",
            MbOk | MbIconError | MbSetForeground);
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(
        nint windowHandle,
        string text,
        string caption,
        uint type);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint WindowHandle;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Rectangle;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal readonly record struct StartupCompatibilityResult(
    bool IsSupported,
    string UserMessage,
    string LogDetails)
{
    public static StartupCompatibilityResult Supported(string logDetails) =>
        new(true, string.Empty, logDetails);

    public static StartupCompatibilityResult Unsupported(string userMessage, string logDetails) =>
        new(false, userMessage, logDetails);
}

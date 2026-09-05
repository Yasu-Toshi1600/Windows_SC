using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

internal static class StartupCompatibilityChecker
{
    private const int MinimumWindowsBuild = 26200;
    private const int MinimumDisplayShortSide = 1080;
    private const uint AbmGetTaskbarPos = 0x00000005;
    private const uint AbeBottom = 3;
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconWarning = 0x00000030;
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

        (string displayWarning, string displayLogDetails) = CheckDisplayResolution();
        return StartupCompatibilityResult.Supported(
            $"os-build={osVersion.Build} " +
            $"os-architecture={RuntimeInformation.OSArchitecture} " +
            $"process-architecture={RuntimeInformation.ProcessArchitecture} " +
            $"taskbar-edge=bottom {displayLogDetails}",
            displayWarning);
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

    public static void ShowWarning(string warning)
    {
        string message =
            $"{warning}\n\n" +
            "［OK］を押すとこのまま起動します。";
        _ = MessageBox(
            nint.Zero,
            message,
            "Windows_SC - 解像度の警告",
            MbOk | MbIconWarning | MbSetForeground);
    }

    public static void ShowFutureSettingsVersionError(long foundVersion, int supportedVersion)
    {
        string message =
            "この設定ファイルは、現在のWindows_SCより新しい版で作成されています。\n\n" +
            $"設定スキーマ: {foundVersion}\n" +
            $"このアプリが対応する設定スキーマ: {supportedVersion}\n\n" +
            "設定を保護するため、内容を変更せずに終了します。";
        _ = MessageBox(
            nint.Zero,
            message,
            "Windows_SC - 新しい設定を検出しました",
            MbOk | MbIconError | MbSetForeground);
    }

    private static (string WarningMessage, string LogDetails) CheckDisplayResolution()
    {
        try
        {
            IReadOnlyList<DisplayArea> displayAreas = DisplayArea.FindAll();
            if (displayAreas.Count == 0)
            {
                return (string.Empty, "display-resolutions=unknown display-warning=unknown");
            }

            List<string> resolutions = [];
            List<string> belowMinimum = [];
            foreach (DisplayArea displayArea in displayAreas)
            {
                int width = displayArea.OuterBounds.Width;
                int height = displayArea.OuterBounds.Height;
                string resolution = $"{width}x{height}";
                resolutions.Add(resolution);
                if (Math.Min(width, height) < MinimumDisplayShortSide)
                {
                    belowMinimum.Add(resolution);
                }
            }

            string logDetails =
                $"display-resolutions={string.Join(',', resolutions)} " +
                $"display-warning={(belowMinimum.Count > 0 ? "required" : "none")}";
            if (belowMinimum.Count == 0)
            {
                return (string.Empty, logDetails);
            }

            string warning =
                "接続中のディスプレイに1080p未満の解像度があります。\n" +
                $"対象: {string.Join(", ", belowMinimum)}\n\n" +
                "1080p未満の環境は現在検証できていないため、表示の見切れや" +
                "配置のずれが発生する可能性があります。可能であれば、縦横の短い方が" +
                "1080ピクセル以上のディスプレイで使用してください。";
            return (warning, logDetails);
        }
        catch (Exception exception)
        {
            return (
                string.Empty,
                $"display-resolutions=unknown display-warning=unknown " +
                $"display-check-exception={exception.GetType().Name}");
        }
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
    string WarningMessage,
    string LogDetails)
{
    public static StartupCompatibilityResult Supported(
        string logDetails,
        string warningMessage = "") =>
        new(true, string.Empty, warningMessage, logDetails);

    public static StartupCompatibilityResult Unsupported(string userMessage, string logDetails) =>
        new(false, userMessage, string.Empty, logDetails);
}

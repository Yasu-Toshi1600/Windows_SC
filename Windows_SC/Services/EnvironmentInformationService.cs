using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics;

namespace Windows_SC.Services;

internal sealed class EnvironmentInformationService(DiagnosticLogger logger)
{
    private readonly object _snapshotGate = new();
    private string? _lastLoggedFingerprint;

    internal string CreateReport()
    {
        EnvironmentSnapshot snapshot = CaptureSnapshot();
        StringBuilder information = new();
        information.AppendLine("Windows_SC 診断情報");
        information.AppendLine($"作成日時: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        information.AppendLine($"アプリ: {snapshot.ApplicationVersion}");
        information.AppendLine($"OS: {snapshot.WindowsVersion}");
        information.AppendLine($"OSアーキテクチャ: {snapshot.OsArchitecture}");
        information.AppendLine($"プロセスアーキテクチャ: {snapshot.ProcessArchitecture}");
        information.AppendLine($".NET: {snapshot.FrameworkDescription}");
        AppendMonitorReport(information, snapshot);
        information.AppendLine(
            $"詳細診断ログ: {(snapshot.IsDetailedLoggingEnabled ? "有効" : "無効")}");
        return information.ToString();
    }

    internal void LogIfChanged(string reason, bool force = false)
    {
        EnvironmentSnapshot snapshot = CaptureSnapshot();
        string fingerprint = snapshot.CreateFingerprint();

        lock (_snapshotGate)
        {
            if (!force && string.Equals(
                    _lastLoggedFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                return;
            }

            _lastLoggedFingerprint = fingerprint;
        }

        string safeReason = NormalizeLogValue(reason);
        logger.Write(
            $"[Environment] reason={safeReason} changed=true " +
            $"app=\"{NormalizeLogValue(snapshot.ApplicationVersion)}\" " +
            $"os=\"{NormalizeLogValue(snapshot.WindowsVersion)}\" " +
            $"os-architecture={snapshot.OsArchitecture} " +
            $"process-architecture={snapshot.ProcessArchitecture} " +
            $"dotnet=\"{NormalizeLogValue(snapshot.FrameworkDescription)}\" " +
            $"monitors={snapshot.Monitors.Count} " +
            $"monitor-result={(snapshot.MonitorErrorType is null ? "success" : "failed")} " +
            $"detailed-logging={(snapshot.IsDetailedLoggingEnabled ? "enabled" : "disabled")}");

        if (snapshot.MonitorErrorType is not null)
        {
            logger.Write(
                $"[EnvironmentMonitor] result=failed exception={snapshot.MonitorErrorType}");
            return;
        }

        for (int index = 0; index < snapshot.Monitors.Count; index++)
        {
            MonitorSnapshot monitor = snapshot.Monitors[index];
            logger.Write(
                $"[EnvironmentMonitor] index={index + 1} primary={monitor.IsPrimary.ToString().ToLowerInvariant()} " +
                $"resolution={monitor.Bounds.Width}x{monitor.Bounds.Height} " +
                $"position=({monitor.Bounds.X},{monitor.Bounds.Y}) " +
                $"work-area=({monitor.WorkArea.X},{monitor.WorkArea.Y}," +
                $"{monitor.WorkArea.Width},{monitor.WorkArea.Height}) " +
                $"dpi={monitor.Dpi} scale={monitor.ScalePercent}% " +
                $"refresh-rate={(monitor.RefreshRate > 1 ? $"{monitor.RefreshRate}Hz" : "unknown")}");
        }
    }

    private EnvironmentSnapshot CaptureSnapshot()
    {
        List<MonitorSnapshot> monitors = [];
        string? monitorErrorType = null;

        try
        {
            monitors = DisplayArea.FindAll()
                .Select(displayArea =>
                {
                    RectInt32 bounds = displayArea.OuterBounds;
                    RectInt32 workArea = displayArea.WorkArea;
                    (uint dpi, int refreshRate) = GetMonitorDetails(bounds);
                    return new MonitorSnapshot(
                        displayArea.IsPrimary,
                        bounds,
                        workArea,
                        dpi,
                        (int)Math.Round(dpi * 100d / 96d),
                        refreshRate);
                })
                .OrderByDescending(monitor => monitor.IsPrimary)
                .ThenBy(monitor => monitor.Bounds.X)
                .ThenBy(monitor => monitor.Bounds.Y)
                .ToList();
        }
        catch (Exception exception)
        {
            monitorErrorType = exception.GetType().Name;
        }

        return new EnvironmentSnapshot(
            ApplicationInformation.Version,
            ApplicationInformation.WindowsVersion,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            logger.IsDetailedLoggingEnabled,
            monitors,
            monitorErrorType);
    }

    private static void AppendMonitorReport(
        StringBuilder information,
        EnvironmentSnapshot snapshot)
    {
        if (snapshot.MonitorErrorType is not null)
        {
            information.AppendLine(
                $"モニター情報: 取得失敗 ({snapshot.MonitorErrorType})");
            return;
        }

        information.AppendLine($"モニター数: {snapshot.Monitors.Count}");
        for (int index = 0; index < snapshot.Monitors.Count; index++)
        {
            MonitorSnapshot monitor = snapshot.Monitors[index];
            string refreshRateText = monitor.RefreshRate > 1
                ? $", リフレッシュレート={monitor.RefreshRate}Hz"
                : string.Empty;
            information.AppendLine(
                $"モニター {index + 1}: " +
                $"{(monitor.IsPrimary ? "メイン, " : string.Empty)}" +
                $"解像度={monitor.Bounds.Width}x{monitor.Bounds.Height}, " +
                $"配置=({monitor.Bounds.X},{monitor.Bounds.Y}), " +
                $"作業領域={monitor.WorkArea.Width}x{monitor.WorkArea.Height}, " +
                $"DPI={monitor.Dpi}, 拡大率={monitor.ScalePercent}%" +
                refreshRateText);
        }
    }

    private static string NormalizeLogValue(string value) =>
        value.Replace('"', '\'')
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static (uint Dpi, int RefreshRate) GetMonitorDetails(RectInt32 bounds)
    {
        NativePoint center = new()
        {
            X = bounds.X + (bounds.Width / 2),
            Y = bounds.Y + (bounds.Height / 2)
        };
        nint monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        uint dpi = monitor != nint.Zero
            && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0
                ? dpiX
                : 96;
        int refreshRate = 0;

        MonitorInfoEx monitorInfo = new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty
        };
        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            nint deviceContext = CreateDC(
                "DISPLAY",
                monitorInfo.DeviceName,
                null,
                nint.Zero);
            if (deviceContext != nint.Zero)
            {
                refreshRate = GetDeviceCaps(deviceContext, VerticalRefreshRate);
                _ = DeleteDC(deviceContext);
            }
        }

        return (dpi, refreshRate);
    }

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int VerticalRefreshRate = 116;

    private sealed record EnvironmentSnapshot(
        string ApplicationVersion,
        string WindowsVersion,
        string OsArchitecture,
        string ProcessArchitecture,
        string FrameworkDescription,
        bool IsDetailedLoggingEnabled,
        IReadOnlyList<MonitorSnapshot> Monitors,
        string? MonitorErrorType)
    {
        internal string CreateFingerprint()
        {
            StringBuilder fingerprint = new();
            fingerprint.Append(ApplicationVersion).Append('|')
                .Append(WindowsVersion).Append('|')
                .Append(OsArchitecture).Append('|')
                .Append(ProcessArchitecture).Append('|')
                .Append(FrameworkDescription).Append('|')
                .Append(IsDetailedLoggingEnabled).Append('|')
                .Append(MonitorErrorType);

            foreach (MonitorSnapshot monitor in Monitors)
            {
                fingerprint.Append('|')
                    .Append(monitor.IsPrimary).Append(',')
                    .Append(monitor.Bounds.X).Append(',')
                    .Append(monitor.Bounds.Y).Append(',')
                    .Append(monitor.Bounds.Width).Append(',')
                    .Append(monitor.Bounds.Height).Append(',')
                    .Append(monitor.WorkArea.X).Append(',')
                    .Append(monitor.WorkArea.Y).Append(',')
                    .Append(monitor.WorkArea.Width).Append(',')
                    .Append(monitor.WorkArea.Height).Append(',')
                    .Append(monitor.Dpi).Append(',')
                    .Append(monitor.RefreshRate);
            }

            return fingerprint.ToString();
        }
    }

    private sealed record MonitorSnapshot(
        bool IsPrimary,
        RectInt32 Bounds,
        RectInt32 WorkArea,
        uint Dpi,
        int ScalePercent,
        int RefreshRate);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateDC(
        string driver,
        string device,
        string? output,
        nint initializationData);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint deviceContext, int index);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}

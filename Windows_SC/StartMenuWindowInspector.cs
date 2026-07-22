using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics;

namespace Windows_SC;

internal sealed class StartMenuWindowInspector
{
    private const int DwmwaCloaked = 14;

    private static readonly HashSet<string> CandidateProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost",
        "SearchHost",
        "ShellExperienceHost",
        "PhoneExperienceHost"
    };

    private static readonly HashSet<string> StartMenuProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost",
        "SearchHost"
    };

    private readonly DiagnosticLogger _logger;
    private string _lastRejectedBoundsSignature = string.Empty;

    public StartMenuWindowInspector(DiagnosticLogger logger)
    {
        _logger = logger;
    }

    public void InspectAndLog()
    {
        List<WindowSnapshot> snapshots = [];

        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle) || !TryGetProcessName(windowHandle, out string processName))
            {
                return true;
            }

            if (!CandidateProcesses.Contains(processName))
            {
                return true;
            }

            if (GetWindowRect(windowHandle, out NativeRectangle rectangle))
            {
                snapshots.Add(new WindowSnapshot(
                    windowHandle,
                    processName,
                    GetClassName(windowHandle),
                    GetWindowText(windowHandle),
                    rectangle));
            }

            return true;
        }, IntPtr.Zero);

        _logger.WriteDetailed($"[StartPanelScan] candidates={snapshots.Count}");

        foreach (WindowSnapshot snapshot in snapshots)
        {
            NativeRectangle rectangle = snapshot.Rectangle;
            _logger.WriteDetailed(
                $"[StartPanelCandidate] process={snapshot.ProcessName} " +
                $"hwnd=0x{snapshot.WindowHandle.ToInt64():X} class=\"{snapshot.ClassName}\" " +
                $"title=\"{snapshot.Title}\" rect=({rectangle.Left},{rectangle.Top})-({rectangle.Right},{rectangle.Bottom}) " +
                $"size={rectangle.Right - rectangle.Left}x{rectangle.Bottom - rectangle.Top}");
        }

        if (snapshots.Count == 0)
        {
            _logger.WriteDetailed("[StartPanelScan] スタートメニュー／スマートフォン連携パネル候補を取得できませんでした。");
        }
    }

    public bool IsStartMenuVisible()
        => TryGetStartMenuBounds(out _);

    public bool TryGetStartMenuBounds(out RectInt32 bounds)
        => TryGetStartMenuBounds(preferredOwnerBounds: null, out bounds);

    public bool TryGetStartMenuBounds(
        RectInt32? preferredOwnerBounds,
        out RectInt32 bounds)
    {
        IntPtr foregroundWindow = GetForegroundWindow();
        if (foregroundWindow != IntPtr.Zero
            && TryGetProcessName(foregroundWindow, out string foregroundProcessName)
            && StartMenuProcesses.Contains(foregroundProcessName)
            && TryGetUsableBounds(foregroundWindow, out bounds))
        {
            return true;
        }

        List<StartWindowCandidate> candidates = [];

        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle)
                || IsWindowCloaked(windowHandle)
                || !TryGetProcessName(windowHandle, out string processName)
                || !StartMenuProcesses.Contains(processName)
                || !TryGetUsableBounds(windowHandle, out RectInt32 candidateBounds))
            {
                return true;
            }

            candidates.Add(new StartWindowCandidate(windowHandle, candidateBounds));
            return true;
        }, IntPtr.Zero);

        if (candidates.Count == 0)
        {
            bounds = default;
            return false;
        }

        if (preferredOwnerBounds is { } ownerBounds)
        {
            NativePoint ownerCenter = new(
                ownerBounds.X + (ownerBounds.Width / 2),
                ownerBounds.Y + (ownerBounds.Height / 2));
            IntPtr preferredMonitor = MonitorFromPoint(ownerCenter, MonitorDefaultToNearest);
            StartWindowCandidate? matchingCandidate = candidates
                .Where(candidate => MonitorFromWindow(
                    candidate.WindowHandle,
                    MonitorDefaultToNearest) == preferredMonitor)
                .OrderBy(candidate => DistanceSquared(candidate.Bounds, ownerCenter))
                .FirstOrDefault();
            if (matchingCandidate is not null)
            {
                bounds = matchingCandidate.Bounds;
                _logger.WriteDetailed(
                    $"[StartPanelSelection] source=start-button-monitor " +
                    $"candidates={candidates.Count} bounds=({bounds.X},{bounds.Y}," +
                    $"{bounds.Width},{bounds.Height})");
                return true;
            }

            _logger.Write(
                $"[StartPanelSelection] result=none reason=owner-monitor-mismatch " +
                $"candidates={candidates.Count}");
            bounds = default;
            return false;
        }

        bounds = candidates[0].Bounds;
        return true;
    }

    private static long DistanceSquared(RectInt32 bounds, NativePoint point)
    {
        long deltaX = bounds.X + (bounds.Width / 2L) - point.X;
        long deltaY = bounds.Y + (bounds.Height / 2L) - point.Y;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    private bool TryGetUsableBounds(IntPtr windowHandle, out RectInt32 bounds)
    {
        bounds = default;
        if (!GetWindowRect(windowHandle, out NativeRectangle rectangle))
        {
            return false;
        }

        int width = rectangle.Right - rectangle.Left;
        int height = rectangle.Bottom - rectangle.Top;
        if (width < 200 || height < 100)
        {
            return false;
        }

        RectInt32 candidateBounds = new(rectangle.Left, rectangle.Top, width, height);
        if (StartMenuBoundsValidator.CoversMostOfMonitor(candidateBounds))
        {
            string signature = $"{rectangle.Left}:{rectangle.Top}:{width}:{height}";
            if (signature != _lastRejectedBoundsSignature)
            {
                _lastRejectedBoundsSignature = signature;
                _logger.WriteDetailed(
                    $"[StartPanelCandidate] rejected=monitor-sized " +
                    $"rect=({rectangle.Left},{rectangle.Top},{width},{height})");
            }

            return false;
        }

        bounds = candidateBounds;
        return true;
    }

    private static bool IsWindowCloaked(IntPtr windowHandle)
    {
        int result = DwmGetWindowAttribute(
            windowHandle,
            DwmwaCloaked,
            out int cloaked,
            Marshal.SizeOf<int>());

        return result == 0 && cloaked != 0;
    }

    private static bool TryGetProcessName(IntPtr windowHandle, out string processName)
    {
        processName = string.Empty;
        GetWindowThreadProcessId(windowHandle, out uint processId);

        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(unchecked((int)processId));
            processName = process.ProcessName;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string GetClassName(IntPtr windowHandle)
    {
        StringBuilder value = new(256);
        _ = GetClassName(windowHandle, value, value.Capacity);
        return Sanitize(value.ToString());
    }

    private static string GetWindowText(IntPtr windowHandle)
    {
        int length = GetWindowTextLength(windowHandle);
        StringBuilder value = new(Math.Max(length + 1, 1));
        _ = GetWindowText(windowHandle, value, value.Capacity);
        return Sanitize(value.ToString());
    }

    private static string Sanitize(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\"", "'", StringComparison.Ordinal);

    private sealed record WindowSnapshot(
        IntPtr WindowHandle,
        string ProcessName,
        string ClassName,
        string Title,
        NativeRectangle Rectangle);

    private sealed record StartWindowCandidate(IntPtr WindowHandle, RectInt32 Bounds);

    private const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRectangle
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }

    private delegate bool EnumWindowsProcedure(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProcedure callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder windowText, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out int attributeValue,
        int attributeSize);
}

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

internal static class MemoryFlightNativeSampler
{
    // EX2 requires the September 2023 update. The sentinel also detects older
    // implementations that succeed but only fill the EX prefix.
    internal static Counters ReadProcess()
    {
        Counters counters = new()
        {
            Size = (uint)Marshal.SizeOf<Counters>(),
            PrivateWorkingSet = nuint.MaxValue
        };
        if (!K32GetProcessMemoryInfo(new IntPtr(-1), ref counters, counters.Size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return counters;
    }

    internal static Performance? ReadSystem()
    {
        Performance data = new() { Size = (uint)Marshal.SizeOf<Performance>() };
        return K32GetPerformanceInfo(ref data, data.Size) ? data : null;
    }

    internal static long? ReadCpuMilliseconds() =>
        GetProcessTimes(new IntPtr(-1), out _, out _, out ulong kernel, out ulong user)
            ? (long)((kernel + user) / 10_000) : null;

    internal static uint? ReadHandles() =>
        GetProcessHandleCount(new IntPtr(-1), out uint count) ? count : null;

    internal static bool? ReadCloaked(IntPtr window) =>
        window != IntPtr.Zero && DwmGetWindowAttribute(window, 14, out uint cloaked, 4) == 0
            ? cloaked != 0 : null;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Counters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool,
            PeakNonPagedPool, NonPagedPool, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSet;
        public ulong SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Performance
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
            SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetPerformanceInfo(ref Performance information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out ulong creation, out ulong exit,
        out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute,
        out uint value, uint size);
}

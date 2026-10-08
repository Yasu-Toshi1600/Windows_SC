using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

// Owned and serialized by ProcessMemoryDiagnostics; never logs counter instance names.
internal sealed class ProcessGpuUsageSampler : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint MoreData = 0x800007D2;
    private readonly string _instancePrefix = $"pid_{Environment.ProcessId}_";
    private IntPtr _query;
    private IntPtr _counter;

    public string Sample()
    {
        if (_query == IntPtr.Zero)
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0
                || PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage",
                    IntPtr.Zero, out _counter) != 0)
            {
                Dispose();
                return "unavailable";
            }

            // Rate counters need two collections. No UI sleep or synchronous wait.
            if (PdhCollectQueryData(_query) != 0)
            {
                Dispose();
                return "unavailable";
            }
            return "warming-up";
        }

        if (PdhCollectQueryData(_query) != 0) return "unavailable";
        uint bufferSize = 0;
        uint itemCount = 0;
        if (PdhGetFormattedCounterArray(_counter, FormatDouble, ref bufferSize,
                out itemCount, IntPtr.Zero) != MoreData || bufferSize == 0)
            return "unavailable";

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
        try
        {
            if (PdhGetFormattedCounterArray(_counter, FormatDouble, ref bufferSize,
                    out itemCount, buffer) != 0) return "unavailable";

            double? maximum = null;
            int itemSize = Marshal.SizeOf<CounterItem>();
            for (int index = 0; index < itemCount; index++)
            {
                CounterItem item = Marshal.PtrToStructure<CounterItem>(
                    IntPtr.Add(buffer, checked(index * itemSize)));
                string? name = Marshal.PtrToStringUni(item.Name);
                if (name?.StartsWith(_instancePrefix, StringComparison.Ordinal) == true
                    && item.Value.Status is 0 or 1 && double.IsFinite(item.Value.Value))
                    maximum = Math.Max(maximum ?? 0, item.Value.Value);
            }

            // Missing instances must not be reported as a measured zero.
            return maximum is { } value
                ? Math.Clamp(value, 0, 100).ToString("F3", CultureInfo.InvariantCulture)
                : "unavailable";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format,
        ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

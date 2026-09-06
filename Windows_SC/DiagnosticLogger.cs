using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Windows_SC.Services;

namespace Windows_SC;

internal sealed class DiagnosticLogger : IDisposable
{
    private const long MaximumLogSize = 2 * 1024 * 1024;
    private const int MaximumPendingEntries = 5000;
    private const int RetainedFileCount = 4;
    private readonly string _logDirectoryPath;
    private readonly string _logFilePath;
    private readonly string _detailedLogFilePath;
    private readonly string[] _normalLogPaths;
    private readonly string[] _detailedLogPaths;
    private readonly ConcurrentQueue<LogEntry> _pendingEntries = new();
    private readonly AutoResetEvent _writeRequested = new(false);
    private readonly Thread _writerThread;
    private readonly object _aggregationGate = new();
    private readonly object _drainGate = new();
    private readonly object _fileGate = new();
    private AggregationState _normalAggregation;
    private AggregationState _detailedAggregation;
    private long _droppedNormalEntries;
    private long _droppedDetailedEntries;
    private long _detailedLoggingExpiresUtcTicks;
    private int _detailedLoggingAlwaysEnabled;
    private int _pendingEntryCount;
    private int _isDisposed;

    public DiagnosticLogger()
    {
        IReadOnlyList<string> migrationMessages = ApplicationDataPaths.MigrateLegacyData();
        _logDirectoryPath = ApplicationDataPaths.LogDirectoryPath;

        Directory.CreateDirectory(_logDirectoryPath);
        _logFilePath = Path.Combine(_logDirectoryPath, "window-diagnostics.log");
        _detailedLogFilePath = Path.Combine(_logDirectoryPath, "window-diagnostics.detail.log");
        _normalLogPaths = CreateRetentionPaths(_logFilePath, "window-diagnostics.previous.log");
        _detailedLogPaths = CreateRetentionPaths(
            _detailedLogFilePath,
            "window-diagnostics.detail.previous.log");
        RotateIfNeeded(_normalLogPaths);
        RotateIfNeeded(_detailedLogPaths);
        EnsureFileExists(_logFilePath);
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "Windows_SC diagnostic writer"
        };
        _writerThread.Start();

        foreach (string message in migrationMessages)
        {
            Write(message);
        }
    }

    public string LogFilePath => _logFilePath;

    public string DetailedLogFilePath => _detailedLogFilePath;

    public string LogDirectoryPath => _logDirectoryPath;

    public bool IsDetailedLoggingEnabled =>
        IsDetailedLoggingAlwaysEnabled
        || Volatile.Read(ref _detailedLoggingExpiresUtcTicks) > DateTime.UtcNow.Ticks;

    public bool IsDetailedLoggingAlwaysEnabled =>
        Volatile.Read(ref _detailedLoggingAlwaysEnabled) != 0;

    public DateTimeOffset? DetailedLoggingExpiresAt
    {
        get
        {
            long ticks = Volatile.Read(ref _detailedLoggingExpiresUtcTicks);
            return ticks <= DateTime.UtcNow.Ticks
                ? null
                : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void ConfigureDetailedLogging(DateTimeOffset? expiresAt, bool alwaysEnabled = false)
    {
        long ticks = !alwaysEnabled
            && expiresAt is { } expiration
            && expiration > DateTimeOffset.UtcNow
            ? expiration.UtcDateTime.Ticks
            : 0;
        Interlocked.Exchange(ref _detailedLoggingExpiresUtcTicks, ticks);
        Interlocked.Exchange(ref _detailedLoggingAlwaysEnabled, alwaysEnabled ? 1 : 0);
        Write(
            alwaysEnabled
                ? "[Diagnostics] action=configure-detailed-logging result=success " +
                  "state=enabled mode=continuous"
                : ticks == 0
                ? "[Diagnostics] action=configure-detailed-logging result=success state=disabled"
                : $"[Diagnostics] action=configure-detailed-logging result=success " +
                  $"state=enabled mode=temporary " +
                  $"expires-at={new DateTimeOffset(ticks, TimeSpan.Zero):O}");
    }

    public void Write(string message) =>
        EnqueueAggregated(
            message,
            includeNormalLog: true,
            includeDetailedLog: IsDetailedLoggingEnabled,
            isDetailedOnly: false);

    public void WriteDetailed(string message)
    {
        if (IsDetailedLoggingEnabled)
        {
            EnqueueAggregated(
                message,
                includeNormalLog: false,
                includeDetailedLog: true,
                isDetailedOnly: true);
        }
    }

    public void WriteCritical(string message)
    {
        Write(message);
        FlushAggregatedErrors();
        FlushPendingEntries();
    }

    public void RotateLogs(string reason)
    {
        FlushAggregatedErrors();
        FlushPendingEntries();
        try
        {
            lock (_fileGate)
            {
                Rotate(_normalLogPaths);
                Rotate(_detailedLogPaths);
                EnsureFileExists(_logFilePath);
            }

            Write($"[Diagnostics] action=rotate-logs result=success reason={reason}");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            Write(
                $"[Diagnostics] action=rotate-logs result=failed reason={reason} " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
    }

    public void ClearLogs()
    {
        FlushAggregatedErrors();
        FlushPendingEntries();
        lock (_fileGate)
        {
            foreach (string path in _normalLogPaths)
            {
                DeleteIfExists(path);
            }

            foreach (string path in _detailedLogPaths)
            {
                DeleteIfExists(path);
            }
        }
    }

    public void Dispose()
    {
        lock (_aggregationGate)
        {
            if (Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }
            FlushAggregatedErrors();
            Interlocked.Exchange(ref _isDisposed, 1);
            _writeRequested.Set();
        }

        if (_writerThread.Join(TimeSpan.FromSeconds(2)))
        {
            _writeRequested.Dispose();
        }
    }

    private void EnqueueAggregated(
        string message,
        bool includeNormalLog,
        bool includeDetailedLog,
        bool isDetailedOnly)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        string sanitized = LogPrivacySanitizer.Sanitize(message);
        lock (_aggregationGate)
        {
            if (Volatile.Read(ref _isDisposed) != 0) return;
            ref AggregationState state = ref (isDetailedOnly
                ? ref _detailedAggregation
                : ref _normalAggregation);
            bool isFailure = sanitized.Contains("result=failed", StringComparison.Ordinal);
            if (isFailure
                && state.Message == sanitized
                && state.IncludeNormalLog == includeNormalLog
                && state.IncludeDetailedLog == includeDetailedLog)
            {
                state.Occurrences++;
                return;
            }

            FlushAggregation(ref state, isDetailedOnly ? "detailed" : "normal");
            EnqueueSanitized(sanitized, includeNormalLog, includeDetailedLog);
            state = isFailure
                ? new AggregationState(sanitized, includeNormalLog, includeDetailedLog, 1)
                : default;
        }
    }

    private void FlushAggregatedErrors()
    {
        lock (_aggregationGate)
        {
            FlushAggregation(ref _normalAggregation, "normal");
            FlushAggregation(ref _detailedAggregation, "detailed");
        }
    }

    private void FlushAggregation(ref AggregationState state, string source)
    {
        if (state.Occurrences > 1)
        {
            EnqueueSanitized(
                $"[Diagnostics] action=aggregate-error result=success source={source} " +
                $"occurrences={state.Occurrences}",
                state.IncludeNormalLog,
                state.IncludeDetailedLog);
        }

        state = default;
    }

    private void EnqueueSanitized(
        string sanitizedMessage,
        bool includeNormalLog,
        bool includeDetailedLog)
    {
        if (!TryReserveQueueEntry())
        {
            if (includeNormalLog)
            {
                Interlocked.Increment(ref _droppedNormalEntries);
            }

            if (includeDetailedLog)
            {
                Interlocked.Increment(ref _droppedDetailedEntries);
            }

            _writeRequested.Set();
            return;
        }

        _pendingEntries.Enqueue(new LogEntry(
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} " +
            $"{sanitizedMessage}{Environment.NewLine}",
            includeNormalLog,
            includeDetailedLog));
        _writeRequested.Set();
    }

    private bool TryReserveQueueEntry()
    {
        while (true)
        {
            int count = Volatile.Read(ref _pendingEntryCount);
            if (count >= MaximumPendingEntries)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _pendingEntryCount, count + 1, count) == count)
            {
                return true;
            }
        }
    }

    private void WriterLoop()
    {
        while (Volatile.Read(ref _isDisposed) == 0)
        {
            _writeRequested.WaitOne();
            FlushPendingEntries();
        }

        FlushPendingEntries();
    }

    private void FlushPendingEntries()
    {
        lock (_drainGate)
        {
            StringBuilder normalBatch = new();
            StringBuilder detailedBatch = new();
            while (_pendingEntries.TryDequeue(out LogEntry entry))
            {
                Interlocked.Decrement(ref _pendingEntryCount);
                if (entry.IncludeNormalLog)
                {
                    normalBatch.Append(entry.Line);
                }

                if (entry.IncludeDetailedLog)
                {
                    detailedBatch.Append(entry.Line);
                }
            }

            AppendDroppedEntrySummary(
                normalBatch,
                Interlocked.Exchange(ref _droppedNormalEntries, 0));
            AppendDroppedEntrySummary(
                detailedBatch,
                Interlocked.Exchange(ref _droppedDetailedEntries, 0));
            if (normalBatch.Length == 0 && detailedBatch.Length == 0)
            {
                return;
            }

            try
            {
                lock (_fileGate)
                {
                    AppendIfNotEmpty(_logFilePath, normalBatch);
                    AppendIfNotEmpty(_detailedLogFilePath, detailedBatch);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                Debug.WriteLine($"診断ログを書き込めませんでした: {exception}");
            }
        }
    }

    private static void AppendDroppedEntrySummary(StringBuilder batch, long count)
    {
        if (count <= 0)
        {
            return;
        }

        batch.Append($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} ");
        batch.Append("[Diagnostics] action=drop-log-entry result=failed ");
        batch.Append($"reason=queue-capacity count={count} capacity={MaximumPendingEntries}");
        batch.Append(Environment.NewLine);
    }

    private static string[] CreateRetentionPaths(string currentPath, string previousFileName)
    {
        string directory = Path.GetDirectoryName(currentPath)!;
        string previousPath = Path.Combine(directory, previousFileName);
        return
        [
            currentPath,
            previousPath,
            previousPath.Replace(".log", ".2.log", StringComparison.Ordinal),
            previousPath.Replace(".log", ".3.log", StringComparison.Ordinal)
        ];
    }

    private static void RotateIfNeeded(string[] paths)
    {
        if (File.Exists(paths[0]) && new FileInfo(paths[0]).Length >= MaximumLogSize)
        {
            Rotate(paths);
        }
    }

    private static void Rotate(string[] paths)
    {
        DeleteIfExists(paths[RetainedFileCount - 1]);
        for (int index = RetainedFileCount - 1; index > 0; index--)
        {
            if (File.Exists(paths[index - 1]))
            {
                File.Move(paths[index - 1], paths[index], overwrite: true);
            }
        }
    }

    private static void AppendIfNotEmpty(string path, StringBuilder batch)
    {
        if (batch.Length > 0)
        {
            File.AppendAllText(
                path,
                batch.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static void EnsureFileExists(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private readonly record struct LogEntry(
        string Line,
        bool IncludeNormalLog,
        bool IncludeDetailedLog);

    private record struct AggregationState(
        string? Message,
        bool IncludeNormalLog,
        bool IncludeDetailedLog,
        int Occurrences);
}

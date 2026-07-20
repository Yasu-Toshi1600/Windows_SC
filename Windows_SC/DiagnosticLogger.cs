using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Windows_SC;

internal sealed class DiagnosticLogger : IDisposable
{
    private const long MaximumLogSize = 2 * 1024 * 1024;
    private readonly string _logDirectoryPath;
    private readonly string _logFilePath;
    private readonly string _previousLogFilePath;
    private readonly string _detailedLogFilePath;
    private readonly string _previousDetailedLogFilePath;
    private readonly ConcurrentQueue<LogEntry> _pendingEntries = new();
    private readonly AutoResetEvent _writeRequested = new(false);
    private readonly Thread _writerThread;
    private readonly object _fileGate = new();
    private long _detailedLoggingExpiresUtcTicks;
    private int _isDisposed;

    public DiagnosticLogger()
    {
        _logDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Windows_SC",
            "Logs");

        Directory.CreateDirectory(_logDirectoryPath);
        _logFilePath = Path.Combine(_logDirectoryPath, "window-diagnostics.log");
        _previousLogFilePath = Path.Combine(_logDirectoryPath, "window-diagnostics.previous.log");
        _detailedLogFilePath = Path.Combine(_logDirectoryPath, "window-diagnostics.detail.log");
        _previousDetailedLogFilePath = Path.Combine(
            _logDirectoryPath,
            "window-diagnostics.detail.previous.log");
        RotateIfNeeded(_logFilePath, _previousLogFilePath);
        RotateIfNeeded(_detailedLogFilePath, _previousDetailedLogFilePath);
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "Windows_SC diagnostic writer"
        };
        _writerThread.Start();
    }

    public string LogFilePath => _logFilePath;

    public string DetailedLogFilePath => _detailedLogFilePath;

    public string LogDirectoryPath => _logDirectoryPath;

    public bool IsDetailedLoggingEnabled =>
        Volatile.Read(ref _detailedLoggingExpiresUtcTicks) > DateTime.UtcNow.Ticks;

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

    public void ConfigureDetailedLogging(DateTimeOffset? expiresAt)
    {
        long ticks = expiresAt is { } expiration && expiration > DateTimeOffset.UtcNow
            ? expiration.UtcDateTime.Ticks
            : 0;
        Interlocked.Exchange(ref _detailedLoggingExpiresUtcTicks, ticks);
        Write(
            ticks == 0
                ? "[Diagnostics] detailed-logging=disabled"
                : $"[Diagnostics] detailed-logging=enabled expires-at={new DateTimeOffset(ticks, TimeSpan.Zero):O}");
    }

    public void Write(string message)
    {
        Enqueue(message, includeNormalLog: true, includeDetailedLog: IsDetailedLoggingEnabled);
    }

    public void WriteDetailed(string message)
    {
        if (IsDetailedLoggingEnabled)
        {
            Enqueue(message, includeNormalLog: false, includeDetailedLog: true);
        }
    }

    public void ClearLogs()
    {
        FlushPendingEntries();
        lock (_fileGate)
        {
            DeleteIfExists(_logFilePath);
            DeleteIfExists(_previousLogFilePath);
            DeleteIfExists(_detailedLogFilePath);
            DeleteIfExists(_previousDetailedLogFilePath);
        }
    }

    private void Enqueue(string message, bool includeNormalLog, bool includeDetailedLog)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        _pendingEntries.Enqueue(new LogEntry(
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} " +
            $"{Services.LogPrivacySanitizer.Sanitize(message)}{Environment.NewLine}",
            includeNormalLog,
            includeDetailedLog));
        _writeRequested.Set();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _writeRequested.Set();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _writeRequested.Dispose();
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
        if (_pendingEntries.IsEmpty)
        {
            return;
        }

        StringBuilder normalBatch = new();
        StringBuilder detailedBatch = new();
        while (_pendingEntries.TryDequeue(out LogEntry entry))
        {
            if (entry.IncludeNormalLog)
            {
                normalBatch.Append(entry.Line);
            }

            if (entry.IncludeDetailedLog)
            {
                detailedBatch.Append(entry.Line);
            }
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

    private static void RotateIfNeeded(string path, string previousPath)
    {
        if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogSize)
        {
            File.Move(path, previousPath, overwrite: true);
        }
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
}

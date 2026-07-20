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
    private readonly string _logFilePath;
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly AutoResetEvent _writeRequested = new(false);
    private readonly Thread _writerThread;
    private int _isDisposed;

    public DiagnosticLogger()
    {
        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Windows_SC",
            "Logs");

        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory, "window-diagnostics.log");
        RotateIfNeeded();
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "Windows_SC diagnostic writer"
        };
        _writerThread.Start();
    }

    public string LogFilePath => _logFilePath;

    public void Write(string message)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return;
        }

        _pendingLines.Enqueue(
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
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
            FlushPendingLines();
        }

        FlushPendingLines();
    }

    private void FlushPendingLines()
    {
        if (_pendingLines.IsEmpty)
        {
            return;
        }

        StringBuilder batch = new();
        while (_pendingLines.TryDequeue(out string? line))
        {
            batch.Append(line);
        }

        try
        {
            File.AppendAllText(
                _logFilePath,
                batch.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            Debug.WriteLine($"診断ログを書き込めませんでした: {exception}");
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logFilePath) || new FileInfo(_logFilePath).Length < MaximumLogSize)
        {
            return;
        }

        string previousLogPath = Path.Combine(
            Path.GetDirectoryName(_logFilePath)!,
            "window-diagnostics.previous.log");

        File.Move(_logFilePath, previousLogPath, overwrite: true);
    }
}

using System;
using System.IO;
using System.Text;

namespace Windows_SC;

internal sealed class DiagnosticLogger
{
    private const long MaximumLogSize = 2 * 1024 * 1024;
    private readonly string _logFilePath;
    private readonly object _syncRoot = new();

    public DiagnosticLogger()
    {
        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Windows_SC",
            "Logs");

        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory, "window-diagnostics.log");
        RotateIfNeeded();
    }

    public string LogFilePath => _logFilePath;

    public void Write(string message)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}";

        lock (_syncRoot)
        {
            File.AppendAllText(_logFilePath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

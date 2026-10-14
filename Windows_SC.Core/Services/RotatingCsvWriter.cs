using System;
using System.IO;
using System.Text;

namespace Windows_SC.Services;

/// <summary>Bounded CSV retention, including during a single long-running session.</summary>
public sealed class RotatingCsvWriter : IDisposable
{
    private readonly string _path;
    private readonly string _header;
    private readonly long _maximumBytes;
    private readonly int _fileCount;
    private StreamWriter? _writer;
    private long _bytes;

    public RotatingCsvWriter(string path, string header, long maximumBytes = 8 * 1024 * 1024,
        int fileCount = 4)
    {
        if (maximumBytes < Encoding.UTF8.GetByteCount(header + Environment.NewLine) + 1)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (fileCount < 1) throw new ArgumentOutOfRangeException(nameof(fileCount));
        _path = path;
        _header = header;
        _maximumBytes = maximumBytes;
        _fileCount = fileCount;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        OpenNext();
    }

    public void WriteLine(string line)
    {
        ObjectDisposedException.ThrowIf(_writer is null, this);
        long size = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
        if (size + Encoding.UTF8.GetByteCount(_header + Environment.NewLine) > _maximumBytes)
            throw new ArgumentException("CSV row exceeds file capacity.", nameof(line));
        if (_bytes + size > _maximumBytes) OpenNext();
        _writer!.WriteLine(line);
        _writer.Flush();
        _bytes += size;
    }

    private void OpenNext()
    {
        _writer?.Dispose();
        _writer = null;
        // Only these exact recorder-owned paths are rotated.
        if (File.Exists(PathAt(_fileCount - 1))) File.Delete(PathAt(_fileCount - 1));
        for (int index = _fileCount - 1; index > 0; index--)
            if (File.Exists(PathAt(index - 1))) File.Move(PathAt(index - 1), PathAt(index));
        _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write,
            FileShare.Read), new UTF8Encoding(false));
        _writer.WriteLine(_header);
        _writer.Flush();
        _bytes = Encoding.UTF8.GetByteCount(_header + Environment.NewLine);
    }

    private string PathAt(int index) => index == 0 ? _path
        : Path.Combine(Path.GetDirectoryName(_path)!,
            $"{Path.GetFileNameWithoutExtension(_path)}.{index}.csv");

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}

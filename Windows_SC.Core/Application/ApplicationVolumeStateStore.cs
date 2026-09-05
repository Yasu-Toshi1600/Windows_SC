using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal sealed record ApplicationVolumeStateEntry(
    string DeviceId,
    ApplicationAudioIdentifierKind TargetKind,
    string TargetId,
    int Volume);

internal interface IApplicationVolumeStateStore : IDisposable
{
    bool TryGet(string deviceId, ApplicationAudioTarget target, out int volume);

    bool Contains(string deviceId, ApplicationAudioTarget target);

    void Set(string deviceId, ApplicationAudioTarget target, int volume);

    Task FlushAsync(CancellationToken cancellationToken = default);
}

internal sealed class ApplicationVolumeStateStore : IApplicationVolumeStateStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);
    private readonly string _path;
    private readonly Action<string> _writeLog;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<StateKey, int> _entries = new();
    private Timer? _saveTimer;
    private bool _isDisposed;

    internal ApplicationVolumeStateStore(string path, Action<string> writeLog)
    {
        _path = path;
        _writeLog = writeLog;
        Load();
    }

    public bool TryGet(string deviceId, ApplicationAudioTarget target, out int volume)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(CreateKey(deviceId, target), out volume);
        }
    }

    public bool Contains(string deviceId, ApplicationAudioTarget target)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(CreateKey(deviceId, target));
        }
    }

    public void Set(string deviceId, ApplicationAudioTarget target, int volume)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _entries[CreateKey(deviceId, target)] = Math.Clamp(volume, 0, 100);
            _saveTimer ??= new Timer(_ => SaveFromTimer(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveSnapshot();
        return Task.CompletedTask;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            StateDocument? document = JsonSerializer.Deserialize<StateDocument>(
                File.ReadAllText(_path),
                JsonOptions);
            if (document?.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException("未対応のアプリ音量状態スキーマです。");
            }

            foreach (ApplicationVolumeStateEntry? entry in document.Entries ?? [])
            {
                if (entry is null)
                {
                    throw new InvalidDataException(
                        "The application volume state contains a null entry.");
                }

                if (string.IsNullOrWhiteSpace(entry.DeviceId)
                    || string.IsNullOrWhiteSpace(entry.TargetId)
                    || !Enum.IsDefined(entry.TargetKind)
                    || entry.Volume is < 0 or > 100)
                {
                    continue;
                }

                _entries[new StateKey(
                    NormalizeDeviceId(entry.DeviceId),
                    entry.TargetKind,
                    entry.TargetId)] = entry.Volume;
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            TryQuarantineCorruptFile();
            _writeLog(
                $"[ApplicationVolume] action=load-state result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
    }

    private void SaveFromTimer()
    {
        try
        {
            SaveSnapshot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _writeLog(
                $"[ApplicationVolume] action=save-state result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
    }

    private void SaveSnapshot()
    {
        lock (_saveGate)
        {
            List<ApplicationVolumeStateEntry> snapshot;
            lock (_gate)
            {
                snapshot = _entries
                    .OrderBy(entry => entry.Key.DeviceId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Key.TargetId, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new ApplicationVolumeStateEntry(
                        entry.Key.DeviceId,
                        entry.Key.TargetKind,
                        entry.Key.TargetId,
                        entry.Value))
                    .ToList();
            }

            string directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("状態ファイルの保存先が不正です。");
            Directory.CreateDirectory(directory);
            string temporaryPath = _path + ".tmp";
            string json = JsonSerializer.Serialize(
                new StateDocument { Entries = snapshot },
                JsonOptions);
            try
            {
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            _writeLog(
                $"[ApplicationVolume] action=save-state result=success entries={snapshot.Count}");
        }
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            string backupPath = Path.Combine(
                Path.GetDirectoryName(_path) ?? string.Empty,
                $"application-volume-state.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.json");
            File.Move(_path, backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _writeLog(
                $"[ApplicationVolume] action=quarantine-state result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
    }

    private static StateKey CreateKey(string deviceId, ApplicationAudioTarget target) =>
        new(
            NormalizeDeviceId(deviceId),
            target.IdentifierKind,
            target.Identifier.Trim().ToLowerInvariant());

    private static string NormalizeDeviceId(string deviceId) =>
        AudioDeviceId.Normalize(deviceId).Trim().ToLowerInvariant();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        try
        {
            SaveSnapshot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _writeLog(
                $"[ApplicationVolume] action=dispose-save result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }

        _saveTimer?.Dispose();
    }

    private readonly record struct StateKey(
        string DeviceId,
        ApplicationAudioIdentifierKind TargetKind,
        string TargetId);

    private sealed class StateDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<ApplicationVolumeStateEntry> Entries { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

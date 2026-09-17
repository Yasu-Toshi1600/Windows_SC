using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal sealed class CommandCycleStateStore
{
    private readonly string _path;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Dictionary<Guid, Entry> _entries = [];

    public sealed record Entry(string Signature, int NextIndex);

    public CommandCycleStateStore(string path, Action<string> log)
    {
        _path = path;
        _log = log;
        try
        {
            if (File.Exists(path))
                _entries = JsonSerializer.Deserialize<Dictionary<Guid, Entry>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"[CommandCycle] action=load-state result=failed exception={ex.GetType().Name}");
        }
    }

    private static string Signature(IReadOnlyList<CommandCycleStepDefinition> steps) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(steps))));

    public int GetNext(Guid id, IReadOnlyList<CommandCycleStepDefinition> steps)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out Entry? entry) && entry is not null
                && entry.Signature == Signature(steps)
                && entry.NextIndex >= 0 && entry.NextIndex < steps.Count ? entry.NextIndex : 0;
        }
    }

    public Task SaveNextAsync(Guid id, IReadOnlyList<CommandCycleStepDefinition> steps, int nextIndex)
    {
        string signature = Signature(steps);
        return Task.Run(() =>
        {
            lock (_gate)
            {
                _entries[id] = new Entry(signature, nextIndex);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    string temporary = _path + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
                    File.Move(temporary, _path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log($"[CommandCycle] action=save-state result=failed exception={ex.GetType().Name}");
                }
            }
        });
    }
}

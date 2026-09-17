using System;
using System.Collections.Generic;
using System.Linq;

namespace Windows_SC.Services;

internal static class AudioDeviceIdentityResolver
{
    public static string? GetStableId(string id, IReadOnlyDictionary<string, string>? stableIds) =>
        stableIds?.FirstOrDefault(pair => string.Equals(AudioDeviceId.Normalize(pair.Key),
            AudioDeviceId.Normalize(id), StringComparison.OrdinalIgnoreCase)).Value;

    public static AudioOutputDevice? Resolve(string id, string? stableId,
        IReadOnlyList<AudioOutputDevice> devices)
    {
        if (!string.IsNullOrEmpty(stableId))
        {
            AudioOutputDevice[] matches = devices.Where(device =>
                string.Equals(device.StableId, stableId, StringComparison.Ordinal)).ToArray();
            if (matches.Length > 0) return matches.Length == 1 ? matches[0] : null;
        }
        return devices.FirstOrDefault(device =>
            string.Equals(AudioDeviceId.Normalize(device.Id), AudioDeviceId.Normalize(id),
                StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(stableId) || string.IsNullOrEmpty(device.StableId)
                || string.Equals(stableId, device.StableId, StringComparison.Ordinal)));
    }

    public static IReadOnlyList<string> ResolveIds(IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, string>? stableIds, IReadOnlyList<AudioOutputDevice> devices) =>
        ids.Select(id => Resolve(id, GetStableId(id, stableIds), devices)?.Id)
            .Where(id => id is not null).Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

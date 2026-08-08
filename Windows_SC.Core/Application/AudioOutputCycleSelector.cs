using System;
using System.Collections.Generic;
using System.Linq;

namespace Windows_SC.Services;

internal static class AudioOutputCycleSelector
{
    public static AudioOutputDevice? FindNext(
        IReadOnlyList<string> orderedIds,
        IReadOnlyDictionary<string, AudioOutputDevice> availableDevices,
        string? currentDeviceId)
    {
        int currentIndex = currentDeviceId is null
            ? -1
            : orderedIds
                .Select((id, index) => (id, index))
                .Where(entry => string.Equals(
                    entry.id,
                    currentDeviceId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.index)
                .DefaultIfEmpty(-1)
                .First();
        int startIndex = currentIndex < 0 ? 0 : currentIndex + 1;

        for (int offset = 0; offset < orderedIds.Count; offset++)
        {
            int index = (startIndex + offset) % orderedIds.Count;
            if (availableDevices.TryGetValue(orderedIds[index], out AudioOutputDevice? device)
                && !string.Equals(device.Id, currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }
}

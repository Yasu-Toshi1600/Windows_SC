using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Windows_SC.Services;

internal interface IAudioOutputService : IDisposable
{
    event EventHandler? StateChanged;

    IReadOnlyList<AudioOutputDevice> GetCachedDevices();

    AudioOutputDevice? GetCachedDefaultDevice();

    AudioMasterVolumeResult GetCachedMasterVolume();

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task<AudioMasterVolumeResult> SetMasterVolumeAsync(
        double volumePercent,
        CancellationToken cancellationToken = default);

    Task<AudioDeviceCycleResult> CycleAsync(
        IReadOnlyList<string> orderedDeviceIds,
        CancellationToken cancellationToken = default);
}

internal sealed record AudioOutputDevice(
    string Id,
    string DisplayName,
    bool IsAvailable);

internal readonly record struct AudioDeviceCycleResult(
    bool IsSuccess,
    AudioOutputDevice? CurrentDevice,
    string ErrorMessage)
{
    public static AudioDeviceCycleResult Success(AudioOutputDevice device) =>
        new(true, device, string.Empty);

    public static AudioDeviceCycleResult Failure(string message) =>
        new(false, null, message);
}

internal readonly record struct AudioMasterVolumeResult(
    bool IsSuccess,
    double VolumePercent,
    string ErrorMessage)
{
    public static AudioMasterVolumeResult Success(double volumePercent) =>
        new(true, volumePercent, string.Empty);

    public static AudioMasterVolumeResult Failure(string message) =>
        new(false, 0, message);
}

internal static class AudioDeviceId
{
    public static string Normalize(string id)
    {
        const string mmDeviceMarker = "MMDEVAPI#";
        int markerIndex = id.IndexOf(mmDeviceMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return id;
        }

        int endpointStart = markerIndex + mmDeviceMarker.Length;
        int interfaceStart = id.IndexOf("#{", endpointStart, StringComparison.OrdinalIgnoreCase);
        return interfaceStart > endpointStart
            ? id[endpointStart..interfaceStart]
            : id[endpointStart..];
    }
}

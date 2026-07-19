using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Windows_SC.Services;

internal sealed class WindowsAudioOutputService(DiagnosticLogger logger) : IAudioOutputService
{
    public IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        try
        {
            return EnumerateDevices();
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or InvalidOperationException)
        {
            logger.Write(
                $"[AudioOutput] action=enumerate result=failed exception={exception.GetType().Name} " +
                $"message=\"{Sanitize(exception.Message)}\"");
            return [];
        }
    }

    public AudioOutputDevice? GetDefaultDevice()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = CreateEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(
                EDataFlow.Render,
                ERole.Multimedia,
                out device));
            return ReadDevice(device);
        }
        catch (COMException exception)
        {
            logger.Write(
                $"[AudioOutput] action=get-default result=failed hresult=0x{exception.HResult:X8} " +
                $"message=\"{Sanitize(exception.Message)}\"");
            return null;
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    public AudioMasterVolumeResult GetMasterVolume()
    {
        try
        {
            float scalar = UseDefaultEndpointVolume(volume =>
            {
                Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out float value));
                return value;
            });
            return AudioMasterVolumeResult.Success(Math.Clamp(scalar * 100, 0, 100));
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or InvalidOperationException)
        {
            logger.Write(
                $"[AudioVolume] action=get result=failed exception={exception.GetType().Name} " +
                $"message=\"{Sanitize(exception.Message)}\"");
            return AudioMasterVolumeResult.Failure(
                $"現在の音量を取得できませんでした。\n{exception.Message}");
        }
    }

    public Task<AudioMasterVolumeResult> SetMasterVolumeAsync(
        double volumePercent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double clampedPercent = Math.Clamp(volumePercent, 0, 100);

        try
        {
            UseDefaultEndpointVolume(volume =>
            {
                Guid eventContext = Guid.Empty;
                Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(
                    (float)(clampedPercent / 100),
                    ref eventContext));
                return true;
            });
            logger.Write($"[AudioVolume] action=set result=success value={clampedPercent:F0}");
            return Task.FromResult(AudioMasterVolumeResult.Success(clampedPercent));
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or InvalidOperationException)
        {
            logger.Write(
                $"[AudioVolume] action=set result=failed exception={exception.GetType().Name} " +
                $"message=\"{Sanitize(exception.Message)}\"");
            return Task.FromResult(AudioMasterVolumeResult.Failure(
                $"音量を変更できませんでした。\n{exception.Message}"));
        }
    }

    public Task<AudioDeviceCycleResult> CycleAsync(
        IReadOnlyList<string> orderedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            List<string> orderedIds = orderedDeviceIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(AudioDeviceId.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (orderedIds.Count < 2)
            {
                return Task.FromResult(AudioDeviceCycleResult.Failure(
                    "音声出力デバイスを2台以上登録してください。"));
            }

            Dictionary<string, AudioOutputDevice> devices = EnumerateDevices()
                .ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
            string? currentDeviceId = GetDefaultDevice()?.Id;
            int currentIndex = currentDeviceId is null
                ? -1
                : orderedIds.FindIndex(id => string.Equals(
                    id,
                    currentDeviceId,
                    StringComparison.OrdinalIgnoreCase));

            AudioOutputDevice? nextDevice = FindNextAvailableDevice(
                orderedIds,
                devices,
                currentIndex,
                currentDeviceId);
            if (nextDevice is null)
            {
                return Task.FromResult(AudioDeviceCycleResult.Failure(
                    "切り替え可能な音声出力デバイスがありません。"));
            }

            SetDefaultDevice(nextDevice.Id);
            logger.Write(
                $"[AudioOutput] action=cycle result=success device-id=\"{Sanitize(nextDevice.Id)}\" " +
                $"device-name=\"{Sanitize(nextDevice.DisplayName)}\"");
            return Task.FromResult(AudioDeviceCycleResult.Success(nextDevice));
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or InvalidOperationException)
        {
            logger.Write(
                $"[AudioOutput] action=cycle result=failed exception={exception.GetType().Name} " +
                $"message=\"{Sanitize(exception.Message)}\"");
            return Task.FromResult(AudioDeviceCycleResult.Failure(
                $"音声出力デバイスを切り替えられませんでした。\n{exception.Message}"));
        }
    }

    private static AudioOutputDevice? FindNextAvailableDevice(
        IReadOnlyList<string> orderedIds,
        IReadOnlyDictionary<string, AudioOutputDevice> devices,
        int currentIndex,
        string? currentDeviceId)
    {
        int startIndex = currentIndex < 0 ? 0 : currentIndex + 1;
        for (int offset = 0; offset < orderedIds.Count; offset++)
        {
            int index = (startIndex + offset) % orderedIds.Count;
            if (!devices.TryGetValue(orderedIds[index], out AudioOutputDevice? device)
                || !device.IsAvailable
                || string.Equals(device.Id, currentDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return device;
        }

        return null;
    }

    private List<AudioOutputDevice> EnumerateDevices()
    {
        string selector = MediaDevice.GetAudioRenderSelector();
        DeviceInformationCollection deviceInformation = DeviceInformation
            .FindAllAsync(selector)
            .AsTask()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        return deviceInformation
            .Select(device => new AudioOutputDevice(
                AudioDeviceId.Normalize(device.Id),
                device.Name,
                device.IsEnabled))
            .OrderByDescending(device => device.IsAvailable)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static AudioOutputDevice ReadDevice(IMMDevice device)
    {
        IPropertyStore? propertyStore = null;
        PropVariant value = default;
        try
        {
            Marshal.ThrowExceptionForHR(device.GetId(out string id));
            Marshal.ThrowExceptionForHR(device.GetState(out DeviceState state));
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(
                StorageAccessMode.Read,
                out propertyStore));
            PropertyKey key = PropertyKeys.DeviceFriendlyName;
            Marshal.ThrowExceptionForHR(propertyStore.GetValue(ref key, out value));
            string displayName = value.GetString();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = id;
            }

            return new AudioOutputDevice(
                id,
                displayName,
                (state & DeviceState.Active) != 0);
        }
        finally
        {
            value.Dispose();
            ReleaseComObject(propertyStore);
        }
    }

    private static void SetDefaultDevice(string deviceId)
    {
        Type policyType = Type.GetTypeFromCLSID(NativeGuids.PolicyConfigClient, throwOnError: true)!;
        object policyObject = Activator.CreateInstance(policyType)
            ?? throw new InvalidOperationException("音声出力ポリシーを作成できませんでした。");
        try
        {
            IPolicyConfig policy = (IPolicyConfig)policyObject;
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Console));
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia));
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Communications));
        }
        finally
        {
            ReleaseComObject(policyObject);
        }
    }

    private static T UseDefaultEndpointVolume<T>(Func<IAudioEndpointVolume, T> operation)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpointVolume = null;
        IntPtr endpointVolumePointer = IntPtr.Zero;
        try
        {
            enumerator = CreateEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(
                EDataFlow.Render,
                ERole.Multimedia,
                out device));

            Guid interfaceId = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(
                ref interfaceId,
                23,
                IntPtr.Zero,
                out endpointVolumePointer));
            endpointVolume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(endpointVolumePointer);
            return operation(endpointVolume);
        }
        finally
        {
            if (endpointVolumePointer != IntPtr.Zero)
            {
                Marshal.Release(endpointVolumePointer);
            }

            ReleaseComObject(endpointVolume);
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator()
    {
        Type enumeratorType = Type.GetTypeFromCLSID(NativeGuids.MMDeviceEnumerator, throwOnError: true)!;
        return (IMMDeviceEnumerator)(Activator.CreateInstance(enumeratorType)
            ?? throw new InvalidOperationException("音声デバイス列挙サービスを作成できませんでした。"));
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
}

internal static class NativeGuids
{
    public static readonly Guid MMDeviceEnumerator =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static readonly Guid PolicyConfigClient =
        new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F
}

internal enum EDataFlow
{
    Render,
    Capture,
    All
}

internal enum ERole
{
    Console,
    Multimedia,
    Communications
}

internal enum StorageAccessMode
{
    Read,
    Write,
    ReadWrite
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

internal static class PropertyKeys
{
    public static PropertyKey DeviceFriendlyName => new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        PropertyId = 14
    };
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant : IDisposable
{
    [FieldOffset(0)]
    private readonly ushort _variantType;

    [FieldOffset(8)]
    private readonly IntPtr _pointerValue;

    public string GetString() => _variantType == 31
        ? Marshal.PtrToStringUni(_pointerValue) ?? string.Empty
        : string.Empty;

    public void Dispose() => PropVariantClear(ref this);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, out IntPtr instance);

    [PreserveSig]
    int OpenPropertyStore(StorageAccessMode accessMode, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out DeviceState state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long period, out long minimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int GetChannelCount(out uint channelCount);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelInDecibels, ref Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelInDecibels);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channelNumber, float levelInDecibels, ref Guid eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channelNumber, float level, ref Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channelNumber, out float levelInDecibels);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channelNumber, out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);

    [PreserveSig]
    int GetVolumeStepInfo(out uint step, out uint stepCount);

    [PreserveSig]
    int VolumeStepUp(ref Guid eventContext);

    [PreserveSig]
    int VolumeStepDown(ref Guid eventContext);

    [PreserveSig]
    int QueryHardwareSupport(out uint hardwareSupportMask);

    [PreserveSig]
    int GetVolumeRange(out float minimumInDecibels, out float maximumInDecibels, out float incrementInDecibels);
}

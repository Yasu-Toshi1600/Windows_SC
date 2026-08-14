using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Windows_SC.Services;

internal sealed class WindowsAudioOutputService : IAudioOutputService
{
    private readonly DiagnosticLogger _logger;
    private readonly object _cacheLock = new();
    private readonly object _notificationLock = new();
    private readonly DeviceWatcher _deviceWatcher = DeviceInformation.CreateWatcher(
        MediaDevice.GetAudioRenderSelector());
    private IReadOnlyList<AudioOutputDevice> _cachedDevices = [];
    private AudioOutputDevice? _cachedDefaultDevice;
    private AudioMasterVolumeResult _cachedMasterVolume =
        AudioMasterVolumeResult.Failure("音量情報を準備しています。");
    private IAudioEndpointVolume? _notificationEndpointVolume;
    private AudioEndpointVolumeCallback? _endpointVolumeCallback;
    private string? _notificationDeviceId;
    private int _notificationGeneration;
    private int _refreshPending;
    private bool _isDisposed;

    public event EventHandler? StateChanged;

    public WindowsAudioOutputService(DiagnosticLogger logger)
    {
        _logger = logger;
        RefreshCacheCore();
        MediaDevice.DefaultAudioRenderDeviceChanged += DefaultAudioRenderDeviceChanged;
        _deviceWatcher.Added += DeviceWatcher_Added;
        _deviceWatcher.Removed += DeviceWatcher_Removed;
        _deviceWatcher.Updated += DeviceWatcher_Updated;
        _deviceWatcher.Start();
    }

    public IReadOnlyList<AudioOutputDevice> GetCachedDevices()
    {
        lock (_cacheLock)
        {
            return _cachedDevices;
        }
    }

    private IReadOnlyList<AudioOutputDevice> QueryDevices()
    {
        try
        {
            return EnumerateDevices();
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or InvalidOperationException)
        {
            _logger.Write(
                $"[AudioOutput] action=enumerate result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8}");
            _logger.WriteDetailed(
                $"[AudioOutput] action=enumerate result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8} " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            return [];
        }
    }

    public AudioOutputDevice? GetCachedDefaultDevice()
    {
        lock (_cacheLock)
        {
            return _cachedDefaultDevice;
        }
    }

    private AudioOutputDevice? QueryDefaultDevice()
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
            _logger.Write(
                $"[AudioOutput] action=get-default result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            _logger.WriteDetailed(
                $"[AudioOutput] action=get-default result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            return null;
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    public AudioMasterVolumeResult GetCachedMasterVolume()
    {
        lock (_cacheLock)
        {
            return _cachedMasterVolume;
        }
    }

    private AudioMasterVolumeResult QueryMasterVolume()
    {
        try
        {
            return UseDefaultEndpointVolume(volume =>
            {
                Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out float scalar));
                Marshal.ThrowExceptionForHR(volume.GetMute(out bool isMuted));
                return AudioMasterVolumeResult.Success(
                    Math.Clamp(scalar * 100, 0, 100),
                    isMuted);
            });
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or InvalidOperationException)
        {
            _logger.Write(
                $"[AudioVolume] action=get result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8}");
            _logger.WriteDetailed(
                $"[AudioVolume] action=get result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8} " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            return AudioMasterVolumeResult.Failure(
                $"現在の音量を取得できませんでした。\n{exception.Message}");
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RefreshCacheCore();
            },
            cancellationToken);

    public Task<AudioMasterVolumeResult> SetMasterVolumeAsync(
        double volumePercent,
        CancellationToken cancellationToken = default)
    {
        double clampedPercent = Math.Clamp(volumePercent, 0, 100);
        return Task.Run(
            () => SetMasterVolumeCore(clampedPercent),
            cancellationToken);
    }

    private AudioMasterVolumeResult SetMasterVolumeCore(double clampedPercent)
    {
        try
        {
            bool isMuted = UseDefaultEndpointVolume(volume =>
            {
                Guid eventContext = Guid.Empty;
                Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(
                    (float)(clampedPercent / 100),
                    ref eventContext));
                Marshal.ThrowExceptionForHR(volume.GetMute(out bool currentMute));
                return currentMute;
            });
            AudioMasterVolumeResult result = AudioMasterVolumeResult.Success(
                clampedPercent,
                isMuted);
            lock (_cacheLock)
            {
                _cachedMasterVolume = result;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            _logger.Write($"[AudioVolume] action=set result=success value={clampedPercent:F0}");
            return result;
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or InvalidOperationException)
        {
            _logger.Write(
                $"[AudioVolume] action=set result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8}");
            _logger.WriteDetailed(
                $"[AudioVolume] action=set result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8} " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            return AudioMasterVolumeResult.Failure(
                $"音量を変更できませんでした。\n{exception.Message}");
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
                _logger.Write(
                    $"[AudioOutput] action=cycle result=failed " +
                    $"reason=insufficient-devices devices={orderedIds.Count}");
                return Task.FromResult(AudioDeviceCycleResult.Failure(
                    "音声出力デバイスを2台以上登録してください。"));
            }

            Dictionary<string, AudioOutputDevice> devices = EnumerateDevices()
                .ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
            string? currentDeviceId = QueryDefaultDevice()?.Id;
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
                _logger.Write(
                    "[AudioOutput] action=cycle result=failed reason=no-available-device");
                return Task.FromResult(AudioDeviceCycleResult.Failure(
                    "切り替え可能な音声出力デバイスがありません。"));
            }

            SetDefaultDevice(nextDevice.Id);
            lock (_cacheLock)
            {
                _cachedDefaultDevice = nextDevice;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            QueueRefresh();
            _logger.Write("[AudioOutput] action=cycle result=success");
            _logger.WriteDetailed(
                $"[AudioOutput] action=cycle result=success " +
                $"device-id=\"{LogValue.Normalize(nextDevice.Id)}\" " +
                $"device-name=\"{LogValue.Normalize(nextDevice.DisplayName)}\"");
            return Task.FromResult(AudioDeviceCycleResult.Success(nextDevice));
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or InvalidOperationException)
        {
            _logger.Write(
                $"[AudioOutput] action=cycle result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8}");
            _logger.WriteDetailed(
                $"[AudioOutput] action=cycle result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8} " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            return Task.FromResult(AudioDeviceCycleResult.Failure(
                $"音声出力デバイスを切り替えられませんでした。\n{exception.Message}"));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        MediaDevice.DefaultAudioRenderDeviceChanged -= DefaultAudioRenderDeviceChanged;
        _deviceWatcher.Added -= DeviceWatcher_Added;
        _deviceWatcher.Removed -= DeviceWatcher_Removed;
        _deviceWatcher.Updated -= DeviceWatcher_Updated;
        if (_deviceWatcher.Status is DeviceWatcherStatus.Started
            or DeviceWatcherStatus.EnumerationCompleted)
        {
            _deviceWatcher.Stop();
        }

        UnbindMasterVolumeNotifications();
    }

    private void RefreshCacheCore()
    {
        IReadOnlyList<AudioOutputDevice> devices = QueryDevices();
        AudioOutputDevice? defaultDevice = QueryDefaultDevice();
        AudioMasterVolumeResult masterVolume = QueryMasterVolume();

        lock (_cacheLock)
        {
            _cachedDevices = devices;
            _cachedDefaultDevice = defaultDevice;
            _cachedMasterVolume = masterVolume;
        }

        BindMasterVolumeNotifications(defaultDevice?.Id);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BindMasterVolumeNotifications(string? deviceId)
    {
        lock (_notificationLock)
        {
            if (_isDisposed
                || (_notificationEndpointVolume is not null
                    && string.Equals(
                        _notificationDeviceId,
                        deviceId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            UnbindMasterVolumeNotificationsCore();
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            IAudioEndpointVolume? endpointVolume = null;
            try
            {
                endpointVolume = ActivateEndpointVolume(deviceId);
                int generation = ++_notificationGeneration;
                AudioEndpointVolumeCallback callback = new(this, generation);
                Marshal.ThrowExceptionForHR(
                    endpointVolume.RegisterControlChangeNotify(callback));
                _notificationEndpointVolume = endpointVolume;
                _endpointVolumeCallback = callback;
                _notificationDeviceId = deviceId;
                endpointVolume = null;
            }
            catch (Exception exception) when (exception is COMException
                or InvalidCastException
                or InvalidOperationException)
            {
                _logger.Write(
                    $"[AudioVolume] action=register-notification result=failed " +
                    $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
                _logger.WriteDetailed(
                    $"[AudioVolume] action=register-notification result=failed " +
                    $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
                    $"message=\"{LogValue.Normalize(exception.Message)}\"");
            }
            finally
            {
                ReleaseComObject(endpointVolume);
            }
        }
    }

    private void UnbindMasterVolumeNotifications()
    {
        lock (_notificationLock)
        {
            UnbindMasterVolumeNotificationsCore();
        }
    }

    private void UnbindMasterVolumeNotificationsCore()
    {
        ++_notificationGeneration;
        try
        {
            if (_notificationEndpointVolume is not null && _endpointVolumeCallback is not null)
            {
                int result = _notificationEndpointVolume.UnregisterControlChangeNotify(
                    _endpointVolumeCallback);
                if (result < 0)
                {
                    _logger.Write(
                        $"[AudioVolume] action=unregister-notification result=failed " +
                        $"hresult=0x{result:X8}");
                }
            }
        }
        catch (Exception exception) when (exception is COMException
            or InvalidComObjectException)
        {
            _logger.Write(
                $"[AudioVolume] action=unregister-notification result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
        finally
        {
            ReleaseComObject(_notificationEndpointVolume);
            _notificationEndpointVolume = null;
            _endpointVolumeCallback = null;
            _notificationDeviceId = null;
        }
    }

    private void HandleMasterVolumeChanged(int generation, IntPtr notificationData)
    {
        if (_isDisposed
            || generation != Volatile.Read(ref _notificationGeneration)
            || notificationData == IntPtr.Zero)
        {
            return;
        }

        AudioVolumeNotificationData notification =
            Marshal.PtrToStructure<AudioVolumeNotificationData>(notificationData);
        AudioMasterVolumeResult result = AudioMasterVolumeResult.Success(
            Math.Clamp(notification.MasterVolume * 100, 0, 100),
            notification.IsMuted);
        lock (_cacheLock)
        {
            _cachedMasterVolume = result;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void QueueRefresh()
    {
        if (_isDisposed || Interlocked.Exchange(ref _refreshPending, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                RefreshCacheCore();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshPending, 0);
            }
        });
    }

    private void DefaultAudioRenderDeviceChanged(
        object sender,
        DefaultAudioRenderDeviceChangedEventArgs args) => QueueRefresh();

    private void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args) =>
        QueueRefresh();

    private void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args) =>
        QueueRefresh();

    private void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args) =>
        QueueRefresh();

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

    private static IAudioEndpointVolume ActivateEndpointVolume(string deviceId)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpointVolume = null;
        IntPtr endpointVolumePointer = IntPtr.Zero;
        try
        {
            enumerator = CreateEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out device));
            Guid interfaceId = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(
                ref interfaceId,
                23,
                IntPtr.Zero,
                out endpointVolumePointer));
            endpointVolume =
                (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(endpointVolumePointer);
            return endpointVolume;
        }
        finally
        {
            if (endpointVolumePointer != IntPtr.Zero)
            {
                Marshal.Release(endpointVolumePointer);
            }

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
            try
            {
                // MMDeviceEnumerator can share an RCW with the application-volume
                // service. FinalReleaseComObject would invalidate that service's
                // still-live reference, so release only this caller's ownership.
                Marshal.ReleaseComObject(value);
            }
            catch (InvalidComObjectException)
            {
            }
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class AudioEndpointVolumeCallback(
        WindowsAudioOutputService owner,
        int generation) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notificationData)
        {
            try
            {
                owner.HandleMasterVolumeChanged(generation, notificationData);
            }
            catch (Exception exception)
            {
                owner._logger.Write(
                    $"[AudioVolume] action=handle-notification result=failed " +
                    $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
                owner._logger.WriteDetailed(
                    $"[AudioVolume] action=handle-notification result=failed " +
                    $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
                    $"message=\"{LogValue.Normalize(exception.Message)}\"");
            }

            return 0;
        }
    }

}

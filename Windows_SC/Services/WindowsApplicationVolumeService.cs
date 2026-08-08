using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Windows_SC.Services;

internal sealed class WindowsApplicationVolumeService : IApplicationVolumeService
{
    private const uint ClsctxAll = 23;
    private static readonly Guid SessionManager2Id =
        new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private readonly DiagnosticLogger _logger;
    private readonly IApplicationAudioIdentityResolver _identityResolver;
    private readonly IApplicationVolumeStateStore _stateStore;
    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly Thread _workerThread;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, SessionHandle> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Guid _eventContext = Guid.NewGuid();
    private IReadOnlyList<ApplicationAudioInfo> _cachedApplications = [];
    private IMMDeviceEnumerator? _deviceEnumerator;
    private IMMDevice? _endpoint;
    private IAudioSessionManager2? _sessionManager;
    private EndpointNotification? _endpointNotification;
    private SessionNotification? _sessionNotification;
    private string? _endpointId;
    private bool _isDisposed;

    public WindowsApplicationVolumeService(
        DiagnosticLogger logger,
        IApplicationVolumeStateStore stateStore)
        : this(logger, stateStore, new ApplicationAudioIdentityResolver())
    {
    }

    internal WindowsApplicationVolumeService(
        DiagnosticLogger logger,
        IApplicationVolumeStateStore stateStore,
        IApplicationAudioIdentityResolver identityResolver)
    {
        _logger = logger;
        _stateStore = stateStore;
        _identityResolver = identityResolver;
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Windows_SC application audio",
            Priority = ThreadPriority.BelowNormal
        };
        _workerThread.SetApartmentState(ApartmentState.MTA);
        _workerThread.Start();
        TryQueueWork(BindDefaultEndpoint);
    }

    public event EventHandler? StateChanged;

    public IReadOnlyList<ApplicationAudioInfo> GetCachedApplications()
    {
        lock (_cacheGate)
        {
            return _cachedApplications;
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                BindDefaultEndpoint();
            },
            cancellationToken);

    public Task<ApplicationVolumeResult> SetVolumeAsync(
        ApplicationAudioTarget target,
        int volumePercent,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            () => SetVolumeCore(target, Math.Clamp(volumePercent, 0, 100)),
            cancellationToken);

    private void WorkerLoop()
    {
        int initializeResult = CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            foreach (Action action in _workQueue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    _logger.Write(
                        $"[ApplicationVolume] action=worker result=failed " +
                        $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
                    _logger.WriteDetailed(
                        $"[ApplicationVolume] action=worker result=failed " +
                        $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
                        $"message=\"{LogValue.Normalize(exception.Message)}\"");
                }
            }
        }
        finally
        {
            try
            {
                ReleaseEndpoint();
            }
            catch (Exception exception)
            {
                LogCleanupFailure("release-endpoint", exception);
            }

            try
            {
                UnregisterEndpointNotification();
            }
            finally
            {
                ReleaseComObject(_deviceEnumerator);
                _deviceEnumerator = null;
                _endpointNotification = null;
                if (initializeResult >= 0)
                {
                    CoUninitialize();
                }
            }
        }
    }

    private void BindDefaultEndpoint()
    {
        ReleaseEndpoint();
        _deviceEnumerator ??= CreateDeviceEnumerator();
        if (_endpointNotification is null)
        {
            _endpointNotification = new EndpointNotification(this);
            Marshal.ThrowExceptionForHR(
                _deviceEnumerator.RegisterEndpointNotificationCallback(_endpointNotification));
        }

        Marshal.ThrowExceptionForHR(_deviceEnumerator.GetDefaultAudioEndpoint(
            EDataFlow.Render,
            ERole.Multimedia,
            out _endpoint));
        Marshal.ThrowExceptionForHR(_endpoint.GetId(out string endpointId));
        _endpointId = AudioDeviceId.Normalize(endpointId);

        IntPtr managerPointer = IntPtr.Zero;
        try
        {
            Guid managerId = SessionManager2Id;
            Marshal.ThrowExceptionForHR(_endpoint.Activate(
                ref managerId,
                ClsctxAll,
                IntPtr.Zero,
                out managerPointer));
            _sessionManager = (IAudioSessionManager2)Marshal.GetObjectForIUnknown(managerPointer);
        }
        finally
        {
            if (managerPointer != IntPtr.Zero)
            {
                Marshal.Release(managerPointer);
            }
        }

        _sessionNotification = new SessionNotification(this);
        Marshal.ThrowExceptionForHR(
            _sessionManager.RegisterSessionNotification(_sessionNotification));
        Marshal.ThrowExceptionForHR(_sessionManager.GetSessionEnumerator(out var enumerator));
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetCount(out int count));
            for (int index = 0; index < count; index++)
            {
                if (enumerator.GetSession(index, out IAudioSessionControl control) >= 0)
                {
                    AddSession(control);
                }
            }
        }
        finally
        {
            ReleaseComObject(enumerator);
        }

        PublishCache();
        _logger.Write(
            $"[ApplicationVolume] action=bind-endpoint result=success " +
            $"sessions={_sessions.Count} applications={_cachedApplications.Count}");
    }

    private void AddSession(IAudioSessionControl control)
    {
        IAudioSessionControl2? control2 = null;
        ISimpleAudioVolume? volume = null;
        try
        {
            control2 = (IAudioSessionControl2)control;
            int processResult = control2.GetProcessId(out uint processId);
            if (processResult < 0)
            {
                return;
            }

            Marshal.ThrowExceptionForHR(control2.GetSessionInstanceIdentifier(out string instanceId));
            if (_sessions.ContainsKey(instanceId))
            {
                return;
            }

            ApplicationAudioTarget? target = _identityResolver.Resolve(processId);
            if (target is null)
            {
                return;
            }

            volume = (ISimpleAudioVolume)control;
            Marshal.ThrowExceptionForHR(volume.GetMasterVolume(out float scalar));
            SessionEvents events = new(this, instanceId);
            Marshal.ThrowExceptionForHR(control2.RegisterAudioSessionNotification(events));
            SessionHandle handle = new(
                instanceId,
                target,
                control,
                control2,
                volume,
                events,
                Math.Clamp(scalar, 0, 1));
            _sessions.Add(instanceId, handle);

            if (_endpointId is not null
                && _stateStore.TryGet(_endpointId, target, out int savedVolume))
            {
                Guid eventContext = _eventContext;
                if (volume.SetMasterVolume(savedVolume / 100f, ref eventContext) >= 0)
                {
                    handle.VolumeScalar = savedVolume / 100f;
                }
            }

            control2 = null;
            volume = null;
            _logger.Write("[ApplicationVolume] action=add-session result=success");
        }
        catch (Exception exception) when (exception is COMException
            or InvalidCastException
            or InvalidOperationException)
        {
            _logger.Write(
                $"[ApplicationVolume] action=add-session result=skipped " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
        finally
        {
            if (control2 is not null)
            {
                ReleaseComObject(control2);
            }

            if (volume is not null)
            {
                ReleaseComObject(volume);
            }

            if (!_sessions.Values.Any(handle => ReferenceEquals(handle.Control, control)))
            {
                ReleaseComObject(control);
            }
        }
    }

    private ApplicationVolumeResult SetVolumeCore(ApplicationAudioTarget target, int volumePercent)
    {
        List<SessionHandle> sessions = FindSessions(target).ToList();
        if (sessions.Count == 0 || _endpointId is null)
        {
            return ApplicationVolumeResult.Failure(
                "対象アプリの音声セッションがありません。",
                0,
                0);
        }

        int succeeded = 0;
        int failed = 0;
        int lastHresult = 0;
        foreach (SessionHandle session in sessions)
        {
            Guid eventContext = _eventContext;
            int result = session.Volume.SetMasterVolume(volumePercent / 100f, ref eventContext);
            if (result >= 0)
            {
                session.VolumeScalar = volumePercent / 100f;
                succeeded++;
            }
            else
            {
                lastHresult = result;
                failed++;
            }
        }

        if (succeeded > 0)
        {
            _stateStore.Set(_endpointId, target, volumePercent);
            PublishCache();
        }

        _logger.Write(
            $"[ApplicationVolume] action=set result={(failed == 0 ? "success" : "failed")} " +
            $"sessions={sessions.Count} succeeded={succeeded} failed={failed} " +
            $"hresult=0x{lastHresult:X8}");
        return failed == 0
            ? ApplicationVolumeResult.Success(succeeded)
            : ApplicationVolumeResult.Failure(
                "一部の音声セッションを変更できませんでした。",
                succeeded,
                failed);
    }

    private void HandleVolumeChanged(string instanceId, float scalar, Guid eventContext)
    {
        if (!_sessions.TryGetValue(instanceId, out SessionHandle? changed))
        {
            return;
        }

        changed.VolumeScalar = Math.Clamp(scalar, 0, 1);
        if (eventContext != _eventContext)
        {
            int percent = (int)Math.Round(changed.VolumeScalar * 100);
            foreach (SessionHandle sibling in FindSessions(changed.Target)
                         .Where(session => session.InstanceId != instanceId))
            {
                Guid ownContext = _eventContext;
                if (sibling.Volume.SetMasterVolume(percent / 100f, ref ownContext) >= 0)
                {
                    sibling.VolumeScalar = percent / 100f;
                }
            }

            if (_endpointId is not null && _stateStore.Contains(_endpointId, changed.Target))
            {
                _stateStore.Set(_endpointId, changed.Target, percent);
            }
        }

        PublishCache();
    }

    private void RemoveSession(string instanceId)
    {
        if (!_sessions.Remove(instanceId, out SessionHandle? handle))
        {
            return;
        }

        UnregisterSessionEvents(handle);
        ReleaseSession(handle);
        PublishCache();
        _logger.Write("[ApplicationVolume] action=remove-session result=success");
    }

    private IEnumerable<SessionHandle> FindSessions(ApplicationAudioTarget target) =>
        _sessions.Values.Where(session =>
            session.Target.IdentifierKind == target.IdentifierKind
            && string.Equals(
                session.Target.Identifier,
                target.Identifier,
                StringComparison.OrdinalIgnoreCase));

    private void PublishCache()
    {
        IReadOnlyList<ApplicationAudioInfo> snapshot = _sessions.Values
            .GroupBy(
                session => new
                {
                    session.Target.IdentifierKind,
                    Identifier = session.Target.Identifier.ToLowerInvariant()
                })
            .Select(group =>
            {
                SessionHandle first = group.First();
                int[] values = group
                    .Select(session => (int)Math.Round(session.VolumeScalar * 100))
                    .ToArray();
                return new ApplicationAudioInfo(
                    first.Target,
                    true,
                    values.Average(),
                    values.Distinct().Skip(1).Any(),
                    values.Length);
            })
            .OrderBy(info => info.Target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        lock (_cacheGate)
        {
            _cachedApplications = snapshot;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseEndpoint()
    {
        foreach (SessionHandle handle in _sessions.Values.ToList())
        {
            UnregisterSessionEvents(handle);
            ReleaseSession(handle);
        }

        _sessions.Clear();
        if (_sessionManager is not null && _sessionNotification is not null)
        {
            try
            {
                int result = _sessionManager.UnregisterSessionNotification(_sessionNotification);
                if (result < 0)
                {
                    LogUnregisterFailure("session-manager", result);
                }
            }
            catch (Exception exception) when (exception is COMException
                or InvalidComObjectException)
            {
                LogUnregisterFailure("session-manager", exception.HResult, exception);
            }
        }

        ReleaseComObject(_sessionManager);
        ReleaseComObject(_endpoint);
        _sessionManager = null;
        _sessionNotification = null;
        _endpoint = null;
        _endpointId = null;
        PublishCache();
    }

    private void UnregisterSessionEvents(SessionHandle handle)
    {
        try
        {
            int result = handle.Control2.UnregisterAudioSessionNotification(handle.Events);
            if (result < 0)
            {
                LogUnregisterFailure("session", result);
            }
        }
        catch (Exception exception) when (exception is COMException
            or InvalidComObjectException)
        {
            LogUnregisterFailure("session", exception.HResult, exception);
        }
    }

    private void UnregisterEndpointNotification()
    {
        if (_deviceEnumerator is null || _endpointNotification is null)
        {
            return;
        }

        try
        {
            int result = _deviceEnumerator.UnregisterEndpointNotificationCallback(
                _endpointNotification);
            if (result < 0)
            {
                LogUnregisterFailure("endpoint", result);
            }
        }
        catch (Exception exception) when (exception is COMException
            or InvalidComObjectException)
        {
            LogUnregisterFailure("endpoint", exception.HResult, exception);
        }
    }

    private void LogUnregisterFailure(string scope, int hresult, Exception? exception = null)
    {
        string exceptionType = exception?.GetType().Name ?? "none";
        _logger.Write(
            $"[ApplicationVolume] action=unregister-notification result=failed " +
            $"scope={scope} exception={exceptionType} hresult=0x{hresult:X8}");
        if (exception is not null)
        {
            _logger.WriteDetailed(
                $"[ApplicationVolume] action=unregister-notification result=failed " +
                $"scope={scope} exception={exceptionType} hresult=0x{hresult:X8} " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
        }
    }

    private void LogCleanupFailure(string action, Exception exception)
    {
        _logger.Write(
            $"[ApplicationVolume] action={action} result=failed " +
            $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        _logger.WriteDetailed(
            $"[ApplicationVolume] action={action} result=failed " +
            $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
            $"message=\"{LogValue.Normalize(exception.Message)}\"");
    }

    private static void ReleaseSession(SessionHandle handle)
    {
        HashSet<object> released = new(ReferenceEqualityComparer.Instance);
        foreach (object instance in new object[] { handle.Volume, handle.Control2, handle.Control })
        {
            if (released.Add(instance))
            {
                ReleaseComObject(instance);
            }
        }
    }

    private static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        Type type = Type.GetTypeFromCLSID(NativeGuids.MMDeviceEnumerator, throwOnError: true)!;
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    private bool TryQueueWork(Action action)
    {
        if (!_isDisposed && !_workQueue.IsAddingCompleted)
        {
            try
            {
                _workQueue.Add(action);
                return true;
            }
            catch (InvalidOperationException)
            {
                // Shutdown won the race.
            }
        }

        return false;
    }

    private Task EnqueueAsync(Action action, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = TryQueueWork(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                completion.TrySetResult();
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        if (!queued)
        {
            CompleteRejectedOperation(completion, cancellationToken);
        }

        return completion.Task;
    }

    private Task<T> EnqueueAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = TryQueueWork(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        if (!queued)
        {
            CompleteRejectedOperation(completion, cancellationToken);
        }

        return completion.Task;
    }

    private static void CompleteRejectedOperation(
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        else
        {
            completion.TrySetException(
                new ObjectDisposedException(nameof(WindowsApplicationVolumeService)));
        }
    }

    private static void CompleteRejectedOperation<T>(
        TaskCompletionSource<T> completion,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        else
        {
            completion.TrySetException(
                new ObjectDisposedException(nameof(WindowsApplicationVolumeService)));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _workQueue.CompleteAdding();
        bool workerStopped = _workerThread.Join(TimeSpan.FromSeconds(5));
        if (!workerStopped)
        {
            _logger.Write("[ApplicationVolume] action=dispose result=failed reason=worker-timeout");
        }
        else
        {
            _logger.Write("[ApplicationVolume] action=dispose result=success");
        }

        if (workerStopped)
        {
            _workQueue.Dispose();
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            try
            {
                Marshal.ReleaseComObject(instance);
            }
            catch (InvalidComObjectException)
            {
            }
        }
    }

    private sealed class SessionHandle(
        string instanceId,
        ApplicationAudioTarget target,
        IAudioSessionControl control,
        IAudioSessionControl2 control2,
        ISimpleAudioVolume volume,
        SessionEvents events,
        float volumeScalar)
    {
        public string InstanceId { get; } = instanceId;
        public ApplicationAudioTarget Target { get; } = target;
        public IAudioSessionControl Control { get; } = control;
        public IAudioSessionControl2 Control2 { get; } = control2;
        public ISimpleAudioVolume Volume { get; } = volume;
        public SessionEvents Events { get; } = events;
        public float VolumeScalar { get; set; } = volumeScalar;
    }

    private sealed class SessionNotification(WindowsApplicationVolumeService owner)
        : IAudioSessionNotification
    {
        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            owner.TryQueueWork(() =>
            {
                owner.AddSession(newSession);
                owner.PublishCache();
            });
            return 0;
        }
    }

    private sealed class SessionEvents(
        WindowsApplicationVolumeService owner,
        string instanceId) : IAudioSessionEvents
    {
        public int OnDisplayNameChanged(string displayName, ref Guid eventContext) => 0;
        public int OnIconPathChanged(string iconPath, ref Guid eventContext) => 0;

        public int OnSimpleVolumeChanged(float volume, bool isMuted, ref Guid eventContext)
        {
            Guid capturedContext = eventContext;
            owner.TryQueueWork(() => owner.HandleVolumeChanged(instanceId, volume, capturedContext));
            return 0;
        }

        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumes, uint changedChannel, ref Guid eventContext) => 0;
        public int OnGroupingParamChanged(ref Guid groupingId, ref Guid eventContext) => 0;

        public int OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.Expired)
            {
                owner.TryQueueWork(() => owner.RemoveSession(instanceId));
            }

            return 0;
        }

        public int OnSessionDisconnected(AudioSessionDisconnectReason reason)
        {
            owner.TryQueueWork(() => owner.RemoveSession(instanceId));
            return 0;
        }
    }

    private sealed class EndpointNotification(WindowsApplicationVolumeService owner)
        : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, DeviceState newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? deviceId)
        {
            if (flow == EDataFlow.Render && role == ERole.Multimedia)
            {
                owner.TryQueueWork(owner.BindDefaultEndpoint);
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint apartmentType);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

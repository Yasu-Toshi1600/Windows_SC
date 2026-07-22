using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace Windows_SC;

internal sealed class UiAutomationStartMenuInspector : IDisposable
{
    private const double MinimumCandidateWidth = 200;
    private const double MinimumCandidateHeight = 100;

    private readonly DiagnosticLogger _logger;
    private readonly StartMenuWindowInspector _windowInspector;
    private readonly AutoResetEvent _scanRequested = new(false);
    private readonly ManualResetEvent _disposeRequested = new(false);
    private readonly Thread _workerThread;
    private readonly Thread _focusEventThread;
    private readonly object _stateLock = new();
    private StartMenuSnapshot _snapshot = StartMenuSnapshot.Hidden;
    private volatile bool _isDisposed;
    private string _lastSnapshotSignature = string.Empty;
    private string _lastFocusedElementSignature = string.Empty;
    private int _monitoringActive;
    private int _isReady;
    private AutomationFocusChangedEventHandler? _focusChangedHandler;

    public event EventHandler? SnapshotChanged;

    public event EventHandler? ReadyChanged;

    public UiAutomationStartMenuInspector(DiagnosticLogger logger)
    {
        _logger = logger;
        _windowInspector = new StartMenuWindowInspector(logger);
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Windows_SC UI Automation"
        };
        _workerThread.SetApartmentState(ApartmentState.STA);
        _focusEventThread = new Thread(FocusEventLoop)
        {
            IsBackground = true,
            Name = "Windows_SC UI Automation Events"
        };
        _focusEventThread.SetApartmentState(ApartmentState.STA);
    }

    public bool IsStartMenuVisible
    {
        get
        {
            lock (_stateLock)
            {
                return _snapshot.IsVisible;
            }
        }
    }

    public bool IsReady => Volatile.Read(ref _isReady) != 0;

    public StartMenuSnapshot Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                return _snapshot;
            }
        }
    }

    public void Start()
    {
        _workerThread.Start();
        _focusEventThread.Start();
        RequestScan();
        _logger.WriteDetailed("[UIAutomation] workers=started apartment=STA");
    }

    public void RequestScan()
    {
        if (!_isDisposed)
        {
            _scanRequested.Set();
        }
    }

    public void SetMonitoringActive(bool isActive)
    {
        Interlocked.Exchange(ref _monitoringActive, isActive ? 1 : 0);
        if (isActive)
        {
            RequestScan();
        }
    }

    public void RequestScanIfStartMenuWindowVisible()
    {
        if (!_isDisposed && _windowInspector.IsStartMenuVisible())
        {
            RequestScan();
        }
    }

    public void AssumeHidden()
    {
        UpdateSnapshot(StartMenuSnapshot.Hidden);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _disposeRequested.Set();
        _scanRequested.Set();
        _workerThread.Join(TimeSpan.FromSeconds(1));
        if (!_workerThread.IsAlive)
        {
            _scanRequested.Dispose();
        }

        // Focus-event registration can be blocked by an external UI Automation
        // provider. It is a background thread, so shutdown must not wait for it.
    }

    private void WorkerLoop()
    {
        while (true)
        {
            // Scans are event driven while idle. MainWindow requests bounded
            // fallback scans after the Windows key and while the launcher is open.
            _scanRequested.WaitOne();

            if (_isDisposed)
            {
                return;
            }

            try
            {
                long scanStartedTimestamp = Stopwatch.GetTimestamp();
                ScanDesktopAutomationTree();
                TimeSpan scanElapsed = Stopwatch.GetElapsedTime(scanStartedTimestamp);
                if (scanElapsed >= TimeSpan.FromMilliseconds(50))
                {
                    _logger.Write(
                        $"[UIAutomation] scan=slow elapsed-ms={scanElapsed.TotalMilliseconds:F1}");
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException
                or InvalidOperationException
                or System.Runtime.InteropServices.COMException)
            {
                UpdateSnapshot(StartMenuSnapshot.Hidden);
                _logger.Write($"[UIAutomation] scan=failed exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            }
        }
    }

    private void FocusEventLoop()
    {
        _focusChangedHandler = (_, _) =>
        {
            if (Volatile.Read(ref _monitoringActive) != 0
                || _windowInspector.IsStartMenuVisible())
            {
                RequestScan();
            }
        };
        bool registered = false;
        long registrationStartedTimestamp = Stopwatch.GetTimestamp();

        try
        {
            Automation.AddAutomationFocusChangedEventHandler(_focusChangedHandler);
            registered = true;
            if (_isDisposed)
            {
                return;
            }

            Interlocked.Exchange(ref _isReady, 1);
            ReadyChanged?.Invoke(this, EventArgs.Empty);
            _logger.WriteDetailed(
                $"[UIAutomation] focus-event=registered elapsed-ms=" +
                $"{Stopwatch.GetElapsedTime(registrationStartedTimestamp).TotalMilliseconds:F1}");
            _disposeRequested.WaitOne();
        }
        catch (Exception exception)
        {
            _logger.Write(
                $"[UIAutomation] focus-event=registration-failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
        finally
        {
            if (Interlocked.Exchange(ref _isReady, 0) != 0)
            {
                ReadyChanged?.Invoke(this, EventArgs.Empty);
            }

            if (registered && _focusChangedHandler is not null)
            {
                try
                {
                    Automation.RemoveAutomationFocusChangedEventHandler(_focusChangedHandler);
                    _logger.WriteDetailed("[UIAutomation] focus-event=unregistered");
                }
                catch (Exception exception)
                {
                    _logger.WriteDetailed(
                        $"[UIAutomation] focus-event=unregister-failed " +
                        $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
                }
            }
        }
    }

    private void ScanDesktopAutomationTree()
    {
        int[] processIds = new[] { "StartMenuExperienceHost", "SearchHost" }
            .SelectMany(Process.GetProcessesByName)
            .Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            })
            .ToArray();

        List<AutomationCandidate> candidates = [];
        Windows.Graphics.RectInt32? preferredStartOwnerBounds = null;

        // The focused UI Automation element identifies which monitor owns the
        // currently open Start surface. Win32 can expose more than one visible
        // SearchHost window in a multi-monitor environment, so enumeration order
        // must not override a focused Start window from another display.
        if (processIds.Length > 0
            && TryCollectFocusedStartMenuElement(
                processIds,
                candidates,
                out preferredStartOwnerBounds))
        {
            System.Windows.Rect? startBounds = SelectStartMenuBounds(candidates);
            if (startBounds is not null)
            {
                UpdateSnapshot(new StartMenuSnapshot(
                    true,
                    ToRectInt32(startBounds.Value),
                    false,
                    null));
                LogSnapshotIfChanged("visible-focused-element", candidates);
                return;
            }
        }

        if (_windowInspector.TryGetStartMenuBounds(
            preferredStartOwnerBounds,
            out Windows.Graphics.RectInt32 windowBounds))
        {
            UpdateSnapshot(new StartMenuSnapshot(true, windowBounds, false, null));
            LogSnapshotIfChanged("visible-win32-window", []);
            return;
        }

        if (processIds.Length == 0)
        {
            UpdateSnapshot(StartMenuSnapshot.Hidden);
            LogSnapshotIfChanged("process-not-found", []);
            return;
        }

        UpdateSnapshot(StartMenuSnapshot.Hidden);
        LogSnapshotIfChanged("hidden", candidates);
    }

    private bool TryCollectFocusedStartMenuElement(
        IReadOnlyCollection<int> startMenuProcessIds,
        ICollection<AutomationCandidate> candidates,
        out Windows.Graphics.RectInt32? preferredStartOwnerBounds)
    {
        preferredStartOwnerBounds = null;
        AutomationElement focusedElement = AutomationElement.FocusedElement;
        AutomationElement.AutomationElementInformation focused = focusedElement.Current;
        string focusedSignature = $"{focused.ProcessId}:{focused.AutomationId}:{focused.Name}";

        if (focusedSignature != _lastFocusedElementSignature)
        {
            _lastFocusedElementSignature = focusedSignature;
            System.Windows.Rect rectangle = focused.BoundingRectangle;
            _logger.WriteDetailed(
                $"[UIAutomationFocus] process={GetProcessName(focused.ProcessId)} pid={focused.ProcessId} " +
                $"name=\"{Sanitize(focused.Name)}\" automation-id=\"{Sanitize(focused.AutomationId)}\" " +
                $"control-type=\"{focused.ControlType?.ProgrammaticName}\" " +
                $"rect=({rectangle.Left:F0},{rectangle.Top:F0})-({rectangle.Right:F0},{rectangle.Bottom:F0})");
        }

        if (!startMenuProcessIds.Contains(focused.ProcessId))
        {
            if (string.Equals(
                    GetProcessName(focused.ProcessId),
                    "explorer",
                    StringComparison.OrdinalIgnoreCase))
            {
                preferredStartOwnerBounds = FindStartButtonBounds(focusedElement);
            }

            return false;
        }

        AutomationElement? currentElement = focusedElement;

        while (currentElement is not null)
        {
            TryAddCandidate(currentElement, candidates);

            try
            {
                currentElement = TreeWalker.RawViewWalker.GetParent(currentElement);
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
        }

        return true;
    }

    private static Windows.Graphics.RectInt32? FindStartButtonBounds(
        AutomationElement focusedElement)
    {
        AutomationElement? currentElement = focusedElement;
        for (int depth = 0; currentElement is not null && depth < 8; depth++)
        {
            try
            {
                AutomationElement.AutomationElementInformation current = currentElement.Current;
                if (string.Equals(
                        current.AutomationId,
                        "StartButton",
                        StringComparison.OrdinalIgnoreCase)
                    && !current.BoundingRectangle.IsEmpty)
                {
                    return ToRectInt32(current.BoundingRectangle);
                }

                currentElement = TreeWalker.RawViewWalker.GetParent(currentElement);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        return null;
    }

    private static System.Windows.Rect? SelectStartMenuBounds(
        IReadOnlyCollection<AutomationCandidate> candidates)
    {
        AutomationCandidate? windowCandidate = candidates
            .Where(candidate => !StartMenuBoundsValidator.CoversMostOfMonitor(
                ToRectInt32(candidate.Rectangle)))
            .Where(candidate => candidate.ControlType == ControlType.Window.ProgrammaticName)
            .OrderBy(candidate => candidate.Rectangle.Width * candidate.Rectangle.Height)
            .FirstOrDefault();

        if (windowCandidate is not null)
        {
            return windowCandidate.Rectangle;
        }

        return candidates
            .Where(candidate => !StartMenuBoundsValidator.CoversMostOfMonitor(
                ToRectInt32(candidate.Rectangle)))
            .OrderBy(candidate => candidate.Rectangle.Width * candidate.Rectangle.Height)
            .Select(candidate => (System.Windows.Rect?)candidate.Rectangle)
            .FirstOrDefault();
    }

    private static Windows.Graphics.RectInt32 ToRectInt32(System.Windows.Rect rectangle) =>
        new(
            (int)Math.Round(rectangle.X),
            (int)Math.Round(rectangle.Y),
            (int)Math.Round(rectangle.Width),
            (int)Math.Round(rectangle.Height));

    private static string GetProcessName(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    private static void TryAddCandidate(
        AutomationElement element,
        ICollection<AutomationCandidate> candidates)
    {
        try
        {
            AutomationElement.AutomationElementInformation current = element.Current;
            System.Windows.Rect rectangle = current.BoundingRectangle;

            if (current.IsOffscreen
                || rectangle.IsEmpty
                || rectangle.Width < MinimumCandidateWidth
                || rectangle.Height < MinimumCandidateHeight)
            {
                return;
            }

            candidates.Add(new AutomationCandidate(
                Sanitize(current.Name),
                Sanitize(current.AutomationId),
                current.ControlType?.ProgrammaticName ?? string.Empty,
                current.NativeWindowHandle,
                rectangle));
        }
        catch (ElementNotAvailableException)
        {
            // The Start menu tree changes while it animates. A vanished element is expected.
        }
    }

    private void UpdateSnapshot(StartMenuSnapshot snapshot)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _snapshot != snapshot;
            _snapshot = snapshot;
        }

        if (changed)
        {
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void LogSnapshotIfChanged(string state, IReadOnlyCollection<AutomationCandidate> candidates)
    {
        string candidateSignature = string.Join(
            '|',
            candidates.Select(candidate =>
                $"{candidate.AutomationId}:{candidate.Rectangle.Left:F0}:{candidate.Rectangle.Top:F0}:" +
                $"{candidate.Rectangle.Width:F0}:{candidate.Rectangle.Height:F0}"));
        string signature = $"{state}:{candidateSignature}";

        if (signature == _lastSnapshotSignature)
        {
            return;
        }

        _lastSnapshotSignature = signature;
        _logger.Write($"[UIAutomation] start-menu-state={state} candidates={candidates.Count}");

        foreach (AutomationCandidate candidate in candidates)
        {
            _logger.WriteDetailed(
                $"[UIAutomationCandidate] name=\"{candidate.Name}\" automation-id=\"{candidate.AutomationId}\" " +
                $"control-type=\"{candidate.ControlType}\" hwnd=0x{candidate.NativeWindowHandle:X} " +
                $"rect=({candidate.Rectangle.Left:F0},{candidate.Rectangle.Top:F0})-" +
                $"({candidate.Rectangle.Right:F0},{candidate.Rectangle.Bottom:F0}) " +
                $"size={candidate.Rectangle.Width:F0}x{candidate.Rectangle.Height:F0}");
        }
    }

    private static string Sanitize(string value)
    {
        StringBuilder sanitized = new(value.Length);

        foreach (char character in value)
        {
            sanitized.Append(character is '\r' or '\n' or '"' ? ' ' : character);
        }

        return sanitized.ToString();
    }

    private sealed record AutomationCandidate(
        string Name,
        string AutomationId,
        string ControlType,
        int NativeWindowHandle,
        System.Windows.Rect Rectangle);

}

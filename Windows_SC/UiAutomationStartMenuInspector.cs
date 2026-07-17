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
    private readonly AutoResetEvent _scanRequested = new(false);
    private readonly Thread _workerThread;
    private readonly object _stateLock = new();
    private StartMenuSnapshot _snapshot = StartMenuSnapshot.Hidden;
    private bool _isDisposed;
    private string _lastSnapshotSignature = string.Empty;
    private string _lastDesktopRootSignature = string.Empty;
    private string _lastFocusedElementSignature = string.Empty;
    private string _lastPhonePanelSignature = "<not-scanned>";
    private bool _phonePanelScannedForCurrentOpen;

    public UiAutomationStartMenuInspector(DiagnosticLogger logger)
    {
        _logger = logger;
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Windows_SC UI Automation"
        };
        _workerThread.SetApartmentState(ApartmentState.STA);
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
        RequestScan();
        _logger.Write("[UIAutomation] worker=started apartment=STA");
    }

    public void RequestScan()
    {
        if (!_isDisposed)
        {
            _scanRequested.Set();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _scanRequested.Set();
        _workerThread.Join(TimeSpan.FromSeconds(1));
        _scanRequested.Dispose();
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _scanRequested.WaitOne(TimeSpan.FromMilliseconds(100));

            if (_isDisposed)
            {
                return;
            }

            try
            {
                ScanDesktopAutomationTree();
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

        if (processIds.Length == 0)
        {
            _phonePanelScannedForCurrentOpen = false;
            UpdateSnapshot(StartMenuSnapshot.Hidden);
            LogSnapshotIfChanged("process-not-found", []);
            return;
        }

        List<AutomationCandidate> candidates = [];

        if (TryCollectFocusedStartMenuElement(processIds, candidates, out AutomationElement? startRoot))
        {
            System.Windows.Rect? startBounds = SelectStartMenuBounds(candidates);
            StartMenuSnapshot previousSnapshot = Snapshot;
            UpdateSnapshot(new StartMenuSnapshot(
                true,
                startBounds is null ? null : ToRectInt32(startBounds.Value),
                previousSnapshot.IsPhonePanelVisible,
                previousSnapshot.PhonePanelBounds));
            LogSnapshotIfChanged("visible-focused-element", candidates);

            // Phone panel discovery can cross process boundaries and is intentionally
            // performed only after the visible state and Start bounds are published.
            if (!_phonePanelScannedForCurrentOpen)
            {
                _phonePanelScannedForCurrentOpen = true;
                List<AutomationCandidate> phonePanelCandidates = CollectPhonePanelCandidates(startRoot);
                System.Windows.Rect? phonePanelBounds = UnionBounds(phonePanelCandidates);
                if (phonePanelCandidates.Count > 0)
                {
                    UpdateSnapshot(new StartMenuSnapshot(
                        true,
                        startBounds is null ? null : ToRectInt32(startBounds.Value),
                        true,
                        phonePanelBounds is null ? null : ToRectInt32(phonePanelBounds.Value)));
                }

                LogPhonePanelIfChanged(phonePanelCandidates);
            }
            return;
        }

        LogDesktopRootsIfChanged();

        foreach (int processId in processIds)
        {
            Condition processCondition = new PropertyCondition(
                AutomationElement.ProcessIdProperty,
                processId);
            AutomationElementCollection roots = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                processCondition);

            foreach (AutomationElement root in roots)
            {
                TryAddCandidate(root, candidates);

                AutomationElementCollection descendants = root.FindAll(
                    TreeScope.Descendants,
                    Condition.TrueCondition);

                foreach (AutomationElement descendant in descendants)
                {
                    TryAddCandidate(descendant, candidates);
                }
            }
        }

        bool isVisible = candidates.Count > 0;
        if (!isVisible)
        {
            _phonePanelScannedForCurrentOpen = false;
        }
        System.Windows.Rect? fallbackBounds = SelectStartMenuBounds(candidates);
        UpdateSnapshot(new StartMenuSnapshot(
            isVisible,
            fallbackBounds is null ? null : ToRectInt32(fallbackBounds.Value),
            false,
            null));
        string signature = isVisible ? "visible" : "hidden";
        LogSnapshotIfChanged(signature, candidates);
    }

    private bool TryCollectFocusedStartMenuElement(
        IReadOnlyCollection<int> startMenuProcessIds,
        ICollection<AutomationCandidate> candidates,
        out AutomationElement? startRoot)
    {
        startRoot = null;
        AutomationElement focusedElement = AutomationElement.FocusedElement;
        AutomationElement.AutomationElementInformation focused = focusedElement.Current;
        string focusedSignature = $"{focused.ProcessId}:{focused.AutomationId}:{focused.Name}";

        if (focusedSignature != _lastFocusedElementSignature)
        {
            _lastFocusedElementSignature = focusedSignature;
            System.Windows.Rect rectangle = focused.BoundingRectangle;
            _logger.Write(
                $"[UIAutomationFocus] process={GetProcessName(focused.ProcessId)} pid={focused.ProcessId} " +
                $"name=\"{Sanitize(focused.Name)}\" automation-id=\"{Sanitize(focused.AutomationId)}\" " +
                $"control-type=\"{focused.ControlType?.ProgrammaticName}\" " +
                $"rect=({rectangle.Left:F0},{rectangle.Top:F0})-({rectangle.Right:F0},{rectangle.Bottom:F0})");
        }

        if (!startMenuProcessIds.Contains(focused.ProcessId))
        {
            return false;
        }

        AutomationElement? currentElement = focusedElement;

        while (currentElement is not null)
        {
            TryAddCandidate(currentElement, candidates);

            try
            {
                if (currentElement.Current.ControlType == ControlType.Window)
                {
                    startRoot = currentElement;
                }
            }
            catch (ElementNotAvailableException)
            {
                break;
            }

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

    private static List<AutomationCandidate> CollectPhonePanelCandidates(AutomationElement? startRoot)
    {
        if (startRoot is null)
        {
            return [];
        }

        string[] keywords =
        [
            "phone", "mobile", "android", "iphone",
            "スマートフォン", "モバイル", "携帯電話", "電話連携"
        ];
        List<AutomationCandidate> candidates = [];

        try
        {
            AutomationElementCollection descendants = startRoot.FindAll(
                TreeScope.Descendants,
                Condition.TrueCondition);

            foreach (AutomationElement element in descendants)
            {
                try
                {
                    AutomationElement.AutomationElementInformation current = element.Current;
                    string searchableText = $"{current.Name} {current.AutomationId}";

                    if (!keywords.Any(keyword =>
                        searchableText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    System.Windows.Rect rectangle = current.BoundingRectangle;
                    if (!current.IsOffscreen && !rectangle.IsEmpty)
                    {
                        candidates.Add(new AutomationCandidate(
                            Sanitize(current.Name),
                            Sanitize(current.AutomationId),
                            current.ControlType?.ProgrammaticName ?? string.Empty,
                            current.NativeWindowHandle,
                            rectangle));
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // The panel tree can change while it animates.
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            return [];
        }

        return candidates;
    }

    private static System.Windows.Rect? SelectStartMenuBounds(
        IReadOnlyCollection<AutomationCandidate> candidates)
    {
        AutomationCandidate? windowCandidate = candidates
            .Where(candidate => candidate.ControlType == ControlType.Window.ProgrammaticName)
            .OrderBy(candidate => candidate.Rectangle.Width * candidate.Rectangle.Height)
            .FirstOrDefault();

        if (windowCandidate is not null)
        {
            return windowCandidate.Rectangle;
        }

        return candidates
            .OrderBy(candidate => candidate.Rectangle.Width * candidate.Rectangle.Height)
            .Select(candidate => (System.Windows.Rect?)candidate.Rectangle)
            .FirstOrDefault();
    }

    private static System.Windows.Rect? UnionBounds(
        IReadOnlyCollection<AutomationCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        System.Windows.Rect bounds = candidates.First().Rectangle;
        foreach (AutomationCandidate candidate in candidates.Skip(1))
        {
            bounds.Union(candidate.Rectangle);
        }

        return bounds;
    }

    private static Windows.Graphics.RectInt32 ToRectInt32(System.Windows.Rect rectangle) =>
        new(
            (int)Math.Round(rectangle.X),
            (int)Math.Round(rectangle.Y),
            (int)Math.Round(rectangle.Width),
            (int)Math.Round(rectangle.Height));

    private void LogDesktopRootsIfChanged()
    {
        AutomationElementCollection roots = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            Condition.TrueCondition);
        List<DesktopRootCandidate> candidates = [];

        foreach (AutomationElement root in roots)
        {
            try
            {
                AutomationElement.AutomationElementInformation current = root.Current;
                System.Windows.Rect rectangle = current.BoundingRectangle;

                if (current.IsOffscreen
                    || rectangle.IsEmpty
                    || rectangle.Width < MinimumCandidateWidth
                    || rectangle.Height < MinimumCandidateHeight)
                {
                    continue;
                }

                candidates.Add(new DesktopRootCandidate(
                    current.ProcessId,
                    GetProcessName(current.ProcessId),
                    Sanitize(current.Name),
                    Sanitize(current.AutomationId),
                    current.ControlType?.ProgrammaticName ?? string.Empty,
                    current.NativeWindowHandle,
                    rectangle));
            }
            catch (ElementNotAvailableException)
            {
                // The desktop tree can change during enumeration.
            }
        }

        string signature = string.Join(
            '|',
            candidates.Select(candidate =>
                $"{candidate.ProcessId}:{candidate.AutomationId}:{candidate.Rectangle.Left:F0}:" +
                $"{candidate.Rectangle.Top:F0}:{candidate.Rectangle.Width:F0}:{candidate.Rectangle.Height:F0}"));

        if (signature == _lastDesktopRootSignature)
        {
            return;
        }

        _lastDesktopRootSignature = signature;
        _logger.Write($"[UIAutomationDesktop] visible-roots={candidates.Count}");

        foreach (DesktopRootCandidate candidate in candidates)
        {
            _logger.Write(
                $"[UIAutomationDesktopRoot] process={candidate.ProcessName} pid={candidate.ProcessId} " +
                $"name=\"{candidate.Name}\" automation-id=\"{candidate.AutomationId}\" " +
                $"control-type=\"{candidate.ControlType}\" hwnd=0x{candidate.NativeWindowHandle:X} " +
                $"rect=({candidate.Rectangle.Left:F0},{candidate.Rectangle.Top:F0})-" +
                $"({candidate.Rectangle.Right:F0},{candidate.Rectangle.Bottom:F0}) " +
                $"size={candidate.Rectangle.Width:F0}x{candidate.Rectangle.Height:F0}");
        }
    }

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
        lock (_stateLock)
        {
            _snapshot = snapshot;
        }
    }

    private void LogPhonePanelIfChanged(IReadOnlyCollection<AutomationCandidate> candidates)
    {
        string signature = string.Join(
            '|',
            candidates.Select(candidate =>
                $"{candidate.AutomationId}:{candidate.Rectangle.Left:F0}:{candidate.Rectangle.Top:F0}:" +
                $"{candidate.Rectangle.Width:F0}:{candidate.Rectangle.Height:F0}"));

        if (signature == _lastPhonePanelSignature)
        {
            return;
        }

        _lastPhonePanelSignature = signature;
        _logger.Write($"[PhonePanel] detected={candidates.Count > 0} candidates={candidates.Count}");

        foreach (AutomationCandidate candidate in candidates)
        {
            _logger.Write(
                $"[PhonePanelCandidate] name=\"{candidate.Name}\" automation-id=\"{candidate.AutomationId}\" " +
                $"rect=({candidate.Rectangle.Left:F0},{candidate.Rectangle.Top:F0})-" +
                $"({candidate.Rectangle.Right:F0},{candidate.Rectangle.Bottom:F0})");
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
            _logger.Write(
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

    private sealed record DesktopRootCandidate(
        int ProcessId,
        string ProcessName,
        string Name,
        string AutomationId,
        string ControlType,
        int NativeWindowHandle,
        System.Windows.Rect Rectangle);
}

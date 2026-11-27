using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using System.Threading;
using ScreenTimeTracker.Models;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using ScreenTimeTracker.Helpers;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Foundation;

namespace ScreenTimeTracker.Services
{
    public class WindowTrackingService : IDisposable
    {
        private readonly System.Timers.Timer _timer;
        private AppUsageRecord? _currentRecord;
        private bool _disposed;
        private readonly object _lockObject = new object();
        private int _trackingGeneration;
        private long _observation;
        private long _lastAppliedForegroundObservation;
        private long _lastAppliedIdleObservation;
        private bool _isIdle = false;
        private AppUsageRecord? _idleRecord;

#if !UNIT_TEST
        // ---------- WinEvent hook fields ----------
        private IntPtr _focusHook = IntPtr.Zero;
        private WinEventDelegate? _winEventDelegate;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint   eventType,
            IntPtr hwnd,
            int    idObject,
            int    idChild,
            uint   dwEventThread,
            uint   dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
#endif

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        // Helper – returns user idle time in seconds
        private static int GetIdleSeconds()
        {
            LASTINPUTINFO li = new LASTINPUTINFO();
            li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            return GetLastInputInfo(ref li) ? (Environment.TickCount - (int)li.dwTime) / 1000 : 0;
        }

        // Helper to detect any system media session that is actively playing
        private static bool IsAnyMediaPlaying()
        {
            try
            {
                var mgr = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().GetAwaiter().GetResult();
                foreach (var session in mgr.GetSessions())
                {
                    var info = session.GetPlaybackInfo();
                    if (info != null && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        return true;
                }
            }
            catch { /* ignore environments where API not available */ }
            return false;
        }

        public event EventHandler<AppUsageRecord>? UsageRecordUpdated;
        public event EventHandler<UsageSlice>? UsageSliceFinalized;

#if UNIT_TEST
        internal Func<DateTime> ClockForTest { get; set; } = () => DateTime.Now;
        private DateTime Now => ClockForTest();
#else
        private static DateTime Now => DateTime.Now;
#endif
        
        public bool IsTracking { get; private set; }
        public AppUsageRecord? CurrentRecord
        {
            get
            {
                lock (_lockObject)
                {
                    return _currentRecord?.CreateSnapshot();
                }
            }
        }

        public WindowTrackingService()
        {

            _timer = new System.Timers.Timer(500);
            _timer.Elapsed += Timer_Elapsed;
            _timer.AutoReset = true;
            
            IsTracking = false;
            Debug.WriteLine("WindowTrackingService initialized.");

#if !UNIT_TEST
            // Register foreground-window change hook
            _winEventDelegate = OnWinEvent;
            _focusHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                                         IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
#endif
        }

        public void StartTracking()
        {
            int generation;
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (IsTracking) return;

                _timer.Start();
                IsTracking = true;
                generation = _trackingGeneration;
            }
            
            // Call CheckActiveWindow outside of lock to avoid deadlock
            CheckActiveWindow(generation);
        }

        public void StopTracking()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (!IsTracking) return;

                _timer.Stop();
                IsTracking = false;
                _trackingGeneration++;

                FinalizeOpenRecords(Now);
            }
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            try
            {
                if (GetTrackingGeneration() is not int generation) return;

                long observation = NextObservation();
                bool mediaPlaying = IsAnyMediaPlaying();
                int? refreshGeneration = null;

                lock (_lockObject)
                {
                    bool currentlyIdle = !mediaPlaying && GetIdleSeconds() > 300;
                    if (ApplyIdleObservation(currentlyIdle, Now, generation, observation))
                        refreshGeneration = _trackingGeneration;
                }

                if (refreshGeneration is int resumedGeneration)
                    CheckActiveWindow(resumedGeneration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in Timer_Elapsed: {ex.Message}");
            }
        }

        private int? GetTrackingGeneration()
        {
            lock (_lockObject)
                return IsTracking ? _trackingGeneration : null;
        }

        private long NextObservation() => Interlocked.Increment(ref _observation);

        private void CheckActiveWindow(int generation, IntPtr? window = null, long? observation = null)
        {
            try
            {
                long order = observation ?? NextObservation();
                var foregroundWindow = window ?? GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero || !IsWindow(foregroundWindow)) return;
                uint processId;
                GetWindowThreadProcessId(foregroundWindow, out processId);
                if (processId == 0) return;
                var windowTitle = GetActiveWindowTitle(foregroundWindow);
                var processName = GetProcessName(foregroundWindow);

                ProcessWindowChange(foregroundWindow, (int)processId, processName, windowTitle, generation, order);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in CheckActiveWindow: {ex.Message}");
            }
        }

        private string GetActiveWindowTitle(IntPtr handle)
        {
            try
            {
                // Validate handle before using it
                if (handle == IntPtr.Zero || !IsWindow(handle))
                {
                    return string.Empty;
                }
                
                const int nChars = 256;
                StringBuilder buff = new StringBuilder(nChars);
                if (GetWindowText(handle, buff, nChars) > 0)
                {
                    return buff.ToString();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in GetActiveWindowTitle: {ex.Message}");
            }
            return string.Empty;
        }

        private string GetProcessName(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return "Unknown";

            try
            {
                // Validate handle before using it
                if (!IsWindow(handle))
                {
                    System.Diagnostics.Debug.WriteLine("GetProcessName - Invalid window handle");
                    return "Unknown";
                }
                
                uint processId;
                GetWindowThreadProcessId(handle, out processId);
                if (processId == 0)
                    return "Unknown";

                using (var process = System.Diagnostics.Process.GetProcessById((int)processId))
                {
                    return process.ProcessName;
                }
            }
            catch (ArgumentException ex)
            {
                // Process has exited
                System.Diagnostics.Debug.WriteLine($"Process has exited: {ex.Message}");
                return "Unknown";
            }
            catch (InvalidOperationException ex)
            {
                // Process has exited or access denied
                System.Diagnostics.Debug.WriteLine($"Cannot access process: {ex.Message}");
                return "Unknown";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in GetProcessName: {ex.Message}");
                return "Unknown";
            }
        }

        public IEnumerable<AppUsageRecord> GetRecords()
        {
            lock (_lockObject)
            {
                var live = new List<AppUsageRecord>();
                if (_currentRecord != null) live.Add(_currentRecord.CreateSnapshot());
                if (_idleRecord != null) live.Add(_idleRecord.CreateSnapshot());
                return live;
            }
        }

        private void PublishUsageRecord(AppUsageRecord record)
        {
            UsageRecordUpdated?.Invoke(this, record.CreateSnapshot());
        }

        private void FinalizeRecord(AppUsageRecord record, DateTime endTime)
        {
            var startTime = FinalizeCompletedDays(record, endTime);
            FinalizeSegment(record, startTime, endTime);
        }

        private void FinalizeSegment(AppUsageRecord record, DateTime startTime, DateTime endTime)
        {
            var ended = record.CreateSnapshot();
            ended.StartTime = startTime;
            ended.Date = startTime.Date;
            ended.IsFocused = false;
            ended.EndTime = endTime;
            ended._accumulatedDuration = TimeSpan.Zero;
            if (UsageSlice.TryCreate(
                record.ProcessName,
                record.ApplicationName,
                record.WindowTitle,
                startTime,
                endTime,
                out var slice) && slice != null)
            {
                ended._accumulatedDuration = slice.Duration;
                if (slice.Duration.TotalMilliseconds >= 1)
                    UsageSliceFinalized?.Invoke(this, slice);
            }

            UsageRecordUpdated?.Invoke(this, ended);
        }

        private DateTime FinalizeCompletedDays(AppUsageRecord record, DateTime now)
        {
            var start = record.StartTime;
            while (start.Date < now.Date)
            {
                var midnight = start.Date.AddDays(1);
                FinalizeSegment(record, start, midnight);
                start = midnight;
            }
            return start;
        }

        private void FinalizeOpenRecords(DateTime endTime)
        {
            if (_currentRecord != null)
            {
                FinalizeRecord(_currentRecord, endTime);
                _currentRecord = null;
            }

            if (_idleRecord != null)
            {
                FinalizeRecord(_idleRecord, endTime);
                _idleRecord = null;
            }

            _isIdle = false;
        }

        private void UpdateLiveRecord(DateTime now)
        {
            var record = _idleRecord ?? _currentRecord;
            if (record == null) return;

            var start = FinalizeCompletedDays(record, now);
            if (start != record.StartTime)
            {
                record.StartTime = start;
                record.Date = start.Date;
                record._accumulatedDuration = TimeSpan.Zero;
                if (record.IsFocused) record.SetIdleAnchor(start);
            }

            if (_isIdle) record._accumulatedDuration = now - start;
            PublishUsageRecord(record);
        }

        private bool ApplyIdleObservation(bool currentlyIdle, DateTime now, int generation, long observation)
        {
            if (!IsTracking || generation != _trackingGeneration || observation <= _lastAppliedIdleObservation)
                return false;

            _lastAppliedIdleObservation = observation;
            return ApplyIdleState(currentlyIdle, now);
        }

        private bool ApplyIdleState(bool currentlyIdle, DateTime now)
        {
            if (currentlyIdle == _isIdle)
            {
                UpdateLiveRecord(now);
                return false;
            }

            _isIdle = currentlyIdle;
            _trackingGeneration++;

            if (currentlyIdle)
            {
                if (_currentRecord != null)
                {
                    FinalizeRecord(_currentRecord, now);
                    _currentRecord = null;
                }

                if (_idleRecord == null)
                {
                    _idleRecord = new AppUsageRecord
                    {
                        ProcessName = "Idle / Away",
                        ApplicationName = "Idle / Away",
                        StartTime = now,
                        Date = now.Date
                    };
                    PublishUsageRecord(_idleRecord);
                }

                return false;
            }

            if (_idleRecord != null)
            {
                FinalizeRecord(_idleRecord, now);
                _idleRecord = null;
            }

            return true;
        }

#if UNIT_TEST
        internal void SetOpenRecordsForTest(AppUsageRecord? currentRecord, AppUsageRecord? idleRecord, bool isTracking = true)
        {
            lock (_lockObject)
            {
                _currentRecord = currentRecord;
                _idleRecord = idleRecord;
                _isIdle = idleRecord != null;
                IsTracking = isTracking;
            }
        }

        internal void UpdateFocusedRecordForTest(DateTime now)
        {
            lock (_lockObject)
            {
                UpdateLiveRecord(now);
            }
        }

        internal void ProcessWindowChangeForTest(IntPtr foregroundWindow, int processId, string processName, string windowTitle)
        {
            if (GetTrackingGeneration() is int generation)
                ProcessWindowChange(foregroundWindow, processId, processName, windowTitle, generation, NextObservation());
        }

        internal bool ApplyIdleStateForTest(bool currentlyIdle, DateTime now)
        {
            lock (_lockObject)
            {
                return ApplyIdleObservation(currentlyIdle, now, _trackingGeneration, NextObservation());
            }
        }
#endif

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WindowTrackingService));
            }
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                if (!_disposed)
                {
                    // Stop and dispose timer first to prevent any more events
                    try
                    {
                        _timer.Stop();
                        _timer.Elapsed -= Timer_Elapsed;
                        _timer.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error disposing timer: {ex.Message}");
                    }
                    
                    IsTracking = false;
                    _currentRecord = null;
                    _disposed = true;

#if !UNIT_TEST
                    if (_focusHook != IntPtr.Zero)
                    {
                        try { UnhookWinEvent(_focusHook); } catch { }
                        _focusHook = IntPtr.Zero;
                        _winEventDelegate = null;
                    }
#endif

                }
            }
        }

#if !UNIT_TEST
        // ---------- WinEvent hook callback ----------
        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (eventType == EVENT_SYSTEM_FOREGROUND && hwnd != IntPtr.Zero && GetTrackingGeneration() is int generation)
            {
                // Process on thread-pool to avoid blocking WinEvent thread
                long observation = NextObservation();
                Task.Run(() => CheckActiveWindow(generation, hwnd, observation));
            }
        }
#endif

        // Extracted shared logic from original CheckActiveWindow body starting after obtaining names
        private void ProcessWindowChange(IntPtr foregroundWindow, int processId, string processName, string windowTitle, int generation, long observation)
        {
            lock (_lockObject)
            {
                if (!IsTracking || _isIdle || generation != _trackingGeneration ||
                    observation <= _lastAppliedForegroundObservation) return;

                _lastAppliedForegroundObservation = observation;
                var now = Now;

                // If the same window remains focused, nothing to do
                if (_currentRecord != null &&
                    _currentRecord.WindowHandle == foregroundWindow &&
                    _currentRecord.ProcessId   == processId        &&
                    _currentRecord.WindowTitle == windowTitle)
                {
                    if (!_currentRecord.IsFocused)
                    {
                        _currentRecord.SetFocus(true);
                        PublishUsageRecord(_currentRecord);
                    }
                    return;
                }

                // Finalise previous slice
                if (_currentRecord != null)
                {
                    FinalizeRecord(_currentRecord, now);
                }

                // Start new slice
                _currentRecord = new AppUsageRecord
                {
                    ProcessName    = processName,
                    WindowTitle    = windowTitle,
                    StartTime      = now,
                    ProcessId      = processId,
                    WindowHandle   = foregroundWindow,
                    Date           = now.Date,
                    ApplicationName= processName
                };
                ApplicationProcessingHelper.ProcessApplicationRecord(_currentRecord);
                _currentRecord.SetFocus(true);
                PublishUsageRecord(_currentRecord);
            }
        }
    }
} 

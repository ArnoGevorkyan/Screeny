using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
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
        private bool _isIdle = false;
        private AppUsageRecord? _idleRecord;

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
        public event EventHandler? WindowChanged;
        public event EventHandler<AppUsageRecord>? RecordReadyForSave;
        
        public bool IsTracking { get; private set; }
        public AppUsageRecord? CurrentRecord => _currentRecord;

        public WindowTrackingService()
        {

            _timer = new System.Timers.Timer(500);
            _timer.Elapsed += Timer_Elapsed;
            _timer.AutoReset = true;
            
            IsTracking = false;
            Debug.WriteLine("WindowTrackingService initialized.");

            // Register foreground-window change hook
            _winEventDelegate = OnWinEvent;
            _focusHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                                         IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        public void StartTracking()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (IsTracking) return;

                _timer.Start();
                IsTracking = true;
            }
            
            // Call CheckActiveWindow outside of lock to avoid deadlock
            CheckActiveWindow();
        }

        public void StopTracking()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (!IsTracking) return;

                _timer.Stop();
                IsTracking = false;

                // Finalise focused slice
                if (_currentRecord != null)
                {
                    _currentRecord.SetFocus(false);
                    _currentRecord.EndTime = DateTime.Now;
                    RecordReadyForSave?.Invoke(this, _currentRecord);
                    _currentRecord = null;
                }

                // Finalise idle slice if present
                if (_idleRecord != null)
                {
                    _idleRecord.EndTime = DateTime.Now;
                    RecordReadyForSave?.Invoke(this, _idleRecord);
                    _idleRecord = null;
                }
            }
        }

        public void PauseTrackingForSuspend()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (!IsTracking) return;

                _timer.Stop();
                IsTracking = false;

                if (_currentRecord != null)
                {
                    _currentRecord.SetFocus(false);
                    _currentRecord.EndTime = DateTime.Now;
                    RecordReadyForSave?.Invoke(this, _currentRecord);
                    _currentRecord = null;
                }
            }
        }

        public void ResumeTrackingAfterSuspend()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (IsTracking) return;

                IsTracking = true;
                _timer.Start();
            }
            
            // Call CheckActiveWindow outside of lock to avoid deadlock
            CheckActiveWindow();
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            // Lightweight tick: idle detection, active-window check, live duration update, day rollover.
            try
            {
                // ----- Idle detection -----
                int idleSec = GetIdleSeconds();
                bool mediaPlaying = IsAnyMediaPlaying();
                bool currentlyIdle = idleSec > 300 && !mediaPlaying; // 5 minutes idle pause

                lock (_lockObject)
                {
                    if (!IsTracking || _disposed) return;

                    if (currentlyIdle && !_isIdle)
                    {
                        _isIdle = true;
                        _currentRecord?.SetIdleAnchor(DateTime.Now);

                        // Start idle slice
                        if (_idleRecord == null)
                        {
                            _idleRecord = new AppUsageRecord
                            {
                                ProcessName = "Idle / Away",
                                ApplicationName = "Idle / Away",
                                StartTime = DateTime.Now,
                                Date = EnsureValidDate(DateTime.Now.Date)
                            };
                            UsageRecordUpdated?.Invoke(this, _idleRecord);
                        }
                    }
                    else if (!currentlyIdle && _isIdle)
                    {
                        _isIdle = false;
                        _currentRecord?.SetIdleAnchor(DateTime.Now);

                        // Finalise idle slice
                        if (_idleRecord != null)
                        {
                            _idleRecord.EndTime = DateTime.Now;
                            RecordReadyForSave?.Invoke(this, _idleRecord);
                            UsageRecordUpdated?.Invoke(this, _idleRecord);
                            _idleRecord = null;
                        }
                    }
                }

                if (currentlyIdle) return; // Skip heavy work while idle

                // ----- Active-window tracking -----
                // Real-time events handle window changes, timer only does duration updates

                // ----- Live-duration update & day rollover -----
                lock (_lockObject)
                {
                    if (_currentRecord != null && _currentRecord.IsFocused)
                    {
                        _currentRecord._accumulatedDuration = DateTime.Now - _currentRecord.StartTime;
                        UsageRecordUpdated?.Invoke(this, _currentRecord);

                        // Detect midnight crossover
                        if (_currentRecord.Date < DateTime.Today)
                        {
                            var endOfPrevDay = _currentRecord.Date.AddDays(1).AddSeconds(-1);
                            _currentRecord.EndTime = endOfPrevDay;
                            _currentRecord.SetFocus(false);
                            RecordReadyForSave?.Invoke(this, _currentRecord);
                            _currentRecord = null;
                        }
                    }
                }

                // Update idle duration if still in idle
                if (_idleRecord != null)
                {
                    _idleRecord._accumulatedDuration = DateTime.Now - _idleRecord.StartTime;
                    UsageRecordUpdated?.Invoke(this, _idleRecord);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in Timer_Elapsed: {ex.Message}");
            }
        }

        private void CheckActiveWindow()
        {
            lock (_lockObject)
            {
                if (!IsTracking || _disposed)
                {
                    return;
                }
            }

            try
            {
                var foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero || !IsWindow(foregroundWindow)) return;
                uint processId;
                GetWindowThreadProcessId(foregroundWindow, out processId);
                if (processId == 0) return;
                var windowTitle = GetActiveWindowTitle(foregroundWindow);
                var processName = GetProcessName(foregroundWindow);

                ProcessWindowChange(foregroundWindow, (int)processId, processName, windowTitle);
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
            var live = new List<AppUsageRecord>();
            if (_currentRecord != null) live.Add(_currentRecord);
            if (_idleRecord    != null) live.Add(_idleRecord);
            return live;
        }

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

                    if (_focusHook != IntPtr.Zero)
                    {
                        try { UnhookWinEvent(_focusHook); } catch { }
                        _focusHook = IntPtr.Zero;
                        _winEventDelegate = null;
                    }

                }
            }
        }

        private DateTime EnsureValidDate(DateTime date)
        {
            if (date > DateTime.Today)
            {
                System.Diagnostics.Debug.WriteLine($"WARNING: Future date detected ({date:yyyy-MM-dd}), using current date instead.");
                return DateTime.Today;
            }
            return date;
        }

        // ---------- WinEvent hook callback ----------
        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (eventType == EVENT_SYSTEM_FOREGROUND && hwnd != IntPtr.Zero)
            {
                // Process on thread-pool to avoid blocking WinEvent thread
                Task.Run(() => CheckActiveWindow(hwnd));
            }
        }

        // Overload that skips GetForegroundWindow and uses supplied handle
        private void CheckActiveWindow(IntPtr foregroundWindow)
        {
            if (foregroundWindow == IntPtr.Zero) return;
            // Reuse main logic by moving body to shared method if possible; for now duplicate minimal parts
            lock (_lockObject)
            {
                if (!IsTracking || _disposed) return;
            }

            try
            {
                if (!IsWindow(foregroundWindow)) return;

                uint processId;
                GetWindowThreadProcessId(foregroundWindow, out processId);
                if (processId == 0) return;

                var windowTitle = GetActiveWindowTitle(foregroundWindow);
                var processName = GetProcessName(foregroundWindow);

                // Rest of logic identical to existing method -> call helper to reduce duplication
                ProcessWindowChange(foregroundWindow, (int)processId, processName, windowTitle);
            }
            catch (Exception ex) 
            { 
                System.Diagnostics.Debug.WriteLine($"Error in window tracking callback: {ex.Message}");
            }
        }

        // Extracted shared logic from original CheckActiveWindow body starting after obtaining names
        private void ProcessWindowChange(IntPtr foregroundWindow, int processId, string processName, string windowTitle)
        {
            lock (_lockObject)
            {
                // If the same window remains focused, nothing to do
                if (_currentRecord != null &&
                    _currentRecord.WindowHandle == foregroundWindow &&
                    _currentRecord.ProcessId   == processId        &&
                    _currentRecord.WindowTitle == windowTitle)
                {
                    if (!_currentRecord.IsFocused)
                    {
                        _currentRecord.SetFocus(true);
                        UsageRecordUpdated?.Invoke(this, _currentRecord);
                    }
                    return;
                }

                // Finalise previous slice
                if (_currentRecord != null)
                {
                    _currentRecord.SetFocus(false);
                    _currentRecord.EndTime = DateTime.Now;
                    RecordReadyForSave?.Invoke(this, _currentRecord);
                    UsageRecordUpdated?.Invoke(this, _currentRecord);
                }

                // Start new slice
                _currentRecord = new AppUsageRecord
                {
                    ProcessName    = processName,
                    WindowTitle    = windowTitle,
                    StartTime      = DateTime.Now,
                    ProcessId      = processId,
                    WindowHandle   = foregroundWindow,
                    Date           = EnsureValidDate(DateTime.Now.Date),
                    ApplicationName= processName
                };
                ApplicationProcessingHelper.ProcessApplicationRecord(_currentRecord);
                _currentRecord.SetFocus(true);
                UsageRecordUpdated?.Invoke(this, _currentRecord);
                WindowChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
} 
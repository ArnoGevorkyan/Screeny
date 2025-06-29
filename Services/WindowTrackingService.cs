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

namespace ScreenTimeTracker.Services
{
    public class WindowTrackingService : IDisposable
    {
        private readonly System.Timers.Timer _timer;
        private readonly System.Timers.Timer _dayChangeTimer;
        private AppUsageRecord? _currentRecord;
        private readonly List<AppUsageRecord> _records;
        private bool _disposed;
        private readonly DatabaseService _databaseService;
        private readonly object _lockObject = new object();
        private DateTime _lastCheckedDate;

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

        public event EventHandler<AppUsageRecord>? UsageRecordUpdated;
        public event EventHandler? WindowChanged;
        
        public bool IsTracking { get; private set; }
        public AppUsageRecord? CurrentRecord => _currentRecord;

        public WindowTrackingService(DatabaseService databaseService)
        {
            if (databaseService == null)
            {
                throw new ArgumentNullException(nameof(databaseService));
            }
            _databaseService = databaseService;

            _records = new List<AppUsageRecord>();
            _timer = new System.Timers.Timer(1000);
            _timer.Elapsed += Timer_Elapsed;
            _timer.AutoReset = true;
            
            _dayChangeTimer = new System.Timers.Timer(60000);
            _dayChangeTimer.Elapsed += DayChangeTimer_Elapsed;
            _dayChangeTimer.AutoReset = true;
            _lastCheckedDate = DateTime.Now.Date;
            
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

                Debug.WriteLine("==== WindowTrackingService.StartTracking() - Starting tracking ====");
                _records.Clear();
                _timer.Start();
                _dayChangeTimer.Start();
                IsTracking = true;
                _lastCheckedDate = DateTime.Now.Date;
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

                Debug.WriteLine("==== WindowTrackingService.StopTracking() - Stopping tracking (Normal) ====");
                _timer.Stop();
                _dayChangeTimer.Stop();
                IsTracking = false;

                // Ensure every record, including the (possible) current one, is unfocused
                // **exactly once** so we don't double-add the same focused slice.

                if (_currentRecord != null)
                {
                    // First finalise the current record
                    _currentRecord.SetFocus(false);
                    _currentRecord.EndTime = DateTime.Now;

                    // Add it to the list if it isn't already stored
                    if (!_records.Contains(_currentRecord))
                    {
                    _records.Add(_currentRecord);
                    }

                    _currentRecord = null;
                }

                // Now iterate the list to make sure none remain focused
                foreach (var rec in _records)
                {
                    if (rec.IsFocused)
                    {
                        rec.SetFocus(false);
                    }
                }

                Debug.WriteLine($"StopTracking: Finalized session with {_records.Count} records ready for saving.");
            }
        }

        public void PauseTrackingForSuspend()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (!IsTracking) return;

                Debug.WriteLine("==== WindowTrackingService.PauseTrackingForSuspend() - Pausing for system suspend ====");
                _timer.Stop();
                _dayChangeTimer.Stop();
                IsTracking = false;

                if (_currentRecord != null)
                {
                    Debug.WriteLine($"Suspend: Finalizing record for {_currentRecord.ProcessName}");
                    _currentRecord.SetFocus(false);
                    _currentRecord.EndTime = DateTime.Now;

                    try
                    {
                        Debug.WriteLine($"Suspend: Attempting immediate save for {_currentRecord.ProcessName} (Calculated Duration: {_currentRecord.Duration.TotalSeconds}s)");
                        
                        // Clone to avoid threading issues
                        var recordToSave = new AppUsageRecord
                        {
                            ProcessName = _currentRecord.ProcessName,
                            WindowTitle = _currentRecord.WindowTitle,
                            StartTime = _currentRecord.StartTime,
                            EndTime = _currentRecord.EndTime,
                            ProcessId = _currentRecord.ProcessId,
                            WindowHandle = _currentRecord.WindowHandle,
                            Date = _currentRecord.Date,
                            ApplicationName = _currentRecord.ApplicationName
                        };
                        
                        lock (_databaseService)
                        {
                            _databaseService.SaveRecord(recordToSave);
                        }
                        
                        Debug.WriteLine($"Suspend: Successfully saved record for {_currentRecord.ProcessName}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"CRITICAL ERROR: Failed to save record during suspend: {ex.Message}");
                        Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                    }

                    // Ensure it's part of the list for consistency, then clear focus list below
                    if (!_records.Contains(_currentRecord))
                    {
                        _records.Add(_currentRecord);
                    }

                    _currentRecord = null;
                }

                // Unfocus any residual records (single pass)
                foreach (var rec in _records)
                {
                    if (rec.IsFocused)
                    {
                        rec.SetFocus(false);
                    }
                }

                _records.Clear();
            }
        }

        public void ResumeTrackingAfterSuspend()
        {
            lock (_lockObject)
            {
                ThrowIfDisposed();
                if (IsTracking) return;

                Debug.WriteLine("==== WindowTrackingService.ResumeTrackingAfterSuspend() - Resuming tracking ====");
                IsTracking = true;
                _records.Clear();
                _timer.Start();
                _dayChangeTimer.Start();
            }
            
            // Call CheckActiveWindow outside of lock to avoid deadlock
            CheckActiveWindow();
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            lock (_lockObject)
            {
                if (_disposed) return;
            }
            
            try
            {
                // Pause tracking if the user is idle beyond the configured threshold
                if (GetIdleSeconds() > DurationLimits.IdlePauseSeconds)
                {
                    if (_currentRecord != null && _currentRecord.IsFocused)
                    {
                        _currentRecord.SetIdleAnchor(DateTime.Now);
                    }
                    return; // Skip this tick – no active interaction
                }

                // Verbose per-tick diagnostics removed.
                
                CheckActiveWindow();
                
                // Periodic cleanup: Save and remove records that haven't been focused for > 5 minutes
                var now = DateTime.Now;
                var recordsToRemove = new List<AppUsageRecord>();
                
                // Removed verbose cleanup diagnostics.
                foreach (var record in _records)
                {
                    // Verbose per-record diagnostics removed.
                    
                    // Skip the current record and focused records
                    if (record == _currentRecord || record.IsFocused)
                    {
                        // Skipping log removed.
                        continue;
                    }
                    
                    // If record has an end time and it's been more than 5 minutes, save and remove it
                    if (record.EndTime.HasValue && (now - record.EndTime.Value).TotalMinutes > 5)
                    {
                        // Removal marker log removed.
                        recordsToRemove.Add(record);
                    }
                    // If record doesn't have end time but hasn't been updated in 5 minutes, close it
                    else if (!record.EndTime.HasValue && record.Duration.TotalSeconds > 0 && 
                             (now - record.StartTime - record.Duration).TotalMinutes > 5)
                    {
                        // Removal marker log removed.
                        record.EndTime = record.StartTime + record.Duration;
                        recordsToRemove.Add(record);
                    }
                }
                
                // Save and remove old records
                foreach (var record in recordsToRemove)
                {
                    try
                    {
                        // Save/remove log removed.
                        lock (_databaseService)
                        {
                            _databaseService.SaveRecord(record);
                        }
                        _records.Remove(record);
                        // Save/remove success log removed.
                    }
                    catch (Exception ex)
                    {
                        // Save/remove error log removed (retained critical error below).
                        System.Diagnostics.Debug.WriteLine($"CRITICAL ERROR: Failed to save record during cleanup: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the exception but don't let it crash the app
                System.Diagnostics.Debug.WriteLine($"ERROR in Timer_Elapsed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private void DayChangeTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            lock (_lockObject)
            {
                if (_disposed || !IsTracking) return;
            }
            
            try
            {
                System.Diagnostics.Debug.WriteLine("DayChangeTimer_Elapsed - Checking for day change");
                var currentDate = DateTime.Now.Date;
                
                if (currentDate != _lastCheckedDate)
                {
                    System.Diagnostics.Debug.WriteLine($"Day changed from {_lastCheckedDate:yyyy-MM-dd} to {currentDate:yyyy-MM-dd}");
                    
                    lock (_lockObject)
                    {
                        // End all current sessions at 23:59:59 of the previous day
                        var endOfPreviousDay = _lastCheckedDate.AddDays(1).AddSeconds(-1);
                        
                        // Save and close any current record
                        if (_currentRecord != null)
                        {
                            _currentRecord.SetFocus(false);
                            _currentRecord.EndTime = endOfPreviousDay;
                            
                            try
                            {
                                // Save the record immediately
                                lock (_databaseService)
                                {
                                    _databaseService.SaveRecord(_currentRecord);
                                }
                                Debug.WriteLine($"Day change: Saved record for {_currentRecord.ProcessName}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"ERROR saving record during day change: {ex.Message}");
                            }
                        }
                        
                        // Save all accumulated records
                        foreach (var record in _records)
                        {
                            if (!record.EndTime.HasValue)
                            {
                                record.EndTime = endOfPreviousDay;
                            }
                            
                            try
                            {
                                lock (_databaseService)
                                {
                                    _databaseService.SaveRecord(record);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"ERROR saving record during day change: {ex.Message}");
                            }
                        }
                        
                        // Clear all records for the new day
                        _records.Clear();
                        _currentRecord = null;
                        _lastCheckedDate = currentDate;
                        
                        Debug.WriteLine($"Day change completed. All sessions closed and saved.");
                    }
                    
                    // Re-check active window to start fresh tracking for the new day
                    CheckActiveWindow();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in DayChangeTimer_Elapsed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
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
            return _records.ToList();
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
                    Debug.WriteLine("Disposing WindowTrackingService...");
                    
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
                    
                    try
                    {
                        _dayChangeTimer.Stop();
                        _dayChangeTimer.Elapsed -= DayChangeTimer_Elapsed;
                        _dayChangeTimer.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error disposing day change timer: {ex.Message}");
                    }
                    
                    IsTracking = false;
                    _records.Clear();
                    _currentRecord = null;
                    _disposed = true;

                    if (_focusHook != IntPtr.Zero)
                    {
                        try { UnhookWinEvent(_focusHook); } catch { }
                        _focusHook = IntPtr.Zero;
                        _winEventDelegate = null;
                    }

                    Debug.WriteLine("WindowTrackingService disposed.");
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
            catch { /* ignore */ }
        }

        // Extracted shared logic from original CheckActiveWindow body starting after obtaining names
        private void ProcessWindowChange(IntPtr foregroundWindow, int processId, string processName, string windowTitle)
        {
            lock (_lockObject)
            {
                // Unfocus previous if different
                if (_currentRecord != null &&
                    (_currentRecord.WindowHandle != foregroundWindow ||
                     _currentRecord.ProcessId    != processId         ||
                     _currentRecord.WindowTitle  != windowTitle))
                {
                    // Finalise previous slice and persist immediately
                    _currentRecord.SetFocus(false);
                    _currentRecord.EndTime = DateTime.Now;
                    try
                    {
                        lock (_databaseService)
                        {
                            if (_currentRecord.Id > 0)
                                _databaseService.UpdateRecord(_currentRecord);
                            else
                                _databaseService.SaveRecord(_currentRecord);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR saving record on focus change: {ex.Message}");
                    }

                    _currentRecord = null; // Clear reference so a fresh slice can start
                }

                // Ensure all other records are unfocused and finalised
                foreach (var record in _records.ToList())
                {
                    if (!record.IsFocused) continue;

                    record.SetFocus(false);
                    record.EndTime = DateTime.Now;

                    try
                    {
                        lock (_databaseService)
                        {
                            if (record.Id > 0)
                                _databaseService.UpdateRecord(record);
                            else
                                _databaseService.SaveRecord(record);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR saving record during unfocus sweep: {ex.Message}");
                    }
                }

                var existingRecord = _records.FirstOrDefault(r => r.ProcessId == processId && r.WindowTitle == windowTitle && r.WindowHandle == foregroundWindow);
                if (existingRecord == null)
                {
                    existingRecord = _records.FirstOrDefault(r => r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
                    if (existingRecord != null)
                    {
                        existingRecord.WindowHandle = foregroundWindow;
                        existingRecord.WindowTitle  = windowTitle;
                        existingRecord.ProcessId    = processId;
                    }
                }

                if (existingRecord != null)
                {
                    _currentRecord = existingRecord;
                    if (!_currentRecord.IsFocused)
                    {
                        _currentRecord.SetFocus(true);
                        ApplicationProcessingHelper.ProcessApplicationRecord(existingRecord);
                        UsageRecordUpdated?.Invoke(this, _currentRecord);
                        WindowChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                else
                {
                    _currentRecord = new AppUsageRecord
                    {
                        ProcessName = processName,
                        WindowTitle = windowTitle,
                        StartTime = DateTime.Now,
                        ProcessId = processId,
                        WindowHandle = foregroundWindow,
                        Date = EnsureValidDate(DateTime.Now.Date),
                        ApplicationName = processName
                    };
                    ApplicationProcessingHelper.ProcessApplicationRecord(_currentRecord);
                    _currentRecord.SetFocus(true);
                    _records.Add(_currentRecord);
                    UsageRecordUpdated?.Invoke(this, _currentRecord);
                    WindowChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }
} 
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using ScreenTimeTracker.Models;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using ScreenTimeTracker.Helpers;

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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

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
                if (foregroundWindow == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("CheckActiveWindow - foregroundWindow is Zero, returning");
                    return;
                }
                
                // Validate window handle before using it
                if (!IsWindow(foregroundWindow))
                {
                    System.Diagnostics.Debug.WriteLine("CheckActiveWindow - Invalid window handle detected");
                    return;
                }
                
                uint processId;
                GetWindowThreadProcessId(foregroundWindow, out processId);
                
                if (processId == 0)
                {
                    System.Diagnostics.Debug.WriteLine("CheckActiveWindow - processId is 0, invalid window");
                    return;
                }
                
                // Comment out this check - we want to track Screeny too
                // if (processId == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                // {
                //     System.Diagnostics.Debug.WriteLine("CheckActiveWindow - Skipping our own process");
                //     return;
                // }

                var windowTitle = GetActiveWindowTitle(foregroundWindow);
                var processName = GetProcessName(foregroundWindow);

                // Removed per-call window detection diagnostics.

                if (_currentRecord != null && 
                    (_currentRecord.WindowHandle != foregroundWindow || 
                     _currentRecord.ProcessId != (int)processId || 
                     _currentRecord.WindowTitle != windowTitle))
                {
                    // Unfocus log removed.
                    _currentRecord.SetFocus(false);
                    _currentRecord = null;
                }

                // IMPORTANT: Unfocus ALL records first to ensure only one is tracking
                foreach (var record in _records)
                {
                    if (record.IsFocused)
                    {
                        // Background unfocus log removed.
                        record.SetFocus(false);
                    }
                }

                // Prefer exact match (pid + title + handle). If not found, fall back to
                // any record with the same ProcessName to avoid duplicate rows when a
                // window title changes (e.g., Telegram chat switch)
                var existingRecord = _records.FirstOrDefault(r =>
                    r.ProcessId == (int)processId &&
                    r.WindowTitle == windowTitle &&
                    r.WindowHandle == foregroundWindow);

                if (existingRecord == null)
                {
                    existingRecord = _records.FirstOrDefault(r =>
                        r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));

                    if (existingRecord != null)
                    {
                        // Update handle/title so future exact-match checks succeed
                        existingRecord.WindowHandle = foregroundWindow;
                        existingRecord.WindowTitle  = windowTitle;
                        existingRecord.ProcessId    = (int)processId;
                    }
                }

                if (existingRecord != null)
                {
                    // Existing record found log removed.
                    _currentRecord = existingRecord;
                    if (!_currentRecord.IsFocused)
                    {
                        // Focus setting log removed.
                        _currentRecord.SetFocus(true);
                        
                        // Apply generic application processing rules (handles java-based games, helper processes, etc.)
                        ApplicationProcessingHelper.ProcessApplicationRecord(existingRecord);
                        
                        // Invoke events safely
                        try
                        {
                            UsageRecordUpdated?.Invoke(this, _currentRecord);
                            WindowChanged?.Invoke(this, EventArgs.Empty);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"ERROR invoking events: {ex.Message}");
                        }
                    }
                }
                else
                {
                    // Creating new record log removed.
                    _currentRecord = new AppUsageRecord
                    {
                        ProcessName = processName,
                        WindowTitle = windowTitle,
                        StartTime = DateTime.Now,
                        ProcessId = (int)processId,
                        WindowHandle = foregroundWindow,
                        Date = EnsureValidDate(DateTime.Now.Date),
                        ApplicationName = processName
                    };
                    
                    // Apply generic application processing rules before tracking/displaying
                    ApplicationProcessingHelper.ProcessApplicationRecord(_currentRecord);
                    
                    _currentRecord.SetFocus(true);
                    _records.Add(_currentRecord);
                    
                    // Event firing log removed.
                    
                    // Invoke events safely
                    try
                    {
                        UsageRecordUpdated?.Invoke(this, _currentRecord);
                        WindowChanged?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR invoking events: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR in CheckActiveWindow: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                
                // Don't rethrow - we want to continue tracking even if one check fails
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
    }
} 
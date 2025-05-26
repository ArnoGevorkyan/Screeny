using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using ScreenTimeTracker.Models;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;

namespace ScreenTimeTracker.Services
{
    public class WindowTrackingService : IDisposable
    {
        private readonly System.Timers.Timer _timer;
        private AppUsageRecord? _currentRecord;
        private readonly List<AppUsageRecord> _records;
        private bool _disposed;
        private readonly DatabaseService _databaseService;
        private readonly object _lockObject = new object();

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

                Debug.WriteLine("==== WindowTrackingService.StopTracking() - Stopping tracking (Normal) ====");
                _timer.Stop();
                IsTracking = false;
                if (_currentRecord != null)
                {
                    _currentRecord.SetFocus(false);
                    _currentRecord.EndTime = DateTime.Now;
                    _records.Add(_currentRecord);
                    _currentRecord = null;
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
                IsTracking = false;

                if (_currentRecord != null)
                {
                    Debug.WriteLine($"Suspend: Finalizing record for {_currentRecord.ProcessName}");
                    _currentRecord.SetFocus(false);
                    _currentRecord.EndTime = DateTime.Now;

                    try
                    {
                        Debug.WriteLine($"Suspend: Attempting immediate save for {_currentRecord.ProcessName} (Calculated Duration: {_currentRecord.Duration.TotalSeconds}s)");
                        
                        // Create a copy of the record to avoid thread issues
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
                        
                        // Save outside of lock to avoid deadlock
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
                    finally
                    {
                        _currentRecord = null;
                    }
                }
                else
                {
                    Debug.WriteLine("Suspend: No current record to finalize and save.");
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
                System.Diagnostics.Debug.WriteLine("Timer_Elapsed - Checking active window");
                CheckActiveWindow();
            }
            catch (Exception ex)
            {
                // Log the exception but don't let it crash the app
                System.Diagnostics.Debug.WriteLine($"ERROR in Timer_Elapsed: {ex.Message}");
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
                
                if (processId == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    System.Diagnostics.Debug.WriteLine("CheckActiveWindow - Skipping our own process");
                    return;
                }

                var windowTitle = GetActiveWindowTitle(foregroundWindow);
                var processName = GetProcessName(foregroundWindow);

                System.Diagnostics.Debug.WriteLine($"CheckActiveWindow - Detected window: {processName} ({processId}) - '{windowTitle}'");

                if (windowTitle.IndexOf("Screeny", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    System.Diagnostics.Debug.WriteLine("CheckActiveWindow - Ignoring our own window based on title");
                    return;
                }

                if (_currentRecord != null && 
                    (_currentRecord.WindowHandle != foregroundWindow || 
                     _currentRecord.ProcessId != (int)processId || 
                     _currentRecord.WindowTitle != windowTitle))
                {
                    System.Diagnostics.Debug.WriteLine($"Unfocusing previous window: {_currentRecord.ProcessName} - {_currentRecord.WindowTitle}");
                    _currentRecord.SetFocus(false);
                    _currentRecord = null;
                }

                var existingRecord = _records.FirstOrDefault(r => 
                    r.ProcessId == (int)processId && 
                    r.WindowTitle == windowTitle &&
                    r.WindowHandle == foregroundWindow);

                if (existingRecord != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Found existing record for: {processName} - {windowTitle}");
                    _currentRecord = existingRecord;
                    if (!_currentRecord.IsFocused)
                    {
                        System.Diagnostics.Debug.WriteLine($"Setting focus to existing record: {processName}");
                        _currentRecord.SetFocus(true);
                        
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
                    System.Diagnostics.Debug.WriteLine($"Creating new record for: {processName} - {windowTitle}");
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
                    
                    _currentRecord.SetFocus(true);
                    _records.Add(_currentRecord);
                    
                    System.Diagnostics.Debug.WriteLine($"Firing UsageRecordUpdated event for: {processName}");
                    
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
                    
                    System.Diagnostics.Debug.WriteLine($"Total records tracked: {_records.Count}");
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
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
            IsTracking = false;
            Debug.WriteLine("WindowTrackingService initialized.");
        }

        public void StartTracking()
        {
            ThrowIfDisposed();
            if (IsTracking) return;

            Debug.WriteLine("==== WindowTrackingService.StartTracking() - Starting tracking ====");
            _records.Clear();
            _timer.Start();
            IsTracking = true;
            CheckActiveWindow();
        }

        public void StopTracking()
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

        public void PauseTrackingForSuspend()
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
                    _databaseService.SaveRecord(_currentRecord);
                    Debug.WriteLine($"Suspend: Successfully saved record for {_currentRecord.ProcessName}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"CRITICAL ERROR: Failed to save record during suspend: {ex.Message}");
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

        public void ResumeTrackingAfterSuspend()
        {
            ThrowIfDisposed();
            if (IsTracking) return;

            Debug.WriteLine("==== WindowTrackingService.ResumeTrackingAfterSuspend() - Resuming tracking ====");
            IsTracking = true;
            _records.Clear();
            _timer.Start();
            CheckActiveWindow();
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (_disposed) return;
            System.Diagnostics.Debug.WriteLine("Timer_Elapsed - Checking active window");
            CheckActiveWindow();
        }

        private void CheckActiveWindow()
        {
            if (!IsTracking || _disposed)
            {
                return;
            }

            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("CheckActiveWindow - foregroundWindow is Zero, returning");
                return;
            }
            
            uint processId;
            GetWindowThreadProcessId(foregroundWindow, out processId);
            
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
                    UsageRecordUpdated?.Invoke(this, _currentRecord);
                    WindowChanged?.Invoke(this, EventArgs.Empty);
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
                UsageRecordUpdated?.Invoke(this, _currentRecord);
                WindowChanged?.Invoke(this, EventArgs.Empty);
                
                System.Diagnostics.Debug.WriteLine($"Total records tracked: {_records.Count}");
            }
        }

        private string GetActiveWindowTitle(IntPtr handle)
        {
            const int nChars = 256;
            StringBuilder buff = new StringBuilder(nChars);
            if (GetWindowText(handle, buff, nChars) > 0)
            {
                return buff.ToString();
            }
            return string.Empty;
        }

        private string GetProcessName(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return "Unknown";

            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            if (processId == 0)
                return "Unknown";

            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById((int)processId))
                {
                    return process.ProcessName;
                }
            }
            catch (Exception)
            {
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
            if (!_disposed)
            {
                Debug.WriteLine("Disposing WindowTrackingService...");
                _timer.Stop();
                _timer.Elapsed -= Timer_Elapsed;
                _timer.Dispose();
                IsTracking = false;
                _records.Clear();
                _currentRecord = null;
                _disposed = true;
                Debug.WriteLine("WindowTrackingService disposed.");
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
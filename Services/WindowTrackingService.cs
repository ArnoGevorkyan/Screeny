using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Services
{
    public class WindowTrackingService : IDisposable
    {
        private readonly System.Timers.Timer _timer;
        private AppUsageRecord? _currentRecord;
        private readonly List<AppUsageRecord> _records;
        private bool _disposed;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public event EventHandler<AppUsageRecord>? UsageRecordUpdated;

        public WindowTrackingService()
        {
            _records = new List<AppUsageRecord>();
            _timer = new System.Timers.Timer(100); // Poll more frequently
            _timer.Elapsed += Timer_Elapsed;
        }

        public void StartTracking()
        {
            ThrowIfDisposed();
            _timer.Start();
            // Force immediate check when starting
            CheckActiveWindow();
        }

        public void StopTracking()
        {
            ThrowIfDisposed();
            _timer.Stop();
            if (_currentRecord != null)
            {
                _currentRecord.SetFocus(false);
                _currentRecord.EndTime = DateTime.Now;
                _records.Add(_currentRecord);
                _currentRecord = null;
            }
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (_disposed) return;
            CheckActiveWindow();
        }

        private void CheckActiveWindow()
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
                return;
            
            // Retrieve the process ID
            uint processId;
            GetWindowThreadProcessId(foregroundWindow, out processId);
            
            // Skip tracking if the window belongs to our tracker process.
            if (processId == (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                return;

            // Get the active window title and process name.
            var windowTitle = GetActiveWindowTitle(foregroundWindow);
            var processName = GetProcessName(foregroundWindow);

            // Ignore windows whose title contains our app's name.
            if (windowTitle.IndexOf("Screen Time Tracker", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            // If we have a current record and it's different from the new window, unfocus it
            if (_currentRecord != null && 
                (_currentRecord.WindowHandle != foregroundWindow || 
                 _currentRecord.ProcessId != (int)processId || 
                 _currentRecord.WindowTitle != windowTitle))
            {
                _currentRecord.SetFocus(false);
                _currentRecord = null;
            }

            // Look for an existing record for this window
            var existingRecord = _records.FirstOrDefault(r => 
                r.ProcessId == (int)processId && 
                r.WindowTitle == windowTitle &&
                r.WindowHandle == foregroundWindow);

            if (existingRecord != null)
            {
                _currentRecord = existingRecord;
                if (!_currentRecord.IsFocused)
                {
                    _currentRecord.SetFocus(true);
                    UsageRecordUpdated?.Invoke(this, _currentRecord);
                }
            }
            else
            {
                // Create a new record for the new active window.
                _currentRecord = new AppUsageRecord
                {
                    ProcessName = processName,
                    WindowTitle = windowTitle,
                    StartTime = DateTime.Now,
                    ProcessId = (int)processId,
                    WindowHandle = foregroundWindow
                };
                
                _currentRecord.SetFocus(true);
                _records.Add(_currentRecord);
                UsageRecordUpdated?.Invoke(this, _currentRecord);
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
                StopTracking();
                _timer.Elapsed -= Timer_Elapsed;
                _timer.Dispose();
                _records.Clear();
                _disposed = true;
            }
        }
    }
} 
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
        private IntPtr _lastForegroundWindow;
        private string _lastWindowTitle = string.Empty;
        private string _lastProcessName = string.Empty;

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
            _lastForegroundWindow = IntPtr.Zero;
            _lastWindowTitle = string.Empty;
            _lastProcessName = string.Empty;
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (_disposed) return;
            CheckActiveWindow();
        }

        private void CheckActiveWindow()
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero) return;

            var windowTitle = GetActiveWindowTitle(foregroundWindow);
            var processName = GetProcessName(foregroundWindow);

            // Check if anything has changed
            bool hasChanged = foregroundWindow != _lastForegroundWindow ||
                            windowTitle != _lastWindowTitle ||
                            processName != _lastProcessName;

            // Update last known values
            _lastForegroundWindow = foregroundWindow;
            _lastWindowTitle = windowTitle;
            _lastProcessName = processName;

            if (hasChanged)
            {
                if (_currentRecord != null)
                {
                    _currentRecord.SetFocus(false);
                    _currentRecord.EndTime = DateTime.Now;
                    _records.Add(_currentRecord);
                }

                _currentRecord = new AppUsageRecord
                {
                    ProcessName = processName,
                    WindowTitle = windowTitle,
                    StartTime = DateTime.Now
                };

                _currentRecord.SetFocus(true);
                UsageRecordUpdated?.Invoke(this, _currentRecord);
            }
            else if (_currentRecord != null)
            {
                // Keep the current record focused
                _currentRecord.SetFocus(true);
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
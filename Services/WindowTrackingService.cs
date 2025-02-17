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
            _timer = new System.Timers.Timer(1000); // Poll every second
            _timer.Elapsed += Timer_Elapsed;
        }

        public void StartTracking()
        {
            ThrowIfDisposed();
            _timer.Start();
        }

        public void StopTracking()
        {
            ThrowIfDisposed();
            _timer.Stop();
            if (_currentRecord != null)
            {
                _currentRecord.EndTime = DateTime.Now;
                _records.Add(_currentRecord);
                _currentRecord = null;
            }
        }

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (_disposed) return;

            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero) return;

            var windowTitle = GetActiveWindowTitle(foregroundWindow);
            var processName = GetProcessName(foregroundWindow);

            if (_currentRecord == null || 
                _currentRecord.WindowTitle != windowTitle || 
                _currentRecord.ProcessName != processName)
            {
                if (_currentRecord != null)
                {
                    _currentRecord.EndTime = DateTime.Now;
                    _records.Add(_currentRecord);
                }

                _currentRecord = new AppUsageRecord
                {
                    ProcessName = processName,
                    WindowTitle = windowTitle,
                    StartTime = DateTime.Now
                };

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
            if (GetWindowThreadProcessId(handle, out processId) == 0)
                return "Unknown";

            try
            {
                var process = System.Diagnostics.Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch (ArgumentException)
            {
                return "Unknown";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "Unknown";
            }
            catch (InvalidOperationException)
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
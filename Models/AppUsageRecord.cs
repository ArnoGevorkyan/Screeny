using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace ScreenTimeTracker.Models
{
    public class AppUsageRecord : INotifyPropertyChanged
    {
        [DllImport("Shell32.dll")]
        private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, StringBuilder lpIconPath, out ushort lpiIcon);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_SMALLICON = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private static readonly HashSet<string> _ignoredProcesses = new()
        {
            "explorer",
            "SearchHost",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "devenv",
            "ApplicationFrameHost",
            "SystemSettings",
            "TextInputHost",
            "WindowsTerminal",
            "cmd",
            "powershell",
            "pwsh",
            "conhost",
            "WinStore.App",
            "LockApp",
            "LogonUI",
            "fontdrvhost",
            "dwm",
            "csrss",
            "services",
            "svchost",
            "taskhostw",
            "ctfmon",
            "rundll32",
            "dllhost",
            "sihost",
            "taskmgr",
            "SecurityHealthSystray",
            "SecurityHealthService",
            "Registry",
            "MicrosoftEdgeUpdate",
            "WmiPrvSE",
            "spoolsv",
            "TabTip",
            "TabTip32"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public int Id { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string WindowTitle { get; set; } = string.Empty;
        public IntPtr WindowHandle { get; set; }
        public bool IsFocused { get; set; }
        
        private DateTime _startTime;
        public DateTime StartTime
        {
            get => _startTime;
            set
            {
                if (_startTime != value)
                {
                    _startTime = value;
                    NotifyPropertyChanged();
                    NotifyPropertyChanged(nameof(Duration));
                    NotifyPropertyChanged(nameof(FormattedDuration));
                }
            }
        }

        private DateTime? _endTime;
        public DateTime? EndTime
        {
            get => _endTime;
            set
            {
                if (_endTime != value)
                {
                    _endTime = value;
                    NotifyPropertyChanged();
                    NotifyPropertyChanged(nameof(Duration));
                    NotifyPropertyChanged(nameof(FormattedDuration));
                }
            }
        }

        private TimeSpan _accumulatedDuration = TimeSpan.Zero;
        private DateTime _lastFocusTime;

        public TimeSpan Duration
        {
            get
            {
                var baseDuration = _accumulatedDuration;
                if (IsFocused)
                {
                    var currentTime = DateTime.Now;
                    var focusedDuration = currentTime - _lastFocusTime;
                    baseDuration += focusedDuration;
                    System.Diagnostics.Debug.WriteLine($"Duration for {ProcessName}: Accumulated={_accumulatedDuration.TotalSeconds:F1}s, Current={focusedDuration.TotalSeconds:F1}s, Total={baseDuration.TotalSeconds:F1}s");
                }
                return baseDuration;
            }
        }

        public string FormattedDuration
        {
            get
            {
                var duration = Duration;
                var hours = (int)duration.TotalHours;
                var minutes = duration.Minutes;
                var seconds = duration.Seconds;

                if (hours > 0)
                {
                    return $"{hours}h {minutes}m {seconds}s";
                }
                else if (minutes > 0)
                {
                    return $"{minutes}m {seconds}s";
                }
                else
                {
                    return $"{seconds}s";
                }
            }
        }

        public string FormattedStartTime => StartTime.ToString("HH:mm");

        public bool IsFromDate(DateTime date)
        {
            return StartTime.Date == date.Date;
        }

        public bool ShouldTrack => !_ignoredProcesses.Contains(ProcessName.ToLower());

        public void SetFocus(bool isFocused)
        {
            System.Diagnostics.Debug.WriteLine($"Setting focus for {ProcessName} to {isFocused}");
            if (IsFocused != isFocused)
            {
                if (isFocused)
                {
                    _lastFocusTime = DateTime.Now;
                    System.Diagnostics.Debug.WriteLine($"Focus started for {ProcessName} at {_lastFocusTime}");
                }
                else
                {
                    // Accumulate the time spent focused
                    var focusedDuration = DateTime.Now - _lastFocusTime;
                    _accumulatedDuration += focusedDuration;
                    System.Diagnostics.Debug.WriteLine($"Focus ended for {ProcessName}, accumulated {focusedDuration.TotalSeconds:F1}s, total: {_accumulatedDuration.TotalSeconds:F1}s");
                }
                
                IsFocused = isFocused;
                NotifyPropertyChanged(nameof(IsFocused));
                NotifyPropertyChanged(nameof(Duration));
                NotifyPropertyChanged(nameof(FormattedDuration));
            }
        }

        public void MergeWith(AppUsageRecord other)
        {
            if (other.EndTime.HasValue)
            {
                // Add the duration of the other record
                var otherDuration = other.EndTime.Value - other.StartTime;
                _accumulatedDuration += otherDuration;

                // Keep tracking from the latest point
                if (!EndTime.HasValue)
                {
                    StartTime = other.EndTime.Value;
                    _lastFocusTime = StartTime;
                }
                
                NotifyPropertyChanged(nameof(Duration));
                NotifyPropertyChanged(nameof(FormattedDuration));
            }
        }

        public void UpdateDuration()
        {
            if (IsFocused)
            {
                System.Diagnostics.Debug.WriteLine($"Updating duration for {ProcessName} (Focused: {IsFocused})");
                NotifyPropertyChanged(nameof(Duration));
                NotifyPropertyChanged(nameof(FormattedDuration));
            }
        }

        public static AppUsageRecord CreateAggregated(string processName, DateTime date)
        {
            return new AppUsageRecord
            {
                ProcessName = processName,
                StartTime = date.Date,
                EndTime = null,
                _accumulatedDuration = TimeSpan.Zero,
                _lastFocusTime = date.Date,
                IsFocused = false
            };
        }
    }
} 
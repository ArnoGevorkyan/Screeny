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

        private BitmapImage? _icon;
        private bool _isLoadingIcon;
        private Task? _loadIconTask;
        private readonly SemaphoreSlim _iconLoadingSemaphore = new SemaphoreSlim(1, 1);

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
                    // Try to load icon when the app gets focus
                    if (_icon == null && !IsLoadingIcon && _loadIconTask == null)
                    {
                        _loadIconTask = LoadIconAsync();
                    }
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

        public bool IsLoadingIcon
        {
            get => _isLoadingIcon;
            private set
            {
                if (_isLoadingIcon != value)
                {
                    _isLoadingIcon = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public BitmapImage? Icon
        {
            get
            {
                if (_icon == null && !IsLoadingIcon && _loadIconTask == null)
                {
                    _loadIconTask = LoadIconAsync();
                }
                return _icon;
            }
        }

        private async Task LoadIconAsync()
        {
            if (!ShouldTrack) return;
            
            try
            {
                await _iconLoadingSemaphore.WaitAsync();
                
                if (IsLoadingIcon || _icon != null) return;
                
                IsLoadingIcon = true;
                NotifyPropertyChanged(nameof(Icon));
                
                var processes = System.Diagnostics.Process.GetProcessesByName(ProcessName);
                if (processes.Length == 0) return;
                
                foreach (var process in processes)
                {
                    try
                    {
                        string? executablePath = null;
                        try
                        {
                            executablePath = process.MainModule?.FileName;
                        }
                        catch
                        {
                            continue;
                        }
                        
                        if (string.IsNullOrEmpty(executablePath)) continue;
                        
                        var iconPath = new StringBuilder(executablePath);
                        ushort iconIndex;
                        // try the P/Invoke extraction first
                        IntPtr hIcon = ExtractAssociatedIcon(IntPtr.Zero, iconPath, out iconIndex);

                        if (hIcon == IntPtr.Zero)
                        {
                            // FALLBACK: try using .NET's ExtractAssociatedIcon
                            System.Drawing.Icon? fallbackIcon = null;
                            try
                            {
                                fallbackIcon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
                            }
                            catch 
                            {
                                // if even the fallback fails, we will try SHGetFileInfo
                            }

                            if (fallbackIcon != null)
                            {
                                using (var icon = fallbackIcon)
                                {
                                    using (var bitmap = icon.ToBitmap())
                                    using (var stream = new MemoryStream())
                                    {
                                        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                                        stream.Position = 0;
                                        
                                        var image = new BitmapImage();
                                        image.DecodePixelWidth = 24; // Match the image size in XAML
                                        image.DecodePixelHeight = 24;
                                        await image.SetSourceAsync(stream.AsRandomAccessStream());
                                        _icon = image;
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                // NEW: try SHGetFileInfo as another fallback
                                SHFILEINFO shinfo = new SHFILEINFO();
                                IntPtr result = SHGetFileInfo(executablePath, 0, ref shinfo, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_SMALLICON);
                                if (shinfo.hIcon != IntPtr.Zero)
                                {
                                    using (var icon = System.Drawing.Icon.FromHandle(shinfo.hIcon))
                                    {
                                        using (var bitmap = icon.ToBitmap())
                                        using (var stream = new MemoryStream())
                                        {
                                            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                                            stream.Position = 0;
                                            
                                            var image = new BitmapImage();
                                            image.DecodePixelWidth = 24; // Match the image size in XAML
                                            image.DecodePixelHeight = 24;
                                            await image.SetSourceAsync(stream.AsRandomAccessStream());
                                            _icon = image;
                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    continue;
                                }
                            }
                        }
                        else
                        {
                            try
                            {
                                using (var icon = System.Drawing.Icon.FromHandle(hIcon))
                                {
                                    if (icon != null)
                                    {
                                        using (var bitmap = icon.ToBitmap())
                                        using (var stream = new MemoryStream())
                                        {
                                            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                                            stream.Position = 0;
                                            
                                            var image = new BitmapImage();
                                            image.DecodePixelWidth = 24; // Match the image size in XAML
                                            image.DecodePixelHeight = 24;
                                            await image.SetSourceAsync(stream.AsRandomAccessStream());
                                            _icon = image;
                                            break;
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                DestroyIcon(hIcon);
                            }
                        }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                _icon = null;
            }
            finally
            {
                IsLoadingIcon = false;
                _loadIconTask = null;
                NotifyPropertyChanged(nameof(Icon));
                _iconLoadingSemaphore.Release();
            }
        }
    }
} 
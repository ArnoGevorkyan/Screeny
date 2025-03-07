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
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

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
        
        private BitmapImage? _appIcon;
        public BitmapImage? AppIcon
        {
            get => _appIcon;
            private set
            {
                if (_appIcon != value)
                {
                    _appIcon = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private bool _isLoadingIcon = false;

        private void LoadIconAsync()
        {
            // Prevent multiple simultaneous attempts to load the icon
            if (_isLoadingIcon) return;
            _isLoadingIcon = true;

            // Start a background task to load the icon
            // But don't use Task.Run which creates a new thread
            _ = InternalLoadIconAsync();
        }

        private async Task InternalLoadIconAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Loading icon for {ProcessName}");
                
                // Try to get executable path
                string? exePath = GetExecutablePath();
                if (string.IsNullOrEmpty(exePath))
                {
                    System.Diagnostics.Debug.WriteLine($"Could not find executable path for {ProcessName}");
                    _isLoadingIcon = false;
                    return;
                }
                
                System.Diagnostics.Debug.WriteLine($"Found executable path: {exePath}");

                bool iconLoaded = false;
                
                // First try using SHGetFileInfo
                iconLoaded = await TryLoadIconWithSHGetFileInfo(exePath);
                
                // If that fails, try ExtractAssociatedIcon
                if (!iconLoaded)
                {
                    iconLoaded = await TryLoadIconWithExtractAssociatedIcon(exePath);
                }
                
                if (!iconLoaded)
                {
                    System.Diagnostics.Debug.WriteLine($"All icon loading methods failed for {ProcessName}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading app icon for {ProcessName}: {ex.Message}");
            }
            finally
            {
                _isLoadingIcon = false;
            }
        }
        
        private async Task<bool> TryLoadIconWithSHGetFileInfo(string exePath)
        {
            try
            {
                // Use SHGetFileInfo to get the icon
                SHFILEINFO shfi = new SHFILEINFO();
                IntPtr result = SHGetFileInfo(
                    exePath,
                    FILE_ATTRIBUTE_NORMAL,
                    ref shfi,
                    (uint)Marshal.SizeOf(shfi),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

                if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"SHGetFileInfo failed for {ProcessName}");
                    return false;
                }
                
                System.Diagnostics.Debug.WriteLine($"Successfully got icon handle for {ProcessName}");

                try
                {
                    // Convert the icon to a bitmap
                    using (Icon icon = Icon.FromHandle(shfi.hIcon))
                    {
                        System.Diagnostics.Debug.WriteLine($"Created Icon from handle for {ProcessName}");
                        using (Bitmap bitmap = icon.ToBitmap())
                        {
                            System.Diagnostics.Debug.WriteLine($"Created Bitmap from Icon for {ProcessName}");
                            
                            // Convert bitmap to BitmapImage for WinUI
                            BitmapImage? bitmapImage = await ConvertBitmapToBitmapImageAsync(bitmap);
                            if (bitmapImage == null)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to convert bitmap to BitmapImage for {ProcessName}");
                                return false;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"Successfully created BitmapImage for {ProcessName}");
                                AppIcon = bitmapImage;
                                System.Diagnostics.Debug.WriteLine($"Set AppIcon property for {ProcessName}");
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error converting icon for {ProcessName}: {ex.Message}");
                    return false;
                }
                finally
                {
                    // Always clean up the icon handle
                    bool destroyed = DestroyIcon(shfi.hIcon);
                    System.Diagnostics.Debug.WriteLine($"Icon handle destroyed: {destroyed}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in TryLoadIconWithSHGetFileInfo: {ex.Message}");
                return false;
            }
            
            return false;
        }
        
        private async Task<bool> TryLoadIconWithExtractAssociatedIcon(string exePath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Trying ExtractAssociatedIcon for {ProcessName}");
                StringBuilder sb = new StringBuilder(exePath);
                ushort iconIndex = 0;
                
                IntPtr iconHandle = ExtractAssociatedIcon(IntPtr.Zero, sb, out iconIndex);
                if (iconHandle == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"ExtractAssociatedIcon failed for {ProcessName}");
                    return false;
                }
                
                System.Diagnostics.Debug.WriteLine($"ExtractAssociatedIcon succeeded for {ProcessName}");
                
                try
                {
                    using (Icon icon = Icon.FromHandle(iconHandle))
                    using (Bitmap bitmap = icon.ToBitmap())
                    {
                        BitmapImage? bitmapImage = await ConvertBitmapToBitmapImageAsync(bitmap);
                        if (bitmapImage != null)
                        {
                            AppIcon = bitmapImage;
                            System.Diagnostics.Debug.WriteLine($"Successfully set icon using ExtractAssociatedIcon for {ProcessName}");
                            return true;
                        }
                    }
                }
                finally
                {
                    DestroyIcon(iconHandle);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in TryLoadIconWithExtractAssociatedIcon: {ex.Message}");
            }
            
            return false;
        }

        private async Task<BitmapImage?> ConvertBitmapToBitmapImageAsync(Bitmap bitmap)
        {
            try
            {
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    // Save bitmap to stream
                    bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                    memoryStream.Position = 0;

                    // Create a BitmapImage and set its source
                    var bitmapImage = new BitmapImage();
                    
                    // Use an InMemoryRandomAccessStream
                    using (var randomAccessStream = new InMemoryRandomAccessStream())
                    {
                        using (var writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
                        {
                            var bytes = memoryStream.ToArray();
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                            await writer.FlushAsync();
                        }
                        
                        randomAccessStream.Seek(0);
                        await bitmapImage.SetSourceAsync(randomAccessStream);
                    }
                    
                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error converting bitmap: {ex.Message}");
                return null;
            }
        }

        private string? GetExecutablePath()
        {
            try
            {
                // First try to find the running process by ID
                if (ProcessId > 0)
                {
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById(ProcessId);
                        if (process != null && !string.IsNullOrEmpty(process.MainModule?.FileName))
                        {
                            return process.MainModule.FileName;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Could not get process by ID {ProcessId}: {ex.Message}");
                    }
                }
                
                // Then try by name
                try
                {
                    var processes = System.Diagnostics.Process.GetProcessesByName(ProcessName);
                    if (processes.Length > 0 && !string.IsNullOrEmpty(processes[0].MainModule?.FileName))
                    {
                        return processes[0].MainModule.FileName;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not get process by name {ProcessName}: {ex.Message}");
                }
                
                // If we still don't have a path, try to infer it from common locations
                var extension = ".exe";
                if (!ProcessName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    var processWithExt = ProcessName + extension;
                    
                    // Try various common program directories
                    string[] commonLocations = {
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                    };
                    
                    // Try both with and without subdirectory
                    foreach (var location in commonLocations)
                    {
                        // Check direct path
                        var directPath = Path.Combine(location, processWithExt);
                        if (File.Exists(directPath))
                        {
                            return directPath;
                        }
                        
                        // Check in subdirectory matching the process name
                        var subDirPath = Path.Combine(location, ProcessName, processWithExt);
                        if (File.Exists(subDirPath))
                        {
                            return subDirPath;
                        }
                    }
                    
                    // As a fallback, just use any executable with this name (for icon purposes)
                    return processWithExt;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetExecutablePath: {ex.Message}");
                return null;
            }
        }
        
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

        public void LoadAppIconIfNeeded()
        {
            if (_appIcon == null && !_isLoadingIcon)
            {
                LoadIconAsync();
            }
        }
    }
} 
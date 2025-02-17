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

        private static readonly HashSet<string> _ignoredProcesses = new()
        {
            "explorer",
            "SearchHost",
            "ShellExperienceHost",
            "StartMenuExperienceHost"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public int Id { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string WindowTitle { get; set; } = string.Empty;
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
                if (!EndTime.HasValue && IsFocused)
                {
                    baseDuration += DateTime.Now - StartTime;
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
            if (IsFocused != isFocused)
            {
                if (isFocused)
                {
                    _lastFocusTime = DateTime.Now;
                }
                else if (IsFocused)
                {
                    // Accumulate the time spent focused
                    _accumulatedDuration += DateTime.Now - _lastFocusTime;
                }
                
                IsFocused = isFocused;
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
            if (!EndTime.HasValue && IsFocused)
            {
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
            try
            {
                await _iconLoadingSemaphore.WaitAsync();
                
                if (IsLoadingIcon || _icon != null) return;

                IsLoadingIcon = true;
                NotifyPropertyChanged(nameof(Icon));

                var process = System.Diagnostics.Process.GetProcessesByName(ProcessName).FirstOrDefault();
                if (process != null)
                {
                    string? executablePath = null;
                    try
                    {
                        executablePath = process.MainModule?.FileName;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // Access denied to process module, skip icon loading
                        return;
                    }

                    if (!string.IsNullOrEmpty(executablePath))
                    {
                        var iconPath = new StringBuilder(executablePath);
                        ushort iconIndex;
                        IntPtr hIcon = ExtractAssociatedIcon(IntPtr.Zero, iconPath, out iconIndex);

                        if (hIcon != IntPtr.Zero)
                        {
                            try
                            {
                                using (var icon = System.Drawing.Icon.FromHandle(hIcon))
                                using (var bitmap = icon.ToBitmap())
                                using (var stream = new MemoryStream())
                                {
                                    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                                    stream.Position = 0;

                                    var image = new BitmapImage();
                                    using (var randomAccessStream = stream.AsRandomAccessStream())
                                    {
                                        await image.SetSourceAsync(randomAccessStream);
                                        await randomAccessStream.FlushAsync();
                                    }
                                    _icon = image;
                                }
                            }
                            finally
                            {
                                DestroyIcon(hIcon);
                            }
                        }
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
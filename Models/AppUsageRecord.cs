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
using ScreenTimeTracker.Helpers;

namespace ScreenTimeTracker.Models
{
    public class AppUsageRecord : ScreenyObservableObject
    {
        public int Id { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string WindowTitle { get; set; } = string.Empty;
        public IntPtr WindowHandle { get; set; }
        public bool IsFocused { get; set; }
        public string ApplicationName { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public DateTime? LastUpdated { get; set; }
        
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

        internal TimeSpan _accumulatedDuration = TimeSpan.Zero;
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
                    
                    // Cap single session duration at 8 hours (reasonable maximum for continuous use)
                    if (focusedDuration.TotalHours > 8)
                    {
                        focusedDuration = TimeSpan.FromHours(8);
                    }
                    
                    baseDuration += focusedDuration;
                }
                
                // Final safety check - cap total duration at 16 hours (allowing for multiple sessions)
                if (baseDuration.TotalHours > 16)
                {
                    baseDuration = TimeSpan.FromHours(16);
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


        public void SetFocus(bool isFocused)
        {
            if (IsFocused != isFocused)
            {
             if (isFocused)
                {
                    _lastFocusTime = DateTime.Now;
                }
                else
                {
                    // Accumulate the time spent focused
                    var currentTime = DateTime.Now;
                    var focusedDuration = currentTime - _lastFocusTime;
                    
                    // Only accumulate if duration is reasonable (less than 1 day)
                    if (focusedDuration.TotalDays < 1 && focusedDuration.TotalSeconds > 0)
                    {
                        _accumulatedDuration += focusedDuration;
                    }
                }
                
                IsFocused = isFocused;
                NotifyPropertyChanged(nameof(IsFocused));
                NotifyPropertyChanged(nameof(Duration));
                NotifyPropertyChanged(nameof(FormattedDuration));
            }
        }

        /// <summary>
        /// Freezes live accumulation when the user is idle by anchoring <see cref="_lastFocusTime"/> to <paramref name="timestamp"/>.
        /// This prevents the Duration getter from adding idle seconds.
        /// </summary>
        /// <param name="timestamp">Current time when idle was detected.</param>
        internal void SetIdleAnchor(DateTime timestamp)
        {
            _lastFocusTime = timestamp;
            NotifyPropertyChanged(nameof(Duration));
            NotifyPropertyChanged(nameof(FormattedDuration));
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



        public static AppUsageRecord CreateAggregated(string processName, DateTime date)
        {
            // Create a new record for the given process name and date
            var record = new AppUsageRecord
            {
                ProcessName = processName,
                ApplicationName = processName, // Default to process name
                Date = date,
                // Use noon instead of midnight to ensure it's visible on charts
                // This only matters for aggregated views where exact time isn't shown
                StartTime = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0),
                _accumulatedDuration = TimeSpan.Zero
            };
            
            // Initialize icon loading in the background
            record.LoadAppIconIfNeeded();
            
            return record;
        }

        public async void LoadAppIconIfNeeded()
        {
            if (AppIcon != null || _loadingIcon) return;

            _loadingIcon = true;
            try
            {
                var icon = await ScreenTimeTracker.Services.IconLoader.Instance.GetIconAsync(this);
                if (icon != null)
                {
                    // Ensure assignment takes place on UI thread
                    if (!DispatcherHelper.EnqueueOnUIThread(() => AppIcon = icon))
                    {
                        // If we are already on UI thread or dispatcher not ready
                        AppIcon = icon;
                    }
                }
            }
            catch { /* ignore icon failures */ }
            finally
            {
                _loadingIcon = false;
            }
        }

        private bool _loadingIcon = false;

        public void ClearIcon()
        {
            AppIcon = null;
        }

        public void RaiseDurationChanged()
        {
            // Notify the UI that the duration-related properties have changed
            NotifyPropertyChanged(nameof(Duration));
            NotifyPropertyChanged(nameof(FormattedDuration));
        }
    }
} 
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
                    
                    // Check if this is a stale session from a previous day
                    if (_lastFocusTime.Date < currentTime.Date)
                    {
                        // If the session started on a previous day, cap it at end of that day
                        var endOfDay = _lastFocusTime.Date.AddDays(1).AddTicks(-1);
                        var focusedDuration = endOfDay - _lastFocusTime;
                        baseDuration += focusedDuration;
                        System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] WARNING: Stale session detected for {ProcessName}. Capping duration at end of day.");
                    }
                    else
                    {
                        // Normal calculation for same-day sessions
                        var focusedDuration = currentTime - _lastFocusTime;
                        
                        // Cap single session duration at 8 hours (reasonable maximum for continuous use)
                        if (focusedDuration.TotalHours > 8)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] WARNING: Session duration exceeds 8 hours for {ProcessName}. Capping at 8 hours.");
                            focusedDuration = TimeSpan.FromHours(8);
                        }
                        
                        baseDuration += focusedDuration;
                    }
                    
                    // ENHANCED LOGGING for double counting detection
                    System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] Duration getter for {ProcessName}:");
                    System.Diagnostics.Debug.WriteLine($"  - _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s");
                    System.Diagnostics.Debug.WriteLine($"  - _lastFocusTime: {_lastFocusTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"  - currentTime: {currentTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"  - calculated session: {(currentTime - _lastFocusTime).TotalSeconds:F2}s");
                    System.Diagnostics.Debug.WriteLine($"  - FINAL baseDuration: {baseDuration.TotalSeconds:F2}s");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] Duration getter for {ProcessName} (NOT FOCUSED): {baseDuration.TotalSeconds:F2}s");
                }
                
                // Final safety check - cap total duration at 16 hours (allowing for multiple sessions)
                if (baseDuration.TotalHours > 16)
                {
                    System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] WARNING: Total duration exceeds 16 hours for {ProcessName}. Capping at 16 hours.");
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

        public bool ShouldTrack => !ProcessFilter.IgnoredProcesses.Contains(ProcessName.ToLower()) && !IsTemporaryProcess(ProcessName);

        /// <summary>
        /// Checks if a process name appears to be a temporary file or installer
        /// </summary>
        private static bool IsTemporaryProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName))
                return false;

            // Convert to lowercase for comparison
            string lowerName = processName.ToLower();

            // Check for patterns that indicate temporary processes
            return
                // Random hex/numeric names (like "1747797458F8MB2zabRaze...")
                (lowerName.Length > 15 && System.Text.RegularExpressions.Regex.IsMatch(lowerName, @"^[0-9a-f]{8,}")) ||
                
                // Files ending with .tmp
                lowerName.EndsWith(".tmp") ||
                
                // Process names starting with numbers and containing random characters
                (lowerName.Length > 20 && System.Text.RegularExpressions.Regex.IsMatch(lowerName, @"^[0-9]{5,}.*[a-z0-9]{5,}")) ||
                
                // Setup/installer temporary files
                (lowerName.Contains("setup") && lowerName.Contains(".tmp")) ||
                (lowerName.Contains("install") && lowerName.Length > 25);
        }

        public void SetFocus(bool isFocused)
        {
            System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] SetFocus called for {ProcessName}: {IsFocused} -> {isFocused}");
            if (IsFocused != isFocused)
            {
             if (isFocused)
                {
                    _lastFocusTime = DateTime.Now;
                    System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] Focus started for {ProcessName} at {_lastFocusTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] Current _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s");
                }
                else
                {
                    // Accumulate the time spent focused
                    var currentTime = DateTime.Now;
                    var focusedDuration = currentTime - _lastFocusTime;
                    
                    System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] Focus ending for {ProcessName}:");
                    System.Diagnostics.Debug.WriteLine($"  - _lastFocusTime: {_lastFocusTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"  - currentTime: {currentTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"  - calculated focusedDuration: {focusedDuration.TotalSeconds:F2}s");
                    System.Diagnostics.Debug.WriteLine($"  - OLD _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s");
                    
                    // Only accumulate if duration is reasonable (less than 1 day)
                    if (focusedDuration.TotalDays < 1 && focusedDuration.TotalSeconds > 0)
                    {
                        _accumulatedDuration += focusedDuration;
                        System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] NEW _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] WARNING: Ignoring unreasonable focus duration for {ProcessName}: {focusedDuration.TotalDays} days, {focusedDuration.TotalSeconds:F2}s");
                    }
                }
                
                IsFocused = isFocused;
                NotifyPropertyChanged(nameof(IsFocused));
                NotifyPropertyChanged(nameof(Duration));
                NotifyPropertyChanged(nameof(FormattedDuration));
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] SetFocus for {ProcessName}: No change (already {isFocused})");
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

        /// <summary>
        /// Explicitly increments the duration of this record by a given interval.
        /// Used for real-time updates in historical views.
        /// </summary>
        /// <param name="interval">The time interval to add.</param>
        public void IncrementDuration(TimeSpan interval)
        {
            var oldAccumulated = _accumulatedDuration;
            var oldLastFocusTime = _lastFocusTime;
            var currentTime = DateTime.Now;
            
            System.Diagnostics.Debug.WriteLine($"[INCREMENT_LOG] IncrementDuration called for {ProcessName}:");
            System.Diagnostics.Debug.WriteLine($"  - IsFocused: {IsFocused}");
            System.Diagnostics.Debug.WriteLine($"  - Interval to add: {interval.TotalSeconds:F2}s");
            System.Diagnostics.Debug.WriteLine($"  - OLD _accumulatedDuration: {oldAccumulated.TotalSeconds:F2}s");
            System.Diagnostics.Debug.WriteLine($"  - OLD _lastFocusTime: {oldLastFocusTime:HH:mm:ss.fff}");
            System.Diagnostics.Debug.WriteLine($"  - Current time: {currentTime:HH:mm:ss.fff}");
            
            if (IsFocused)
            {
                var timeSinceLastFocus = currentTime - oldLastFocusTime;
                System.Diagnostics.Debug.WriteLine($"  - Time since last focus: {timeSinceLastFocus.TotalSeconds:F2}s");
                System.Diagnostics.Debug.WriteLine($"  - *** POTENTIAL DOUBLE COUNT: Adding {interval.TotalSeconds:F2}s but {timeSinceLastFocus.TotalSeconds:F2}s already calculated in Duration getter ***");
            }
            
            // For unfocused records we add directly to accumulated.
            // For the currently focused record we do **not** move _lastFocusTime; that timestamp
            // is the anchor the Duration getter uses to measure the live-elapsed span.
            if (!IsFocused)
            {
                _accumulatedDuration += interval;
                System.Diagnostics.Debug.WriteLine($"  - NEW _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s (incremented – record not focused)");

                // Advance _lastFocusTime so that when the record becomes focused again,
                // the live span starts from this point and we don't double-count.
                _lastFocusTime = DateTime.Now;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"  - SKIPPING accumulated increment; record is focused and live span will be derived from _lastFocusTime");

                // Do NOT touch _lastFocusTime here – it acts as the fixed anchor for the live
                // elapsed span while this record remains focused.  Moving it every tick would
                // reset the anchor and make Duration appear stalled.
            }
            
            // Update the anchor timestamp ONLY when the record is not focused.
            // This prevents the live Duration from stalling at ~0 seconds.
            if (!IsFocused)
            {
                _lastFocusTime = DateTime.Now;
            }
            System.Diagnostics.Debug.WriteLine($"  - NEW _lastFocusTime: {_lastFocusTime:HH:mm:ss.fff}");
            
            NotifyPropertyChanged(nameof(Duration));
            NotifyPropertyChanged(nameof(FormattedDuration));
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
            
            System.Diagnostics.Debug.WriteLine($"Created aggregated record for {processName} with date {date:yyyy-MM-dd} and start time {record.StartTime}");
            
            return record;
        }

        public async void LoadAppIconIfNeeded()
                                        {
            if (AppIcon != null) return;

            try
            {
                var icon = await ScreenTimeTracker.Services.IconLoader.Instance.GetIconAsync(this);
                if (icon != null)
                    {
                    AppIcon = icon;
                }
            }
            catch { /* ignore icon failures */ }
        }

        public void ClearIcon()
        {
            AppIcon = null;
        }
    }
} 
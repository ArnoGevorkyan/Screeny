using ScreenTimeTracker.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Linq;
using System;
using ScreenTimeTracker.Helpers;
using System.Collections.Generic;
using ScreenTimeTracker.Services;

namespace ScreenTimeTracker
{
    // This partial class will eventually contain all UI event handlers and helper methods that directly
    // manipulate XAML elements. It is empty for now so we can migrate code incrementally without breaking
    // compilation.
    public sealed partial class MainWindow
    {
        // ---------------- Chart update helper ----------------
        private void UpdateUsageChart(AppUsageRecord? liveFocusedRecord = null)
        {
            // Safety checks
            if (UsageChartLive == null || _usageRecords == null) return;

            var totalTime = Helpers.ChartHelper.UpdateUsageChart(
                UsageChartLive,
                _usageRecords,
                _currentChartViewMode,
                _currentTimePeriod,
                _selectedDate,
                _selectedEndDate,
                liveFocusedRecord);

            // Update UI text block with formatted total time if available
            if (ChartTimeValue != null)
            {
                ChartTimeValue.Text = Helpers.ChartHelper.FormatTimeSpan(totalTime);
            }
        }

        // ---------------- Restored helper methods ----------------
        private void CleanupSystemProcesses()
        {
            // Simplified cleanup: keep only non-system processes or duration >=10s
            if (_usageRecords == null) return;
            var toRemove = _usageRecords.Where(r => IsWindowsSystemProcess(r.ProcessName) && r.Duration.TotalSeconds < 10).ToList();
            foreach (var rec in toRemove) _usageRecords.Remove(rec);
        }

        private void UpdateAveragePanel(List<AppUsageRecord> aggregatedRecords, DateTime startDate, DateTime endDate)
        {
            if (AveragePanel == null || DailyAverage == null) return;

            int dayCount = (endDate.Date - startDate.Date).Days + 1;
            if (dayCount <= 0) dayCount = 1;

            // Sum total duration across all aggregated records.
            TimeSpan total = TimeSpan.Zero;
            foreach (var rec in aggregatedRecords)
                total += rec.Duration;

            // Determine how many days actually have usage
            int activeDayCount = _usageRecords
                                 .Where(r => r.Duration.TotalSeconds > 0)
                                 .Select(r => r.StartTime.Date)
                                 .Distinct()
                                 .Count();
            if (activeDayCount <= 0) activeDayCount = 1; // safeguard

            var avg = TimeSpan.FromSeconds(total.TotalSeconds / activeDayCount);

            DailyAverage.Text = ChartHelper.FormatTimeSpan(avg);

            // Always show average panel for multi-day views
            AveragePanel.Visibility = Visibility.Visible;
        }

        private void UpdateViewModeAndChartForDateRange(DateTime startDate, DateTime endDate, List<AppUsageRecord> aggregatedRecords)
        {
            // Minimal version: force weekly period & daily chart view then refresh
            _currentTimePeriod    = TimePeriod.Weekly;
            _currentChartViewMode = ChartViewMode.Daily;
            UpdateUsageChart();
            UpdateSummaryTab(aggregatedRecords);
        }

        private void ShowNoDataDialog(DateTime startDate, DateTime endDate)
        {
            if (Content == null) return;
            var dlg = new ContentDialog
            {
                Title = "No Data Available",
                Content = $"No usage data found for the selected date range ({startDate:MMM d} - {endDate:MMM d}).",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            _ = dlg.ShowAsync();
        }

        private void ShowErrorDialog(string message)
        {
            if (Content == null) return;
            var dlg = new ContentDialog
            {
                Title = "Error",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            _ = dlg.ShowAsync();
        }

        // ---------------- AppWindow helpers ----------------
        private Microsoft.UI.Windowing.AppWindow GetAppWindowForCurrentWindow()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var wndId   = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(wndId);
        }

        private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            // Hide instead of close
            args.Cancel = true;
            sender.Hide();
        }

        private void Window_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
        {
            // Delegate to Dispose logic already in Logic partial
            Dispose();
        }

        // ---------------- Additional UI helpers migrated from MainWindow.xaml.cs ----------------
        private void UpdateChartViewMode()
        {
            var today = DateTime.Today;
            var yesterday = today.AddDays(-1);

            bool isLast7Days = _isDateRangeSelected && _selectedDate == today.AddDays(-6) && _selectedEndDate == today;
            bool isCustomRange = _isDateRangeSelected && _currentTimePeriod == TimePeriod.Custom;

            if ((_selectedDate == today || _selectedDate == yesterday) && !_isDateRangeSelected)
            {
                _currentChartViewMode = ChartViewMode.Hourly;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ViewModeLabel != null)
                        ViewModeLabel.Text = "Hourly View";

                    if (ViewModePanel != null)
                        ViewModePanel.Visibility = Visibility.Collapsed;
                });
            }
            else if (isLast7Days)
            {
                _currentChartViewMode = ChartViewMode.Daily;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ViewModeLabel != null)
                        ViewModeLabel.Text = "Daily View";

                    if (ViewModePanel != null)
                        ViewModePanel.Visibility = Visibility.Collapsed;
                });
            }
            else if (isCustomRange)
            {
                _currentChartViewMode = ChartViewMode.Daily;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ViewModeLabel != null)
                        ViewModeLabel.Text = "Daily View";

                    if (ViewModePanel != null)
                        ViewModePanel.Visibility = Visibility.Collapsed;
                });
            }
            else
            {
                if (_currentTimePeriod == TimePeriod.Daily)
                {
                    _currentChartViewMode = ChartViewMode.Hourly;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ViewModeLabel != null)
                            ViewModeLabel.Text = "Hourly View";

                        if (ViewModePanel != null)
                            ViewModePanel.Visibility = Visibility.Visible;
                    });
                }
                else
                {
                    _currentChartViewMode = ChartViewMode.Daily;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ViewModeLabel != null)
                            ViewModeLabel.Text = "Daily View";

                        if (ViewModePanel != null)
                            ViewModePanel.Visibility = Visibility.Visible;
                    });
                }
            }

            if (ViewModePanel != null)
                ViewModePanel.Visibility = Visibility.Collapsed;

            UpdateUsageChart();
        }

        // Forces the LiveCharts control to refresh completely
        private void ForceChartRefresh()
        {
            if (UsageChartLive == null) return;

            var totalTime = ChartHelper.ForceChartRefresh(
                UsageChartLive,
                _usageRecords,
                _currentChartViewMode,
                _currentTimePeriod,
                _selectedDate,
                _selectedEndDate);

            if (ChartTimeValue != null)
                ChartTimeValue.Text = ChartHelper.FormatTimeSpan(totalTime);
        }

        private void UpdateDatePickerButtonText()
        {
            try
            {
                if (DatePickerButton == null) return;

                var today = DateTime.Today;

                if (_selectedDate == today && !_isDateRangeSelected)
                {
                    DatePickerButton.Content = "Today";
                    return;
                }

                if (_selectedDate == today.AddDays(-1) && !_isDateRangeSelected)
                {
                    DatePickerButton.Content = "Yesterday";
                    return;
                }

                if (_selectedDate == today && _isDateRangeSelected && _currentTimePeriod == TimePeriod.Weekly)
                {
                    DatePickerButton.Content = "Last 7 days";
                    return;
                }

                if (_selectedDate == today.AddDays(-29) && _isDateRangeSelected && _currentTimePeriod == TimePeriod.Custom)
                {
                    DatePickerButton.Content = "Last 30 days";
                    return;
                }

                if (_selectedDate == new DateTime(today.Year, today.Month, 1) && _isDateRangeSelected && _currentTimePeriod == TimePeriod.Custom)
                {
                    DatePickerButton.Content = "This month";
                    return;
                }

                if (!_isDateRangeSelected)
                {
                    DatePickerButton.Content = _selectedDate.ToString("MMM dd");
                }
                else if (_selectedEndDate.HasValue)
                {
                    DatePickerButton.Content = $"{_selectedDate:MMM dd} - {_selectedEndDate:MMM dd}";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateDatePickerButtonText: {ex.Message}");
                if (DatePickerButton != null)
                    DatePickerButton.Content = _selectedDate.ToString("MMM dd");
            }
        }

        private void LoadRecordsForLastSevenDays()
        {
            try
            {
                DateTime today = DateTime.Today;
                DateTime startDate = today.AddDays(-6);

                var weekRecords = _aggregationService.GetAggregatedRecordsForDateRange(startDate, today);

                UpdateRecordListView(weekRecords);
                SetTimeFrameHeader($"Last 7 Days ({startDate:MMM d} - {today:MMM d}, {today.Year})");

                if (weekRecords.Any())
                {
                    double totalHours = weekRecords.Sum(r => r.Duration.TotalHours);
                    double dailyAverage = totalHours / 7.0;
                    System.Diagnostics.Debug.WriteLine($"Daily average: {dailyAverage:F1} h");
                }

                UpdateChartWithRecords(weekRecords);

                for (var d = startDate; d <= today; d = d.AddDays(1))
                    LoadRecordsForSpecificDay(d, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in LoadRecordsForLastSevenDays: {ex.Message}");
            }
        }

        private void UpdateRecordListView(List<AppUsageRecord> records)
        {
            try
            {
                if (_usageRecords == null) return;
                _usageRecords.Clear();

                foreach (var r in records.OrderByDescending(r => r.Duration))
                    _usageRecords.Add(r);

                if (!_disposed && UsageListView != null)
                {
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (_disposed || UsageListView == null) return;
                        // ItemsSource binding handles updates automatically
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateRecordListView: {ex.Message}");
            }
        }

        private void UpdateChartWithRecords(List<AppUsageRecord> records)
        {
            try
            {
                _currentTimePeriod = TimePeriod.Weekly;
                _currentChartViewMode = ChartViewMode.Daily;

                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_disposed) return;

                    if (ViewModeLabel != null)
                        ViewModeLabel.Text = "Daily View";

                    if (ViewModePanel != null)
                        ViewModePanel.Visibility = Visibility.Collapsed;

                    UpdateUsageChart();
                    UpdateSummaryTab(records);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateChartWithRecords: {ex.Message}");
            }
        }

        private void SetTimeFrameHeader(string headerText)
        {
            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_disposed) return;

                    if (DateDisplay != null)
                    {
                        DateDisplay.Text = headerText;
                        System.Diagnostics.Debug.WriteLine($"DateDisplay updated to: {headerText}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in SetTimeFrameHeader: {ex.Message}");
            }
        }

        private void UpdateSummaryTab()
        {
            UpdateSummaryTab(_usageRecords.ToList());
        }

        private void UpdateSummaryTab(List<AppUsageRecord> recordsToSummarize)
        {
            try
            {
                // Calculate total screen time without double-counting overlapping focus intervals
                TimeSpan totalTime = TimeSpan.Zero;
                TimeSpan absoluteMaxDuration = TimeSpan.Zero;

                try
                {
                    var now = DateTime.Now;

                    // Build interval list for every record
                    var intervals = recordsToSummarize.Select(r =>
                    {
                        DateTime start = r.StartTime;
                        DateTime end;

                        if (r.EndTime.HasValue)
                        {
                            end = r.EndTime.Value;
                        }
                        else
                        {
                            // Live or aggregated record without explicit end -> derive from Duration or current time
                            end = r.IsFocused ? now : r.StartTime + r.Duration;
                        }

                        // Guard against corrupt data
                        if (end < start) end = start;

                        return (Start: start, End: end);
                    }).ToList();

                    // Merge overlapping slices and sum their lengths
                    var merged = ChartHelper.MergeIntervals(intervals);
                    totalTime = merged.Aggregate(TimeSpan.Zero, (span, iv) => span + (iv.End - iv.Start));

                // Cap to a realistic maximum: 24 h per day * days in period
                int totalMaxDays = GetDayCountForTimePeriod(_currentTimePeriod, _selectedDate);
                    absoluteMaxDuration = TimeSpan.FromHours(24 * totalMaxDays);
                if (totalTime > absoluteMaxDuration)
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Capping total time from {totalTime.TotalHours:F1}h to {absoluteMaxDuration.TotalHours:F1}h");
                    totalTime = absoluteMaxDuration;
                }

                // Update summary UI – total screen time block
                if (TotalScreenTime != null)
                {
                    TotalScreenTime.Text = FormatTimeSpan(totalTime);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error computing merged totalTime: {ex.Message}");
                }

                // Determine most-used application within the supplied list
                AppUsageRecord? mostUsedApp = null;
                foreach (var record in recordsToSummarize)
                {
                    var capped = record.Duration > absoluteMaxDuration ? absoluteMaxDuration : record.Duration;
                    if (mostUsedApp == null || capped > mostUsedApp.Duration)
                        mostUsedApp = record;
                }

                if (mostUsedApp != null)
                {
                    if (MostUsedApp != null)       MostUsedApp.Text  = mostUsedApp.ProcessName;
                    if (MostUsedAppTime != null)   MostUsedAppTime.Text = FormatTimeSpan(mostUsedApp.Duration);

                    // Ensure icon is loaded (deferred)
                    mostUsedApp.LoadAppIconIfNeeded();

                    if (MostUsedAppIcon != null && MostUsedPlaceholderIcon != null)
                    {
                        if (mostUsedApp.AppIcon != null)
                        {
                            MostUsedAppIcon.Source = mostUsedApp.AppIcon;
                            MostUsedAppIcon.Visibility = Visibility.Visible;
                            MostUsedPlaceholderIcon.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            MostUsedAppIcon.Visibility = Visibility.Collapsed;
                            MostUsedPlaceholderIcon.Visibility = Visibility.Visible;
                        }
                    }
                }
                else
                {
                    if (MostUsedApp != null)       MostUsedApp.Text = "None";
                    if (MostUsedAppTime != null)   MostUsedAppTime.Text = FormatTimeSpan(TimeSpan.Zero);
                    if (MostUsedAppIcon != null && MostUsedPlaceholderIcon != null)
                    {
                        MostUsedAppIcon.Visibility = Visibility.Collapsed;
                        MostUsedPlaceholderIcon.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating summary tab: {ex.Message}");
            }
        }

        private string FormatTimeSpan(TimeSpan time) => ChartHelper.FormatTimeSpan(time);

        // ---------------- TitleBar & DatePicker button handlers ----------------
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => _windowHelper.MinimizeWindow();
        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => _windowHelper.MaximizeOrRestoreWindow();
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _trackingService.StopTracking();
            _windowHelper.CloseWindow();
        }

        private void DatePickerButton_Click(object sender, RoutedEventArgs e)
        {
            _datePickerPopup?.ShowDatePicker(DatePickerButton, _selectedDate, _selectedEndDate, _isDateRangeSelected);
        }

        // ---------------- UI refresh & tracking events migrated from XAML partial ----------------
        private void SetUpUiElements()
        {
            // Initialize the date button text and timers
            UpdateDatePickerButtonText();

            // Timers were configured in constructor; ensure delegates attached
            _updateTimer.Tick += UpdateTimer_Tick;
            _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        }

        private void UpdateTimer_Tick(object? sender, object e)
        {
            try
            {
                if (_disposed || _usageRecords == null) return;
                if (_trackingService == null || !_trackingService.IsTracking) return;

                // -------------------------------------------------------------
                // Guard: only apply live updates if the active view includes today
                // -------------------------------------------------------------
                bool viewIncludesToday;
                if (_isDateRangeSelected)
                {
                    if (_selectedEndDate == null)
                    {
                        viewIncludesToday = false;
                    }
                    else
                    {
                        viewIncludesToday = _selectedDate.Date <= DateTime.Today && _selectedEndDate.Value.Date >= DateTime.Today;
                    }
                }
                else
                {
                    viewIncludesToday = _selectedDate.Date == DateTime.Today;
                }

                if (!viewIncludesToday)
                    return; // Ignore live tracking updates for historic views

                // Increment duration only for focused record to minimize UI churn
                var activeRec = _usageRecords.FirstOrDefault(r => r.IsFocused);
                activeRec?.RaiseDurationChanged();

                // If the focused record still lacks icon, retry fetch (may succeed once window sets its icon)
                if (activeRec != null && activeRec.AppIcon == null)
                {
                    activeRec.LoadAppIconIfNeeded();
                }

                // Update total time using unified helper (avoids code duplication & flicker)
                try
                {
                    var liveTotal = ChartHelper.CalculateUniqueTotalTime(_usageRecords);

                    if (ChartTimeValue != null) ChartTimeValue.Text = ChartHelper.FormatTimeSpan(liveTotal);
                }
                catch { /* swallow race conditions */ }

                // Throttle chart refresh scheduling: no change but we'll modify ChartRefreshTimer_Tick.
                _chartStaleSeconds++;
                if (_chartStaleSeconds >= 15)
                {
                    _isChartDirty     = true;
                    _chartStaleSeconds = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateTimer_Tick: {ex.Message}");
            }
        }

        private void TrackingService_UsageRecordUpdated(object? sender, AppUsageRecord record)
        {
            if (_disposed) return;
            DispatcherQueue?.TryEnqueue(() =>
            {
                try
                {
                    // -------------------------------------------------------------
                    // Guard: only apply live updates if the active view includes today
                    // -------------------------------------------------------------
                    bool viewIncludesToday;
                    if (_isDateRangeSelected)
                    {
                        if (_selectedEndDate == null)
                        {
                            viewIncludesToday = false;
                        }
                        else
                        {
                            viewIncludesToday = _selectedDate.Date <= DateTime.Today && _selectedEndDate.Value.Date >= DateTime.Today;
                        }
                    }
                    else
                    {
                        viewIncludesToday = _selectedDate.Date == DateTime.Today;
                    }

                    if (!viewIncludesToday)
                        return; // Ignore live tracking updates for historic views

                    // Skip unwanted/system processes.
                    if (ScreenTimeTracker.Models.ProcessFilter.IgnoredProcesses.Contains(record.ProcessName))
                        return;

                    // Replace or merge by process name so the UI shows only one row per app.
                    var existing = _usageRecords.FirstOrDefault(r => r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase));

                    if (existing == null)
                    {
                        // First time we encounter this app – add directly.
                        _usageRecords.Add(record);
                        existing = record;

                        existing.LoadAppIconIfNeeded();
                    }
                    else if (!ReferenceEquals(existing, record))
                    {
                        // Same app already present – merge runtime info and avoid duplicates.
                        existing.MergeWith(record);

                        // Sync focus / window metadata so subsequent updates hit the same object.
                        existing.WindowHandle = record.WindowHandle;
                        existing.WindowTitle  = record.WindowTitle;
                        existing.ProcessId    = record.ProcessId;

                        if (record.IsFocused)
                            existing.SetFocus(true);

                        existing.RaiseDurationChanged();

                        // Ensure icon requested once we know canonical record
                        existing.LoadAppIconIfNeeded();
                    }
                    else
                    {
                        // Reference already in list – just refresh its duration.
                        existing.RaiseDurationChanged();
                    }

                    // Purge any leftover duplicates (same ProcessName but different reference)
                    var dupes = _usageRecords.Where(r => r.ProcessName.Equals(existing.ProcessName, StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(r, existing)).ToList();
                    foreach (var d in dupes) _usageRecords.Remove(d);

                    // Mark chart for deferred refresh
                    _isChartDirty = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error in UsageRecordUpdated handler: {ex.Message}");
                }
            });
        }

        private void TrackingService_WindowChanged(object? sender, EventArgs e)
        {
            if (_disposed) return;
            DispatcherQueue?.TryEnqueue(() =>
            {
                try
                {
                    // -------------------------------------------------------------
                    // Guard: only apply live updates if the active view includes today
                    // -------------------------------------------------------------
                    bool viewIncludesToday;
                    if (_isDateRangeSelected)
                    {
                        if (_selectedEndDate == null)
                        {
                            viewIncludesToday = false;
                        }
                        else
                        {
                            viewIncludesToday = _selectedDate.Date <= DateTime.Today && _selectedEndDate.Value.Date >= DateTime.Today;
                        }
                    }
                    else
                    {
                        viewIncludesToday = _selectedDate.Date == DateTime.Today;
                    }

                    if (!viewIncludesToday)
                        return; // Ignore live tracking updates for historic views

                    // Clear focus flags then set on the current record
                    foreach (var rec in _usageRecords) rec.SetFocus(false);
                    var current = _trackingService?.CurrentRecord;
                    if (current != null)
                    {
                        var uiRec = _usageRecords.FirstOrDefault(r => r.ProcessName.Equals(current.ProcessName, StringComparison.OrdinalIgnoreCase));
                        uiRec?.SetFocus(true);
                    }
                    _isChartDirty = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error in WindowChanged handler: {ex.Message}");
                }
            });
        }
    }
} 
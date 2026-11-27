using ScreenTimeTracker.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Linq;
using System;
using ScreenTimeTracker.Helpers;
using System.Collections.Generic;
using ScreenTimeTracker.Services;
using System.Collections.ObjectModel;

namespace ScreenTimeTracker
{
    // This partial class will eventually contain all UI event handlers and helper methods that directly
    // manipulate XAML elements. It is empty for now so we can migrate code incrementally without breaking
    // compilation.
    public sealed partial class MainWindow
    {
        private volatile int _usageViewRevision;
        private bool _chartRefreshPending;
        private bool _chartRefreshRequested;
        private (DateTime Date, List<AppUsageRecord> Records)? _loadedHourlyChart;

        private int InvalidateUsageView()
        {
            _loadedHourlyChart = null;
            _liveTotalRead = null;
            _cachedTotalTime = TimeSpan.Zero;
            _lastFullRefresh = DateTime.MinValue;
            return ++_usageViewRevision;
        }

        private bool IsCurrentUsageView(int revision) => !_disposed && _usageViewRevision == revision;

        private void SetUsageViewLoading(bool loading)
        {
            _isReloading = loading;
            if (LoadingIndicator != null)
                LoadingIndicator.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- Chart update helper ----------------
        private void UpdateUsageChart(List<AppUsageRecord>? hourlyRecords = null)
        {
            // Safety checks
            if (UsageChartLive == null || _usageRecords == null) return;

            var date = _viewModel.SelectedDate.Date;
            if (hourlyRecords != null)
                _loadedHourlyChart = (date, hourlyRecords);

            ICollection<AppUsageRecord> chartRecords = _usageRecords;
            if (_viewModel.CurrentChartViewMode == ChartViewMode.Hourly)
                chartRecords = _loadedHourlyChart is { } loaded && loaded.Date == date
                    ? loaded.Records
                    : Array.Empty<AppUsageRecord>();

            var totalTime = Helpers.ChartHelper.UpdateUsageChart(
                UsageChartLive,
                chartRecords,
                _viewModel.CurrentChartViewMode,
                _viewModel.CurrentTimePeriod,
                _viewModel.SelectedDate,
                _viewModel.SelectedEndDate);

            _liveTotalRead = null;
            _cachedTotalTime = totalTime;

            // Update UI text block with formatted total time if available
            if (ChartTimeValue != null)
            {
                ChartTimeValue.Text = TimeUtil.FormatTimeSpan(totalTime);
            }
        }

        // ---------------- Restored helper methods ----------------
        private void CleanupSystemProcesses()
        {
            // Simplified cleanup: keep only non-system processes or duration >=10s
            if (_usageRecords == null) return;
            var toRemove = _usageRecords.Where(r => Models.ProcessFilter.ShouldIgnoreProcess(r.ProcessName) && r.Duration.TotalSeconds < 10).ToList();
            foreach (var rec in toRemove) _usageRecords.Remove(rec);
        }

        private void UpdateAveragePanel(List<AppUsageRecord> aggregatedRecords, DateTime startDate, DateTime endDate)
        {
            if (AveragePanel == null || DailyAverage == null) return;

            int dayCount = (endDate.Date - startDate.Date).Days + 1;
            if (dayCount <= 0) dayCount = 1;

            var total = TimeUtil.CalculateUniqueTotalTime(aggregatedRecords);

            // Determine how many days actually have usage
            int activeDayCount = _usageRecords
                                 .Where(r => r.Duration.TotalSeconds > 0)
                                 .Select(r => r.StartTime.Date)
                                 .Distinct()
                                 .Count();
            if (activeDayCount <= 0) activeDayCount = 1; // safeguard

            var avg = TimeSpan.FromSeconds(total.TotalSeconds / activeDayCount);

            DailyAverage.Text = TimeUtil.FormatTimeSpan(avg);

            // Always show average panel for multi-day views
            AveragePanel.Visibility = Visibility.Visible;
        }

        private void UpdateViewModeAndChartForDateRange(DateTime startDate, DateTime endDate, List<AppUsageRecord> aggregatedRecords)
        {
            // Minimal version: force weekly period & daily chart view then refresh
            _viewModel.CurrentTimePeriod = TimePeriod.Weekly;
            _viewModel.CurrentChartViewMode = ChartViewMode.Daily;
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
        private void UpdateChartViewMode(List<AppUsageRecord>? hourlyRecords = null)
        {
            var today = DateTime.Today;
            var date = _viewModel.SelectedDate;
            _viewModel.CurrentChartViewMode = _viewModel.IsDateRangeSelected
                ? ChartViewMode.Daily
                : ChartViewMode.Hourly;

            if (ViewModeLabel != null) ViewModeLabel.Text = _viewModel.ChartViewModeLabel;
            if (ViewModePanel != null)
                ViewModePanel.Visibility = _viewModel.IsDateRangeSelected || date == today || date == today.AddDays(-1)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            UpdateUsageChart(hourlyRecords);
        }

        private void UpdateDatePickerButtonText()
        {
            try
            {
                if (DatePickerButton == null) return;

                var today = DateTime.Today;

                if (_viewModel.SelectedDate == today && !_viewModel.IsDateRangeSelected)
                {
                    DatePickerButton.Content = "Today";
                    return;
                }

                if (_viewModel.SelectedDate == today.AddDays(-1) && !_viewModel.IsDateRangeSelected)
                {
                    DatePickerButton.Content = "Yesterday";
                    return;
                }

                if (_viewModel.SelectedDate == today && _viewModel.IsDateRangeSelected && _viewModel.CurrentTimePeriod == TimePeriod.Weekly)
                {
                    DatePickerButton.Content = "Last 7 days";
                    return;
                }

                if (_viewModel.SelectedDate == today.AddDays(-29) && _viewModel.IsDateRangeSelected && _viewModel.CurrentTimePeriod == TimePeriod.Custom)
                {
                    DatePickerButton.Content = "Last 30 days";
                    return;
                }

                if (_viewModel.SelectedDate == new DateTime(today.Year, today.Month, 1) && _viewModel.IsDateRangeSelected && _viewModel.CurrentTimePeriod == TimePeriod.Custom)
                {
                    DatePickerButton.Content = "This month";
                    return;
                }

                if (!_viewModel.IsDateRangeSelected)
                {
                    DatePickerButton.Content = _viewModel.SelectedDate.ToString("MMM dd");
                }
                else if (_viewModel.SelectedEndDate.HasValue)
                {
                    DatePickerButton.Content = $"{_viewModel.SelectedDate:MMM dd} - {_viewModel.SelectedEndDate:MMM dd}";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateDatePickerButtonText: {ex.Message}");
                if (DatePickerButton != null)
                    DatePickerButton.Content = _viewModel.SelectedDate.ToString("MMM dd");
            }
        }

        private void LoadRecordsForLastSevenDays()
        {
            try
            {
                DateTime today = DateTime.Today;
                DateTime startDate = today.AddDays(-6);

                var weekRecords = _databaseService?.GetAggregatedRecordsWithLive(startDate, today, _trackingService) ?? new List<AppUsageRecord>();

                UpdateRecordListView(weekRecords);
                SetTimeFrameHeader($"Last 7 Days ({startDate:MMM d} - {today:MMM d}, {today.Year})");

                if (weekRecords.Any())
                {
                    double totalHours = weekRecords.Sum(r => r.Duration.TotalHours);
                    double dailyAverage = totalHours / 7.0;
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
                _viewModel.CurrentTimePeriod = TimePeriod.Weekly;
                _viewModel.CurrentChartViewMode = ChartViewMode.Daily;

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
            // Always recalc from authoritative source: the aggregation service + live slice
            var (start, end) = GetCurrentViewDateRange();
            var aggregated = _databaseService?.GetAggregatedRecordsWithLive(start, end, _trackingService) ?? new List<AppUsageRecord>();
            
            // Reset cache when view changes to ensure accurate totals
            _cachedTotalTime = TimeSpan.Zero;
            _lastFullRefresh = DateTime.MinValue;
            
            UpdateSummaryTab(aggregated);
        }

        // Helper returns the date-span currently shown in the UI
        private (DateTime Start, DateTime End) GetCurrentViewDateRange()
        {
            if (_viewModel?.IsDateRangeSelected == true && _viewModel.SelectedEndDate != null)
            {
                return (_viewModel.SelectedDate.Date, _viewModel.SelectedEndDate.Value.Date);
            }

            return (_viewModel?.SelectedDate.Date ?? DateTime.Today, _viewModel?.SelectedDate.Date ?? DateTime.Today);
        }

        private void UpdateSummaryTab(IEnumerable<AppUsageRecord> recordsToSummarize)
        {
            try
            {
                var totalTime = TimeUtil.CalculateUniqueTotalTime(recordsToSummarize);
                var (start, end) = GetCurrentViewDateRange();
                var maxDuration = TimeSpan.FromDays((end - start).Days + 1);
                if (totalTime > maxDuration) totalTime = maxDuration;

                // Update summary UI – total screen time block
                if (TotalScreenTime != null)
                {
                    TotalScreenTime.Text = TimeUtil.FormatTimeSpan(totalTime);
                }

                // Compute idle time and update IdleRow visibility
                // Calculate idle time from any processes that start with "Idle" 
                var idleTotal = recordsToSummarize
                                    .Where(r => r.ProcessName.StartsWith("Idle", StringComparison.OrdinalIgnoreCase))
                                    .Aggregate(TimeSpan.Zero, (sum, r) => sum + r.Duration);

                if (IdleRow != null && IdleTimeValue != null)
                {
                    if (idleTotal.TotalSeconds >= 5)
                    {
                        IdleRow.Visibility = Visibility.Visible;
                        IdleTimeValue.Text = TimeUtil.FormatTimeSpan(idleTotal);
                    }
                    else
                    {
                        IdleRow.Visibility = Visibility.Collapsed;
                    }
                }

                var mostUsed = recordsToSummarize
                    .Where(r => !ProcessFilter.ShouldIgnoreProcess(r.ProcessName))
                    .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => (Record: group.First(), Duration: TimeUtil.CalculateUniqueTotalTime(group)))
                    .OrderByDescending(app => app.Duration)
                    .FirstOrDefault();

                if (mostUsed.Record is { } mostUsedApp)
                {
                    if (MostUsedApp != null)       MostUsedApp.Text  = mostUsedApp.ProcessName;
                    if (MostUsedAppTime != null)   MostUsedAppTime.Text = TimeUtil.FormatTimeSpan(mostUsed.Duration);

                    mostUsedApp.LoadAppIconIfNeeded();

                    if (MostUsedAppIcon != null) MostUsedAppIcon.DataContext = mostUsedApp;
                    if (MostUsedPlaceholderIcon != null) MostUsedPlaceholderIcon.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (MostUsedApp != null)       MostUsedApp.Text = "None";
                    if (MostUsedAppTime != null)   MostUsedAppTime.Text = TimeUtil.FormatTimeSpan(TimeSpan.Zero);
                    if (MostUsedAppIcon != null) MostUsedAppIcon.DataContext = null;
                    if (MostUsedPlaceholderIcon != null) MostUsedPlaceholderIcon.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating summary tab: {ex.Message}");
            }
        }

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
            _datePickerPopup?.ShowDatePicker(DatePickerButton, _viewModel.SelectedDate, _viewModel.SelectedEndDate, _viewModel.IsDateRangeSelected);
        }

        // ---------------- UI refresh & tracking events migrated from XAML partial ----------------
        private void SetUpUiElements()
        {
            // Initialize the date button text
            UpdateDatePickerButtonText();

            // _usageRecords collection already initialised in MainWindow constructor.
        }

        private void UpdateTimer_Tick(object? sender, object e)
        {
            try
            {
                if (_disposed || _usageRecords == null) return;
                if (_isReloading) return;
                
                _tickCount++;

                // Every 1 second: Live UI updates (only when tracking)
                if (_trackingService != null && _trackingService.IsTracking)
                {
                    DoLiveUpdates();
                }

                // Every 5 seconds: Chart refresh if needed
                if (_tickCount % 5 == 0 && _isChartDirty)
                {
                    DoChartRefresh();
                }

                // Every 30 seconds: Retry missing icons
                if (_tickCount % 30 == 0)
                {
                    DoIconRetry();
                }

                // Every 5 minutes: Auto-save
                if (_tickCount % 300 == 0)
                {
                    DoAutoSave();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateTimer_Tick: {ex.Message}");
            }
        }

        private TimeSpan _cachedTotalTime = TimeSpan.Zero;
        private DateTime _lastFullRefresh = DateTime.MinValue;
        private Task<TimeSpan>? _liveTotalRead;

        private async void DoLiveUpdates()
        {
            Task<TimeSpan>? read = null;
            try
            {
                var (start, end) = GetCurrentViewDateRange();
                var today = DateTime.Today;
                if (today < start || today > end) return;

                var activeRec = _usageRecords.FirstOrDefault(r => r.IsFocused);
                activeRec?.RaiseDurationChanged();
                if (_tickCount % 15 == 0) _isChartDirty = true;

                if ((DateTime.Now - _lastFullRefresh).TotalSeconds < 10)
                {
                    if (activeRec != null)
                    {
                        _cachedTotalTime = _cachedTotalTime.Add(TimeSpan.FromSeconds(1));
                        if (ChartTimeValue != null) ChartTimeValue.Text = TimeUtil.FormatTimeSpan(_cachedTotalTime);
                    }

                    return;
                }
                if (_liveTotalRead != null) return;

                var revision = _usageViewRevision;
                read = Task.Run(() =>
                {
                    var agg = _databaseService?.GetAggregatedRecordsWithLive(start, end, _trackingService) ?? new List<AppUsageRecord>();
                    return TimeUtil.CalculateUniqueTotalTime(agg);
                });
                _liveTotalRead = read;
                var totalTime = await read;
                if (!IsCurrentUsageView(revision) || _liveTotalRead != read) return;

                _cachedTotalTime = totalTime;
                _lastFullRefresh = DateTime.Now;
                if (ChartTimeValue != null) ChartTimeValue.Text = TimeUtil.FormatTimeSpan(totalTime);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating live total time: {ex.Message}");
            }
            finally
            {
                if (read != null && _liveTotalRead == read) _liveTotalRead = null;
            }
        }

        private void DoChartRefresh()
        {
            if (_disposed || _isReloading) return;

            _isChartDirty = false;
            if (_chartRefreshPending)
            {
                _chartRefreshRequested = true;
                return;
            }

            _chartRefreshPending = true;
            _chartRefreshRequested = false;
            var revision = _usageViewRevision;
            var date = _viewModel.SelectedDate;
            var mode = _viewModel.CurrentChartViewMode;

            // Run heavy chart work at idle priority to keep UI responsive
            if (DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    if (!IsCurrentUsageView(revision) || _chartRefreshRequested) return;

                    var hourlyRecords = mode == ChartViewMode.Hourly && _databaseService is { } database
                        ? await Task.Run(() => database.GetHourlyRecordsWithLive(date, _trackingService))
                        : null;

                    if (!IsCurrentUsageView(revision) || _chartRefreshRequested || _viewModel.CurrentChartViewMode != mode)
                        return;

                    UpdateUsageChart(hourlyRecords);
                    UpdateSummaryTab(_usageRecords);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error in chart refresh: {ex.Message}");
                }
                finally
                {
                    _chartRefreshPending = false;
                    if (_chartRefreshRequested && !_disposed)
                        DoChartRefresh();
                }
            }) != true)
                _chartRefreshPending = false;
        }

        private void DoIconRetry()
        {
            try
            {
                foreach (var rec in _usageRecords)
                {
                    rec.LoadAppIconIfNeeded();
                }
            }
            catch (Exception ex) 
            { 
                System.Diagnostics.Debug.WriteLine($"Error loading missing icons: {ex.Message}");
            }
        }

        private void TrackingService_UsageRecordUpdated(object? sender, AppUsageRecord record)
        {
            if (_disposed) return;
            var revision = _usageViewRevision;
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!IsCurrentUsageView(revision) || _isReloading) return;

                    try
                    {
                        var (start, end) = GetCurrentViewDateRange();
                        var today = DateTime.Today;
                        if (today < start || today > end || record.Date.Date < start || record.Date.Date > end) return;

                        var currentProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                        if (record.ProcessName.Equals(currentProcessName, StringComparison.OrdinalIgnoreCase)) return;
                        if (ProcessFilter.ShouldIgnoreProcess(record.ProcessName)) return;

                        UpdateOrAddLiveRecord(record);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in UsageRecordUpdated handler: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error queuing live usage record: {ex.Message}");
            }
        }

        // Helper: update or insert a live record without rebuilding the entire list (reduces flicker)
        private void UpdateOrAddLiveRecord(AppUsageRecord record)
        {
            ApplicationProcessingHelper.ProcessApplicationRecord(record);

            var existing = _usageRecords.FirstOrDefault(r =>
                r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                (!_viewModel.IsDateRangeSelected || r.StartTime == record.StartTime));
            if (_viewModel.IsDateRangeSelected && existing is { Id: > 0 }) return;
            bool isFocused = record.IsFocused && _trackingService?.IsTracking == true;
            if (isFocused)
            {
                foreach (var row in _usageRecords)
                    if (row.IsFocused && !ReferenceEquals(row, existing))
                        row.SetFocus(false);
            }

            if (existing == null)
            {
                record.IsFocused = isFocused;
                if (isFocused) record.SetFocus(true);
                record.LoadAppIconIfNeeded();
                _usageRecords.Add(record);
            }
            else
            {
                if (!isFocused && existing.IsFocused) existing.SetFocus(false);

                if (existing.StartTime == record.StartTime && record.EndTime.HasValue)
                {
                    existing._accumulatedDuration = record.Duration;
                    existing.EndTime = record.EndTime;
                }
                else if (!existing.IsFocused && record.Duration > existing.Duration)
                {
                    existing._accumulatedDuration = record.Duration;
                }

                if (isFocused && !existing.IsFocused) existing.SetFocus(true);
                if (isFocused) existing.LoadAppIconIfNeeded(record);
                existing.RaiseDurationChanged();
            }

            _isChartDirty = true;
        }

        // Helper: rebuild live records list for today (used on initial load)
        private void RefreshLiveRecords()
        {
            try
            {
                var live = _databaseService?.GetDetailRecordsWithLive(DateTime.Today, _trackingService) ?? new List<AppUsageRecord>();
                _usageRecords.Clear();
                foreach (var r in live.OrderByDescending(r => r.Duration))
                {
                    r.LoadAppIconIfNeeded();
                    _usageRecords.Add(r);
                }

                // Ensure chart and summary refresh
                _isChartDirty = true;
                UpdateSummaryTab(_usageRecords.ToList());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in RefreshLiveRecords: {ex.Message}");
            }
        }

        private void TrackingService_UsageSliceFinalized(object? sender, UsageSlice slice)
        {
            _sliceWriter.Enqueue(slice);
        }

        private void ReportPersistenceHealth(PersistenceHealthStatus health, bool drained)
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed) return;

                    try
                    {
                        if (_viewModel.PersistenceHealth != health)
                        {
                            if (health == PersistenceHealthStatus.HistoryIncomplete)
                                App.WriteToLog("One or more finalized usage slices could not be saved.");
                            _viewModel.PersistenceHealth = health;
                        }

                        if (drained && !_viewModel.IsTracking &&
                            (_trackingPauseReasons & (TrackingPauseReason.Resetting | TrackingPauseReason.Exiting)) == 0)
                            DoChartRefresh();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error notifying persistence health: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error queuing persistence health: {ex.Message}");
            }
        }
    }
} 

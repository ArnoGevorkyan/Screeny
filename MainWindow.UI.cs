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
        private int _chartRevision;
        private bool _chartRefreshPending;
        private bool _chartRefreshRequested;
        private (DateTime Start, DateTime End, List<AppUsageRecord> Records)? _loadedDashboardVisits;

        private int InvalidateUsageView()
        {
            _loadedDashboardVisits = null;
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
        private void UpdateUsageChart(List<AppUsageRecord>? visits = null)
        {
            // Safety checks
            if (UsageChartLive == null || _usageRecords == null) return;

            var (start, end) = GetCurrentViewDateRange();
            if (visits != null)
            {
                _chartRevision++;
                _loadedDashboardVisits = (start, end, visits);
            }

            IEnumerable<AppUsageRecord> records = _loadedDashboardVisits is { } loaded && loaded.Start == start && loaded.End == end
                ? loaded.Records
                : Array.Empty<AppUsageRecord>();
            var usage = DashboardUsage.Calculate(records, start, end, DateTime.Now);

            ChartHelper.UpdateUsageChart(
                UsageChartLive,
                usage.Visits,
                _viewModel.CurrentChartViewMode,
                _viewModel.CurrentTimePeriod,
                _viewModel.SelectedDate,
                _viewModel.SelectedEndDate);

            var total = TimeUtil.FormatTimeSpan(usage.Total);
            if (ChartTimeValue != null)
            {
                ChartTimeValue.Text = total;
            }
            UpdateSummaryTab(usage.Visits, total);
            if (_viewModel.IsDateRangeSelected)
                UpdateAveragePanel(usage.DailyAverage);
        }

        // ---------------- Restored helper methods ----------------
        private void CleanupSystemProcesses()
        {
            // Simplified cleanup: keep only non-system processes or duration >=10s
            if (_usageRecords == null) return;
            var toRemove = _usageRecords.Where(r => Models.ProcessFilter.ShouldIgnoreProcess(r.ProcessName) && r.Duration.TotalSeconds < 10).ToList();
            foreach (var rec in toRemove) _usageRecords.Remove(rec);
        }

        private void UpdateAveragePanel(TimeSpan average)
        {
            if (AveragePanel == null || DailyAverage == null) return;

            DailyAverage.Text = TimeUtil.FormatTimeSpan(average);

            // Always show average panel for multi-day views
            AveragePanel.Visibility = Visibility.Visible;
        }

        private void UpdateViewModeAndChartForDateRange(List<AppUsageRecord> visits)
        {
            // Minimal version: force weekly period & daily chart view then refresh
            _viewModel.CurrentTimePeriod = TimePeriod.Weekly;
            _viewModel.CurrentChartViewMode = ChartViewMode.Daily;
            UpdateUsageChart(visits);
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

                UpdateChartWithRecords(_databaseService?.GetRawRecordsForDateRange(startDate, today) ?? new List<AppUsageRecord>());

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

                    UpdateUsageChart(records);
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

        // Helper returns the date-span currently shown in the UI
        private (DateTime Start, DateTime End) GetCurrentViewDateRange()
        {
            if (_viewModel?.IsDateRangeSelected == true && _viewModel.SelectedEndDate != null)
            {
                return (_viewModel.SelectedDate.Date, _viewModel.SelectedEndDate.Value.Date);
            }

            return (_viewModel?.SelectedDate.Date ?? DateTime.Today, _viewModel?.SelectedDate.Date ?? DateTime.Today);
        }

        private void UpdateSummaryTab(IEnumerable<AppUsageRecord> visits, string total)
        {
            try
            {
                // Update summary UI – total screen time block
                if (TotalScreenTime != null)
                {
                    TotalScreenTime.Text = total;
                }

                // Compute idle time and update IdleRow visibility
                // Calculate idle time from any processes that start with "Idle" 
                var idleTotal = visits
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

                var mostUsed = visits
                    .GroupBy(r => ApplicationNameNormalizer.NormalizeProcessName(r.ProcessName), StringComparer.OrdinalIgnoreCase)
                    .Select(group => (ProcessName: group.Key,
                        Record: group.FirstOrDefault(r => r.WindowHandle != IntPtr.Zero) ?? group.First(),
                        Duration: TimeUtil.CalculateUniqueTotalTime(group)))
                    .OrderByDescending(app => app.Duration)
                    .ThenBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (mostUsed.Record is { } mostUsedApp)
                {
                    if (MostUsedApp != null)       MostUsedApp.Text  = mostUsed.ProcessName;
                    if (MostUsedAppTime != null)   MostUsedAppTime.Text = TimeUtil.FormatTimeSpan(mostUsed.Duration);

                    if (MostUsedAppIcon != null)
                    {
                        if (MostUsedAppIcon.DataContext is AppUsageRecord displayed &&
                            string.Equals(ApplicationNameNormalizer.NormalizeProcessName(displayed.ProcessName),
                                mostUsed.ProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            displayed.LoadAppIconIfNeeded(mostUsedApp);
                        }
                        else
                        {
                            mostUsedApp.LoadAppIconIfNeeded();
                            MostUsedAppIcon.DataContext = mostUsedApp;
                        }
                    }
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

                int chartRefreshInterval = _viewModel.IsDateRangeSelected && _trackingService?.IsTracking == true ? 10 : 5;
                if (_tickCount % chartRefreshInterval == 0 && _isChartDirty)
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

        private void DoLiveUpdates()
        {
            var (start, end) = GetCurrentViewDateRange();
            var today = DateTime.Today;
            if (today < start || today > end) return;

            _usageRecords.FirstOrDefault(r => r.IsFocused)?.RaiseDurationChanged();
            if (_tickCount % 15 == 0) _isChartDirty = true;
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
            var chartRevision = _chartRevision;
            var (start, end) = GetCurrentViewDateRange();
            var mode = _viewModel.CurrentChartViewMode;

            // Run heavy chart work at idle priority to keep UI responsive
            if (DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    if (!IsCurrentUsageView(revision) || _isReloading || _chartRefreshRequested ||
                        _chartRevision != chartRevision || GetCurrentViewDateRange() != (start, end) ||
                        _viewModel.CurrentChartViewMode != mode)
                        return;

                    var visits = _databaseService is { } database
                        ? await Task.Run(() => mode == ChartViewMode.Hourly
                            ? database.GetHourlyRecordsWithLive(start, _trackingService)
                            : database.GetDashboardRecordsWithLive(start, end, _trackingService))
                        : null;

                    if (!IsCurrentUsageView(revision) || _isReloading || _chartRefreshRequested ||
                        _chartRevision != chartRevision || GetCurrentViewDateRange() != (start, end) ||
                        _viewModel.CurrentChartViewMode != mode)
                        return;

                    if (visits != null) UpdateUsageChart(visits);
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
                if (MostUsedAppIcon?.DataContext is AppUsageRecord displayed)
                    displayed.LoadAppIconIfNeeded();

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
                var visits = new List<AppUsageRecord>();
                var live = _databaseService?.GetDetailRecordsWithLive(DateTime.Today, _trackingService, visits) ?? new List<AppUsageRecord>();
                _usageRecords.Clear();
                foreach (var r in live.OrderByDescending(r => r.Duration))
                {
                    r.LoadAppIconIfNeeded();
                    _usageRecords.Add(r);
                }

                // Ensure chart and summary refresh
                _isChartDirty = true;
                UpdateUsageChart(visits);
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

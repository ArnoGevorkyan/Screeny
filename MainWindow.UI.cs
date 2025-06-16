using ScreenTimeTracker.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Linq;
using System;
using ScreenTimeTracker.Helpers;
using System.Collections.Generic;

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

            // Calculate daily average.
            var avg = TimeSpan.FromSeconds(total.TotalSeconds / dayCount);

            DailyAverage.Text = ChartHelper.FormatTimeSpan(avg);

            // Show panel only when selection spans more than one day.
            AveragePanel.Visibility = dayCount > 1 ? Visibility.Visible : Visibility.Collapsed;
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

                var weekRecords = GetAggregatedRecordsForDateRange(startDate, today);

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
                        UsageListView.ItemsSource = null;
                        UsageListView.ItemsSource = _usageRecords;
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
    }
} 
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Linq;
using System;
using ScreenTimeTracker.Helpers;
using System.Collections.Generic;
using ScreenTimeTracker.Services;
using System.Collections.ObjectModel;
using ScreenTimeTracker.Models;

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
                _viewModel.SetChartTotalTime(totalTime);
            }
        }

        // ---------------- Restored helper methods ----------------
        private void CleanupSystemProcesses()
        {
            // Remove transient Windows system processes (short-lived) and hide the synthetic "Idle / Away" row.
            if (_usageRecords == null) return;

            var toRemove = _usageRecords.Where(r =>
                               (IsWindowsSystemProcess(r.ProcessName) && r.Duration.TotalSeconds < 10) ||
                               r.ProcessName.StartsWith("Idle", StringComparison.OrdinalIgnoreCase))
                           .ToList();

            foreach (var rec in toRemove)
                _usageRecords.Remove(rec);
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

            _viewModel.SetChartTotalTime(totalTime);
        }

        private void RefreshLiveRecords()
        {
            var liveRecords = _trackingService.GetRecords();
            var focusedRecord = liveRecords.FirstOrDefault(r => r.IsFocused);

            // Update existing records or add new ones
            foreach (var record in liveRecords)
            {
                UpdateOrAddLiveRecord(record);
            }

            // Update chart
            UpdateUsageChart(focusedRecord);

            // Calculate and update summary via ViewModel
            List<AppUsageRecord> allRecords = _usageRecords.ToList();
            TimeSpan totalTime = CalculateTotalActiveTime(allRecords);
            double idleSeconds = allRecords.Where(r => r.IsIdle).Sum(r => r.Duration.TotalSeconds);
            TimeSpan idleTotal = TimeSpan.FromSeconds(idleSeconds);
            var mostUsedApp = allRecords.OrderByDescending(r => r.Duration).FirstOrDefault();
            string mostUsedName = mostUsedApp?.ApplicationName ?? "N/A";
            TimeSpan mostUsedDuration = mostUsedApp?.Duration ?? TimeSpan.Zero;
            TimeSpan? dailyAverage = null; // Adjust if needed
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage? mostUsedIcon = mostUsedApp?.AppIcon;

            _viewModel.UpdateSummary(totalTime, idleTotal, mostUsedName, mostUsedDuration, dailyAverage, mostUsedIcon);
        }

        private void TrackingService_WindowChanged(object sender, EventArgs e)
        {
            RefreshLiveRecords();
        }

        private void TrackingService_UsageRecordUpdated(object sender, AppUsageRecord rec)
        {
            UpdateOrAddLiveRecord(rec);
        }

        private void UpdateOrAddLiveRecord(AppUsageRecord record)
        {
            if (record == null) return;
            RecordListBinder.UpdateOrAdd(_usageRecords, record);
            // Additional updates if needed, e.g., notify changes
        }
    }
} 
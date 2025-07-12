using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using ScreenTimeTracker.Models;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using ScreenTimeTracker.Services;
using ScreenTimeTracker.Helpers;

namespace ScreenTimeTracker
{
    // This partial class will gradually absorb lifecycle logic, services, timers, and power-event handling
    // migrated out of the original MainWindow.xaml.cs. For now it is an empty placeholder to keep the project
    // compiling while we refactor incrementally.
    public sealed partial class MainWindow
    {
        // ---------------- Power-notification plumbing ----------------
        // These were moved out of the giant code-behind to keep UI file lean.
        
        private void RegisterPowerNotifications()
        {
            if (_hWnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("Cannot register power notifications: HWND is zero.");
                return;
            }

            try
            {
                Guid consoleGuid = GuidConsoleDisplayState; // Need local copy for ref parameter
                _hConsoleDisplayState = RegisterPowerSettingNotification(_hWnd, ref consoleGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_hConsoleDisplayState == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to register for GuidConsoleDisplayState. Error: {Marshal.GetLastWin32Error()}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Successfully registered for GuidConsoleDisplayState.");
                }

                Guid awayGuid = GuidSystemAwayMode; // Need local copy for ref parameter
                _hSystemAwayMode = RegisterPowerSettingNotification(_hWnd, ref awayGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_hSystemAwayMode == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to register for GuidSystemAwayMode. Error: {Marshal.GetLastWin32Error()}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Successfully registered for GuidSystemAwayMode.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error registering power notifications: {ex.Message}");
            }
        }

        private void UnregisterPowerNotifications()
        {
            try
            {
                if (_hConsoleDisplayState != IntPtr.Zero)
                {
                    if (UnregisterPowerSettingNotification(_hConsoleDisplayState))
                        System.Diagnostics.Debug.WriteLine("Successfully unregistered GuidConsoleDisplayState.");
                    else
                        System.Diagnostics.Debug.WriteLine($"Failed to unregister GuidConsoleDisplayState. Error: {Marshal.GetLastWin32Error()}");
                    _hConsoleDisplayState = IntPtr.Zero;
                }
                if (_hSystemAwayMode != IntPtr.Zero)
                {
                    if (UnregisterPowerSettingNotification(_hSystemAwayMode))
                        System.Diagnostics.Debug.WriteLine("Successfully unregistered GuidSystemAwayMode.");
                    else
                        System.Diagnostics.Debug.WriteLine($"Failed to unregister GuidSystemAwayMode. Error: {Marshal.GetLastWin32Error()}");
                    _hSystemAwayMode = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error unregistering power notifications: {ex.Message}");
            }
        }

        // ---------------- Lifecycle & disposal ----------------
        public void Dispose()
        {
            System.Diagnostics.Debug.WriteLine("[LOG] ENTERING Dispose");
            if (!_disposed)
            {
                // Dispose Tray Icon Helper FIRST to remove icon
                _trayIconHelper?.Dispose();

                // Unregister power notifications
                UnregisterPowerNotifications();

                // Restore original window procedure
                RestoreWindowProc();

                // Unsubscribe from AppWindow event
                if (_appWindow != null)
                {
                    _appWindow.Closing -= AppWindow_Closing;
                }

                // Stop services
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Stopping services...");
                EndTracking();
                _updateTimer?.Stop();
                _autoSaveTimer?.Stop();
                // _chartRefreshTimer removed – no handler to detach
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Services stopped.");

                // REMOVED SaveRecordsToDatabase() - handled by PrepareForSuspend or ExitClicked.
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Save skipped.");

                // Clear collections
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Clearing collections...");
                _usageRecords?.Clear();

                // Dispose services
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Disposing services...");
                _trackingService?.Dispose();
                _databaseService?.Dispose();

                // Remove event handlers
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Removing event handlers...");
                if (_updateTimer != null) _updateTimer.Tick -= UpdateTimer_Tick;
                _busSubA?.Dispose();
                _busSubB?.Dispose();
                if (_autoSaveTimer != null) _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
                if (Content is FrameworkElement root) root.Loaded -= MainWindow_Loaded;
                if (_trayIconHelper != null)
                {
                    _trayIconHelper.ShowClicked -= TrayIcon_ShowClicked;
                    _trayIconHelper.ExitClicked -= TrayIcon_ExitClicked;
                }

                _disposed = true;
                System.Diagnostics.Debug.WriteLine("[LOG] MainWindow disposed.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Already disposed.");
            }
            System.Diagnostics.Debug.WriteLine("[LOG] EXITING Dispose");
        }

        /// <summary>
        /// Called from App.OnSuspending to pause tracking and save data.
        /// </summary>
        public void PrepareForSuspend()
        {
            System.Diagnostics.Debug.WriteLine("[LOG] ENTERING PrepareForSuspend");
            try
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] Current system time: {DateTime.Now}");
                if (_selectedDate > DateTime.Today)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOG] WARNING: Future _selectedDate detected ({_selectedDate:yyyy-MM-dd}), resetting to today.");
                    _selectedDate = DateTime.Today;
                }

                if (_trackingService != null && _trackingService.IsTracking)
                {
                    System.Diagnostics.Debug.WriteLine("[LOG] PrepareForSuspend: BEFORE EndTracking()");
                    _trackingService.EndTracking();
                    System.Diagnostics.Debug.WriteLine("[LOG] PrepareForSuspend: AFTER EndTracking()");
                }

                System.Diagnostics.Debug.WriteLine("[LOG] PrepareForSuspend: BEFORE SaveRecordsToDatabase()");
                SaveRecordsToDatabase();
                System.Diagnostics.Debug.WriteLine("[LOG] PrepareForSuspend: AFTER SaveRecordsToDatabase()");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] PrepareForSuspend: **** ERROR **** during save: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
            System.Diagnostics.Debug.WriteLine("[LOG] EXITING PrepareForSuspend");
        }

        private void AutoSaveTimer_Tick(object? sender, object e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Auto-save timer tick - saving records");

                SaveRecordsToDatabase();
                CleanupSystemProcesses();

                _autoSaveCycleCount++;
                if (_autoSaveCycleCount >= 12 && _databaseService != null)
                {
                    _autoSaveCycleCount = 0;
                    System.Diagnostics.Debug.WriteLine("Running periodic database maintenance");
                    Task.Run(() =>
                    {
                        try
                        {
                            _databaseService.PerformDatabaseMaintenance();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error during periodic database maintenance: {ex.Message}");
                        }
                    });
                }

                if (_selectedDate.Date == DateTime.Now.Date && _currentTimePeriod == TimePeriod.Daily)
                {
                    System.Diagnostics.Debug.WriteLine("Refreshing today's data after auto-save");
                    LoadRecordsForDate(_selectedDate);
                }
                else
                {
                    UpdateUsageChart();
                    // Replace UpdateSummaryTab with ViewModel update
                    List<AppUsageRecord> allRecords = _usageRecords.ToList();
                    TimeSpan totalTime = CalculateTotalActiveTime(allRecords);
                    double idleSeconds = allRecords.Where(r => r.IsIdle).Sum(r => r.Duration.TotalSeconds);
                    TimeSpan idleTotal = TimeSpan.FromSeconds(idleSeconds);
                    var mostUsedApp = allRecords.OrderByDescending(r => r.Duration).FirstOrDefault();
                    string mostUsedName = mostUsedApp?.ApplicationName ?? "N/A";
                    TimeSpan mostUsedDuration = mostUsedApp?.Duration ?? TimeSpan.Zero;
                    TimeSpan? dailyAverage = null;
                    _viewModel.UpdateSummary(totalTime, idleTotal, mostUsedName, mostUsedDuration, dailyAverage);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in auto-save timer tick: {ex}");
            }
        }

        // ---------------- Tracking control logic ----------------
        private void BeginTracking()
        {
            ThrowIfDisposed();
            try
            {
                System.Diagnostics.Debug.WriteLine("Starting tracking");
                _trackingService.StartTracking();
                System.Diagnostics.Debug.WriteLine($"Tracking started: IsTracking={_trackingService.IsTracking}");

                // Log current foreground window
                if (_trackingService.CurrentRecord != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Current window: {_trackingService.CurrentRecord.ProcessName} - {_trackingService.CurrentRecord.WindowTitle}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("No current window detected yet");
                }

                // Start/Stop buttons are collapsed; tracking indicator handles state.

                // Start timers
                _updateTimer.Start();
                _autoSaveTimer.Start();
                // Chart refresh consolidated into 1-s UI timer – no separate start needed
                // _isChartDirty = true; // force initial chart render

                // Update chart
                UpdateUsageChart();

                // Calculate and update summary
                List<AppUsageRecord> allRecords = _usageRecords.ToList();
                TimeSpan totalTime = CalculateTotalActiveTime(allRecords);
                double idleSeconds = allRecords.Where(r => r.IsIdle).Sum(r => r.Duration.TotalSeconds);
                TimeSpan idleTotal = TimeSpan.FromSeconds(idleSeconds);
                var mostUsedApp = allRecords.OrderByDescending(r => r.Duration).FirstOrDefault();
                string mostUsedName = mostUsedApp?.ApplicationName ?? "N/A";
                TimeSpan mostUsedDuration = mostUsedApp?.Duration ?? TimeSpan.Zero;
                TimeSpan? dailyAverage = null;
                _viewModel.UpdateSummary(totalTime, idleTotal, mostUsedName, mostUsedDuration, dailyAverage);

                // Sync ViewModel state
                _viewModel.IsTracking = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error starting tracking: {ex.Message}");
                var dialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Failed to start tracking: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                };
                _ = dialog.ShowAsync();
            }
            finally
            {
                // Tracking indicator now handled via data bindings (no imperative update needed)
            }
        }

        private void EndTracking()
        {
            ThrowIfDisposed();
            System.Diagnostics.Debug.WriteLine("Stopping tracking");
            _trackingService.EndTracking();

            _viewModel.IsTracking = false;

            // Unfocus all UI records
            foreach (var uiRec in _usageRecords.ToList())
            {
                if (uiRec.IsFocused)
                {
                    uiRec.SetFocus(false);
                }
            }

            // Stop timers
            _updateTimer.Stop();
            _autoSaveTimer.Stop();
            // Chart refresh handled by UI timer – nothing to stop

            // Persist session data
            SaveRecordsToDatabase();
            CleanupSystemProcesses();

            // Update summary and chart
            List<AppUsageRecord> allRecords = _usageRecords.ToList();
            TimeSpan totalTime = CalculateTotalActiveTime(allRecords);
            double idleSeconds = allRecords.Where(r => r.IsIdle).Sum(r => r.Duration.TotalSeconds);
            TimeSpan idleTotal = TimeSpan.FromSeconds(idleSeconds);
            var mostUsedApp = allRecords.OrderByDescending(r => r.Duration).FirstOrDefault();
            string mostUsedName = mostUsedApp?.ApplicationName ?? "N/A";
            TimeSpan mostUsedDuration = mostUsedApp?.Duration ?? TimeSpan.Zero;
            TimeSpan? dailyAverage = null;
            _viewModel.UpdateSummary(totalTime, idleTotal, mostUsedName, mostUsedDuration, dailyAverage);
            UpdateUsageChart();
            // Tracking indicator handled via binding; no imperative call.
        }

        private void SaveRecordsToDatabase()
        {
            System.Diagnostics.Debug.WriteLine("[LOG] ENTERING SaveRecordsToDatabase");
            if (_databaseService == null || _trackingService == null)
            {
                System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: prerequisites missing, skipping.");
                return;
            }
            try
            {
                var recordsToSave = _trackingService.GetRecords()
                    .Where(r => r.IsFromDate(DateTime.Now.Date))
                    .ToList();

                var recordsByProcess = recordsToSave
                    .Where(r => !IsWindowsSystemProcess(r.ProcessName) && r.Duration.TotalSeconds > 0)
                    .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase);

                foreach (var processGroup in recordsByProcess)
                {
                    try
                    {
                        var totalDuration = TimeSpan.FromSeconds(processGroup.Sum(r => r.Duration.TotalSeconds));
                        var record        = processGroup.OrderByDescending(r => r.Duration).First();
                        if (record.IsFocused) record.SetFocus(false);
                        record._accumulatedDuration = totalDuration;
                        if (_databaseService != null)
                        {
                            if (record.Id > 0)
                                _databaseService.UpdateRecord(record);
                            else
                                _databaseService.SaveRecord(record);
                        }
                    }
                    catch (Exception pe)
                    {
                        System.Diagnostics.Debug.WriteLine($"Save error for {processGroup.Key}: {pe.Message}");
                    }
                }
                System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase complete");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecordsToDatabase exception: {ex.Message}");
            }
        }

        // Helper to retrieve current live records for today
        private List<AppUsageRecord> GetLiveRecordsForToday()
        {
            return _trackingService?.GetRecords()
                       ?.Where(r => r.IsFromDate(DateTime.Today))
                       ?.ToList() ?? new List<AppUsageRecord>();
        }

        // ---------------- Data loading logic ----------------
        private void LoadRecordsForDate(DateTime date)
        {
            System.Diagnostics.Debug.WriteLine($"Loading records for date: {date:yyyy-MM-dd}, System.Today: {DateTime.Today:yyyy-MM-dd}");
            if (date > DateTime.Today) date = DateTime.Today;

            _selectedDate       = date;
            _selectedEndDate    = null;
            _isDateRangeSelected = false;

            _usageRecords.Clear();

            _currentTimePeriod = TimePeriod.Daily;
            _currentChartViewMode = ChartViewMode.Hourly;

            List<AppUsageRecord> records = new();
            try
            {
                records = LoadRecordsForSpecificDay(date);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records for {date:yyyy-MM-dd}: {ex.Message}");
                ShowErrorDialog($"Error loading data for {date:yyyy-MM-dd}: {ex.Message}");
                return;
            }

            if (records.Any())
            {
                foreach (var r in records)
                {
                    _usageRecords.Add(r);
                }
            }
            else
            {
                ShowNoDataDialog(date, date);
            }

            CleanupSystemProcesses();

            // Calculate summary metrics
            TimeSpan totalTime = CalculateTotalActiveTime(records);
            double idleSeconds = records.Where(r => r.IsIdle).Sum(r => r.Duration.TotalSeconds);
            TimeSpan idleTotal = TimeSpan.FromSeconds(idleSeconds);
            var mostUsedApp = records.OrderByDescending(r => r.Duration).FirstOrDefault();
            string mostUsedName = mostUsedApp?.ApplicationName ?? "N/A";
            TimeSpan mostUsedDuration = mostUsedApp?.Duration ?? TimeSpan.Zero;
            TimeSpan? dailyAverage = null; // Calculate if needed

            // Update ViewModel
            _viewModel.UpdateSummary(totalTime, idleTotal, mostUsedName, mostUsedDuration, dailyAverage);
            _viewModel.SummaryTitle = (date == DateTime.Today) ? "Today's Screen Time Summary" : $"{date:MMMM d, yyyy} Screen Time Summary";
            _viewModel.IsAverageVisible = false; // Or calculate based on logic

            UpdateUsageChart();
        }

        // ---------------- Date range loading logic ----------------
        private void LoadRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            System.Diagnostics.Debug.WriteLine($"Loading records for range: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

            _selectedDate = startDate;
            _selectedEndDate = endDate;
            _isDateRangeSelected = true;

            _usageRecords.Clear();

            _currentTimePeriod = TimePeriod.Custom;
            _currentChartViewMode = ChartViewMode.Daily;

            List<AppUsageRecord> records = new();
            try
            {
                records = _aggregationService.GetDetailRecordsForDateRange(startDate, endDate);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading range: {ex.Message}");
                ShowErrorDialog($"Error loading data for range: {ex.Message}");
                return;
            }

            if (records.Any())
            {
                foreach (var r in records)
                {
                    _usageRecords.Add(r);
                }
            }
            else
            {
                ShowNoDataDialog(startDate, endDate);
            }

            CleanupSystemProcesses();

            // Calculate summary metrics
            TimeSpan totalTime = CalculateTotalActiveTime(records);
            double idleSeconds = records.Where(r => r.IsIdle).Sum(r => r.Duration.TotalSeconds);
            TimeSpan idleTotal = TimeSpan.FromSeconds(idleSeconds);
            var mostUsedApp = records.OrderByDescending(r => r.Duration).FirstOrDefault();
            string mostUsedName = mostUsedApp?.ApplicationName ?? "N/A";
            TimeSpan mostUsedDuration = mostUsedApp?.Duration ?? TimeSpan.Zero;
            int dayCount = (endDate - startDate).Days + 1;
            TimeSpan? dailyAverage = dayCount > 0 ? (TimeSpan?)(totalTime / dayCount) : null;

            // Update ViewModel
            _viewModel.UpdateSummary(totalTime, idleTotal, mostUsedName, mostUsedDuration, dailyAverage);
            _viewModel.SummaryTitle = $"{startDate:MMMM d} - {endDate:MMMM d} Screen Time Summary";
            _viewModel.IsAverageVisible = true;

            UpdateUsageChart();
        }

        // ---------------- Date-period helpers migrated from MainWindow.xaml.cs ----------------
        private int GetDayCountForTimePeriod(TimePeriod period, DateTime date)
        {
            return period switch
            {
                TimePeriod.Weekly => 7,
                _ => 1,
            };
        }

        /// <summary>
        /// Calculates the total active time covered by a set of records, merging overlapping intervals so time isn't double-counted.
        /// </summary>
        private TimeSpan CalculateTotalActiveTime(List<AppUsageRecord> records)
        {
            var intervals = records
                .Select(r => new { Start = r.StartTime, End = r.StartTime + r.Duration })
                .Where(iv => iv.End > iv.Start)
                .OrderBy(iv => iv.Start)
                .ToList();

            var merged = new List<(DateTime Start, DateTime End)>();

            foreach (var iv in intervals)
            {
                if (!merged.Any() || iv.Start > merged.Last().End)
                {
                    merged.Add((iv.Start, iv.End));
                }
                else
                {
                    var last = merged[^1];
                    merged[^1] = (last.Start, iv.End > last.End ? iv.End : last.End);
                }
            }

            TimeSpan total = TimeSpan.Zero;
            foreach (var span in merged)
                total += span.End - span.Start;

            return total;
        }

        // ---------------- Helper: Load single-day records without UI refresh ----------------
        private List<AppUsageRecord> LoadRecordsForSpecificDay(DateTime date, bool updateUI = true)
        {
            if (_databaseService == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: _databaseService is null in LoadRecordsForSpecificDay. Returning empty list.");
                return new List<AppUsageRecord>();
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"Loading records for specific day: {date:yyyy-MM-dd}");

                var records = BuildRecords(() => _aggregationService.GetDetailRecordsForDate(date));

                if (!updateUI)
                    return records;

                _selectedDate        = date;
                _selectedEndDate     = null;
                _isDateRangeSelected = false;

                foreach (var r in records.OrderByDescending(r => r.Duration))
                {
                    if (r.ProcessName.StartsWith("Idle", StringComparison.OrdinalIgnoreCase)) continue; // hide idle row
                    r.LoadAppIconIfNeeded();
                    _usageRecords.Add(r);
                }

                // Remove system / idle rows if any slipped through
                CleanupSystemProcesses();

                // Refresh UI elements on dispatcher
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_disposed) return;

                    if (UsageListView != null)
                    {
                        UsageListView.ItemsSource = null;
                        UsageListView.ItemsSource = _usageRecords;
                    }

                    UpdateUsageChart();
                });

                return records;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in LoadRecordsForSpecificDay: {ex.Message}");
                return new List<AppUsageRecord>();
            }
        }

        // ---------------- Utility helpers (non-UI) ----------------
        private bool IsWindowsSystemProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;

            string normalizedName = processName.Trim().ToLowerInvariant();

            string[] highPriority = {
                "explorer","shellexperiencehost","searchhost","startmenuexperiencehost","applicationframehost",
                "systemsettings","dwm","winlogon","csrss","services","svchost","runtimebroker"
            };
            if (highPriority.Any(p => normalizedName.Contains(p))) return true;

            string[] others = {
                "textinputhost","windowsterminal","cmd","powershell","pwsh","conhost","winstore.app",
                "lockapp","logonui","fontdrvhost","taskhostw","ctfmon","rundll32","dllhost","sihost",
                "taskmgr","backgroundtaskhost","smartscreen","securityhealthservice","registry",
                "microsoftedgeupdate","wmiprvse","spoolsv","tabtip","tabtip32","searchui","searchapp",
                "settingssynchost","wudfhost"
            };
            return others.Contains(normalizedName);
        }

        // -------------------------------------------------------------
        // Helper: build and canonicalise record list on a background thread
        // -------------------------------------------------------------
        private static List<AppUsageRecord> BuildRecords(Func<List<AppUsageRecord>> query)
        {
            return Task.Run(() =>
            {
                var list = query();
                foreach (var r in list)
                    ApplicationProcessingHelper.ProcessApplicationRecord(r);
                return list;
            }).Result;
        }

        private void UpdateTimer_Tick(object? sender, object e)
        {
            // Periodic UI update logic: refresh live records, mark chart dirty if needed, etc.
            RefreshLiveRecords();

            UpdateUsageChart();
        }
    }
} 
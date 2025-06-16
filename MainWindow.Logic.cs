using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using ScreenTimeTracker.Models;
using System.Linq;
using Microsoft.UI.Xaml.Controls;

namespace ScreenTimeTracker
{
    // This partial class will gradually absorb lifecycle logic, services, timers, and power-event handling
    // migrated out of the original MainWindow.xaml.cs. For now it is an empty placeholder to keep the project
    // compiling while we refactor incrementally.
    public sealed partial class MainWindow
    {
        // Shared field for UpdateTimer_Tick counter
        private int _timerTickCounter = 0;

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
                _trackingService?.StopTracking();
                _updateTimer?.Stop();
                _autoSaveTimer?.Stop();
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
                if (_trackingService != null)
                {
                    _trackingService.WindowChanged -= TrackingService_WindowChanged;
                    _trackingService.UsageRecordUpdated -= TrackingService_UsageRecordUpdated;
                }
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
                    System.Diagnostics.Debug.WriteLine("[LOG] PrepareForSuspend: BEFORE StopTracking()");
                    _trackingService.StopTracking();
                    System.Diagnostics.Debug.WriteLine("[LOG] PrepareForSuspend: AFTER StopTracking()");
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
                    UpdateSummaryTab(_usageRecords.ToList());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in auto-save timer tick: {ex}");
            }
        }

        // ---------------- Tracking control logic ----------------
        private void StartTracking()
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

                // Update UI state
                StartButton.IsEnabled = false;
                StopButton.IsEnabled  = true;

                // Start timers
                _updateTimer.Start();
                _autoSaveTimer.Start();

                // Immediate chart refresh
                UpdateUsageChart();
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
                // Ensure UI consistency
                StartButton.IsEnabled = false;
                StopButton.IsEnabled  = true;
                UpdateTrackingIndicator();
            }
        }

        private void StopTracking()
        {
            ThrowIfDisposed();
            System.Diagnostics.Debug.WriteLine("Stopping tracking");
            _trackingService.StopTracking();

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

            // Persist session data
            SaveRecordsToDatabase();
            CleanupSystemProcesses();

            // Update UI state
            StartButton.IsEnabled = true;
            StopButton.IsEnabled  = false;
            UpdateSummaryTab(_usageRecords.ToList());
            UpdateUsageChart();
            UpdateTrackingIndicator();
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
                        if (record.Id > 0)
                            _databaseService.UpdateRecord(record);
                        else
                            _databaseService.SaveRecord(record);
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
    }
} 
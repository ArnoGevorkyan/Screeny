using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using MicrosoftUI = Microsoft.UI; // Alias to avoid ambiguity
using WinRT.Interop;
using System.Collections.ObjectModel;
using ScreenTimeTracker.Services;
using ScreenTimeTracker.Models;
using System.Runtime.InteropServices;
using System.Linq;
using System.Collections.Generic;
using Microsoft.UI; // Add this for Win32Interop
// LiveCharts using directives
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.VisualElements;
using LiveChartsCore.SkiaSharpView.WinUI;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using LiveChartsCore.Drawing;
using SkiaSharp;
using Windows.Globalization; // For DayOfWeek
using SDColor = System.Drawing.Color; // Alias for System.Drawing.Color
using ScreenTimeTracker.Helpers;
using Microsoft.UI.Dispatching;

namespace ScreenTimeTracker
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public sealed partial class MainWindow : Window, IDisposable
    {
        private readonly WindowTrackingService _trackingService;
        private readonly DatabaseService? _databaseService;
        private readonly ObservableCollection<AppUsageRecord> _usageRecords;
        private DateTime _selectedDate;
        private DateTime? _selectedEndDate; // For date ranges
        private DispatcherTimer _updateTimer;
        private DispatcherTimer _autoSaveTimer;
        private bool _disposed;
        private TimePeriod _currentTimePeriod = TimePeriod.Daily;
        private ChartViewMode _currentChartViewMode = ChartViewMode.Hourly;
        private bool _isDateRangeSelected = false;
        
        // Static constructor to configure LiveCharts
        static MainWindow()
        {
            // Configure global LiveCharts settings
            LiveChartsSettings.ConfigureTheme();
        }

        // Replace the old popup fields with a DatePickerPopup instance
        private DatePickerPopup? _datePickerPopup;

        // Add WindowControlHelper field
        private readonly WindowControlHelper _windowHelper;
        
        public MainWindow()
        {
            _disposed = false;
            _selectedDate = DateTime.Today;
            _usageRecords = new ObservableCollection<AppUsageRecord>();
            
            // Initialize timer fields to avoid nullable warnings
            _updateTimer = new DispatcherTimer();
            _autoSaveTimer = new DispatcherTimer();

            InitializeComponent();

            // Initialize the WindowControlHelper
            _windowHelper = new WindowControlHelper(this);

            // Initialize services
            _databaseService = new DatabaseService();
            _trackingService = new WindowTrackingService();

            // Set up tracking service events
            _trackingService.WindowChanged += TrackingService_WindowChanged;
            _trackingService.UsageRecordUpdated += TrackingService_UsageRecordUpdated;
            
            System.Diagnostics.Debug.WriteLine("MainWindow: Tracking service events registered");

            // Handle window closing
            this.Closed += (sender, args) =>
            {
                Dispose();
            };

            // In WinUI 3, use a loaded handler directly in the constructor
            FrameworkElement root = (FrameworkElement)Content;
            root.Loaded += MainWindow_Loaded;
            
            System.Diagnostics.Debug.WriteLine("MainWindow constructor completed");

            // Initialize the date picker popup
            _datePickerPopup = new DatePickerPopup(this);
            _datePickerPopup.SingleDateSelected += DatePickerPopup_SingleDateSelected;
            _datePickerPopup.DateRangeSelected += DatePickerPopup_DateRangeSelected;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MainWindow));
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _trackingService.StopTracking();
                _updateTimer.Stop();
                _autoSaveTimer.Stop();

                // Save any unsaved records
                SaveRecordsToDatabase();
                
                // Clear collections
                _usageRecords.Clear();
                
                // Dispose tracking service
                _trackingService.Dispose();
                _databaseService?.Dispose();
                
                // Remove event handlers
                _updateTimer.Tick -= UpdateTimer_Tick;
                _trackingService.UsageRecordUpdated -= TrackingService_UsageRecordUpdated;
                _autoSaveTimer.Tick -= AutoSaveTimer_Tick;

                _disposed = true;
            }
        }

        // Add a counter for timer ticks to control periodic chart refreshes
        private int _timerTickCounter = 0;

        private void UpdateTimer_Tick(object? sender, object e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Timer tick - updating durations");
                _timerTickCounter++;
                
                bool didUpdate = false;
                
                // Get the focused record first
                var focusedRecord = _usageRecords.FirstOrDefault(r => r.IsFocused);
                if (focusedRecord != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Updating focused record: {focusedRecord.ProcessName}");
                    
                    // Get duration before update
                    var prevDuration = focusedRecord.Duration;
                    
                    // Update the duration
                    focusedRecord.UpdateDuration();
                    
                    // Check if duration changed meaningfully (by at least 0.1 second)
                    if ((focusedRecord.Duration - prevDuration).TotalSeconds >= 0.1)
                    {
                        didUpdate = true;
                    }
                }
                
                // Always update the UI on regular intervals to ensure chart is refreshed,
                // even if no focused app duration changed
                if (didUpdate || _timerTickCounter >= 5) // Force update every ~5 seconds
                {
                    System.Diagnostics.Debug.WriteLine($"Updating UI (didUpdate={didUpdate}, tickCounter={_timerTickCounter})");
                    
                    // Reset counter if we're updating
                    if (_timerTickCounter >= 5)
                    {
                        _timerTickCounter = 0;
                    }
                    
                    // Update summary and chart
                    UpdateSummaryTab();
                    UpdateUsageChart();
                    
                    // If we haven't had any updates for a while, force a chart refresh
                    if (!didUpdate && _timerTickCounter == 0)
                    {
                        ForceChartRefresh();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in timer tick: {ex}");
            }
        }

        private void TrackingService_UsageRecordUpdated(object? sender, AppUsageRecord record)
        {
            try
            {
                // Process the application record to improve its naming
                ApplicationProcessingHelper.ProcessApplicationRecord(record);

                // Additional check to make sure we filter out windows system processes
                if (!record.ShouldTrack || IsWindowsSystemProcess(record.ProcessName)) 
                {
                    System.Diagnostics.Debug.WriteLine($"Ignoring system process: {record.ProcessName}");
                    return;
                }
                
                if (!record.IsFromDate(_selectedDate)) 
                {
                    System.Diagnostics.Debug.WriteLine($"Ignoring record from different date: {record.Date}, selected date: {_selectedDate}");
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    System.Diagnostics.Debug.WriteLine($"UI Update: Processing record for: {record.ProcessName} ({record.WindowTitle})");

                    // Track if we made any changes that require UI updates
                    bool recordsChanged = false;

                    // First try to find exact match
                    var existingRecord = FindExistingRecord(record);

                    if (existingRecord != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Found existing record: {existingRecord.ProcessName}");
                        
                        // Update the existing record instead of adding a new one
                        if (existingRecord.IsFocused != record.IsFocused)
                        {
                            existingRecord.SetFocus(record.IsFocused);
                            recordsChanged = true;
                        }

                        // If the window title of the existing record is empty, use the new one
                        if (string.IsNullOrEmpty(existingRecord.WindowTitle) && !string.IsNullOrEmpty(record.WindowTitle))
                        {
                            existingRecord.WindowTitle = record.WindowTitle;
                        }

                        // If we're updating the active status, make sure we unfocus any other records
                        if (record.IsFocused)
                        {
                            foreach (var otherRecord in _usageRecords.Where(r => r != existingRecord && r.IsFocused))
                            {
                                otherRecord.SetFocus(false);
                                recordsChanged = true;
                            }
                        }
                    }
                    else
                    {
                        // Additional check before adding to make sure it's not a system process
                        if (IsWindowsSystemProcess(record.ProcessName))
                        {
                            System.Diagnostics.Debug.WriteLine($"Skipping system process: {record.ProcessName}");
                            return;
                        }

                        System.Diagnostics.Debug.WriteLine($"Adding new record: {record.ProcessName}");
                        
                        // If we're adding a new focused record, unfocus all other records
                        if (record.IsFocused)
                        {
                            foreach (var otherRecord in _usageRecords.Where(r => r.IsFocused))
                            {
                                otherRecord.SetFocus(false);
                            }
                        }

                        _usageRecords.Add(record);
                        recordsChanged = true;
                        System.Diagnostics.Debug.WriteLine($"Record added to _usageRecords, collection count: {_usageRecords.Count}");
                        System.Diagnostics.Debug.WriteLine($"Added record details: Process={record.ProcessName}, Duration={record.Duration.TotalSeconds:F1}s, Start={record.StartTime}, IsFocused={record.IsFocused}");
                        
                        // Log full collection details for troubleshooting
                        System.Diagnostics.Debug.WriteLine("Current _usageRecords collection:");
                        foreach (var r in _usageRecords.Take(5)) // Show first 5 records
                        {
                            System.Diagnostics.Debug.WriteLine($"  - {r.ProcessName}: {r.Duration.TotalSeconds:F1}s, IsFocused={r.IsFocused}");
                        }
                        if (_usageRecords.Count > 5)
                        {
                            System.Diagnostics.Debug.WriteLine($"  - ... and {_usageRecords.Count - 5} more records");
                        }
                        
                        // Make sure the list view is actually showing the new record
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            // Ensure the UsageListView has the updated data
                            if (UsageListView != null)
                            {
                                System.Diagnostics.Debug.WriteLine("Manually refreshing UsageListView");
                                UsageListView.ItemsSource = null;
                                UsageListView.ItemsSource = _usageRecords;
                            }
                        });
                    }

                    // Only update the UI if we made changes
                    if (recordsChanged)
                    {
                        // Update the summary and chart in real-time
                        System.Diagnostics.Debug.WriteLine("Updating summary and chart in real-time");
                        UpdateSummaryTab();
                        UpdateUsageChart();
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error handling usage record update: {ex.Message}");
            }
        }

        private AppUsageRecord? FindExistingRecord(AppUsageRecord record)
        {
            if (record == null)
                return null;

            // Check existing records for a match based on process name and window title
            foreach (var r in _usageRecords)
            {
                // If it's the exact same window, return it
                if (r.WindowHandle == record.WindowHandle)
                    return r;

                // For applications that should be consolidated, look for matching process names
                string baseAppName = ApplicationProcessingHelper.GetBaseAppName(record.ProcessName);
                if (!string.IsNullOrEmpty(baseAppName) && 
                    ApplicationProcessingHelper.GetBaseAppName(r.ProcessName).Equals(baseAppName, StringComparison.OrdinalIgnoreCase))
                {
                    // For applications we want to consolidate, just match on process name
                    if (ApplicationProcessingHelper.IsApplicationThatShouldConsolidate(record.ProcessName))
                        return r;
                    
                    // For other applications, match on process name + check if they're related processes
                    if (r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                       ApplicationProcessingHelper.IsAlternateProcessNameForSameApp(r.ProcessName, record.ProcessName))
                    {
                        // If window titles are similar, consider it the same application
                        if (ApplicationProcessingHelper.IsSimilarWindowTitle(r.WindowTitle, record.WindowTitle))
                            return r;
                    }
                }
            }

            // No match found
            return null;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartTracking();
        }
        
        private void StartTracking()
        {
            ThrowIfDisposed();
            
            try
            {
                System.Diagnostics.Debug.WriteLine("Starting tracking");

                // Start tracking the current window
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
            StopButton.IsEnabled = true;
            
                // Start timer for duration updates
                _updateTimer.Start();
                System.Diagnostics.Debug.WriteLine("Update timer started");
                
                // Start auto-save timer
                _autoSaveTimer.Start();
                System.Diagnostics.Debug.WriteLine("Auto-save timer started");
                
                // Update the chart immediately to show the initial state
                UpdateUsageChart();
                System.Diagnostics.Debug.WriteLine("Initial chart update called");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error starting tracking: {ex.Message}");
                
                // Display error dialog
                var dialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Failed to start tracking: {ex.Message}",
                    CloseButtonText = "OK"
                };

                dialog.XamlRoot = this.Content.XamlRoot;
                _ = dialog.ShowAsync();
            }
        }

        private void LoadRecordsForDate(DateTime date)
        {
            System.Diagnostics.Debug.WriteLine($"Loading records for date: {date}");
            
            _selectedDate = date;
            _selectedEndDate = null;
            _isDateRangeSelected = false;
            List<AppUsageRecord> records = new List<AppUsageRecord>();
            
            try
            {
                // Clear existing records
                _usageRecords.Clear();
                
                // Update date display with selected date
                if (DateDisplay != null)
                {
                    // Format date display - use "Today", "Yesterday", or date without year
                    var today = DateTime.Today;
                    var yesterday = today.AddDays(-1);
                    
                    if (date == today)
                    {
                        DateDisplay.Text = "Today";
                    }
                    else if (date == yesterday)
                    {
                        DateDisplay.Text = "Yesterday";
                    }
                    else
                    {
                        // Format without year for cleaner display
                        DateDisplay.Text = date.ToString("MMMM d");
                    }
                }
                
                // Load records from the database based on current time period
                switch (_currentTimePeriod)
                {
                    case TimePeriod.Weekly:
                        // Get records for the week containing the selected date
                        var startOfWeek = date.AddDays(-(int)date.DayOfWeek);
                        var endOfWeek = startOfWeek.AddDays(6);
                        records = GetAggregatedRecordsForDateRange(startOfWeek, endOfWeek);
                        
                        // Update date display with week range (no year)
                        if (DateDisplay != null)
                        {
                            DateDisplay.Text = $"{startOfWeek:MMM d} - {endOfWeek:MMM d}";
                        }
                        
                        // Update chart title
                        SummaryTitle.Text = "Weekly Screen Time Summary";
                        
                        // Show daily average for weekly view
                        AveragePanel.Visibility = Visibility.Visible;
                        break;
                        
                    case TimePeriod.Daily:
                    default:
                        // Get records for the selected date
                        if (_databaseService != null)
                        {
                            records = _databaseService.GetRecordsForDate(date);
                        }
                        else
                        {
                            // Fallback to tracking service records if database is not available
                            records = _trackingService.GetRecords()
                                .Where(r => r.IsFromDate(date))
                                .ToList();
                        }
                        
                        // Update chart title
                        SummaryTitle.Text = "Daily Screen Time Summary";
                        
                        // Hide daily average for daily view
                        AveragePanel.Visibility = Visibility.Collapsed;
                        break;
                }
                
                // Sort records by duration (descending)
                var sortedRecords = records.OrderByDescending(r => r.Duration).ToList();
                
                // Add sorted records to the observable collection
                foreach (var record in sortedRecords)
                {
                    _usageRecords.Add(record);
                }
                
                // Update the summary tab
                UpdateSummaryTab();
                
                // Update chart based on current view mode
                UpdateChartViewMode();
                
                System.Diagnostics.Debug.WriteLine($"Loaded {records.Count} records");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
        }
        
        private List<AppUsageRecord> GetAggregatedRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            // This method aggregates records across multiple dates
            var result = new List<AppUsageRecord>();
            
            try
            {
                // Get the raw usage data from database
                if (_databaseService != null)
                {
                    var usageData = _databaseService.GetUsageReportForDateRange(startDate, endDate);
                    
                    // Convert the tuples to AppUsageRecord objects
                    foreach (var (processName, duration) in usageData)
                    {
                        if (!IsWindowsSystemProcess(processName))
                        {
                            var record = AppUsageRecord.CreateAggregated(processName, startDate);
                            record._accumulatedDuration = duration;
                            result.Add(record);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting aggregated records: {ex.Message}");
            }
            
            return result;
        }
        
        private void UpdateUsageChart()
        {
            // Call the ChartHelper method to update the chart
            TimeSpan totalTime = ChartHelper.UpdateUsageChart(
                UsageChartLive, 
                _usageRecords, 
                _currentChartViewMode, 
                _currentTimePeriod, 
                _selectedDate, 
                _selectedEndDate);
                
            // Update the chart time display
            ChartTimeValue.Text = ChartHelper.FormatTimeSpan(totalTime);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            ThrowIfDisposed();

            try
            {
                // Stop tracking
                System.Diagnostics.Debug.WriteLine("Stopping tracking");
            _trackingService.StopTracking();

                // Stop UI updates and auto-save
                _updateTimer.Stop();
                _autoSaveTimer.Stop();

                // Update UI state
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;

                // Save all active records to the database
                SaveRecordsToDatabase();

                // Cleanup any system processes one last time
                CleanupSystemProcesses();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error stopping tracking: {ex.Message}");
                // Display error dialog
                var dialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Failed to stop tracking: {ex.Message}",
                    CloseButtonText = "OK"
                };

                dialog.XamlRoot = this.Content.XamlRoot;
                _ = dialog.ShowAsync();
            }
        }

        private void SaveRecordsToDatabase()
        {
            // Skip saving if database is not available
            if (_databaseService == null) return;

            try
            {
                // Save each record that's from today and not a system process
                foreach (var record in _usageRecords.Where(r =>
                    r.IsFromDate(DateTime.Now.Date) &&
                    !IsWindowsSystemProcess(r.ProcessName) &&
                    r.Duration.TotalSeconds > 0))
                {
                    System.Diagnostics.Debug.WriteLine($"Saving record: {record.ProcessName}, Duration: {record.Duration}");

                    // Make sure focus is turned off to finalize duration
                    if (record.IsFocused)
            {
                record.SetFocus(false);
                    }

                    // If record has an ID greater than 0, it was loaded from the database
                    if (record.Id > 0)
                    {
                        _databaseService.UpdateRecord(record);
                    }
                    else
                    {
                        _databaseService.SaveRecord(record);
                    }
                }

                System.Diagnostics.Debug.WriteLine("Records saved to database");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving records: {ex.Message}");

                // Show error dialog
                var dialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Failed to save records: {ex.Message}",
                    CloseButtonText = "OK"
                };

                dialog.XamlRoot = this.Content.XamlRoot;
                _ = dialog.ShowAsync();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            _windowHelper.MinimizeWindow();
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            _windowHelper.MaximizeOrRestoreWindow();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _trackingService.StopTracking();
            _windowHelper.CloseWindow();
        }

        private void UsageListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is AppUsageRecord record)
            {
                System.Diagnostics.Debug.WriteLine($"Container changing for {record.ProcessName}, has icon: {record.AppIcon != null}");

                // Get the container and find the UI elements
                if (args.ItemContainer?.ContentTemplateRoot is Grid grid)
                {
                    var placeholderIcon = grid.FindName("PlaceholderIcon") as FontIcon;
                    var appIconImage = grid.FindName("AppIconImage") as Microsoft.UI.Xaml.Controls.Image;

                    if (placeholderIcon != null && appIconImage != null)
                    {
                        // Update visibility based on whether the app icon is loaded
                        UpdateIconVisibility(record, placeholderIcon, appIconImage);

                        // Store the control references in tag for property changed event
                        if (args.ItemContainer.Tag == null)
                        {
                            // Only add the event handler once
                            args.ItemContainer.Tag = true;

                            record.PropertyChanged += (s, e) =>
                            {
                                if (e.PropertyName == nameof(AppUsageRecord.AppIcon))
                                {
                                    System.Diagnostics.Debug.WriteLine($"AppIcon property changed for {record.ProcessName}");
                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        if (grid != null && placeholderIcon != null && appIconImage != null)
                                        {
                                            UpdateIconVisibility(record, placeholderIcon, appIconImage);
                                        }
                                    });
                                }
                            };
                        }
                    }
                }

                // Request icon to load
                if (record.AppIcon == null)
                {
                    record.LoadAppIconIfNeeded();
                }

                // Register for phase-based callback to handle deferred loading
                if (args.Phase == 0)
                {
                    args.RegisterUpdateCallback(UsageListView_ContainerContentChanging);
                }
            }

            // Increment the phase
            args.Handled = true;
        }

        private void UpdateIconVisibility(AppUsageRecord record, FontIcon placeholder, Microsoft.UI.Xaml.Controls.Image iconImage)
        {
            if (record.AppIcon != null)
            {
                System.Diagnostics.Debug.WriteLine($"Setting icon visible for {record.ProcessName}");
                placeholder.Visibility = Visibility.Collapsed;
                iconImage.Visibility = Visibility.Visible;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Setting placeholder visible for {record.ProcessName}");
                placeholder.Visibility = Visibility.Visible;
                iconImage.Visibility = Visibility.Collapsed;
            }
        }

        private bool IsWindowsSystemProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;

            // Common Windows system process names we want to ignore
            string[] systemProcesses = {
                "explorer",
                "SearchHost",
                "ShellExperienceHost",
                "StartMenuExperienceHost",
                "ApplicationFrameHost",
                "SystemSettings",
                "TextInputHost",
                "WindowsTerminal",
                "cmd",
                "powershell",
                "pwsh",
                "conhost",
                "WinStore.App",
                "LockApp",
                "LogonUI",
                "fontdrvhost",
                "dwm",
                "csrss",
                "services",
                "svchost",
                "taskhostw",
                "ctfmon",
                "rundll32",
                "dllhost",
                "sihost",
                "taskmgr",
                "backgroundtaskhost",
                "smartscreen",
                "SecurityHealthService",
                "Registry",
                "MicrosoftEdgeUpdate",
                "WmiPrvSE",
                "spoolsv",
                "TabTip",
                "TabTip32",
                "SearchUI",
                "SearchApp",
                "RuntimeBroker",
                "SettingsSyncHost",
                "WUDFHost"
            };

            // Check if the processName is in our list
            return systemProcesses.Contains(processName.ToLower());
        }

        private void AutoSaveTimer_Tick(object? sender, object e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Auto-save timer tick - saving records");

                // Save all active records to the database
                SaveRecordsToDatabase();

                // Clean up any system processes one last time
                CleanupSystemProcesses();
                
                // If viewing today's data, reload from database to ensure UI is in sync
                if (_selectedDate.Date == DateTime.Now.Date && _currentTimePeriod == TimePeriod.Daily)
                {
                    System.Diagnostics.Debug.WriteLine("Refreshing today's data after auto-save");
                    
                    // Reload data without clearing the selection
                    LoadRecordsForDate(_selectedDate);
                }
                else
                {
                    // Otherwise just update the chart and summary
                    UpdateUsageChart();
                    UpdateSummaryTab();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in auto-save timer tick: {ex}");
            }
        }

        private void UpdateSummaryTab()
        {
            try
            {
                // Get total screen time
                TimeSpan totalTime = TimeSpan.Zero;
                
                // Find most used app
                AppUsageRecord? mostUsedApp = null;
                
                // Calculate total time and find most used app
                foreach (var record in _usageRecords)
                {
                    totalTime += record.Duration;
                    
                    if (mostUsedApp == null || record.Duration > mostUsedApp.Duration)
                    {
                        mostUsedApp = record;
                    }
                }
                
                // Update total time display
                TotalScreenTime.Text = FormatTimeSpan(totalTime);
                
                // Update most used app
                if (mostUsedApp != null && mostUsedApp.Duration.TotalSeconds > 0)
                {
                    MostUsedApp.Text = mostUsedApp.ProcessName;
                    MostUsedAppTime.Text = FormatTimeSpan(mostUsedApp.Duration);
                    
                    // Update the icon for most used app
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
                        
                        // Try to load the icon if it's not already loaded
                        mostUsedApp.LoadAppIconIfNeeded();
                        mostUsedApp.PropertyChanged += (s, e) =>
                        {
                            if (e.PropertyName == nameof(AppUsageRecord.AppIcon) && mostUsedApp.AppIcon != null)
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    MostUsedAppIcon.Source = mostUsedApp.AppIcon;
                                    MostUsedAppIcon.Visibility = Visibility.Visible;
                                    MostUsedPlaceholderIcon.Visibility = Visibility.Collapsed;
                                });
                            }
                        };
                    }
                }
                else
                {
                    MostUsedApp.Text = "None";
                    MostUsedAppTime.Text = "0h 0m";
                    MostUsedAppIcon.Visibility = Visibility.Collapsed;
                    MostUsedPlaceholderIcon.Visibility = Visibility.Visible;
                }
                
                // Calculate daily average for weekly/monthly views
                if (_currentTimePeriod != TimePeriod.Daily && AveragePanel != null)
                {
                    int dayCount = GetDayCountForTimePeriod(_currentTimePeriod, _selectedDate);
                    if (dayCount > 0)
                    {
                        TimeSpan averageTime = TimeSpan.FromTicks(totalTime.Ticks / dayCount);
                        DailyAverage.Text = FormatTimeSpan(averageTime);
                    }
                    else
                    {
                        DailyAverage.Text = "0h 0m";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating summary tab: {ex.Message}");
            }
        }
        
        private string FormatTimeSpan(TimeSpan time)
        {
            return ChartHelper.FormatTimeSpan(time);
        }

        // New method to handle initialization after window is loaded
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("MainWindow_Loaded called");
                
                // Initialize UI elements
                SetUpUiElements();
                
                // Set today's date and update button text
                _selectedDate = DateTime.Today;
                _currentTimePeriod = TimePeriod.Daily; // Default to daily view
                UpdateDatePickerButtonText();
                
                // Set the selected date display
                if (DateDisplay != null)
                {
                    DateDisplay.Text = "Today";
                }
                
                // Load today's records
                LoadRecordsForDate(_selectedDate);
                
                // Set up the UsageListView
                if (UsageListView != null && UsageListView.ItemsSource == null)
                {
                    UsageListView.ItemsSource = _usageRecords;
                }
                
                // Check if this is the first run
                CheckFirstRun();
                
                // Clean up system processes that shouldn't be tracked
                CleanupSystemProcesses();
                
                // Set the initial chart view mode
                _currentChartViewMode = ChartViewMode.Hourly;
                
                // Update view mode label
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = "Hourly View";
                    }
                });
                
                // Start tracking automatically
                StartTracking();
                
                System.Diagnostics.Debug.WriteLine("MainWindow_Loaded completed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in MainWindow_Loaded: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
        }

        // Move UI initialization to a separate method
        private void SetUpUiElements()
        {
            // Initialize the date button
            UpdateDatePickerButtonText();

            // Initialize tracking start/stop buttons
            UpdateTrackingButtonsState();

            // Configure timer for duration updates (already initialized in constructor)
            _updateTimer.Interval = TimeSpan.FromSeconds(1);
            _updateTimer.Tick += UpdateTimer_Tick;

            // Configure auto-save timer (already initialized in constructor)
            _autoSaveTimer.Interval = TimeSpan.FromMinutes(5);
            _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        }

        // New method to handle initialization after window is loaded
        private void CheckFirstRun()
        {
            // Welcome message has been removed as requested
            System.Diagnostics.Debug.WriteLine("First run check - welcome message disabled");
        }

        // New method to handle initialization after window is loaded
        private void UpdateTrackingButtonsState()
        {
            if (_trackingService != null)
            {
                StartButton.IsEnabled = !_trackingService.IsTracking;
                StopButton.IsEnabled = _trackingService.IsTracking;
            }
        }

        // New method to handle initialization after window is loaded
        private void TrackingService_WindowChanged(object? sender, EventArgs e)
        {
            // This handles window change events from the tracking service
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // Update the UI based on current tracking data
                    if (_trackingService.CurrentRecord != null && !IsWindowsSystemProcess(_trackingService.CurrentRecord.ProcessName))
                    {
                        System.Diagnostics.Debug.WriteLine($"Window changed to: {_trackingService.CurrentRecord.ProcessName} - {_trackingService.CurrentRecord.WindowTitle}");
                        
                        // For now, we don't have CurrentAppTextBlock or CurrentDurationTextBlock in our UI
                        // Instead, we'll update the chart and summary with the latest data
                        UpdateUsageChart();
                        UpdateSummaryTab();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error updating UI on window change: {ex.Message}");
                }
            });
        }

        private int GetDayCountForTimePeriod(TimePeriod period, DateTime date)
        {
            switch (period)
            {
                case TimePeriod.Weekly:
                    // A week has 7 days
                    return 7;
                
                case TimePeriod.Daily:
                default:
                    // Daily view is just 1 day
                    return 1;
            }
        }

        private void UpdateChartViewMode()
        {
            // Adjust chart view mode automatically based on time period
            if (_currentTimePeriod == TimePeriod.Daily)
            {
                _currentChartViewMode = ChartViewMode.Hourly;
                
                // Update view mode label
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = "Hourly View";
                    }
                });
            }
            else // Weekly
            {
                _currentChartViewMode = ChartViewMode.Daily;
                
                // Update view mode label
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = "Daily View";
                    }
                });
            }
            
            // Update the chart
            UpdateUsageChart();
        }

        // Add a method to force a chart refresh - useful for debugging and ensuring chart gets updated
        private void ForceChartRefresh()
        {
            // Call the ChartHelper method to force refresh the chart
            TimeSpan totalTime = ChartHelper.ForceChartRefresh(
                UsageChartLive, 
                _usageRecords, 
                _currentChartViewMode, 
                _currentTimePeriod, 
                _selectedDate, 
                _selectedEndDate);
                
            // Update the chart time display
            ChartTimeValue.Text = ChartHelper.FormatTimeSpan(totalTime);
        }

        private void DatePickerButton_Click(object sender, RoutedEventArgs e)
        {
            // Use the DatePickerPopup helper to show the date picker
            _datePickerPopup?.ShowDatePicker(
                sender, 
                _selectedDate, 
                _selectedEndDate, 
                _isDateRangeSelected);
        }
        
        // Event handlers for DatePickerPopup events
        private void DatePickerPopup_SingleDateSelected(object? sender, DateTime selectedDate)
        {
            _selectedDate = selectedDate;
            _isDateRangeSelected = false;
            _selectedEndDate = null;
            
            // Update button text
            UpdateDatePickerButtonText();
            
            // Load records for the selected date
            LoadRecordsForDate(_selectedDate);
            
            // Adjust the chart view mode based on whether this is a today/yesterday vs. other date
            UpdateChartViewMode();
        }
        
        private void DatePickerPopup_DateRangeSelected(object? sender, (DateTime Start, DateTime End) dateRange)
        {
            _selectedDate = dateRange.Start;
            _selectedEndDate = dateRange.End;
            _isDateRangeSelected = true;
            
            // Update button text
            UpdateDatePickerButtonText();
            
            // Load records for the date range
            LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
        }
        
        private void UpdateDatePickerButtonText()
        {
            try
            {
                if (DatePickerButton != null)
                {
                    // First check if we're in any of the quick select modes
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
                    
                    // Special case for Last 7 days
                    if (_selectedDate == today && _isDateRangeSelected && 
                        _currentTimePeriod == TimePeriod.Weekly)
                    {
                        DatePickerButton.Content = "Last 7 days";
                        return;
                    }
                    
                    // For single date selection
                    if (!_isDateRangeSelected)
                    {
                        DatePickerButton.Content = _selectedDate.ToString("MMM dd");
                    }
                    // For date range selection
                    else if (_selectedEndDate.HasValue)
                    {
                        DatePickerButton.Content = $"{_selectedDate:MMM dd} - {_selectedEndDate:MMM dd}";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateDatePickerButtonText: {ex.Message}");
                // Set a fallback value
                if (DatePickerButton != null)
                {
                    DatePickerButton.Content = _selectedDate.ToString("MMM dd");
                }
            }
        }
        
        private void LoadRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            System.Diagnostics.Debug.WriteLine($"Loading records for date range: {startDate} to {endDate}");
            
            _selectedDate = startDate;
            _selectedEndDate = endDate;
            List<AppUsageRecord> records = new List<AppUsageRecord>();
            
            try
            {
                // Clear existing records
                _usageRecords.Clear();
                
                // Update date display with selected date range
                if (DateDisplay != null)
                {
                    // Format without year for cleaner display
                    DateDisplay.Text = $"{startDate:MMM d} - {endDate:MMM d}";
                }
                
                // Get aggregated records for the date range
                records = GetAggregatedRecordsForDateRange(startDate, endDate);
                
                // Update chart title
                SummaryTitle.Text = "Screen Time Summary";
                
                // Show daily average for date range view
                AveragePanel.Visibility = Visibility.Visible;
                
                // Sort records by duration (descending)
                var sortedRecords = records.OrderByDescending(r => r.Duration).ToList();
                
                // Add sorted records to the observable collection
                foreach (var record in sortedRecords)
                {
                    _usageRecords.Add(record);
                }
                
                // Update the summary tab
                UpdateSummaryTab();
                
                // Update chart based on current view mode
                _currentChartViewMode = ChartViewMode.Daily; // Force daily chart for range
                UpdateChartViewMode();
                
                System.Diagnostics.Debug.WriteLine($"Loaded {records.Count} records for date range");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records for date range: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
        }

        private void CleanupSystemProcesses()
        {
            // Create a list of records to remove (can't modify collection while enumerating)
            var recordsToRemove = _usageRecords
                .Where(r => IsWindowsSystemProcess(r.ProcessName))
                .ToList();

            // Remove each system process from the collection
            foreach (var record in recordsToRemove)
            {
                _usageRecords.Remove(record);
            }
        }
    }
}


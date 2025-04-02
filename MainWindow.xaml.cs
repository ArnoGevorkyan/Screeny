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
        
        // Counter for auto-save cycles to run database maintenance periodically
        private int _autoSaveCycleCount = 0;
        
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

                // Get the LIVE focused application from the tracking service
                var liveFocusedApp = _trackingService.CurrentRecord;
                AppUsageRecord? recordToUpdate = null;

                if (liveFocusedApp != null)
                {
                    // Find if this app exists in the currently displayed list (_usageRecords)
                    // Match based on ProcessName for simplicity in aggregated views
                    recordToUpdate = _usageRecords
                        .FirstOrDefault(r => r.ProcessName.Equals(liveFocusedApp.ProcessName, StringComparison.OrdinalIgnoreCase));

                    if (recordToUpdate != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Incrementing duration for displayed record: {recordToUpdate.ProcessName}");
                        
                        // --- FIX START: Only increment if viewing today or a range including today ---
                        bool isViewingToday = _selectedDate.Date == DateTime.Today && !_isDateRangeSelected;
                        bool isViewingRangeIncludingToday = _isDateRangeSelected && _selectedEndDate.HasValue && 
                                                          DateTime.Today >= _selectedDate.Date && DateTime.Today <= _selectedEndDate.Value.Date;
                                                          
                        if (isViewingToday || isViewingRangeIncludingToday)
                        {
                            // Use the new IncrementDuration method
                            recordToUpdate.IncrementDuration(TimeSpan.FromSeconds(1));
                            didUpdate = true;
                        }
                        else
                        {
                             System.Diagnostics.Debug.WriteLine("Not incrementing duration - viewing a past date/range.");
                        }
                        // --- FIX END ---
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Live focused app {liveFocusedApp.ProcessName} not found in current view");
                    }
                }
                
                // Periodically force UI refresh even if no specific app was updated
                // to ensure chart updates correctly.
                if (didUpdate || _timerTickCounter >= 5) // Force update every ~5 seconds
                {
                    System.Diagnostics.Debug.WriteLine($"Updating UI (didUpdate={didUpdate}, tickCounter={_timerTickCounter})");

                    if (_timerTickCounter >= 5)
                    {
                        _timerTickCounter = 0;
                    }
                    
                    // Update summary and chart
                    UpdateSummaryTab();
                    UpdateUsageChart(liveFocusedApp);
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
                        
                        // Clean up any system processes
                        CleanupSystemProcesses();
                        
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
                        // --- Load records for single day view --- 
                        List<AppUsageRecord> dbRecords = new List<AppUsageRecord>();
                        if (_databaseService != null)
                        {
                            dbRecords = _databaseService.GetRecordsForDate(date);
                        }
                        else
                        {
                            // Fallback if DB service fails - unlikely but safe
                            dbRecords = _trackingService.GetRecords()
                                .Where(r => r.IsFromDate(date))
                                .ToList();
                        }
                        
                        // Aggregate records loaded FROM DATABASE
                        var aggregatedDbData = dbRecords
                            .GroupBy(r => r.ProcessName)
                            .Select(g => {
                                var totalDuration = TimeSpan.FromSeconds(g.Sum(rec => rec.Duration.TotalSeconds));
                                var aggregatedRecord = AppUsageRecord.CreateAggregated(g.Key, date);
                                aggregatedRecord._accumulatedDuration = totalDuration;
                                aggregatedRecord.LoadAppIconIfNeeded(); 
                                return aggregatedRecord;
                            })
                            .Where(ar => ar.Duration.TotalSeconds >= 1) 
                            .ToList();

                        // --- REMOVED MERGE BLOCK FOR TODAY ---
                        // Let the normal tracking service updates handle live data.
                        // The initial load will now show data persisted from the last session.
                        records = aggregatedDbData;
                        // --- END REMOVAL ---
                        
                        // Update chart title
                        SummaryTitle.Text = "Daily Screen Time Summary";
                        
                        // Hide daily average for daily view
                        AveragePanel.Visibility = Visibility.Collapsed;
                        break;
                }
                
                System.Diagnostics.Debug.WriteLine($"Retrieved {records.Count} records from database/service");
                
                // Check if we have data - if not and it's not today, show a message to the user
                if (records.Count == 0 && date.Date != DateTime.Today)
                {
                    System.Diagnostics.Debug.WriteLine("No data found for the selected date");
                    
                    // Show a message to the user
                    DispatcherQueue.TryEnqueue(async () => {
                        try {
                            ContentDialog infoDialog = new ContentDialog()
                            {
                                Title = "No Data Available",
                                Content = $"No usage data found for {DateDisplay.Text}.",
                                CloseButtonText = "OK",
                                XamlRoot = Content.XamlRoot
                            };
                            
                            await infoDialog.ShowAsync();
                        }
                        catch (Exception dialogEx) {
                            System.Diagnostics.Debug.WriteLine($"Error showing dialog: {dialogEx.Message}");
                        }
                    });
                }
                
                // Sort records by duration (descending)
                var sortedRecords = records.OrderByDescending(r => r.Duration).ToList();
                
                // Add sorted records to the observable collection
                foreach (var record in sortedRecords)
                {
                    _usageRecords.Add(record);
                }
                
                // Clean up any system processes
                CleanupSystemProcesses();
                
                // Force a refresh of the ListView
                if (UsageListView != null)
                {
                    DispatcherQueue.TryEnqueue(() => {
                        UsageListView.ItemsSource = null;
                        UsageListView.ItemsSource = _usageRecords;
                    });
                }
                
                // Update the summary tab
                UpdateSummaryTab();
                
                // Update chart based on current view mode
                UpdateChartViewMode();
                
                System.Diagnostics.Debug.WriteLine($"Successfully loaded and displayed {records.Count} records");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                
                // Show an error message to the user
                DispatcherQueue.TryEnqueue(async () => {
                    try {
                        ContentDialog errorDialog = new ContentDialog()
                        {
                            Title = "Error Loading Data",
                            Content = $"Failed to load screen time data: {ex.Message}",
                            CloseButtonText = "OK",
                            XamlRoot = Content.XamlRoot
                        };
                        
                        await errorDialog.ShowAsync();
                    }
                    catch (Exception dialogEx) {
                        System.Diagnostics.Debug.WriteLine($"Error showing dialog: {dialogEx.Message}");
                    }
                });
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
                    System.Diagnostics.Debug.WriteLine($"Getting usage data for date range {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
                    var usageData = _databaseService.GetUsageReportForDateRange(startDate, endDate);
                    System.Diagnostics.Debug.WriteLine($"Retrieved {usageData.Count} raw records from database");
                    
                    // First check if we have any non-system processes in the data
                    var nonSystemProcesses = usageData
                        .Where(item => {
                            string normalizedName = item.ProcessName.Trim().ToLowerInvariant();
                            bool isHighPrioritySystem = new[] { 
                                "explorer", "shellexperiencehost", "searchhost", 
                                "dwm", "runtimebroker", "svchost" 
                            }.Any(p => normalizedName.Contains(p));
                            
                            // Keep it if it's not a high-priority system process and has meaningful duration
                            return !isHighPrioritySystem && item.TotalDuration.TotalSeconds >= 5;
                        })
                        .ToList();
                        
                    System.Diagnostics.Debug.WriteLine($"Found {nonSystemProcesses.Count} non-system processes with meaningful duration");
                    
                    // If we don't have any meaningful non-system processes, be more lenient
                    if (nonSystemProcesses.Count == 0)
                    {
                        System.Diagnostics.Debug.WriteLine("No significant non-system processes found, using lenient filtering");
                        
                        // Convert all processes with some meaningful duration
                        foreach (var (processName, duration) in usageData)
                        {
                            // Only filter out very short duration processes
                            if (duration.TotalSeconds >= 2)
                            {
                                var record = AppUsageRecord.CreateAggregated(processName, startDate);
                                record._accumulatedDuration = duration;
                                result.Add(record);
                                System.Diagnostics.Debug.WriteLine($"Added (lenient mode): {processName} - {duration.TotalMinutes:F1} minutes");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"Filtered out short duration: {processName} - {duration.TotalSeconds:F1} seconds");
                            }
                        }
                    }
                    else
                    {
                        // Normal case - convert non-system processes to records
                        foreach (var (processName, duration) in nonSystemProcesses)
                        {
                            var record = AppUsageRecord.CreateAggregated(processName, startDate);
                            record._accumulatedDuration = duration;
                            result.Add(record);
                            System.Diagnostics.Debug.WriteLine($"Added record: {processName} - {duration.TotalMinutes:F1} minutes");
                        }
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Database service is not available - cannot get records for date range");
                }
                
                System.Diagnostics.Debug.WriteLine($"GetAggregatedRecordsForDateRange: Found {result.Count} valid records after filtering");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting aggregated records: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
            
            return result;
        }
        
        private void UpdateUsageChart(AppUsageRecord? liveFocusedRecord = null)
        {
            // Call the ChartHelper method to update the chart, passing the live record
            TimeSpan totalTime = ChartHelper.UpdateUsageChart(
                UsageChartLive, 
                _usageRecords, 
                _currentChartViewMode, 
                _currentTimePeriod, 
                _selectedDate, 
                _selectedEndDate,
                liveFocusedRecord);
                
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

            // Normalize the process name (trim and convert to lowercase)
            string normalizedName = processName.Trim().ToLowerInvariant();
            
            // List of high-priority system processes that should ALWAYS be filtered out
            // regardless of duration or view mode
            string[] highPriorityFilterProcesses = {
                "explorer",
                "shellexperiencehost", 
                "searchhost",
                "startmenuexperiencehost",
                "applicationframehost",
                "systemsettings",
                "dwm",
                "winlogon",
                "csrss",
                "services",
                "svchost",
                "runtimebroker",
            };
            
            // Check high-priority list first (these are always filtered)
            if (highPriorityFilterProcesses.Any(p => normalizedName.Contains(p)))
            {
                System.Diagnostics.Debug.WriteLine($"Filtering high-priority system process: {processName}");
                return true;
            }

            // Broader list of system processes that might be filtered depending on context
            string[] systemProcesses = {
                "textinputhost",
                "windowsterminal",
                "cmd",
                "powershell",
                "pwsh",
                "conhost",
                "winstore.app",
                "lockapp",
                "logonui",
                "fontdrvhost",
                "taskhostw",
                "ctfmon",
                "rundll32",
                "dllhost",
                "sihost",
                "taskmgr",
                "backgroundtaskhost",
                "smartscreen",
                "securityhealthservice",
                "registry",
                "microsoftedgeupdate",
                "wmiprvse",
                "spoolsv",
                "tabtip",
                "tabtip32",
                "searchui",
                "searchapp",
                "settingssynchost",
                "wudfhost"
            };

            // Return true if in the general system process list
            return systemProcesses.Contains(normalizedName);
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
                
                // Increment counter and run database maintenance every 12 cycles (approximately once per hour)
                _autoSaveCycleCount++;
                if (_autoSaveCycleCount >= 12 && _databaseService != null)
                {
                    _autoSaveCycleCount = 0;
                    System.Diagnostics.Debug.WriteLine("Running periodic database maintenance");
                    
                    // Run maintenance in background thread to avoid blocking UI
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
                
                // Perform database maintenance on startup (in background)
                if (_databaseService != null)
                {
                    Task.Run(() => 
                    {
                        try
                        {
                            // This will perform integrity checks and optimization
                            bool integrityPassed = _databaseService.PerformDatabaseMaintenance();
                            System.Diagnostics.Debug.WriteLine($"Database maintenance completed. Integrity passed: {integrityPassed}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Database maintenance error: {ex.Message}");
                        }
                    });
                }
                
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
                
                // Update view mode label and hide toggle panel (since we start with Today view)
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = "Hourly View";
                    }
                    
                    // Hide the view mode panel (user can't change the view for Today)
                    if (ViewModePanel != null)
                    {
                        ViewModePanel.Visibility = Visibility.Collapsed;
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
            // Get today and yesterday dates for comparison
            var today = DateTime.Today;
            var yesterday = today.AddDays(-1);
            
            // Check if this is a "Last 7 days" selection
            bool isLast7Days = _isDateRangeSelected && _selectedDate == today.AddDays(-6) && _selectedEndDate == today;
            
            // Force specific view modes based on selection
            if ((_selectedDate == today || _selectedDate == yesterday) && !_isDateRangeSelected)
            {
                // Today or Yesterday: Force Hourly view
                _currentChartViewMode = ChartViewMode.Hourly;
                
                // Update view mode label and hide toggle panel (since it can't be changed)
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = "Hourly View";
                    }
                    
                    // Hide the view mode panel (user can't change the view)
                    if (ViewModePanel != null)
                    {
                        ViewModePanel.Visibility = Visibility.Collapsed;
                    }
                });
            }
            else if (isLast7Days)
            {
                // Last 7 days: Force Daily view
                _currentChartViewMode = ChartViewMode.Daily;
                
                // Update view mode label and hide toggle panel (since it can't be changed)
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = "Daily View";
                    }
                    
                    // Hide the view mode panel (user can't change the view)
                    if (ViewModePanel != null)
                    {
                        ViewModePanel.Visibility = Visibility.Collapsed;
                    }
                });
            }
            else
            {
                // Default behavior based on time period for other selections
                if (_currentTimePeriod == TimePeriod.Daily)
                {
                    _currentChartViewMode = ChartViewMode.Hourly;
                    
                    // Update view mode label
                    DispatcherQueue.TryEnqueue(() => {
                        if (ViewModeLabel != null)
                        {
                            ViewModeLabel.Text = "Hourly View";
                        }
                        
                        // Show the view mode panel (user can change the view)
                        if (ViewModePanel != null)
                        {
                            ViewModePanel.Visibility = Visibility.Visible;
                        }
                    });
                }
                else // Weekly or Custom
                {
                    _currentChartViewMode = ChartViewMode.Daily;
                    
                    // Update view mode label
                    DispatcherQueue.TryEnqueue(() => {
                        if (ViewModeLabel != null)
                        {
                            ViewModeLabel.Text = "Daily View";
                        }
                        
                        // Show the view mode panel (user can change the view)
                        if (ViewModePanel != null)
                        {
                            ViewModePanel.Visibility = Visibility.Visible;
                        }
                    });
                }
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
            _selectedEndDate = null;
            _isDateRangeSelected = false;
            
            // Update button text
            UpdateDatePickerButtonText();
            
            // Special handling for Today vs. other days
            var today = DateTime.Today;
            if (_selectedDate == today)
            {
                // For "Today", use the current tracking settings
                _currentTimePeriod = TimePeriod.Daily;
                _currentChartViewMode = ChartViewMode.Hourly;
                
                // Update view mode label and hide toggle panel
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = "Hourly View";
                    }
                    
                    // Hide the view mode panel (user can't change the view)
                    if (ViewModePanel != null)
                    {
                        ViewModePanel.Visibility = Visibility.Collapsed;
                    }
                });
                
                // Load records for today immediately and with normal priority
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => {
                    // Show loading indicator
                    if (LoadingIndicator != null)
                    {
                        LoadingIndicator.Visibility = Visibility.Visible;
                    }

                    // Load the data immediately
                    LoadRecordsForDate(_selectedDate);
                    
                    // Hide loading indicator
                    if (LoadingIndicator != null)
                    {
                        LoadingIndicator.Visibility = Visibility.Collapsed;
                    }
                });
            }
            else
            {
                // Ensure we switch to Daily period when selecting a single past date
                _currentTimePeriod = TimePeriod.Daily;
                
                // For other single dates, use the normal update logic with a delay
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => {
                    // Show loading indicator
                    if (LoadingIndicator != null)
                    {
                        LoadingIndicator.Visibility = Visibility.Visible;
                    }
                    
                    // Use a delay to allow UI to show loading indicator
                    DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () => {
                        // Small delay to give UI time to update
                        await Task.Delay(100);
                        
                        // Load the data
                        LoadRecordsForDate(_selectedDate);
                        UpdateChartViewMode();
                        
                        // Hide loading indicator
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Collapsed;
                        }
                    });
                });
            }
        }
        
        private void DatePickerPopup_DateRangeSelected(object? sender, (DateTime Start, DateTime End) dateRange)
        {
            _selectedDate = dateRange.Start;
            _selectedEndDate = dateRange.End;
            _isDateRangeSelected = true;
            
            // Update button text
            UpdateDatePickerButtonText();
            
            // For Last 7 days, ensure we're in Weekly time period and force Daily view
            var today = DateTime.Today;
            if (_selectedDate == today.AddDays(-6) && _selectedEndDate == today)
            {
                _currentTimePeriod = TimePeriod.Weekly;
                _currentChartViewMode = ChartViewMode.Daily;
                
                // Update view mode label and hide toggle panel
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = "Daily View";
                    }
                    
                    // Hide the view mode panel (user can't change the view)
                    if (ViewModePanel != null)
                    {
                        ViewModePanel.Visibility = Visibility.Collapsed;
                    }
                });
            }
            
            // Load records for the date range - use DispatcherQueue to allow UI to update first
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => {
                // Show loading indicator
                if (LoadingIndicator != null)
                {
                    LoadingIndicator.Visibility = Visibility.Visible;
                }
                
                // Use a delay to allow UI to show loading indicator
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () => {
                    // Small delay to give UI time to update
                    await Task.Delay(100);
                    
                    // Load the data
                    LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
                    
                    // Hide loading indicator
                    if (LoadingIndicator != null)
                    {
                        LoadingIndicator.Visibility = Visibility.Collapsed;
                    }
                });
            });
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
                var aggregatedRecords = GetAggregatedRecordsForDateRange(startDate, endDate);
                
                // --- Merge with LIVE data if range includes TODAY --- 
                if (endDate.Date >= DateTime.Today) 
                {
                    var currentDateForMerge = DateTime.Today;
                    var liveRecords = _trackingService.GetRecords()
                                        .Where(r => r.IsFromDate(currentDateForMerge))
                                        .ToList();
                                        
                    var mergedData = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

                    // Add aggregated records first (these contain historical data)
                    foreach (var aggRecord in aggregatedRecords)
                    {
                        mergedData[aggRecord.ProcessName] = aggRecord;
                    }

                    // Overwrite with or add live records (more accurate for today)
                    foreach (var liveRecord in liveRecords)
                    {
                        // If the live record is already in the merged data (from aggregation),
                        // we might want to keep the aggregated duration + live increment,
                        // but for simplicity, we'll just use the live record directly as it's most current.
                        // This assumes the live record reflects total usage for today accurately.
                        liveRecord.LoadAppIconIfNeeded(); // Ensure icon is loaded
                        mergedData[liveRecord.ProcessName] = liveRecord; 
                    }
                    
                    records = mergedData.Values.ToList();
                }
                else
                {
                    // For purely historical ranges, just use the aggregated data
                    records = aggregatedRecords;
                }
                // --- Merge END ---
                
                System.Diagnostics.Debug.WriteLine($"Retrieved {records.Count} records after merge/aggregation");
                
                // Check if we have data - if not, show a message to the user
                if (records.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("No data found for the selected date range");
                    
                    // Show a message to the user
                    DispatcherQueue.TryEnqueue(async () => {
                        try {
                            ContentDialog infoDialog = new ContentDialog()
                            {
                                Title = "No Data Available",
                                Content = $"No usage data found for the selected date range ({startDate:MMM d} - {endDate:MMM d}).",
                                CloseButtonText = "OK",
                                XamlRoot = Content.XamlRoot
                            };
                            
                            await infoDialog.ShowAsync();
                        }
                        catch (Exception dialogEx) {
                            System.Diagnostics.Debug.WriteLine($"Error showing dialog: {dialogEx.Message}");
                        }
                    });
                }
                
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
                
                // Clean up any system processes - be less aggressive with date ranges
                CleanupSystemProcesses();
                
                // Force a refresh of the ListView
                if (UsageListView != null)
                {
                    DispatcherQueue.TryEnqueue(() => {
                        UsageListView.ItemsSource = null;
                        UsageListView.ItemsSource = _usageRecords;
                    });
                }
                
                // Check if this is the Last 7 days selection
                var today = DateTime.Today;
                bool isLast7Days = startDate == today.AddDays(-6) && endDate == today;
                
                if (isLast7Days)
                {
                    // Force daily chart for Last 7 days
                    _currentChartViewMode = ChartViewMode.Daily;
                    
                    // Update view mode label and hide toggle panel
                    DispatcherQueue.TryEnqueue(() => {
                        if (ViewModeLabel != null)
                        {
                            ViewModeLabel.Text = "Daily View";
                        }
                        
                        // Hide the view mode panel (user can't change the view)
                        if (ViewModePanel != null)
                        {
                            ViewModePanel.Visibility = Visibility.Collapsed;
                        }
                    });
                    
                    // Update the chart
                    UpdateUsageChart();
                    
                    // Update the summary tab
                    UpdateSummaryTab();
                }
                else
                {
                    // For other date ranges, use default behavior
                    // Update chart based on current view mode
                    _currentChartViewMode = ChartViewMode.Daily; // Default to daily for ranges
                    UpdateChartViewMode(); // This will call UpdateUsageChart internally
                    
                    // Update the summary tab
                    UpdateSummaryTab();
                }
                
                System.Diagnostics.Debug.WriteLine($"Successfully loaded and displayed {records.Count} records for date range");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records for date range: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                
                // Show an error message to the user
                DispatcherQueue.TryEnqueue(async () => {
                    try {
                        ContentDialog errorDialog = new ContentDialog()
                        {
                            Title = "Error Loading Data",
                            Content = $"Failed to load screen time data: {ex.Message}",
                            CloseButtonText = "OK",
                            XamlRoot = Content.XamlRoot
                        };
                        
                        await errorDialog.ShowAsync();
                    }
                    catch (Exception dialogEx) {
                        System.Diagnostics.Debug.WriteLine($"Error showing dialog: {dialogEx.Message}");
                    }
                });
            }
        }

        private void CleanupSystemProcesses()
        {
            try
            {
                // Debug before cleanup
                System.Diagnostics.Debug.WriteLine($"Before cleanup: UsageRecords count: {_usageRecords.Count}");
                
                // Special case for date ranges
                bool isDateRange = _selectedEndDate.HasValue;
                int initialCount = _usageRecords.Count;
                
                // Modified approach: two-pass filtering
                
                // PASS 1: First remove high-priority system processes regardless of duration or view
                var highPriorityProcesses = _usageRecords
                    .Where(r => {
                        string normalizedName = r.ProcessName.Trim().ToLowerInvariant();
                        return new[] { "explorer", "shellexperiencehost", "searchhost", "dwm", "runtimebroker", "svchost" }
                            .Any(p => normalizedName.Contains(p));
                    })
                    .ToList();
                    
                foreach (var record in highPriorityProcesses)
                {
                    System.Diagnostics.Debug.WriteLine($"Removing high-priority system process: {record.ProcessName} ({record.Duration.TotalSeconds:F1}s)");
                    _usageRecords.Remove(record);
                }
                
                // PASS 2: For other system processes, use the duration-based approach
                var otherSystemProcesses = _usageRecords
                    .Where(r => IsWindowsSystemProcess(r.ProcessName) && 
                               // For date ranges, still be less aggressive with filtering
                               (!isDateRange || r.Duration.TotalSeconds < 10))
                    .ToList();

                foreach (var record in otherSystemProcesses)
                {
                    System.Diagnostics.Debug.WriteLine($"Removing secondary system process: {record.ProcessName} ({record.Duration.TotalSeconds:F1}s)");
                    _usageRecords.Remove(record);
                }
                
                int removedCount = initialCount - _usageRecords.Count;
                System.Diagnostics.Debug.WriteLine($"Cleanup complete: Removed {removedCount} system processes. Records remaining: {_usageRecords.Count}");
                
                // Update UI to reflect changes only if we actually removed something
                if (removedCount > 0)
                {
                    // Force a refresh of the ListView
                    if (UsageListView != null && UsageListView.ItemsSource == _usageRecords)
                    {
                        // Only refresh if our collection is the source
                        DispatcherQueue.TryEnqueue(() => {
                            UsageListView.ItemsSource = null;
                            UsageListView.ItemsSource = _usageRecords;
                        });
                    }
                    
                    // Update the summary and chart
                    UpdateSummaryTab();
                    UpdateUsageChart();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cleaning system processes: {ex.Message}");
            }
        }
    }
}


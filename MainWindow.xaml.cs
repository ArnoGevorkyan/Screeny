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
using System.Diagnostics; // Add for Debug.WriteLine

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
        
        private AppWindow _appWindow; // Field to hold the AppWindow

        // P/Invoke constants and structures for power notifications
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

        // GUID_CONSOLE_DISPLAY_STATE = {6FE69556-704A-47A0-8F24-C28D936FDA47}
        private static readonly Guid GuidConsoleDisplayState = new Guid(0x6fe69556, 0x704a, 0x47a0, 0x8f, 0x24, 0xc2, 0x8d, 0x93, 0x6f, 0xda, 0x47);
        // GUID_SYSTEM_AWAYMODE = {98A7F580-01F7-48AA-9C0F-44352C29E5C0}
        private static readonly Guid GuidSystemAwayMode = new Guid(0x98a7f580, 0x01f7, 0x48aa, 0x9c, 0x0f, 0x44, 0x35, 0x2c, 0x29, 0xe5, 0xc0);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            // Followed by DataLength bytes of data
            // For the settings we are interested in, Data is typically a single byte or DWORD (uint)
            public byte Data; // Simplified for single byte data (like display state)
        }

        [DllImport("User32.dll", SetLastError = true, EntryPoint = "RegisterPowerSettingNotification", CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, int Flags);

        [DllImport("User32.dll", SetLastError = true, EntryPoint = "UnregisterPowerSettingNotification", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle); 

        // P/Invoke for window subclassing
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const int GWLP_WNDPROC = -4;

        // Fields for power notification handles
        private IntPtr _hConsoleDisplayState = IntPtr.Zero; 
        private IntPtr _hSystemAwayMode = IntPtr.Zero;

        // Fields for window subclassing
        private IntPtr _hWnd = IntPtr.Zero;
        private WndProcDelegate? _newWndProcDelegate = null; // Keep delegate alive
        private IntPtr _oldWndProc = IntPtr.Zero;

        public MainWindow()
        {
            _disposed = false;
            
            // Ensure today's date is valid
            DateTime todayDate = DateTime.Today;
            System.Diagnostics.Debug.WriteLine($"[LOG] System time check - Today: {todayDate:yyyy-MM-dd}, Now: {DateTime.Now}");
            
            // Validate that today's date is not in the future, fall back to a reasonable date if it is
            if (todayDate.Year > 2024)
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] WARNING: System date may be incorrect ({todayDate:yyyy-MM-dd})");
                // Use a reasonable default date (April 2024)
                _selectedDate = new DateTime(2024, 4, 13);
                System.Diagnostics.Debug.WriteLine($"[LOG] Using fallback date: {_selectedDate:yyyy-MM-dd}");
            }
            else
            {
                _selectedDate = todayDate;
            }
            
            _usageRecords = new ObservableCollection<AppUsageRecord>();
            
            // Initialize timer fields to avoid nullable warnings
            _updateTimer = new DispatcherTimer();
            _autoSaveTimer = new DispatcherTimer();

            InitializeComponent();

            // Get window handle AFTER InitializeComponent
            _hWnd = WindowNative.GetWindowHandle(this);
            if (_hWnd == IntPtr.Zero)
            {
                 Debug.WriteLine("CRITICAL ERROR: Could not get window handle in constructor.");
                 // Cannot proceed with power notifications without HWND
            }
            else
            {
                // Subclass the window procedure
                SubclassWindow();
            }

            // Initialize the WindowControlHelper
            _windowHelper = new WindowControlHelper(this);

            // Initialize services
            _databaseService = new DatabaseService();
            // Log database initialization status
            System.Diagnostics.Debug.WriteLine($"[Database Check] DatabaseService initialized. IsDatabaseInitialized: {_databaseService.IsDatabaseInitialized()}");
            
            // Pass DatabaseService to WindowTrackingService
            if (_databaseService == null) 
            {
                // Handle the case where database service failed to initialize (optional)
                 System.Diagnostics.Debug.WriteLine("CRITICAL: DatabaseService is null, cannot initialize WindowTrackingService properly.");
                 // Consider throwing an exception or showing an error message
                 throw new InvalidOperationException("DatabaseService could not be initialized.");
            }
            _trackingService = new WindowTrackingService(_databaseService);

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

            // Get the AppWindow and subscribe to Closing event
            _appWindow = GetAppWindowForCurrentWindow();
            _appWindow.Closing += AppWindow_Closing;
        }

        private void SubclassWindow()
        {
            if (_hWnd == IntPtr.Zero)
            {
                Debug.WriteLine("Cannot subclass window: HWND is zero.");
                return;
            }
            
            // Ensure the delegate is kept alive
            _newWndProcDelegate = new WndProcDelegate(NewWindowProc);
            IntPtr newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_newWndProcDelegate);

            // Set the new window procedure
             _oldWndProc = SetWindowLongPtr(_hWnd, GWLP_WNDPROC, newWndProcPtr);
            if (_oldWndProc == IntPtr.Zero)
            {
                 int error = Marshal.GetLastWin32Error();
                 Debug.WriteLine($"Failed to subclass window procedure. Error code: {error}");
                 _newWndProcDelegate = null; // Clear delegate if failed
            }
             else
             {
                 Debug.WriteLine("Successfully subclassed window procedure.");
             }
        }

        private void RestoreWindowProc()
        {
             if (_hWnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
             {
                 SetWindowLongPtr(_hWnd, GWLP_WNDPROC, _oldWndProc);
                 _oldWndProc = IntPtr.Zero;
                 _newWndProcDelegate = null; // Allow delegate to be garbage collected
                 Debug.WriteLine("Restored original window procedure.");
             }
        }

        // The new window procedure
        private IntPtr NewWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_POWERBROADCAST)
            {
                if (wParam.ToInt32() == PBT_POWERSETTINGCHANGE)
                {
                    // Marshal lParam to our structure
                    POWERBROADCAST_SETTING setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);

                    // Check the GUID and Data
                    HandlePowerSettingChange(setting.PowerSetting, setting.Data);
                }
                // Note: Other PBT_ events exist (like PBT_APMSUSPEND, PBT_APMRESUMEAUTOMATIC) 
                // but PBT_POWERSETTINGCHANGE is generally preferred for modern apps.
            }

            // Call the original window procedure for all messages
            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private void HandlePowerSettingChange(Guid settingGuid, byte data)
        {
            if (settingGuid == GuidConsoleDisplayState)
            {
                // 0 = Off, 1 = On, 2 = Dimmed
                if (data == 0) // Display turning off (potential sleep)
                {
                    Debug.WriteLine("Power Event: Console Display State - OFF");
                    _trackingService?.PauseTrackingForSuspend();
                }
                else if (data == 1) // Display turning on (potential resume)
                {
                     Debug.WriteLine("Power Event: Console Display State - ON");
                     _trackingService?.ResumeTrackingAfterSuspend();
                }
                 else
                 {
                      Debug.WriteLine($"Power Event: Console Display State - DIMMED ({data})");
                 }
            }
            else if (settingGuid == GuidSystemAwayMode)
            {
                // 1 = Entering Away Mode, 0 = Exiting Away Mode
                if (data == 1) // Entering away mode (sleep)
                {
                     Debug.WriteLine("Power Event: System Away Mode - ENTERING");
                     _trackingService?.PauseTrackingForSuspend();
                }
                else if (data == 0) // Exiting away mode (resume)
                {
                    Debug.WriteLine("Power Event: System Away Mode - EXITING");
                    _trackingService?.ResumeTrackingAfterSuspend();
                }
            }
        }

        // Add this method to allow App.xaml.cs to access the service
        public WindowTrackingService? GetTrackingService()
        {
            return _trackingService;
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
            System.Diagnostics.Debug.WriteLine("[LOG] ENTERING Dispose");
            if (!_disposed)
            {
                 // Unregister power notifications FIRST
                 UnregisterPowerNotifications();

                 // Restore original window procedure
                 RestoreWindowProc();

                // Unsubscribe from AppWindow event
                if (_appWindow != null)
                {
                    _appWindow.Closing -= AppWindow_Closing;
                }

                // Stop services - this might be redundant if PrepareForSuspend ran
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Stopping services (might be redundant)...");
                _trackingService?.StopTracking();
                _updateTimer?.Stop();
                _autoSaveTimer?.Stop();
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Services stopped.");

                // REMOVED SaveRecordsToDatabase() - handled by PrepareForSuspend
                System.Diagnostics.Debug.WriteLine("[LOG] Dispose: Save skipped - handled by PrepareForSuspend.");
                
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
                 if (_trackingService != null) _trackingService.UsageRecordUpdated -= TrackingService_UsageRecordUpdated;
                 if (_autoSaveTimer != null) _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
                  // Ensure Loaded event is removed if subscribed
                  if (Content is FrameworkElement root) root.Loaded -= MainWindow_Loaded; 

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
        /// Prepares the application for suspension by stopping tracking and saving data.
        /// This is called from the App.OnSuspending event handler.
        /// </summary>
        public void PrepareForSuspend()
        {
            System.Diagnostics.Debug.WriteLine("[LOG] ENTERING PrepareForSuspend");
            try
            {
                // Debug check for system time
                System.Diagnostics.Debug.WriteLine($"[LOG] Current system time: {DateTime.Now}");
                
                // Validate _selectedDate to ensure it's not in the future
                if (_selectedDate > DateTime.Today)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOG] WARNING: Future _selectedDate detected ({_selectedDate:yyyy-MM-dd}), resetting to today.");
                    _selectedDate = DateTime.Today;
                }
                
                // Ensure tracking is stopped first to finalize durations
                if (_trackingService != null && _trackingService.IsTracking)
                {
                     System.Diagnostics.Debug.WriteLine("[LOG] PrepareForSuspend: BEFORE StopTracking()");
                    _trackingService.StopTracking(); 
                    System.Diagnostics.Debug.WriteLine("[LOG] PrepareForSuspend: AFTER StopTracking()");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[LOG] PrepareForSuspend: Tracking service null or not tracking.");
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

        // Add a counter for timer ticks to control periodic chart refreshes
        private int _timerTickCounter = 0;

        private void UpdateTimer_Tick(object? sender, object e)
        {
            try
            {
                // Check if disposed or disposing
                if (_disposed || _usageRecords == null) 
                {
                    System.Diagnostics.Debug.WriteLine("UpdateTimer_Tick: Skipping update as window is disposed or collection is null");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("Timer tick - updating durations");
        
                // Use a local variable for safer thread interaction
                _timerTickCounter++;
                int localTickCounter = _timerTickCounter;
        
                // Get the LIVE focused application from the tracking service
                var liveFocusedApp = _trackingService?.CurrentRecord;
                AppUsageRecord? recordToUpdate = null;

                if (liveFocusedApp != null)
                {
                    // Safely access collection - check if disposed first
                    if (_disposed || _usageRecords == null) return;
                    
                    // Find if this app exists in the currently displayed list (_usageRecords)
                    // Match based on ProcessName for simplicity in aggregated views
                    // Use ToList to get a snapshot to avoid collection modified exception
                    var snapshot = _usageRecords.ToList();
                    
                    recordToUpdate = snapshot
                        .FirstOrDefault(r => r.ProcessName.Equals(liveFocusedApp.ProcessName, StringComparison.OrdinalIgnoreCase));

                    if (recordToUpdate != null && !_disposed)
                    {
                        System.Diagnostics.Debug.WriteLine($"Incrementing duration for displayed record: {recordToUpdate.ProcessName}");
                        
                        // --- Check if viewing today or a range including today --- 
                        bool isViewingToday = _selectedDate.Date == DateTime.Today && !_isDateRangeSelected;
                        bool isViewingRangeIncludingToday = _isDateRangeSelected && _selectedEndDate.HasValue && 
                                                          DateTime.Today >= _selectedDate.Date && DateTime.Today <= _selectedEndDate.Value.Date;
                                                          
                        if (isViewingToday || isViewingRangeIncludingToday)
                        {
                            // Increment duration every second for accuracy
                            recordToUpdate.IncrementDuration(TimeSpan.FromSeconds(1));
                        }
                        else
                        {
                             System.Diagnostics.Debug.WriteLine("Not incrementing duration - viewing a past date/range.");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Live focused app {liveFocusedApp.ProcessName} not found in current view or window disposed");
                    }
                }
                
                // --- CHANGE: Only update UI based on timer interval, not duration increment --- 
                // Periodically force UI refresh to ensure chart updates correctly.
                if (localTickCounter >= 10 && !_disposed) // Reduced from 15 to 10 seconds for more frequent updates
                {
                    System.Diagnostics.Debug.WriteLine($"Periodic UI Update Triggered (tickCounter={localTickCounter})");
                    _timerTickCounter = 0; // Reset counter
                    
                    // Update UI using dispatcher to ensure we're on the UI thread
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        try 
                        {
                            // Double-check we're not disposed before updating UI
                            if (!_disposed && _usageRecords != null)
                            {
                                // Update summary and chart
                                UpdateSummaryTab();
                                UpdateUsageChart(liveFocusedApp); // Pass live app in case it needs it
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error in UI update dispatcher: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in timer tick: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
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

            // First, check for a match based on process name (case-insensitive)
            // This is the most reliable way to identify the same app across different sessions
            var processMatch = _usageRecords.FirstOrDefault(r => 
                r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase));
                
            if (processMatch != null)
            {
                System.Diagnostics.Debug.WriteLine($"FindExistingRecord: Found match by process name: {record.ProcessName}");
                return processMatch;
            }

            // Check existing records for a match based on window handle (same session only)
            foreach (var r in _usageRecords)
            {
                // If it's the exact same window, return it
                if (r.WindowHandle == record.WindowHandle && r.WindowHandle != IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"FindExistingRecord: Found match by window handle for {record.ProcessName}");
                    return r;
                }
            }
            
            // Try to find matches based on application characteristics
            foreach (var r in _usageRecords)
            {
                // For applications that should be consolidated, look for matching process names
                string baseAppName = ApplicationProcessingHelper.GetBaseAppName(record.ProcessName);
                if (!string.IsNullOrEmpty(baseAppName) && 
                    ApplicationProcessingHelper.GetBaseAppName(r.ProcessName).Equals(baseAppName, StringComparison.OrdinalIgnoreCase))
                {
                    // For applications we want to consolidate, just match on process name
                    if (ApplicationProcessingHelper.IsApplicationThatShouldConsolidate(record.ProcessName))
                    {
                        System.Diagnostics.Debug.WriteLine($"FindExistingRecord: Found match for consolidatable app: {record.ProcessName} -> {r.ProcessName}");
                        return r;
                    }
                    
                    // For other applications, match on process name + check if they're related processes
                    if (r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                       ApplicationProcessingHelper.IsAlternateProcessNameForSameApp(r.ProcessName, record.ProcessName))
                    {
                        // If window titles are similar, consider it the same application
                        if (ApplicationProcessingHelper.IsSimilarWindowTitle(r.WindowTitle, record.WindowTitle))
                        {
                            System.Diagnostics.Debug.WriteLine($"FindExistingRecord: Found match by title similarity: {record.ProcessName}");
                            return r;
                        }
                    }
                }
            }

            // No match found
            System.Diagnostics.Debug.WriteLine($"FindExistingRecord: No match found for {record.ProcessName}");
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
            System.Diagnostics.Debug.WriteLine($"Loading records for date: {date:yyyy-MM-dd}, System.Today: {DateTime.Today:yyyy-MM-dd}");
            
            // Make sure we're not working with a future date
            if (date > DateTime.Today)
            {
                System.Diagnostics.Debug.WriteLine($"WARNING: Future date requested ({date:yyyy-MM-dd}), using today instead");
                date = DateTime.Today;
            }
            
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
                        System.Diagnostics.Debug.WriteLine($"Loading weekly records from {startOfWeek:yyyy-MM-dd} to {endOfWeek:yyyy-MM-dd}");
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
                            // Check if database is initialized
                            bool dbInitialized = _databaseService.IsDatabaseInitialized();
                            System.Diagnostics.Debug.WriteLine($"Database initialized: {dbInitialized}");
                            
                            // Load raw records, not aggregated ones, to preserve timestamps
                            dbRecords = _databaseService.GetRecordsForDate(date); 
                            System.Diagnostics.Debug.WriteLine($"Loaded {dbRecords.Count} records from database for date {date:yyyy-MM-dd}");
                            
                            // Log the first few records
                            foreach (var record in dbRecords.Take(3))
                            {
                                System.Diagnostics.Debug.WriteLine($" - DB Record: {record.ProcessName}, Duration: {record.Duration.TotalSeconds:F1}s, Date: {record.Date:yyyy-MM-dd}, StartTime: {record.StartTime:yyyy-MM-dd HH:mm:ss}");
                            }
                        }
                        else
                        {
                            // Fallback if DB service fails - unlikely but safe
                            System.Diagnostics.Debug.WriteLine("WARNING: _databaseService is null, using tracking service records as fallback");
                            dbRecords = _trackingService.GetRecords()
                                .Where(r => r.IsFromDate(date))
                                .ToList();
                            System.Diagnostics.Debug.WriteLine($"Loaded {dbRecords.Count} records from tracking service as fallback");
                        }
                        
                        // --- REVISED MERGE BLOCK FOR TODAY ---
                        if (date == DateTime.Today)
                        {
                             System.Diagnostics.Debug.WriteLine($"LoadRecordsForDate (Today): Starting merge. DB records: {dbRecords.Count}");
                             // Combine DB records and live records
                             var liveRecords = _trackingService.GetRecords()
                                                 .Where(r => r.IsFromDate(date))
                                                 .ToList();
                             System.Diagnostics.Debug.WriteLine($"LoadRecordsForDate (Today): Live records: {liveRecords.Count}");
                             
                             // Create a dictionary to ensure we have one record per app by process name
                             // Use case-insensitive comparison
                             var uniqueApps = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);
                             
                             // Add database records first as the base 
                             foreach (var dbRecord in dbRecords)
                             {
                                 if (!uniqueApps.ContainsKey(dbRecord.ProcessName))
                                 {
                                     uniqueApps[dbRecord.ProcessName] = dbRecord;
                                     System.Diagnostics.Debug.WriteLine($"Added DB record: {dbRecord.ProcessName} - {dbRecord.Duration.TotalSeconds:F1}s");
                                 }
                                 else
                                 {
                                     // If we already have this process, merge durations
                                     var existingRecord = uniqueApps[dbRecord.ProcessName];
                                     existingRecord._accumulatedDuration += dbRecord.Duration;
                                     System.Diagnostics.Debug.WriteLine($"Merged duplicate DB record: {dbRecord.ProcessName} - Total now: {existingRecord.Duration.TotalSeconds:F1}s");
                                 }
                             }
                             
                             // Now incorporate live records (merging with DB records if the process name matches)
                             foreach (var liveRecord in liveRecords)
                             {
                                 if (uniqueApps.TryGetValue(liveRecord.ProcessName, out var existingRecord))
                                 {
                                     // If this process already exists, update its properties
                                     // First preserve the accumulated duration from DB
                                     TimeSpan dbDuration = existingRecord.Duration;
                                     
                                     // Only add the live record's duration if it's a NEW session (not already in DB)
                                     // We check if live record's ID is 0 (not saved to DB yet)
                                     if (liveRecord.Id == 0)
                                     {
                                         dbDuration += liveRecord.Duration;
                                     }
                                     
                                     // Use the live record's window handle and focus state
                                     existingRecord.WindowHandle = liveRecord.WindowHandle;
                                     existingRecord.IsFocused = liveRecord.IsFocused;
                                     existingRecord._accumulatedDuration = dbDuration;
                                     
                                     // Use live record's window title if the existing one is empty
                                     if (string.IsNullOrEmpty(existingRecord.WindowTitle) && !string.IsNullOrEmpty(liveRecord.WindowTitle))
                                     {
                                         existingRecord.WindowTitle = liveRecord.WindowTitle;
                                     }
                                     
                                     System.Diagnostics.Debug.WriteLine($"Updated {liveRecord.ProcessName} with live data. Duration: {existingRecord.Duration.TotalSeconds:F1}s");
                                 }
                                 else
                                 {
                                     // This is a new process we haven't seen before - add it
                                     uniqueApps[liveRecord.ProcessName] = liveRecord;
                                     System.Diagnostics.Debug.WriteLine($"Added new live record: {liveRecord.ProcessName} - {liveRecord.Duration.TotalSeconds:F1}s");
                                 }
                             }
                             
                             // Convert the dictionary to our list
                             records = uniqueApps.Values.ToList();
                             System.Diagnostics.Debug.WriteLine($"LoadRecordsForDate (Today): Final unique records: {records.Count}");
                        }
                        else
                        {
                            // For past dates, aggregate records loaded FROM DATABASE
                            System.Diagnostics.Debug.WriteLine($"LoadRecordsForDate (Past Date: {date:yyyy-MM-dd}): Aggregating {dbRecords.Count} DB records.");
                            records = dbRecords
                                .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
                                .Select(g => {
                                    var totalDuration = TimeSpan.FromSeconds(g.Sum(rec => rec.Duration.TotalSeconds));
                                    // Use CreateAggregated for past dates - StartTime will be midnight
                                    var aggregatedRecord = AppUsageRecord.CreateAggregated(g.Key, date); 
                                    aggregatedRecord._accumulatedDuration = totalDuration;
                                    aggregatedRecord.LoadAppIconIfNeeded(); 
                                     System.Diagnostics.Debug.WriteLine($"  Aggregated (Past): {aggregatedRecord.ProcessName}, Duration: {aggregatedRecord.Duration.TotalSeconds:F1}s");
                                    return aggregatedRecord;
                                })
                                .Where(ar => ar.Duration.TotalSeconds >= 1) 
                                .ToList();
                             System.Diagnostics.Debug.WriteLine($"LoadRecordsForDate (Past Date): Records after aggregation: {records.Count}");
                        }
                        // --- END REVISED MERGE/AGGREGATION ---
                        
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
                    DispatcherQueue?.TryEnqueue(async () => {
                        try {
                            if (this.Content != null)
                            {
                                ContentDialog infoDialog = new ContentDialog()
                                {
                                    Title = "No Data Available",
                                    Content = $"No usage data found for {DateDisplay?.Text ?? "the selected date"}.",
                                    CloseButtonText = "OK",
                                    XamlRoot = this.Content.XamlRoot
                                };
                                
                                await infoDialog.ShowAsync();
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("Content was null, cannot show error dialog.");
                            }
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
                    DispatcherQueue?.TryEnqueue(() => {
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
                DispatcherQueue?.TryEnqueue(async () => {
                    try {
                        if (this.Content != null)
                        {
                            ContentDialog errorDialog = new ContentDialog()
                            {
                                Title = "Error Loading Data",
                                Content = $"Failed to load screen time data: {ex.Message}",
                                CloseButtonText = "OK",
                                XamlRoot = this.Content.XamlRoot
                            };
                            
                            await errorDialog.ShowAsync();
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("Content was null, cannot show error dialog.");
                        }
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
                    
                    // Create a lookup by date to determine actual start times
                    Dictionary<string, DateTime> processEarliestStartTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                    
                    // Get all individual records to find earliest start times per process
                    for (DateTime date = startDate; date <= endDate; date = date.AddDays(1))
                    {
                        var dateRecords = _databaseService.GetRecordsForDate(date);
                        foreach (var record in dateRecords)
                        {
                            // Track the earliest start time for each process
                            if (!processEarliestStartTimes.ContainsKey(record.ProcessName) || 
                                record.StartTime < processEarliestStartTimes[record.ProcessName])
                            {
                                processEarliestStartTimes[record.ProcessName] = record.StartTime;
                                System.Diagnostics.Debug.WriteLine($"Found earliest start time for {record.ProcessName}: {record.StartTime:HH:mm:ss}");
                            }
                        }
                    }
                    
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
                                // Create record with actual start time if we have it
                                var record = new AppUsageRecord
                                {
                                    ProcessName = processName,
                                    ApplicationName = processName,
                                    Date = startDate,
                                    _accumulatedDuration = duration
                                };
                                
                                // Set the actual start time if we have it, otherwise use noon
                                if (processEarliestStartTimes.TryGetValue(processName, out DateTime actualStartTime))
                                {
                                    record.StartTime = actualStartTime;
                                    System.Diagnostics.Debug.WriteLine($"Using actual start time {actualStartTime:HH:mm:ss} for {processName}");
                                }
                                else
                                {
                                    // Default to noon for visibility on charts
                                    record.StartTime = new DateTime(startDate.Year, startDate.Month, startDate.Day, 12, 0, 0);
                                    System.Diagnostics.Debug.WriteLine($"Using default noon start time for {processName}");
                                }
                                
                                // Load icon for UI display
                                record.LoadAppIconIfNeeded();
                                
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
                            // Create a new record for this process
                            var record = new AppUsageRecord
                            {
                                ProcessName = processName,
                                ApplicationName = processName,
                                Date = startDate,
                                _accumulatedDuration = duration
                            };
                            
                            // Set the actual start time if we have it, otherwise use noon
                            if (processEarliestStartTimes.TryGetValue(processName, out DateTime actualStartTime))
                            {
                                record.StartTime = actualStartTime;
                                System.Diagnostics.Debug.WriteLine($"Using actual start time {actualStartTime:HH:mm:ss} for {processName}");
                            }
                            else
                            {
                                // Default to noon for visibility on charts
                                record.StartTime = new DateTime(startDate.Year, startDate.Month, startDate.Day, 12, 0, 0);
                                System.Diagnostics.Debug.WriteLine($"Using default noon start time for {processName}");
                            }
                            
                            // Load icon for UI display
                            record.LoadAppIconIfNeeded();
                            
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
            System.Diagnostics.Debug.WriteLine("[LOG] ENTERING SaveRecordsToDatabase");
            // Skip saving if database or tracking service is not available
            if (_databaseService == null || _trackingService == null)
            {
                System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: DB or Tracking service is null. Skipping save.");
                System.Diagnostics.Debug.WriteLine("[LOG] EXITING SaveRecordsToDatabase (skipped)");
                return;
            }

            try
            {
                // Get the definitive list of records from the tracking service for today
                System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: Getting records from tracking service...");
                var recordsToSave = _trackingService.GetRecords()
                                    .Where(r => r.IsFromDate(DateTime.Now.Date))
                                    .ToList(); 
                                    
                System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecordsToDatabase: Found {recordsToSave.Count} records from tracking service for today.");

                // Group by process name to avoid duplicates
                var recordsByProcess = recordsToSave
                    .Where(r => !IsWindowsSystemProcess(r.ProcessName) && r.Duration.TotalSeconds > 0)
                    .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                    
                System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecordsToDatabase: Found {recordsByProcess.Count} unique processes after filtering.");

                // Save each unique process record after merging duplicates
                System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: Starting save loop...");
                foreach (var processGroup in recordsByProcess)
                {
                    try
                    {
                        // Calculate total duration for this process
                        var totalDuration = TimeSpan.FromSeconds(processGroup.Sum(r => r.Duration.TotalSeconds));
                        
                        // Get the first record as our representative (with longest duration preferably)
                        var record = processGroup.OrderByDescending(r => r.Duration).First();
                        
                        // Ensure focus is off to finalize duration - critical step!
                        if (record.IsFocused)
                        {
                            System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecordsToDatabase: Explicitly finalizing duration for {record.ProcessName} before saving.");
                            record.SetFocus(false);
                        }
                        
                        // Use the calculated total duration
                        record._accumulatedDuration = totalDuration;
                        
                        System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecordsToDatabase: >>> Preparing to save: {record.ProcessName}, Duration: {record.Duration.TotalSeconds:F1}s, ID: {record.Id}");

                        // If record has an ID greater than 0, it likely exists in DB (but might be partial)
                        if (record.Id > 0)
                        {
                            System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: Calling UpdateRecord...");
                            _databaseService.UpdateRecord(record);
                            System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: UpdateRecord returned.");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: Calling SaveRecord...");
                            _databaseService.SaveRecord(record);
                            System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: SaveRecord returned.");
                        }
                        System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecordsToDatabase: <<< Finished save attempt for: {record.ProcessName}");
                    }
                    catch (Exception processEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecordsToDatabase: Error saving process {processGroup.Key}: {processEx.Message}");
                    }
                }
                System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: FINISHED save loop.");

                System.Diagnostics.Debug.WriteLine("[LOG] SaveRecordsToDatabase: Save process completed normally.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecordsToDatabase: **** ERROR **** during save process: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
            finally
            {
                System.Diagnostics.Debug.WriteLine("[LOG] EXITING SaveRecordsToDatabase");
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
                        // Use null-forgiving operator as mostUsedApp is checked above
                        mostUsedApp.PropertyChanged += (s, e) =>
                        {
                            // Safely check mostUsedApp inside the handler
                            if (mostUsedApp != null && e.PropertyName == nameof(AppUsageRecord.AppIcon) && mostUsedApp.AppIcon != null)
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (MostUsedAppIcon != null && MostUsedPlaceholderIcon != null)
                                    {
                                        MostUsedAppIcon.Source = mostUsedApp.AppIcon;
                                        MostUsedAppIcon.Visibility = Visibility.Visible;
                                        MostUsedPlaceholderIcon.Visibility = Visibility.Collapsed;
                                    }
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

        // New method to handle initialization after window is loaded - Made async void
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow_Loaded event triggered");
            
            try
            {
                ThrowIfDisposed(); // Check if already disposed early

                // Set up window title and icon using the helper
                _windowHelper.SetUpWindow(); 
            
                // Set the custom XAML TitleBar element using the Window's SetTitleBar method
                if (AppTitleBar != null) // Check if the XAML element exists
                {
                    this.SetTitleBar(AppTitleBar); // Correct: Call SetTitleBar on the Window itself
                    Debug.WriteLine("Set AppTitleBar as the custom title bar using Window.SetTitleBar.");
                }
                else
                {
                    Debug.WriteLine("WARNING: Could not set custom title bar (AppTitleBar XAML element is null).");
                }

                // Double-check our selected date is valid (not in the future)
                if (_selectedDate > DateTime.Today)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOG] WARNING: Future date detected at load time: {_selectedDate:yyyy-MM-dd}");
                    _selectedDate = DateTime.Today;
                    System.Diagnostics.Debug.WriteLine($"[LOG] Corrected to: {_selectedDate:yyyy-MM-dd}");
                }
                
                // Set up UI elements
                SetUpUiElements();
                
                // Check if this is the first run
                CheckFirstRun();
                
                // Set today's date and update button text
                _selectedDate = DateTime.Today;
                _currentTimePeriod = TimePeriod.Daily; // Default to daily view
                UpdateDatePickerButtonText();
                
                // Set the selected date display
                if (DateDisplay != null)
                {
                    DateDisplay.Text = "Today";
                }
                
                // Load today's records (assuming LoadRecordsForDate handles its internal errors)
                LoadRecordsForDate(_selectedDate);

                // Set up the UsageListView
                if (UsageListView != null && UsageListView.ItemsSource == null)
                {
                    UsageListView.ItemsSource = _usageRecords;
                }
                
                // Clean up system processes that shouldn't be tracked
                CleanupSystemProcesses();
                
                // Set the initial chart view mode
                _currentChartViewMode = ChartViewMode.Hourly;
                
                // Update view mode label and hide toggle panel (since we start with Today view)
                DispatcherQueue?.TryEnqueue(() => {
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

                // Register for power notifications AFTER window handle is valid and before tracking starts
                RegisterPowerNotifications();

                // Start tracking automatically (assuming StartTracking handles its internal errors)
                StartTracking();
                
                System.Diagnostics.Debug.WriteLine("MainWindow_Loaded completed");
            }
            catch (ObjectDisposedException odEx) // Catch specific expected exceptions first
            {
                // This might happen if the window is closed rapidly during loading
                System.Diagnostics.Debug.WriteLine($"Error in MainWindow_Loaded (ObjectDisposed): {odEx.Message}");
                // Don't try to show UI if disposed
            }
            catch (Exception ex)
            {
                // Log the critical error first, as UI might not be available
                System.Diagnostics.Debug.WriteLine($"CRITICAL Error in MainWindow_Loaded: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);

                // Attempt to show an error dialog, but safely
                try
                {
                    // Check if window is still valid and has a XamlRoot before showing dialog
                    if (!_disposed && this.Content?.XamlRoot != null) 
                    {
                        ContentDialog errorDialog = new ContentDialog()
                        {
                            Title = "Application Load Error",
                            Content = $"The application encountered a critical error during startup and may not function correctly.\n\nDetails: {ex.Message}",
                            CloseButtonText = "OK",
                            XamlRoot = this.Content.XamlRoot // Use the existing XamlRoot
                        };
                        await errorDialog.ShowAsync(); // Await the dialog
                    }
                    else
                    {
                         System.Diagnostics.Debug.WriteLine("Could not show error dialog: Window content/XamlRoot was null or window disposed.");
                    }
                }
                catch (Exception dialogEx)
                {
                    // Log error showing the dialog itself
                    System.Diagnostics.Debug.WriteLine($"Error showing error dialog in MainWindow_Loaded: {dialogEx.Message}");
                }
                // Consider if the app should close here depending on the severity?
                // For now, just log and show message if possible.
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
            try
            {
                System.Diagnostics.Debug.WriteLine($"DatePickerPopup_SingleDateSelected: Selected date: {selectedDate:yyyy-MM-dd}, CurrentTimePeriod: {_currentTimePeriod}");
                
                _selectedDate = selectedDate;
                _selectedEndDate = null;
                _isDateRangeSelected = false;
                
                // Update button text
                UpdateDatePickerButtonText();
                
                // Special handling for Today vs. other days
                var today = DateTime.Today;
                if (_selectedDate == today)
                {
                    System.Diagnostics.Debug.WriteLine("Switching to Today view");
                    
                    // For "Today", use the current tracking settings
                    _currentTimePeriod = TimePeriod.Daily;
                    _currentChartViewMode = ChartViewMode.Hourly;
                    
                    // --- REVISED: Load data safely on UI thread using async/await --- 
                    DispatcherQueue.TryEnqueue(async () => { // Make lambda async
                        try
                        {
                            // Update view mode UI first
                            if (ViewModeLabel != null) ViewModeLabel.Text = "Hourly View";
                            if (ViewModePanel != null) ViewModePanel.Visibility = Visibility.Collapsed;
                            
                            // Show loading indicator
                            if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Visible;

                            // Short delay to allow UI to update (render loading indicator)
                            await Task.Delay(50); 

                            // Load data directly on UI thread
                            LoadRecordsForDate(_selectedDate);

                            // Hide loading indicator AFTER loading is done
                            if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Collapsed;
                            
                             System.Diagnostics.Debug.WriteLine("Today view loaded successfully on UI thread.");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error loading/updating UI for Today view: {ex.Message}");
                            // Hide loading indicator in case of error
                            if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Collapsed;
                            // Optionally show error dialog
                        }
                    });
                    // --- END REVISED --- 
                }
                else // Handle selection of other single dates
                {
                   // ... (Existing logic for other single dates, keep similar async pattern if needed) ...
                    // Ensure we switch to Daily period when selecting a single past date
                    _currentTimePeriod = TimePeriod.Daily;
                    
                    // --- REVISED: Load data safely on UI thread using async/await --- 
                    DispatcherQueue.TryEnqueue(async () => { // Make lambda async
                        try
                        {
                            // Show loading indicator
                            if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Visible;
                            
                            // Short delay to allow UI to update
                            await Task.Delay(50); 
                            
                            // Load the data directly on UI thread
                            LoadRecordsForDate(_selectedDate);
                            UpdateChartViewMode(); // Update chart view after loading
                            
                            // Hide loading indicator AFTER loading is done
                            if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Collapsed;

                            System.Diagnostics.Debug.WriteLine("Past date view loaded successfully on UI thread.");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error loading/updating UI for past date view: {ex.Message}");
                            // Hide loading indicator in case of error
                            if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Collapsed;
                            // Optionally show error dialog
                        }
                    });
                     // --- END REVISED --- 
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in DatePickerPopup_SingleDateSelected: {ex.Message}");
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
                DispatcherQueue.TryEnqueue(async () => {
                    try
                    {
                        // Update UI first
                        if (ViewModeLabel != null)
                        {
                            ViewModeLabel.Text = "Daily View";
                        }
                        
                        // Hide the view mode panel (user can't change the view)
                        if (ViewModePanel != null)
                        {
                            ViewModePanel.Visibility = Visibility.Collapsed;
                        }
                        
                        // Show loading indicator
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Visible;
                        }
                        
                        // Short delay to allow UI to update
                        await Task.Delay(50);
                        
                        // Load the data directly on UI thread
                        LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
                        
                        // Hide loading indicator
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Collapsed;
                        }
                        
                        System.Diagnostics.Debug.WriteLine("Last 7 days view loaded successfully on UI thread");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading/updating UI for Last 7 days view: {ex.Message}");
                        // Hide loading indicator in case of error
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Collapsed;
                        }
                    }
                });
            }
            else
            {
                // Load records for the date range - use DispatcherQueue to allow UI to update first
                DispatcherQueue.TryEnqueue(async () => {
                    try
                    {
                        // Show loading indicator
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Visible;
                        }
                        
                        // Short delay to allow UI to update
                        await Task.Delay(50);
                        
                        // Load the data directly on UI thread
                        LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
                        
                        // Hide loading indicator
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Collapsed;
                        }
                        
                        System.Diagnostics.Debug.WriteLine("Date range view loaded successfully on UI thread");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading/updating UI for date range view: {ex.Message}");
                        // Hide loading indicator in case of error
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Collapsed;
                        }
                    }
                });
            }
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
            System.Diagnostics.Debug.WriteLine($"Loading records for date range: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
            
            // Make sure we're using valid dates (not in the future)
            DateTime today = DateTime.Today;
            if (startDate > today)
            {
                System.Diagnostics.Debug.WriteLine($"WARNING: Future start date requested ({startDate:yyyy-MM-dd}), using today instead");
                startDate = today;
            }
            if (endDate > today)
            {
                System.Diagnostics.Debug.WriteLine($"WARNING: Future end date requested ({endDate:yyyy-MM-dd}), using today instead");
                endDate = today;
            }
            
            _selectedDate = startDate;
            _selectedEndDate = endDate;
            _isDateRangeSelected = true;
            List<AppUsageRecord> records = new List<AppUsageRecord>();
            
            try
            {
                // Check thread safety
                if (_disposed)
                {
                    System.Diagnostics.Debug.WriteLine("Window is disposed, cannot load records");
                    return;
                }
                
                // Clear existing records safely
                if (_usageRecords != null)
                {
                    _usageRecords.Clear();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("ERROR: _usageRecords collection is null");
                    return;
                }
                
                // Update date display with selected date range
                if (DateDisplay != null)
                {
                    // Format without year for cleaner display
                    DateDisplay.Text = $"{startDate:MMM d} - {endDate:MMM d}";
                }
                
                // Load raw records directly from the database for each day in the range
                var allDayRecords = new List<AppUsageRecord>();
                
                // Get records for each day in the range
                for (DateTime date = startDate; date <= endDate; date = date.AddDays(1))
                {
                    var dayRecords = _databaseService?.GetRecordsForDate(date);
                    if (dayRecords != null && dayRecords.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"Loaded {dayRecords.Count} records for {date:yyyy-MM-dd}");
                        allDayRecords.AddRange(dayRecords);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"No records found for {date:yyyy-MM-dd}");
                    }
                }
                
                // Now get the aggregated records (totals by app across the whole period)
                var aggregatedRecords = GetAggregatedRecordsForDateRange(startDate, endDate);
                
                // Add the aggregated records to our collection for display in the list
                records = aggregatedRecords;
                
                // Add the raw records to our _usageRecords behind the scenes for chart rendering
                // This ensures the chart shows data properly distributed by day
                foreach (var record in allDayRecords)
                {
                    // Only add raw records for charts, not for list display
                    _usageRecords.Add(record);
                }
                
                // --- Merge with LIVE data if range includes TODAY --- 
                if (endDate.Date >= DateTime.Today) 
                {
                    try
                    {
                        var currentDateForMerge = DateTime.Today;
                        var liveRecords = _trackingService?.GetRecords()
                                    ?.Where(r => r.IsFromDate(currentDateForMerge))
                                    ?.ToList() ?? new List<AppUsageRecord>();
                                    
                        System.Diagnostics.Debug.WriteLine($"Found {liveRecords.Count} live records for today");
                        
                        // Create a merged set of records to display in the list
                        var mergedData = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

                        // Add aggregated records first (these contain historical data)
                        foreach (var aggRecord in aggregatedRecords)
                        {
                            mergedData[aggRecord.ProcessName] = aggRecord;
                        }

                        // Overwrite or ADD live records, ensuring total duration is correct
                        foreach (var liveRecord in liveRecords)
                        {
                            if (liveRecord == null) continue;
                            
                            liveRecord.LoadAppIconIfNeeded(); // Ensure icon is loaded
                            
                            // Also add the live record to _usageRecords for chart rendering
                            _usageRecords.Add(liveRecord);
                            
                            if (mergedData.TryGetValue(liveRecord.ProcessName, out var existingRecord))
                            {
                                // Existing record found from aggregation.
                                if (existingRecord != null)
                                {
                                    // Use the live record's window handle and focus state
                                    existingRecord.WindowHandle = liveRecord.WindowHandle;
                                    existingRecord.IsFocused = liveRecord.IsFocused;
                                    
                                    // Use live record's window title if the existing one is empty
                                    if (string.IsNullOrEmpty(existingRecord.WindowTitle) && !string.IsNullOrEmpty(liveRecord.WindowTitle))
                                    {
                                        existingRecord.WindowTitle = liveRecord.WindowTitle;
                                    }
                                    
                                    System.Diagnostics.Debug.WriteLine($"Updated {liveRecord.ProcessName} with live data. Duration: {existingRecord.Duration.TotalSeconds:F1}s");
                                }
                            }
                            else
                            {
                                // This is a new process we haven't seen before - add it
                                mergedData[liveRecord.ProcessName] = liveRecord;
                                System.Diagnostics.Debug.WriteLine($"Added new live record: {liveRecord.ProcessName} - {liveRecord.Duration.TotalSeconds:F1}s");
                            }
                        }
                        
                        // Replace the display records with the merged data
                        records = mergedData.Values.ToList();
                        System.Diagnostics.Debug.WriteLine($"LoadRecordsForDateRange (Today included): Final unique records: {records.Count}");
                    }
                    catch (Exception mergeEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error merging live data: {mergeEx.Message}");
                        // Fallback to just using the aggregated records if merging fails
                        records = aggregatedRecords;
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"Retrieved {records.Count} records for display, {_usageRecords.Count} records for charts");
                
                // Check if we have data - if not, show a message to the user
                if (records.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("No data found for the selected date range");
                    
                    // Show a message to the user
                    DispatcherQueue?.TryEnqueue(async () => {
                        try {
                            if (!_disposed && this.Content != null)
                            {
                                ContentDialog infoDialog = new ContentDialog()
                                {
                                    Title = "No Data Available",
                                    Content = $"No usage data found for the selected date range ({startDate:MMM d} - {endDate:MMM d}).",
                                    CloseButtonText = "OK",
                                    XamlRoot = this.Content.XamlRoot
                                };
                                
                                await infoDialog.ShowAsync();
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("Content was null or window disposed, cannot show error dialog.");
                            }
                        }
                        catch (Exception dialogEx) {
                            System.Diagnostics.Debug.WriteLine($"Error showing dialog: {dialogEx.Message}");
                        }
                    });
                }
                
                // Update chart title
                if (SummaryTitle != null)
                {
                    SummaryTitle.Text = "Screen Time Summary";
                }
                
                // Show daily average for date range view
                if (AveragePanel != null)
                {
                    AveragePanel.Visibility = Visibility.Visible;
                }
                
                // Sort records by duration (descending)
                var sortedRecords = records.OrderByDescending(r => r.Duration).ToList();
                
                // Add sorted records to the observable collection for list display
                foreach (var record in sortedRecords)
                {
                    _usageRecords.Add(record);
                }
                
                // Clean up any system processes - be less aggressive with date ranges
                CleanupSystemProcesses();
                
                // Force a refresh of the ListView
                if (!_disposed && UsageListView != null)
                {
                    DispatcherQueue?.TryEnqueue(() => {
                        if (!_disposed && UsageListView != null)
                        {
                            UsageListView.ItemsSource = null;
                            UsageListView.ItemsSource = _usageRecords;
                        }
                    });
                }
                
                // Check if this is the Last 7 days selection
                var lastWeekStart = today.AddDays(-6);
                bool isLast7Days = startDate == lastWeekStart && endDate == today;
                
                if (isLast7Days)
                {
                    // Force daily chart for Last 7 days
                    _currentTimePeriod = TimePeriod.Weekly;
                    _currentChartViewMode = ChartViewMode.Daily;
                    
                    // Update view mode label and hide toggle panel
                    DispatcherQueue?.TryEnqueue(() => {
                        if (!_disposed)
                        {
                            if (ViewModeLabel != null)
                            {
                                ViewModeLabel.Text = "Daily View";
                            }
                            
                            // Hide the view mode panel (user can't change the view)
                            if (ViewModePanel != null)
                            {
                                ViewModePanel.Visibility = Visibility.Collapsed;
                            }
                            
                            // Update the chart
                            UpdateUsageChart();
                            
                            // Update the summary tab
                            UpdateSummaryTab();
                        }
                    });
                }
                else
                {
                    // For other date ranges, use default behavior
                    // Update chart based on current view mode
                    _currentTimePeriod = TimePeriod.Weekly;
                    _currentChartViewMode = ChartViewMode.Daily; // Default to daily for ranges
                    
                    // Update view mode and chart
                    DispatcherQueue?.TryEnqueue(() => {
                        if (!_disposed)
                        {
                            UpdateChartViewMode(); // This will call UpdateUsageChart internally
                            
                            // Update the summary tab
                            UpdateSummaryTab();
                        }
                    });
                }
                
                System.Diagnostics.Debug.WriteLine($"Successfully loaded and displayed {records.Count} records for date range");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records for date range: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                
                // Show an error message to the user
                DispatcherQueue?.TryEnqueue(async () => {
                    try {
                        if (!_disposed && this.Content != null)
                        {
                            ContentDialog errorDialog = new ContentDialog()
                            {
                                Title = "Error Loading Data",
                                Content = $"Failed to load screen time data: {ex.Message}",
                                CloseButtonText = "OK",
                                XamlRoot = this.Content.XamlRoot
                            };
                            
                            await errorDialog.ShowAsync();
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("Content was null or window disposed, cannot show error dialog.");
                        }
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
                        DispatcherQueue?.TryEnqueue(() => {
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

        // Helper method to get the AppWindow
        private AppWindow GetAppWindowForCurrentWindow()
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(wndId);
        }

        // Event handler for AppWindow Closing - REMOVED save logic
        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("[LOG] ENTERING AppWindow_Closing (Save logic removed - handled by Suspending)");
            // Attempt to save records synchronously before closing
            // try
            // {
                // // Ensure tracking is stopped first to finalize durations
                // if (_trackingService != null && _trackingService.IsTracking)
                // {
                //      System.Diagnostics.Debug.WriteLine("[LOG] AppWindow_Closing: BEFORE StopTracking()");
                //     _trackingService.StopTracking(); 
                //     System.Diagnostics.Debug.WriteLine("[LOG] AppWindow_Closing: AFTER StopTracking()");
                // }
                // else
                // {
                //     System.Diagnostics.Debug.WriteLine("[LOG] AppWindow_Closing: Tracking service null or not tracking.");
                // }
                // 
                // System.Diagnostics.Debug.WriteLine("[LOG] AppWindow_Closing: BEFORE SaveRecordsToDatabase()");
                // SaveRecordsToDatabase();
                // System.Diagnostics.Debug.WriteLine("[LOG] AppWindow_Closing: AFTER SaveRecordsToDatabase()");
            // }
            // catch (Exception ex)
            // {
            //      System.Diagnostics.Debug.WriteLine($"[LOG] AppWindow_Closing: **** ERROR **** during save attempt: {ex.Message}");
            // }
            System.Diagnostics.Debug.WriteLine("[LOG] EXITING AppWindow_Closing (Save logic removed - handled by Suspending)");
            // We don't cancel the closing process: args.Cancel = true;
        }

        // Event handler for the Window.Closed event
        private void Window_Closed(object sender, WindowEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("Window_Closed event triggered.");
            try
            {
                // Stop tracking if service exists
                _trackingService?.StopTracking();
                System.Diagnostics.Debug.WriteLine("Tracking stopped in Window_Closed.");
                
                // Save data if database service exists
                SaveRecordsToDatabase();
                System.Diagnostics.Debug.WriteLine("SaveRecordsToDatabase completed successfully in Window_Closed.");
            }
            catch (Exception ex)
            {
                // Log any errors during the save process on close
                System.Diagnostics.Debug.WriteLine($"Error during StopTrackingAndSave in Window_Closed: {ex.Message}");
                // Optionally log to file as well
                // Log.Error(ex, "Error saving data on window close.");
            }
        }

        // New methods for registration/unregistration
        private void RegisterPowerNotifications()
        {
            if (_hWnd == IntPtr.Zero)
            {
                Debug.WriteLine("Cannot register power notifications: HWND is zero.");
                return;
            }

            try
            {
                Guid consoleGuid = GuidConsoleDisplayState; // Need local copy for ref parameter
                _hConsoleDisplayState = RegisterPowerSettingNotification(_hWnd, ref consoleGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_hConsoleDisplayState == IntPtr.Zero)
                {                    
                    Debug.WriteLine($"Failed to register for GuidConsoleDisplayState. Error: {Marshal.GetLastWin32Error()}");
                }
                else
                {
                    Debug.WriteLine("Successfully registered for GuidConsoleDisplayState.");
                }

                Guid awayGuid = GuidSystemAwayMode; // Need local copy for ref parameter
                _hSystemAwayMode = RegisterPowerSettingNotification(_hWnd, ref awayGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_hSystemAwayMode == IntPtr.Zero)
                {
                    Debug.WriteLine($"Failed to register for GuidSystemAwayMode. Error: {Marshal.GetLastWin32Error()}");
                }
                 else
                 {
                     Debug.WriteLine("Successfully registered for GuidSystemAwayMode.");
                 }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error registering power notifications: {ex.Message}");
            }
        }

        private void UnregisterPowerNotifications()
        {
            try
            {
                if (_hConsoleDisplayState != IntPtr.Zero)
                {
                    if (UnregisterPowerSettingNotification(_hConsoleDisplayState))
                        Debug.WriteLine("Successfully unregistered GuidConsoleDisplayState.");
                    else
                        Debug.WriteLine($"Failed to unregister GuidConsoleDisplayState. Error: {Marshal.GetLastWin32Error()}");
                    _hConsoleDisplayState = IntPtr.Zero;
                }
                if (_hSystemAwayMode != IntPtr.Zero)
                {
                    if (UnregisterPowerSettingNotification(_hSystemAwayMode))
                        Debug.WriteLine("Successfully unregistered GuidSystemAwayMode.");
                    else
                        Debug.WriteLine($"Failed to unregister GuidSystemAwayMode. Error: {Marshal.GetLastWin32Error()}");
                     _hSystemAwayMode = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                 Debug.WriteLine($"Error unregistering power notifications: {ex.Message}");
            }
        }
    }
}


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
using ScreenTimeTracker.Helpers;
using Microsoft.UI.Dispatching;
using System.Diagnostics; // Add for Debug.WriteLine
using System.ComponentModel;

namespace ScreenTimeTracker
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public sealed partial class MainWindow : Window, IDisposable
    {
        private readonly WindowTrackingService _trackingService;
        private readonly DatabaseService? _databaseService;
        private ObservableCollection<AppUsageRecord> _usageRecords;
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

        // Add Win32 DPI helper – returns the DPI for the window (96 = 100 %)
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private const int GWLP_WNDPROC = -4;

        // Fields for power notification handles
        private IntPtr _hConsoleDisplayState = IntPtr.Zero; 
        private IntPtr _hSystemAwayMode = IntPtr.Zero;

        // Fields for window subclassing
        private IntPtr _hWnd = IntPtr.Zero;
        private WndProcDelegate? _newWndProcDelegate = null; // Keep delegate alive
        private IntPtr _oldWndProc = IntPtr.Zero;

        private TrayIconHelper? _trayIconHelper; // Add field for TrayIconHelper

        private bool _iconsRefreshedOnce = false; // flag to ensure refresh runs only once

        private readonly MainViewModel _viewModel;

        private readonly IconRefreshService _iconService;

        public MainWindow()
        {
            _disposed = false;
            
            // Ensure today's date is valid
            DateTime todayDate = DateTime.Today;
            System.Diagnostics.Debug.WriteLine($"[LOG] System time check - Today: {todayDate:yyyy-MM-dd}, Now: {DateTime.Now}");
            
            // Use today's date
                _selectedDate = todayDate;
            
            // The records collection now lives in the ViewModel so we alias to it after VM creation

            InitializeComponent();

            // Get window handle AFTER InitializeComponent
            _hWnd = WindowNative.GetWindowHandle(this);
            if (_hWnd == IntPtr.Zero)
            {
                 Debug.WriteLine("CRITICAL ERROR: Could not get window handle in constructor.");
                 // Cannot proceed with power notifications or subclassing without HWND
            }
            // MOVED SubclassWindow() and TrayIconHelper initialization to MainWindow_Loaded
            // else
            // {
            //     // Subclass the window procedure
            //     SubclassWindow();
            //     // Initialize TrayIconHelper AFTER getting handle
            //     _trayIconHelper = new TrayIconHelper(_hWnd);
            //      Debug.WriteLine("TrayIconHelper initialized.");
            //      // Subscribe to TrayIconHelper events
            //      if (_trayIconHelper != null)
            //      {
            //          _trayIconHelper.ShowClicked += TrayIcon_ShowClicked;
            //          _trayIconHelper.ExitClicked += TrayIcon_ExitClicked;
            //      }
            // }

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

            // Indicator visuals are data-bound; no imperative call needed

            // NEW – instantiate the ViewModel early and set it as DataContext. We will migrate
            // state into it incrementally so the UI can bind to a single source of truth.
            _viewModel = new MainViewModel();
            if (Content is FrameworkElement fe)
            {
                fe.DataContext = _viewModel;
            }

            // Alias local collection to the ViewModel one so existing logic keeps working
            _usageRecords = _viewModel.Records;

            // Instantiate icon service
            _iconService = new IconRefreshService();

            // After _viewModel instantiated
            _viewModel.SelectedDate = _selectedDate;
            _viewModel.SelectedEndDate = _selectedEndDate;
            _viewModel.IsDateRangeSelected = _isDateRangeSelected;
            _viewModel.CurrentTimePeriod = _currentTimePeriod;
            _viewModel.CurrentChartViewMode = _currentChartViewMode;
            _viewModel.IsTracking = _trackingService.IsTracking;

            _viewModel.OnStartTrackingRequested += (_, __) => StartTracking();
            _viewModel.OnStopTrackingRequested  += (_, __) => StopTracking();
            _viewModel.OnToggleTrackingRequested += (_, __) =>
            {
                if (_trackingService.IsTracking) StopTracking(); else StartTracking();
            };
            _viewModel.OnPickDateRequested += (_, __) =>
            {
                _datePickerPopup?.ShowDatePicker(DatePickerButton, _selectedDate, _selectedEndDate, _isDateRangeSelected);
            };
            _viewModel.OnToggleViewModeRequested += (_, __) =>
            {
                if (_currentChartViewMode == ChartViewMode.Hourly)
                    _currentChartViewMode = ChartViewMode.Daily;
                else
                    _currentChartViewMode = ChartViewMode.Hourly;
                UpdateUsageChart();
            };

            // Initialize timer fields
            _updateTimer = new DispatcherTimer();
            _autoSaveTimer = new DispatcherTimer();
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
            const int WM_GETMINMAXINFO = 0x0024;
            // Minimum window size expressed in logical units (device-independent pixels)
            const double MIN_WIDTH_DIP  = 600; // ~600 DIP ≈ 8.3 in at 96 DPI
            const double MIN_HEIGHT_DIP = 450; // ~450 DIP

            // Handle Tray Icon Messages first
            _trayIconHelper?.HandleWindowMessage(msg, wParam, lParam);

            // Then handle Power Notifications
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

            // Enforce minimum window size
            if (msg == WM_GETMINMAXINFO)
            {
                try
                {
                    MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

                    // Convert the DIP minimums to physical pixels based on current DPI
                    uint dpi = GetDpiForWindow(hWnd);
                    double scale = dpi <= 0 ? 1.0 : dpi / 96.0; // 96 DPI = 100 %
                    int minWidthPx  = (int)Math.Round(MIN_WIDTH_DIP  * scale);
                    int minHeightPx = (int)Math.Round(MIN_HEIGHT_DIP * scale);

                    if (mmi.ptMinTrackSize.x < minWidthPx)
                        mmi.ptMinTrackSize.x = minWidthPx;
                    if (mmi.ptMinTrackSize.y < minHeightPx)
                        mmi.ptMinTrackSize.y = minHeightPx;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    return IntPtr.Zero; // handled
                }
                catch { /* ignore marshal errors */ }
            }

            // Call the original window procedure for all other messages
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

        // Methods moved to MainWindow.Logic.cs (Dispose, PrepareForSuspend, AutoSaveTimer_Tick)

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

                // Stop any duration increments when tracking is paused
                if (_trackingService == null || !_trackingService.IsTracking)
                {
                    System.Diagnostics.Debug.WriteLine("UpdateTimer_Tick: Tracking is paused – skipping updates");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] ===== UpdateTimer_Tick at {DateTime.Now:HH:mm:ss.fff} =====");
                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Current view state:");
                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Selected Date: {_selectedDate:yyyy-MM-dd}");
                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Today's Date: {DateTime.Today:yyyy-MM-dd}");
                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Is Date Range: {_isDateRangeSelected}");
                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - End Date: {(_selectedEndDate?.ToString("yyyy-MM-dd") ?? "null")}");
                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - UI Records Count: {_usageRecords.Count}");
        
                // Use a local variable for safer thread interaction
                _timerTickCounter++;
                int localTickCounter = _timerTickCounter;
        
                // Debug: Show all current records and their focus states
                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Current UI records:");
                foreach (var rec in _usageRecords)
                {
                    System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - {rec.ProcessName}: IsFocused={rec.IsFocused}, Duration={rec.Duration.TotalSeconds:F1}s, Date={rec.Date:yyyy-MM-dd}, StartTime={rec.StartTime:yyyy-MM-dd HH:mm:ss}");
                }
                
                // Get the current focused app from the tracking service
                var liveFocusedApp = _trackingService?.CurrentRecord;
                if (liveFocusedApp != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Live focused app from service: {liveFocusedApp.ProcessName} (Date: {liveFocusedApp.Date:yyyy-MM-dd}, StartTime: {liveFocusedApp.StartTime:yyyy-MM-dd HH:mm:ss})");
                    
                    AppUsageRecord? recordToUpdate = null;
                    
                    // Find if this app exists in the currently displayed list (_usageRecords)
                    // Match based on ProcessName for simplicity in aggregated views
                    // Use ToList to get a snapshot to avoid collection modified exception
                    var snapshot = _usageRecords.ToList();
                    
                    System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Searching for matching record in {snapshot.Count} UI records...");
                    
                    // CRITICAL FIX: Only match records that are from TODAY
                    // This prevents updating historical records when viewing past dates
                    recordToUpdate = snapshot
                        .FirstOrDefault(r => r.ProcessName.Equals(liveFocusedApp.ProcessName, StringComparison.OrdinalIgnoreCase) 
                                          && r.IsFromDate(DateTime.Today));
                    
                    System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Record lookup result: {(recordToUpdate != null ? $"Found {recordToUpdate.ProcessName} from {recordToUpdate.Date:yyyy-MM-dd}" : "No matching record from today")}");

                    if (recordToUpdate != null && !_disposed)
                    {
                        // COMPREHENSIVE LOGGING TO TRACE THE ISSUE
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] ===== FOUND MATCHING RECORD =====");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Current view date: {_selectedDate:yyyy-MM-dd}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Today's date: {DateTime.Today:yyyy-MM-dd}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Record details:");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - ProcessName: {recordToUpdate.ProcessName}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Record.Date: {recordToUpdate.Date:yyyy-MM-dd}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Record.StartTime: {recordToUpdate.StartTime:yyyy-MM-dd HH:mm:ss}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Record.ID: {recordToUpdate.Id}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Record.Duration: {recordToUpdate.Duration.TotalSeconds:F1}s");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Record.IsFocused: {recordToUpdate.IsFocused}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Record._accumulatedDuration: {recordToUpdate._accumulatedDuration.TotalSeconds:F1}s");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - IsFromDate(Today): {recordToUpdate.IsFromDate(DateTime.Today)}");
                        
                        // CRITICAL FIX: Only update duration if the record is from TODAY
                        // This prevents past date records from being incremented
                        if (!recordToUpdate.IsFromDate(DateTime.Today))
                        {
                            System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] BLOCKING UPDATE - Record is from {recordToUpdate.Date:yyyy-MM-dd}, not today");
                            return; // Exit the entire method, not just this iteration
                        }
                        
                        // ADDITIONAL CHECK: Only update if we're actually viewing today or a range that includes today
                        bool isViewingToday = _selectedDate.Date == DateTime.Today && !_isDateRangeSelected;
                        bool isViewingRangeIncludingToday = _isDateRangeSelected && _selectedEndDate.HasValue && 
                                                          DateTime.Today >= _selectedDate.Date && DateTime.Today <= _selectedEndDate.Value.Date;
                        
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] View validation:");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - isViewingToday: {isViewingToday}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - isViewingRangeIncludingToday: {isViewingRangeIncludingToday}");
                        
                        if (!isViewingToday && !isViewingRangeIncludingToday)
                        {
                            System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] BLOCKING UPDATE - Not viewing today (viewing {_selectedDate:yyyy-MM-dd})");
                            return; // Exit the entire method
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] ALLOWING UPDATE - All checks passed");
                        
                        // IMPORTANT: Check if this ListView record truly represents the CURRENT live focused app.
                        // We require either object identity (same reference) OR a strict match on window handle
                        // *and* focus state, so that a stale record from before a pause isn't mistaken for the
                        // active one after we resume.
                        bool isActuallyFocused =
                            (recordToUpdate.WindowHandle != IntPtr.Zero &&
                             recordToUpdate.WindowHandle == liveFocusedApp.WindowHandle) ||
                            liveFocusedApp.ProcessName.Equals(recordToUpdate.ProcessName, StringComparison.OrdinalIgnoreCase);

                        // --- Focus comparison & duration handling ---
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Focus comparison:");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Live focused app: {liveFocusedApp.ProcessName}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Matching record: {recordToUpdate.ProcessName}");
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG]   - Is actually focused: {isActuallyFocused}");

                        if (isActuallyFocused)
                        {
                            System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] INCREMENTING duration for ACTUALLY FOCUSED record: {recordToUpdate.ProcessName}");
                            System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Before increment: Duration={recordToUpdate.Duration.TotalSeconds:F1}s, Accumulated={recordToUpdate._accumulatedDuration.TotalSeconds:F1}s");

                            // First credit the elapsed second.  If the focus flag was stale (IsFocused == false)
                            // this call will add the second to _accumulatedDuration.
                            recordToUpdate.IncrementDuration(TimeSpan.FromSeconds(1));

                            // Now ensure the record is marked as focused for the next tick.
                            if (!recordToUpdate.IsFocused)
                            {
                                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Correcting stale focus flag for {recordToUpdate.ProcessName}");
                                recordToUpdate.SetFocus(true);
                            }

                            System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] After increment: Duration={recordToUpdate.Duration.TotalSeconds:F1}s, Accumulated={recordToUpdate._accumulatedDuration.TotalSeconds:F1}s");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] NOT incrementing duration for {recordToUpdate.ProcessName} - it's not the current focused app!");

                            // Make sure it's marked as unfocused in the UI collection
                            if (recordToUpdate.IsFocused)
                            {
                                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Correcting focus state for {recordToUpdate.ProcessName} - setting to unfocused");
                                recordToUpdate.SetFocus(false);
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Live focused app {liveFocusedApp.ProcessName} not found in current view or window disposed");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] No live focused app from tracking service");
                }
                
                // --- CHANGE: Only update UI based on timer interval, not duration increment --- 
                // Periodically force UI refresh to ensure chart updates correctly.
                if (localTickCounter >= 10 && !_disposed) // Reduced from 15 to 10 seconds for more frequent updates
                {
                    System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] Periodic UI Update Triggered (tickCounter={localTickCounter})");
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
                                UpdateSummaryTab(_usageRecords.ToList()); // Pass List
                                UpdateUsageChart(liveFocusedApp); // Pass live app in case it needs it
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error in UI update dispatcher: {ex.Message}");
                        }
                    });
                }
                
                System.Diagnostics.Debug.WriteLine($"[UI_TIMER_LOG] ===== UpdateTimer_Tick complete =====");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in timer tick: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
        }

        private void TrackingService_UsageRecordUpdated(object? sender, AppUsageRecord record)
        {
            // Check if window is disposed
            if (_disposed)
            {
                System.Diagnostics.Debug.WriteLine("TrackingService_UsageRecordUpdated: Window is disposed, ignoring event");
                return;
            }
            
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
                    System.Diagnostics.Debug.WriteLine($"[TRACKING_DEBUG] Ignoring record from different date:");
                    System.Diagnostics.Debug.WriteLine($"[TRACKING_DEBUG]   - Record.ProcessName: {record.ProcessName}");
                    System.Diagnostics.Debug.WriteLine($"[TRACKING_DEBUG]   - Record.Date: {record.Date:yyyy-MM-dd}");
                    System.Diagnostics.Debug.WriteLine($"[TRACKING_DEBUG]   - Record.StartTime: {record.StartTime:yyyy-MM-dd HH:mm:ss}");
                    System.Diagnostics.Debug.WriteLine($"[TRACKING_DEBUG]   - Selected Date: {_selectedDate:yyyy-MM-dd}");
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    // Double-check disposed state on UI thread
                    if (_disposed)
                    {
                        System.Diagnostics.Debug.WriteLine("UI Update cancelled - window disposed");
                        return;
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"UI Update: Processing record for: {record.ProcessName} ({record.WindowTitle})");

                    // Track if we made any changes that require UI updates
                    bool recordsChanged = false;

                    // First try to find exact match
                    var existingRecord = FindExistingRecord(record);

                    if (existingRecord != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Found existing record: {existingRecord.ProcessName}");
                        
                        // Update the existing record's focus state
                        if (existingRecord.IsFocused != record.IsFocused)
                        {
                            // CRITICAL: Only update focus if the record is from TODAY
                            // This prevents historical records from tracking real-time duration
                            if (existingRecord.IsFromDate(DateTime.Today))
                        {
                            System.Diagnostics.Debug.WriteLine($"Focus state changed for {existingRecord.ProcessName}: {existingRecord.IsFocused} -> {record.IsFocused}");
                            existingRecord.SetFocus(record.IsFocused);
                            recordsChanged = true;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"NOT updating focus for {existingRecord.ProcessName} - it's from {existingRecord.Date:yyyy-MM-dd}, not today");
                            }
                        }

                        // If the window title of the existing record is empty, use the new one
                        if (string.IsNullOrEmpty(existingRecord.WindowTitle) && !string.IsNullOrEmpty(record.WindowTitle))
                        {
                            existingRecord.WindowTitle = record.WindowTitle;
                        }

                        // If we're updating the active status, make sure we unfocus any other records
                        if (record.IsFocused)
                        {
                            System.Diagnostics.Debug.WriteLine($"Setting {record.ProcessName} as focused, unfocusing all others");
                            foreach (var otherRecord in _usageRecords.Where(r => r != existingRecord))
                            {
                                if (otherRecord.IsFocused)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Unfocusing {otherRecord.ProcessName}");
                                otherRecord.SetFocus(false);
                                recordsChanged = true;
                                }
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
                            System.Diagnostics.Debug.WriteLine($"New record {record.ProcessName} is focused, unfocusing all existing records");
                            foreach (var otherRecord in _usageRecords)
                            {
                                if (otherRecord.IsFocused)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Unfocusing existing record: {otherRecord.ProcessName}");
                                otherRecord.SetFocus(false);
                                }
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
                                if (UsageListView != null) // Add explicit null check here
                                {
                                     UsageListView.ItemsSource = _usageRecords;
                                }
                            }
                        });
                    }

                    // Only update the UI if we made changes
                    if (recordsChanged)
                    {
                        // Update the summary and chart in real-time
                        System.Diagnostics.Debug.WriteLine("Updating summary and chart in real-time");
                        UpdateSummaryTab(_usageRecords.ToList()); // Pass List
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

        // Convenience overload: summarize current usage records without providing list
        private void UpdateSummaryTab()
        {
            UpdateSummaryTab(_usageRecords.ToList());
        }

        private void UpdateSummaryTab(List<AppUsageRecord> recordsToSummarize)
        {
            try
            {
                // Calculate total screen time by summing individual record durations
                TimeSpan totalTime = recordsToSummarize.Aggregate(TimeSpan.Zero, (sum, rec) => sum + rec.Duration);
                // Cap to reasonable maximum
                int totalMaxDays = GetDayCountForTimePeriod(_currentTimePeriod, _selectedDate);
                TimeSpan absoluteMaxDuration = TimeSpan.FromHours(24 * totalMaxDays);
                if (totalTime > absoluteMaxDuration)
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Capping total time from {totalTime.TotalHours:F1}h to {absoluteMaxDuration.TotalHours:F1}h");
                    totalTime = absoluteMaxDuration;
                }
                // Update total time display
                TotalScreenTime.Text = FormatTimeSpan(totalTime);

                // Find most used app based on aggregated list
                AppUsageRecord? mostUsedApp = null;
                foreach (var record in recordsToSummarize)
                {
                    TimeSpan cappedDuration = record.Duration;
                    if (cappedDuration > absoluteMaxDuration)
                        cappedDuration = absoluteMaxDuration;
                    if (mostUsedApp == null || cappedDuration > mostUsedApp.Duration)
                        mostUsedApp = record;
                }
                // Update most used app (rest of existing code remains)
                 
                // ... existing code ...
                // Update most used app (rest of existing code remains)
                if (mostUsedApp != null)
                {
                    MostUsedApp.Text = mostUsedApp.ProcessName;
                    MostUsedAppTime.Text = FormatTimeSpan(mostUsedApp.Duration);
                    // Icon visibility is now handled via XAML converters; no direct UI manipulation needed.
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
                    MostUsedApp.Text = "None";
                    MostUsedAppTime.Text = FormatTimeSpan(TimeSpan.Zero);
                    if (MostUsedAppIcon != null && MostUsedPlaceholderIcon != null)
                    {
                        MostUsedAppIcon.Visibility = Visibility.Collapsed;
                        MostUsedPlaceholderIcon.Visibility = Visibility.Visible;
                    }
                }
                // ... existing code ...
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

                // --- Moved from constructor: Setup subclassing and tray icon --- 
                if (_hWnd != IntPtr.Zero)
                {
                    SubclassWindow(); // Subclass here
                    _trayIconHelper = new TrayIconHelper(_hWnd); // Initialize here
                    Debug.WriteLine("TrayIconHelper initialized in Loaded.");
                    // Subscribe to TrayIconHelper events
                    if (_trayIconHelper != null)
                    {
                        _trayIconHelper.ShowClicked += TrayIcon_ShowClicked;
                        _trayIconHelper.ExitClicked += TrayIcon_ExitClicked;
                    }
                }
                else
                {
                    Debug.WriteLine("WARNING: Skipping SubclassWindow/TrayIcon init in Loaded due to missing HWND.");
                }
                // --- End moved section ---

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

                // Add the tray icon with appropriate tooltip based on startup mode
                string trayTooltip = App.StartedFromWindowsStartup ? 
                    "Screeny - Running in background" : 
                    "Screeny - Tracking";
                _trayIconHelper?.AddIcon(trayTooltip);

                // Start tracking automatically (assuming StartTracking handles its internal errors)
                StartTracking();
                
                // Log the startup status
                if (App.StartedFromWindowsStartup)
                {
                    System.Diagnostics.Debug.WriteLine("MainWindow loaded - Started from Windows startup, running in background");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("MainWindow loaded - Normal startup, window visible");
                }
                
                System.Diagnostics.Debug.WriteLine("MainWindow_Loaded completed");

                // Schedule a one-time icon refresh so updated pipeline can replace old cached icons
                if (!_iconsRefreshedOnce)
                {
                    _iconsRefreshedOnce = true;
                    // Delay slightly to let initial icons finish binding and avoid UI jank
                    await Task.Delay(2000);
                    try
                    {
                        foreach (var rec in _usageRecords)
                        {
                            rec.ClearIcon();
                            rec.LoadAppIconIfNeeded();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Icon auto-refresh error: {ex.Message}");
                    }
                }

                // Indicator bound via ViewModel; no imperative call needed
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
            try
            {
                System.Diagnostics.Debug.WriteLine("==== WINDOW CHANGED EVENT ====");
            
            DispatcherQueue.TryEnqueue(() =>
            {
                    // Get the currently focused app from the tracking service
                    var currentRecord = _trackingService?.CurrentRecord;
                    
                    if (currentRecord != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Current active window: {currentRecord.ProcessName}");
                    }
                    
                    // Unfocus ALL records in the UI collection first
                    bool anyChanges = false;
                    foreach (var record in _usageRecords)
                    {
                        if (record.IsFocused)
                {
                            System.Diagnostics.Debug.WriteLine($"Window Changed: Unfocusing {record.ProcessName}");
                            record.SetFocus(false);
                            anyChanges = true;
                }
                    }
                    
                    // Now set focus on the current record if it exists in our collection
                    if (currentRecord != null)
                    {
                        var uiRecord = _usageRecords.FirstOrDefault(r => 
                            r.ProcessName.Equals(currentRecord.ProcessName, StringComparison.OrdinalIgnoreCase));
                        
                        if (uiRecord != null)
                    {
                            // CRITICAL: Only set focus if the record is from TODAY
                            // This prevents historical records from tracking real-time duration
                            if (uiRecord.IsFromDate(DateTime.Today))
                    {
                            System.Diagnostics.Debug.WriteLine($"Window Changed: Setting focus on {uiRecord.ProcessName}");
                            uiRecord.SetFocus(true);
                            anyChanges = true;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"Window Changed: NOT setting focus on {uiRecord.ProcessName} - it's from {uiRecord.Date:yyyy-MM-dd}, not today");
                            }
                        }
                    }
                    
                    // Update UI if we made changes
                    if (anyChanges)
                    {
                        UpdateSummaryTab(_usageRecords.ToList());
                        UpdateUsageChart();
                    }
                });
                }
                catch (Exception ex)
                {
                System.Diagnostics.Debug.WriteLine($"Error in TrackingService_WindowChanged: {ex.Message}");
                }
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
            try
            {
                // Validate date range
                var today = DateTime.Today;
                var start = dateRange.Start;
                var end = dateRange.End;
                
                // Ensure dates aren't in the future and are valid
                if (start > today)
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Future start date ({start:yyyy-MM-dd}) corrected to today");
                    start = today;
                }
                
                if (end > today)
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Future end date ({end:yyyy-MM-dd}) corrected to today");
                    end = today;
                }
                
                // Ensure start date isn't after end date
                if (start > end)
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Start date ({start:yyyy-MM-dd}) is after end date ({end:yyyy-MM-dd})");
                    start = end.AddDays(-1); // Make start date 1 day before end date
                }
                
                _selectedDate = start;
                _selectedEndDate = end;
            _isDateRangeSelected = true;
            
            // Update button text
            UpdateDatePickerButtonText();
            
            // For Last 7 days, ensure we're in Weekly time period and force Daily view
                bool isLast7Days = (_selectedDate == today.AddDays(-6) && _selectedEndDate == today);
                
                bool isLast30Days = (_selectedDate == today.AddDays(-29) && _selectedEndDate == today);
                bool isThisMonth = (_selectedDate == new DateTime(today.Year, today.Month, 1) && _selectedEndDate == today);
                
                if (isLast7Days)
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
                        
                            try
                            {
                        // Load the data directly on UI thread
                        LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
                        
                                System.Diagnostics.Debug.WriteLine("Last 7 days view loaded successfully on UI thread");
                            }
                            catch (Exception loadEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error loading records for Last 7 days: {loadEx.Message}");
                                System.Diagnostics.Debug.WriteLine(loadEx.StackTrace);
                            }
                            finally
                            {
                                // Hide loading indicator AFTER loading attempt, regardless of success
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Collapsed;
                        }
                            }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading/updating UI for Last 7 days view: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                            
                        // Hide loading indicator in case of error
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Collapsed;
                        }
                    }
                });
            }
            else if (isLast30Days || isThisMonth)
            {
                // Treat as custom range
                _currentTimePeriod = TimePeriod.Custom;
                _currentChartViewMode = ChartViewMode.Daily;
                DispatcherQueue.TryEnqueue(async () => {
                    try
                    {
                        if (ViewModeLabel != null) ViewModeLabel.Text = "Daily View";
                        if (ViewModePanel != null) ViewModePanel.Visibility = Visibility.Collapsed;

                        if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Visible;
                        await Task.Delay(50);

                        LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);

                        if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Collapsed;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading records for custom preset: {ex.Message}");
                        if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Collapsed;
                    }
                });
            }
            else
            {
                    // Similar pattern for other date ranges
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
                        
                            try
                            {
                        // Load the data directly on UI thread
                        LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
                        
                                System.Diagnostics.Debug.WriteLine("Date range view loaded successfully on UI thread");
                            }
                            catch (Exception loadEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error loading records for date range: {loadEx.Message}");
                                System.Diagnostics.Debug.WriteLine(loadEx.StackTrace);
                            }
                            finally
                            {
                        // Hide loading indicator
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Collapsed;
                        }
                            }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading/updating UI for date range view: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                            
                        // Hide loading indicator in case of error
                        if (LoadingIndicator != null)
                        {
                            LoadingIndicator.Visibility = Visibility.Collapsed;
                        }
                    }
                });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Critical error in DatePickerPopup_DateRangeSelected: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
        }
        
        // Event Handlers for Tray Icon Clicks
        private void TrayIcon_ShowClicked(object? sender, EventArgs e)
        {
            Debug.WriteLine("Tray Icon Show Clicked");
            // Ensure we run this on the UI thread
            DispatcherQueue?.TryEnqueue(() =>
            {
                try
                {
                    if (_appWindow != null)
                    {
                        // Show the window
                        _appWindow.Show();
                        
                        // If the window was started hidden, we need to activate it to bring it to the foreground
                        this.Activate();
                        
                        Debug.WriteLine("Window shown and activated from tray icon");
                    }
                    else
                    {
                        Debug.WriteLine("AppWindow is null, cannot show window from tray");
                    }
                }
                catch (Exception ex)
                {                    
                    Debug.WriteLine($"Error showing window from tray: {ex.Message}");
                }
            });
        }

        private void TrayIcon_ExitClicked(object? sender, EventArgs e)
        {
            Debug.WriteLine("Tray Icon Exit Clicked");
            // Ensure proper cleanup and exit
            // Using Application.Current.Exit() is generally preferred for WinUI 3
            DispatcherQueue?.TryEnqueue(() =>
            {
                try
                {
                    Application.Current.Exit();
                }
                catch (Exception ex)
                {                    
                    Debug.WriteLine($"Error exiting application from tray: {ex.Message}");
                }
            });
        }

        // Structures for WM_GETMINMAXINFO
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT_WIN32
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT_WIN32 ptReserved;
            public POINT_WIN32 ptMaxSize;
            public POINT_WIN32 ptMaxPosition;
            public POINT_WIN32 ptMinTrackSize;
            public POINT_WIN32 ptMaxTrackSize;
        }

        // --- Tracking status indicator helper ---
        private void UpdateTrackingIndicator()
        {
            // Ensure XAML elements exist (could be null during early constructor)
            if (TrackingStatusText == null || PulseStoryboard == null)
                return;

            bool isActive = _trackingService != null && _trackingService.IsTracking;

            if (isActive)
            {
                TrackingStatusText.Text = "Active";
                PulseDot.Fill = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush;
                PulseStoryboard.Begin();
            }
            else
            {
                TrackingStatusText.Text = "Paused";
                PulseDot.Fill = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
                PulseStoryboard.Stop();
            }
        }

        // Utility to determine whether a process is a system/utility process we don't track visually
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
    }
}



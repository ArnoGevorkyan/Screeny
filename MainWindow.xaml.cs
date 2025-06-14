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

        public MainWindow()
        {
            _disposed = false;
            
            // Ensure today's date is valid
            DateTime todayDate = DateTime.Today;
            System.Diagnostics.Debug.WriteLine($"[LOG] System time check - Today: {todayDate:yyyy-MM-dd}, Now: {DateTime.Now}");
            
            // Use today's date
                _selectedDate = todayDate;
            
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

            // Set initial indicator state
            UpdateTrackingIndicator();
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
                 // Unsubscribe from Tray Icon events
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

            // Update UI state
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;

            // Update tracking indicator
            UpdateTrackingIndicator();
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
                                 // If this process is already in our dictionary, merge durations
                                 if (uniqueApps.TryGetValue(dbRecord.ProcessName, out var existingRecord))
                                 {
                                     // Merge by adding durations
                                     existingRecord._accumulatedDuration += dbRecord.Duration;
                                     System.Diagnostics.Debug.WriteLine($"Merged DB record for {dbRecord.ProcessName}: added {dbRecord.Duration.TotalSeconds:F1}s, new total: {existingRecord.Duration.TotalSeconds:F1}s");
                                 }
                                 else
                                 {
                                     // This is a new process - add it to our dictionary
                                     uniqueApps[dbRecord.ProcessName] = dbRecord;
                                     System.Diagnostics.Debug.WriteLine($"Added new DB record: {dbRecord.ProcessName} - {dbRecord.Duration.TotalSeconds:F1}s");
                                 }
                             }
                             
                             // Now process the live records
                             foreach (var liveRecord in liveRecords)
                             {
                                 // Skip system processes
                                 if (IsWindowsSystemProcess(liveRecord.ProcessName) && liveRecord.Duration.TotalSeconds < 5)
                                 {
                                     System.Diagnostics.Debug.WriteLine($"Skipping system process: {liveRecord.ProcessName}");
                                     continue;
                                 }
                                 
                                 // If the process exists in the dictionary, merge with it
                                 if (uniqueApps.TryGetValue(liveRecord.ProcessName, out var existingDbRecord))
                                 {
                                     // Only merge if this is a different session (different ID or no DB record ID)
                                     // Skip if this exact record is already in the database
                                     if (existingDbRecord.Id != liveRecord.Id || liveRecord.Id <= 0)
                                     {
                                         double oldDuration = existingDbRecord.Duration.TotalSeconds;
                                         
                                         // Merge all properties from the live record except start time which we keep from DB
                                         // We keep the existing record's focus state
                                         bool wasFocused = existingDbRecord.IsFocused;
                                         
                                         // For the merged record, we only take the accumulated durations, not live state
                                         // The SetFocus method below will restore focus if needed
                                         existingDbRecord._accumulatedDuration += liveRecord._accumulatedDuration;
                                         
                                         // Restore focused state if the live record was focused
                                         if (liveRecord.IsFocused)
                                         {
                                             existingDbRecord.SetFocus(true); // This will set last focus time
                                         }
                                         else if (wasFocused)
                                         {
                                             existingDbRecord.SetFocus(true); // Maintain focus if it was already focused
                                         }
                                         
                                         System.Diagnostics.Debug.WriteLine($"Merged live record for {liveRecord.ProcessName}: " +
                                             $"from {oldDuration:F1}s to {existingDbRecord.Duration.TotalSeconds:F1}s, " +
                                             $"IsFocused={existingDbRecord.IsFocused}");
                                     }
                                     else
                                     {
                                         System.Diagnostics.Debug.WriteLine($"Skipping duplicate record for {liveRecord.ProcessName} with same ID: {liveRecord.Id}");
                                     }
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
                            
                            // First, aggregate records by process name to avoid duplicates
                            var uniqueApps = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);
                            
                            // Combine records with the same process name
                            foreach (var record in dbRecords)
                            {
                                System.Diagnostics.Debug.WriteLine($"[AGGREGATE_DEBUG] Processing DB record: {record.ProcessName}, Date: {record.Date:yyyy-MM-dd}, StartTime: {record.StartTime:yyyy-MM-dd HH:mm:ss}");
                                
                                // CRITICAL: Ensure historical records are NEVER focused
                                record.IsFocused = false;
                                
                                if (uniqueApps.TryGetValue(record.ProcessName, out var existingRecord))
                                {
                                    // Merge by adding durations
                                    System.Diagnostics.Debug.WriteLine($"[AGGREGATE_DEBUG] Before merge: existing Date: {existingRecord.Date:yyyy-MM-dd}, StartTime: {existingRecord.StartTime:yyyy-MM-dd HH:mm:ss}");
                                    existingRecord._accumulatedDuration += record.Duration;
                                    // Ensure the aggregated record is also not focused
                                    existingRecord.IsFocused = false;
                                    System.Diagnostics.Debug.WriteLine($"[AGGREGATE_DEBUG] After merge: Date: {existingRecord.Date:yyyy-MM-dd}, StartTime: {existingRecord.StartTime:yyyy-MM-dd HH:mm:ss}");
                                }
                                else
                                {
                                    // This is a new process - add it
                                    uniqueApps[record.ProcessName] = record;
                                    System.Diagnostics.Debug.WriteLine($"[AGGREGATE_DEBUG] Added new aggregated record: {record.ProcessName}, Date: {record.Date:yyyy-MM-dd}");
                                }
                            }
                            
                            // Convert the dictionary to our final list
                            records = uniqueApps.Values.ToList();
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
                
                System.Diagnostics.Debug.WriteLine($"[LOAD_VALIDATION] After loading records for {date:yyyy-MM-dd}, _usageRecords contains:");
                foreach (var rec in _usageRecords)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOAD_VALIDATION]   - {rec.ProcessName}: Date={rec.Date:yyyy-MM-dd}, StartTime={rec.StartTime:yyyy-MM-dd HH:mm:ss}, ID={rec.Id}");
                }
                
                // Clean up any system processes
                CleanupSystemProcesses();
                
                // Force a refresh of the ListView
                if (UsageListView != null)
                {
                    DispatcherQueue?.TryEnqueue(() => {
                        UsageListView.ItemsSource = null;
                        if (UsageListView != null) // Add explicit null check here
                        {
                             UsageListView.ItemsSource = _usageRecords;
                        }
                    });
                }
                
                // Update the summary tab
                UpdateSummaryTab(_usageRecords.ToList()); // Pass List
                
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
        
        private List<AppUsageRecord> GetAggregatedRecordsForDateRange(DateTime startDate, DateTime endDate, bool includeLiveRecords = true)
        {
            // Add null check for _databaseService at the beginning
            if (_databaseService == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: _databaseService is null in GetAggregatedRecordsForDateRange. Returning empty list.");
                return new List<AppUsageRecord>();
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"Getting aggregated records for date range: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

                // This dictionary will hold the final aggregated records, ensuring uniqueness by process name
                Dictionary<string, AppUsageRecord> uniqueRecords = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

                // Get the base aggregated report from the database for the entire date range
                // This returns List<(string ProcessName, TimeSpan TotalDuration)>
                var reportData = _databaseService.GetUsageReportForDateRange(startDate, endDate);

                System.Diagnostics.Debug.WriteLine($"Retrieved {reportData.Count} items from initial database report");

                // Populate the dictionary with the historical aggregated data, converting tuples to AppUsageRecord
                foreach (var (processName, totalDuration) in reportData)
                {
                    if (!uniqueRecords.ContainsKey(processName))
                    {
                        // Create a new AppUsageRecord from the report data
                        var record = new AppUsageRecord
                        {
                            ProcessName = processName,
                            ApplicationName = processName, // Default ApplicationName to ProcessName
                            _accumulatedDuration = totalDuration,
                            Date = startDate, // Assign a date (start date is reasonable for aggregation)
                            StartTime = startDate, // Assign a start time
                            IsFocused = false // Ensure aggregated records are not marked as focused
                        };
                        uniqueRecords[processName] = record;
                        System.Diagnostics.Debug.WriteLine($"Added base record: {record.ProcessName}, Historical Duration: {record.Duration.TotalSeconds:F1}s");
                    }
                }

                // If the date range includes today, merge current live tracking data
                if (includeLiveRecords && endDate.Date >= DateTime.Today)
                {
                    System.Diagnostics.Debug.WriteLine("Merging live records for today...");
                    var liveRecords = GetLiveRecordsForToday(); // Assuming this gets current session data for today
                    System.Diagnostics.Debug.WriteLine($"Found {liveRecords.Count} live records.");

                    foreach (var liveRecord in liveRecords)
                    {
                        if (liveRecord.Duration.TotalSeconds <= 0) continue; // Skip records with no duration

                        if (uniqueRecords.TryGetValue(liveRecord.ProcessName, out var existingRecord))
                        {
                            // IMPORTANT: Add the live duration to the existing historical duration
                            double previousDuration = existingRecord.Duration.TotalSeconds;
                            existingRecord._accumulatedDuration += liveRecord.Duration; // Add durations correctly

                            // Update other relevant properties from live record if needed
                            // CRITICAL: Do NOT set IsFocused = true for the aggregated record here.
                            // existingRecord.IsFocused = liveRecord.IsFocused; // REMOVE or ensure it stays false
                            existingRecord.WindowHandle = liveRecord.WindowHandle; // Keep latest handle
                            if (!string.IsNullOrEmpty(liveRecord.WindowTitle)) existingRecord.WindowTitle = liveRecord.WindowTitle; // Keep latest title
                            // Use the live record's StartTime if it's earlier than the current one
                            if (liveRecord.StartTime < existingRecord.StartTime)
                            {
                                existingRecord.StartTime = liveRecord.StartTime;
                            }


                            System.Diagnostics.Debug.WriteLine($"Merged live duration for {liveRecord.ProcessName}: " +
                                                               $"Historical {previousDuration:F1}s + Live {liveRecord.Duration.TotalSeconds:F1}s = New Total {existingRecord.Duration.TotalSeconds:F1}s");
                        }
                        else
                        {
                            // This process was only running live today, not historically in the range
                            liveRecord.Date = DateTime.Today; // Ensure date is set correctly
                            // Ensure IsFocused is handled correctly if adding a live-only record
                            // If it's truly live-only, its IsFocused state might be relevant, but it's being added to an aggregated list.
                            // For consistency in the aggregated view, maybe set IsFocused = false here too?
                            // liveRecord.IsFocused = false; // Consider this if live-only records appear focused in agg view
                            uniqueRecords[liveRecord.ProcessName] = liveRecord;
                            System.Diagnostics.Debug.WriteLine($"Added new live-only record: {liveRecord.ProcessName}, Duration: {liveRecord.Duration.TotalSeconds:F1}s");
                        }
                    }
                     System.Diagnostics.Debug.WriteLine("Finished merging live records.");
                }

                // Convert dictionary values to a list
                var aggregatedRecords = uniqueRecords.Values.ToList();

                // Filter out system processes and those with very short TOTAL durations
                // Apply filtering AFTER aggregation
                System.Diagnostics.Debug.WriteLine($"Filtering {aggregatedRecords.Count} aggregated records...");
                var filteredRecords = aggregatedRecords
                    .Where(r =>
                        !IsWindowsSystemProcess(r.ProcessName) &&
                        r.Duration.TotalSeconds >= 300)  // Filter based on TOTAL duration (>= 5 minutes)
                    .ToList();
                System.Diagnostics.Debug.WriteLine($"Found {filteredRecords.Count} records after primary filtering.");

                // If no significant non-system processes are found after filtering,
                // maybe show the top few regardless of duration? (Optional: keep original leniency)
                if (filteredRecords.Count == 0 && aggregatedRecords.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine("Primary filter removed all records. Applying lenient filtering.");
                    // Use the original list (before duration filter) but still filter system processes
                    filteredRecords = aggregatedRecords
                        .Where(r => !IsWindowsSystemProcess(r.ProcessName))
                        .OrderByDescending(r => r.Duration.TotalSeconds)
                        .Take(5) // Take top 5 non-system apps regardless of duration
                        .ToList();
                     System.Diagnostics.Debug.WriteLine($"Found {filteredRecords.Count} records after lenient filtering.");
                }

                // Sort the final list by duration descending
                var finalSortedRecords = filteredRecords
                    .OrderByDescending(r => r.Duration.TotalSeconds)
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"Returning {finalSortedRecords.Count} final aggregated and sorted records");
                return finalSortedRecords;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CRITICAL Error in GetAggregatedRecordsForDateRange: {ex.Message}");
                 System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                return new List<AppUsageRecord>(); // Return empty list on error
            }
        }
        
        // Helper function to get live records for today (replace with your actual implementation if different)
        private List<AppUsageRecord> GetLiveRecordsForToday()
        {
            // Assuming _trackingService holds the current session's live data
            return _trackingService?.GetRecords()
                       ?.Where(r => r.IsFromDate(DateTime.Today))
                       ?.ToList() ?? new List<AppUsageRecord>();
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

                // NEW: Explicitly unfocus all UI records to guarantee no record continues
                // accumulating time while tracking is paused.  This is especially important
                // for aggregated views where the backing service record may differ from the
                // UI instance, leaving its IsFocused flag untouched by the service-level stop.
                foreach (var uiRec in _usageRecords.ToList())
                {
                    if (uiRec.IsFocused)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PAUSE_FIX] Unfocusing UI record {uiRec.ProcessName} during StopButton_Click");
                        uiRec.SetFocus(false);
                    }
                }

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

            // Update UI state
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;

            // Refresh summary and chart immediately to reflect the unfocused state
            UpdateSummaryTab(_usageRecords.ToList());
            UpdateUsageChart();

            // Update tracking indicator
            UpdateTrackingIndicator();
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
            // Handle recycle to avoid memory leaks and stale handlers
            if (args.InRecycleQueue && args.ItemContainer?.ContentTemplateRoot is Grid oldGrid && args.ItemContainer.Tag is PropertyChangedEventHandler oldHandler)
            {
                if (args.Item is AppUsageRecord oldRecord)
                {
                    oldRecord.PropertyChanged -= oldHandler;
                }
                args.ItemContainer.Tag = null;
            }

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

                            PropertyChangedEventHandler handler = (s, e) =>
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

                            record.PropertyChanged += handler;
                            // store handler so we can detach later on recycle
                            args.ItemContainer.Tag = handler;
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
                    UpdateSummaryTab(_usageRecords.ToList()); // Pass List
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in auto-save timer tick: {ex}");
            }
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
                    mostUsedApp.LoadAppIconIfNeeded();
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
                else
                {
                    MostUsedApp.Text = "None";
                    MostUsedAppTime.Text = FormatTimeSpan(TimeSpan.Zero);
                    MostUsedAppIcon.Visibility = Visibility.Collapsed;
                    MostUsedPlaceholderIcon.Visibility = Visibility.Visible;
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

                // Set initial indicator state
                UpdateTrackingIndicator();
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
            bool isCustomRange = _isDateRangeSelected && _currentTimePeriod == TimePeriod.Custom;
            
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
            else if (isCustomRange)
            {
                _currentChartViewMode = ChartViewMode.Daily;
                DispatcherQueue.TryEnqueue(() => {
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = "Daily View";
                    }
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
            if (ViewModePanel != null)
            {
                ViewModePanel.Visibility = Visibility.Collapsed;
            }
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
                    
                    // Special case for Last 30 days
                    if (_selectedDate == today.AddDays(-29) && _isDateRangeSelected &&
                        _currentTimePeriod == TimePeriod.Custom)
                    {
                        DatePickerButton.Content = "Last 30 days";
                        return;
                    }
                    
                    // Special case for This month
                    if (_selectedDate == new DateTime(today.Year, today.Month, 1) && _isDateRangeSelected &&
                        _currentTimePeriod == TimePeriod.Custom)
                    {
                        DatePickerButton.Content = "This month";
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

            // Validate dates
            DateTime today = DateTime.Today;
            if (startDate > today) startDate = today;
            if (endDate > today) endDate = today;
            if (startDate > endDate) startDate = endDate; // Ensure start <= end

            _selectedDate = startDate;
            _selectedEndDate = endDate;
            _isDateRangeSelected = true;
            List<AppUsageRecord> finalAggregatedRecords; // Holds aggregated data for List/Summary

            try
            {
                if (_disposed) return;

                // --- Clear UI Collection ---
                // _usageRecords is primarily for the *Chart* which needs daily granularity
                if (_usageRecords != null)
                {
                    _usageRecords.Clear();
                    System.Diagnostics.Debug.WriteLine("Cleared _usageRecords collection (for chart).");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("ERROR: _usageRecords collection is null");
                    return;
                }

                // Update date display
                if (DateDisplay != null) DateDisplay.Text = $"{startDate:MMM d} - {endDate:MMM d}";

                // --- 1. Fetch and Populate Granular Data for Chart (_usageRecords) ---
                var rawDatabaseRecords = new List<AppUsageRecord>();
                if (_databaseService != null)
                {
                    for (DateTime date = startDate; date <= endDate; date = date.AddDays(1))
                    {
                        var dayRecords = _databaseService.GetRecordsForDate(date); // Get raw records for the day
                        if (dayRecords != null && dayRecords.Count > 0)
                        {
                            rawDatabaseRecords.AddRange(dayRecords);
                        }
                    }
                     System.Diagnostics.Debug.WriteLine($"Fetched {rawDatabaseRecords.Count} raw DB records for the chart.");
                }

                // Add raw DB records to the chart's collection
                foreach(var dbRecord in rawDatabaseRecords)
                {
                    _usageRecords.Add(dbRecord);
                }

                // Add live records for today to the chart's collection
                 var liveRecords = GetLiveRecordsForToday();
                if (endDate.Date >= DateTime.Today && liveRecords.Any())
                {
                     System.Diagnostics.Debug.WriteLine($"Adding {liveRecords.Count} live records to chart data (_usageRecords).");
                    foreach(var liveRec in liveRecords)
                    {
                        _usageRecords.Add(liveRec);
                    }
                }
                 System.Diagnostics.Debug.WriteLine($"_usageRecords (for chart) now contains {_usageRecords.Count} granular records.");

                 // Clean up system processes from the chart data
                 CleanupSystemProcesses(); // Operates on _usageRecords
                 System.Diagnostics.Debug.WriteLine($"_usageRecords count after CleanupSystemProcesses: {_usageRecords.Count}");


                // --- 2. Fetch Aggregated Data for ListView and Summary ---
                finalAggregatedRecords = GetAggregatedRecordsForDateRange(startDate, endDate);
                System.Diagnostics.Debug.WriteLine($"Retrieved {finalAggregatedRecords.Count} final aggregated records for ListView/Summary.");


                // --- 3. Update UI Elements ---

                // Update ListView with AGGREGATED data
                if (!_disposed && UsageListView != null)
                {
                     // Sort aggregated data for the list
                    var sortedAggregatedList = finalAggregatedRecords.OrderByDescending(r => r.Duration).ToList();
                     System.Diagnostics.Debug.WriteLine($"Updating ListView with {sortedAggregatedList.Count} sorted aggregated records.");

                    DispatcherQueue?.TryEnqueue(() => { // <-- UI thread update
                        System.Diagnostics.Debug.WriteLine("*** INSIDE DispatcherQueue for ListView update ***"); // Log Entry
                        if (!_disposed && UsageListView != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"--> Setting ItemsSource with {sortedAggregatedList.Count} items."); // Log Count
                            // IMPORTANT: Set ItemsSource to the AGGREGATED list, not _usageRecords
                            UsageListView.ItemsSource = null;
                            UsageListView.ItemsSource = sortedAggregatedList; // <-- Is this line executing correctly?
                            System.Diagnostics.Debug.WriteLine("--> ListView ItemsSource SET."); // Log Exit
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("--> ListView update SKIPPED (disposed or UsageListView is null).");
                        }
                    });
                }

                // Check if we have any aggregated data to display
                if (!finalAggregatedRecords.Any())
                {
                    System.Diagnostics.Debug.WriteLine("No aggregated data found for the selected date range after filtering.");
                    ShowNoDataDialog(startDate, endDate);
                }

                // Update chart title
                if (SummaryTitle != null) SummaryTitle.Text = "Screen Time Summary";

                // Update Average Panel (using aggregated data)
                if (AveragePanel != null)
                {
                    UpdateAveragePanel(finalAggregatedRecords, startDate, endDate); // Pass aggregated data
                    AveragePanel.Visibility = Visibility.Visible;
                }

                // Set View Mode and Update Chart (which uses _usageRecords with granular data)
                UpdateViewModeAndChartForDateRange(startDate, endDate, finalAggregatedRecords);


                System.Diagnostics.Debug.WriteLine($"LoadRecordsForDateRange: Successfully completed. Displaying {finalAggregatedRecords.Count} aggregated items in list.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records for date range: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                ShowErrorDialog($"Failed to load screen time data: {ex.Message}");
            }
        }

        // Helper to update AveragePanel TextBlock
        private void UpdateAveragePanel(List<AppUsageRecord> aggregatedRecords, DateTime startDate, DateTime endDate)
        {
             if (aggregatedRecords.Any())
             {
                 double totalHours = aggregatedRecords.Sum(r => r.Duration.TotalHours);
                 int dayCount = Math.Max(1, (int)(endDate - startDate).TotalDays + 1);
                 double dailyAverage = totalHours / dayCount;
                 System.Diagnostics.Debug.WriteLine($"Calculated Daily Average: {dailyAverage:F1} hours for {dayCount} days");
                 // Update the UI text here (assuming DailyAverage TextBlock exists in XAML)
                 // DispatcherQueue?.TryEnqueue(() => {
                 //    if (DailyAverageTextBlock != null) DailyAverageTextBlock.Text = $"Daily Average: {dailyAverage:F1} hours";
                 // });
             }
             else
             {
                 System.Diagnostics.Debug.WriteLine("No records for daily average calculation.");
                 // Update the UI text here (assuming DailyAverage TextBlock exists in XAML)
                 // DispatcherQueue?.TryEnqueue(() => {
                 //    if (DailyAverageTextBlock != null) DailyAverageTextBlock.Text = "Daily Average: 0h 0m";
                 // });
             }
        }

        // Helper to set view mode and trigger chart/summary updates
        private void UpdateViewModeAndChartForDateRange(DateTime startDate, DateTime endDate, List<AppUsageRecord> aggregatedRecords)
        {
             var todayForCheck = DateTime.Today;
             var lastWeekStart = todayForCheck.AddDays(-6);
             bool isLast7Days = startDate == lastWeekStart && endDate == todayForCheck;

             if (isLast7Days)
             {
                 _currentTimePeriod = TimePeriod.Weekly;
                 _currentChartViewMode = ChartViewMode.Daily;
                 DispatcherQueue?.TryEnqueue(() => {
                     if (!_disposed)
                     {
                         if (ViewModeLabel != null) ViewModeLabel.Text = "Daily View";
                         if (ViewModePanel != null) ViewModePanel.Visibility = Visibility.Collapsed;
                         UpdateUsageChart(); // Uses granular _usageRecords
                         UpdateSummaryTab(aggregatedRecords); // Pass aggregated records
                     }
                 });
             }
             else
             {
                 _currentTimePeriod = TimePeriod.Weekly;
                 _currentChartViewMode = ChartViewMode.Daily;
                 DispatcherQueue?.TryEnqueue(() => {
                     if (!_disposed)
                     {
                         if (ViewModeLabel != null) ViewModeLabel.Text = "Daily View";
                         if (ViewModePanel != null) ViewModePanel.Visibility = Visibility.Collapsed;
                         UpdateUsageChart(); // Uses granular _usageRecords
                         UpdateSummaryTab(aggregatedRecords); // Pass aggregated records
                     }
                 });
             }
        }

        // Helper method to show the 'No Data' dialog
        private void ShowNoDataDialog(DateTime startDate, DateTime endDate)
        {
            DispatcherQueue?.TryEnqueue(async () => {
                try {
                    if (!_disposed && this.Content?.XamlRoot != null)
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
                }
                catch (Exception dialogEx) {
                    System.Diagnostics.Debug.WriteLine($"Error showing 'No Data' dialog: {dialogEx.Message}");
                }
            });
        }

        // Helper method to show a generic error dialog
        private void ShowErrorDialog(string message)
        {
             DispatcherQueue?.TryEnqueue(async () => {
                try {
                    if (!_disposed && this.Content?.XamlRoot != null)
                    {
                        ContentDialog errorDialog = new ContentDialog()
                        {
                            Title = "Error",
                            Content = message,
                            CloseButtonText = "OK",
                            XamlRoot = this.Content.XamlRoot
                        };
                        await errorDialog.ShowAsync();
                    }
                }
                catch (Exception dialogEx) {
                    System.Diagnostics.Debug.WriteLine($"Error showing error dialog: {dialogEx.Message}");
                }
            });
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
                    UpdateSummaryTab(_usageRecords.ToList()); // Pass List
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
            Debug.WriteLine("[LOG] AppWindow_Closing: Hiding window instead of closing.");
            // Prevent the window from closing
            args.Cancel = true;
            
            // Hide the window
            sender.Hide();
            
            // Optional: Show a notification that the app is still running?
            // _trayIconHelper?.ShowNotification("Screeny", "Still running in the background.");
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

        private void LoadRecordsForLastSevenDays()
        {
            try
            {
                // Get current date
                DateTime today = DateTime.Today;
                DateTime startDate = today.AddDays(-6); // Last 7 days including today
                DateTime endDate = today;

                System.Diagnostics.Debug.WriteLine($"Loading records for Last 7 Days: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

                // Get aggregated records for the entire week
                var weekRecords = GetAggregatedRecordsForDateRange(startDate, endDate);

                // Update the UI with the aggregated data
                UpdateRecordListView(weekRecords);

                // Set header based on date range
                SetTimeFrameHeader($"Last 7 Days ({startDate:MMM d} - {endDate:MMM d}, {endDate.Year})");

                // Calculate and display daily average
                if (weekRecords.Any())
                {
                    double totalHours = weekRecords.Sum(r => r.Duration.TotalHours);
                    double dailyAverage = totalHours / 7.0;
                    // DailyAverageTextBlock.Text = $"Daily Average: {dailyAverage:F1} hours"; // Commented out - UI element missing
                    // DailyAverageTextBlock.Visibility = Microsoft.UI.Xaml.Visibility.Visible; // Commented out - UI element missing
                    System.Diagnostics.Debug.WriteLine($"Calculated Daily Average: {dailyAverage:F1} hours (UI element 'DailyAverageTextBlock' not found or commented out)");
                }
                else
                {
                    // DailyAverageTextBlock.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; // Commented out - UI element missing
                    System.Diagnostics.Debug.WriteLine("No records for daily average calculation (UI element 'DailyAverageTextBlock' not found or commented out)");
                }

                // Update the chart for the entire week
                UpdateChartWithRecords(weekRecords);

                // Also load the individual day records for reference but don't display them
                DateTime currentDate = startDate;
                while (currentDate <= endDate)
                {
                    System.Diagnostics.Debug.WriteLine($"Loading reference data for {currentDate:yyyy-MM-dd}");
                    var dailyRecords = LoadRecordsForSpecificDay(currentDate, false);
                    System.Diagnostics.Debug.WriteLine($"Found {dailyRecords.Count} records for {currentDate:yyyy-MM-dd}");
                    currentDate = currentDate.AddDays(1);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in LoadRecordsForLastSevenDays: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the record list view with the provided records
        /// </summary>
        /// <param name="records">The records to display in the list view</param>
        private void UpdateRecordListView(List<AppUsageRecord> records)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"UpdateRecordListView: Updating with {records.Count} records");
                
                // Clear existing records
                if (_usageRecords != null)
                {
                    _usageRecords.Clear();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("ERROR: _usageRecords collection is null");
                    return;
                }
                
                // Sort records by duration (descending)
                var sortedRecords = records.OrderByDescending(r => r.Duration).ToList();
                
                // Add sorted records to the observable collection
                foreach (var record in sortedRecords)
                {
                    if (_usageRecords != null) // Add explicit null check
                    {
                        _usageRecords.Add(record);
                    }
                }
                
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
                
                System.Diagnostics.Debug.WriteLine("UpdateRecordListView: Complete");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateRecordListView: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the chart display with the provided records
        /// </summary>
        /// <param name="records">The records to display in the chart</param>
        private void UpdateChartWithRecords(List<AppUsageRecord> records)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"UpdateChartWithRecords: Updating with {records.Count} records");
                
                // Set the weekly view mode
                _currentTimePeriod = TimePeriod.Weekly;
                _currentChartViewMode = ChartViewMode.Daily; // Default to daily for weekly view
                
                // Update the chart
                DispatcherQueue?.TryEnqueue(() => {
                    if (!_disposed)
                    {
                        // Update view mode label
                        if (ViewModeLabel != null)
                        {
                            ViewModeLabel.Text = "Daily View";
                        }
                        
                        // Hide the view mode panel (user can't change the view for weekly)
                        if (ViewModePanel != null)
                        {
                            ViewModePanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        }
                        
                        // Update the chart
                        UpdateUsageChart();
                        
                        // Update the summary tab
                        UpdateSummaryTab(records); // Pass the received records
                    }
                });
                
                System.Diagnostics.Debug.WriteLine("UpdateChartWithRecords: Complete");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in UpdateChartWithRecords: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads records for a specific day without updating the UI
        /// </summary>
        /// <param name="date">The date to load records for</param>
        /// <param name="updateUI">Whether to update the UI with the loaded records</param>
        /// <returns>List of app usage records for the specified day</returns>
        private List<AppUsageRecord> LoadRecordsForSpecificDay(DateTime date, bool updateUI = true)
        {
            // Add explicit null check for _databaseService
            if (_databaseService == null)
            {
                System.Diagnostics.Debug.WriteLine("ERROR: _databaseService is null in LoadRecordsForSpecificDay. Returning empty list.");
                return new List<AppUsageRecord>();
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"Loading records for specific day: {date:yyyy-MM-dd}");
                
                // Get records from database
                var records = _databaseService?.GetRecordsForDate(date) ?? new List<AppUsageRecord>();
                
                System.Diagnostics.Debug.WriteLine($"Retrieved {records.Count} records for {date:yyyy-MM-dd}");
                
                // For weekly view, we just want to return the records without updating UI
                if (!updateUI)
                {
                    return records;
                }
                
                // Otherwise update the UI (similar to LoadRecordsForDate)
                _selectedDate = date;
                _selectedEndDate = null;
                _isDateRangeSelected = false;
                
                // Clear existing records
                if (_usageRecords != null)
                {
                    _usageRecords.Clear();
                }
                
                // Sort and add records
                var sortedRecords = records.OrderByDescending(r => r.Duration).ToList();
                foreach (var record in sortedRecords)
                {
                    // Add explicit null check before adding
                    if (_usageRecords != null)
                    {
                        _usageRecords.Add(record);
                    }
                }
                
                // Update the ListView
                DispatcherQueue?.TryEnqueue(() => {
                    if (!_disposed && UsageListView != null)
                    {
                        UsageListView.ItemsSource = null;
                        UsageListView.ItemsSource = _usageRecords;
                        
                        // Update the chart (already has null check)
                        if (_usageRecords != null)
                        {
                            UpdateUsageChart();
                        }
                        
                        // Update the summary tab (add explicit null check)
                        if (_usageRecords != null)
                        {
                            UpdateSummaryTab(_usageRecords.ToList());
                        }
                    }
                });
                
                return records;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in LoadRecordsForSpecificDay: {ex.Message}");
                return new List<AppUsageRecord>();
            }
        }

        /// <summary>
        /// Sets the time frame header text in the UI
        /// </summary>
        /// <param name="headerText">The text to set as the header</param>
        private void SetTimeFrameHeader(string headerText)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Setting time frame header: {headerText}");

                // Update the UI on the UI thread
                DispatcherQueue?.TryEnqueue(() => {
                    if (!_disposed)
                    {
                        // Assuming there's a TextBlock named TimeFrameHeader
                        // if (TimeFrameHeader != null) // Commented out - UI element missing
                        // {
                        //     TimeFrameHeader.Text = headerText;
                        // }

                        // Also update the date display
                        if (DateDisplay != null)
                        {
                             // Update DateDisplay instead, as TimeFrameHeader seems missing
                            DateDisplay.Text = headerText;
                            System.Diagnostics.Debug.WriteLine($"Updated DateDisplay text to: {headerText} (UI element 'TimeFrameHeader' not found or commented out)");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in SetTimeFrameHeader: {ex.Message}");
            }
        }

        private TimeSpan CalculateTotalActiveTime(List<AppUsageRecord> records)
        {
            // Create time intervals for each record
            var intervals = records
                .Select(r => new { Start = r.StartTime, End = r.StartTime + r.Duration })
                .Where(iv => iv.End > iv.Start)
                .OrderBy(iv => iv.Start)
                .ToList();
            
            // Merge overlapping intervals
            var merged = new List<(DateTime Start, DateTime End)>();
            foreach (var iv in intervals)
            {
                if (!merged.Any() || iv.Start > merged.Last().End)
                {
                    merged.Add((iv.Start, iv.End));
                }
                else
                {
                    // Extend the last interval end if overlapping
                    var last = merged[merged.Count - 1];
                    var newEnd = iv.End > last.End ? iv.End : last.End;
                    merged[merged.Count - 1] = (last.Start, newEnd);
                }
            }
            
            // Sum merged intervals durations
            TimeSpan total = TimeSpan.Zero;
            foreach (var span in merged)
            {
                total += span.End - span.Start;
            }
            return total;
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

        private void TrackingStatusButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle tracking by forwarding to existing button handlers
            if (_trackingService != null && _trackingService.IsTracking)
            {
                StopButton_Click(StopButton, new RoutedEventArgs());
            }
            else
            {
                StartButton_Click(StartButton, new RoutedEventArgs());
            }
        }
    }
}


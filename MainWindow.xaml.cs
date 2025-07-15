using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
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
        private bool _disposed;
        private bool _iconsRefreshedOnce = false;
        
        // Static constructor to configure LiveCharts
        static MainWindow()
        {
            // Configure global LiveCharts settings
            Helpers.TimeUtil.ConfigureCharts();
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

        // Initialised inline so property wrappers above can work immediately
        private readonly MainViewModel _viewModel = new MainViewModel();




        // Direct ViewModel access - no property wrappers needed

        private DispatcherTimer _updateTimer;
        private bool _isChartDirty = false; // set by events; consumed by unified timer
        private bool _isReloading = false;  // true while usage records are being regenerated
        private int _tickCount = 0; // unified tick counter for all periodic tasks

        public MainWindow()
        {
            _disposed = false;
            
            // Ensure today's date is valid
            DateTime todayDate = DateTime.Today;
            
            // Use today's date
            _viewModel.SelectedDate = todayDate;
            
            InitializeComponent();

            // After InitializeComponent, set DataContext
            if (Content is FrameworkElement fe)
            {
                fe.DataContext = _viewModel;
            }

            // Alias local collection to ViewModel collection
            _usageRecords = _viewModel.Records;

            // Get window handle AFTER InitializeComponent
            _hWnd = WindowNative.GetWindowHandle(this);
            if (_hWnd == IntPtr.Zero)
            {
                 Debug.WriteLine("CRITICAL ERROR: Could not get window handle in constructor.");
                 // Cannot proceed with power notifications or subclassing without HWND
            }

            // Initialize the WindowControlHelper
            _windowHelper = new WindowControlHelper(this);

            // Initialize services
            _databaseService = new DatabaseService();
            // Pass DatabaseService to WindowTrackingService
            if (_databaseService == null) 
            {
                // Handle the case where database service failed to initialize (optional)
                 throw new InvalidOperationException("DatabaseService could not be initialized.");
            }
            _trackingService = new WindowTrackingService();

            // Set up tracking service events
            _trackingService.WindowChanged += TrackingService_WindowChanged;
            _trackingService.UsageRecordUpdated += TrackingService_UsageRecordUpdated;
            _trackingService.RecordReadyForSave += TrackingService_RecordReadyForSave;
            

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

            // ViewModel is already kept in sync via property wrappers.

            // Initialize single unified timer
            _updateTimer = new DispatcherTimer();
            _updateTimer.Interval = TimeSpan.FromSeconds(1);
            _updateTimer.Tick += UpdateTimer_Tick;

            // Hook ViewModel command events
            _viewModel.OnStartTrackingRequested    += (_, __) => StartTracking();
            _viewModel.OnStopTrackingRequested     += (_, __) => StopTracking();
            _viewModel.OnToggleTrackingRequested   += (_, __) =>
            {
                if (_trackingService.IsTracking) StopTracking(); else StartTracking();
            };
            _viewModel.OnPickDateRequested         += (_, __) =>
            {
                _datePickerPopup?.ShowDatePicker(DatePickerButton, _viewModel.SelectedDate, _viewModel.SelectedEndDate, _viewModel.IsDateRangeSelected);
            };
            _viewModel.OnToggleViewModeRequested   += (_, __) =>
            {
                if (_viewModel.CurrentChartViewMode == ChartViewMode.Hourly)
                    _viewModel.CurrentChartViewMode = ChartViewMode.Daily;
                else
                    _viewModel.CurrentChartViewMode = ChartViewMode.Hourly;
                UpdateUsageChart();
            };

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
                        _trayIconHelper.ResetClicked += OnResetDataRequested;
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
                if (_viewModel.SelectedDate > DateTime.Today)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOG] WARNING: Future date detected at load time: {_viewModel.SelectedDate:yyyy-MM-dd}");
                    _viewModel.SelectedDate = DateTime.Today;
                    System.Diagnostics.Debug.WriteLine($"[LOG] Corrected to: {_viewModel.SelectedDate:yyyy-MM-dd}");
                }
                
                // Set up UI elements
                SetUpUiElements();
                
                // Check if this is the first run
                CheckFirstRun();
                
                // Set today's date and update button text
                _viewModel.SelectedDate = DateTime.Today;
                _viewModel.CurrentTimePeriod = TimePeriod.Daily; // Default to daily view
                UpdateDatePickerButtonText();
                
                // Set the selected date display
                if (DateDisplay != null)
                {
                    DateDisplay.Text = "Today";
                }
                
                // Load today's records (assuming LoadRecordsForDate handles its internal errors)
                LoadRecordsForDate(_viewModel.SelectedDate);

                // Set up the UsageListView
                if (UsageListView != null && UsageListView.ItemsSource == null)
                {
                    UsageListView.ItemsSource = _usageRecords;
                }
                
                // Clean up system processes that shouldn't be tracked
                CleanupSystemProcesses();
                
                // Set the initial chart view mode
                _viewModel.CurrentChartViewMode = ChartViewMode.Hourly;
                
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

        // SetUpUiElements implementation moved to MainWindow.UI.cs

        // New method to handle initialization after window is loaded
        private void CheckFirstRun()
        {
            // Welcome message has been removed as requested
            System.Diagnostics.Debug.WriteLine("First run check - welcome message disabled");
        }

        // TrackingService_WindowChanged implementation moved to MainWindow.UI.cs

        // Event handlers for DatePickerPopup events
        private void DatePickerPopup_SingleDateSelected(object? sender, DateTime selectedDate)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"DatePickerPopup_SingleDateSelected: Selected date: {selectedDate:yyyy-MM-dd}, CurrentTimePeriod: {_viewModel.CurrentTimePeriod}");
                
                _viewModel.SelectedDate = selectedDate;
                _viewModel.SelectedEndDate = null;
                _viewModel.IsDateRangeSelected = false;
                
                // Update button text
                UpdateDatePickerButtonText();
                
                // Special handling for Today vs. other days
                var today = DateTime.Today;
                if (_viewModel.SelectedDate == today)
                {
                    System.Diagnostics.Debug.WriteLine("Switching to Today view");
                    
                    // For "Today", use the current tracking settings
                    _viewModel.CurrentTimePeriod = TimePeriod.Daily;
                    _viewModel.CurrentChartViewMode = ChartViewMode.Hourly;
                    
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
                            LoadRecordsForDate(_viewModel.SelectedDate);

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
                    _viewModel.CurrentTimePeriod = TimePeriod.Daily;
                    
                    // --- REVISED: Load data safely on UI thread using async/await --- 
                    DispatcherQueue.TryEnqueue(async () => { // Make lambda async
                        try
                        {
                            // Show loading indicator
                            if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Visible;
                            
                            // Short delay to allow UI to update
                            await Task.Delay(50); 
                            
                            // Load the data directly on UI thread
                            LoadRecordsForDate(_viewModel.SelectedDate);
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
                
                _viewModel.SelectedDate = start;
                _viewModel.SelectedEndDate = end;
            _viewModel.IsDateRangeSelected = true;
            
            // Update button text
            UpdateDatePickerButtonText();
            
            // For Last 7 days, ensure we're in Weekly time period and force Daily view
                bool isLast7Days = (_viewModel.SelectedDate == today.AddDays(-6) && _viewModel.SelectedEndDate == today);
                
                bool isLast30Days = (_viewModel.SelectedDate == today.AddDays(-29) && _viewModel.SelectedEndDate == today);
                bool isThisMonth = (_viewModel.SelectedDate == new DateTime(today.Year, today.Month, 1) && _viewModel.SelectedEndDate == today);
                
                if (isLast7Days)
            {
                _viewModel.CurrentTimePeriod = TimePeriod.Weekly;
                _viewModel.CurrentChartViewMode = ChartViewMode.Daily;
                
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
                        LoadRecordsForDateRange(_viewModel.SelectedDate, _viewModel.SelectedEndDate.Value);
                        
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
                _viewModel.CurrentTimePeriod = TimePeriod.Custom;
                _viewModel.CurrentChartViewMode = ChartViewMode.Daily;
                DispatcherQueue.TryEnqueue(async () => {
                    try
                    {
                        if (ViewModeLabel != null) ViewModeLabel.Text = "Daily View";
                        if (ViewModePanel != null) ViewModePanel.Visibility = Visibility.Collapsed;

                        if (LoadingIndicator != null) LoadingIndicator.Visibility = Visibility.Visible;
                        await Task.Delay(50);

                        LoadRecordsForDateRange(_viewModel.SelectedDate, _viewModel.SelectedEndDate.Value);

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
                        LoadRecordsForDateRange(_viewModel.SelectedDate, _viewModel.SelectedEndDate.Value);
                        
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
            // Persist current session before exiting.
            try
            {
                PrepareForSuspend();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error preparing for suspend on exit: {ex.Message}");
            }

            // Ensure proper cleanup and exit on the UI thread
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

        private async void OnResetDataRequested(object? sender, EventArgs e)
        {
            if (_disposed) return;

            var confirm = new ContentDialog
            {
                Title             = "Erase all screen-time data?",
                Content           = "This permanently deletes every recorded session. Tracking will restart from zero.",
                PrimaryButtonText = "Delete",
                CloseButtonText   = "Cancel",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = this.Content?.XamlRoot
            };

            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            try
            {
                _trackingService?.StopTracking();
                bool ok = _databaseService?.WipeDatabase() ?? false;

                // Clear in-memory collections BEFORE restarting tracking so the first new slice repopulates immediately
                _usageRecords?.Clear();
                _viewModel?.AggregatedRecords?.Clear();
                UpdateUsageChart();
                UpdateSummaryTab();

                _trackingService?.StartTracking();

                await new ContentDialog
                {
                    Title           = ok ? "Data deleted" : "Deletion failed",
                    Content         = ok ? "All records were removed." : "Could not clear the database.",
                    CloseButtonText = "OK",
                    XamlRoot        = this.Content?.XamlRoot
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error wiping database: {ex.Message}");
                await new ContentDialog
                {
                    Title           = "Error",
                    Content         = $"Could not delete data: {ex.Message}",
                    CloseButtonText = "OK",
                    XamlRoot        = this.Content?.XamlRoot
                }.ShowAsync();
            }
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

    }
}



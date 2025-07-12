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
using ScreenTimeTracker.Infrastructure;

namespace ScreenTimeTracker
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public sealed partial class MainWindow : Window, IDisposable
    {
        private readonly WindowTrackingService _trackingService;
        private readonly DatabaseService? _databaseService;
        // Forward-only property – removes the redundant in-memory copy.
        private ObservableCollection<AppUsageRecord> _usageRecords => _viewModel.Records;
        private bool _disposed;
        private bool _iconsRefreshedOnce = false;
        
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

        // Initialised inline so property wrappers above can work immediately
        private readonly MainViewModel _viewModel = new MainViewModel();

        private readonly IconRefreshService _iconService;

        private readonly UsageAggregationService _aggregationService;

        private DispatcherTimer? _missingIconTimer; // retries missing app icons

        // --- ViewModel-backed state properties ---
        private DateTime _selectedDate
        {
            get => _viewModel.SelectedDate;
            set => _viewModel.SelectedDate = value;
        }

        private DateTime? _selectedEndDate
        {
            get => _viewModel.SelectedEndDate;
            set => _viewModel.SelectedEndDate = value;
        }

        private bool _isDateRangeSelected
        {
            get => _viewModel.IsDateRangeSelected;
            set => _viewModel.IsDateRangeSelected = value;
        }

        private TimePeriod _currentTimePeriod
        {
            get => _viewModel.CurrentTimePeriod;
            set => _viewModel.CurrentTimePeriod = value;
        }

        private ChartViewMode _currentChartViewMode
        {
            get => _viewModel.CurrentChartViewMode;
            set => _viewModel.CurrentChartViewMode = value;
        }

        private DispatcherTimer _updateTimer;
        private DispatcherTimer _autoSaveTimer;

        // Event bus subscriptions disposables
        private IDisposable? _busSubA;
        private IDisposable? _busSubB;

        public MainWindow()
        {
            _disposed = false;
            
            // Ensure today's date is valid
            DateTime todayDate = DateTime.Today;
            System.Diagnostics.Debug.WriteLine($"[LOG] System time check - Today: {todayDate:yyyy-MM-dd}, Now: {DateTime.Now}");
            
            // Use today's date
            _selectedDate = todayDate;
            
            InitializeComponent();

            // After InitializeComponent, set DataContext
            if (Content is FrameworkElement fe)
            {
                fe.DataContext = _viewModel;
            }

            // _usageRecords now directly references ViewModel.Records – no alias required.

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

            // Subscribe to central event bus instead of direct service events
            _busSubA = ScreenTimeTracker.Infrastructure.ScreenyEventBus.Instance.Subscribe<ScreenTimeTracker.Infrastructure.WindowChangedMessage>(_ => TrackingService_WindowChanged(this, EventArgs.Empty));
            _busSubB = ScreenTimeTracker.Infrastructure.ScreenyEventBus.Instance.Subscribe<ScreenTimeTracker.Models.AppUsageRecord>(rec => TrackingService_UsageRecordUpdated(this, rec));
            
            // Handle window closing
            this.Closed += (sender, args) =>
            {
                Dispose();
            };

            // In WinUI 3, use a loaded handler directly in the constructor
            FrameworkElement root = (FrameworkElement)Content;
            root.Loaded += MainWindow_Loaded;
            
            // Initialize the date picker popup
            _datePickerPopup = new DatePickerPopup(this);
            _datePickerPopup.SingleDateSelected += DatePickerPopup_SingleDateSelected;
            _datePickerPopup.DateRangeSelected += DatePickerPopup_DateRangeSelected;

            // Get the AppWindow and subscribe to Closing event
            _appWindow = GetAppWindowForCurrentWindow();
            _appWindow.Closing += AppWindow_Closing;

            // Indicator visuals are data-bound; no imperative call needed

            // Instantiate icon service
            _iconService = new IconRefreshService();

            // ViewModel is already kept in sync via property wrappers.

            // Initialize timer fields
            _updateTimer = new DispatcherTimer();
            _autoSaveTimer = new DispatcherTimer();

            // Configure primary UI update timer (1-second pulse)
            _updateTimer.Interval = TimeSpan.FromSeconds(1);
            _updateTimer.Tick += UpdateTimer_Tick;

            // Configure auto-save/maintenance timer (5-minute pulse)
            _autoSaveTimer.Interval = TimeSpan.FromMinutes(5);
            _autoSaveTimer.Tick += AutoSaveTimer_Tick;

            // Removed: _chartRefreshTimer – consolidated into 1-second UI timer

            // Timer to retry loading icons that are still null
            _missingIconTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _missingIconTimer.Tick += MissingIconTimer_Tick;
            _missingIconTimer.Start();

            // Hook ViewModel command events
            _viewModel.OnStartTrackingRequested    += (_, __) => BeginTracking();
            _viewModel.OnEndTrackingRequested     += (_, __) => EndTracking();
            _viewModel.OnToggleTrackingRequested   += (_, __) =>
            {
                if (_trackingService.IsTracking) EndTracking(); else BeginTracking();
            };
            _viewModel.OnPickDateRequested         += (_, __) =>
            {
                _datePickerPopup?.ShowDatePicker(DatePickerButton, _selectedDate, _selectedEndDate, _isDateRangeSelected);
            };
            _viewModel.OnToggleViewModeRequested   += (_, __) =>
            {
                if (_currentChartViewMode == ChartViewMode.Hourly)
                    _currentChartViewMode = ChartViewMode.Daily;
                else
                    _currentChartViewMode = ChartViewMode.Hourly;
                UpdateUsageChart();
            };

            _aggregationService = new UsageAggregationService(_databaseService, _trackingService);
        }

        private void SubclassWindow()
        {
            if (_hWnd == IntPtr.Zero)
            {
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
                 _newWndProcDelegate = null; // Clear delegate if failed
            }
             else
             {
                 // Removed verbose log
             }
        }

        private void RestoreWindowProc()
        {
             if (_hWnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
             {
                 SetWindowLongPtr(_hWnd, GWLP_WNDPROC, _oldWndProc);
                 _oldWndProc = IntPtr.Zero;
                 _newWndProcDelegate = null; // Allow delegate to be garbage collected
                 // Removed verbose log
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
                    _trackingService?.PauseTrackingForSuspend();
                }
                else if (data == 1) // Display turning on (potential resume)
                {
                     _trackingService?.ResumeTrackingAfterSuspend();
                }
                 else
                 {
                      // Removed verbose log
                 }
            }
            else if (settingGuid == GuidSystemAwayMode)
            {
                // 1 = Entering Away Mode, 0 = Exiting Away Mode
                if (data == 1) // Entering away mode (sleep)
                {
                     _trackingService?.PauseTrackingForSuspend();
                }
                else if (data == 0) // Exiting away mode (resume)
                {
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
            try
            {
                ThrowIfDisposed(); // Check if already disposed early

                // --- Moved from constructor: Setup subclassing and tray icon --- 
                if (_hWnd != IntPtr.Zero)
                {
                    SubclassWindow(); // Subclass here
                    _trayIconHelper = new TrayIconHelper(_hWnd); // Initialize here
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
                    // Removed verbose log
                }
                // --- End moved section ---

                // Set up window title and icon using the helper
                _windowHelper.SetUpWindow(); 
            
                // Set the custom XAML TitleBar element using the Window's SetTitleBar method
                if (AppTitleBar != null) // Check if the XAML element exists
                {
                    this.SetTitleBar(AppTitleBar); // Correct: Call SetTitleBar on the Window itself
                    // Removed verbose log
                }
                else
                {
                    // Removed verbose log
                }

                // Double-check our selected date is valid (not in the future)
                if (_selectedDate > DateTime.Today)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOG] WARNING: Future date detected at load time: {_selectedDate:yyyy-MM-dd}");
                    _selectedDate = DateTime.Today;
                    System.Diagnostics.Debug.WriteLine($"[LOG] Corrected to: {_selectedDate:yyyy-MM-dd}");
                }
                
                // Set up UI elements
                // Check if this is the first run
                CheckFirstRun();
                
                // Set today's date and update button text
                _selectedDate = DateTime.Today;
                _currentTimePeriod = TimePeriod.Daily; // Default to daily view
                // UpdateDatePickerButtonText(); // Removed as per edit hint
                
                // Set the selected date display
                // if (DateDisplay != null) // Removed as per edit hint
                // {
                //     DateDisplay.Text = "Today";
                // }
                
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
                    // if (ViewModePanel != null) // Removed as per edit hint
                    // {
                    //     ViewModePanel.Visibility = Visibility.Collapsed;
                    // }
                });

                // Register for power notifications AFTER window handle is valid and before tracking starts
                RegisterPowerNotifications();

                // Add the tray icon with appropriate tooltip based on startup mode
                string trayTooltip = App.StartedFromWindowsStartup ? 
                    "Screeny - Running in background" : 
                    "Screeny - Tracking";
                _trayIconHelper?.AddIcon(trayTooltip);

                // Start tracking automatically (assuming StartTracking handles its internal errors)
                BeginTracking();
                
                // Removed verbose logs for window loaded status

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
            // Removed verbose log
        }

        // TrackingService_WindowChanged implementation moved to MainWindow.UI.cs

        // Event handlers for DatePickerPopup events
        private void DatePickerPopup_SingleDateSelected(object? sender, DateTime selectedDate)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"DatePickerPopup_SingleDateSelected: Selected date: {selectedDate:yyyy-MM-dd}, CurrentTimePeriod: {_currentTimePeriod}");
                
                _selectedDate = selectedDate;
                _selectedEndDate = null;
                _isDateRangeSelected = false;
                
                // Update ViewModel properties instead of direct UI
                _viewModel.DateDisplayText = (selectedDate == DateTime.Today) ? "Today" : selectedDate.ToString("MMM d, yyyy");
                _viewModel.IsViewModePanelVisible = true;
                
                LoadRecordsForDate(selectedDate);
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
            
            // Update ViewModel properties
            _viewModel.DateDisplayText = $"{start:MMM d} - {end:MMM d}";
            _viewModel.IsViewModePanelVisible = false;
            
            LoadRecordsForDateRange(start, end);
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
            // Removed tray icon verbose logs
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
                        
                        // Removed verbose log
                    }
                    else
                    {
                        // Removed verbose log
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
            // Removed tray icon verbose logs
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
                EndTracking();
                bool ok = _databaseService?.WipeDatabase() ?? false;

                // Clear in-memory collections BEFORE restarting tracking so the first new slice repopulates immediately
                _usageRecords.Clear();
                _viewModel.AggregatedRecords.Clear();

                _viewModel.UpdateSummary(TimeSpan.Zero, TimeSpan.Zero, "None", TimeSpan.Zero, null, null);
                _viewModel.SummaryTitle = "Today's Screen Time Summary";
                _viewModel.IsAverageVisible = false;

                UpdateUsageChart();

                BeginTracking();

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

        // ChartRefreshTimer_Tick removed – chart refresh handled by unified 1-second UI timer

        private void MissingIconTimer_Tick(object? sender, object e)
        {
            try
            {
                foreach (var rec in _usageRecords)
                {
                    if (rec.AppIcon == null)
                    {
                        rec.LoadAppIconIfNeeded();
                    }
                }
            }
            catch { /* ignore */ }
        }
    }
}



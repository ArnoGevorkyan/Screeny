using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;
using System.Collections.ObjectModel;
using ScreenTimeTracker.Services;
using ScreenTimeTracker.Models;
using System.Runtime.InteropServices;
using System.Linq;

namespace ScreenTimeTracker
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public sealed partial class MainWindow : Window, IDisposable
    {
        private readonly WindowTrackingService _trackingService;
        private readonly ObservableCollection<AppUsageRecord> _usageRecords;
        private AppWindow? _appWindow;
        private OverlappedPresenter? _presenter;
        private bool _isMaximized = false;
        private DateTime _selectedDate;
        private DispatcherTimer _updateTimer;
        private bool _disposed;

        // Add these Win32 API declarations at the top of the class
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int IMAGE_ICON = 1;
        private const int LR_LOADFROMFILE = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType,
                                            int cxDesired, int cyDesired, uint fuLoad);

        public MainWindow()
        {
            InitializeComponent();

            // Set up window
            _appWindow = GetAppWindowForCurrentWindow();
            if (_appWindow != null)
            {
                _appWindow.Title = "Screen Time Tracker";
                _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                // Set the window icon
                IntPtr windowHandle = WindowNative.GetWindowHandle(this);
                WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
                if (File.Exists(iconPath))
                {
                    SendMessage(windowHandle, WM_SETICON, ICON_SMALL, LoadImage(IntPtr.Zero, iconPath,
                        IMAGE_ICON, 0, 0, LR_LOADFROMFILE));
                    SendMessage(windowHandle, WM_SETICON, ICON_BIG, LoadImage(IntPtr.Zero, iconPath,
                        IMAGE_ICON, 0, 0, LR_LOADFROMFILE));
                }

                // Set up presenter
                _presenter = _appWindow.Presenter as OverlappedPresenter;
                if (_presenter != null)
                {
                    _presenter.IsResizable = true;
                    _presenter.IsMaximizable = true;
                    _presenter.IsMinimizable = true;
                }

                // Set default size
                var display = DisplayArea.Primary;
                var scale = GetScaleAdjustment();
                _appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = (int)(1000 * scale), Height = (int)(600 * scale) });
            }

            // Initialize collections
            _usageRecords = new ObservableCollection<AppUsageRecord>();
            UsageListView.ItemsSource = _usageRecords;

            // Initialize date
            _selectedDate = DateTime.Today;
            DatePicker.Date = _selectedDate;

            // Initialize services
            _trackingService = new WindowTrackingService();
            _trackingService.UsageRecordUpdated += TrackingService_UsageRecordUpdated;
            
            // Set up timer for duration updates
            _updateTimer = new DispatcherTimer();
            _updateTimer.Interval = TimeSpan.FromSeconds(1);
            _updateTimer.Tick += UpdateTimer_Tick;

            // Handle window closing
            this.Closed += (sender, args) =>
            {
                Dispose();
            };
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
                
                // Clear collections
                _usageRecords.Clear();
                
                // Dispose tracking service
                _trackingService.Dispose();
                
                // Remove event handlers
                _updateTimer.Tick -= UpdateTimer_Tick;
                _trackingService.UsageRecordUpdated -= TrackingService_UsageRecordUpdated;

                _disposed = true;
            }
        }

        private void UpdateTimer_Tick(object? sender, object e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Timer tick - updating durations");
                
                // Get the focused record first
                var focusedRecord = _usageRecords.FirstOrDefault(r => r.IsFocused);
                if (focusedRecord != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Updating focused record: {focusedRecord.ProcessName}");
                    focusedRecord.UpdateDuration();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in timer tick: {ex}");
            }
        }

        private double GetScaleAdjustment()
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            DisplayArea displayArea = DisplayArea.GetFromWindowId(wndId, DisplayAreaFallback.Primary);
            return displayArea.OuterBounds.Height / displayArea.WorkArea.Height;
        }

        private AppWindow GetAppWindowForCurrentWindow()
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(wndId);
        }

        private void TrackingService_UsageRecordUpdated(object? sender, AppUsageRecord record)
        {
            if (!record.ShouldTrack) return;
            if (!record.IsFromDate(_selectedDate)) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                System.Diagnostics.Debug.WriteLine($"Updating UI for record: {record.ProcessName}");
                
                // Check if the record is already in the collection
                var existingRecord = _usageRecords.FirstOrDefault(r => 
                    r.ProcessId == record.ProcessId && 
                    r.WindowTitle == record.WindowTitle &&
                    r.WindowHandle == record.WindowHandle);

                if (existingRecord == null)
                {
                    _usageRecords.Add(record);
                }
            });
        }

        private void DatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            if (args.NewDate.HasValue)
            {
                _selectedDate = args.NewDate.Value.Date;
                // For real-time view, you might clear and reload your list here.
                // For simplicity, we do nothing if _selectedDate equals today.
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            ThrowIfDisposed();
            System.Diagnostics.Debug.WriteLine("Starting tracking");
            
            // First start the update timer
            if (!_updateTimer.IsEnabled)
            {
                System.Diagnostics.Debug.WriteLine("Starting UI update timer");
                _updateTimer.Start();
            }

            // Then start the tracking service
            _trackingService.StartTracking();
            
            // Update UI state
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            
            // Automatically minimize the tracker window so that external apps become active.
            if (_presenter != null)
            {
                _presenter.Minimize();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            ThrowIfDisposed();
            _trackingService.StopTracking();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _updateTimer.Stop();

            // Clear focus state for all records.
            foreach (var record in _usageRecords)
            {
                record.SetFocus(false);
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_presenter != null)
            {
                _presenter.Minimize();
            }
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_presenter == null) return;

            if (_isMaximized)
            {
                _presenter.Restore();
                _isMaximized = false;
            }
            else
            {
                _presenter.Maximize();
                _isMaximized = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _trackingService.StopTracking();
            this.Close();
        }
    }
}
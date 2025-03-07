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
using System.Collections.Generic;

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

            // Handle special cases like Java applications
            HandleSpecialCases(record);

            DispatcherQueue.TryEnqueue(() =>
            {
                System.Diagnostics.Debug.WriteLine($"Updating UI for record: {record.ProcessName} ({record.WindowTitle})");
                
                AppUsageRecord? existingRecord = FindExistingRecord(record);

                if (existingRecord != null)
                {
                    // Update the existing record instead of adding a new one
                    existingRecord.SetFocus(record.IsFocused);
                    
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
                        }
                    }
                }
                else
                {
                    // If we're adding a new focused record, unfocus all other records
                    if (record.IsFocused)
                    {
                        foreach (var otherRecord in _usageRecords.Where(r => r.IsFocused))
                        {
                            otherRecord.SetFocus(false);
                        }
                    }
                    
                    _usageRecords.Add(record);
                }
            });
        }
        
        private AppUsageRecord? FindExistingRecord(AppUsageRecord record)
        {
            // Try several strategies to find a match
            
            // 1. Exact match by window handle (most reliable)
            var exactMatch = _usageRecords.FirstOrDefault(r => r.WindowHandle == record.WindowHandle);
            if (exactMatch != null) return exactMatch;
            
            // 2. Match by process ID and similar window title
            var pidMatch = _usageRecords.FirstOrDefault(r => 
                r.ProcessId == record.ProcessId && 
                IsSimilarWindowTitle(r.WindowTitle, record.WindowTitle));
            if (pidMatch != null) return pidMatch;
            
            // 3. Match by exact process name and window title
            var exactNameAndTitleMatch = _usageRecords.FirstOrDefault(r => 
                r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                r.WindowTitle.Equals(record.WindowTitle, StringComparison.OrdinalIgnoreCase));
            if (exactNameAndTitleMatch != null) return exactNameAndTitleMatch;
            
            // 4. Match by process name and related window
            var relatedWindowMatch = _usageRecords.FirstOrDefault(r => 
                r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                IsRelatedWindow(r.WindowTitle, record.WindowTitle));
            if (relatedWindowMatch != null) return relatedWindowMatch;
            
            // 5. For special applications like browsers, match just by process name
            if (IsApplicationThatShouldConsolidate(record.ProcessName))
            {
                var nameOnlyMatch = _usageRecords.FirstOrDefault(r => 
                    r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase));
                if (nameOnlyMatch != null) return nameOnlyMatch;
            }
            
            // No match found
            return null;
        }
        
        private bool IsSimilarWindowTitle(string title1, string title2)
        {
            // If either title is empty, they can't be similar
            if (string.IsNullOrEmpty(title1) || string.IsNullOrEmpty(title2))
                return false;
                
            // Check if one title contains the other
            if (title1.Contains(title2, StringComparison.OrdinalIgnoreCase) ||
                title2.Contains(title1, StringComparison.OrdinalIgnoreCase))
                return true;
                
            // Check for very similar titles (over 80% similarity)
            if (GetSimilarity(title1, title2) > 0.8)
                return true;
                
            // Check if they follow the pattern "Document - Application"
            return IsRelatedWindow(title1, title2);
        }
        
        private double GetSimilarity(string a, string b)
        {
            // A simple string similarity measure based on shared words
            var wordsA = a.ToLower().Split(new[] { ' ', '-', '_', ':', '|', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var wordsB = b.ToLower().Split(new[] { ' ', '-', '_', ':', '|', '.' }, StringSplitOptions.RemoveEmptyEntries);
            
            int sharedWords = wordsA.Intersect(wordsB).Count();
            int totalWords = Math.Max(wordsA.Length, wordsB.Length);
            
            return totalWords == 0 ? 0 : (double)sharedWords / totalWords;
        }
        
        private bool IsApplicationThatShouldConsolidate(string processName)
        {
            // List of applications that should be consolidated even with different window titles
            string[] consolidateApps = new string[]
            {
                "chrome", "firefox", "msedge", "iexplore", "opera", "brave", "arc", // Browsers
                "winword", "excel", "powerpnt", "outlook", // Office
                "code", "vscode", "devenv", // Code editors
                "minecraft", // Games
                "spotify", "discord", "slack" // Media/Communication apps
            };
            
            return consolidateApps.Any(app => 
                processName.Equals(app, StringComparison.OrdinalIgnoreCase) || 
                processName.Contains(app, StringComparison.OrdinalIgnoreCase));
        }

        private void HandleSpecialCases(AppUsageRecord record)
        {
            // Handle known browsers
            if (IsBrowser(record.ProcessName))
            {
                string detectedBrowser = DetectBrowserType(record.ProcessName, record.WindowTitle);
                if (!string.IsNullOrEmpty(detectedBrowser))
                {
                    record.ProcessName = detectedBrowser;
                    System.Diagnostics.Debug.WriteLine($"Detected browser: {detectedBrowser}");
                }
            }
            // Handle Java applications 
            else if (record.ProcessName.Equals("javaw", StringComparison.OrdinalIgnoreCase) ||
                record.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase))
            {
                // Check for Java-based games by window title
                if (IsJavaBasedGame(record.WindowTitle))
                {
                    // Extract game name from window title
                    string gameName = ExtractGameNameFromTitle(record.WindowTitle);
                    if (!string.IsNullOrEmpty(gameName))
                    {
                        record.ProcessName = gameName;
                        System.Diagnostics.Debug.WriteLine($"Detected Java game: {gameName} from title: {record.WindowTitle}");
                    }
                    else
                    {
                        // Default to "Minecraft" if we can't determine specific game
                        record.ProcessName = "Minecraft";
                    }
                }
                // Check for Java IDEs
                else if (record.WindowTitle.Contains("Eclipse", StringComparison.OrdinalIgnoreCase))
                {
                    record.ProcessName = "Eclipse";
                }
                else if (record.WindowTitle.Contains("IntelliJ", StringComparison.OrdinalIgnoreCase))
                {
                    record.ProcessName = "IntelliJ IDEA";
                }
                else if (record.WindowTitle.Contains("NetBeans", StringComparison.OrdinalIgnoreCase))
                {
                    record.ProcessName = "NetBeans";
                }
            }
            
            // Handle Python applications
            else if (record.ProcessName.Equals("python", StringComparison.OrdinalIgnoreCase) ||
                     record.ProcessName.Equals("pythonw", StringComparison.OrdinalIgnoreCase))
            {
                // Check window title for clues about which Python app is running
                if (record.WindowTitle.Contains("PyCharm", StringComparison.OrdinalIgnoreCase))
                {
                    record.ProcessName = "PyCharm";
                }
                else if (record.WindowTitle.Contains("Jupyter", StringComparison.OrdinalIgnoreCase))
                {
                    record.ProcessName = "Jupyter Notebook";
                }
            }
            
            // Handle node.js applications
            else if (record.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                if (record.WindowTitle.Contains("VS Code", StringComparison.OrdinalIgnoreCase))
                {
                    record.ProcessName = "VS Code";
                }
            }
        }
        
        private bool IsBrowser(string processName)
        {
            string[] browsers = { "chrome", "firefox", "msedge", "iexplore", "opera", "brave", "arc" };
            return browsers.Any(b => processName.Equals(b, StringComparison.OrdinalIgnoreCase) || 
                                    processName.Contains(b, StringComparison.OrdinalIgnoreCase));
        }
        
        private string DetectBrowserType(string processName, string windowTitle)
        {
            // Map processes to browser names
            if (processName.Contains("chrome", StringComparison.OrdinalIgnoreCase))
                return "Chrome";
            if (processName.Contains("firefox", StringComparison.OrdinalIgnoreCase))
                return "Firefox";
            if (processName.Contains("msedge", StringComparison.OrdinalIgnoreCase))
                return "Microsoft Edge";
            if (processName.Contains("iexplore", StringComparison.OrdinalIgnoreCase))
                return "Internet Explorer";
            if (processName.Contains("opera", StringComparison.OrdinalIgnoreCase))
                return "Opera";
            if (processName.Contains("brave", StringComparison.OrdinalIgnoreCase))
                return "Brave";
            if (processName.Contains("arc", StringComparison.OrdinalIgnoreCase))
                return "Arc";
                
            // Extract from window title if possible
            if (windowTitle.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
                return "Chrome";
            if (windowTitle.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
                return "Firefox";
            if (windowTitle.Contains("Edge", StringComparison.OrdinalIgnoreCase))
                return "Microsoft Edge";
            if (windowTitle.Contains("Internet Explorer", StringComparison.OrdinalIgnoreCase))
                return "Internet Explorer";
            if (windowTitle.Contains("Opera", StringComparison.OrdinalIgnoreCase))
                return "Opera";
            if (windowTitle.Contains("Brave", StringComparison.OrdinalIgnoreCase))
                return "Brave";
            if (windowTitle.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                return "Arc";
                
            // No specific browser detected
            return string.Empty;
        }
        
        private bool IsJavaBasedGame(string windowTitle)
        {
            // List of common Java-based game keywords
            string[] gameKeywords = {
                "Minecraft", "MC", "Forge", "Fabric", "CraftBukkit", "Spigot", "Paper", "Optifine",
                "Game", "Server", "Client", "Launcher", "Mod", "MultiMC", "TLauncher", "Vime"
            };
            
            return gameKeywords.Any(keyword => 
                windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        
        private string ExtractGameNameFromTitle(string windowTitle)
        {
            // A map of known game title patterns to game names
            Dictionary<string, string> gameTitlePatterns = new Dictionary<string, string>
            {
                { "Minecraft", "Minecraft" },
                { "Forge", "Minecraft" },
                { "Fabric", "Minecraft" },
                { "Vime", "Minecraft" },
                { "MC", "Minecraft" }
            };
            
            foreach (var pattern in gameTitlePatterns.Keys)
            {
                if (windowTitle.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return gameTitlePatterns[pattern];
                }
            }
            
            return string.Empty;
        }

        private bool IsRelatedWindow(string title1, string title2)
        {
            // Check if two window titles appear to be from the same application
            // This helps consolidate things like "Document1 - Word" and "Document2 - Word"
            
            // Check if both titles end with the same application name
            string[] separators = { " - ", " – ", " | " };
            
            foreach (var separator in separators)
            {
                // Check if both titles have the separator
                if (title1.Contains(separator) && title2.Contains(separator))
                {
                    // Get the app name (usually after the last separator)
                    string app1 = title1.Substring(title1.LastIndexOf(separator) + separator.Length);
                    string app2 = title2.Substring(title2.LastIndexOf(separator) + separator.Length);
                    
                    if (app1.Equals(app2, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            
            return false;
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

        private void UsageListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is AppUsageRecord record)
            {
                System.Diagnostics.Debug.WriteLine($"Container changing for {record.ProcessName}, has icon: {record.AppIcon != null}");
                
                // Get the container and find the UI elements
                if (args.ItemContainer?.ContentTemplateRoot is Grid grid)
                {
                    var placeholderIcon = grid.FindName("PlaceholderIcon") as FontIcon;
                    var appIconImage = grid.FindName("AppIconImage") as Image;
                    
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

        private void UpdateIconVisibility(AppUsageRecord record, FontIcon placeholder, Image iconImage)
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
    }
}
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
using SDColor = System.Drawing.Color; // Alias for System.Drawing.Color

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
        private AppWindow? _appWindow;
        private OverlappedPresenter? _presenter;
        private bool _isMaximized = false;
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
            // Configure LiveCharts defaults
            LiveChartsSettings.ConfigureTheme();
        }
        
        public enum TimePeriod
        {
            Daily,
            Weekly
        }

        public enum ChartViewMode
        {
            Hourly,
            Daily
        }

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

        // Fields for the custom date picker
        private Popup? _datePickerPopup;
        private CalendarView? _customCalendarView;
        private Button? _todayButton;
        private Button? _yesterdayButton;
        private Button? _last7DaysButton;
        private Button? _last30DaysButton;
        private Button? _thisMonthButton;

        public MainWindow()
        {
            _disposed = false;
            _selectedDate = DateTime.Today;
            _usageRecords = new ObservableCollection<AppUsageRecord>();
            
            // Initialize timer fields to avoid nullable warnings
            _updateTimer = new DispatcherTimer();
            _autoSaveTimer = new DispatcherTimer();

            InitializeComponent();

            // Configure window
            SetUpWindow();

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

        private double GetScaleAdjustment()
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            Microsoft.UI.Windowing.DisplayArea displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(wndId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            return displayArea.OuterBounds.Height / displayArea.WorkArea.Height;
        }

        private void TrackingService_UsageRecordUpdated(object? sender, AppUsageRecord record)
        {
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

            // Handle special cases like Java applications
            HandleSpecialCases(record);

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

        private AppUsageRecord? FindExistingRecord(AppUsageRecord record)
        {
            // Try several strategies to find a match

            // 1. Exact match by window handle (most reliable)
            var exactMatch = _usageRecords.FirstOrDefault(r => r.WindowHandle == record.WindowHandle);
            if (exactMatch != null) return exactMatch;

            // 2. Match by process name and process ID - handles multiple instances of same app with different windows
            var pidAndNameMatch = _usageRecords.FirstOrDefault(r =>
                r.ProcessId == record.ProcessId &&
                r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (pidAndNameMatch != null) return pidAndNameMatch;

            // 3. Match by extracted application name to handle apps with multiple processes (like Telegram, Discord)
            string baseAppName = GetBaseAppName(record.ProcessName);
            var appNameMatch = _usageRecords.FirstOrDefault(r =>
                GetBaseAppName(r.ProcessName).Equals(baseAppName, StringComparison.OrdinalIgnoreCase));
            if (appNameMatch != null) return appNameMatch;

            // 4. Match by process name and similar window title (for MDI applications)
            var similarTitleMatch = _usageRecords.FirstOrDefault(r =>
                r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                IsSimilarWindowTitle(r.WindowTitle, record.WindowTitle));
            if (similarTitleMatch != null) return similarTitleMatch;

            // 5. For special applications like browsers, match just by application type
            if (IsApplicationThatShouldConsolidate(record.ProcessName))
            {
                var nameOnlyMatch = _usageRecords.FirstOrDefault(r =>
                    r.ProcessName.Equals(record.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                    IsAlternateProcessNameForSameApp(r.ProcessName, record.ProcessName));
                if (nameOnlyMatch != null) return nameOnlyMatch;
            }

            // No match found
            return null;
        }

        private string GetBaseAppName(string processName)
        {
            // Extract the base application name (removing numbers, suffixes, etc.)
            if (string.IsNullOrEmpty(processName)) return processName;

            // Remove common process suffixes
            string cleanName = processName.ToLower()
                .Replace("64", "")
                .Replace("32", "")
                .Replace("x86", "")
                .Replace("x64", "")
                .Replace(" (x86)", "")
                .Replace(" (x64)", "");

            // Match common app variations
            if (cleanName.StartsWith("telegram"))
                return "telegram";
            if (cleanName.StartsWith("discord"))
                return "discord";
            if (cleanName.Contains("chrome") || cleanName.Contains("chromium"))
                return "chrome";
            if (cleanName.Contains("firefox"))
                return "firefox";
            if (cleanName.Contains("devenv") || cleanName.Contains("visualstudio"))
                return "visualstudio";
            if (cleanName.Contains("code") || cleanName.Contains("vscode"))
                return "vscode";

            return cleanName;
        }

        private bool IsAlternateProcessNameForSameApp(string name1, string name2)
        {
            // Check if two different process names might belong to the same application
            if (string.IsNullOrEmpty(name1) || string.IsNullOrEmpty(name2))
                return false;

            // Convert to lowercase for case-insensitive comparison
            name1 = name1.ToLower();
            name2 = name2.ToLower();

            // Define groups of related process names
            var processGroups = new List<HashSet<string>>
            {
                new HashSet<string> { "telegram", "telegramdesktop", "telegram.exe", "updater" },
                new HashSet<string> { "discord", "discordptb", "discordcanary", "discord.exe", "update.exe" },
                new HashSet<string> { "chrome", "chrome.exe", "chromedriver", "chromium" },
                new HashSet<string> { "firefox", "firefox.exe", "firefoxdriver" },
                new HashSet<string> { "visualstudio", "devenv", "devenv.exe", "msvsmon", "vshost", "vs_installer" },
                new HashSet<string> { "code", "code.exe", "vscode", "vscode.exe", "code - insiders" },
                new HashSet<string> { "whatsapp", "whatsapp.exe", "whatsappdesktop", "electron" }
            };

            // Check if the two names are in the same group
            return processGroups.Any(group => group.Contains(name1) && group.Contains(name2));
        }

        private bool IsApplicationThatShouldConsolidate(string processName)
        {
            // List of applications that should be consolidated even with different window titles
            string[] consolidateApps = new string[]
            {
                "chrome", "firefox", "msedge", "iexplore", "opera", "brave", "arc", // Browsers
                "winword", "excel", "powerpnt", "outlook", // Office
                "code", "vscode", "devenv", "visualstudio", // Code editors
                "minecraft", // Games
                "spotify", "discord", "slack", "telegram", "whatsapp", "teams", "skype", // Communication apps
                "explorer", "firefox", "chrome", "edge" // Common apps
            };

            // Extract base name for broader matching
            string baseName = GetBaseAppName(processName);

            return consolidateApps.Any(app =>
                processName.Contains(app, StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains(app, StringComparison.OrdinalIgnoreCase));
        }

        private void HandleSpecialCases(AppUsageRecord record)
        {
            // WhatsApp detection (various process names based on install method)
            if (record.ProcessName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
                record.WindowTitle.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
                record.ProcessName.Contains("WhatsAppDesktop", StringComparison.OrdinalIgnoreCase) ||
                record.ProcessName.Contains("Electron", StringComparison.OrdinalIgnoreCase) &&
                record.WindowTitle.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
            {
                record.ProcessName = "WhatsApp";
                return;
            }

            // Visual Studio detection (runs as 'devenv')
            if (record.ProcessName.Equals("devenv", StringComparison.OrdinalIgnoreCase))
            {
                record.ProcessName = "Visual Studio";
                return;
            }

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

            // Handle common application renames
            RenameCommonApplications(record);
        }

        private void RenameCommonApplications(AppUsageRecord record)
        {
            // A generic approach to rename common applications to better names

            // WhatsApp related processes
            if (record.ProcessName.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
                (record.ProcessName.Contains("Electron", StringComparison.OrdinalIgnoreCase) &&
                 record.WindowTitle.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)))
            {
                record.ProcessName = "WhatsApp";
                return;
            }

            // Telegram related processes
            if (record.ProcessName.Contains("Telegram", StringComparison.OrdinalIgnoreCase) ||
                record.ProcessName.StartsWith("tg", StringComparison.OrdinalIgnoreCase))
            {
                record.ProcessName = "Telegram";
                return;
            }

            // Visual Studio related processes
            if (record.ProcessName.Contains("msvsmon", StringComparison.OrdinalIgnoreCase) ||
                    record.ProcessName.Contains("vshost", StringComparison.OrdinalIgnoreCase) ||
                    record.ProcessName.Contains("vs_professional", StringComparison.OrdinalIgnoreCase) ||
                    record.ProcessName.Contains("devenv", StringComparison.OrdinalIgnoreCase))
            {
                record.ProcessName = "Visual Studio";
            }

            // VS Code related processes
            else if (record.ProcessName.Contains("Code", StringComparison.OrdinalIgnoreCase) ||
                    record.ProcessName.Contains("VSCode", StringComparison.OrdinalIgnoreCase) ||
                    record.ProcessName.Contains("Code - Insiders", StringComparison.OrdinalIgnoreCase))
            {
                record.ProcessName = "VS Code";
            }

            // Microsoft Office related processes
            else if (record.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
                record.ProcessName = "Word";
            else if (record.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase))
                record.ProcessName = "Excel";
            else if (record.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase))
                record.ProcessName = "PowerPoint";
            else if (record.ProcessName.Equals("OUTLOOK", StringComparison.OrdinalIgnoreCase))
                record.ProcessName = "Outlook";

            // Extract from window title as last resort
            else if (string.IsNullOrEmpty(record.ProcessName) || record.ProcessName.Length <= 3)
            {
                string extractedName = ExtractApplicationNameFromWindowTitle(record.WindowTitle);
                if (!string.IsNullOrEmpty(extractedName))
                {
                    record.ProcessName = extractedName;
                }
            }
        }

        private string ExtractApplicationNameFromWindowTitle(string windowTitle)
        {
            if (string.IsNullOrEmpty(windowTitle)) return string.Empty;

            // Common title patterns
            string[] patterns = { " - ", " – ", " | ", ": " };

            foreach (var pattern in patterns)
            {
                if (windowTitle.Contains(pattern))
                {
                    string[] parts = windowTitle.Split(new[] { pattern }, StringSplitOptions.None);
                    if (parts.Length >= 2)
                    {
                        // Usually the application name is the last part
                        string lastPart = parts[parts.Length - 1].Trim();
                        if (lastPart.Length > 2 && !string.IsNullOrEmpty(lastPart))
                            return lastPart;
                    }
                }
            }

            return string.Empty;
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
            System.Diagnostics.Debug.WriteLine("=== UpdateUsageChart CALLED ===");

            if (UsageChartLive == null)
            {
                System.Diagnostics.Debug.WriteLine("Chart is null, exiting");
                return;
            }

            // Calculate total time for the chart title
            TimeSpan totalTime = TimeSpan.Zero;
            foreach (var record in _usageRecords)
            {
                totalTime += record.Duration;
            }
            ChartTimeValue.Text = FormatTimeSpan(totalTime);
            System.Diagnostics.Debug.WriteLine($"Total time for chart: {totalTime}");
            
            // Get system accent color for chart series
            SKColor seriesColor;
            
            // Try to get the system accent color
            try
            {
                if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object accentColorObj) && 
                    accentColorObj is Windows.UI.Color accentColor)
                {
                    seriesColor = new SKColor(accentColor.R, accentColor.G, accentColor.B);
                }
                else
                {
                    // Default fallback color if system accent color isn't available
                    seriesColor = SKColors.DodgerBlue;
                }
            }
            catch
            {
                // Fallback color if anything goes wrong
                seriesColor = SKColors.DodgerBlue;
            }
            
            // Create a darker color for axis text for better contrast
            SKColor axisColor = SKColors.Black;
            if (Application.Current.RequestedTheme == ApplicationTheme.Dark)
            {
                axisColor = SKColors.White;
            }
            
            if (_currentChartViewMode == ChartViewMode.Hourly)
            {
                System.Diagnostics.Debug.WriteLine("Building HOURLY chart");
                
                // Create a dictionary to store hourly usage
                var hourlyUsage = new Dictionary<int, double>();
                
                // Initialize all hours to 0
                for (int i = 0; i < 24; i++)
                {
                    hourlyUsage[i] = 0;
                }

                // Process all usage records to distribute time by hour
                System.Diagnostics.Debug.WriteLine($"Processing {_usageRecords.Count} records for hourly chart");
                foreach (var record in _usageRecords)
                {
                    // Get the hour from the start time
                    int startHour = record.StartTime.Hour;
                    
                    // Add the duration to the appropriate hour (convert to hours)
                    hourlyUsage[startHour] += record.Duration.TotalHours;
                    
                    System.Diagnostics.Debug.WriteLine($"Record: {record.ProcessName}, Hour: {startHour}, Duration: {record.Duration.TotalHours:F4} hours");
                }

                // Check if all values are zero
                bool allZero = true;
                double maxValue = 0;
                foreach (var value in hourlyUsage.Values)
                {
                    if (value > 0.0001)
                    {
                        allZero = false;
                    }
                    maxValue = Math.Max(maxValue, value);
                }
                
                System.Diagnostics.Debug.WriteLine($"All values zero? {allZero}, Max value: {maxValue:F4}");
                
                // If all values are zero, add a tiny value to the current hour to make the chart visible
                if (allZero || maxValue < 0.001)
                {
                    int currentHour = DateTime.Now.Hour;
                    hourlyUsage[currentHour] = 0.001; // Add a tiny value
                    System.Diagnostics.Debug.WriteLine($"Added tiny value to hour {currentHour}");
                }

                // Set up series and labels for the chart
                var values = new List<double>();
                var labels = new List<string>();
                
                // Create a more concise label format for narrow windows
                bool useShortLabels = UsageChartLive.ActualWidth < 500;
                
                // Add values and labels for each hour (with spacing logic)
                for (int i = 0; i < 24; i++)
                {
                    values.Add(hourlyUsage[i]);
                    
                    // Use spacing logic to prevent label crowding
                    // For narrow screens, we only show labels every 2 or 3 hours
                    if (useShortLabels)
                    {
                        // Very narrow: show label only for 12am, 6am, 12pm, 6pm
                        if (i % 6 == 0)
                        {
                            labels.Add($"{(i % 12 == 0 ? 12 : i % 12)}{(i >= 12 ? "p" : "a")}");
                        }
                        else
                        {
                            labels.Add(""); // Empty label for hours we're skipping
                        }
                    }
                    else if (UsageChartLive.ActualWidth < 700)
                    {
                        // Narrow: show label every 3 hours (12am, 3am, 6am, etc.)
                        if (i % 3 == 0)
                        {
                            labels.Add($"{(i % 12 == 0 ? 12 : i % 12)}{(i >= 12 ? "PM" : "AM")}");
                        }
                        else
                        {
                            labels.Add(""); // Empty label for hours we're skipping
                        }
                    }
                    else if (UsageChartLive.ActualWidth < 900)
                    {
                        // Medium: show label every 2 hours
                        if (i % 2 == 0)
                        {
                            labels.Add($"{(i % 12 == 0 ? 12 : i % 12)}{(i >= 12 ? "PM" : "AM")}");
                        }
                        else
                        {
                            labels.Add(""); // Empty label for hours we're skipping
                        }
                    }
                    else
                    {
                        // Wide: show all hour labels
                        labels.Add($"{(i % 12 == 0 ? 12 : i % 12)} {(i >= 12 ? "PM" : "AM")}");
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"Hour {i}: {hourlyUsage[i]:F4} hours -> {labels[i]}");
                }

                // Determine a good Y-axis maximum based on the actual data
                double yAxisMax = maxValue;
                if (yAxisMax < 0.005) yAxisMax = 0.01;  // Very small values
                else if (yAxisMax < 0.05) yAxisMax = 0.1;  // Small values
                else if (yAxisMax < 0.5) yAxisMax = 1;  // Medium values
                else yAxisMax = Math.Ceiling(yAxisMax * 1.2);  // Large values
                
                System.Diagnostics.Debug.WriteLine($"Setting Y-axis max to {yAxisMax:F4}");

                // Create the line series with system accent color
                var lineSeries = new LineSeries<double>
                {
                    Values = values,
                    Fill = null,
                    GeometrySize = 4, // Add small data points for better visibility
                    Stroke = new SolidColorPaint(seriesColor, 2.5f), // Use system accent color with slightly thicker line
                    GeometryStroke = new SolidColorPaint(seriesColor, 2), // Match stroke color
                    GeometryFill = new SolidColorPaint(SKColors.White), // White fill for points
                    Name = "Usage"
                };

                // Set up the axes with improved contrast
                UsageChartLive.XAxes = new Axis[]
                {
                    new Axis
                    {
                        Labels = labels,
                        LabelsRotation = useShortLabels ? 0 : 45, // Rotate labels when space is limited
                        ForceStepToMin = true,
                        MinStep = 1,
                        TextSize = 11, // Slightly larger text
                        LabelsPaint = new SolidColorPaint(axisColor), // More contrast
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100)) // Subtle grid lines
                    }
                };

                UsageChartLive.YAxes = new Axis[]
                {
                    new Axis
                    {
                        Name = "Hours",
                        NamePaint = new SolidColorPaint(axisColor),
                        NameTextSize = 12,
                        LabelsPaint = new SolidColorPaint(axisColor),
                        TextSize = 11, // Slightly larger text
                        MinLimit = 0,
                        MaxLimit = yAxisMax,
                        ForceStepToMin = true,
                        MinStep = yAxisMax > 4 ? 2 : (yAxisMax < 0.1 ? 0.05 : 0.5),  // Use 2h steps for larger values
                        Labeler = FormatHoursForYAxis,
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100)) // Subtle grid lines
                    }
                };

                // Update the chart with new series
                UsageChartLive.Series = new ISeries[] { lineSeries };
                
                System.Diagnostics.Debug.WriteLine("Hourly chart updated with values");
            }
            else // Daily view
            {
                System.Diagnostics.Debug.WriteLine("Building DAILY chart");
                
                int daysToShow = 7;
                if (_currentTimePeriod == TimePeriod.Daily)
                    daysToShow = 1;
                else if (_currentTimePeriod == TimePeriod.Weekly)
                    daysToShow = 7;
                
                System.Diagnostics.Debug.WriteLine($"Days to show for daily chart: {daysToShow}");

                var values = new List<double>();
                var labels = new List<string>();
                
                DateTime currentDate = DateTime.Now.Date;
                DateTime startDate = _selectedDate.Date.AddDays(-(daysToShow - 1));

                for (int i = 0; i < daysToShow; i++)
                {
                    DateTime date = startDate.AddDays(i);
                    double totalHours = 0;
                    
                    // For today's date, sum up records from _usageRecords
                    if (date.Date == DateTime.Now.Date)
                    {
                        foreach (var record in _usageRecords)
                        {
                            totalHours += record.Duration.TotalHours;
                        }
                    }
                    else
                    {
                        // For other dates, get data from database
                        if (_databaseService != null)
                        {
                            var records = _databaseService.GetAggregatedRecordsForDate(date);
                            foreach (var record in records)
                            {
                                totalHours += record.Duration.TotalHours;
                            }
                        }
                    }
                    
                    values.Add(totalHours);
                    
                    // Adjust label format based on window size and number of days
                    bool useShortLabels = UsageChartLive.ActualWidth < 500 || daysToShow > 10;
                    
                    // For monthly view with many days, we skip some labels to avoid overcrowding
                    if (daysToShow > 15 && i % 3 != 0 && i != daysToShow - 1)
                    {
                        labels.Add(""); // Skip some labels for monthly view
                    }
                    else
                    {
                        labels.Add(date.ToString(useShortLabels ? "d" : "MM/dd"));
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"Date {date:MM/dd}: {totalHours:F4} hours");
                }
                
                // Check if all values are zero
                bool allZero = true;
                double maxValue = 0;
                foreach (var value in values)
                {
                    if (value > 0.0001)
                    {
                        allZero = false;
                    }
                    maxValue = Math.Max(maxValue, value);
                }
                
                System.Diagnostics.Debug.WriteLine($"All values zero? {allZero}, Max value: {maxValue:F4}");
                
                // If all values are zero, add a tiny value to the current day to make the chart visible
                if (allZero || maxValue < 0.001)
                {
                    int lastIndex = values.Count - 1;
                    values[lastIndex] = 0.001; // Add a tiny value
                    System.Diagnostics.Debug.WriteLine("Added tiny value to current day");
                }
                
                // Determine a good Y-axis maximum based on the actual data
                double yAxisMax = maxValue;
                if (yAxisMax < 0.005) yAxisMax = 0.01;  // Very small values
                else if (yAxisMax < 0.05) yAxisMax = 0.1;  // Small values
                else if (yAxisMax < 0.5) yAxisMax = 1;  // Medium values
                else yAxisMax = Math.Ceiling(yAxisMax * 1.2);  // Large values
                
                System.Diagnostics.Debug.WriteLine($"Setting Y-axis max to {yAxisMax:F4}");

                // Create semi-transparent color for fill
                var fillColor = seriesColor.WithAlpha(180); // 70% opacity

                // Create the column series with system accent color
                var columnSeries = new ColumnSeries<double>
                {
                    Values = values,
                    Fill = new SolidColorPaint(fillColor),
                    Stroke = new SolidColorPaint(seriesColor, 1),
                    Name = "Usage",
                    IgnoresBarPosition = false,
                    Padding = 2 // Add small padding between bars
                };

                // Determine rotation angle based on number of days and window width
                int rotationAngle = 0;
                if (daysToShow > 10 || UsageChartLive.ActualWidth < 600)
                {
                    rotationAngle = 45; // Rotate labels for better spacing
                }

                // Set up the axes with improved contrast
                UsageChartLive.XAxes = new Axis[]
                {
                    new Axis
                    {
                        Labels = labels,
                        LabelsRotation = rotationAngle,
                        ForceStepToMin = true,
                        MinStep = 1,
                        TextSize = 11, // Slightly larger text
                        LabelsPaint = new SolidColorPaint(axisColor), // More contrast
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100)) // Subtle grid lines
                    }
                };

                UsageChartLive.YAxes = new Axis[]
                {
                    new Axis
                    {
                        Name = "Hours",
                        NamePaint = new SolidColorPaint(axisColor),
                        NameTextSize = 12,
                        LabelsPaint = new SolidColorPaint(axisColor),
                        TextSize = 11, // Slightly larger text
                        MinLimit = 0,
                        MaxLimit = yAxisMax,
                        ForceStepToMin = true,
                        MinStep = yAxisMax > 4 ? 2 : (yAxisMax < 0.1 ? 0.05 : 0.5),  // Use 2h steps for larger values
                        Labeler = FormatHoursForYAxis,
                        SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(100)) // Subtle grid lines
                    }
                };

                // Update the chart with new series
                UsageChartLive.Series = new ISeries[] { columnSeries };
                
                System.Diagnostics.Debug.WriteLine("Daily chart updated with values");
            }
            
            // Set additional chart properties for better appearance
            UsageChartLive.LegendPosition = LiveChartsCore.Measure.LegendPosition.Hidden;
            UsageChartLive.AnimationsSpeed = TimeSpan.FromMilliseconds(300);
            
            System.Diagnostics.Debug.WriteLine($"Chart updated with {UsageChartLive.Series?.Count() ?? 0} series");
            System.Diagnostics.Debug.WriteLine("=== UpdateUsageChart COMPLETED ===");
        }

        // Helper methods for chart formatting
        private string FormatTimeSpanForChart(TimeSpan time)
        {
            if (time.TotalDays >= 1)
            {
                return $"{(int)time.TotalDays}d {time.Hours}h {time.Minutes}m";
            }
            else if (time.TotalHours >= 1)
            {
                return $"{(int)time.TotalHours}h {time.Minutes}m";
            }
            else
            {
                return $"{(int)time.TotalMinutes}m";
            }
        }

        private string FormatHoursForYAxis(double value)
        {
            var time = TimeSpan.FromHours(value);
            
            if (time.TotalMinutes < 1)
            {
                // Show seconds for very small values
                return $"{time.TotalSeconds:F0}s";
            }
            else if (time.TotalHours < 1)
            {
                // Show only minutes for less than an hour
                return $"{time.TotalMinutes:F0}m";
            }
            else
            {
                // Just show hour value without minutes for cleaner display
                return $"{Math.Floor(time.TotalHours)}h";
            }
        }

        private void TimePeriodSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TimePeriodSelector.SelectedIndex >= 0)
            {
                System.Diagnostics.Debug.WriteLine($"Time period changed from {_currentTimePeriod} to {(TimePeriod)TimePeriodSelector.SelectedIndex}");
                
                _currentTimePeriod = (TimePeriod)TimePeriodSelector.SelectedIndex;
                
                // Keep the same selected date when switching views
                DateTime dateToLoad = _selectedDate;
                
                // Adjust chart view mode automatically based on time period
                if (_currentTimePeriod == TimePeriod.Daily)
                {
                    // For Daily view, only show hourly chart
                    _currentChartViewMode = ChartViewMode.Hourly;
                }
                else // Weekly
                {
                    // For Weekly view, only show daily chart
                    _currentChartViewMode = ChartViewMode.Daily;
                }
                
                // Update UI safely using DispatcherQueue
                DispatcherQueue.TryEnqueue(() => {
                    // Update the view mode label
                    if (ViewModeLabel != null)
                    {
                        ViewModeLabel.Text = _currentChartViewMode == ChartViewMode.Hourly ? "Hourly View" : "Daily View";
                    }
                });
                
                // Always reload data when the time period changes
                LoadRecordsForDate(dateToLoad);
            }
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
            if (time.TotalDays >= 1)
            {
                return $"{(int)time.TotalDays}d {time.Hours}h {time.Minutes}m";
            }
            else if (time.TotalHours >= 1)
            {
                return $"{(int)time.TotalHours}h {time.Minutes}m";
            }
            else if (time.TotalMinutes >= 1)
            {
                return $"{(int)time.TotalMinutes}m {time.Seconds}s";
            }
            else
            {
                return $"{time.Seconds}s";
            }
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
                
                // Set the initial chart view mode based on the current time period
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

        // Add the SetUpWindow method
        private void SetUpWindow()
        {
            try
            {
                IntPtr windowHandle = WindowNative.GetWindowHandle(this);
                Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
                _appWindow = AppWindow.GetFromWindowId(windowId);
                
                if (_appWindow != null)
                {
                    _appWindow.Title = "Screeny";
                    _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                    _appWindow.TitleBar.ButtonBackgroundColor = MicrosoftUI.Colors.Transparent;
                    _appWindow.TitleBar.ButtonInactiveBackgroundColor = MicrosoftUI.Colors.Transparent;

                    // Set the window icon
                    var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
                    if (File.Exists(iconPath))
                    {
                        try
                        {
                            // For ICON_SMALL (16x16), explicitly request a small size to improve scaling quality
                            IntPtr smallIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                            SendMessage(windowHandle, WM_SETICON, ICON_SMALL, smallIcon);
                            
                            // For ICON_BIG, let Windows decide the best size based on DPI settings
                            IntPtr bigIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                            SendMessage(windowHandle, WM_SETICON, ICON_BIG, bigIcon);
                        }
                        catch (Exception ex)
                        {
                            // Non-critical failure - continue without icon
                            System.Diagnostics.Debug.WriteLine($"Failed to set window icon: {ex.Message}");
                        }
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
                    try
                    {
                        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                        var scale = GetScaleAdjustment();
                        _appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = (int)(1000 * scale), Height = (int)(600 * scale) });
                    }
                    catch (Exception ex)
                    {
                        // Non-critical failure - window will use default size
                        System.Diagnostics.Debug.WriteLine($"Failed to set window size: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting up window: {ex.Message}");
                // Continue with default window settings
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
            System.Diagnostics.Debug.WriteLine("Force refreshing chart...");
            
            // Log the current state of data
            System.Diagnostics.Debug.WriteLine($"Current records in collection: {_usageRecords.Count}");
            if (_usageRecords.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine("First 3 records:");
                foreach (var record in _usageRecords.Take(3))
                {
                    System.Diagnostics.Debug.WriteLine($"  - {record.ProcessName}: {record.Duration.TotalMinutes:F1}m, Start={record.StartTime}");
                }
            }
            
            // Manually clear and rebuild the chart
            if (UsageChartLive != null)
            {
                // First clear the chart
                UsageChartLive.Series = new ISeries[] { };
                
                // Then update it
                UpdateUsageChart();
                
                System.Diagnostics.Debug.WriteLine("Chart refresh completed");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("ERROR: UsageChartLive is null, cannot refresh chart");
            }
        }

        private void DatePickerButton_Click(object sender, RoutedEventArgs e)
        {
            // Create the popup if it doesn't exist
            if (_datePickerPopup == null)
            {
                CreateDatePickerPopup();
            }
            else
            {
                // Ensure XamlRoot is set (in case it was lost)
                _datePickerPopup.XamlRoot = Content.XamlRoot;
            }

            if (_datePickerPopup != null && _customCalendarView != null)
            {
                // Update calendar view selection
                _customCalendarView.SelectedDates.Clear();
                _customCalendarView.SelectedDates.Add(_selectedDate);
                
                // Reset all button styles
                ResetQuickSelectButtonStyles();
                
                // Highlight the appropriate quick selection button based on current selection
                if (_selectedEndDate.HasValue)
                {
                    // Date range is selected
                    var today = DateTime.Today;
                    var yesterday = today.AddDays(-1);
                    var sevenDaysAgo = today.AddDays(-6);
                    var thirtyDaysAgo = today.AddDays(-29);
                    var firstDayOfMonth = new DateTime(today.Year, today.Month, 1);
                    
                    // Find the matching button and highlight it
                    if (_selectedDate == today && _selectedEndDate == today)
                    {
                        HighlightQuickSelectButton("Today");
                    }
                    else if (_selectedDate == yesterday && _selectedEndDate == yesterday)
                    {
                        HighlightQuickSelectButton("Yesterday");
                    }
                    else if (_selectedDate == sevenDaysAgo && _selectedEndDate == today)
                    {
                        HighlightQuickSelectButton("Last 7 days");
                    }
                    else if (_selectedDate == thirtyDaysAgo && _selectedEndDate == today)
                    {
                        HighlightQuickSelectButton("Last 30 days");
                    }
                    else if (_selectedDate == firstDayOfMonth && _selectedEndDate == today)
                    {
                        HighlightQuickSelectButton("This month");
                    }
                }
                else
                {
                    // Single date is selected
                    if (_selectedDate == DateTime.Today)
                    {
                        HighlightQuickSelectButton("Today");
                    }
                    else if (_selectedDate == DateTime.Today.AddDays(-1))
                    {
                        HighlightQuickSelectButton("Yesterday");
                    }
                }
                
                // Position the popup near the button
                var button = sender as Button;
                if (button != null)
                {
                    var transform = button.TransformToVisual(null);
                    var point = transform.TransformPoint(new Windows.Foundation.Point(0, button.ActualHeight));
                    
                    // Set the position of the popup
                    _datePickerPopup.HorizontalOffset = point.X;
                    _datePickerPopup.VerticalOffset = point.Y;
                }
                
                // Show the popup
                _datePickerPopup.IsOpen = true;
            }
        }
        
        private void CreateDatePickerPopup()
        {
            // Create a new popup
            _datePickerPopup = new Popup();
            
            // Set the XamlRoot property to connect the popup to the UI tree
            _datePickerPopup.XamlRoot = Content.XamlRoot;
            
            // Create the root grid for the popup content
            var rootGrid = new Grid
            {
                Background = Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] as Brush,
                BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20),
                Width = 450
            };
            
            // Set up row definitions
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) }); // Spacing
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Quick selection buttons
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) }); // Spacing
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Calendar
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); // Spacing
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Action buttons
            
            // Create header text
            var headerText = new TextBlock
            {
                Text = "Select date range",
                Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetRow(headerText, 0);
            rootGrid.Children.Add(headerText);
            
            // Create quick selection buttons in a more compact layout
            var buttonsStackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 8
            };
            Grid.SetRow(buttonsStackPanel, 2);
            
            // Create two rows of buttons
            var topRowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8)
            };
            
            var bottomRowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            
            // Create button style with smaller padding
            var compactButtonStyle = new Style(typeof(Button));
            compactButtonStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(8, 4, 8, 4)));
            compactButtonStyle.Setters.Add(new Setter(Button.MinHeightProperty, 32.0));
            
            // Create accent button style with smaller padding
            var compactAccentButtonStyle = new Style(typeof(Button), Application.Current.Resources["AccentButtonStyle"] as Style);
            compactAccentButtonStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(8, 4, 8, 4)));
            compactAccentButtonStyle.Setters.Add(new Setter(Button.MinHeightProperty, 32.0));
            
            // Create Today and Yesterday buttons in the top row
            _todayButton = new Button
            {
                Content = "Today",
                Style = compactAccentButtonStyle,
                MinWidth = 100
            };
            _todayButton.Click += QuickSelect_Today_Click;
            topRowPanel.Children.Add(_todayButton);
            
            _yesterdayButton = new Button
            {
                Content = "Yesterday",
                Style = compactButtonStyle,
                MinWidth = 100
            };
            _yesterdayButton.Click += QuickSelect_Yesterday_Click;
            topRowPanel.Children.Add(_yesterdayButton);
            
            // Create Last 7 days, Last 30 days and This month buttons in the bottom row
            _last7DaysButton = new Button
            {
                Content = "Last 7 days",
                Style = compactButtonStyle,
                MinWidth = 100
            };
            _last7DaysButton.Click += QuickSelect_Last7Days_Click;
            bottomRowPanel.Children.Add(_last7DaysButton);
            
            _last30DaysButton = new Button
            {
                Content = "Last 30 days",
                Style = compactButtonStyle,
                MinWidth = 100
            };
            _last30DaysButton.Click += QuickSelect_Last30Days_Click;
            bottomRowPanel.Children.Add(_last30DaysButton);
            
            _thisMonthButton = new Button
            {
                Content = "This month",
                Style = compactButtonStyle,
                MinWidth = 100
            };
            _thisMonthButton.Click += QuickSelect_ThisMonth_Click;
            bottomRowPanel.Children.Add(_thisMonthButton);
            
            // Add rows to the stack panel
            buttonsStackPanel.Children.Add(topRowPanel);
            buttonsStackPanel.Children.Add(bottomRowPanel);
            
            rootGrid.Children.Add(buttonsStackPanel);
            
            // Create calendar view with better range selection
            _customCalendarView = new CalendarView
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectionMode = CalendarViewSelectionMode.Multiple,
                MinHeight = 320
            };
            
            // Add handler for day item changing to visualize the range
            _customCalendarView.DayItemChanging += CustomCalendarView_DayItemChanging;
            _customCalendarView.SelectedDatesChanged += CustomCalendarView_SelectedDatesChanged;
            
            // Set selection hints
            var selectionHint = new TextBlock
            {
                Text = "Tip: Click multiple dates to select a range",
                Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 0)
            };
            
            var calendarContainer = new StackPanel();
            calendarContainer.Children.Add(_customCalendarView);
            calendarContainer.Children.Add(selectionHint);
            
            Grid.SetRow(calendarContainer, 4);
            rootGrid.Children.Add(calendarContainer);
            
            // Create action buttons grid
            var actionsGrid = new Grid();
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(actionsGrid, 6);
            
            // Create Cancel button
            var cancelButton = new Button
            {
                Content = "Cancel",
                HorizontalAlignment = HorizontalAlignment.Left
            };
            cancelButton.Click += DatePickerCancel_Click;
            Grid.SetColumn(cancelButton, 0);
            actionsGrid.Children.Add(cancelButton);
            
            // Create Done button
            var doneButton = new Button
            {
                Content = "Done",
                Style = Application.Current.Resources["AccentButtonStyle"] as Style
            };
            doneButton.Click += DatePickerDone_Click;
            Grid.SetColumn(doneButton, 1);
            actionsGrid.Children.Add(doneButton);
            
            rootGrid.Children.Add(actionsGrid);
            
            // Set the popup content
            _datePickerPopup.Child = rootGrid;
        }
        
        private void HighlightQuickSelectButton(string buttonContent)
        {
            if (_todayButton != null && buttonContent == "Today")
                _todayButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
            else if (_yesterdayButton != null && buttonContent == "Yesterday")
                _yesterdayButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
            else if (_last7DaysButton != null && buttonContent == "Last 7 days")
                _last7DaysButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
            else if (_last30DaysButton != null && buttonContent == "Last 30 days")
                _last30DaysButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
            else if (_thisMonthButton != null && buttonContent == "This month")
                _thisMonthButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
        }
        
        private void ResetQuickSelectButtonStyles()
        {
            var defaultStyle = Application.Current.Resources["DefaultButtonStyle"] as Style;
            
            if (_todayButton != null)
                _todayButton.Style = defaultStyle;
            if (_yesterdayButton != null)
                _yesterdayButton.Style = defaultStyle;
            if (_last7DaysButton != null)
                _last7DaysButton.Style = defaultStyle;
            if (_last30DaysButton != null)
                _last30DaysButton.Style = defaultStyle;
            if (_thisMonthButton != null)
                _thisMonthButton.Style = defaultStyle;
        }
        
        private void QuickSelect_Today_Click(object sender, RoutedEventArgs e)
        {
            _selectedDate = DateTime.Today;
            _selectedEndDate = null;
            _isDateRangeSelected = false;
            
            if (_customCalendarView != null)
            {
                _customCalendarView.SelectedDates.Clear();
                _customCalendarView.SelectedDates.Add(_selectedDate);
            }
            
            LoadRecordsForDate(_selectedDate);
            UpdateDatePickerButtonText();
            HighlightQuickSelectButton("Today");
            
            // Set time period to Daily
            if (TimePeriodSelector.SelectedIndex != (int)TimePeriod.Daily)
            {
                TimePeriodSelector.SelectedIndex = (int)TimePeriod.Daily;
            }
            else
            {
                // Force refresh since the selection changed event won't fire
                UpdateChartViewMode();
                LoadRecordsForDate(_selectedDate);
                UpdateUsageChart();
            }
            
            if (_datePickerPopup != null)
            {
                _datePickerPopup.IsOpen = false;
            }
        }
        
        private void QuickSelect_Yesterday_Click(object sender, RoutedEventArgs e)
        {
            _selectedDate = DateTime.Today.AddDays(-1);
            _selectedEndDate = null;
            _isDateRangeSelected = false;
            
            if (_customCalendarView != null)
            {
                _customCalendarView.SelectedDates.Clear();
                _customCalendarView.SelectedDates.Add(_selectedDate);
            }
            
            LoadRecordsForDate(_selectedDate);
            UpdateDatePickerButtonText();
            HighlightQuickSelectButton("Yesterday");
            
            // Set time period to Daily
            if (TimePeriodSelector.SelectedIndex != (int)TimePeriod.Daily)
            {
                TimePeriodSelector.SelectedIndex = (int)TimePeriod.Daily;
            }
            else
            {
                // Force refresh since the selection changed event won't fire
                UpdateChartViewMode();
                LoadRecordsForDate(_selectedDate);
                UpdateUsageChart();
            }
            
            if (_datePickerPopup != null)
            {
                _datePickerPopup.IsOpen = false;
            }
        }
        
        private void QuickSelect_Last7Days_Click(object sender, RoutedEventArgs e)
        {
            _selectedEndDate = DateTime.Today;
            _selectedDate = DateTime.Today.AddDays(-6);
            _isDateRangeSelected = true;
            
            if (_customCalendarView != null)
            {
                _customCalendarView.SelectedDates.Clear();
                for (var date = _selectedDate; date <= _selectedEndDate; date = date.AddDays(1))
                {
                    _customCalendarView.SelectedDates.Add(date);
                }
            }
            
            LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
            UpdateDatePickerButtonText();
            HighlightQuickSelectButton("Last 7 Days");
            
            // Set time period to Weekly
            if (TimePeriodSelector.SelectedIndex != (int)TimePeriod.Weekly)
            {
                TimePeriodSelector.SelectedIndex = (int)TimePeriod.Weekly;
            }
            else
            {
                // Force refresh since the selection changed event won't fire
                UpdateChartViewMode();
                LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
                UpdateUsageChart();
            }
            
            if (_datePickerPopup != null)
            {
                _datePickerPopup.IsOpen = false;
            }
        }
        
        private void QuickSelect_Last30Days_Click(object sender, RoutedEventArgs e)
        {
            _selectedEndDate = DateTime.Today;
            _selectedDate = DateTime.Today.AddDays(-29);
            _isDateRangeSelected = true;
            
            if (_customCalendarView != null)
            {
                _customCalendarView.SelectedDates.Clear();
                for (var date = _selectedDate; date <= _selectedEndDate; date = date.AddDays(1))
                {
                    _customCalendarView.SelectedDates.Add(date);
                }
            }
            
            LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
            UpdateDatePickerButtonText();
            HighlightQuickSelectButton("Last 30 Days");
            
            // Set time period to Weekly
            if (TimePeriodSelector.SelectedIndex != (int)TimePeriod.Weekly)
            {
                TimePeriodSelector.SelectedIndex = (int)TimePeriod.Weekly;
            }
            else
            {
                // Force refresh since the selection changed event won't fire
                UpdateChartViewMode();
                LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
                UpdateUsageChart();
            }
            
            if (_datePickerPopup != null)
            {
                _datePickerPopup.IsOpen = false;
            }
        }
        
        private void QuickSelect_ThisMonth_Click(object sender, RoutedEventArgs e)
        {
            var today = DateTime.Today;
            _selectedDate = new DateTime(today.Year, today.Month, 1);
            _selectedEndDate = today;
            _isDateRangeSelected = true;
            
            if (_customCalendarView != null)
            {
                _customCalendarView.SelectedDates.Clear();
                for (var date = _selectedDate; date <= _selectedEndDate; date = date.AddDays(1))
                {
                    _customCalendarView.SelectedDates.Add(date);
                }
            }
            
            LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
            UpdateDatePickerButtonText();
            HighlightQuickSelectButton("This Month");
            
            // Set time period to Weekly
            if (TimePeriodSelector.SelectedIndex != (int)TimePeriod.Weekly)
            {
                TimePeriodSelector.SelectedIndex = (int)TimePeriod.Weekly;
            }
            else
            {
                // Force refresh since the selection changed event won't fire
                UpdateChartViewMode();
                LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
                UpdateUsageChart();
            }
            
            if (_datePickerPopup != null)
            {
                _datePickerPopup.IsOpen = false;
            }
        }
        
        private void CustomCalendarView_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
        {
            // Only process phase 1 (first phase)
            if (args.Phase == 0)
            {
                // Register for future phases
                args.RegisterUpdateCallback(CustomCalendarView_DayItemChanging);
            }
            else if (args.Phase == 1 && _customCalendarView != null)
            {
                // Check if this date is between the first and last selected dates (but not one of them)
                if (_customCalendarView.SelectedDates.Count >= 2)
                {
                    var sortedDates = _customCalendarView.SelectedDates.OrderBy(d => d.Date).ToList();
                    var firstDate = sortedDates.First().Date;
                    var lastDate = sortedDates.Last().Date;
                    
                    // Check if this date is in the range between first and last
                    if (args.Item.Date.Date > firstDate && args.Item.Date.Date < lastDate)
                    {
                        // Mark dates in the range with a lighter selection visual
                        args.Item.IsInRange = true;
                    }
                }
            }
        }
        
        private void CustomCalendarView_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (_customCalendarView == null) return;
            
            // Handle date selection for ranges
            if (_customCalendarView.SelectedDates.Count >= 2)
            {
                // Get all selected dates sorted
                var sortedDates = _customCalendarView.SelectedDates.OrderBy(d => d.Date).ToList();
                
                // Use the first and last dates as range boundaries
                _selectedDate = sortedDates.First().Date;
                _selectedEndDate = sortedDates.Last().Date;
                _isDateRangeSelected = true;
                
                // If we have exactly two dates, ensure all dates in between are marked as in range
                if (sortedDates.Count == 2)
                {
                    // The calendar will automatically connect these as a range
                    System.Diagnostics.Debug.WriteLine($"Date range selected: {_selectedDate:d} to {_selectedEndDate:d}");
                }
                
                // Reset quick selection button styles
                ResetQuickSelectButtonStyles();
                
                // Check if the selected range matches any of our predefined ranges
                var today = DateTime.Today;
                var yesterday = today.AddDays(-1);
                var sevenDaysAgo = today.AddDays(-6);
                var thirtyDaysAgo = today.AddDays(-29);
                var firstDayOfMonth = new DateTime(today.Year, today.Month, 1);
                
                if (_selectedDate == today && _selectedEndDate == today)
                {
                    HighlightQuickSelectButton("Today");
                }
                else if (_selectedDate == yesterday && _selectedEndDate == yesterday)
                {
                    HighlightQuickSelectButton("Yesterday");
                }
                else if (_selectedDate == sevenDaysAgo && _selectedEndDate == today)
                {
                    HighlightQuickSelectButton("Last 7 Days");
                }
                else if (_selectedDate == thirtyDaysAgo && _selectedEndDate == today)
                {
                    HighlightQuickSelectButton("Last 30 Days");
                }
                else if (_selectedDate == firstDayOfMonth && _selectedEndDate == today)
                {
                    HighlightQuickSelectButton("This Month");
                }
            }
            else if (_customCalendarView.SelectedDates.Count == 1)
            {
                // Single date selection
                _selectedDate = _customCalendarView.SelectedDates[0].Date;
                _selectedEndDate = null;
                _isDateRangeSelected = false;
                
                // Reset quick selection button styles
                ResetQuickSelectButtonStyles();
                
                // Check if the selected date matches any of our predefined dates
                var today = DateTime.Today;
                var yesterday = today.AddDays(-1);
                
                if (_selectedDate == today)
                {
                    HighlightQuickSelectButton("Today");
                }
                else if (_selectedDate == yesterday)
                {
                    HighlightQuickSelectButton("Yesterday");
                }
            }
        }
        
        private void DatePickerCancel_Click(object sender, RoutedEventArgs e)
        {
            // Close the popup without applying changes
            if (_datePickerPopup != null)
            {
                _datePickerPopup.IsOpen = false;
            }
        }
        
        private void DatePickerDone_Click(object sender, RoutedEventArgs e)
        {
            // Apply the selected date/range and close the popup
            if (_datePickerPopup != null)
            {
                _datePickerPopup.IsOpen = false;
            }
            
            // Update the button text to reflect the selected date or range
            UpdateDatePickerButtonText();
            
            // Load records for the selected date/range
            if (_isDateRangeSelected && _selectedEndDate.HasValue)
            {
                // For date ranges, we'll use Weekly time period
                TimePeriodSelector.SelectedIndex = (int)TimePeriod.Weekly;
                LoadRecordsForDateRange(_selectedDate, _selectedEndDate.Value);
            }
            else
            {
                // For single dates, use Daily time period
                TimePeriodSelector.SelectedIndex = (int)TimePeriod.Daily;
                LoadRecordsForDate(_selectedDate);
            }
        }
        
        private void UpdateDatePickerButtonText()
        {
            if (_isDateRangeSelected && _selectedEndDate.HasValue)
            {
                // Format for date range
                DatePickerButton.Content = $"{_selectedDate:MM/dd} - {_selectedEndDate:MM/dd}";
            }
            else
            {
                // Format for single date
                var today = DateTime.Today;
                var yesterday = today.AddDays(-1);
                
                if (_selectedDate == today)
                {
                    DatePickerButton.Content = "Today";
                }
                else if (_selectedDate == yesterday)
                {
                    DatePickerButton.Content = "Yesterday";
                }
                else
                {
                    // Format single date without year for cleaner display
                    DatePickerButton.Content = _selectedDate.ToString("MMM d");
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
    }
}


using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
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
        private DispatcherTimer _updateTimer;
        private DispatcherTimer _autoSaveTimer;
        private bool _disposed;
        private TimePeriod _currentTimePeriod = TimePeriod.Daily;
        private ChartViewMode _currentChartViewMode = ChartViewMode.Hourly;
        
        // Static constructor to configure LiveCharts
        static MainWindow()
        {
            // Configure LiveCharts defaults
            LiveChartsSettings.ConfigureTheme();
        }
        
        public enum TimePeriod
        {
            Daily,
            Weekly,
            Monthly
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

        private void DatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            if (args.NewDate.HasValue)
            {
                _selectedDate = args.NewDate.Value.Date;
                
                // Update the date display text
                if (DateDisplay != null)
                {
                    DateDisplay.Text = _selectedDate.ToString("MMM dd, yyyy");
                }
                
                LoadRecordsForDate(_selectedDate);

                // Clean up any system processes that might have been added
                CleanupSystemProcesses();
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
            try
            {
                // Store the selected date
                _selectedDate = date;
                
                // Update the date display
                if (DateDisplay != null)
                {
                    DateDisplay.Text = date.ToString("MMM dd, yyyy");
                }
                
                // Clear existing records
                _usageRecords.Clear();

                // Load records from database if available
                if (_databaseService != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Loading records for date: {date.ToShortDateString()} with period: {_currentTimePeriod}");
                    
                    // For weekly and monthly views, we need to get records for a range of dates
                    List<AppUsageRecord> records;
                    
                    switch (_currentTimePeriod)
                    {
                        case TimePeriod.Weekly:
                            // Get records for the week containing the selected date
                            var startOfWeek = date.AddDays(-(int)date.DayOfWeek);
                            var endOfWeek = startOfWeek.AddDays(6);
                            records = GetAggregatedRecordsForDateRange(startOfWeek, endOfWeek);
                            
                            // Update chart title - not needed with new design
                            SummaryTitle.Text = "Screen Time Summary";
                            
                            // Show daily average
                            AveragePanel.Visibility = Visibility.Visible;
                            break;
                            
                        case TimePeriod.Monthly:
                            // Get records for the month containing the selected date
                            var startOfMonth = new DateTime(date.Year, date.Month, 1);
                            var endOfMonth = startOfMonth.AddMonths(1).AddDays(-1);
                            records = GetAggregatedRecordsForDateRange(startOfMonth, endOfMonth);
                            
                            // Update chart title - not needed with new design
                            SummaryTitle.Text = "Screen Time Summary";
                            
                            // Show daily average
                            AveragePanel.Visibility = Visibility.Visible;
                            break;
                            
                        case TimePeriod.Daily:
                        default:
                            // Default to daily view - include aggregate data and active tracking data
                            records = new List<AppUsageRecord>();
                            
                            // First, get saved records for today's date
                            var savedRecords = _databaseService.GetAggregatedRecordsForDate(date);
                            records.AddRange(savedRecords);
                            
                            // If viewing today's data and tracking is active, include live records
                            if (date.Date == DateTime.Today && _trackingService.IsTracking)
                            {
                                // Get live tracking records that aren't in the database yet
                                var liveRecords = _trackingService.GetRecords()
                                    .Where(r => r.IsFromDate(date) && !IsWindowsSystemProcess(r.ProcessName))
                                    .ToList();
                                
                                System.Diagnostics.Debug.WriteLine($"Adding {liveRecords.Count} live tracking records");
                                
                                // Merge with existing records where possible
                                foreach (var liveRecord in liveRecords)
                                {
                                    var existingRecord = records
                                        .FirstOrDefault(r => r.ProcessName.Equals(liveRecord.ProcessName, StringComparison.OrdinalIgnoreCase));
                                    
                                    if (existingRecord != null)
                                    {
                                        // Merge duration into existing record
                                        existingRecord.MergeWith(liveRecord);
                                    }
                                    else
                                    {
                                        // Add as new record if it has meaningful duration
                                        if (liveRecord.Duration.TotalSeconds > 1)
                                        {
                                            records.Add(liveRecord);
                                        }
                                    }
                                }
                            }
                            
                            // Update chart title - not needed with new design
                            SummaryTitle.Text = "Screen Time Summary";
                            
                            // Hide daily average for daily view
                            AveragePanel.Visibility = Visibility.Collapsed;
                            break;
                    }

                    // Add records to the observable collection - filter out system processes
                    foreach (var record in records.Where(r => !IsWindowsSystemProcess(r.ProcessName)))
                    {
                        _usageRecords.Add(record);
                        // Load app icons for each record
                        record.LoadAppIconIfNeeded();
                    }

                    // Sort records by duration for better display
                    var sortedRecords = _usageRecords.OrderByDescending(r => r.Duration).ToList();
                    _usageRecords.Clear();
                    foreach (var record in sortedRecords)
                    {
                        _usageRecords.Add(record);
                    }

                    System.Diagnostics.Debug.WriteLine($"Loaded {records.Count} records, displayed {_usageRecords.Count} after filtering");
                    
                    // Update the chart
                    UpdateUsageChart();
                    
                    // Update the summary
                    UpdateSummaryTab();
                    
                    // Force a chart refresh to ensure it always renders
                    ForceChartRefresh();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading records: {ex.Message}");

                // Show error dialog
                var dialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Failed to load records: {ex.Message}",
                    CloseButtonText = "OK"
                };

                dialog.XamlRoot = this.Content.XamlRoot;
                _ = dialog.ShowAsync();
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
                        MinStep = yAxisMax < 0.1 ? 0.005 : 0.5,
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
                else if (_currentTimePeriod == TimePeriod.Monthly)
                    daysToShow = 30;
                
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
                        MinStep = yAxisMax < 0.1 ? 0.005 : 0.5,
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
            else if (time.TotalHours < 10)
            {
                // Show hours and minutes for moderate times
                return $"{Math.Floor(time.TotalHours)}h {time.Minutes}m";
            }
            else
            {
                // Show only hours for large values
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
                // If switching to/from weekly or monthly, ensure we stay on the same date
                DateTime dateToLoad = _selectedDate;
                
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
                
                // Initialize UI
                SetUpUiElements();

                // Set the initial date display text
                if (DateDisplay != null)
                {
                    DateDisplay.Text = _selectedDate.ToString("MMM dd, yyyy");
                }

                // Load initial records
                LoadRecordsForDate(_selectedDate);

                // TabView is removed - no need to set active tab
                System.Diagnostics.Debug.WriteLine("TabView removed - using single-page layout");

                // Ensure UsageListView is bound to _usageRecords
                if (UsageListView != null && UsageListView.ItemsSource == null)
                {
                    System.Diagnostics.Debug.WriteLine("Setting UsageListView.ItemsSource to _usageRecords");
                    UsageListView.ItemsSource = _usageRecords;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"UsageListView status: {(UsageListView == null ? "null" : "not null")}, ItemsSource: {(UsageListView?.ItemsSource == null ? "null" : "not null")}");
                }

                // Check if this is first run
                CheckFirstRun();

                // Make sure we start with a clean state (no system processes)
                CleanupSystemProcesses();
                
                // Start tracking automatically
                StartTracking();
                
                // Set initial view mode
                UpdateChartViewMode();
                
                // Set initial button styles
                HourlyButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
                DailyButton.Style = Application.Current.Resources["DefaultButtonStyle"] as Style;
                
                System.Diagnostics.Debug.WriteLine("MainWindow_Loaded completed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during window load: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                // Continue with basic functionality even if UI initialization fails
            }
        }

        // Move UI initialization to a separate method
        private void SetUpUiElements()
        {
            // Initialize the date picker
            DatePicker.Date = _selectedDate;

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
                    _appWindow.Title = "Screen Time Tracker";
                    _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                    _appWindow.TitleBar.ButtonBackgroundColor = MicrosoftUI.Colors.Transparent;
                    _appWindow.TitleBar.ButtonInactiveBackgroundColor = MicrosoftUI.Colors.Transparent;

                    // Set the window icon
                    var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
                    if (File.Exists(iconPath))
                    {
                        try
                        {
                            SendMessage(windowHandle, WM_SETICON, ICON_SMALL, LoadImage(IntPtr.Zero, iconPath,
                                IMAGE_ICON, 0, 0, LR_LOADFROMFILE));
                            SendMessage(windowHandle, WM_SETICON, ICON_BIG, LoadImage(IntPtr.Zero, iconPath,
                                IMAGE_ICON, 0, 0, LR_LOADFROMFILE));
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
                
                case TimePeriod.Monthly:
                    // Get days in the selected month
                    return DateTime.DaysInMonth(date.Year, date.Month);
                
                case TimePeriod.Daily:
                default:
                    // Daily view is just 1 day
                    return 1;
            }
        }

        private void HourlyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChartViewMode != ChartViewMode.Hourly)
            {
                _currentChartViewMode = ChartViewMode.Hourly;
                UpdateChartViewMode();
                UpdateUsageChart();
                
                // Update button styles
                HourlyButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
                DailyButton.Style = Application.Current.Resources["DefaultButtonStyle"] as Style;
            }
        }

        private void DailyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChartViewMode != ChartViewMode.Daily)
            {
                _currentChartViewMode = ChartViewMode.Daily;
                UpdateChartViewMode();
                UpdateUsageChart();
                
                // Update button styles
                DailyButton.Style = Application.Current.Resources["AccentButtonStyle"] as Style;
                HourlyButton.Style = Application.Current.Resources["DefaultButtonStyle"] as Style;
            }
        }
        
        private void UpdateChartViewMode()
        {
            // No need to update the chart title text since it's now split into label and value
            // The actual value will be updated in UpdateUsageChart
                
            // Other chart settings can be updated here based on view mode
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
    }
}


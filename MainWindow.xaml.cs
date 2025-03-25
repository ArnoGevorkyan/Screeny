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
using ScottPlot;
using ScottPlot.WinUI;
using SDColor = System.Drawing.Color; // Alias for System.Drawing.Color
using Microsoft.UI; // Add this for Win32Interop

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
        
        public enum TimePeriod
        {
            Daily,
            Weekly,
            Monthly
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
                    
                    // Update summary and chart since durations changed
                    UpdateSummaryTab();
                    UpdateUsageChart();
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

                // First try to find exact match
                var existingRecord = FindExistingRecord(record);

                if (existingRecord != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Found existing record: {existingRecord.ProcessName}");
                    
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
                    System.Diagnostics.Debug.WriteLine($"Record added to _usageRecords, collection count: {_usageRecords.Count}");
                    
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
                
                // Update the summary and chart in real-time
                System.Diagnostics.Debug.WriteLine("Updating summary and chart in real-time");
                UpdateSummaryTab();
                UpdateUsageChart();
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
                LoadRecordsForDate(_selectedDate);

                // Clean up any system processes that might have been added
                CleanupSystemProcesses();
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            ThrowIfDisposed();

            try
            {
                System.Diagnostics.Debug.WriteLine("StartButton_Click: Starting tracking");
                var currentDate = DateTime.Now.Date;

                // Set the date picker to today
                if (_selectedDate != currentDate)
                {
                    _selectedDate = currentDate;
                    DatePicker.Date = _selectedDate;
                    LoadRecordsForDate(_selectedDate);
                }
                else
                {
                    // Make sure the chart is updated
                    UpdateUsageChart();
                    
                    // Update the summary
                    UpdateSummaryTab();
                }

                // Start tracking window activity
                System.Diagnostics.Debug.WriteLine("Starting window tracking");
            _trackingService.StartTracking();
            
                // Update UI elements
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
                System.Diagnostics.Debug.WriteLine("StartButton disabled, StopButton enabled");
            
                // Start the timers
                _updateTimer.Start();
                _autoSaveTimer.Start();
                System.Diagnostics.Debug.WriteLine("Timers started");

                // Clean up any system processes that might have been added
                CleanupSystemProcesses();
                
                System.Diagnostics.Debug.WriteLine("StartButton_Click completed successfully");
            }
            catch (Exception ex)
            {
                // Log the error
                System.Diagnostics.Debug.WriteLine($"Error starting tracking: {ex.Message}");

                // Display an error message
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
                // Clear existing records
                _usageRecords.Clear();

                // Load records from database if available
                if (_databaseService != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Loading records for date: {date.ToShortDateString()}");
                    
                    // For weekly and monthly views, we need to get records for a range of dates
                    List<AppUsageRecord> records;
                    
                    switch (_currentTimePeriod)
                    {
                        case TimePeriod.Weekly:
                            // Get records for the week containing the selected date
                            var startOfWeek = date.AddDays(-(int)date.DayOfWeek);
                            var endOfWeek = startOfWeek.AddDays(6);
                            records = GetAggregatedRecordsForDateRange(startOfWeek, endOfWeek);
                            
                            // Update chart title
                            ChartTitle.Text = $"Weekly Screen Time ({startOfWeek:MMM dd} - {endOfWeek:MMM dd})";
                            break;
                            
                        case TimePeriod.Monthly:
                            // Get records for the month containing the selected date
                            var startOfMonth = new DateTime(date.Year, date.Month, 1);
                            var endOfMonth = startOfMonth.AddMonths(1).AddDays(-1);
                            records = GetAggregatedRecordsForDateRange(startOfMonth, endOfMonth);
                            
                            // Update chart title
                            ChartTitle.Text = $"Monthly Screen Time ({startOfMonth:MMMM yyyy})";
                            break;
                            
                        case TimePeriod.Daily:
                        default:
                            // Default to daily view
                            records = _databaseService.GetAggregatedRecordsForDate(date);
                            
                            // Update chart title
                            ChartTitle.Text = $"Daily Screen Time ({date:MMM dd, yyyy})";
                            break;
                    }

                    // Add records to the observable collection
                    foreach (var record in records.Where(r => !IsWindowsSystemProcess(r.ProcessName)))
                    {
                        _usageRecords.Add(record);
                        // Load app icons for each record
                        record.LoadAppIconIfNeeded();
                    }

                    System.Diagnostics.Debug.WriteLine($"Loaded {records.Count} records from database");
                    
                    // Update the chart
                    UpdateUsageChart();
                    
                    // Update the summary
                    UpdateSummaryTab();
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting aggregated records: {ex.Message}");
            }
            
            return result;
        }
        
        private void UpdateUsageChart()
        {
            try
            {
                // Check if chart control is available
                if (UsageChart == null)
                {
                    System.Diagnostics.Debug.WriteLine("UsageChart is null - unable to update chart");
                    return;
                }

                // Clear the existing plot
                UsageChart.Plot.Clear();
                
                // Get top 10 applications by usage duration
                var topApps = _usageRecords
                    .OrderByDescending(r => r.Duration)
                    .Take(10)
                    .ToList();
                
                if (topApps.Count == 0)
                {
                    // No data to display
                    UsageChart.Plot.Add.Text(
                        "No data available for the selected period", 
                        0.5, 0.5);
                    UsageChart.Refresh();
                    return;
                }
                
                // Prepare data for the chart
                string[] labels = topApps.Select(r => r.ProcessName).ToArray();
                double[] values = topApps.Select(r => r.Duration.TotalHours).ToArray();
                
                // Create a bar chart
                var barPlot = UsageChart.Plot.Add.Bars(values);
                
                // Configure axes
                UsageChart.Plot.Axes.Bottom.Label.Text = "Applications";
                UsageChart.Plot.Axes.Left.Label.Text = "Hours";
                
                // Set custom tick labels for bottom axis
                double[] positions = Enumerable.Range(0, labels.Length).Select(i => (double)i).ToArray();
                UsageChart.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(positions, labels);
                
                // Refresh the chart
                UsageChart.Refresh();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating chart: {ex.Message}");
                // Don't throw - allow the application to continue without the chart
            }
        }
        
        private void TimePeriodSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TimePeriodSelector.SelectedIndex >= 0)
            {
                _currentTimePeriod = (TimePeriod)TimePeriodSelector.SelectedIndex;
                
                // Reload data with the new time period
                LoadRecordsForDate(_selectedDate);
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
                
                // Update the chart if we're viewing today's data
                if (_selectedDate.Date == DateTime.Now.Date)
                {
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
                if (_usageRecords.Count == 0)
                {
                    // No data available
                    TotalScreenTime.Text = "0h 0m";
                    MostUsedApp.Text = "None";
                    MostUsedAppTime.Text = "0h 0m";
                    SummaryTitle.Text = "No Data Available";
                    AveragePanel.Visibility = Visibility.Collapsed;
                    return;
                }
                
                // Calculate total screen time
                TimeSpan totalTime = TimeSpan.Zero;
                foreach (var record in _usageRecords)
                {
                    totalTime += record.Duration;
                }
                
                // Find the most used app
                var mostUsedRecord = _usageRecords.OrderByDescending(r => r.Duration).FirstOrDefault();
                
                // Update UI
                TotalScreenTime.Text = FormatTimeSpan(totalTime);
                
                if (mostUsedRecord != null)
                {
                    MostUsedApp.Text = mostUsedRecord.ProcessName;
                    MostUsedAppTime.Text = FormatTimeSpan(mostUsedRecord.Duration);
                }
                else
                {
                    MostUsedApp.Text = "None";
                    MostUsedAppTime.Text = "0h 0m";
                }
                
                // Update summary title based on time period
                switch (_currentTimePeriod)
                {
                    case TimePeriod.Weekly:
                        var startOfWeek = _selectedDate.AddDays(-(int)_selectedDate.DayOfWeek);
                        var endOfWeek = startOfWeek.AddDays(6);
                        SummaryTitle.Text = $"Weekly Summary ({startOfWeek:MMM dd} - {endOfWeek:MMM dd})";
                        
                        // Show daily average
                        AveragePanel.Visibility = Visibility.Visible;
                        int daysInPeriod = 7;
                        DailyAverage.Text = FormatTimeSpan(TimeSpan.FromTicks(totalTime.Ticks / daysInPeriod));
                        break;
                        
                    case TimePeriod.Monthly:
                        var startOfMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);
                        var endOfMonth = startOfMonth.AddMonths(1).AddDays(-1);
                        SummaryTitle.Text = $"Monthly Summary ({startOfMonth:MMMM yyyy})";
                        
                        // Show daily average
                        AveragePanel.Visibility = Visibility.Visible;
                        daysInPeriod = DateTime.DaysInMonth(_selectedDate.Year, _selectedDate.Month);
                        DailyAverage.Text = FormatTimeSpan(TimeSpan.FromTicks(totalTime.Ticks / daysInPeriod));
                        break;
                        
                    case TimePeriod.Daily:
                    default:
                        SummaryTitle.Text = $"Daily Summary ({_selectedDate:MMM dd, yyyy})";
                        AveragePanel.Visibility = Visibility.Collapsed;
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating summary: {ex.Message}`");
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

                // Load initial records
                LoadRecordsForDate(_selectedDate);

                // Set active tab
                FrameworkElement root = (FrameworkElement)Content;
                var mainTabView = root.FindName("DataTabView") as TabView;
                if (mainTabView != null)
                {
                    mainTabView.SelectedIndex = 0;
                    System.Diagnostics.Debug.WriteLine("Set DataTabView selected index to 0");
                }

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
            // This is now a separate method for clarity
            if (_databaseService?.IsFirstRun() == true)
            {
                try
                {
                    // First run message - but only show if window is initialized
                    if (this.Content?.XamlRoot != null)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Welcome to Screen Time Tracker",
                            Content = "This application will track your app usage. Press 'Start Tracking' to begin monitoring.",
                            CloseButtonText = "OK"
                        };

                        dialog.XamlRoot = this.Content.XamlRoot;
                        _ = dialog.ShowAsync();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error showing first run dialog: {ex.Message}");
                }
            }
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
                        // Access UI elements properly in WinUI 3
                        FrameworkElement root = (FrameworkElement)Content;
                        
                        // Update active window info
                        var currentAppTextBlock = root.FindName("CurrentAppTextBlock") as TextBlock;
                        if (currentAppTextBlock != null)
                        {
                            currentAppTextBlock.Text = _trackingService.CurrentRecord.ApplicationName;
                        }

                        var currentDurationTextBlock = root.FindName("CurrentDurationTextBlock") as TextBlock;
                        if (currentDurationTextBlock != null)
                        {
                            currentDurationTextBlock.Text = _trackingService.CurrentRecord.Duration.ToString(@"hh\:mm\:ss");
                        }
                        
                        // Update current app icon if available
                        if (_trackingService.CurrentRecord.AppIcon != null)
                        {
                            var currentAppIcon = root.FindName("CurrentAppIcon") as Microsoft.UI.Xaml.Controls.Image;
                            if (currentAppIcon != null)
                            {
                                currentAppIcon.Source = _trackingService.CurrentRecord.AppIcon;
                            }
                        }
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
    }
}

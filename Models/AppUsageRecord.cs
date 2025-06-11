using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace ScreenTimeTracker.Models
{
    public class AppUsageRecord : INotifyPropertyChanged
    {
        [DllImport("Shell32.dll")]
        private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, StringBuilder lpIconPath, out ushort lpiIcon);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_SMALLICON = 0x1;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private static readonly HashSet<string> _ignoredProcesses = new()
        {
            // Windows explorer and UI components
            "explorer",
            "SearchHost",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "ApplicationFrameHost",
            "SystemSettings",
            "TextInputHost",
            "SearchUI",
            "Cortana",
            "SearchApp",
            
            // Terminals and command line
            "WindowsTerminal",
            "cmd",
            "powershell",
            "pwsh",
            "conhost",
            "wt",
            
            // Windows store and system apps
            "WinStore.App",
            "LockApp",
            "LogonUI",
            "UserOOBEBroker",
            "osk", // On-screen keyboard
            
            // Core system processes
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
            "SecurityHealthSystray",
            "SecurityHealthService",
            "Registry",
            "MicrosoftEdgeUpdate",
            "WmiPrvSE",
            "spoolsv",
            "TabTip",
            "TabTip32",
            "winlogon",
            "wlanext",
            "wuauclt",
            "lsaiso",
            "csrss",
            "wininit",
            "runtimebroker",
            "backgroundTaskHost",
            
            // Windows Defender and Security
            "MsMpEng",
            "SecurityHealthService",
            "SecurityHealthSystray",
            "smartscreen",
            "MpCmdRun",
            "NisSrv",
            "MpSigStub",
            
            // Windows Update and Maintenance
            "TiWorker",
            "UsoClient",
            "WaasMedicAgent",
            "SIHClient",
            "UpdateAssistant",
            "MediaCreationTool",
            "WindowsUpdateBox",
            
            // Background services and drivers
            "igfxEM",
            "igfxHK",
            "igfxTray",
            "igfxCUIService",
            "audiodg",
            "smss",
            "lsass",
            "NVDisplay.Container",
            "nvcontainer",
            "ONENOTEM",
            "SettingSyncHost",
            "uhssvc",
            "WUDFHost",
            "AAM Updates Notifier",
            "CompPkgSrv",
            "PresentationFontCache",
            "SearchIndexer",
            "SgrmBroker",
            "SpeechRuntime",
            "startup",
            "System",
            "SystemSettingsBroker",
            
            // Graphics and Hardware
            "nvtray",
            "nvdisplay.container",
            "RtkAuUService64",
            "RtkAudioService",
            "IAStorIcon",
            "IntelAudioService",
            "AdobeUpdateService",
            
            // Cloud and Sync Services (background only)
            "OneDrive",
            "GoogleDriveFS",
            "Dropbox",
            "iCloudServices",
            "SkypeHost",
            
            // Microsoft Office background services
            "OfficeClickToRun",
            "msoia",
            "MSOSYNC",
            "MicrosoftEdgeUpdate",
            
            // Antivirus background processes (common ones)
            "AvastSvc",
            "avp",
            "avgnt",
            "McShield",
            "nortonsecurity",
            "SentinelAgent",
            "CarbonBlack",
            
            // Windows tools and dialogs
            "SnippingTool",
            "EaseOfAccessDialog",
            "Magnify", // Windows Magnifier
            "Narrator", // Windows Narrator
            "Notepad", // Basic notepad for quick notes
            "mspaint", // MS Paint
            "charmap", // Character Map
            "calc", // Windows Calculator
            "SoundVolumeView",
            
            // Razer and gaming hardware utilities
            "RazerCortex.Shell",
            "Razer Synapse Service Process",
            "RazerCentralService",
            "RzSDKService",
            "RzChromaStreamServer",
            "RazerIngameEngine",
            "RzActionSvc",
            "Razer Synapse 3 Host",
            "RazerAppEngine",
            
            // Other gaming hardware utilities
            "LogitechGHUB",
            "LGHUB",
            "LGHUBUpdater",
            "CORSAIR iCUE 4 Software",
            "iCUE",
            "CorsairService",
            "SteelSeriesEngine",
            "SSEngine3",
            "NvContainer",
            "NVIDIA Web Helper Service",
            "NVIDIA Display Driver Service",
            "GeForceExperience",
            "NvTelemetryContainer",
            "MSI Afterburner",
            "RTSSHooksLoader64",
            "EVGA Precision X1",
            "HWiNFO64",
            "GPU-Z",
            "CPU-Z",
            
            // RGB and peripheral software
            "SignalRGB",
            "OpenRGB",
            "AuraService",
            "AsusSystemControlInterface",
            "ArmouryCrate.Service",
            "ArmouryCrateService",
            "LightingService",
            
            // Network and connectivity
            "NetworkManager",
            "WifiManager",
            "BluetoothUserService",
            "PhoneExperienceHost",
            
            // Gaming platform launchers/background services
            "VimeWorld",
            "Steam", // Steam when just running in background
            "EpicGamesLauncher",
            "Origin",
            "Battle.net",
            "RiotClientServices",
            
            // System utilities that run briefly
            "msiexec",
            "InstallUtil",
            "MicrosoftEdgeCP",
            "WWAHost",
            "Video.UI",
            "Photos",
            "Movies & TV",
            "Groove Music"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            if (propertyName == "Duration" || propertyName == "FormattedDuration")
            {
                System.Diagnostics.Debug.WriteLine($"[PROPERTY_LOG] NotifyPropertyChanged for {ProcessName}.{propertyName} at {DateTime.Now:HH:mm:ss.fff}");
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public int Id { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string WindowTitle { get; set; } = string.Empty;
        public IntPtr WindowHandle { get; set; }
        public bool IsFocused { get; set; }
        public string ApplicationName { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public DateTime? LastUpdated { get; set; }
        
        private BitmapImage? _appIcon;
        public BitmapImage? AppIcon
        {
            get => _appIcon;
            private set
            {
                if (_appIcon != value)
                {
                    _appIcon = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private bool _isLoadingIcon = false;

        private void LoadIconAsync()
        {
            // Prevent multiple simultaneous attempts to load the icon
            if (_isLoadingIcon) return;
            _isLoadingIcon = true;

            // Start a background task to load the icon
            // But don't use Task.Run which creates a new thread
            _ = InternalLoadIconAsync();
        }

        private async Task InternalLoadIconAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Loading icon for {ProcessName}");
                
                bool iconLoaded = false;
                
                // First, check for UWP apps which need special handling
                iconLoaded = await TryLoadUwpAppIcon();
                if (iconLoaded)
                {
                    System.Diagnostics.Debug.WriteLine($"Successfully loaded UWP app icon for {ProcessName}");
                    return;
                }
                
                // Next, try well-known system apps
                iconLoaded = await TryGetWellKnownSystemIcon();
                if (iconLoaded)
                {
                    System.Diagnostics.Debug.WriteLine($"Successfully loaded well-known system icon for {ProcessName}");
                    return;
                }
                
                // Then try to get executable path for standard apps
                string? exePath = GetExecutablePath();
                if (!string.IsNullOrEmpty(exePath))
                {
                    System.Diagnostics.Debug.WriteLine($"Found executable path: {exePath}");
                    
                    // Try standard icon extraction methods
                    iconLoaded = await TryLoadIconWithSHGetFileInfo(exePath);
                    if (iconLoaded)
                    {
                        System.Diagnostics.Debug.WriteLine($"Successfully loaded icon with SHGetFileInfo for {ProcessName}");
                        return;
                    }
                    
                    iconLoaded = await TryLoadIconWithExtractAssociatedIcon(exePath);
                    if (iconLoaded)
                    {
                        System.Diagnostics.Debug.WriteLine($"Successfully loaded icon with ExtractAssociatedIcon for {ProcessName}");
                        return;
                    }
                    
                    // If the file is a DLL, try the DLL icon extractor
                    if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        iconLoaded = await TryLoadIconFromDll(exePath);
                        if (iconLoaded)
                        {
                            System.Diagnostics.Debug.WriteLine($"Successfully loaded icon from DLL for {ProcessName}");
                            return;
                        }
                    }
                }
                
                // As a last resort, try to load a generic icon from shell32.dll
                if (!iconLoaded)
                {
                    string shell32Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
                    if (File.Exists(shell32Path))
                    {
                        iconLoaded = await TryLoadIconFromDll(shell32Path);
                        if (iconLoaded)
                        {
                            System.Diagnostics.Debug.WriteLine($"Used generic shell32.dll icon for {ProcessName}");
                            return;
                        }
                    }
                }
                
                if (!iconLoaded)
                {
                    System.Diagnostics.Debug.WriteLine($"All icon loading methods failed for {ProcessName}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading app icon for {ProcessName}: {ex.Message}");
            }
            finally
            {
                _isLoadingIcon = false;
            }
        }
        
        private async Task<bool> TryGetWellKnownSystemIcon()
        {
            try
            {
                string? iconPath = null;
                string processNameLower = ProcessName.ToLower();
                
                // Special handling for WhatsApp
                if (ProcessName.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase))
                {
                    // Common paths for WhatsApp
                    string[] whatsAppPaths = {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsApp", "WhatsApp.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WhatsApp", "WhatsApp.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WhatsApp", "WhatsApp.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "WhatsAppDesktop.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "WhatsApp.exe")
                    };
                    
                    foreach (var path in whatsAppPaths)
                    {
                        if (File.Exists(path))
                        {
                            System.Diagnostics.Debug.WriteLine($"Using WhatsApp icon from: {path}");
                            return await TryLoadIconWithSHGetFileInfo(path);
                        }
                    }
                    
                    // Search for WhatsApp in WindowsApps directories
                    try
                    {
                        string windowsAppsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
                        if (Directory.Exists(windowsAppsDir))
                        {
                            var whatsAppDirs = Directory.GetDirectories(windowsAppsDir, "*WhatsApp*", SearchOption.AllDirectories);
                            foreach (var dir in whatsAppDirs)
                            {
                                foreach (var ext in new[] { "*.exe", "*.ico", "*.png" })
                                {
                                    var files = Directory.GetFiles(dir, ext, SearchOption.AllDirectories);
                                    foreach (var file in files)
                                    {
                                        if (Path.GetFileName(file).Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                            {
                                                return await TryLoadIconWithSHGetFileInfo(file);
                                            }
                                            else if (file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
                                                    file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                            {
                                                return await LoadImageFromFile(file);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error searching for WhatsApp icon: {ex.Message}");
                    }
                    
                    processNameLower = "whatsapp";
                }
                
                // Special handling for browsers
                if (IsBrowser(ProcessName))
                {
                    Dictionary<string, string[]> browserPaths = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Arc", new string[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Arc", "Arc.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Arc", "Arc.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Arc", "Arc.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arc", "app", "Arc.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arc", "Arc.exe")
                        }},
                        { "Chrome", new string[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe")
                        }},
                        { "Firefox", new string[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe")
                        }},
                        { "Microsoft Edge", new string[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe")
                        }},
                        { "Opera", new string[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Opera", "launcher.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Opera", "launcher.exe")
                        }},
                        { "Brave", new string[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
                        }}
                    };

                    // Check for browser in standard paths
                    if (browserPaths.TryGetValue(ProcessName, out string[]? paths))
                    {
                        foreach (var path in paths)
                        {
                            if (File.Exists(path))
                            {
                                System.Diagnostics.Debug.WriteLine($"Found browser executable for {ProcessName}: {path}");
                                return await TryLoadIconWithSHGetFileInfo(path);
                            }
                        }
                    }
                    
                    // If specific paths don't work, try more generic browser detection
                    processNameLower = ProcessName.ToLower();
                }
                
                // Special handling for Java-based games (like Minecraft)
                else if (IsJavaGame(ProcessName))
                {
                    // Try game-specific paths
                    List<string> gamePaths = new List<string>();
                    
                    // Add common paths based on the type of game
                    string gameDir = GetGameDirectory(ProcessName);
                    if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
                    {
                        // Try to find game executable or launcher
                        gamePaths.AddRange(new[]
                        {
                            Path.Combine(gameDir, "launcher", $"{ProcessName}.exe"),
                            Path.Combine(gameDir, $"{ProcessName}.exe"),
                            Path.Combine(gameDir, "launcher", "launcher.exe"),
                            Path.Combine(gameDir, "launcher", "game.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProcessName, $"{ProcessName}.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), ProcessName, $"{ProcessName}.exe")
                        });
                        
                        // Try to find icon files
                        TryFindIconFilesInDirectory(gameDir, gamePaths);
                    }
                    
                    // Try standard Minecraft paths as fallbacks
                    gamePaths.AddRange(new[] 
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "launcher", "Minecraft.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Minecraft", "MinecraftLauncher.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe")
                    });
                    
                    foreach (var path in gamePaths)
                    {
                        if (File.Exists(path))
                        {
                            System.Diagnostics.Debug.WriteLine($"Found game icon path: {path}");
                            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                return await TryLoadIconWithSHGetFileInfo(path);
                            }
                            else if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || 
                                     path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                            {
                                return await LoadImageFromFile(path);
                            }
                        }
                    }
                    
                    // If we can't find a specific game icon, use Java as fallback
                    string javaPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java", "bin", "javaw.exe");
                    if (File.Exists(javaPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"Using Java icon for {ProcessName}: {javaPath}");
                        return await TryLoadIconWithSHGetFileInfo(javaPath);
                    }
                    
                    processNameLower = "javaw"; // Fall back to finding java icon
                }
                
                // Map common processes to known system DLLs/EXEs with good icons
                Dictionary<string, string> wellKnownPaths = new Dictionary<string, string>
                {
                    // Browsers
                    { "chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe") },
                    { "firefox", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe") },
                    { "msedge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe") },
                    { "iexplore", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Internet Explorer", "iexplore.exe") },
                    { "arc", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Arc", "Arc.exe") },
                    { "brave", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe") },
                    { "opera", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Opera", "launcher.exe") },
                    
                    // Microsoft Office
                    { "winword", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Office", "root", "Office16", "WINWORD.EXE") },
                    { "excel", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Office", "root", "Office16", "EXCEL.EXE") },
                    { "powerpnt", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Office", "root", "Office16", "POWERPNT.EXE") },
                    { "outlook", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Office", "root", "Office16", "OUTLOOK.EXE") },
                    
                    // System processes with special handling
                    { "explorer", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") },
                    { "notepad", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe") },
                    { "mspaint", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mspaint.exe") },
                    { "calc", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "calc.exe") },
                    
                    // Java applications
                    { "javaw", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java", "bin", "javaw.exe") },
                    { "java", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java", "bin", "java.exe") },
                    
                    // Python
                    { "python", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python", "python.exe") },
                    { "pythonw", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python", "pythonw.exe") },
                    
                    // Known system DLLs with good icons
                    { "searchapp", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll") },
                    { "applicationframehost", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll") },
                    { "systemsettings", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll") },
                    
                    // Common applications
                    { "code", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe") },
                    { "discord", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "app-", "Discord.exe") },
                    { "spotify", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "Spotify.exe") },
                    { "whatsapp", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsApp", "WhatsApp.exe") },
                    { "telegram", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Telegram Desktop", "Telegram.exe") },
                    { "visualstudio", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "2022", "Professional", "Common7", "IDE", "devenv.exe") }
                };
                
                // Check if we have a known path for this process
                if (wellKnownPaths.TryGetValue(processNameLower, out string? knownPath) && File.Exists(knownPath))
                {
                    iconPath = knownPath;
                    System.Diagnostics.Debug.WriteLine($"Found known system path for {ProcessName}: {iconPath}");
                }


                
                // If we found a path, try to load the icon
                if (!string.IsNullOrEmpty(iconPath))
                {
                    // For DLL files, we need to use a different approach with icon resource indices
                    if (iconPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        return await TryLoadIconFromDll(iconPath);
                    }
                    else
                    {
                        // For EXE files, try our normal methods
                        bool success = await TryLoadIconWithSHGetFileInfo(iconPath);
                        if (!success)
                        {
                            success = await TryLoadIconWithExtractAssociatedIcon(iconPath);
                        }
                        return success;
                    }
                }

                // General fallback: Search for any executable with the process name in common directories
                if (string.IsNullOrEmpty(iconPath))
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"Trying general fallback search for {ProcessName}");
                        
                        // Common installation directories to search
                        string[] searchDirectories = {
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps")
                        };

                        foreach (string baseDir in searchDirectories)
                        {
                            if (!Directory.Exists(baseDir)) continue;

                            try
                            {
                                // Search for directories that contain the process name
                                var matchingDirs = Directory.GetDirectories(baseDir, $"*{ProcessName}*", SearchOption.TopDirectoryOnly);
                                
                                foreach (string dir in matchingDirs)
                                {
                                    // Look for executable with same name as process
                                    string[] possibleExePaths = {
                                        Path.Combine(dir, $"{ProcessName}.exe"),
                                        Path.Combine(dir, "app", $"{ProcessName}.exe"),
                                        Path.Combine(dir, "bin", $"{ProcessName}.exe"),
                                        Path.Combine(dir, "Application", $"{ProcessName}.exe")
                                    };

                                    foreach (string exePath in possibleExePaths)
                                    {
                                        if (File.Exists(exePath))
                                        {
                                            System.Diagnostics.Debug.WriteLine($"Found executable in fallback search: {exePath}");
                                            bool success = await TryLoadIconWithSHGetFileInfo(exePath);
                                            if (!success)
                                            {
                                                success = await TryLoadIconWithExtractAssociatedIcon(exePath);
                                            }
                                            if (success) return true;
                                        }
                                    }
                                }

                                // Also search for the executable directly in the base directory
                                string directExePath = Path.Combine(baseDir, $"{ProcessName}.exe");
                                if (File.Exists(directExePath))
                                {
                                    System.Diagnostics.Debug.WriteLine($"Found executable directly in base dir: {directExePath}");
                                    bool success = await TryLoadIconWithSHGetFileInfo(directExePath);
                                    if (!success)
                                    {
                                        success = await TryLoadIconWithExtractAssociatedIcon(directExePath);
                                    }
                                    if (success) return true;
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error searching in {baseDir}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in general fallback search for {ProcessName}: {ex.Message}");
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in TryGetWellKnownSystemIcon: {ex.Message}");
                return false;
            }
        }
        
        private bool IsBrowser(string processName)
        {
            string[] browsers = { "chrome", "firefox", "msedge", "iexplore", "opera", "brave", "arc" };
            return browsers.Any(browser => 
                processName.Equals(browser, StringComparison.OrdinalIgnoreCase) ||
                processName.Contains(browser, StringComparison.OrdinalIgnoreCase));
        }
        
        private bool IsJavaGame(string processName)
        {
            string[] knownJavaGames = { "minecraft", "forge", "fabric" };
            return knownJavaGames.Any(g => processName.Contains(g, StringComparison.OrdinalIgnoreCase));
        }
        
        private string GetGameDirectory(string processName)
        {
            // Generic logic to determine game directory based on process name
            string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            
            // Try to create a standardized directory name from process name
            string gameFolder = processName.ToLower();
            if (gameFolder.Contains("minecraft") || gameFolder == "minecraft")
            {
                return Path.Combine(appDataDir, ".minecraft");
            }
            
            // Try directly using the process name with a dot prefix
            string dotPrefixDir = Path.Combine(appDataDir, $".{gameFolder}");
            if (Directory.Exists(dotPrefixDir))
            {
                return dotPrefixDir;
            }
            
            // Try without dot prefix
            string noDotDir = Path.Combine(appDataDir, gameFolder);
            if (Directory.Exists(noDotDir))
            {
                return noDotDir;
            }
            
            // If no specific directory found, default to .minecraft
            return Path.Combine(appDataDir, ".minecraft");
        }
        
        private void TryFindIconFilesInDirectory(string directory, List<string> paths)
        {
            try
            {
                // Common icon filenames
                string[] iconFileNames = { "icon.png", "logo.png", "favicon.png", "icon.ico", "launcher.ico" };
                
                // Check in main directory and launcher subdirectory
                string[] dirsToCheck = { directory, Path.Combine(directory, "launcher") };
                
                foreach (var dir in dirsToCheck)
                {
                    if (Directory.Exists(dir))
                    {
                        foreach (var iconName in iconFileNames)
                        {
                            string iconPath = Path.Combine(dir, iconName);
                            if (File.Exists(iconPath))
                            {
                                paths.Add(iconPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error searching for icon files: {ex.Message}");
            }
        }
        
        [DllImport("Shell32.dll", EntryPoint = "#727")]
        private extern static int SHGetImageList(int iImageList, ref Guid riid, ref IImageList ppv);
        
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig]
            int Draw(IntPtr hdc, int i, int x, int y, int style);
            
            [PreserveSig]
            int GetIcon(int i, int flag, ref IntPtr picon);
        }
        
        private async Task<bool> TryLoadIconFromDll(string dllPath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Loading icon from DLL {dllPath} for {ProcessName}");
                
                // For well-known system processes, use specific icon indices from shell32.dll
                int iconIndex = 0;
                
                string processNameLower = ProcessName.ToLower();
                if (dllPath.ToLower().Contains("shell32.dll"))
                {
                    // Map processes to shell32.dll icon indices
                    Dictionary<string, int> iconIndices = new Dictionary<string, int>
                    {
                        { "searchapp", 22 },             // Search icon
                        { "applicationframehost", 15 },  // Generic application icon
                        { "systemsettings", 317 },       // Settings gear icon
                        { "explorer", 3 },               // Folder icon
                        // Add more mappings as needed
                    };
                    
                    if (iconIndices.TryGetValue(processNameLower, out int index))
                    {
                        iconIndex = index;
                    }
                }
                
                // Try to get the icon using ExtractIconEx
                IntPtr[] largeIcons = new IntPtr[1] { IntPtr.Zero };
                IntPtr[] smallIcons = new IntPtr[1] { IntPtr.Zero };
                
                try
                {
                    int iconCount = ExtractIconEx(dllPath, iconIndex, largeIcons, smallIcons, 1);
                    if (iconCount > 0 && smallIcons[0] != IntPtr.Zero)
                    {
                        using (Icon icon = Icon.FromHandle(smallIcons[0]))
                        using (Bitmap bitmap = icon.ToBitmap())
                        {
                            BitmapImage? bitmapImage = await ConvertBitmapToBitmapImageAsync(bitmap);
                            if (bitmapImage != null)
                            {
                                AppIcon = bitmapImage;
                                System.Diagnostics.Debug.WriteLine($"Successfully loaded icon from DLL for {ProcessName}");
                                return true;
                            }
                        }
                    }
                }
                finally
                {
                    // Clean up handles
                    if (largeIcons[0] != IntPtr.Zero)
                        DestroyIcon(largeIcons[0]);
                    if (smallIcons[0] != IntPtr.Zero)
                        DestroyIcon(smallIcons[0]);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading icon from DLL for {ProcessName}: {ex.Message}");
            }
            
            return false;
        }
        
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int ExtractIconEx(string szFileName, int nIconIndex, 
            [Out] IntPtr[] phiconLarge, [Out] IntPtr[] phiconSmall, int nIcons);
        
        private async Task<bool> TryLoadIconWithSHGetFileInfo(string exePath)
        {
            try
            {
                // Use SHGetFileInfo to get the icon
                SHFILEINFO shfi = new SHFILEINFO();
                IntPtr result = SHGetFileInfo(
                    exePath,
                    FILE_ATTRIBUTE_NORMAL,
                    ref shfi,
                    (uint)Marshal.SizeOf(shfi),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

                // retry with large icon if no small icon returned
                if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"Small icon not found, trying large icon for {ProcessName}");
                    result = SHGetFileInfo(
                        exePath,
                        FILE_ATTRIBUTE_NORMAL,
                        ref shfi,
                        (uint)Marshal.SizeOf(shfi),
                        SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                }

                if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"SHGetFileInfo failed for {ProcessName}");
                    return false;
                }
                
                System.Diagnostics.Debug.WriteLine($"Successfully got icon handle for {ProcessName}");

                try
                {
                    // Convert the icon to a bitmap
                    using (Icon icon = Icon.FromHandle(shfi.hIcon))
                    {
                        System.Diagnostics.Debug.WriteLine($"Created Icon from handle for {ProcessName}");
                        using (Bitmap bitmap = icon.ToBitmap())
                        {
                            System.Diagnostics.Debug.WriteLine($"Created Bitmap from Icon for {ProcessName}");
                            
                            // Convert bitmap to BitmapImage for WinUI
                            BitmapImage? bitmapImage = await ConvertBitmapToBitmapImageAsync(bitmap);
                            if (bitmapImage == null)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to convert bitmap to BitmapImage for {ProcessName}");
                                return false;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"Successfully created BitmapImage for {ProcessName}");
                                AppIcon = bitmapImage;
                                System.Diagnostics.Debug.WriteLine($"Set AppIcon property for {ProcessName}");
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error converting icon for {ProcessName}: {ex.Message}");
                    return false;
                }
                finally
                {
                    // Always clean up the icon handle
                    bool destroyed = DestroyIcon(shfi.hIcon);
                    System.Diagnostics.Debug.WriteLine($"Icon handle destroyed: {destroyed}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in TryLoadIconWithSHGetFileInfo: {ex.Message}");
                return false;
            }
        }
        
        private async Task<bool> TryLoadIconWithExtractAssociatedIcon(string exePath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Trying ExtractAssociatedIcon for {ProcessName}");
                StringBuilder sb = new StringBuilder(exePath);
                ushort iconIndex = 0;
                
                IntPtr iconHandle = ExtractAssociatedIcon(IntPtr.Zero, sb, out iconIndex);
                if (iconHandle == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine($"ExtractAssociatedIcon failed for {ProcessName}");
                    return false;
                }
                
                System.Diagnostics.Debug.WriteLine($"ExtractAssociatedIcon succeeded for {ProcessName}");
                
                try
                {
                    using (Icon icon = Icon.FromHandle(iconHandle))
                    using (Bitmap bitmap = icon.ToBitmap())
                    {
                        BitmapImage? bitmapImage = await ConvertBitmapToBitmapImageAsync(bitmap);
                        if (bitmapImage != null)
                        {
                            AppIcon = bitmapImage;
                            System.Diagnostics.Debug.WriteLine($"Successfully set icon using ExtractAssociatedIcon for {ProcessName}");
                            return true;
                        }
                    }
                }
                finally
                {
                    DestroyIcon(iconHandle);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in TryLoadIconWithExtractAssociatedIcon: {ex.Message}");
            }
            
            return false;
        }

        private async Task<BitmapImage?> ConvertBitmapToBitmapImageAsync(Bitmap bitmap)
        {
            try
            {
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    // Save bitmap to stream
                    bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                    memoryStream.Position = 0;

                    // Create a BitmapImage and set its source
                    var bitmapImage = new BitmapImage();
                    
                    // Use an InMemoryRandomAccessStream
                    using (var randomAccessStream = new InMemoryRandomAccessStream())
                    {
                        using (var writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
                        {
                            var bytes = memoryStream.ToArray();
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                            await writer.FlushAsync();
                        }
                        
                        randomAccessStream.Seek(0);
                        await bitmapImage.SetSourceAsync(randomAccessStream);
                    }
                    
                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error converting bitmap: {ex.Message}");
                return null;
            }
        }

        private string? GetExecutablePath()
        {
            try
            {
                // Check for WhatsApp (including Microsoft Store version)
                if (ProcessName.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
                    WindowTitle.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase))
                {
                    // Try multiple possible paths for WhatsApp
                    string[] whatsAppPaths = {
                        // Standard desktop app
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsApp", "WhatsApp.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WhatsApp", "WhatsApp.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WhatsApp", "WhatsApp.exe"),
                        
                        // Microsoft Store version
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "WhatsAppDesktop.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "WhatsApp.exe")
                    };
                    
                    foreach (var path in whatsAppPaths)
                    {
                        if (File.Exists(path))
                        {
                            System.Diagnostics.Debug.WriteLine($"Found WhatsApp path: {path}");
                            return path;
                        }
                    }
                    
                    // Check for WhatsApp in WindowsApps folder with wildcard
                    try
                    {
                        string windowsAppsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
                        if (Directory.Exists(windowsAppsDir))
                        {
                            // Search for WhatsApp directories
                            string[] whatsAppDirs = Directory.GetDirectories(windowsAppsDir, "*WhatsApp*");
                            foreach (var dir in whatsAppDirs)
                            {
                                string exePath = Path.Combine(dir, "WhatsApp.exe");
                                if (File.Exists(exePath))
                                {
                                    System.Diagnostics.Debug.WriteLine($"Found WhatsApp in WindowsApps: {exePath}");
                                    return exePath;
                                }
                                
                                exePath = Path.Combine(dir, "WhatsAppDesktop.exe");
                                if (File.Exists(exePath))
                                {
                                    System.Diagnostics.Debug.WriteLine($"Found WhatsAppDesktop in WindowsApps: {exePath}");
                                    return exePath;
                                }
                                
                                // Sometimes the main exe is in a subfolder
                                string[] subdirs = { "app-*", "bin", "app" };
                                foreach (var subPattern in subdirs)
                                {
                                    try
                                    {
                                        var matchingSubdirs = Directory.GetDirectories(dir, subPattern);
                                        foreach (var subdir in matchingSubdirs)
                                        {
                                            exePath = Path.Combine(subdir, "WhatsApp.exe");
                                            if (File.Exists(exePath))
                                            {
                                                System.Diagnostics.Debug.WriteLine($"Found WhatsApp in subdirectory: {exePath}");
                                                return exePath;
                                            }
                                        }
                                    }
                                    catch { /* Ignore directory access errors */ }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error searching for WhatsApp: {ex.Message}");
                    }
                    
                    // If all else fails, look for electron processes running WhatsApp
                    try
                    {
                        var processes = System.Diagnostics.Process.GetProcessesByName("electron");
                        foreach (var process in processes)
                        {
                            try
                            {
                                if (process.MainWindowTitle.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) &&
                                    process.MainModule != null &&
                                    !string.IsNullOrEmpty(process.MainModule.FileName))
                                {
                                    return process.MainModule.FileName;
                                }
                            }
                            catch { /* Skip process if we can't access it */ }
                        }
                    }
                    catch { /* Ignore process access errors */ }
                }
                
                // Check for browsers first (including Arc)
                if (IsBrowser(ProcessName))
                {
                    // Specific Arc browser detection
                    if (ProcessName.Equals("Arc", StringComparison.OrdinalIgnoreCase))
                    {
                        // Common Arc browser paths
                        string[] arcPaths = {
                            // Typical installer location
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Arc", "Arc.exe"),
                            // Newer builds renamed to "Arc Browser"
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Arc Browser", "Arc.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Arc Browser", "Arc Browser.exe"),
                            // Program Files fall-back
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Arc", "Arc.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Arc", "Arc.exe"),
                            // Legacy electron-style directory layout
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arc", "app", "Arc.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arc", "Arc.exe")
                        };
                        
                        foreach (var path in arcPaths)
                        {
                            if (File.Exists(path))
                            {
                                System.Diagnostics.Debug.WriteLine($"Found Arc browser at {path}");
                                return path;
                            }
                        }
                        
                        // Look for executable in user's app data folder
                        string userAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        try
                        {
                            // Search for Arc in the LocalApplicationData folder with pattern matching
                            string[] arcDirs = Directory.GetDirectories(userAppData, "Arc*");
                            foreach (var dir in arcDirs)
                            {
                                string exePath = Path.Combine(dir, "Arc.exe");
                                if (File.Exists(exePath))
                                {
                                    System.Diagnostics.Debug.WriteLine($"Found Arc browser in app data: {exePath}");
                                    return exePath;
                                }
                                
                                // Check subdirectories
                                string[] subDirs = { "app", "bin", "Application" };
                                foreach (var subDir in subDirs)
                                {
                                    exePath = Path.Combine(dir, subDir, "Arc.exe");
                                    if (File.Exists(exePath))
                                    {
                                        System.Diagnostics.Debug.WriteLine($"Found Arc browser in subdirectory: {exePath}");
                                        return exePath;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error searching for Arc browser: {ex.Message}");
                        }
                    }
                    
                    // Other browsers detection
                    Dictionary<string, string[]> browserPaths = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Chrome", new[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe")
                        }},
                        { "Firefox", new[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe")
                        }},
                        { "Microsoft Edge", new[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe")
                        }},
                        { "Opera", new[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Opera", "launcher.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Opera", "launcher.exe")
                        }},
                        { "Brave", new[] {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
                        }}
                    };
                    
                    if (browserPaths.TryGetValue(ProcessName, out string[]? paths))
                    {
                        foreach (var path in paths)
                        {
                            if (File.Exists(path))
                            {
                                System.Diagnostics.Debug.WriteLine($"Found browser path: {path}");
                                return path;
                            }
                        }
                    }
                }
                
                // For special apps that are Java-based games
                if (IsJavaGame(ProcessName))
                {
                    // Look for game-specific launcher in common locations
                    string gameDir = GetGameDirectory(ProcessName);

                    // Try to find executable in the game directory
                    if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
                    {
                        string[] possibleExePaths = {
                            Path.Combine(gameDir, "launcher", $"{ProcessName}.exe"),
                            Path.Combine(gameDir, $"{ProcessName}.exe"),
                            Path.Combine(gameDir, "launcher", "launcher.exe"),
                            Path.Combine(gameDir, "bin", "launcher.exe")
                        };

                        foreach (var path in possibleExePaths)
                        {
                            if (File.Exists(path))
                            {
                                return path;
                            }
                        }
                    }
                    
                    // Try standard game installation directories
                    string[] standardPaths = {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProcessName, $"{ProcessName}.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), ProcessName, $"{ProcessName}.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProcessName, $"{ProcessName}.exe")
                    };
                    
                    foreach (var path in standardPaths)
                    {
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                    
                    // If the game is Minecraft or derivative, try Minecraft-specific paths
                    if (ProcessName.Contains("mine", StringComparison.OrdinalIgnoreCase) || 
                        ProcessName.Contains("craft", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] minecraftPaths = {
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "launcher", "Minecraft.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Minecraft", "MinecraftLauncher.exe"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe")
                        };
                        
                        foreach (var path in minecraftPaths)
                        {
                            if (File.Exists(path))
                            {
                                return path;
                            }
                        }
                    }
                    
                    // If we can't find the game executable, return Java as a fallback
                    string javaPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java", "bin", "javaw.exe");
                    if (File.Exists(javaPath))
                    {
                        return javaPath;
                    }
                }
                
                // First try to find the running process by ID
                if (ProcessId > 0)
                {
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById(ProcessId);
                        if (process != null && process.MainModule != null && !string.IsNullOrEmpty(process.MainModule.FileName))
                        {
                            return process.MainModule.FileName;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Could not get process by ID {ProcessId}: {ex.Message}");
                    }
                }
                
                // Then try by name (use original process name for processes that were renamed)
                string processNameToUse = ProcessName;
                if (IsJavaGame(ProcessName))
                {
                    processNameToUse = "javaw";
                }
                else if (ProcessName == "Eclipse" || 
                         ProcessName == "IntelliJ IDEA" ||
                         ProcessName == "NetBeans")
                {
                    processNameToUse = "javaw";
                }
                else if (ProcessName == "PyCharm" || ProcessName == "Jupyter Notebook")
                {
                    processNameToUse = "python";
                }
                
                try
                {
                    var processes = System.Diagnostics.Process.GetProcessesByName(processNameToUse);
                    foreach (var process in processes)
                    {
                        try
                        {
                            if (process != null && process.MainModule != null)
                            {
                                string fileName = process.MainModule.FileName;
                                if (!string.IsNullOrEmpty(fileName))
                                {
                                    return fileName;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error accessing process module: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not get process by name {processNameToUse}: {ex.Message}");
                }
                
                // If we still don't have a path, try to infer it from common locations
                var extension = ".exe";
                if (!ProcessName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    var processWithExt = ProcessName + extension;
                    
                    // Try various common program directories
                    string[] commonLocations = {
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                    };
                    
                    // Try both with and without subdirectory
                    foreach (var location in commonLocations)
                    {
                        // Check direct path
                        var directPath = Path.Combine(location, processWithExt);
                        if (File.Exists(directPath))
                        {
                            return directPath;
                        }
                        
                        // Check in subdirectory matching the process name
                        var subDirPath = Path.Combine(location, ProcessName, processWithExt);
                        if (File.Exists(subDirPath))
                        {
                            return subDirPath;
                        }
                    }
                    
                    // As a fallback, just use any executable with this name (for icon purposes)
                    return processWithExt;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetExecutablePath: {ex.Message}");
                return null;
            }
        }
        
        private DateTime _startTime;
        public DateTime StartTime
        {
            get => _startTime;
            set
            {
                if (_startTime != value)
                {
                    _startTime = value;
                    NotifyPropertyChanged();
                    NotifyPropertyChanged(nameof(Duration));
                    NotifyPropertyChanged(nameof(FormattedDuration));
                }
            }
        }

        private DateTime? _endTime;
        public DateTime? EndTime
        {
            get => _endTime;
            set
            {
                if (_endTime != value)
                {
                    _endTime = value;
                    NotifyPropertyChanged();
                    NotifyPropertyChanged(nameof(Duration));
                    NotifyPropertyChanged(nameof(FormattedDuration));
                }
            }
        }

        internal TimeSpan _accumulatedDuration = TimeSpan.Zero;
        private DateTime _lastFocusTime;

        public TimeSpan Duration
        {
            get
            {
                var baseDuration = _accumulatedDuration;
                if (IsFocused)
                {
                    var currentTime = DateTime.Now;
                    
                    // Check if this is a stale session from a previous day
                    if (_lastFocusTime.Date < currentTime.Date)
                    {
                        // If the session started on a previous day, cap it at end of that day
                        var endOfDay = _lastFocusTime.Date.AddDays(1).AddTicks(-1);
                        var focusedDuration = endOfDay - _lastFocusTime;
                        baseDuration += focusedDuration;
                        System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] WARNING: Stale session detected for {ProcessName}. Capping duration at end of day.");
                    }
                    else
                    {
                        // Normal calculation for same-day sessions
                        var focusedDuration = currentTime - _lastFocusTime;
                        
                        // Cap single session duration at 8 hours (reasonable maximum for continuous use)
                        if (focusedDuration.TotalHours > 8)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] WARNING: Session duration exceeds 8 hours for {ProcessName}. Capping at 8 hours.");
                            focusedDuration = TimeSpan.FromHours(8);
                        }
                        
                        baseDuration += focusedDuration;
                    }
                    
                    // ENHANCED LOGGING for double counting detection
                    System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] Duration getter for {ProcessName}:");
                    System.Diagnostics.Debug.WriteLine($"  - _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s");
                    System.Diagnostics.Debug.WriteLine($"  - _lastFocusTime: {_lastFocusTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"  - currentTime: {currentTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"  - calculated session: {(currentTime - _lastFocusTime).TotalSeconds:F2}s");
                    System.Diagnostics.Debug.WriteLine($"  - FINAL baseDuration: {baseDuration.TotalSeconds:F2}s");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] Duration getter for {ProcessName} (NOT FOCUSED): {baseDuration.TotalSeconds:F2}s");
                }
                
                // Final safety check - cap total duration at 16 hours (allowing for multiple sessions)
                if (baseDuration.TotalHours > 16)
                {
                    System.Diagnostics.Debug.WriteLine($"[DURATION_LOG] WARNING: Total duration exceeds 16 hours for {ProcessName}. Capping at 16 hours.");
                    baseDuration = TimeSpan.FromHours(16);
                }
                
                return baseDuration;
            }
        }

        public string FormattedDuration
        {
            get
            {
                var duration = Duration;
                var hours = (int)duration.TotalHours;
                var minutes = duration.Minutes;
                var seconds = duration.Seconds;

                if (hours > 0)
                {
                    return $"{hours}h {minutes}m {seconds}s";
                }
                else if (minutes > 0)
                {
                    return $"{minutes}m {seconds}s";
                }
                else
                {
                    return $"{seconds}s";
                }
            }
        }

        public string FormattedStartTime => StartTime.ToString("HH:mm");

        public bool IsFromDate(DateTime date)
        {
            return StartTime.Date == date.Date;
        }

        public bool ShouldTrack => !_ignoredProcesses.Contains(ProcessName.ToLower()) && !IsTemporaryProcess(ProcessName);

        /// <summary>
        /// Checks if a process name appears to be a temporary file or installer
        /// </summary>
        private static bool IsTemporaryProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName))
                return false;

            // Convert to lowercase for comparison
            string lowerName = processName.ToLower();

            // Check for patterns that indicate temporary processes
            return
                // Random hex/numeric names (like "1747797458F8MB2zabRaze...")
                (lowerName.Length > 15 && System.Text.RegularExpressions.Regex.IsMatch(lowerName, @"^[0-9a-f]{8,}")) ||
                
                // Files ending with .tmp
                lowerName.EndsWith(".tmp") ||
                
                // Process names starting with numbers and containing random characters
                (lowerName.Length > 20 && System.Text.RegularExpressions.Regex.IsMatch(lowerName, @"^[0-9]{5,}.*[a-z0-9]{5,}")) ||
                
                // Setup/installer temporary files
                (lowerName.Contains("setup") && lowerName.Contains(".tmp")) ||
                (lowerName.Contains("install") && lowerName.Length > 25);
        }

        public void SetFocus(bool isFocused)
        {
            System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] SetFocus called for {ProcessName}: {IsFocused} -> {isFocused}");
            if (IsFocused != isFocused)
            {
             if (isFocused)
                {
                    _lastFocusTime = DateTime.Now;
                    System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] Focus started for {ProcessName} at {_lastFocusTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] Current _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s");
                }
                else
                {
                    // Accumulate the time spent focused
                    var currentTime = DateTime.Now;
                    var focusedDuration = currentTime - _lastFocusTime;
                    
                    System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] Focus ending for {ProcessName}:");
                    System.Diagnostics.Debug.WriteLine($"  - _lastFocusTime: {_lastFocusTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"  - currentTime: {currentTime:HH:mm:ss.fff}");
                    System.Diagnostics.Debug.WriteLine($"  - calculated focusedDuration: {focusedDuration.TotalSeconds:F2}s");
                    System.Diagnostics.Debug.WriteLine($"  - OLD _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s");
                    
                    // Only accumulate if duration is reasonable (less than 1 day)
                    if (focusedDuration.TotalDays < 1 && focusedDuration.TotalSeconds > 0)
                    {
                        _accumulatedDuration += focusedDuration;
                        System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] NEW _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] WARNING: Ignoring unreasonable focus duration for {ProcessName}: {focusedDuration.TotalDays} days, {focusedDuration.TotalSeconds:F2}s");
                    }
                }
                
                IsFocused = isFocused;
                NotifyPropertyChanged(nameof(IsFocused));
                NotifyPropertyChanged(nameof(Duration));
                NotifyPropertyChanged(nameof(FormattedDuration));
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[FOCUS_LOG] SetFocus for {ProcessName}: No change (already {isFocused})");
            }
        }

        public void MergeWith(AppUsageRecord other)
        {
            if (other.EndTime.HasValue)
            {
                // Add the duration of the other record
                var otherDuration = other.EndTime.Value - other.StartTime;
                _accumulatedDuration += otherDuration;

                // Keep tracking from the latest point
                if (!EndTime.HasValue)
                {
                    StartTime = other.EndTime.Value;
                    _lastFocusTime = StartTime;
                }
                
                NotifyPropertyChanged(nameof(Duration));
                NotifyPropertyChanged(nameof(FormattedDuration));
            }
        }

        public void UpdateDuration()
        {
            if (IsFocused)
            {
                System.Diagnostics.Debug.WriteLine($"Updating duration for {ProcessName} (Focused: {IsFocused})");
                NotifyPropertyChanged(nameof(Duration));
                NotifyPropertyChanged(nameof(FormattedDuration));
            }
        }

        /// <summary>
        /// Explicitly increments the duration of this record by a given interval.
        /// Used for real-time updates in historical views.
        /// </summary>
        /// <param name="interval">The time interval to add.</param>
        public void IncrementDuration(TimeSpan interval)
        {
            var oldAccumulated = _accumulatedDuration;
            var oldLastFocusTime = _lastFocusTime;
            var currentTime = DateTime.Now;
            
            System.Diagnostics.Debug.WriteLine($"[INCREMENT_LOG] IncrementDuration called for {ProcessName}:");
            System.Diagnostics.Debug.WriteLine($"  - IsFocused: {IsFocused}");
            System.Diagnostics.Debug.WriteLine($"  - Interval to add: {interval.TotalSeconds:F2}s");
            System.Diagnostics.Debug.WriteLine($"  - OLD _accumulatedDuration: {oldAccumulated.TotalSeconds:F2}s");
            System.Diagnostics.Debug.WriteLine($"  - OLD _lastFocusTime: {oldLastFocusTime:HH:mm:ss.fff}");
            System.Diagnostics.Debug.WriteLine($"  - Current time: {currentTime:HH:mm:ss.fff}");
            
            if (IsFocused)
            {
                var timeSinceLastFocus = currentTime - oldLastFocusTime;
                System.Diagnostics.Debug.WriteLine($"  - Time since last focus: {timeSinceLastFocus.TotalSeconds:F2}s");
                System.Diagnostics.Debug.WriteLine($"  - *** POTENTIAL DOUBLE COUNT: Adding {interval.TotalSeconds:F2}s but {timeSinceLastFocus.TotalSeconds:F2}s already calculated in Duration getter ***");
            }
            
            // FIX: Only increment if NOT focused, since Duration getter already calculates real-time for focused apps
            if (!IsFocused)
            {
                _accumulatedDuration += interval;
                System.Diagnostics.Debug.WriteLine($"  - NEW _accumulatedDuration: {_accumulatedDuration.TotalSeconds:F2}s (incremented because NOT focused)");
            }
            else
            {
                // When focused, just update _lastFocusTime to current time to sync with the timer
                System.Diagnostics.Debug.WriteLine($"  - SKIPPING increment (app is focused, Duration getter handles real-time calculation)");
            }
            
            // Always update last focus time to keep in sync with timer
            _lastFocusTime = DateTime.Now;
            System.Diagnostics.Debug.WriteLine($"  - NEW _lastFocusTime: {_lastFocusTime:HH:mm:ss.fff}");
            
            NotifyPropertyChanged(nameof(Duration));
            NotifyPropertyChanged(nameof(FormattedDuration));
        }

        public static AppUsageRecord CreateAggregated(string processName, DateTime date)
        {
            // Create a new record for the given process name and date
            var record = new AppUsageRecord
            {
                ProcessName = processName,
                ApplicationName = processName, // Default to process name
                Date = date,
                // Use noon instead of midnight to ensure it's visible on charts
                // This only matters for aggregated views where exact time isn't shown
                StartTime = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0),
                _accumulatedDuration = TimeSpan.Zero
            };
            
            // Initialize icon loading in the background
            record.LoadAppIconIfNeeded();
            
            System.Diagnostics.Debug.WriteLine($"Created aggregated record for {processName} with date {date:yyyy-MM-dd} and start time {record.StartTime}");
            
            return record;
        }

        public void LoadAppIconIfNeeded()
        {
            if (_appIcon == null && !_isLoadingIcon)
            {
                LoadIconAsync();
            }
        }

        private async Task<bool> TryLoadUwpAppIcon()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Trying to load UWP app icon for {ProcessName}");
                
                // Check for ApplicationFrameHost which hosts UWP apps
                if (ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                {
                    // For ApplicationFrameHost, we need to look at the window title which often contains the app name
                    string appName = WindowTitle;
                    if (!string.IsNullOrEmpty(appName))
                    {
                        System.Diagnostics.Debug.WriteLine($"ApplicationFrameHost window title: {appName}");
                        
                        // Try to find a corresponding Store app
                        Dictionary<string, string> uwpLogos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "Calculator", @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_*\Assets\CalculatorAppList.png" },
                            { "Calendar", @"C:\Program Files\WindowsApps\microsoft.windowscommunicationsapps_*\ppleae38af2e007f4358a809ac99a64a67c1\MailCalendarLogo.png" },
                            { "Mail", @"C:\Program Files\WindowsApps\microsoft.windowscommunicationsapps_*\ppleae38af2e007f4358a809ac99a64a67c1\MailCalendarLogo.png" },
                            { "Maps", @"C:\Program Files\WindowsApps\Microsoft.WindowsMaps_*\Assets\tile-sdk.png" },
                            { "Microsoft Store", @"C:\Program Files\WindowsApps\Microsoft.WindowsStore_*\icon.png" },
                            { "Photos", @"C:\Program Files\WindowsApps\Microsoft.Windows.Photos_*\Assets\PhotosAppList.png" },
                            { "Weather", @"C:\Program Files\WindowsApps\Microsoft.BingWeather_*\Assets\ApplicationLogo.png" },
                            { "Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" }
                        };
                        
                        foreach (var app in uwpLogos.Keys)
                        {
                            if (appName.Contains(app, StringComparison.OrdinalIgnoreCase))
                            {
                                string logoPattern = uwpLogos[app];
                                
                                // Handle regular executables
                                if (logoPattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(logoPattern))
                                {
                                    // For regular EXEs, use our normal methods
                                    bool success = await TryLoadIconWithSHGetFileInfo(logoPattern);
                                    if (!success)
                                    {
                                        success = await TryLoadIconWithExtractAssociatedIcon(logoPattern);
                                    }
                                    return success;
                                }
                                
                                // For UWP app assets, find the file using the pattern
                                try 
                                {
                                    string? directory = Path.GetDirectoryName(logoPattern);
                                    string fileName = Path.GetFileName(logoPattern);
                                    
                                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                                    {
                                        var matchingDirs = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
                                        foreach (var dir in matchingDirs)
                                        {
                                            var assetFiles = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories);
                                            if (assetFiles.Length > 0)
                                            {
                                                return await LoadImageFromFile(assetFiles[0]);
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Error searching for UWP asset files: {ex.Message}");
                                }
                            }
                        }
                    }
                    
                    // If we couldn't match the specific UWP app, use a generic UWP app icon
                    return await TryLoadIconFromDll(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll"));
                }
                
                // For other UWP processes, try to find their package folder and app icon
                if (ProcessName.Contains("App") || WindowTitle.Contains("App"))
                {
                    string appsFolder = @"C:\Program Files\WindowsApps";
                    if (Directory.Exists(appsFolder))
                    {
                        // Try to find a matching directory
                        var dirs = Directory.GetDirectories(appsFolder, "*" + ProcessName + "*");
                        if (dirs.Length > 0)
                        {
                            // Look for logo files in common locations
                            string[] possibleLogos = {
                                "Assets\\Logo.png",
                                "Assets\\StoreLogo.png",
                                "Assets\\AppIcon.png",
                                "Assets\\ApplicationIcon.png",
                                "icon.png",
                                "logo.png"
                            };
                            
                            foreach (var dir in dirs)
                            {
                                foreach (var logoPath in possibleLogos)
                                {
                                    string fullPath = Path.Combine(dir, logoPath);
                                    if (File.Exists(fullPath))
                                    {
                                        return await LoadImageFromFile(fullPath);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading UWP app icon for {ProcessName}: {ex.Message}");
            }
            
            return false;
        }
        
        private async Task<bool> LoadImageFromFile(string imagePath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Loading image from file: {imagePath}");
                
                BitmapImage bitmapImage = new BitmapImage();
                using (var fileStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
                {
                    using (var memStream = new MemoryStream())
                    {
                        await fileStream.CopyToAsync(memStream);
                        memStream.Position = 0;
                        
                        using (var randomAccessStream = new InMemoryRandomAccessStream())
                        {
                            using (var writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
                            {
                                var bytes = memStream.ToArray();
                                writer.WriteBytes(bytes);
                                await writer.StoreAsync();
                                await writer.FlushAsync();
                            }
                            
                            randomAccessStream.Seek(0);
                            await bitmapImage.SetSourceAsync(randomAccessStream);
                        }
                    }
                }
                
                if (bitmapImage.PixelWidth > 0)
                {
                    AppIcon = bitmapImage;
                    System.Diagnostics.Debug.WriteLine($"Successfully loaded image from file for {ProcessName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading image from file for {ProcessName}: {ex.Message}");
            }
            
            return false;
        }

        #region Window-handle icon extraction (generic fallback)

        private const int WM_GETICON   = 0x007F;
        private const int ICON_SMALL   = 0;
        private const int ICON_BIG     = 1;
        private const int ICON_SMALL2  = 2;
        private const int GCL_HICON    = -14;
        private const int GCL_HICONSM  = -34;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

#if NET6_0_OR_GREATER
        // GetClassLongPtr is pointer-size dependent. In WinUI 3/.NET 6+ we can map to
        // GetClassLongPtrW which automatically handles 64-bit.
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
        private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);
#else
        [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
        private static extern uint GetClassLongPtr(IntPtr hWnd, int nIndex);
#endif

        private async Task<bool> TryLoadIconFromWindowHandle()
        {
            if (WindowHandle == IntPtr.Zero)
                return false;

            try
            {
                // 1) Ask the window for its small icon
                IntPtr hIcon = SendMessage(WindowHandle, WM_GETICON, ICON_SMALL2, 0);
                if (hIcon == IntPtr.Zero)
                    hIcon = SendMessage(WindowHandle, WM_GETICON, ICON_SMALL, 0);
                if (hIcon == IntPtr.Zero)
                    hIcon = SendMessage(WindowHandle, WM_GETICON, ICON_BIG, 0);

                // 2) If not provided, try the class icon
                if (hIcon == IntPtr.Zero)
                    hIcon = GetClassLongPtr(WindowHandle, GCL_HICONSM);
                if (hIcon == IntPtr.Zero)
                    hIcon = GetClassLongPtr(WindowHandle, GCL_HICON);

                if (hIcon == IntPtr.Zero)
                    return false;

                using (var icon = Icon.FromHandle(hIcon))
                using (var bmp  = icon.ToBitmap())
                {
                    var bmpImg = await ConvertBitmapToBitmapImageAsync(bmp);
                    if (bmpImg != null)
                    {
                        AppIcon = bmpImg;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TryLoadIconFromWindowHandle failed for {ProcessName}: {ex.Message}");
            }

            return false;
        }

        #endregion
    }
} 
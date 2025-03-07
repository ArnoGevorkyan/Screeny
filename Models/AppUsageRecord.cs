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
            "explorer",
            "SearchHost",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "devenv",
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
            "SecurityHealthSystray",
            "SecurityHealthService",
            "Registry",
            "MicrosoftEdgeUpdate",
            "WmiPrvSE",
            "spoolsv",
            "TabTip",
            "TabTip32"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public int Id { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public string WindowTitle { get; set; } = string.Empty;
        public IntPtr WindowHandle { get; set; }
        public bool IsFocused { get; set; }
        
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
                
                // Special handling for renamed processes
                if (ProcessName == "Minecraft")
                {
                    // Try specific Minecraft paths first
                    string[] minecraftPaths = {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "launcher", "Minecraft.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Minecraft", "MinecraftLauncher.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "minecraft.exe")
                    };
                    
                    foreach (var path in minecraftPaths)
                    {
                        if (File.Exists(path))
                        {
                            System.Diagnostics.Debug.WriteLine($"Found Minecraft icon path: {path}");
                            return await TryLoadIconWithSHGetFileInfo(path);
                        }
                    }
                    
                    // Try to load Minecraft icon from assets
                    string minecraftIconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "assets", "objects");
                    if (Directory.Exists(minecraftIconPath))
                    {
                        // Try to find PNG files that might be the icon
                        var pngFiles = Directory.GetFiles(minecraftIconPath, "*.png", SearchOption.AllDirectories);
                        foreach (var file in pngFiles)
                        {
                            if (file.Contains("minecraft") || file.Contains("icon"))
                            {
                                if (await LoadImageFromFile(file))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                    
                    // Use Java as fallback
                    processNameLower = "javaw";
                }
                
                // Map common processes to known system DLLs/EXEs with good icons
                Dictionary<string, string> wellKnownPaths = new Dictionary<string, string>
                {
                    // Browsers
                    { "chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe") },
                    { "firefox", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe") },
                    { "msedge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe") },
                    { "iexplore", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Internet Explorer", "iexplore.exe") },
                    
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
                };
                
                // Check if we have a known path for this process
                if (wellKnownPaths.TryGetValue(processNameLower, out string? knownPath) && File.Exists(knownPath))
                {
                    iconPath = knownPath;
                    System.Diagnostics.Debug.WriteLine($"Found known system path for {ProcessName}: {iconPath}");
                }
                // Handle paths with wildcard components
                else if (processNameLower == "discord")
                {
                    // For Discord, try to find the version-specific folder
                    string discordBaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord");
                    if (Directory.Exists(discordBaseDir))
                    {
                        var versionDirs = Directory.GetDirectories(discordBaseDir, "app-*");
                        if (versionDirs.Length > 0)
                        {
                            string possiblePath = Path.Combine(versionDirs[0], "Discord.exe");
                            if (File.Exists(possiblePath))
                            {
                                iconPath = possiblePath;
                            }
                        }
                    }
                }
                else if (processNameLower == "spotify")
                {
                    // For Spotify from Microsoft Store, try to find the version-specific folder
                    string spotifyBaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
                    if (Directory.Exists(spotifyBaseDir))
                    {
                        var spotifyDirs = Directory.GetDirectories(spotifyBaseDir, "SpotifyAB.SpotifyMusic_*");
                        if (spotifyDirs.Length > 0)
                        {
                            string possiblePath = Path.Combine(spotifyDirs[0], "Spotify.exe");
                            if (File.Exists(possiblePath))
                            {
                                iconPath = possiblePath;
                            }
                        }
                    }
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
                
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in TryGetWellKnownSystemIcon: {ex.Message}");
                return false;
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
            
            return false;
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
                // For special apps that we've renamed for better identification
                if (ProcessName == "Minecraft")
                {
                    // Look for Minecraft launcher in common locations
                    string[] minecraftPaths = {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "launcher", "Minecraft.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Minecraft", "MinecraftLauncher.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "MinecraftLauncher.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime", "jre-x64", "bin", "javaw.exe")
                    };
                    
                    foreach (var path in minecraftPaths)
                    {
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                    
                    // If we can't find Minecraft specifically, look for Java as a fallback
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
                        if (process != null && !string.IsNullOrEmpty(process.MainModule?.FileName))
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
                if (ProcessName == "Minecraft" || 
                    ProcessName == "Eclipse" || 
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
                    if (processes.Length > 0 && !string.IsNullOrEmpty(processes[0].MainModule?.FileName))
                    {
                        return processes[0].MainModule.FileName;
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

        private TimeSpan _accumulatedDuration = TimeSpan.Zero;
        private DateTime _lastFocusTime;

        public TimeSpan Duration
        {
            get
            {
                var baseDuration = _accumulatedDuration;
                if (IsFocused)
                {
                    var currentTime = DateTime.Now;
                    var focusedDuration = currentTime - _lastFocusTime;
                    baseDuration += focusedDuration;
                    System.Diagnostics.Debug.WriteLine($"Duration for {ProcessName}: Accumulated={_accumulatedDuration.TotalSeconds:F1}s, Current={focusedDuration.TotalSeconds:F1}s, Total={baseDuration.TotalSeconds:F1}s");
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

        public bool ShouldTrack => !_ignoredProcesses.Contains(ProcessName.ToLower());

        public void SetFocus(bool isFocused)
        {
            System.Diagnostics.Debug.WriteLine($"Setting focus for {ProcessName} to {isFocused}");
            if (IsFocused != isFocused)
            {
                if (isFocused)
                {
                    _lastFocusTime = DateTime.Now;
                    System.Diagnostics.Debug.WriteLine($"Focus started for {ProcessName} at {_lastFocusTime}");
                }
                else
                {
                    // Accumulate the time spent focused
                    var focusedDuration = DateTime.Now - _lastFocusTime;
                    _accumulatedDuration += focusedDuration;
                    System.Diagnostics.Debug.WriteLine($"Focus ended for {ProcessName}, accumulated {focusedDuration.TotalSeconds:F1}s, total: {_accumulatedDuration.TotalSeconds:F1}s");
                }
                
                IsFocused = isFocused;
                NotifyPropertyChanged(nameof(IsFocused));
                NotifyPropertyChanged(nameof(Duration));
                NotifyPropertyChanged(nameof(FormattedDuration));
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

        public static AppUsageRecord CreateAggregated(string processName, DateTime date)
        {
            return new AppUsageRecord
            {
                ProcessName = processName,
                StartTime = date.Date,
                EndTime = null,
                _accumulatedDuration = TimeSpan.Zero,
                _lastFocusTime = date.Date,
                IsFocused = false
            };
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
                                    string directory = Path.GetDirectoryName(logoPattern);
                                    string fileName = Path.GetFileName(logoPattern);
                                    
                                    if (Directory.Exists(directory))
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
    }
} 
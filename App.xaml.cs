using Microsoft.UI.Xaml;
using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using SQLitePCL;
using System.Diagnostics;
using System.Text;
using WinRT;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using ScreenTimeTracker.Services; // Assuming WindowTrackingService is here
using System.Threading.Tasks;
using System.Threading;
using Windows.ApplicationModel;
using Microsoft.Windows.AppLifecycle;
using Windows.Services.Store;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);

    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_OK = 0x00000000;
    private const int MaxLogBytes = 1024 * 1024;
    private const string LogFileName = "app_log.txt";

    private Window? m_window;
    private WindowTrackingService? _trackingService; // Field to hold the service instance

    // Add static property to hold MainWindow instance
    public static Window? MainWindowInstance { get; private set; }
    
    // Add property to track if app started from Windows startup
    public static bool StartedFromWindowsStartup { get; private set; } = false;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        // Log the start of the application
        WriteToLog("Application starting...");

        // Check if started from Windows startup
        StartedFromWindowsStartup = IsStartedFromWindowsStartup();
        WriteToLog($"Started from Windows startup: {StartedFromWindowsStartup}");

        try
        {
            // Enable DPI awareness
            SetProcessDPIAware();
            WriteToLog("DPI awareness enabled");

            // Initialize SQLite
            try
            {
                // This ensures SQLite native libraries are loaded early
                Batteries_V2.Init();
                WriteToLog("SQLite initialized successfully");
            }
            catch (Exception ex)
            {
                LogException("SQLite initialization error", ex);
                // Don't throw - continue without SQLite if needed
            }

            this.UnhandledException += App_UnhandledException;
            WriteToLog("UnhandledException handler registered");

            InitializeComponent();
            WriteToLog("Application components initialized");

            // Store the UI thread's dispatcher for use throughout the app
            DispatcherHelper.Initialize(DispatcherQueue.GetForCurrentThread());
            WriteToLog("Dispatcher initialized");
        }
        catch (Exception ex)
        {
            ShowErrorAndExit("App initialization failed", "The application failed to initialize properly.", ex);
        }
    }

    /// <summary>
    /// Detects if the application was started from Windows startup
    /// </summary>
    private static bool IsStartedFromWindowsStartup()
    {
        try
        {
            // Check command line arguments
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                // Check for startup-related arguments
                if (args[i].Contains("--startup", StringComparison.OrdinalIgnoreCase) ||
                    args[i].Contains("/startup", StringComparison.OrdinalIgnoreCase) ||
                    args[i].Contains("-startup", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Check if running from the startup folder
            string startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            
            if (!string.IsNullOrEmpty(currentPath) && currentPath.StartsWith(startupPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Fallback: if system just booted (within 20 s), assume startup
            TimeSpan systemUptime = TimeSpan.FromMilliseconds(Environment.TickCount);
            if (systemUptime.TotalSeconds < 20)
            {
                WriteToLog("Recent system boot - likely startup");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            LogException("Error detecting startup mode", ex);
            return false;
        }
    }

    /// <summary>
    /// Checks if this is the first time the app is being run
    /// </summary>
    private static bool IsFirstRun()
    {
        try
        {
            string firstRunMarkerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Screeny",
                ".firstrun"
            );

            if (File.Exists(firstRunMarkerPath))
            {
                WriteToLog("Not first run - marker file exists");
                return false;
            }
            else
            {
                // Create the marker file
                Directory.CreateDirectory(Path.GetDirectoryName(firstRunMarkerPath) ?? "");
                File.WriteAllText(firstRunMarkerPath, DateTime.Now.ToString());
                WriteToLog("First run detected - created marker file");
                return true;
            }
        }
        catch (Exception ex)
        {
            LogException("Error checking first run", ex);
            return false; // Assume not first run on error to avoid showing window unnecessarily
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            // --- Store update check removed for simplicity ---

            WriteToLog("OnLaunched method called - attempting to create the main window");
            
            // Add handler for unobserved task exceptions
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            WriteToLog("UnobservedTaskException handler registered");
            
            // Log runtime information
            WriteToLog($"Running on .NET version: {Environment.Version}");
            WriteToLog($"OS version: {Environment.OSVersion.Version}");
            WriteToLog($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            WriteToLog($"64-bit Process: {Environment.Is64BitProcess}");
            
            m_window = new MainWindow();
            WriteToLog("MainWindow created successfully");
            
            // Store the instance
            MainWindowInstance = m_window;
            
            // Retrieve the tracking service from the MainWindow instance
            if (m_window is MainWindow mainWindow)
            {
                _trackingService = mainWindow.GetTrackingService();
                if (_trackingService == null)
                {
                    WriteToLog("CRITICAL WARNING: Could not retrieve WindowTrackingService...");
                }

                mainWindow.EnsureStartupInitialized();
                WriteToLog("MainWindow startup services initialized");
            }
            else
            {
                 WriteToLog("CRITICAL WARNING: m_window is not a MainWindow instance.");
            }

            // Determine whether to show the window
            bool isFirstRun = IsFirstRun();
            bool showWindow = StartupLaunchPolicy.ShouldShowWindow(StartedFromWindowsStartup, isFirstRun);

            if (showWindow)
            {
                m_window.Activate();
                if (isFirstRun)
                {
                    WriteToLog("MainWindow activated (first run)");
                }
                else
                {
                    WriteToLog("MainWindow activated (normal launch)");
                }
            }
            else
            {
                WriteToLog("MainWindow created but not activated (startup launch - running in background)");
                // Startup services are initialized explicitly before activation, so tracking can run while hidden.
            }
        }
        catch (Exception ex)
        {
            ShowErrorAndExit("Application launch failed", "The application failed to start properly.", ex);
        }
    }

    private static bool LogException(string eventName, Exception ex)
    {
        string entry = $"{eventName}: {ex.GetType().Name} (0x{ex.HResult:X8})";
        if (ex.InnerException is Exception cause)
            entry += $"; caused by {cause.GetType().Name} (0x{cause.HResult:X8})";
        return WriteToLog(entry);
    }

    private static string LogStatus(bool logged) =>
        logged ? $"See {LogFileName} in local app data for details." : "No diagnostic entry was saved.";

    private static void ShowErrorAndExit(string eventName, string message, Exception ex)
    {
        bool logged = LogException(eventName, ex);
        string detailedMessage = $"{message}\n\nError code: 0x{ex.HResult:X8}\n\n{LogStatus(logged)}";
        MessageBox(IntPtr.Zero, detailedMessage, "Critical Application Error", MB_OK | MB_ICONERROR);
        Environment.Exit(1);
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        bool logged = LogException("Unhandled UI exception", e.Exception);
        e.Handled = true;
        MessageBox(IntPtr.Zero, $"A critical error occurred.\n\n{LogStatus(logged)}", "Application Error",
            MB_OK | MB_ICONERROR);
    }

    internal static bool WriteToLog(string message)
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
                return false;

            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n";
            Debug.WriteLine(logEntry);
            byte[] entry = Encoding.UTF8.GetBytes(logEntry);

            string logFolder = Path.Combine(localAppData, "ScreenTimeTracker");
            Directory.CreateDirectory(logFolder);
            using var log = new FileStream(Path.Combine(logFolder, LogFileName), FileMode.Append,
                FileAccess.Write, FileShare.Read);
            if (log.Length > MaxLogBytes - entry.Length)
                return false;

            log.Write(entry);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("Unobserved task exception", e.Exception);

        // Mark as observed to prevent app crash
        e.SetObserved();
    }
}


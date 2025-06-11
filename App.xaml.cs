using Microsoft.UI.Xaml;
using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using SQLitePCL;
using System.Diagnostics;
using System.Text;
using System.Reflection;
using WinRT;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
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
                WriteToLog($"SQLite initialization error: {ex.Message}\n{ex.StackTrace}");
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
            WriteToLog($"CRITICAL ERROR during App constructor: {ex.Message}\n{ex.StackTrace}");
            ShowErrorAndExit("The application failed to initialize properly.", ex);
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
            WriteToLog($"Command line arguments count: {args.Length}");
            
            for (int i = 0; i < args.Length; i++)
            {
                WriteToLog($"Arg[{i}]: {args[i]}");
                
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

            // Check if started shortly after system boot (less than 2 minutes)
            TimeSpan systemUptime = TimeSpan.FromMilliseconds(Environment.TickCount);
            if (systemUptime.TotalMinutes < 2)
            {
                WriteToLog($"System uptime: {systemUptime.TotalMinutes:F1} minutes - likely startup");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            WriteToLog($"Error detecting startup mode: {ex.Message}");
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
            WriteToLog($"Error checking first run: {ex.Message}");
            return false; // Assume not first run on error to avoid showing window unnecessarily
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            // --- Automatic Microsoft Store update check ---
            try
            {
                // Skip update logic when running a side-loaded/dev build
                if (!Windows.ApplicationModel.Package.Current.IsDevelopmentMode)
                {
                    bool updateInstalled = await Services.StoreUpdateService.CheckAndApplyUpdatesAsync();

                    if (updateInstalled)
                    {
                        WriteToLog("A newer version was installed. Restarting application …");
                        Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
                        return; // Terminate further launch actions; the process will exit shortly
                    }
                }
            }
            catch (Exception updEx)
            {
                WriteToLog($"Store update check failed: {updEx.Message}");
                // Continue normal launch so the app is still usable
            }

            WriteToLog("OnLaunched method called - attempting to create the main window");
            
            // Add handler for unobserved task exceptions
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            WriteToLog("UnobservedTaskException handler registered");
            
            // Log runtime information
            WriteToLog($"Running on .NET version: {Environment.Version}");
            WriteToLog($"OS: {Environment.OSVersion}");
            WriteToLog($"Machine Name: {Environment.MachineName}");
            WriteToLog($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            WriteToLog($"64-bit Process: {Environment.Is64BitProcess}");
            WriteToLog($"Current Directory: {Environment.CurrentDirectory}");
            WriteToLog($"App Directory: {AppContext.BaseDirectory}");
            
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
            }
            else
            {
                 WriteToLog("CRITICAL WARNING: m_window is not a MainWindow instance.");
            }

            // Determine whether to show the window
            bool isFirstRun = IsFirstRun();
            bool showWindow = !StartedFromWindowsStartup || isFirstRun;

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
                // The window will remain hidden, and the tray icon will be available
                // The tracking service will still start automatically in MainWindow_Loaded
            }
        }
        catch (Exception ex)
        {
            WriteToLog($"FATAL ERROR: Failed to launch application: {ex.Message}");
            WriteToLog($"Stack trace: {ex.StackTrace}");
            
            // Try to get inner exception details
            if (ex.InnerException != null)
            {
                WriteToLog($"Inner exception: {ex.InnerException.Message}");
                WriteToLog($"Inner stack trace: {ex.InnerException.StackTrace}");
            }
            
            // Try to get more detailed debugging information
            try
            {
                var exceptionDetails = new StringBuilder();
                BuildExceptionDetails(ex, exceptionDetails);
                WriteToLog($"Detailed exception information:\n{exceptionDetails}");
            }
            catch (Exception logEx)
            {
                WriteToLog($"Failed to log detailed exception: {logEx.Message}");
            }
            
            ShowErrorAndExit("The application failed to start properly.", ex);
        }
    }

    private void BuildExceptionDetails(Exception ex, StringBuilder details, int level = 0)
    {
        string indent = new string(' ', level * 2);
        
        details.AppendLine($"{indent}Exception: {ex.GetType().FullName}");
        details.AppendLine($"{indent}Message: {ex.Message}");
        details.AppendLine($"{indent}Source: {ex.Source}");
        
        // Get additional properties using reflection
        try
        {
            var props = ex.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (prop.Name != "InnerException" && prop.Name != "StackTrace" && 
                    prop.Name != "Message" && prop.Name != "Source")
                {
                    try
                    {
                        var value = prop.GetValue(ex);
                        if (value != null)
                        {
                            details.AppendLine($"{indent}{prop.Name}: {value}");
                        }
                    }
                    catch { /* Ignore property read errors */ }
                }
            }
        }
        catch { /* Ignore reflection errors */ }
        
        details.AppendLine($"{indent}Stack trace:");
        details.AppendLine($"{indent}{ex.StackTrace}");
        
        if (ex.InnerException != null)
        {
            details.AppendLine($"{indent}Inner exception:");
            BuildExceptionDetails(ex.InnerException, details, level + 1);
        }
    }

    private static void ShowErrorAndExit(string message, Exception ex, bool exit = true)
    {
        string detailedMessage = $"{message}\n\nError: {ex.Message}\n\nSee Screeny_ErrorLog.txt for details.";
        WriteToLog($"ShowErrorAndExit: {detailedMessage}"); // Log the error message shown to user
        LogExceptionToFile(ex); // Ensure the exception is logged
        MessageBox(IntPtr.Zero, detailedMessage, "Critical Application Error", MB_OK | MB_ICONERROR);
        if (exit)
        {
            Environment.Exit(1); // Завершаем приложение
        }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteToLog($"CRITICAL UNHANDLED EXCEPTION: {e.Exception}");
        // Логируем ошибку перед падением
        LogExceptionToFile(e.Exception); // Используем отдельный метод для записи в файл
        e.Handled = true; // Помечаем как обработанное, чтобы приложение НЕ падало сразу (для теста)

        // Показываем сообщение пользователю (можно улучшить диалог)
        ShowErrorDialog("An critical error occurred. Please check the log file.");
    }

    // Дополнительный обработчик на случай ошибок в XAML потоках (если понадобится)
    // private void Current_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    // {
    //    WriteToLog($"CRITICAL XAML UNHANDLED EXCEPTION: {e.Exception}");
    //    LogExceptionToFile(e.Exception);
    //    e.Handled = true;
    //    ShowErrorDialog("An critical UI error occurred. Please check the log file.");
    // }

    // Метод для записи ошибки в файл
    private static void LogExceptionToFile(Exception ex)
    {
        try
        {
            string logPath = System.IO.Path.Combine(
                System.AppContext.BaseDirectory, // Папка рядом с Screeny.exe
                "Screeny_ErrorLog.txt");

            string errorMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {ex.GetType().Name}\n" +
                                  $"Message: {ex.Message}\n" +
                                  $"Stack Trace:\n{ex.StackTrace}\n\n";

            // Добавляем информацию о внутреннем исключении, если оно есть
            Exception? currentEx = ex.InnerException;
            int level = 1;
            while (currentEx != null)
            {
                errorMessage += $"--- Inner Exception (Level {level}) ---\n" +
                                $"Type: {currentEx.GetType().Name}\n" +
                                $"Message: {currentEx.Message}\n" +
                                $"Stack Trace:\n{currentEx.StackTrace}\n\n";
                currentEx = currentEx.InnerException;
                level++;
            }

            System.IO.File.AppendAllText(logPath, errorMessage);
        }
        catch (Exception logEx)
        {
            // Ошибка при записи лога - выводим в Debug
            System.Diagnostics.Debug.WriteLine($"Failed to log exception to file: {logEx}");
            System.Diagnostics.Debug.WriteLine($"Original exception: {ex}");
            // Попробуем показать сообщение об ошибке хотя бы в MessageBox
            ShowErrorAndExit("Failed to write error log.", logEx, false);
        }
    }

    // Упрощенный метод для показа ошибки пользователю
    private static void ShowErrorDialog(string message)
    {
       MessageBox(IntPtr.Zero, message, "Application Error", MB_OK | MB_ICONERROR);
    }

    private static void WriteToLog(string message)
    {
        try
        {
            // Use System.IO.Path explicitly if needed, but removing Shapes should fix it.
            string logFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenTimeTracker");

            if (!Directory.Exists(logFolder))
            {
                Directory.CreateDirectory(logFolder);
            }

            string logFile = System.IO.Path.Combine(logFolder, "app_log.txt");
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logEntry = $"[{timestamp}] {message}\n";

            // Append to log file
            File.AppendAllText(logFile, logEntry);
            
            // Also write to debug output
            Debug.WriteLine(logEntry);
        }
        catch (Exception ex)
        {
            // If logging itself fails, we can only write to debug output
            Debug.WriteLine($"LOGGING ERROR: {ex.Message}");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteToLog($"CRITICAL UNOBSERVED TASK EXCEPTION: {e.Exception}");
        LogExceptionToFile(e.Exception);
        
        // Mark as observed to prevent app crash
        e.SetObserved();
        
        // Log each inner exception
        if (e.Exception.InnerExceptions != null)
        {
            foreach (var innerEx in e.Exception.InnerExceptions)
            {
                WriteToLog($"Inner exception: {innerEx.Message}");
                WriteToLog($"Stack trace: {innerEx.StackTrace}");
            }
        }
    }
}



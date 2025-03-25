using Microsoft.UI.Xaml;
using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using System.IO;
using SQLitePCL;
using System.Diagnostics;
using System.Text;
using System.Reflection;
using WinRT;

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

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        // Log the start of the application
        WriteToLog("Application starting...");

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
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            WriteToLog("OnLaunched method called - attempting to create the main window");
            
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
            
            m_window.Activate();
            WriteToLog("MainWindow activated");
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

    private void ShowErrorAndExit(string message, Exception ex)
    {
        string errorMessage = $"{message}\n\nError: {ex.Message}";
        
        // Try to use native MessageBox as a fallback
        MessageBox(IntPtr.Zero, errorMessage, "Application Error", MB_ICONERROR | MB_OK);
        
        // Log and exit
        WriteToLog("Showing error message box and exiting application");
        Exit();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        
        // Log the exception
        WriteToLog($"UNHANDLED EXCEPTION: {e.Exception.GetType().Name}");
        WriteToLog($"Message: {e.Exception.Message}");
        WriteToLog($"Stack trace: {e.Exception.StackTrace}");
        
        if (e.Exception.InnerException != null)
        {
            WriteToLog($"Inner exception: {e.Exception.InnerException.Message}");
            WriteToLog($"Inner stack trace: {e.Exception.InnerException.StackTrace}");
        }
        
        // Show error message
        ShowErrorAndExit("An unhandled exception occurred.", e.Exception);
    }

    private void WriteToLog(string message)
    {
        try
        {
            string logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenTimeTracker");

            if (!Directory.Exists(logFolder))
            {
                Directory.CreateDirectory(logFolder);
            }

            string logFile = Path.Combine(logFolder, "app_log.txt");
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
}


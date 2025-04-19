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
}


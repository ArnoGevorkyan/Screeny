using Microsoft.UI.Xaml;
using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using System.IO;
using SQLitePCL;

namespace ScreenTimeTracker;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private Window? m_window;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        // Enable DPI awareness
        SetProcessDPIAware();

        // Initialize SQLite
        try
        {
            // This ensures SQLite native libraries are loaded early
            Batteries_V2.Init();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SQLite initialization error: {ex.Message}");
        }

        this.UnhandledException += App_UnhandledException;

        InitializeComponent();

        // Store the UI thread's dispatcher for use throughout the app
        DispatcherHelper.Initialize(DispatcherQueue.GetForCurrentThread());
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            m_window = new MainWindow();
            m_window.Activate();
        }
        catch (Exception ex)
        {
            LogStartupError(ex);
            ShowErrorDialog(ex);
        }
    }

    private async void ShowErrorDialog(Exception ex)
    {
        ContentDialog errorDialog = new ContentDialog
        {
            Title = "Application Error",
            Content = $"The application failed to start: {ex.Message}\n\nPlease check the log file for more details.",
            CloseButtonText = "Close"
        };

        // Use a new window to show the error since the main window may not have been created
        Window errorWindow = new Window();
        errorWindow.Content = new Grid();
        errorDialog.XamlRoot = errorWindow.Content.XamlRoot;

        errorWindow.Activate();
        await errorDialog.ShowAsync();

        // Close the app
        this.Exit();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogStartupError(e.Exception);
        ShowErrorDialog(e.Exception);
    }

    private void LogStartupError(Exception ex)
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

            string logFile = Path.Combine(logFolder, "error_log.txt");
            string logEntry = $"[{DateTime.Now}] ERROR: {ex.Message}\n{ex.StackTrace}\n\n";

            // Append to log file
            File.AppendAllText(logFile, logEntry);
        }
        catch
        {
            // If logging itself fails, we can't do much
        }
    }
}


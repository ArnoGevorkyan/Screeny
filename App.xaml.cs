using Microsoft.UI.Xaml;
using System;
using Microsoft.UI.Dispatching;

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
        m_window = new MainWindow();
        m_window.Activate();
    }
}


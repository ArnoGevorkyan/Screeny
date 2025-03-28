using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Helper class for controlling window behavior and appearance
    /// </summary>
    public class WindowControlHelper
    {
        // Win32 API constants and imports
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int IMAGE_ICON = 1;
        private const int LR_LOADFROMFILE = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, int uType, int cxDesired, int cyDesired, uint fuLoad);

        private readonly Window _window;
        private readonly AppWindow _appWindow;
        private readonly OverlappedPresenter? _presenter;
        private readonly IntPtr _windowHandle;
        private bool _isMaximized = false;

        /// <summary>
        /// Initializes a new instance of the WindowControlHelper class
        /// </summary>
        /// <param name="window">The window instance to control</param>
        public WindowControlHelper(Window window)
        {
            _window = window;
            
            // Get the window handle and AppWindow
            _windowHandle = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _presenter = _appWindow.Presenter as OverlappedPresenter;
        }

        /// <summary>
        /// Sets up the window with the proper size, icon, and other properties
        /// </summary>
        /// <param name="initialWidth">Initial window width</param>
        /// <param name="initialHeight">Initial window height</param>
        /// <param name="title">Window title</param>
        public void SetUpWindow(double initialWidth = 1000, double initialHeight = 600, string title = "Screeny")
        {
            try
            {
                if (_appWindow != null)
                {
                    // Set the window title
                    _appWindow.Title = title;
                    
                    // Set up title bar
                    _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                    _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                    _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                    // Set the window icon
                    SetWindowIcon();

                    // Set up presenter
                    if (_presenter != null)
                    {
                        _presenter.IsResizable = true;
                        _presenter.IsMaximizable = true;
                        _presenter.IsMinimizable = true;
                    }

                    // Set default size
                    try
                    {
                        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
                        var scale = GetScaleAdjustment();
                        _appWindow.Resize(new SizeInt32 { Width = (int)(initialWidth * scale), Height = (int)(initialHeight * scale) });
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

        /// <summary>
        /// Sets the window icon from the assets folder
        /// </summary>
        private void SetWindowIcon()
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
            if (File.Exists(iconPath))
            {
                try
                {
                    // For ICON_SMALL (16x16), explicitly request a small size to improve scaling quality
                    IntPtr smallIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                    SendMessage(_windowHandle, WM_SETICON, ICON_SMALL, smallIcon);
                    
                    // For ICON_BIG, let Windows decide the best size based on DPI settings
                    IntPtr bigIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                    SendMessage(_windowHandle, WM_SETICON, ICON_BIG, bigIcon);
                }
                catch (Exception ex)
                {
                    // Non-critical failure - continue without icon
                    System.Diagnostics.Debug.WriteLine($"Failed to set window icon: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Minimizes the window
        /// </summary>
        public void MinimizeWindow()
        {
            if (_presenter != null)
            {
                _presenter.Minimize();
            }
        }

        /// <summary>
        /// Maximizes the window if it's not maximized, or restores it if it is
        /// </summary>
        public void MaximizeOrRestoreWindow()
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

        /// <summary>
        /// Closes the window
        /// </summary>
        public void CloseWindow()
        {
            _window.Close();
        }

        /// <summary>
        /// Gets a scale adjustment factor based on the system DPI
        /// </summary>
        /// <returns>Scaling factor for window dimensions</returns>
        private double GetScaleAdjustment()
        {
            // Get the system DPI
            try
            {
                return _window.Content?.XamlRoot?.RasterizationScale ?? 1.0;
            }
            catch
            {
                return 1.0; // Default to no scaling if we can't get the DPI
            }
        }
    }
} 
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using WinRT.Interop; // For WindowNative

namespace ScreenTimeTracker.Helpers
{
    public class TrayIconHelper : IDisposable
    {
        // Window message constants
        private const uint WM_USER = 0x0400;
        private const uint WM_TRAYICON_MSG = WM_USER + 1; // Custom message identifier
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONUP = 0x0205;

        // Shell_NotifyIcon constants
        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;

        // Menu constants
        private const uint MF_STRING = 0x00000000;
        private const uint MF_POPUP = 0x00000010;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RETURNCMD = 0x0100;

        // Menu command IDs
        private const uint IDM_SHOW   = 1001;
        private const uint IDM_EXIT   = 1002;
        private const uint IDM_RESET  = 1003; // delete all data

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            // Additional fields for balloon notifications (optional)
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion; // Used for balloon timeout or version
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
        }

        // P/Invoke declarations
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, [In] ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, string lpIconName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x0010;
        private const uint LR_DEFAULTSIZE = 0x0040;
        private const uint LR_SHARED = 0x8000;


        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        private IntPtr _hWnd; // Main window handle
        private IntPtr _hIcon; // Handle for the loaded icon
        private IntPtr _hContextMenu; // Handle for the context menu
        private bool _isIconAdded = false;
        private NOTIFYICONDATA _notifyIconData;

        public event EventHandler? ShowClicked;
        public event EventHandler? ExitClicked;
        public event EventHandler? ResetClicked; // raised when user picks "Delete all data…"

        public TrayIconHelper(IntPtr ownerHwnd)
        {
            _hWnd = ownerHwnd;
            if (_hWnd == IntPtr.Zero)
            {
                throw new ArgumentException("Owner window handle cannot be zero.", nameof(ownerHwnd));
            }
            LoadIconFromFile(); // Load the icon during construction
            CreateContextMenu();
        }

        private void LoadIconFromFile()
        {
            try
            {
                // Construct the absolute path to the icon file
                 string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "screeny icon.ico");

                 if (!System.IO.File.Exists(iconPath))
                 {
                     _hIcon = IntPtr.Zero;
                     return;
                 }

                // Load the icon using LoadImage for file loading support
                _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE | LR_SHARED);

                if (_hIcon == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"ERROR: Failed to load tray icon. Error code: {error}");
                    // Optionally, try loading a default system icon as fallback
                }
            }
            catch (Exception ex)
            {
                 Debug.WriteLine($"EXCEPTION loading icon: {ex.Message}");
                 _hIcon = IntPtr.Zero;
            }
        }


        public void AddIcon(string tip = "Screeny")
        {
            if (_hIcon == IntPtr.Zero)
            {
                Debug.WriteLine("Cannot add tray icon: Icon handle is null.");
                return;
            }
             if (_hWnd == IntPtr.Zero)
             {
                 Debug.WriteLine("Cannot add tray icon: Window handle is null.");
                 return;
             }


            _notifyIconData = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hWnd,
                uID = 1, // Unique ID for the icon
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON_MSG,
                hIcon = _hIcon,
                szTip = tip
            };

            if (Shell_NotifyIcon(NIM_ADD, ref _notifyIconData))
            {
                _isIconAdded = true;
                 Debug.WriteLine("Tray icon added successfully.");
            }
            else
            {
                 int error = Marshal.GetLastWin32Error();
                 Debug.WriteLine($"Failed to add tray icon. Error code: {error}");
            }
        }

        public void RemoveIcon()
        {
            if (_isIconAdded)
            {
                 if (Shell_NotifyIcon(NIM_DELETE, ref _notifyIconData))
                 {
                     _isIconAdded = false;
                     Debug.WriteLine("Tray icon removed successfully.");
                 }
                 else
                 {
                     int error = Marshal.GetLastWin32Error();
                     Debug.WriteLine($"Failed to remove tray icon. Error code: {error}");
                 }
            }
        }

        private void CreateContextMenu()
        {
            _hContextMenu = CreatePopupMenu();
            if (_hContextMenu == IntPtr.Zero)
            {
                Debug.WriteLine("Failed to create context menu.");
                return;
            }
            AppendMenu(_hContextMenu, MF_STRING, IDM_SHOW,  "Show");
            AppendMenu(_hContextMenu, MF_SEPARATOR, 0,        string.Empty);
            AppendMenu(_hContextMenu, MF_STRING, IDM_RESET, "Delete all data…");
            AppendMenu(_hContextMenu, MF_SEPARATOR, 0,        string.Empty);
            AppendMenu(_hContextMenu, MF_STRING, IDM_EXIT,  "Exit");
             Debug.WriteLine("Context menu created.");
        }

        private void ShowContextMenu()
        {
            if (_hContextMenu == IntPtr.Zero) return;

            GetCursorPos(out POINT pt);
            uint command = TrackPopupMenu(_hContextMenu, TPM_LEFTALIGN | TPM_RETURNCMD, pt.X, pt.Y, 0, _hWnd, IntPtr.Zero);

            if (command == IDM_SHOW)
            {
                ShowClicked?.Invoke(this, EventArgs.Empty);
            }
            else if (command == IDM_RESET)
            {
                ResetClicked?.Invoke(this, EventArgs.Empty);
            }
            else if (command == IDM_EXIT)
            {
                ExitClicked?.Invoke(this, EventArgs.Empty);
            }
        }

        // This method needs to be called from the main window's message loop (WndProc)
        public void HandleWindowMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAYICON_MSG && (uint)lParam == WM_RBUTTONUP)
            {
                Debug.WriteLine("Tray icon right-clicked.");
                ShowContextMenu();
            }
            else if (msg == WM_TRAYICON_MSG && (uint)lParam == WM_LBUTTONUP)
            {
                 Debug.WriteLine("Tray icon left-clicked.");
                 // Typically, left-click shows the main window
                 ShowClicked?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                RemoveIcon(); // Remove from tray first
                if (_hContextMenu != IntPtr.Zero)
                {
                    DestroyMenu(_hContextMenu);
                    _hContextMenu = IntPtr.Zero;
                     Debug.WriteLine("Context menu destroyed.");
                }
                // Note: Do NOT call DestroyIcon on shared icons loaded with LR_SHARED
                // if (_hIcon != IntPtr.Zero)
                // {
                //    DestroyIcon(_hIcon);
                //    _hIcon = IntPtr.Zero;
                //    Debug.WriteLine("Icon handle destroyed.");
                // }
            }
        }
    }
} 
using System;
using System.Runtime.InteropServices;

public static class AuditNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MenuBarInfo
    {
        public int Size;
        public Rect Bounds;
        public IntPtr Menu;
        public IntPtr MenuWindow;
        public int FocusBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetMenuBarInfo(IntPtr window, int objectId, int item, ref MenuBarInfo info);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint item, System.Text.StringBuilder name, int capacity, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    public static bool ExitMenuBounds(IntPtr popup, out Rect bounds)
    {
        bounds = new Rect();
        var info = new MenuBarInfo { Size = Marshal.SizeOf(typeof(MenuBarInfo)) };
        if (!GetMenuBarInfo(popup, -4, 0, ref info)) return false;
        int count = GetMenuItemCount(info.Menu);
        for (int i = 0; i < count; i++)
        {
            if (GetMenuItemID(info.Menu, i) != 1002) continue;
            var title = new System.Text.StringBuilder(64);
            if (GetMenuString(info.Menu, (uint)i, title, title.Capacity, 0x400) <= 0 ||
                title.ToString() != "Exit" ||
                !GetMenuItemRect(IntPtr.Zero, info.Menu, (uint)i, out bounds)) return false;
            var point = new Point { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
            return WindowFromPoint(point) == popup;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconKey { public uint Size; public IntPtr Owner; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref IconKey key, out Rect rect);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string name);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    public static uint OwnerPid(IntPtr hwnd)
    {
        uint process;
        GetWindowThreadProcessId(hwnd, out process);
        return process;
    }

    public static int IconRect(IntPtr hwnd, out Rect rect)
    {
        var key = new IconKey { Size = (uint)Marshal.SizeOf(typeof(IconKey)), Owner = hwnd, Id = 1 };
        return Shell_NotifyIconGetRect(ref key, out rect);
    }

    public static void Mouse(bool right)
    {
        mouse_event(right ? 8u : 2u, 0, 0, 0, UIntPtr.Zero);
        mouse_event(right ? 16u : 4u, 0, 0, 0, UIntPtr.Zero);
    }

    public static void Keys(byte key, byte modifier)
    {
        if (modifier != 0) keybd_event(modifier, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 2, UIntPtr.Zero);
        if (modifier != 0) keybd_event(modifier, 0, 2, UIntPtr.Zero);
    }
}

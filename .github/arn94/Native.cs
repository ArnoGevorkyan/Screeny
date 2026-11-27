using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DiagnosticsProbe
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint pid);
    }

    public static int Activate(string aumid, string args)
    {
        var cls = Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c"), true);
        var manager = (IApplicationActivationManager)Activator.CreateInstance(cls);
        try
        {
            uint pid;
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(aumid, args, 0, out pid));
            return (int)pid;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    private delegate bool EnumWindow(IntPtr window, IntPtr unused);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindow callback, IntPtr unused);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int id);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flag);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint msg, UIntPtr wp,
        StringBuilder text, uint flags, uint timeout, out UIntPtr result);

    public static IntPtr Dialog(int owner)
    {
        var handle = IntPtr.Zero;
        EnumWindows((window, unused) =>
        {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid != owner) return true;
            var caption = new StringBuilder(256);
            GetWindowText(window, caption, caption.Capacity);
            var value = caption.ToString();
            if (value != "Critical Application Error" && value != "Application Error") return true;
            handle = window;
            return false;
        }, IntPtr.Zero);
        return handle;
    }

    public static string Text(IntPtr dialog)
    {
        var buffer = new StringBuilder(1024);
        var label = GetDlgItem(dialog, 0xffff);
        UIntPtr result;
        SendMessageTimeout(label, 0x000d, (UIntPtr)buffer.Capacity, buffer, 2, 700, out result);
        return buffer.ToString();
    }

    public static bool Click(IntPtr dialog, int x, int y)
    {
        var point = new Point { X = x, Y = y };
        if (GetAncestor(WindowFromPoint(point), 2) != dialog || !SetCursorPos(x, y)) return false;
        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        return true;
    }
}

using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, StringBuilder name, int size);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr window, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public delegate bool EnumWindowsCallback(IntPtr window, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr param);

    public static IntPtr WindowFor(uint owner)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, unused) => {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            var name = new StringBuilder(256);
            if (pid != owner || GetClassName(window, name, name.Capacity) == 0 ||
                !name.ToString().Contains("WinUI")) return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder name);

    public static uint ForegroundPid()
    {
        uint pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        return pid;
    }

    public static bool RefreshSyntheticInput(uint expectedForeground)
    {
        if (ForegroundPid() != expectedForeground) return false;
        keybd_event(0x87, 0, 0, UIntPtr.Zero);
        keybd_event(0x87, 0, 2, UIntPtr.Zero);
        return true;
    }

    public static string Identity(IntPtr process)
    {
        uint length = 0;
        int status = GetPackageFullName(process, ref length, null);
        if (status == 15700)
            return "unpackaged";
        if (status != 122)
            return "error=" + status;
        var name = new StringBuilder((int)length);
        status = GetPackageFullName(process, ref length, name);
        return status == 0 ? name.ToString() : "error=" + status;
    }
}

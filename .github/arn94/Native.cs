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

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint msg, UIntPtr wp, IntPtr lp);

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

    public static bool Dismiss(IntPtr dialog)
    {
        var ok = GetDlgItem(dialog, 1);
        return ok != IntPtr.Zero && PostMessage(dialog, 0x0111, (UIntPtr)1, ok);
    }
}

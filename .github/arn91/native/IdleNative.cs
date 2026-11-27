using System;
using System.Runtime.InteropServices;

public static class IdleNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint cbSize;
        public uint dwTime;
    }
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInput info);

    public static uint Tick()
    {
        var info = new LastInput { cbSize = (uint)Marshal.SizeOf(typeof(LastInput)) };
        if (!GetLastInputInfo(ref info)) throw new InvalidOperationException("Native last-input clock unavailable");
        return info.dwTime;
    }

    public static uint Seconds()
    {
        return unchecked((uint)Environment.TickCount - Tick()) / 1000;
    }
}

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTimeTracker.Helpers
{
    internal static class Win32Interop
    {
        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint ExtractIconEx(string file, int index, out IntPtr large, IntPtr small, uint count);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        // For Window-handle icon extraction
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr(IntPtr window, int index);

        internal static IntPtr GetWindowIcon(IntPtr window)
        {
            const uint WM_GETICON = 0x007F;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            if (SendMessageTimeout(window, WM_GETICON, (IntPtr)1, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var handle) == IntPtr.Zero)
                return IntPtr.Zero;
            if (handle == IntPtr.Zero)
                SendMessageTimeout(window, WM_GETICON, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out handle);
            if (handle == IntPtr.Zero) handle = GetClassLongPtr(window, -14);
            return handle != IntPtr.Zero ? handle : GetClassLongPtr(window, -34);
        }

        // For QueryFullProcessImageName
        internal const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
    }
} 
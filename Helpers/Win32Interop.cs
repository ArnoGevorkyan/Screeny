using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTimeTracker.Helpers
{
    internal static class Win32Interop
    {
        // For ExtractAssociatedIcon
        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, StringBuilder lpIconPath, out ushort lpiIcon);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        // For SHGetFileInfo
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        internal const uint SHGFI_ICON = 0x100;
        internal const uint SHGFI_SMALLICON = 0x1;
        internal const uint SHGFI_LARGEICON = 0x0;
        internal const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        // For Window-handle icon extraction
        internal const int WM_GETICON = 0x007F;
        internal const int ICON_SMALL = 0;
        internal const int ICON_BIG = 1;
        internal const int ICON_SMALL2 = 2;
        internal const int GCL_HICON = -14;
        internal const int GCL_HICONSM = -34;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
        private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
        private static extern uint GetClassLongPtr32(IntPtr hWnd, int nIndex);

        internal static IntPtr GetClassLongPtrSafe(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, nIndex) : new IntPtr(GetClassLongPtr32(hWnd, nIndex));
        }

        // For IImageList (UWP/Store icons)
        [DllImport("Shell32.dll", EntryPoint = "#727")]
        internal static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IImageList
        {
            [PreserveSig]
            int Draw(IntPtr hdc, int i, int x, int y, int style);

            [PreserveSig]
            int GetIcon(int i, int flag, ref IntPtr picon);
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

        // For UWP Package access
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetPackageFullNameFromToken(IntPtr token, ref uint packageFullNameLength, StringBuilder packageFullName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder path);

        // For process token access (UWP package detection)
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        // ---------- Stock icons ----------
        internal const uint SHGSI_ICON = 0x000000100;
        internal const uint SHGSI_LARGEICON = 0x000000000; // 'Large icon'
        internal const uint SHGSI_SMALLICON = 0x000000001; // 'Small icon'
        internal const uint SIID_APPLICATION = 0x002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szPath;
        }

        [DllImport("Shell32.dll", SetLastError = false)]
        internal static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);
    }
} 
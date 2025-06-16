using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using ScreenTimeTracker.Models;
using System.Linq;

namespace ScreenTimeTracker.Services
{
    /// <summary>
    /// Centralised service responsible for retrieving an app icon for a given <see cref="AppUsageRecord"/>.
    /// All heavy-weight file system probing and Win32 icon extraction will eventually live here.
    /// </summary>
    public interface IIconLoader
    {
        /// <summary>
        /// Attempts to asynchronously resolve a <see cref="BitmapImage"/> for the supplied usage record.
        /// </summary>
        /// <param name="record">The record whose icon should be fetched.</param>
        /// <param name="ct">Optional cancellation token.</param>
        /// <returns>The resolved image, or <c>null</c> when no icon could be located.</returns>
        Task<BitmapImage?> GetIconAsync(AppUsageRecord record, CancellationToken ct = default);
    }

    /// <inheritdoc />
    public sealed class IconLoader : IIconLoader
    {
        // NOTE: Screeny is single-window; a simple singleton keeps plumbing minimal for now.
        public static IconLoader Instance { get; } = new IconLoader();

        // Cache resolved icons to avoid repeated disk I/O and Win32 calls.
        private readonly ConcurrentDictionary<string, BitmapImage?> _iconCache = new();

        private IconLoader() { }

        /// <inheritdoc />
        public async Task<BitmapImage?> GetIconAsync(AppUsageRecord record, CancellationToken ct = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            // Primary cache key – process name only (case-insensitive)
            string cacheKey = record.ProcessName.ToLowerInvariant();
            if (_iconCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
            // Secondary key that includes the window handle (covers edge-cases where two apps share name)
            string handleKey = cacheKey + "|" + record.WindowHandle.ToInt64();
            if (_iconCache.TryGetValue(handleKey, out cached))
            {
                return cached;
            }

            BitmapImage? resolved = null;

            // 1) Window-handle extraction (fast, no disk I/O)
            resolved = await TryLoadIconFromWindowHandleAsync(record, ct);

            // 2) Executable-resource extraction (covers classic Win32 apps)
            if (resolved == null)
            {
                string? exePath = ResolveExecutablePath(record);
                if (!string.IsNullOrEmpty(exePath))
                {
                    // Prefer SHGetFileInfo first (cheaper)
                    resolved = await TryLoadIconWithSHGetFileInfoAsync(exePath, ct);

                    // Fallback to full ExtractAssociatedIcon if still unresolved
                    if (resolved == null)
                    {
                        resolved = await TryLoadIconWithExtractAssociatedIconAsync(exePath, ct);
                    }
                }
            }

            // 3) UWP/MSIX package assets (for Windows Store or packaged apps)
            if (resolved == null)
            {
                resolved = await TryLoadIconFromPackageAsync(record, ct);
            }

            // 4) WindowsApps packaged Win32 directory (for store-installed Win32 apps)
            if (resolved == null)
            {
                resolved = await TryLoadIconFromWindowsAppsAsync(record, ct);
            }

            // NOTE: Simplified pipeline – start-menu (.lnk) fallback removed

            // 4) DLL resource (some games store icons in DLLs)
            // NOTE: Simplified pipeline – DLL resource fallback removed

            // 5) Well-known custom fallbacks (WhatsApp etc.) – placeholder
            // NOTE: Simplified pipeline – custom well-known fallbacks removed

            if (resolved != null)
            {
                _iconCache[cacheKey]  = resolved; // store by name
                _iconCache[handleKey] = resolved; // store by name+handle for future lookups
            }
            return resolved;
        }

        #region  Window-handle icon extraction (first-line attempt)

        private static async Task<BitmapImage?> TryLoadIconFromWindowHandleAsync(AppUsageRecord record, CancellationToken ct)
        {
            if (record.WindowHandle == IntPtr.Zero) return null;

            try
            {
                IntPtr iconHandle = Helpers.Win32Interop.SendMessage(record.WindowHandle, Helpers.Win32Interop.WM_GETICON, (IntPtr)Helpers.Win32Interop.ICON_BIG, IntPtr.Zero);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = Helpers.Win32Interop.SendMessage(record.WindowHandle, Helpers.Win32Interop.WM_GETICON, (IntPtr)Helpers.Win32Interop.ICON_SMALL, IntPtr.Zero);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = Helpers.Win32Interop.SendMessage(record.WindowHandle, Helpers.Win32Interop.WM_GETICON, (IntPtr)Helpers.Win32Interop.ICON_SMALL2, IntPtr.Zero);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = Helpers.Win32Interop.GetClassLongPtrSafe(record.WindowHandle, Helpers.Win32Interop.GCL_HICON);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = Helpers.Win32Interop.GetClassLongPtrSafe(record.WindowHandle, Helpers.Win32Interop.GCL_HICONSM);

                if (iconHandle != IntPtr.Zero)
                {
                    using var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(iconHandle).Clone();
                    var bitmap = icon.ToBitmap();
                    return await ConvertBitmapToBitmapImageAsync(bitmap, ct);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IconLoader – window-handle extraction failed for {record.ProcessName}: {ex.Message}");
            }

            return null;
        }

        private static async Task<BitmapImage?> ConvertBitmapToBitmapImageAsync(System.Drawing.Bitmap bitmap, CancellationToken ct)
        {
            try
            {
                using var memStream = new System.IO.MemoryStream();
                bitmap.Save(memStream, System.Drawing.Imaging.ImageFormat.Png);
                memStream.Position = 0;

                var bmpImage = new BitmapImage();
                using Windows.Storage.Streams.InMemoryRandomAccessStream ras = new();
                using (var writer = new Windows.Storage.Streams.DataWriter(ras.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(memStream.ToArray());
                    await writer.StoreAsync().AsTask(ct);
                    await writer.FlushAsync().AsTask(ct);
                }
                ras.Seek(0);
                await bmpImage.SetSourceAsync(ras);
                return bmpImage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IconLoader – bitmap conversion failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region  Executable-resource extraction helpers

        private static string? ResolveExecutablePath(AppUsageRecord record)
        {
            // 1) Direct process inspection
            if (record.ProcessId != 0)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(record.ProcessId);
                    if (p.MainModule != null && !string.IsNullOrWhiteSpace(p.MainModule.FileName))
                        return p.MainModule.FileName;
                }
                catch { /* ignore */ }
            }

            // Fallback via Win32 QueryFullProcessImageName (limited-information) – more reliable for some sandboxed apps
            try
            {
                var hProcess = Helpers.Win32Interop.OpenProcess(Helpers.Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION, false, record.ProcessId);
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder(1024);
                        int size = sb.Capacity;
                        if (Helpers.Win32Interop.QueryFullProcessImageName(hProcess, 0, sb, ref size))
                        {
                            return sb.ToString();
                        }
                    }
                    finally
                    {
                        Helpers.Win32Interop.CloseHandle(hProcess);
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        private static async Task<BitmapImage?> TryLoadIconWithSHGetFileInfoAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;

            var psfi = new Helpers.Win32Interop.SHFILEINFO();
            uint flags = Helpers.Win32Interop.SHGFI_ICON | Helpers.Win32Interop.SHGFI_LARGEICON | Helpers.Win32Interop.SHGFI_USEFILEATTRIBUTES;
            _ = Helpers.Win32Interop.SHGetFileInfo(path, Helpers.Win32Interop.FILE_ATTRIBUTE_NORMAL, ref psfi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(psfi), flags);

            if (psfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    using var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(psfi.hIcon).Clone();
                    var bitmap = icon.ToBitmap();
                    return await ConvertBitmapToBitmapImageAsync(bitmap, ct);
                }
                catch { /* ignore */ }
                finally
                {
                    Helpers.Win32Interop.DestroyIcon(psfi.hIcon);
                }
            }
            return null;
        }

        private static async Task<BitmapImage?> TryLoadIconWithExtractAssociatedIconAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;

            IntPtr iconPtr = Helpers.Win32Interop.ExtractAssociatedIcon(IntPtr.Zero, new System.Text.StringBuilder(path), out ushort _);
            if (iconPtr != IntPtr.Zero)
            {
                try
                {
                    using var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(iconPtr).Clone();
                    var bitmap = icon.ToBitmap();
                    return await ConvertBitmapToBitmapImageAsync(bitmap, ct);
                }
                catch { /* ignore */ }
                finally
                {
                    Helpers.Win32Interop.DestroyIcon(iconPtr);
                }
            }
            return null;
        }

        #endregion

        #region  WindowsApps / Start-menu / DLL / misc helpers

        private static async Task<BitmapImage?> TryLoadIconFromWindowsAppsAsync(AppUsageRecord record, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(record.ProcessName)) return null;

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string windowsAppsPath = System.IO.Path.Combine(localAppData, "Microsoft", "WindowsApps");

                if (!System.IO.Directory.Exists(windowsAppsPath)) return null;

                var matchingDirs = System.IO.Directory.GetDirectories(windowsAppsPath, $"*{record.ProcessName}*", System.IO.SearchOption.TopDirectoryOnly);
                foreach (var dir in matchingDirs)
                {
                    string exePath = System.IO.Path.Combine(dir, record.ProcessName + ".exe");
                    if (System.IO.File.Exists(exePath))
                    {
                        var bmp = await TryLoadIconWithSHGetFileInfoAsync(exePath, ct) ?? await TryLoadIconWithExtractAssociatedIconAsync(exePath, ct);
                        if (bmp != null) return bmp;
                    }

                    // fallback: any exe/ico/png inside dir
                    foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.*", System.IO.SearchOption.AllDirectories))
                    {
                        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var bmp = await TryLoadIconWithSHGetFileInfoAsync(file, ct) ?? await TryLoadIconWithExtractAssociatedIconAsync(file, ct);
                            if (bmp != null) return bmp;
                        }
                        else if (file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        {
                            var bmp = await LoadImageFromFileAsync(file, ct);
                            if (bmp != null) return bmp;
                        }
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        private static async Task<BitmapImage?> TryLoadIconFromStartMenuAsync(AppUsageRecord record, CancellationToken ct)
        {
            try
            {
                string[] startMenuDirs = {
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                };

                foreach (var dir in startMenuDirs)
                {
                    if (!System.IO.Directory.Exists(dir)) continue;

                    var lnkFiles = System.IO.Directory.GetFiles(dir, "*.lnk", System.IO.SearchOption.AllDirectories);
                    foreach (var lnk in lnkFiles)
                    {
                        if (!System.IO.Path.GetFileNameWithoutExtension(lnk).Contains(record.ProcessName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var bmp = await TryLoadIconWithSHGetFileInfoAsync(lnk, ct);
                        if (bmp != null) return bmp;
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        private static async Task<BitmapImage?> TryLoadIconFromPackageAsync(AppUsageRecord record, CancellationToken ct)
        {
            if (record.ProcessId == 0) return null;

            IntPtr hProcess = Helpers.Win32Interop.OpenProcess(Helpers.Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION, false, record.ProcessId);
            if (hProcess == IntPtr.Zero) return null;

            try
            {
                uint length = 0;
                var tmp = new System.Text.StringBuilder(0);
                int res = Helpers.Win32Interop.GetPackageFullNameFromToken(hProcess, ref length, tmp);
                const int ERROR_INSUFFICIENT_BUFFER = 15700;
                if (res == ERROR_INSUFFICIENT_BUFFER)
                {
                    var pkgFullName = new System.Text.StringBuilder((int)length);
                    if (Helpers.Win32Interop.GetPackageFullNameFromToken(hProcess, ref length, pkgFullName) == 0)
                    {
                        uint pathLen = 0;
                        if (Helpers.Win32Interop.GetPackagePathByFullName(pkgFullName.ToString(), ref pathLen, tmp) == ERROR_INSUFFICIENT_BUFFER)
                        {
                            var pkgPath = new System.Text.StringBuilder((int)pathLen);
                            if (Helpers.Win32Interop.GetPackagePathByFullName(pkgFullName.ToString(), ref pathLen, pkgPath) == 0)
                            {
                                string manifestPath = System.IO.Path.Combine(pkgPath.ToString(), "AppxManifest.xml");
                                if (System.IO.File.Exists(manifestPath))
                                {
                                    // Quick heuristic: first PNG file containing "logo" in its name inside package root.
                                    var png = System.IO.Directory.EnumerateFiles(pkgPath.ToString(), "*logo*.png", System.IO.SearchOption.AllDirectories)
                                                         .FirstOrDefault();
                                    if (png != null)
                                    {
                                        return await LoadImageFromFileAsync(png, ct);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { /* ignore */ }
            finally
            {
                Helpers.Win32Interop.CloseHandle(hProcess);
            }
            return null;
        }

        private static async Task<BitmapImage?> TryLoadIconFromDllAsync(string dllPath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(dllPath) || !System.IO.File.Exists(dllPath)) return null;

            try
            {
                var icon = Helpers.Win32Interop.ExtractAssociatedIcon(IntPtr.Zero, new System.Text.StringBuilder(dllPath), out ushort _);
                if (icon != IntPtr.Zero)
                {
                    using var ic = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(icon).Clone();
                    var bmp = ic.ToBitmap();
                    return await ConvertBitmapToBitmapImageAsync(bmp, ct);
                }
            }
            catch { /* ignore */ }

            return null;
        }

        private static async Task<BitmapImage?> TryGetWellKnownSystemIconAsync(AppUsageRecord record, CancellationToken ct)
        {
            // Special-case WhatsApp (many users) – look in common install paths
            if (!record.ProcessName.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase)) return null;

            string[] paths = {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsApp", "WhatsApp.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WhatsApp", "WhatsApp.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WhatsApp", "WhatsApp.exe")
            };

            foreach (var p in paths)
            {
                var bmp = await TryLoadIconWithSHGetFileInfoAsync(p, ct);
                if (bmp != null) return bmp;
            }

            return null;
        }

        private static async Task<BitmapImage?> LoadImageFromFileAsync(string imagePath, CancellationToken ct)
        {
            try
            {
                using var fileStream = System.IO.File.OpenRead(imagePath);
                using var mem = new System.IO.MemoryStream();
                await fileStream.CopyToAsync(mem, ct);
                mem.Position = 0;

                var bitmapImage = new BitmapImage();
                using Windows.Storage.Streams.InMemoryRandomAccessStream ras = new();
                using (var w = new Windows.Storage.Streams.DataWriter(ras.GetOutputStreamAt(0)))
                {
                    w.WriteBytes(mem.ToArray());
                    await w.StoreAsync().AsTask(ct);
                    await w.FlushAsync().AsTask(ct);
                }
                ras.Seek(0);
                await bitmapImage.SetSourceAsync(ras);
                return bitmapImage.PixelWidth > 0 ? bitmapImage : null;
            }
            catch { return null; }
        }

        #endregion
    }
} 
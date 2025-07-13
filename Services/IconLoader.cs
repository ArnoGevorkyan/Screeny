using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using ScreenTimeTracker.Models;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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

            // 2) Executable-resource extraction via shell API
            if (resolved == null)
            {
                string? exePath = ResolveExecutablePath(record);
                if (!string.IsNullOrEmpty(exePath))
                {
                    resolved = await TryLoadIconWithSHGetFileInfoAsync(exePath, ct);
                }
            }

            // 3) Ultimate fallback - create a simple app icon programmatically
            if (resolved == null)
            {
                resolved = await CreateSimpleAppIconAsync(ct);
            }

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

            // First try without the USEFILEATTRIBUTES flag – this forces SHGetFileInfo to
            // load the actual file and extract its embedded icon (gives correct app icon
            // for most .exe / .lnk files).
            uint flags = Helpers.Win32Interop.SHGFI_ICON | Helpers.Win32Interop.SHGFI_LARGEICON;

            if (Helpers.Win32Interop.SHGetFileInfo(path, 0, ref psfi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(psfi), flags) == IntPtr.Zero || psfi.hIcon == IntPtr.Zero)
            {
                // Fallback – use file attributes only (avoids hitting disk for inaccessible paths)
                flags |= Helpers.Win32Interop.SHGFI_USEFILEATTRIBUTES;
                _ = Helpers.Win32Interop.SHGetFileInfo(path, Helpers.Win32Interop.FILE_ATTRIBUTE_NORMAL, ref psfi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(psfi), flags);
            }

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


        #endregion

        /// <summary>
        /// Creates a simple application icon when all other methods fail
        /// This ensures the UI never shows gear icons
        /// </summary>
        private static async Task<BitmapImage?> CreateSimpleAppIconAsync(CancellationToken ct)
        {
            try
            {
                using var bitmap = new System.Drawing.Bitmap(32, 32);
                using var g = System.Drawing.Graphics.FromImage(bitmap);
                
                // Create a simple, recognizable app icon
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                
                // Light blue background circle
                g.FillEllipse(System.Drawing.Brushes.LightSteelBlue, 2, 2, 28, 28);
                g.DrawEllipse(System.Drawing.Pens.SteelBlue, 2, 2, 28, 28);
                
                // Simple app window representation
                g.FillRectangle(System.Drawing.Brushes.White, 8, 10, 16, 12);
                g.DrawRectangle(System.Drawing.Pens.DarkBlue, 8, 10, 16, 12);
                g.FillRectangle(System.Drawing.Brushes.CornflowerBlue, 8, 10, 16, 3);
                
                return await ConvertBitmapToBitmapImageAsync(bitmap, ct);
            }
            catch
            {
                // Create absolute minimum fallback - single pixel transparent image
                try
                {
                    using var fallbackBitmap = new System.Drawing.Bitmap(1, 1);
                    fallbackBitmap.SetPixel(0, 0, System.Drawing.Color.Transparent);
                    return await ConvertBitmapToBitmapImageAsync(fallbackBitmap, ct);
                }
                catch
                {
                    // Still return null if absolutely everything fails
                    return null;
                }
            }
        }
    }
} 
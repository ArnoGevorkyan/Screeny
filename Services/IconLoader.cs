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

            // 2) UWP/MSIX package assets (for Windows Store or packaged apps)
            if (resolved == null)
            {
                resolved = await TryLoadIconFromPackageAsync(record, ct);
            }

            // 3) Executable-resource extraction (covers classic Win32 apps & fallbacks)
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

            // 4) WindowsApps packaged Win32 directory (for store-installed Win32 apps)
            if (resolved == null)
            {
                resolved = await TryLoadIconFromWindowsAppsAsync(record, ct);
            }

            // 4) Start-menu (.lnk) shortcut lookup – helps Store/UWP apps that register a link.
            if (resolved == null)
            {
                resolved = await TryLoadIconFromStartMenuAsync(record, ct);
            }

            // 5) DLL resource (some games store icons in DLLs)
            if (resolved == null)
            {
                string dllPath = ResolveExecutablePath(record) ?? record.ProcessName;
                resolved = await TryLoadIconFromDllAsync(dllPath, ct);
            }

            // 6) Well-known custom fallbacks (WhatsApp etc.) – placeholder
            if (resolved == null)
            {
                resolved = await TryGetWellKnownSystemIconAsync(record, ct);
            }

            // 7) Absolute fallback – generic application stock icon so UI never shows gear
            if (resolved == null)
            {
                resolved = await TryLoadStockApplicationIconAsync(ct);
            }

            // 8) Ultimate fallback - create a simple app icon programmatically if everything fails
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
                var searchRoots = new List<string>
                {
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")
                };

                foreach (var windowsAppsPath in searchRoots)
                {
                    if (!System.IO.Directory.Exists(windowsAppsPath)) continue;

                    var matchingDirs = System.IO.Directory.GetDirectories(windowsAppsPath, $"*{record.ProcessName}*", System.IO.SearchOption.TopDirectoryOnly);
                    foreach (var dir in matchingDirs)
                    {
                        // 1) Look for high-quality image assets first (avoid stub EXE generic icon)
                        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.ico", System.IO.SearchOption.AllDirectories)
                                                          .Concat(System.IO.Directory.EnumerateFiles(dir, "*.png", System.IO.SearchOption.AllDirectories)))
                        {
                            var bmpImg = await LoadImageFromFileAsync(file, ct);
                            if (bmpImg != null) return bmpImg;
                        }

                        // 2) Then try the expected exe name inside that folder
                        string exePath = System.IO.Path.Combine(dir, record.ProcessName + ".exe");
                        if (System.IO.File.Exists(exePath))
                        {
                            var bmp = await TryLoadIconWithSHGetFileInfoAsync(exePath, ct) ?? await TryLoadIconWithExtractAssociatedIconAsync(exePath, ct);
                            if (bmp != null) return bmp;
                        }

                        // 3) Finally, scan any other executables (fall-back)
                        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.exe", System.IO.SearchOption.AllDirectories))
                        {
                            var bmp = await TryLoadIconWithSHGetFileInfoAsync(file, ct) ?? await TryLoadIconWithExtractAssociatedIconAsync(file, ct);
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

                bool anySuccess = false;
                foreach (var dir in startMenuDirs)
                {
                    if (!System.IO.Directory.Exists(dir)) continue;

                    var lnkFiles = System.IO.Directory.GetFiles(dir, "*.lnk", System.IO.SearchOption.AllDirectories);
                    foreach (var lnk in lnkFiles)
                    {
                        var nameNoExt = System.IO.Path.GetFileNameWithoutExtension(lnk);
                        if (IsStartMenuNameMatch(record.ProcessName, nameNoExt))
                        {
                            var bmp = await TryLoadIconWithSHGetFileInfoAsync(lnk, ct);
                            if (bmp != null) return bmp;
                            anySuccess = true;
                        }
                    }
                }

                // Fallback: first .lnk with a valid icon if none matched by name
                if (!anySuccess)
                {
                    foreach (var dir in startMenuDirs)
                    {
                        var lnkFiles = System.IO.Directory.GetFiles(dir, "*.lnk", System.IO.SearchOption.AllDirectories);
                        foreach (var lnk in lnkFiles)
                        {
                        var bmp = await TryLoadIconWithSHGetFileInfoAsync(lnk, ct);
                        if (bmp != null) return bmp;
                        }
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        private static bool IsStartMenuNameMatch(string processName, string shortcutName)
        {
            // Fast path – exact substring (already case-insensitive)
            if (shortcutName.Contains(processName, StringComparison.OrdinalIgnoreCase))
                return true;
            
            // Normalised comparison: keep only alphanumerics and lower-case
            string Normalize(string s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (var ch in s)
                {
                    if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                }
                return sb.ToString();
            }

            var normProcess = Normalize(processName);
            var normShortcut = Normalize(shortcutName);

            // Two-way containment handles "GitHubDesktop" vs "github desktop"
            return normShortcut.Contains(normProcess) || normProcess.Contains(normShortcut);
        }

        private static async Task<BitmapImage?> TryLoadIconFromPackageAsync(AppUsageRecord record, CancellationToken ct)
        {
            if (record.ProcessId == 0) return null;

            IntPtr hProcess = Helpers.Win32Interop.OpenProcess(Helpers.Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION, false, record.ProcessId);
            if (hProcess == IntPtr.Zero) return null;

            IntPtr hToken = IntPtr.Zero;
            try
            {
                // Obtain an access token for the target process (required by GetPackageFullNameFromToken)
                const uint TOKEN_QUERY = 0x0008;
                if (!Helpers.Win32Interop.OpenProcessToken(hProcess, TOKEN_QUERY, out hToken) || hToken == IntPtr.Zero)
                {
                    return null;
                }

                uint length = 0;
                var tmp = new System.Text.StringBuilder(0);
                int res = Helpers.Win32Interop.GetPackageFullNameFromToken(hToken, ref length, tmp);
                const int ERROR_INSUFFICIENT_BUFFER = 15700;
                if (res == ERROR_INSUFFICIENT_BUFFER)
                {
                    var pkgFullName = new System.Text.StringBuilder((int)length);
                    if (Helpers.Win32Interop.GetPackageFullNameFromToken(hToken, ref length, pkgFullName) == 0)
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
                                    // Parse manifest for explicit logo path (more reliable for Arc/Spotify)
                                    var logo = GetLogoPathFromManifest(manifestPath);
                                    if (!string.IsNullOrEmpty(logo))
                                    {
                                        string logoPath = System.IO.Path.Combine(pkgPath.ToString(), logo.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString()).Replace("\\", System.IO.Path.DirectorySeparatorChar.ToString()));
                                        if (System.IO.File.Exists(logoPath))
                                        {
                                            var img = await LoadImageFromFileAsync(logoPath, ct);
                                            if (img != null) return img;
                                        }
                                    }

                                    // Fallback: any PNG with "logo" in filename
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
                if (hToken != IntPtr.Zero) Helpers.Win32Interop.CloseHandle(hToken);
                Helpers.Win32Interop.CloseHandle(hProcess);
            }
            return null;
        }

        private static async Task<BitmapImage?> TryLoadIconFromDllAsync(string dllPath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(dllPath) || !System.IO.File.Exists(dllPath)) return null;

            IntPtr icon = IntPtr.Zero;
            try
            {
                icon = Helpers.Win32Interop.ExtractAssociatedIcon(IntPtr.Zero, new System.Text.StringBuilder(dllPath), out ushort _);
                if (icon != IntPtr.Zero)
                {
                    using var ic = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(icon).Clone();
                    var bmp = ic.ToBitmap();
                    return await ConvertBitmapToBitmapImageAsync(bmp, ct);
                }
            }
            catch { /* ignore */ }
            finally
            {
                if (icon != IntPtr.Zero)
                    Helpers.Win32Interop.DestroyIcon(icon);
            }

            return null;
        }

        private static Task<BitmapImage?> TryGetWellKnownSystemIconAsync(AppUsageRecord record, CancellationToken ct)
        {
            // Generic fallback - no hardcoded app paths
            return Task.FromResult<BitmapImage?>(null);
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

        static string? GetLogoPathFromManifest(string manifestPath)
        {
            try
            {
                var xdoc = System.Xml.Linq.XDocument.Load(manifestPath);
                // VisualElements live under namespace "http://schemas.microsoft.com/appx/manifest/uap/windows10"
                var visualElements = xdoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
                if (visualElements != null)
                {
                    var logoAttr = visualElements.Attribute("Square44x44Logo") ?? visualElements.Attribute("Logo");
                    return logoAttr?.Value;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        #endregion

        private static async Task<BitmapImage?> TryLoadStockApplicationIconAsync(CancellationToken ct)
        {
            try
            {
                var sii = new Helpers.Win32Interop.SHSTOCKICONINFO();
                sii.cbSize = (uint)Marshal.SizeOf(typeof(Helpers.Win32Interop.SHSTOCKICONINFO));
                int hr = Helpers.Win32Interop.SHGetStockIconInfo(Helpers.Win32Interop.SIID_APPLICATION,
                                                                 Helpers.Win32Interop.SHGSI_ICON | Helpers.Win32Interop.SHGSI_LARGEICON,
                                                                 ref sii);
                if (hr == 0 && sii.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        using var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(sii.hIcon).Clone();
                        var bmp = icon.ToBitmap();
                        return await ConvertBitmapToBitmapImageAsync(bmp, ct);
                    }
                    finally
                    {
                        Helpers.Win32Interop.DestroyIcon(sii.hIcon);
                    }
                }
            }
            catch { /* ignore */ }
            return null;
        }

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
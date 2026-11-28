using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using ScreenTimeTracker.Helpers;
using ScreenTimeTracker.Models;

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
        private readonly ConcurrentDictionary<string, BitmapImage> _iconCache = new();
        private readonly SemaphoreSlim _extractionSlots = new(4);
        private readonly object _processSnapshotLock = new();
        private Dictionary<string, List<int>> _runningProcesses = new(StringComparer.OrdinalIgnoreCase);
        private long _processSnapshotExpires;
        private Task<BitmapImage?>? _fallbackIcon;

        private IconLoader() { }

        /// <inheritdoc />
        public async Task<BitmapImage?> GetIconAsync(AppUsageRecord record, CancellationToken ct = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            // Primary cache key: stable process identity only, case-insensitive.
            string cacheKey = AppIconIdentity.CreateProcessCacheKey(record);
            if (_iconCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var window = record.WindowHandle;
            var processId = record.ProcessId;
            await _extractionSlots.WaitAsync(ct);
            try
            {
                if (_iconCache.TryGetValue(cacheKey, out cached)) return cached;
                using var bitmap = await Task.Run(() => ExtractIcon(window, processId, cacheKey), ct);
                if (bitmap == null) return null;
                var resolved = await ConvertBitmapToBitmapImageAsync(bitmap, ct);
                return resolved == null ? null : _iconCache.GetOrAdd(cacheKey, resolved);
            }
            finally
            {
                _extractionSlots.Release();
            }
        }

        internal Task<BitmapImage?> GetFallbackIconAsync()
            => _fallbackIcon ??= CreateSimpleAppIconAsync(CancellationToken.None);

        private Bitmap? ExtractIcon(IntPtr window, int processId, string processName)
        {
            try
            {
                if (processId != 0 && window != IntPtr.Zero)
                    using (var owner = Process.GetProcessById(processId))
                        if (Win32Interop.GetWindowThreadProcessId(window, out var ownerId) != 0 &&
                            ownerId == processId && ApplicationNameNormalizer.NormalizeProcessName(owner.ProcessName)
                                .Equals(processName, StringComparison.OrdinalIgnoreCase))
                        {
                            var handle = Win32Interop.GetWindowIcon(window);
                            if (handle != IntPtr.Zero)
                                return Icon.FromHandle(handle).ToBitmap();
                        }
            }
            catch (Exception) { }

            var bitmap = TryExtractExecutableIcon(processId, processName);
            if (bitmap != null) return bitmap;
            foreach (var pid in GetMatchingProcesses(processName))
            {
                if (pid == processId) continue;
                bitmap = TryExtractExecutableIcon(pid, processName);
                if (bitmap != null) return bitmap;
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

        private List<int> GetMatchingProcesses(string processName)
        {
            lock (_processSnapshotLock)
            {
                if (Environment.TickCount64 >= _processSnapshotExpires)
                {
                    var snapshot = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var process in Process.GetProcesses())
                    {
                        using (process)
                        {
                            string name;
                            try { name = ApplicationNameNormalizer.NormalizeProcessName(process.ProcessName); }
                            catch (InvalidOperationException) { continue; }
                            if (!snapshot.TryGetValue(name, out var ids)) snapshot[name] = ids = new();
                            ids.Add(process.Id);
                        }
                    }
                    _runningProcesses = snapshot;
                    _processSnapshotExpires = Environment.TickCount64 + 1000;
                }
                return _runningProcesses.TryGetValue(processName, out var matches) ? matches : [];
            }
        }

        private static Bitmap? TryExtractExecutableIcon(int processId, string processName)
        {
            if (processId <= 0) return null;
            var process = Win32Interop.OpenProcess(Win32Interop.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process == IntPtr.Zero) return null;

            try
            {
                var buffer = new StringBuilder(32768);
                int length = buffer.Capacity;
                if (!Win32Interop.QueryFullProcessImageName(process, 0, buffer, ref length)) return null;
                var path = buffer.ToString();
                if (path.Length < 3 || path[1] != ':' || path[2] != '\\' ||
                    new DriveInfo(path[..3]).DriveType == DriveType.Network ||
                    !ApplicationNameNormalizer.NormalizeProcessName(Path.GetFileNameWithoutExtension(path))
                        .Equals(processName, StringComparison.OrdinalIgnoreCase))
                    return null;

                if (Win32Interop.ExtractIconEx(path, 0, out var handle, IntPtr.Zero, 1) != 1 ||
                    handle == IntPtr.Zero)
                    return null;

                try { return Icon.FromHandle(handle).ToBitmap(); }
                finally { Win32Interop.DestroyIcon(handle); }
            }
            catch (Exception) { return null; }
            finally { Win32Interop.CloseHandle(process); }
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

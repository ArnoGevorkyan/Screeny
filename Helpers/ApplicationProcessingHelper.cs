using ScreenTimeTracker.Models;
using System;
using System.Diagnostics;
using System.IO;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Minimal helper that normalises <see cref="AppUsageRecord.ProcessName"/> for display.
    /// All legacy heuristics, browser detection, and fuzzy matching logic have been removed.
    /// </summary>
    public static class ApplicationProcessingHelper
    {
        /// <summary>
        /// Attempts to produce a friendly application name for a record.
        /// 1. If we can read the executable’s <c>ProductName</c> from its file version info, use that.
        /// 2. Otherwise fall back to a title-cased, de-suffixed version of <see cref="AppUsageRecord.ProcessName"/>.
        /// </summary>
        public static void ProcessApplicationRecord(AppUsageRecord record)
        {
            if (record == null) return;
            
            // Try filesystem product name first (best-effort; ignore failures).
            try
            {
                if (record.ProcessId > 0)
                {
                    using var proc = Process.GetProcessById(record.ProcessId);
                    var exe = proc?.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                    {
                        var info = FileVersionInfo.GetVersionInfo(exe);
                        if (!string.IsNullOrWhiteSpace(info.ProductName))
                        {
                            record.ProcessName = info.ProductName.Trim();
                            return;
                        }
                    }
                }
            }
            catch { /* ignored – permissions, 32/64-bit mismatch, etc. */ }
            
            // Fallback: clean & title-case the original process name.
            var baseName = GetBaseAppName(record.ProcessName);
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                record.ProcessName = char.ToUpperInvariant(baseName[0]) + baseName[1..];
            }
        }
        
        /// <summary>
        /// Strips common numeric / architecture suffixes and file extensions from a raw process name.
        /// </summary>
        public static string GetBaseAppName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return processName ?? string.Empty;

            var lower = processName.ToLowerInvariant();

            // Remove file extension if present.
            if (lower.EndsWith(".exe")) lower = lower[..^4];
                        
            // Drop common architecture suffixes.
            string[] suffixes = { "64", "32", "x64", "x86", "_64", "_32" };
            foreach (var s in suffixes)
            {
                if (lower.EndsWith(s))
                {
                    lower = lower[..^s.Length];
                    break;
                }
            }

            // Collapse any remaining whitespace/underscores/dashes.
            lower = lower.Replace("_", " ").Replace("-", " ").Trim();

            return lower;
        }
    }
} 
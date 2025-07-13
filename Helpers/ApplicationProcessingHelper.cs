using ScreenTimeTracker.Models;
using System;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Helper class for processing application records and normalizing names
    /// </summary>
    public static class ApplicationProcessingHelper
    {
        /// <summary>
        /// Process an application record to improve naming
        /// </summary>
        public static void ProcessApplicationRecord(AppUsageRecord record)
        {
            if (record == null) return;
            
            // Use window title first if available and reasonable quality
            if (!string.IsNullOrEmpty(record.WindowTitle) && 
                record.WindowTitle.Length <= 50 && 
                !record.WindowTitle.Contains("?"))
            {
                record.ProcessName = record.WindowTitle;
                return;
            }
            
            // Try to use the executable's ProductName as a friendly label (existing logic)
            try
            {
                if (record.ProcessId > 0)
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(record.ProcessId);
                    var modulePath = proc?.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(modulePath))
                    {
                        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(modulePath);
                        if (!string.IsNullOrWhiteSpace(info.ProductName))
                        {
                            record.ProcessName = info.ProductName;
                        }
                    }
                }
            }
            catch { /* best-effort – ignore failures (permissions, 32/64-bit, etc.) */ }
            
            // Simple cleanup of process name
            record.ProcessName = CleanProcessName(record.ProcessName);
        }
        
        /// <summary>
        /// Simple cleanup of process names - removes obvious suffixes but preserves original names
        /// </summary>
        private static string CleanProcessName(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return processName;

            // Remove common architecture suffixes only
            string cleaned = processName
                .Replace(" (x86)", "")
                .Replace(" (x64)", "")
                .Trim();

            return cleaned;
        }
    }
}
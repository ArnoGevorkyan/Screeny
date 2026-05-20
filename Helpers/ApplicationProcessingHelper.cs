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
            
            var stableProcessName = ApplicationNameNormalizer.NormalizeProcessName(record.ProcessName);

            // Try to use the executable's ProductName as a friendly display label.
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
                            record.ApplicationName = info.ProductName;
                        }
                    }
                }
            }
            catch { /* best-effort – ignore failures (permissions, 32/64-bit, etc.) */ }

            record.ProcessName = stableProcessName;
            if (string.IsNullOrWhiteSpace(record.ApplicationName))
            {
                record.ApplicationName = stableProcessName;
            }
        }
        
    }
}

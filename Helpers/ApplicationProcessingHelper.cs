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
        /// Process an application record to improve naming and categorization
        /// </summary>
        public static void ProcessApplicationRecord(AppUsageRecord record)
        {
            if (record == null) return;
            
            // Try to use the executable's ProductName as a friendly label (generic solution)
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
            
            // Generic normalisation: use base name (lower-case stripped of suffixes) and title-case it for display.
            var baseName = GetBaseAppName(record.ProcessName);
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                // Title-case for nicer display (simple approach – capitalise first letter)
                record.ProcessName = char.ToUpper(baseName[0]) + baseName.Substring(1);
            }
        }
        
        /// <summary>
        /// Gets the base name of an application by removing common suffixes and normalizing
        /// </summary>
        public static string GetBaseAppName(string processName)
        {
            // Extract the base application name (removing numbers, suffixes, etc.)
            if (string.IsNullOrEmpty(processName)) return processName;

            // Remove common process suffixes
            string cleanName = processName.ToLower()
                .Replace("64", "")
                .Replace("32", "")
                .Replace("x86", "")
                .Replace("x64", "")
                .Replace(" (x86)", "")
                .Replace(" (x64)", "");

            // Match common app variations
            if (cleanName.StartsWith("telegram"))
                return "telegram";
            if (cleanName.StartsWith("discord"))
                return "discord";
            if (cleanName.Contains("chrome") || cleanName.Contains("chromium"))
                return "chrome";
            if (cleanName.Contains("firefox"))
                return "firefox";
            if (cleanName.Contains("devenv") || cleanName.Contains("visualstudio"))
                return "visualstudio";
            if (cleanName.Contains("code") || cleanName.Contains("vscode"))
                return "vscode";

            return cleanName;
        }
    }
}
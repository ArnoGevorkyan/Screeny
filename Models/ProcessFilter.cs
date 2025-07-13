using System.Collections.Generic;
using System.Diagnostics;

namespace ScreenTimeTracker.Models
{
    public static class ProcessFilter
    {
        public static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            // Essential Windows system processes only
            "dwm",
            "csrss", 
            "services",
            "svchost",
            "winlogon",
            "wininit",
            "lsass",
            "smss",
            "System",
            "Registry",
            
            // Windows security and defender
            "MsMpEng",
            "SecurityHealthService", 
            "smartscreen",
            
            // Windows updates
            "TiWorker",
            "UsoClient",
            
            // Audio system
            "audiodg",
            
            // Windows shell and system UI
            "explorer",
            "Microsoft Windows",
            "Windows Shell Experience",
            
            // Self-exclusion (dynamic)
            Process.GetCurrentProcess().ProcessName
        };

        /// <summary>
        /// Check if a process should be ignored (includes partial matching for system processes)
        /// </summary>
        public static bool ShouldIgnoreProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return true;
            
            // Check exact matches
            if (IgnoredProcesses.Contains(processName)) return true;
            
            // Check partial matches for system processes
            if (processName.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase) ||
                processName.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase) ||
                processName.Contains("Shell Experience", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            return false;
        }
    }
} 
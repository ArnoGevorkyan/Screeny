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
            
            // Self-exclusion (dynamic)
            Process.GetCurrentProcess().ProcessName
        };
    }
} 
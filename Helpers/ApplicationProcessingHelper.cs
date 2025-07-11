using ScreenTimeTracker.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace ScreenTimeTracker.Helpers
{
    /// <summary>
    /// Helper class for processing application records, normalizing names, and detecting application types
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
        /// Determines if two different process names might belong to the same application
        /// </summary>
        public static bool IsAlternateProcessNameForSameApp(string name1, string name2)
        {
            // Check if two different process names might belong to the same application
            if (string.IsNullOrEmpty(name1) || string.IsNullOrEmpty(name2))
                return false;

            // Convert to lowercase for case-insensitive comparison
            name1 = name1.ToLower();
            name2 = name2.ToLower();

            // Define groups of related process names
            var processGroups = new List<HashSet<string>>
            {
                new HashSet<string> { "telegram", "telegramdesktop", "telegram.exe", "updater" },
                new HashSet<string> { "discord", "discordptb", "discordcanary", "discord.exe", "update.exe" },
                new HashSet<string> { "chrome", "chrome.exe", "chromedriver", "chromium" },
                new HashSet<string> { "firefox", "firefox.exe", "firefoxdriver" },
                new HashSet<string> { "visualstudio", "devenv", "devenv.exe", "msvsmon", "vshost", "vs_installer" },
                new HashSet<string> { "code", "code.exe", "vscode", "vscode.exe", "code - insiders" },
                new HashSet<string> { "whatsapp", "whatsapp.exe", "whatsappdesktop", "electron" }
            };

            // Check if the two names are in the same group
            return processGroups.Any(group => group.Contains(name1) && group.Contains(name2));
        }
        
        /// <summary>
        /// Checks if the application is one that should be consolidated even with different window titles
        /// </summary>
        public static bool IsApplicationThatShouldConsolidate(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            
            // List of applications that should be consolidated even with different window titles
            string[] consolidateApps = new string[]
            {
                "chrome", "firefox", "msedge", "iexplore", "opera", "brave", "arc", // Browsers
                "winword", "excel", "powerpnt", "outlook", // Office
                "code", "vscode", "devenv", "visualstudio", // Code editors
                "minecraft", // Games
                "spotify", "discord", "slack", "telegram", "whatsapp", "teams", "skype", // Communication apps
                "explorer", "firefox", "chrome", "edge" // Common apps
            };

            // Extract base name for broader matching
            string baseName = GetBaseAppName(processName);

            return consolidateApps.Any(app =>
                processName.Contains(app, StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains(app, StringComparison.OrdinalIgnoreCase));
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
        
        /// <summary>
        /// Check if two window titles are similar or related
        /// </summary>
        public static bool IsSimilarWindowTitle(string title1, string title2)
        {
            // If either title is empty, they can't be similar
            if (string.IsNullOrEmpty(title1) || string.IsNullOrEmpty(title2))
                return false;

            // Check if one title contains the other
            if (title1.Contains(title2, StringComparison.OrdinalIgnoreCase) ||
                title2.Contains(title1, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check for very similar titles (over 80% similarity)
            if (GetSimilarity(title1, title2) > 0.8)
                return true;

            // Check if they follow the pattern "Document - Application"
            return IsRelatedWindow(title1, title2);
        }
        
        /// <summary>
        /// Calculate similarity between two strings based on shared words
        /// </summary>
        private static double GetSimilarity(string a, string b)
        {
            // A simple string similarity measure based on shared words
            var wordsA = a.ToLower().Split(new[] { ' ', '-', '_', ':', '|', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var wordsB = b.ToLower().Split(new[] { ' ', '-', '_', ':', '|', '.' }, StringSplitOptions.RemoveEmptyEntries);

            int sharedWords = wordsA.Intersect(wordsB).Count();
            int totalWords = Math.Max(wordsA.Length, wordsB.Length);

            return totalWords == 0 ? 0 : (double)sharedWords / totalWords;
        }
        
        /// <summary>
        /// Check if two window titles appear to be from the same application
        /// </summary>
        private static bool IsRelatedWindow(string title1, string title2)
        {
            // This helps consolidate things like "Document1 - Word" and "Document2 - Word"
            try
            {
                // Common title separators
                string[] separators = { " - ", " – ", " | ", ": " };
                
                // Try each separator pattern
                foreach (var separator in separators)
                {
                    if (title1.Contains(separator) && title2.Contains(separator))
                    {
                        var parts1 = title1.Split(new[] { separator }, StringSplitOptions.None);
                        var parts2 = title2.Split(new[] { separator }, StringSplitOptions.None);
                        
                        // If both have at least 2 parts and the last parts match (application name)
                        if (parts1.Length >= 2 && parts2.Length >= 2 && 
                            parts1[parts1.Length - 1].Equals(parts2[parts2.Length - 1], StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                
                return false;
            }
            catch
            {
                // If any error occurs during parsing, assume they're not related
                return false;
            }
        }
        
        /// <summary>
        /// Check if a process is a known browser
        /// </summary>
        private static bool IsBrowser(string processName)
        {
            string[] browsers = { "chrome", "firefox", "msedge", "iexplore", "opera", "brave", "arc" };
            return browsers.Any(b => processName.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                                    processName.Contains(b, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Detect browser type from process name and window title
        /// </summary>
        private static string DetectBrowserType(string processName, string windowTitle)
        {
            // Map processes to browser names
            if (processName.Contains("chrome", StringComparison.OrdinalIgnoreCase))
                return "Chrome";
            if (processName.Contains("firefox", StringComparison.OrdinalIgnoreCase))
                return "Firefox";
            if (processName.Contains("msedge", StringComparison.OrdinalIgnoreCase))
                return "Microsoft Edge";
            if (processName.Contains("iexplore", StringComparison.OrdinalIgnoreCase))
                return "Internet Explorer";
            if (processName.Contains("opera", StringComparison.OrdinalIgnoreCase))
                return "Opera";
            if (processName.Contains("brave", StringComparison.OrdinalIgnoreCase))
                return "Brave";
            if (processName.Contains("arc", StringComparison.OrdinalIgnoreCase))
                return "Arc";

            // Extract from window title if possible
            if (windowTitle.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
                return "Chrome";
            if (windowTitle.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
                return "Firefox";
            if (windowTitle.Contains("Edge", StringComparison.OrdinalIgnoreCase))
                return "Microsoft Edge";
            if (windowTitle.Contains("Internet Explorer", StringComparison.OrdinalIgnoreCase))
                return "Internet Explorer";
            if (windowTitle.Contains("Opera", StringComparison.OrdinalIgnoreCase))
                return "Opera";
            if (windowTitle.Contains("Brave", StringComparison.OrdinalIgnoreCase))
                return "Brave";
            if (windowTitle.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                return "Arc";

            // No specific browser detected
            return string.Empty;
        }
        
        /// <summary>
        /// Check if a window title likely belongs to a Java-based game
        /// </summary>
        private static bool IsJavaBasedGame(string windowTitle)
        {
            // List of common Java-based game keywords
            string[] gameKeywords = {
                "Minecraft", "MC", "Forge", "Fabric", "CraftBukkit", "Spigot", "Paper", "Optifine",
                "Game", "Server", "Client", "Launcher", "Mod", "MultiMC", "TLauncher", "Vime"
            };

            return gameKeywords.Any(keyword =>
                windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Extract game name from window title
        /// </summary>
        private static string ExtractGameNameFromTitle(string windowTitle)
        {
            // A map of known game title patterns to game names
            Dictionary<string, string> gameTitlePatterns = new Dictionary<string, string>
            {
                { "Minecraft", "Minecraft" },
                { "Forge", "Minecraft" },
                { "Fabric", "Minecraft" },
                { "Vime", "Minecraft" },
                { "MC", "Minecraft" }
            };

            foreach (var pattern in gameTitlePatterns.Keys)
            {
                if (windowTitle.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return gameTitlePatterns[pattern];
                }
            }

            return string.Empty;
        }
    }
} 
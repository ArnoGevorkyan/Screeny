using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ScreenTimeTracker.Helpers
{
    public static class DebugLogger
    {
        [Conditional("DEBUG")]
        public static void Log(string message, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
        {
            System.Diagnostics.Debug.WriteLine(message);
        }

        [Conditional("DEBUG")]
        public static void LogError(string message, Exception? ex = null, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
        {
            System.Diagnostics.Debug.WriteLine($"ERROR: {message}");
            if (ex != null)
            {
                System.Diagnostics.Debug.WriteLine($"Exception: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
            }
        }
    }
} 
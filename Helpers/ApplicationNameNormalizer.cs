namespace ScreenTimeTracker.Helpers
{
    public static class ApplicationNameNormalizer
    {
        public static string NormalizeProcessName(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return processName;

            return processName
                .Replace(" (x86)", "")
                .Replace(" (x64)", "")
                .Trim();
        }
    }
}
